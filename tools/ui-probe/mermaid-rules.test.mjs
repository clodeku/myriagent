// mermaidrules.js 純函式單元測試：流程圖、循序圖、狀態圖、類別圖、ER 圖與安全邊界
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseMermaid } from '../../src/Myriagent/Web/mermaidrules.js';

const ALLOWED_SHAPES = new Set(['rect', 'circle', 'ellipse', 'polygon', 'path', 'line', 'text']);

function assertPureGeometry(plan) {
  assert.ok(plan && plan.ok === true, 'parseMermaid 應回傳 ok: true');
  assert.ok(Number.isFinite(plan.width) && plan.width > 0);
  assert.ok(Number.isFinite(plan.height) && plan.height > 0);
  assert.ok(Array.isArray(plan.shapes) && plan.shapes.length > 0);
  for (const el of plan.shapes) {
    assert.ok(ALLOWED_SHAPES.has(el.tag), `不允許的 SVG 圖元：${el.tag}`);
    assert.ok(!('href' in el) && !('src' in el) && !('style' in el), '圖元不可帶網址或行內樣式');
  }
}

function shapeTexts(plan) {
  return plan.shapes
    .filter((e) => e.tag === 'text')
    .flatMap((e) => (e.lines || []).map((l) => (typeof l === 'string' ? l : l.text)));
}

test('flowchart TD 解析節點形狀、子圖與邊標籤並排出純幾何座標', () => {
  const src = `
flowchart TD
  subgraph Backend["後端核心"]
    A["接收請求"] --> B{"檢查額度"}
  end
  B -->|足夠| C(["執行代理"])
  B -.->|耗盡| D[("備援帳戶")]
`;
  const plan = parseMermaid(src);
  assertPureGeometry(plan);
  assert.equal(plan.kind, 'flowchart');
  assert.equal(plan.direction, 'TD');
  assert.equal(plan.nodes.length, 4);
  assert.equal(plan.edges.length, 3);
  assert.equal(plan.subgraphs.length, 1);

  const texts = shapeTexts(plan);
  assert.ok(texts.includes('接收請求'));
  assert.ok(texts.includes('檢查額度'));
  assert.ok(texts.includes('足夠'));
});

test('sequenceDiagram 解析參與者、訊息箭頭與備註框', () => {
  const src = `
sequenceDiagram
  participant U as 使用者
  participant H as AgentHub
  participant A as Antigravity
  U->>H: 送出訊息
  H->>A: stream-json
  Note right of A: 寫入 brain/plan.md
  A-->>H: step_update (DONE)
`;
  const plan = parseMermaid(src);
  assertPureGeometry(plan);
  assert.equal(plan.kind, 'sequenceDiagram');
  const texts = shapeTexts(plan);
  assert.ok(texts.includes('使用者'));
  assert.ok(texts.includes('AgentHub'));
  assert.ok(texts.includes('送出訊息'));
});

test('stateDiagram-v2 解析起始結束狀態與狀態轉移', () => {
  const src = `
stateDiagram-v2
  [*] --> Idle
  Idle --> Running : SendAsync
  Running --> Waiting : RequestFeedback
  Waiting --> Running : Proceed
  Running --> [*]
`;
  const plan = parseMermaid(src);
  assertPureGeometry(plan);
  assert.ok(plan.shapes.some((e) => e.tag === 'circle'), '應包含起始／結束圓點');
});

test('classDiagram 與 erDiagram 皆可離線排版成純幾何圖元', () => {
  const cls = parseMermaid(`
classDiagram
  class AgentHub {
    +SendAsync()
    +SwitchAccount()
  }
  class AntigravityAgentSession {
    +HasLiveBackgroundWork bool
  }
  AgentHub --> AntigravityAgentSession : manages
`);
  assertPureGeometry(cls);
  assert.equal(cls.kind, 'classDiagram');

  const er = parseMermaid(`
erDiagram
  CONVERSATION ||--o{ CHAT_ITEM : contains
  ACCOUNT ||--o{ CONVERSATION : owns
`);
  assertPureGeometry(er);
  assert.equal(er.kind, 'erDiagram');
});

test('不支援或空白語法回傳 ok:false（前端保留原始程式碼框）且惡意標籤只當純文字', () => {
  assert.equal(parseMermaid('').ok, false);
  assert.equal(parseMermaid('pie title 佔比\n  "A" : 40\n  "B" : 60').ok, false);

  const xss = parseMermaid('flowchart LR\n  A["<img src=x onerror=alert(1)>"] --> B["<svg onload=alert(2)>"]');
  assertPureGeometry(xss);
  const texts = shapeTexts(xss);
  assert.ok(texts.some((t) => t.includes('<img src=x onerror=alert(1)>')));

  // 無標題 subgraph 應產生決定性的固定 ID，不隨 Math.random 變動
  const untitled = 'flowchart TD\n  subgraph\n    X --> Y\n  end\n  subgraph\n    Y --> Z\n  end';
  const run1 = parseMermaid(untitled);
  const run2 = parseMermaid(untitled);
  assert.deepEqual(run1.subgraphs.map((s) => s.id), ['sg_1', 'sg_2']);
  assert.deepEqual(run1, run2);
});

// 儀表板 MCP 的純規則（web-protocol 的 mcp；mcp.js 用）：帳戶卡上那一行的摘要、明細的每個伺服器。
// 宿主的 mcp：{accounts: {帳戶 id: {checking, checkedAt, error, authorizing, safeMode, servers: [{name, status, source?, transport?, tools?, error?}] | null}}}

const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);

/** 各狀態的等級（同健康度的 ok／info／warn／bad）與文字 */
const STATUS = {
  connected: { level: 'ok', text: '已連線' },
  'needs-auth': { level: 'warn', text: '需要授權' },
  failed: { level: 'bad', text: '連不上' },
  pending: { level: 'info', text: '連線中…' },
  disabled: { level: 'info', text: '已停用' },
};

/** 設定從哪裡來（CLI 的 source） */
export const SOURCE_LABEL = {
  user: '使用者',
  plugin: '外掛',
  claudeai: 'claude.ai 連接器',
  project: '專案',
  local: '本機',
  dynamic: '共用設定',
  enterprise: '管理員',
  managed: '管理員',
};

/** 等級的徽章文字 */
export const LEVEL_LABEL = { ok: '正常', info: '', warn: '要授權', bad: '有問題' };

/** 這個帳戶的項目（沒有、不是物件回 null） */
export function accountMcpOf(mcp, accountId) {
  const accounts = isObj(mcp) && isObj(mcp.accounts) ? mcp.accounts : null;
  const entry = accounts && str(accountId) ? accounts[accountId] : null;
  return isObj(entry) ? entry : null;
}

function serversOf(entry) {
  return Array.isArray(entry.servers) ? entry.servers.filter((s) => isObj(s) && str(s.name)) : null;
}

/**
 * 帳戶卡上那一行：{level, text, busy}。精簡模式不載入 MCP；還沒查到過時是檢查中、查失敗或還沒檢查；
 * 查到了：已連線數／總數，另外寫要授權、連不上的數量（有連不上的是 bad、有要授權的是 warn）
 */
export function mcpStrip(entry) {
  if (!isObj(entry)) return null;
  const busy = entry.checking === true || !!str(entry.authorizing);
  if (entry.safeMode === true) return { level: 'info', text: '精簡模式不載入 MCP', busy: false };
  const servers = serversOf(entry);
  if (!servers) {
    if (entry.checking === true) return { level: 'info', text: '檢查中…', busy: true };
    if (str(entry.error)) return { level: 'bad', text: '檢查失敗：' + entry.error, busy };
    return { level: 'info', text: '還沒檢查', busy };
  }
  if (!servers.length) return { level: 'ok', text: '沒有設定 MCP 伺服器', busy };
  const count = (status) => servers.filter((s) => s.status === status).length;
  const auth = count('needs-auth');
  const failed = count('failed');
  const parts = [count('connected') + '/' + servers.length + ' 個已連線'];
  if (auth) parts.push(auth + ' 個要授權');
  if (failed) parts.push(failed + ' 個連不上');
  return { level: failed ? 'bad' : auth ? 'warn' : 'ok', text: parts.join('・'), busy };
}

/**
 * 明細的每個伺服器：{name, status, level, text, source, detail: [字串], actions: [{action, label, title, disabled}]}。
 * 需要授權的可以授權、連不上與已連線的可以重新連線（伺服器恢復、授權換過之後讓開著的對話接回來）；連線中、停用的不能操作。
 * 正在檢查或授權時按鈕都停用，正在授權的那一個寫「授權中…」
 */
export function mcpRows(entry) {
  if (!isObj(entry)) return [];
  const servers = serversOf(entry) || [];
  const busy = entry.checking === true || !!str(entry.authorizing);
  return servers.map((s) => {
    const st = STATUS[s.status] || { level: 'info', text: str(s.status) || '狀態不明' };
    const source = str(s.source) ? SOURCE_LABEL[s.source] || s.source : null;
    const tools = num(s.tools);
    const detail = [];
    if (str(s.error)) detail.push(s.error);
    if (s.status === 'connected' && tools !== null) detail.push(tools + ' 個工具');
    const actions = [];
    if (s.status === 'needs-auth') {
      const authorizing = entry.authorizing === s.name;
      actions.push({ action: 'authenticate', label: authorizing ? '授權中…' : '授權…', title: '在瀏覽器開啟授權頁，完成後狀態會自動更新', disabled: busy });
    } else if (s.status === 'failed' || s.status === 'connected') {
      actions.push({ action: 'reconnect', label: '重新連線', title: '重新連線並檢查；連得上的話，這個帳戶開著的對話也一起重新連線', disabled: busy });
    }
    return { name: s.name, status: str(s.status) || '', level: st.level, text: st.text, source, detail, actions };
  });
}

/** 帳戶卡 MCP 那一行的燈（每個伺服器一顆，照宿主的順序）：{ name, status（connected／needs-auth／failed／pending／disabled，其他當 unknown）, text } */
export function mcpLeds(entry) {
  if (!isObj(entry)) return [];
  return (serversOf(entry) || []).map((s) => {
    const status = Object.prototype.hasOwnProperty.call(STATUS, s.status) ? s.status : 'unknown';
    return { name: String(s.name || ''), status, text: STATUS[status] ? STATUS[status].text : '狀態不明' };
  });
}

/** 這次從別的狀態變成已連線的伺服器名稱（prev 是上一次 名稱→狀態 的 Map；第一次〔prev 是 null〕不算） */
export function mcpNewlyConnected(prev, leds) {
  const out = new Set();
  if (!(prev instanceof Map) || !Array.isArray(leds)) return out;
  for (const l of leds) if (l.status === 'connected' && prev.has(l.name) && prev.get(l.name) !== 'connected') out.add(l.name);
  return out;
}

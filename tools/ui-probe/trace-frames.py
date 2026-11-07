"""依觸發來源分類每一格主執行緒畫面（2026-11-08）。

讀 perf-probe.mjs 以 MYRIAGENT_PERF_RAWTRACE=<檔案> 錄的 2 秒追蹤（含失效追蹤與堆疊），把渲染程序主執行緒的每一次
WebFrameWidgetImpl::UpdateLifecycle（一格畫面）依「上一格之後有哪些網頁程式改了 DOM」（失效追蹤的堆疊最上層的網頁檔名）分類，
列出每一類幾格、每格平均幾毫秒，以及其中各階段（樣式、版面、預繪、繪製、圖層化、合成輸入、IntersectionObserver）的平均毫秒數。
「(nothing)」是沒有網頁程式改 DOM 的格（捲動事件、動畫開始結束要的影格）。追蹤開了很多分類，各階段的毫秒數會被灌水，只看相對大小與格數。

用法：python -X utf8 trace-frames.py <追蹤.json>
"""
import bisect
import collections
import json
import sys

ev = json.load(open(sys.argv[1], encoding='utf-8'))['traceEvents']
names = {(e['pid'], e['tid']): e['args']['name'] for e in ev if e.get('ph') == 'M' and e.get('name') == 'thread_name'}
main = [k for k, v in names.items() if v == 'CrRendererMain'][0]
evs = sorted([e for e in ev if (e['pid'], e['tid']) == main and e.get('ts')], key=lambda e: e['ts'])


def top(stack):
    """堆疊最上層的網頁檔名（略過瀏覽器內部與 Playwright 注入的）"""
    for s in (stack or []):
        u = s.get('url', '')
        if u and not u.startswith('chrome') and 'playwright' not in u:
            return u.split('/')[-1]
    return None


INV = {'ScheduleStyleInvalidationTracking', 'StyleRecalcInvalidationTracking', 'LayoutInvalidationTracking',
       'StyleInvalidatorInvalidationTracking', 'PaintInvalidationTracking'}
PHASES = ['UpdateLayoutTree', 'Layout', 'PrePaint', 'Paint', 'Layerize', 'Blink.CompositingInputs.UpdateTime',
          'AnimationTimeline::serviceAnimations', 'IntersectionObserverController::computeIntersections', 'Commit']
lifes = [e for e in evs if e['name'] == 'WebFrameWidgetImpl::UpdateLifecycle' and e.get('ph') == 'X']
starts = [e['ts'] for e in lifes]
phase_ms = collections.defaultdict(collections.Counter)
for x in evs:
    if x.get('ph') != 'X' or x['name'] not in PHASES:
        continue
    i = bisect.bisect_right(starts, x['ts']) - 1
    if i >= 0 and x['ts'] + x['dur'] <= lifes[i]['ts'] + lifes[i]['dur'] + 1:
        phase_ms[lifes[i]['ts']][x['name']] += x['dur'] / 1000

pending = set()
count = collections.Counter()
cost = collections.Counter()
phases = collections.defaultdict(collections.Counter)
for e in evs:
    if e['name'] in INV:
        src = top(e['args'].get('data', {}).get('stackTrace'))
        if src:
            pending.add(src)
    elif e['name'] == 'WebFrameWidgetImpl::UpdateLifecycle' and e.get('ph') == 'X':
        key = '+'.join(sorted(pending)) or '(nothing)'
        count[key] += 1
        cost[key] += e['dur'] / 1000
        phases[key].update(phase_ms[e['ts']])
        pending = set()

span = (lifes[-1]['ts'] - lifes[0]['ts']) / 1e6
print(f'畫面 {len(lifes)} 格、{span:.2f} 秒（每秒 {len(lifes) / span:.1f} 格）、合計 {sum(cost.values()):.0f} ms')
for k, v in sorted(count.items(), key=lambda x: -x[1])[:12]:
    print(f'{v:4d} 格  每格 {cost[k] / v:5.2f} ms  {k}')
    print('        ' + '  '.join(f'{n.split(".")[-1].split("::")[-1][:12]}={phases[k][n] / v:.2f}' for n in PHASES))

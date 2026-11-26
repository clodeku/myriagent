"""渲染程序主執行緒的執行緒時間。

讀 perf-probe.mjs 以 MYRIAGENT_PERF_RAWTRACE=<檔案> 錄的 2 秒追蹤，列出主執行緒每秒的工作合計、畫面格數與各階段（樣式、版面、預繪、繪製、
圖層化、IntersectionObserver、送出〔Commit〕）每秒的毫秒數。用的是追蹤事件的 tdur（執行緒真的用掉的 CPU 時間）：
機器被其他程式佔滿時牆上時間會被拉長，tdur 比較不受影響（但仍會被快取、頻率拖慢，新舊要交錯跑、只比同一回合）。

用法：python -X utf8 trace-threadtime.py <追蹤.json>...
"""
import collections
import json
import sys

PHASES = ('WebFrameWidgetImpl::UpdateLifecycle', 'UpdateLayoutTree', 'Layout', 'PrePaint', 'Paint', 'Layerize', 'Commit',
          'IntersectionObserverController::computeIntersections', 'FunctionCall')
for f in sys.argv[1:]:
    ev = json.load(open(f, encoding='utf-8'))['traceEvents']
    names = {(e['pid'], e['tid']): e['args']['name'] for e in ev if e.get('ph') == 'M' and e.get('name') == 'thread_name'}
    main = [k for k, v in names.items() if v == 'CrRendererMain'][0]
    evs = [e for e in ev if (e['pid'], e['tid']) == main and e.get('ph') == 'X' and 'tdur' in e]
    span = (max(e['ts'] + e['dur'] for e in evs) - min(e['ts'] for e in evs)) / 1e6
    # 只算最外層的工作（RunTask 可能巢狀）
    tasks = sorted((e for e in evs if e['name'] in ('ThreadControllerImpl::RunTask', 'RunTask')), key=lambda e: e['ts'])
    top, end = [], -1
    for e in tasks:
        if e['ts'] >= end:
            top.append(e)
            end = e['ts'] + e['dur']
    total = sum(e['tdur'] for e in top) / 1000
    ph = collections.Counter()
    for e in evs:
        if e['name'] in PHASES:
            ph[e['name']] += e['tdur'] / 1000
    frames = sum(1 for e in evs if e['name'] == 'WebFrameWidgetImpl::UpdateLifecycle')
    print(f"{f.replace(chr(92), '/').split('/')[-1]:16s} 主執行緒 {total / span:6.1f} ms/s  畫面 {frames / span:5.1f}/s  "
          + '  '.join(f"{k.split('::')[-1][:14]}={ph[k] / span:5.1f}" for k in PHASES))

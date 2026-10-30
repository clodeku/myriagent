"""load-sampler.ps1 的輸出摘要（WP4.9：measure_sync 的數字旁邊記的負載）。

每一行是「<ISO 時間> cpu=<整機 CPU 百分比> <程序名>=<數量> ...」；摘要是取樣數、起訖時間（HH:MM）、CPU 的平均／最小／最大、超過 50% 的次數、
各程序數的範圍（沒出現的程序是 0～0）。看不懂的行略過。

用法：python -X utf8 tools/fake-device/load_stats.py <load.txt> [...]   → 每個檔一個 JSON 物件
"""

import json
import re
import statistics
import sys
from pathlib import Path

LINE = re.compile(r"(\S+) cpu=(\d+(?:\.\d+)?)(.*)")
COUNT = re.compile(r"(\w+)=(\d+)")
PROCESSES = ("dotnet", "testhost", "MyriagentHub", "node", "chrome", "Myriagent", "FakeDevice")
BUSY_PERCENT = 50


def summarize(text):
    """load-sampler.ps1 的一個輸出檔的內容 → 摘要（沒有可用的行時 samples 是 0、其他是 None）"""
    cpus, times, counts = [], [], []
    for raw in text.splitlines():
        m = LINE.match(raw.strip())
        if not m:
            continue
        times.append(m.group(1)[11:16])
        cpus.append(float(m.group(2)))
        counts.append({k: int(v) for k, v in COUNT.findall(m.group(3))})
    result = {
        "samples": len(cpus),
        "from": times[0] if times else None,
        "to": times[-1] if times else None,
        "cpuMean": round(statistics.mean(cpus), 1) if cpus else None,
        "cpuMin": min(cpus) if cpus else None,
        "cpuMax": max(cpus) if cpus else None,
        "cpuOverBusy": sum(1 for c in cpus if c > BUSY_PERCENT),
    }
    for name in PROCESSES:
        values = [c.get(name, 0) for c in counts]
        result[name] = [min(values), max(values)] if values else None
    return result


def main():
    out = {path: summarize(Path(path).read_text(encoding="utf-8-sig")) for path in sys.argv[1:]}
    print(json.dumps(out, ensure_ascii=False, indent=1))


if __name__ == "__main__":
    main()

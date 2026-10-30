"""同步的實機量測（WP3.3 第 16 步：S5 暖啟動、S6 未配對與已配對閒置）。

真的 Myriagent.exe（WebView2）以實機探針（MYRIAGENT_AGENT_PROBE）執行，一律加
MYRIAGENT_AGENT_PROBE_SEND=0（不送訊息）與 MYRIAGENT_AGENT_PROBE_NO_CLI=1（不找、不執行任何 CLI；帳戶一律未安裝、不查用量），
CLAUDE_CONFIG_DIR、CODEX_HOME 指到工作資料夾裡的空資料夾：不碰這台電腦的帳戶、不耗額度、不連 CLI 的端點。

資料根由 tools/fake-device 準備（各一個隔離的假 Claude 帳戶；fake-device 把 Antigravity、Grok 標成已加過，app 不建這兩個系統預設帳戶）：
- unpaired：沒有配對（--base-app 時另一個給基準建置用的 base-unpaired）。
- paired：對這個腳本自己啟動的本機 Testing MyriagentHub（暫存資料根、自動挑的高埠）配對（擁有者自動核准）。

量測：
- S5：每一輪以固定種子（--seed，記進 summary）打亂各組的順序，各跑 --runs 次，比 report.json 的 uiReadyMs（啟動到介面就緒）。
  HEAD 的組（WP4.9 步 2：設計 11.4 的三種狀態都量，另加回補中與吞封包）：
  head-unpaired（未配對）、head-paired-online（已配對、連本機 MyriagentHub，MYRIAGENT_SYNC_DEV_TRUST=1）、
  head-paired-offline（MYRIAGENT_SYNC_SERVER=https://127.0.0.1:9，連線被拒）、
  head-paired-blackhole（MYRIAGENT_SYNC_SERVER 指到這個腳本自己開的 TCP 監聽：接受連線、從不讀寫，結束時關掉），
  有 --conversations 時再加 head-paired-backfilling（每一輪一個新的資料根：先 run 灌 N 個舊對話、再 pair --no-drain、立刻跑 app，
  app 啟動時回補還沒做完；同一台伺服器上的不同裝置要用不同的種子，否則撞 not_owner，所以種子是 --seed＋1000＋第幾輪，形狀相同）。
  --base-app 時再加 base-unpaired（基準建置，--base-label 記它是哪一個：配對前的 7c9bcd3＋探針修補，或第 4 階段之前的 1724718）。
  各組回報中位數與 p90。各組的閒置記憶體（兩次取樣與每一回合的較大值）另列 s5IdleMemoryMb、減 head-unpaired 的差值列 s5IdleMinusUnpairedMb
  （WP4.9 步 6b：連不上的兩組不在 S6 的判定裡，只供參考）。
  判定：有基準建置時，每一個 head 組的中位數都不超過 base-unpaired 的中位數＋50 ms；沒有時和 README 記的基準上限（930 ms）＋50 ms 比。
  前提：head-paired-online 每一回合 state=online；head-paired-blackhole 每一回合 paired=true、state 為 offline／connecting、載入了 SignalR 與 Sqlite；
  head-paired-backfilling 每一回合 pair 的輸出 backfill 不是 null（真的還沒回補完）。已配對對未配對的差值在構造上必然接近 0
  （同步在介面就緒之後才延後啟動），只證明延後啟動有效，不拿來判定。
- S6 未配對：head-unpaired 每一次的 sync.loadedForbidden 都是空的。
- S6 已配對閒置：paired 連本機 MyriagentHub（MYRIAGENT_SYNC_DEV_TRUST=1）--idle-runs 次。探針（WP4.9 步 1）等連上後先記一次進 Live 當下的記憶體
  （memoryAtLiveMb，舊量法的取樣點，含啟動重整的暫態），再等同步安定（重整做完、佇列空、outbox 全部確認、沒有在途批次、回補進度是 null，
  連續 1 秒；最多 10 分鐘，花的秒數是 settleSeconds、逾時記 settleTimedOut），之後不論是否配對都再等 25 秒才取閒置記憶體
  （sync.appPrivateMb＝memoryIdleMb，含 gc 細項：heapMb、committedMb、fragmentedMb、nativeEstimateMb），最後連取三個 10 秒窗的 app 程序 CPU
  （idleCpuWindows，以整台電腦的處理器數正規化；30 秒涵蓋至少兩次心跳與 SignalR keepalive），記下取樣期間的心跳數
  （sync.heartbeatsDuringSample）；CPU 窗之後（再等 --idle-extra-seconds 秒）取第二次閒置記憶體（memoryIdleLateMb，WP4.9 合併前審查 F1：
  第一次落在閒置釋放 DLP 樣式的積極回收之後約 5 秒，是已配對那一邊的低點；之後心跳與 keepalive 的配置會讓 gen0 的已提交空間長回來），
  兩次之間的配置速率（idleLate.allocMbPerHour）與 gen0 預算（idleLate.gen0MaxBudgetMb）一併記下。
  判定：CPU ≤0.05%；閒置記憶體每一回合取兩次的較大值，已配對的中位數比 unpaired 多不超過 30 MB（s6JudgedDeltaMb；剛好 30.0 算過）。
  第一次、第二次各自的差值另列 s6PrivateDeltaMb、s6LateDeltaMb，進 Live 當下的差值另列 s6AtLiveDeltaMb（只供和舊量法對照）。
  --idle-extra-seconds 只給 head-unpaired 與 S6 已配對的回合（S6 記憶體判定用到的兩群）；summary 記 idleExtraSeconds。
  S6 的回合排在全部 S5 之後：伺服器上已有 head-paired-backfilling 每一輪那台裝置的對話（root-paired 連上時拉成副本），
  summary.s6ReplicaConversations＝--conversations×輪數（S6 回合時伺服器上別台裝置的對話數），S5 每一回合另記 serverOtherConversations。
  這幾回合的 uiReadyMs 另列在 s5Stats 的 s6-paired-online（只供參考，不判定）。

前提（任一不成立，對應的判定是 None、原因列在 result.invalid）：每一回合 ok；head-unpaired 是 paired=false 且沒載入同步的組件；
head-paired-offline 與 head-paired-blackhole 是 paired=true、state 為 offline／connecting，而且載入了 SignalR 與 Sqlite（陽性對照：真的走到同步路徑）；
head-paired-online 是 paired=true、state=online；head-paired-backfilling 的 pair 輸出 backfill 不是 null；
S6 每一回合 paired=true、state=online、沒有 notOnline、取樣期間至少一次心跳；S6 每一回合 settleTimedOut 是 false（否則記憶體判定無效）；
S6 已配對與 head-unpaired 每一回合都有 memoryIdleLateMb（沒有＝探針太舊，記憶體判定無效）。

--app-env KEY=VALUE（可重複，WP4.9 步 1）：給受測的 app（HEAD 與基準建置）加環境變數，只收 DOTNET_GC 開頭的鍵（不分大小寫；GC 的實驗用），
其他的鍵結束碼 2。子程序本來就不繼承這個 session 的 DOTNET_GC* 與 COMPlus_*（COMPlus_ 前綴在 .NET 10 照樣生效；只有 --app-env 給的才有），
summary.appEnv 記下給了什麼。

工作資料夾：不存在或是空的才直接用；非空時只有含這個工具寫的標記檔（.measure_sync）才清掉重來；拒絕 repo、repo 的上層、家目錄與磁碟根目錄。
summary.build 記下 git HEAD、工作樹是否乾淨，與受測的 exe／dll 的 SHA-256、修改時間（dll 比 HEAD 的提交時間舊就是 stale）。

結束時不論成敗：paired 有 device.json 就以 fake-device unpair 刪掉裝置金鑰；只剩 pending-key.json 就跑一次 fake-device status
（啟動時的配對清理會刪）；以 admin stop 正常停止 MyriagentHub；掃金鑰存放區，新增的 Myriagent.Device.* 金鑰寫進
summary.orphanDeviceKeys。結果寫到 <work>/summary.json 並印出；任何判定不是 True、或有孤兒金鑰時結束碼 1。

--conversations N（WP4.3 步 13）：三個資料根（unpaired、base-unpaired、paired）在啟動之前都以 fake-device 的 seedConversations 灌同一份
N 個舊對話（種子＝--seed、baseTimeUtc 固定 2026-10-20T00:00:00Z、每個 --items-per-conversation 則、最新的早 --conversation-days 天；
帳戶別名 measure-seed，三個資料根都加這個帳戶）；summary.conversations 記每個資料根的筆數與 SHA-256，三份不同就判定無效。
這時一定要有 --base-app（同一份資料跑基準建置，不拿 README 的 930 ms 比；沒有就結束碼 2）。S6 的回合之前先以
fake-device status --wait-backfill 等回補做完（進度是 null、outbox 清空），花的秒數記在 summary.conversations.backfillSeconds；
伺服器沒有 conv1（重新為基底之前）時回補不會開始，量到的就不是「已回補」的情境（README 有說明）。

用法（先建置 slnx 與 tools/fake-device 的 Release）：
  python -X utf8 tools/fake-device/measure_sync.py --work <暫存資料夾> [--runs 5] [--idle-runs 3] [--seed 20261016] [--base-app <基準建置的 bin 或 exe>]
      [--base-label 7c9bcd3+probe] [--conversations 300 [--items-per-conversation 40] [--conversation-days 3]] [--app-env DOTNET_GCgen0size=0x400000 ...]
      [--idle-extra-seconds 1800]
"""

import argparse
import hashlib
import json
import math
import os
import random
import shutil
import socket
import ssl
import statistics
import subprocess
import sys
import threading
import time
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
APP_BIN = REPO / "src" / "Myriagent" / "bin" / "Release" / "net10.0-windows" / "win-x64"
HUB_BIN = REPO / "src" / "MyriagentHub" / "bin" / "Release" / "net10.0" / "win-x64"
FAKE_DEVICE = REPO / "tools" / "fake-device" / "bin" / "Release" / "net10.0-windows" / "win-x64" / "FakeDevice.exe"
OWNER_PASSWORD = "changeme2026"  # Testing MyriagentHub 的預設擁有者密碼（HubAuthNames.DefaultPassword）

WORK_MARKER = ".measure_sync"
NO_CLI_VARIABLE = "MYRIAGENT_AGENT_PROBE_NO_CLI"
DEVICE_KEY_PREFIX = "Myriagent.Device."
SYNC_ASSEMBLIES = ("Microsoft.AspNetCore.SignalR.Client", "Microsoft.Data.Sqlite")
README_BASELINE_MAX_MS = 930  # README 2026-10-10 重測的暖啟動 0.87～0.93 秒的上限（沒有 --base-app 時的基準）
S5_MARGIN_MS = 50
S6_CPU_MAX_PERCENT = 0.05
S6_MEMORY_MAX_DELTA_MB = 30
SEED_BASE_TIME_UTC = "2026-10-20T00:00:00Z"  # seedConversations 的基準時間（固定：重跑才產生同一份）
SEED_ACCOUNT = "measure-seed"
# head-paired-backfilling 每一輪的種子：--seed＋這個值＋第幾輪（同一台伺服器上的不同裝置要用不同的種子，否則撞 not_owner）
BACKFILL_SEED_OFFSET = 1000
UNREACHABLE_SERVER = "https://127.0.0.1:9"  # head-paired-offline：連線被拒
# 每一組的設定之外，判定看的 HEAD 組（base-unpaired 是基準）
HEAD_GROUPS = ("head-unpaired", "head-paired-online", "head-paired-offline", "head-paired-blackhole", "head-paired-backfilling")
OFFLINE_STATES = ("offline", "connecting")
APP_ENV_PREFIX = "DOTNET_GC"  # --app-env 只收這個前綴的鍵（不分大小寫）
# 一回合 app 的時限：探針平常約 1 分鐘；已配對而且連上時多等同步安定（最多 10 分鐘，ProbeRules.SettleTimeout）與 25 秒（IdleSettleDelay）；
# --idle-extra-seconds 的回合再加那麼多秒
APP_TIMEOUT_SECONDS = 240 + 600 + 25
IDLE_EXTRA_VARIABLE = "MYRIAGENT_AGENT_PROBE_IDLE_EXTRA_SECONDS"  # 探針取第二次閒置記憶體之前多等幾秒（ProbeRules.IdleExtraVariable）
IDLE_EXTRA_MAX_SECONDS = 86400  # 同 ProbeRules.IdleExtraMax

# 子程序不繼承的環境變數（本 session 的 Claude／agy 工作階段、各代理的設定與金鑰、同步覆寫、ASP.NET Core 環境、GC 設定：GC 只經 --app-env 給；
# COMPlus_ 是舊的前綴，.NET 10 照樣採用，例如 COMPlus_GCgen0MaxBudget、COMPlus_gcServer，整個前綴都剝）。
# 和 tools/hub-e2e/lib/child-env.mjs、tools/hub-probe/ProbeClient/ChildEnvironment.cs 的清單相同（test_measure_sync 比對）
STRIP_PREFIXES = ("CLAUDE", "ANTHROPIC_", "OPENAI_", "ANTIGRAVITY_", "GROK_", "XAI_", "MYRIAGENT_", "CODEX_", "ASPNETCORE_", "DOTNET_ENVIRONMENT", APP_ENV_PREFIX,
                  "COMPLUS_")


def log(text):
    print(text, file=sys.stderr, flush=True)


def fail(text, code=2):
    log(text)
    sys.exit(code)


def clean_env(**extra):
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(STRIP_PREFIXES)}
    env.update(extra)
    return env


def parse_app_env(values):
    """--app-env 的 KEY=VALUE 清單 → dict（依給的順序）；鍵不是 DOTNET_GC 開頭（不分大小寫）、沒有等號、鍵或值是空的就丟 ValueError"""
    env = {}
    for item in values or []:
        key, sep, value = item.partition("=")
        key = key.strip()
        if not sep or not key or not value:
            raise ValueError(f"--app-env 要寫成 KEY=VALUE：{item!r}")
        if not key.upper().startswith(APP_ENV_PREFIX):
            raise ValueError(f"--app-env 只收 {APP_ENV_PREFIX} 開頭的鍵（GC 的實驗用）：{key}")
        env[key] = value
    return env


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Blackhole:
    """會吞封包的伺服器（head-paired-blackhole）：只聽 127.0.0.1 的自動挑的埠，接受連線、從不讀也不寫（TLS 交握永遠等不到回應），close() 時全部關掉"""

    def __init__(self):
        self._listener = socket.socket()
        self._listener.bind(("127.0.0.1", 0))
        self._listener.listen(64)
        # 每 0.5 秒醒來看一次要不要結束（Windows 上從別的執行緒關掉監聽不一定喚得醒擋住的 accept）
        self._listener.settimeout(0.5)
        self.port = self._listener.getsockname()[1]
        self._held = []
        self._lock = threading.Lock()
        self._closed = False
        self.accepted = 0
        self._thread = threading.Thread(target=self._accept, name="blackhole", daemon=True)
        self._thread.start()

    def _accept(self):
        while True:
            try:
                conn, _ = self._listener.accept()
            except socket.timeout:
                with self._lock:
                    if self._closed:
                        return
                continue
            except OSError:
                return  # close() 關掉了監聽
            with self._lock:
                if self._closed:
                    conn.close()
                    return
                self._held.append(conn)  # 留著不讀不寫，直到 close()
                self.accepted += 1

    def close(self):
        with self._lock:
            self._closed = True
        self._thread.join(timeout=5)
        self._listener.close()
        with self._lock:
            held, self._held = self._held, []
        for conn in held:
            try:
                conn.close()
            except OSError:
                pass


def iso(timestamp):
    return datetime.fromtimestamp(timestamp, timezone.utc).isoformat(timespec="seconds")


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


# ───────────────────────────── 工作資料夾與建置指紋 ─────────────────────────────

def prepare_work(work):
    """清掉上一次的工作資料夾（只清這個工具建的），寫標記檔"""
    home = Path.home().resolve()
    root = Path(work.anchor)
    if work == REPO or work in REPO.parents or work == home or work in home.parents or work == root:
        fail(f"--work 不能是 repo、repo 的上層、家目錄或磁碟根目錄：{work}")
    if work.exists():
        if not work.is_dir():
            fail(f"--work 不是資料夾：{work}")
        if any(work.iterdir()):
            if not (work / WORK_MARKER).is_file():
                fail(f"--work 不是空的、也不是這個工具建的（沒有 {WORK_MARKER}），不刪：{work}")
            shutil.rmtree(work)
    work.mkdir(parents=True, exist_ok=True)
    (work / WORK_MARKER).write_text("measure_sync.py 的工作資料夾；下次執行會整個刪掉重建\n", encoding="utf-8")


def git(*args):
    r = subprocess.run(["git", "-C", str(REPO), *args], capture_output=True, text=True, encoding="utf-8")
    return r.stdout.strip() if r.returncode == 0 else None


def build_info(base_app):
    head = git("rev-parse", "HEAD")
    porcelain = git("status", "--porcelain") or ""
    commit_time = git("log", "-1", "--format=%ct")
    targets = {
        "app/Myriagent.exe": APP_BIN / "Myriagent.exe",
        "app/Myriagent.dll": APP_BIN / "Myriagent.dll",
        "app/Myriagent.Protocol.dll": APP_BIN / "Myriagent.Protocol.dll",
        "hub/MyriagentHub.exe": HUB_BIN / "MyriagentHub.exe",
        "hub/MyriagentHub.dll": HUB_BIN / "MyriagentHub.dll",
        "fake-device/FakeDevice.exe": FAKE_DEVICE,
        "fake-device/FakeDevice.dll": FAKE_DEVICE.with_name("FakeDevice.dll"),
    }
    if base_app is not None:
        targets["base/Myriagent.dll"] = base_app / "Myriagent.dll"
    files = {}
    stale = []
    for name, path in targets.items():
        if not path.is_file():
            files[name] = None
            stale.append(f"{name} 不存在")
            continue
        mtime = path.stat().st_mtime
        files[name] = {"path": str(path), "sha256": sha256(path), "mtime": iso(mtime)}
        # 基準建置本來就比 HEAD 舊
        if not name.startswith("base/") and name.endswith(".dll") and commit_time and mtime < int(commit_time):
            stale.append(f"{name} 比 HEAD 的提交時間舊（忘了重建？）")
    info = {
        "head": head,
        "commitTime": iso(int(commit_time)) if commit_time else None,
        "dirty": bool(porcelain),
        "dirtyFiles": porcelain.splitlines()[:50],
        "stale": bool(stale),
        "staleReasons": stale,
        "files": files,
    }
    if info["dirty"]:
        log(f"警告：工作樹不乾淨（{len(porcelain.splitlines())} 個檔），量到的數字對不到單一提交")
    for reason in stale:
        log(f"警告：{reason}")
    return info


def has_no_cli_switch(app_dir):
    """靜態檢查建置認得 MYRIAGENT_AGENT_PROBE_NO_CLI（.NET 字串常值是 UTF-16LE）：認不得的建置會找到並執行這台電腦真正的 CLI"""
    dll = app_dir / "Myriagent.dll"
    return dll.is_file() and NO_CLI_VARIABLE.encode("utf-16-le") in dll.read_bytes()


# ───────────────────────────── 金鑰存放區 ─────────────────────────────

def key_store():
    return Path(os.environ["APPDATA"]) / "Microsoft" / "Crypto" / "Keys"


def key_snapshot():
    store = key_store()
    return {p.name for p in store.iterdir()} if store.is_dir() else set()


def new_device_keys(before, creators):
    """開始之後新增、內容含裝置金鑰名稱的金鑰檔：(這次的工具建的, 別的程式建的)"""
    store = key_store()
    prefix = DEVICE_KEY_PREFIX.encode("utf-16-le")
    needles = [str(c).lower().encode("utf-16-le") for c in creators]
    ours, others = [], []
    for name in sorted(key_snapshot() - before):
        try:
            content = (store / name).read_bytes()
        except OSError:
            continue
        if prefix not in content:
            continue
        lowered = content.lower()
        (ours if any(n in lowered for n in needles) else others).append(name)
    return ours, others


# ───────────────────────────── MyriagentHub、fake-device、app ─────────────────────────────

def hub_args(data, port):
    values = {
        "Hub:ServerDomain": "localhost",
        "Hub:HttpsPort": str(port),
        "Hub:BindAddress": "127.0.0.1",
        "Hub:BelowNormalPriority": "false",
        "Hub:SelfTest:Enabled": "false",
        "Hub:Acme:Enabled": "false",
        "Hub:Backup:Enabled": "false",
        "Hub:FileLog:Enabled": "false",
        "Hub:AdminInbox:PollSeconds": "1",
        "Hub:RateLimits:Enabled": "false",
        "Hub:Tls:CertStore": "file",
        "Hub:MemoryFuse:SampleSeconds": "3600",
        "Hub:PublicOrigin": "https://localhost",
    }
    return ["--data", str(data), "--environment", "Testing"] + [f"--{k}={v}" for k, v in values.items()]


def wait_healthz(port, timeout=60):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"https://127.0.0.1:{port}/healthz", context=ctx, timeout=2) as r:
                if r.status == 200:
                    return
        except Exception:
            pass
        time.sleep(0.2)
    raise RuntimeError("MyriagentHub 沒有就緒")


def fake_device(root, *args, env=None, timeout=180):
    cmd = [str(FAKE_DEVICE), "--data", str(root), *args]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", env=env or clean_env(), timeout=timeout)
    if r.returncode != 0:
        raise RuntimeError(f"fake-device {' '.join(args)} 結束碼 {r.returncode}：{r.stderr[-2000:]}")
    return json.loads(r.stdout.strip().splitlines()[-1])


def write_script(path, alias, seed=None):
    """seed：seedConversations 的參數（劇本最前面，中樞啟動之前寫進資料根）與它的帳戶；None＝不灌對話"""
    steps = [{"op": "addAccount", "alias": alias, "provider": "Claude", "label": "量測用假帳戶", "email": f"{alias}@example.test", "orgId": "org-measure", "plan": "max"}]
    if seed is not None:
        steps.insert(0, {"op": "seedConversations", **seed})
        steps.append({"op": "addAccount", "alias": seed["account"], "provider": "Claude", "label": "舊對話的帳戶", "email": f"{seed['account']}@example.test",
                      "orgId": "org-measure", "plan": "max"})
    path.write_text(json.dumps({"steps": steps}, ensure_ascii=False), encoding="utf-8")


def seeded_of(run_result):
    """fake-device run 的結果裡 seedConversations 那一步（筆數、SHA-256）"""
    for step in run_result.get("steps") or []:
        if step.get("op") == "seedConversations":
            return {k: step.get(k) for k in ("conversations", "items", "sha256", "firstId")}
    return None


def run_app(app_exe, root, out_dir, work, extra_env, timeout=APP_TIMEOUT_SECONDS):
    """跑一次實機探針，回傳 report.json（extra_env 含這一組的設定與 --app-env）"""
    if out_dir.exists():
        shutil.rmtree(out_dir)
    out_dir.mkdir(parents=True)
    empty = work / "empty-config"
    (empty / "claude").mkdir(parents=True, exist_ok=True)
    (empty / "codex").mkdir(parents=True, exist_ok=True)
    env = clean_env(
        MYRIAGENT_AGENT_PROBE=str(out_dir),
        MYRIAGENT_AGENT_PROBE_SEND="0",
        MYRIAGENT_AGENT_PROBE_NO_CLI="1",
        CLAUDE_CONFIG_DIR=str(empty / "claude"),
        CODEX_HOME=str(empty / "codex"),
        **extra_env,
    )
    started = time.time()
    proc = subprocess.Popen([str(app_exe), "--data", str(root)], env=env, cwd=str(app_exe.parent))
    try:
        proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        # 只結束自己啟動的那一個（含它的 WebView2 子程序）
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
        raise RuntimeError(f"app 沒有在 {timeout} 秒內結束：{out_dir}")
    report_file = out_dir / "report.json"
    if not report_file.exists():
        raise RuntimeError(f"沒有 report.json（結束碼 {proc.returncode}）：{out_dir}")
    report = json.loads(report_file.read_text(encoding="utf-8"))
    report["_seconds"] = round(time.time() - started, 1)
    return report


def pick(report):
    sync = report.get("sync") or {}
    at_live = report.get("memoryAtLiveMb") or {}
    idle = report.get("memoryIdleMb") or {}
    late = report.get("memoryIdleLateMb") or {}
    idle_late = report.get("idleLate") or {}
    return {
        "ok": report.get("ok"),
        "error": (report.get("error") or "")[:300] or None,
        "navigationCompletedMs": report.get("navigationCompletedMs"),
        "uiReadyMs": report.get("uiReadyMs"),
        "idleCpuPercent": report.get("idleCpuPercent"),
        "idleCpuWindows": report.get("idleCpuWindows"),
        "appPrivateMb": sync.get("appPrivateMb"),
        "memoryAtLiveMb": at_live.get("appPrivateMb"),
        "memoryIdleLateMb": late.get("appPrivateMb"),
        "gc": idle.get("gc"),
        "gcAtLive": at_live.get("gc"),
        "gcLate": late.get("gc"),
        "idleLateSeconds": idle_late.get("elapsedSeconds"),
        "idleAllocMbPerHour": idle_late.get("allocMbPerHour"),
        "gen0MaxBudgetMb": idle_late.get("gen0MaxBudgetMb"),
        "settleSeconds": report.get("settleSeconds"),
        "settleTimedOut": report.get("settleTimedOut"),
        "paired": sync.get("paired"),
        "state": sync.get("state"),
        "notOnline": sync.get("notOnline"),
        "heartbeatsDuringSample": sync.get("heartbeatsDuringSample"),
        "loadedForbidden": sync.get("loadedForbidden"),
        "consoleErrors": len(report.get("consoleErrors") or []),
        "seconds": report.get("_seconds"),
    }


# ───────────────────────────── 統計與判定 ─────────────────────────────

def numbers(items, key):
    return [r[key] for r in items if isinstance(r.get(key), (int, float)) and not isinstance(r.get(key), bool)]


def med(items, key):
    values = numbers(items, key)
    return statistics.median(values) if values else None


def p90(values):
    """最近秩的 90 百分位（回合數少時就是最大或次大）"""
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(0.9 * len(ordered)) - 1)]


GC_KEYS = ("heapMb", "committedMb", "fragmentedMb", "nativeEstimateMb")


def gc_medians(items, field="gc"):
    """各回合閒置取樣的 gc 細項取中位數（field：gc＝第一次、gcLate＝第二次；沒有的回合略過；全部沒有時每一項是 None）"""
    result = {}
    for key in GC_KEYS:
        values = [r[field][key] for r in items if isinstance(r.get(field), dict) and isinstance(r[field].get(key), (int, float))]
        result[key] = statistics.median(values) if values else None
    return result


def is_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def idle_max(round_):
    """一回合兩次閒置取樣（memoryIdleMb、memoryIdleLateMb）的較大值；有一個不是數字就是 None（WP4.9 合併前審查 F1）"""
    early, late = round_.get("appPrivateMb"), round_.get("memoryIdleLateMb")
    return max(early, late) if is_number(early) and is_number(late) else None


def idle_medians(items):
    """一組回合的閒置記憶體中位數：idle＝第一次取樣、late＝第二次、max＝每一回合兩次的較大值（沒有的回合略過；全部沒有是 None）"""
    maxes = [m for m in map(idle_max, items) if m is not None]
    return {"idle": med(items, "appPrivateMb"), "late": med(items, "memoryIdleLateMb"), "max": statistics.median(maxes) if maxes else None}


def delta(paired, unpaired):
    return None if paired is None or unpaired is None else round(paired - unpaired, 1)


def describe(items, key="uiReadyMs"):
    values = numbers(items, key)
    return {
        "n": len(values),
        "median": statistics.median(values) if values else None,
        "p90": p90(values),
        "min": min(values) if values else None,
        "max": max(values) if values else None,
    }


def judge(s5, s6, has_base, seed_problem=None):
    """
    前提檢查＋判定；前提不成立的判定是 None，原因列在 invalid。s5：組名 → 回合（HEAD_GROUPS 裡有跑的組、有基準時加 base-unpaired；
    head-paired-backfilling 的回合帶 pairBackfill＝那一輪 pair --no-drain 輸出的 backfill）。seed_problem：--conversations 時各資料根的對話不是同一份（原因）
    """
    invalid = {"s5": [], "s6Unpaired": [], "s6Cpu": [], "s6Memory": []}

    def require(condition, keys, reason):
        if not condition:
            for key in keys:
                invalid[key].append(reason)

    require(seed_problem is None, ["s5", "s6Memory"], f"各資料根的舊對話不是同一份：{seed_problem}")

    unpaired = s5["head-unpaired"]
    head_groups = [g for g in HEAD_GROUPS if g in s5]
    for group, items in s5.items():
        require(len(items) > 0, ["s5"], f"S5 {group} 沒有回合")
        require(all(r["ok"] is True for r in items), ["s5"] + (["s6Unpaired", "s6Memory"] if group == "head-unpaired" else []),
                f"S5 {group} 有回合不是 ok")
    require(all(r["paired"] is False for r in unpaired), ["s5", "s6Unpaired", "s6Memory"], "head-unpaired 有回合不是 paired=false")
    # 連不到的兩組（連線被拒、吞封包）：真的走到同步路徑（陽性對照）、而且沒連上
    for group in ("head-paired-offline", "head-paired-blackhole"):
        items = s5.get(group)
        if items is None:
            continue
        require(all(r["paired"] is True for r in items), ["s5"], f"{group} 有回合不是 paired=true（配對設定沒生效）")
        require(all(r["state"] in OFFLINE_STATES for r in items), ["s5"], f"{group} 有回合的 state 不是 offline／connecting")
        require(all(all(a in (r["loadedForbidden"] or []) for a in SYNC_ASSEMBLIES) for r in items), ["s5"],
                f"陽性對照失敗：{group} 有回合沒載入 SignalR 與 Sqlite（沒走到同步路徑）")
    if "head-paired-online" in s5:
        require(all(r["paired"] is True and r["state"] == "online" for r in s5["head-paired-online"]), ["s5"],
                "head-paired-online 有回合不是 paired=true、state=online（沒連上本機 MyriagentHub）")
    if "head-paired-backfilling" in s5:
        items = s5["head-paired-backfilling"]
        require(all(r["paired"] is True for r in items), ["s5"], "head-paired-backfilling 有回合不是 paired=true")
        require(all(r.get("pairBackfill") is not None for r in items), ["s5"],
                "head-paired-backfilling 有回合在 app 啟動之前回補就做完了（pair --no-drain 輸出的 backfill 是 null）：量到的不是回補中的暖啟動")
    require(len(s6) > 0, ["s6Cpu", "s6Memory"], "沒有 S6 已配對的回合")
    require(all(r["ok"] is True for r in s6), ["s6Cpu", "s6Memory"], "S6 已配對有回合不是 ok")
    require(all(r["paired"] is True and r["state"] == "online" and r["notOnline"] is not True for r in s6), ["s6Cpu", "s6Memory"],
            "S6 已配對有回合沒有連上（paired、state=online、沒有 notOnline）")
    require(all(isinstance(r["heartbeatsDuringSample"], int) and r["heartbeatsDuringSample"] > 0 for r in s6), ["s6Cpu"],
            "S6 已配對有回合的取樣窗裡沒有心跳（heartbeatsDuringSample 不是正數：量到的只是交握後的靜止期）")
    require(all(r.get("settleTimedOut") is False for r in s6), ["s6Memory"],
            "S6 已配對有回合沒有等到同步安定（settleTimedOut 不是 false：閒置記憶體取在啟動重整或回補的途中）")
    require(all(idle_max(r) is not None for r in [*s6, *unpaired]), ["s6Memory"],
            "S6 已配對或 head-unpaired 有回合沒有兩次閒置取樣（memoryIdleLateMb：探針太舊；只有積極回收之後 5 秒的那一次不算數）")

    stats = {group: describe(items) for group, items in s5.items()}
    # S6 那幾回合的介面就緒只供參考（不判定；S5 的 head-paired-online 是自己的一組）
    stats["s6-paired-online"] = describe(s6)
    reference = (stats["base-unpaired"]["median"] if "base-unpaired" in stats else None) if has_base else README_BASELINE_MAX_MS
    if reference is None:
        invalid["s5"].append("基準建置沒有可用的 uiReadyMs")
    head_medians = {group: stats[group]["median"] for group in head_groups}
    s5_pass = None
    if not invalid["s5"] and None not in head_medians.values():
        s5_pass = all(m <= reference + S5_MARGIN_MS for m in head_medians.values())
    p90_reference = stats["base-unpaired"]["p90"] if has_base and "base-unpaired" in stats else None
    p90s = [stats[group]["p90"] for group in head_groups]
    offline_minus_unpaired = (None if head_medians.get("head-paired-offline") is None or head_medians.get("head-unpaired") is None
                              else head_medians["head-paired-offline"] - head_medians["head-unpaired"])

    unpaired_mb = med(unpaired, "appPrivateMb")
    paired_mb = med(s6, "appPrivateMb")
    windows = [w for r in s6 for w in (r["idleCpuWindows"] or [r["idleCpuPercent"]]) if isinstance(w, (int, float))]
    cpu_max = max(windows) if windows else None
    delta_mb = delta(paired_mb, unpaired_mb)
    # 第二次閒置取樣（CPU 窗與 --idle-extra-seconds 之後）與判定用的「每一回合兩次取較大值」
    unpaired_late_mb = med(unpaired, "memoryIdleLateMb")
    paired_late_mb = med(s6, "memoryIdleLateMb")
    unpaired_max = [m for m in map(idle_max, unpaired) if m is not None]
    paired_max = [m for m in map(idle_max, s6) if m is not None]
    judged_delta_mb = delta(statistics.median(paired_max) if paired_max else None, statistics.median(unpaired_max) if unpaired_max else None)
    # S5 各組的閒置記憶體（WP4.9 步 6b，只供參考、不判定）：已配對但連不上的兩組不進 Live、不在 S6 的判定裡，但步 6b 要它們閒置時比未配對
    # 多不到約 10 MB（啟動之後的那一次積極回收）。idle＝第一次取樣（memoryIdleMb）、late＝第二次、max＝每一回合兩次的較大值；差值都減 head-unpaired
    idle_memory = {group: idle_medians(items) for group, items in s5.items()}
    idle_minus_unpaired = {group: {key: delta(values[key], idle_memory["head-unpaired"][key]) for key in ("idle", "max")}
                           for group, values in idle_memory.items() if group != "head-unpaired"}
    # 進 Live 當下（舊量法的取樣點）：只供和舊的數字對照，不判定
    unpaired_live_mb = med(unpaired, "memoryAtLiveMb")
    paired_live_mb = med(s6, "memoryAtLiveMb")
    live_delta_mb = None if unpaired_live_mb is None or paired_live_mb is None else round(paired_live_mb - unpaired_live_mb, 1)
    result = {
        "s5Stats": stats,
        "s5Reference": "base-unpaired" if has_base else f"README 基準上限 {README_BASELINE_MAX_MS} ms",
        "s5ReferenceMs": reference,
        "s5Note": None if has_base else "沒有 --base-app：和 README 記的基準上限比；已配對對未配對的差值在構造上必然接近 0，只證明延後啟動有效",
        "s5HeadGroups": head_groups,
        "s5HeadMinusReferenceMs": {g: (None if m is None or reference is None else m - reference) for g, m in head_medians.items()},
        "s5PairedMinusUnpairedMs": offline_minus_unpaired,
        "s5P90WithinBase": None if p90_reference is None or None in p90s else all(p <= p90_reference + S5_MARGIN_MS for p in p90s),
        "s5Pass": s5_pass,
        "s5IdleMemoryMb": idle_memory,
        "s5IdleMinusUnpairedMb": idle_minus_unpaired,
        "s6UnpairedForbiddenEmpty": all(r["loadedForbidden"] == [] for r in unpaired),
        "s6UnpairedIdleCpuMedian": med(unpaired, "idleCpuPercent"),
        "s6PairedOnline": len(s6) > 0 and all(r["state"] == "online" for r in s6),
        "s6PairedHeartbeats": [r["heartbeatsDuringSample"] for r in s6],
        "s6PairedIdleCpuMax": cpu_max,
        "s6PairedIdleCpuMedian": statistics.median(windows) if windows else None,
        "s6UnpairedPrivateMbMedian": unpaired_mb,
        "s6PairedPrivateMbMedian": paired_mb,
        "s6PrivateDeltaMb": delta_mb,
        "s6UnpairedLatePrivateMbMedian": unpaired_late_mb,
        "s6PairedLatePrivateMbMedian": paired_late_mb,
        "s6LateDeltaMb": delta(paired_late_mb, unpaired_late_mb),
        "s6JudgedDeltaMb": judged_delta_mb,
        "s6UnpairedAtLiveMbMedian": unpaired_live_mb,
        "s6PairedAtLiveMbMedian": paired_live_mb,
        "s6AtLiveDeltaMb": live_delta_mb,
        "s6Gc": {"unpaired": gc_medians(unpaired), "paired": gc_medians(s6)},
        "s6GcLate": {"unpaired": gc_medians(unpaired, "gcLate"), "paired": gc_medians(s6, "gcLate")},
        "s6IdleAllocMbPerHour": {"unpaired": med(unpaired, "idleAllocMbPerHour"), "paired": med(s6, "idleAllocMbPerHour")},
        "s6Gen0MaxBudgetMb": med(s6, "gen0MaxBudgetMb"),
        "s6IdleLateSeconds": [r.get("idleLateSeconds") for r in s6],
        "s6SettleSeconds": [r.get("settleSeconds") for r in s6],
    }
    result["s6UnpairedPass"] = None if invalid["s6Unpaired"] else result["s6UnpairedForbiddenEmpty"]
    result["s6CpuPass"] = None if invalid["s6Cpu"] or cpu_max is None else cpu_max <= S6_CPU_MAX_PERCENT
    # 設計 1.3 的 S6 是「私有記憶體 +≤30 MB」：剛好 30.0 MB 算過（同 S5 的「剛好等於基準＋50 ms 算過」）；每一回合取兩次閒置取樣的較大值（F1）
    result["s6MemoryPass"] = None if invalid["s6Memory"] or judged_delta_mb is None else judged_delta_mb <= S6_MEMORY_MAX_DELTA_MB
    result["invalid"] = {k: v for k, v in invalid.items() if v}
    return result


PASS_KEYS = ("s5Pass", "s6UnpairedPass", "s6CpuPass", "s6MemoryPass")


# ───────────────────────────── 主程式 ─────────────────────────────

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", required=True)
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--idle-runs", type=int, default=3)
    parser.add_argument("--seed", type=int, default=20261016)
    parser.add_argument("--base-app", help="基準建置（含 MYRIAGENT_AGENT_PROBE_NO_CLI 的探針開關）的 bin 資料夾或其中的 Myriagent.exe；見 README")
    parser.add_argument("--base-label", help="基準建置是哪一個（寫進 summary.baseLabel，例如 7c9bcd3+probe、1724718）")
    parser.add_argument("--conversations", type=int, default=0, help="每個資料根先灌這麼多個舊對話（seedConversations；要有 --base-app）")
    parser.add_argument("--items-per-conversation", type=int, default=40)
    parser.add_argument("--conversation-days", type=int, default=3, help="最新的舊對話比基準時間早幾天")
    parser.add_argument("--app-env", action="append", default=[], metavar="KEY=VALUE",
                        help="給受測的 app 加環境變數（可重複；只收 DOTNET_GC 開頭的鍵，GC 的實驗用）")
    parser.add_argument("--idle-extra-seconds", type=int, default=0,
                        help=f"探針取第二次閒置記憶體之前多等幾秒（0～{IDLE_EXTRA_MAX_SECONDS}；只給 head-unpaired 與 S6 已配對的回合）")
    args = parser.parse_args()
    # 先驗參數（不碰工作資料夾、不檢查建置）
    try:
        app_env = parse_app_env(args.app_env)
    except ValueError as ex:
        fail(str(ex))
    if not 0 <= args.idle_extra_seconds <= IDLE_EXTRA_MAX_SECONDS:
        fail(f"--idle-extra-seconds 要在 0～{IDLE_EXTRA_MAX_SECONDS} 之間：{args.idle_extra_seconds}")
    # 第二次閒置取樣之前多等的秒數：給 S6 記憶體判定用到的兩群（head-unpaired、S6 已配對）；時限跟著加
    idle_env = {IDLE_EXTRA_VARIABLE: str(args.idle_extra_seconds)} if args.idle_extra_seconds > 0 else {}
    idle_timeout = APP_TIMEOUT_SECONDS + args.idle_extra_seconds
    work = Path(args.work).resolve()
    base_src = Path(args.base_app).resolve() if args.base_app else None
    if base_src is not None and base_src.is_file():
        base_src = base_src.parent
    if args.conversations < 0:
        fail("--conversations 不能是負的")
    if args.conversations > 0 and base_src is None:
        fail("--conversations 要和 --base-app 一起用：同一份對話跑基準建置比較（不拿 README 的 930 ms 比）")
    seed = None
    if args.conversations > 0:
        seed = {"count": args.conversations, "olderThanDays": args.conversation_days, "itemsPerConversation": args.items_per_conversation,
                "seed": args.seed, "baseTimeUtc": SEED_BASE_TIME_UTC, "account": SEED_ACCOUNT}

    # 先靜態檢查兩份建置都認得 NO_CLI（HEAD 那份同時是這個檢查的陽性對照）
    if not has_no_cli_switch(APP_BIN):
        fail(f"HEAD 的建置找不到 {NO_CLI_VARIABLE}（先建置 slnx 的 Release）：{APP_BIN}")
    if base_src is not None and not has_no_cli_switch(base_src):
        fail(f"基準建置不認得 {NO_CLI_VARIABLE}：探針會找到並執行這台電腦真正的 claude／codex／grok，不量。見 README 的基準建置做法：{base_src}")
    prepare_work(work)

    summary = {"runs": args.runs, "idleRuns": args.idle_runs, "seed": args.seed, "baseLabel": args.base_label, "appEnv": app_env,
               "idleExtraSeconds": args.idle_extra_seconds, "build": build_info(base_src)}
    if seed is not None:
        summary["conversations"] = {"params": seed, "roots": {}, "backfillSeconds": None}
    keys_before = key_snapshot()

    # 複製一份再執行：殘留的程序不會鎖住 bin（下次建置 MSB3027）
    app_dir = work / "app"
    hub_dir = work / "hub"
    shutil.copytree(APP_BIN, app_dir)
    shutil.copytree(HUB_BIN, hub_dir)
    app_exe = app_dir / "Myriagent.exe"
    hub_exe = hub_dir / "MyriagentHub.exe"
    base_exe = None
    if base_src is not None:
        shutil.copytree(base_src, work / "app-base")
        base_exe = work / "app-base" / "Myriagent.exe"

    # 吞封包的監聽（head-paired-blackhole）在 MyriagentHub 之前開：開不起來時還沒有要收的程序
    blackhole = Blackhole()
    summary["blackholePort"] = blackhole.port
    port = free_port()
    summary["port"] = port
    hub_data = work / "hub-data"
    hub_data.mkdir()
    hub_log = open(work / "hub.log", "w", encoding="utf-8")
    hub = subprocess.Popen([str(hub_exe), *hub_args(hub_data, port)], cwd=str(hub_dir), env=clean_env(), stdout=hub_log, stderr=subprocess.STDOUT)
    paired_root = work / "root-paired"
    unpaired_root = work / "root-unpaired"
    base_root = work / "root-base-unpaired"
    backfill_roots = []  # head-paired-backfilling 每一輪配對的資料根（收尾時一一解除配對）

    def pair_root(root, name, *extra):
        return fake_device(root, "pair", "--server", f"https://127.0.0.1:{port}", "--name", name, "--dev-trust",
                           "--owner-user", "admin", "--owner-password-env", "FD_OWNER_PW", "--owner-origin", "https://localhost", *extra,
                           env=clean_env(FD_OWNER_PW=OWNER_PASSWORD))

    def prepare_backfilling(i):
        """head-paired-backfilling 的第 i 輪：新的資料根灌 N 個舊對話（自己的種子）→ pair --no-drain；回傳（資料根, 這一輪要記的欄位）"""
        root = work / f"root-backfilling-{i + 1}"
        round_seed = dict(seed, seed=args.seed + BACKFILL_SEED_OFFSET + i, account=f"{SEED_ACCOUNT}-b{i + 1}")
        script = work / f"backfilling-{i + 1}.json"
        write_script(script, f"measure-backfilling-{i + 1}", round_seed)
        seeded_round = seeded_of(fake_device(root, "run", "--script", str(script), timeout=900))
        backfill_roots.append(root)
        paired = pair_root(root, f"量測用電腦（回補中 {i + 1}）", "--no-drain")
        return root, {"seed": round_seed["seed"], "seeded": seeded_round, "pairBackfill": paired.get("backfill"), "pairDevice": paired.get("deviceId")}

    try:
        wait_healthz(port)
        log(f"MyriagentHub 就緒：https://127.0.0.1:{port}；吞封包的監聽：127.0.0.1:{blackhole.port}")

        write_script(work / "unpaired.json", "measure-unpaired", seed)
        write_script(work / "paired.json", "measure-paired", seed)
        seeded = {"unpaired": seeded_of(fake_device(unpaired_root, "run", "--script", str(work / "unpaired.json"), timeout=900))}
        if base_exe is not None:
            seeded["base-unpaired"] = seeded_of(fake_device(base_root, "run", "--script", str(work / "unpaired.json"), timeout=900))
        pair = pair_root(paired_root, "量測用電腦")
        # 已配對的資料根在配對之後才灌（劇本最前面的 seedConversations 在 fake-device 的中樞啟動之前寫）；回補可能要一段時間
        seeded["paired"] = seeded_of(fake_device(paired_root, "run", "--script", str(work / "paired.json"), "--dev-trust", "--timeout", "1800", timeout=1900))
        summary["pairedDevice"] = pair.get("deviceId")
        log(f"配對完成：{pair.get('deviceId')}")
        seed_problem = None
        if seed is not None:
            summary["conversations"]["roots"] = seeded
            hashes = {name: (s or {}).get("sha256") for name, s in seeded.items()}
            if None in hashes.values() or len(set(hashes.values())) != 1:
                seed_problem = f"SHA-256 {hashes}"
            log(f"舊對話：{seeded}")

        # S5：每一輪以固定種子打亂各組的順序（順序造成的偏差互相抵消）。值：(exe, 資料根, 這一組的環境變數)；回補中那一組每一輪現做資料根（None）
        groups = {
            "head-unpaired": (app_exe, unpaired_root, idle_env),
            "head-paired-online": (app_exe, paired_root, {"MYRIAGENT_SYNC_DEV_TRUST": "1"}),
            "head-paired-offline": (app_exe, paired_root, {"MYRIAGENT_SYNC_SERVER": UNREACHABLE_SERVER}),
            "head-paired-blackhole": (app_exe, paired_root, {"MYRIAGENT_SYNC_SERVER": f"https://127.0.0.1:{blackhole.port}"}),
        }
        if seed is not None:
            groups["head-paired-backfilling"] = (app_exe, None, {"MYRIAGENT_SYNC_DEV_TRUST": "1"})
        if base_exe is not None:
            groups["base-unpaired"] = (base_exe, base_root, {})
        rng = random.Random(args.seed)
        s5 = {name: [] for name in groups}
        orders = []
        for i in range(args.runs):
            order = list(groups)
            rng.shuffle(order)
            orders.append(order)
            for name in order:
                exe, root, extra = groups[name]
                fields = {}
                if root is None:
                    root, fields = prepare_backfilling(i)
                    log(f"S5 {name} #{i + 1}：資料根 {root.name}、種子 {fields['seed']}、pair 時的回補進度 {fields['pairBackfill']}")
                # 伺服器上別台裝置的對話數（這一回合開始時；F6）：root-paired 的別台＝每一輪回補中的裝置，回補中那一輪的別台＝root-paired＋前幾輪
                # （它自己這一輪已排進 backfill_roots，算起來同樣是輪數）。未配對與基準不連伺服器
                if root is not unpaired_root and root is not base_root:
                    fields = fields | {"serverOtherConversations": args.conversations * len(backfill_roots)}
                timeout = idle_timeout if name == "head-unpaired" else APP_TIMEOUT_SECONDS
                report = pick(run_app(exe, root, work / "out" / f"s5-{name}-{i + 1}", work, extra | app_env, timeout)) | fields
                s5[name].append(report)
                log(f"S5 {name} #{i + 1}：uiReady {report['uiReadyMs']} ms、ok {report['ok']}、paired {report['paired']}、state {report['state']}、"
                    f"進 Live 時 {report['memoryAtLiveMb']} MB、閒置 {report['appPrivateMb']} MB")
        summary["s5Order"] = orders
        summary["s5"] = s5
        summary["blackholeAccepted"] = blackhole.accepted

        # S6：已配對、連本機 MyriagentHub、閒置。有舊對話時先等回補做完（進度是 null、outbox 清空）才取樣（回補不限速，答 3）
        if seed is not None:
            status = fake_device(paired_root, "status", "--dev-trust", "--wait-backfill", "3600", timeout=3700)
            summary["conversations"]["backfillSeconds"] = status.get("backfillSeconds")
            summary["conversations"]["backfillStatus"] = {k: status.get(k) for k in ("state", "conversations", "backfill", "outbox")}
            log(f"回補做完：{status.get('backfillSeconds')} 秒，{status.get('conversations')} 個對話，backfill {status.get('backfill')}")
        # S6 的回合在全部 S5 之後：伺服器上已有每一輪回補中那台裝置的對話，root-paired 連上時拉成副本（F6）
        summary["s6ReplicaConversations"] = args.conversations * len(backfill_roots)
        s6 = []
        for i in range(args.idle_runs):
            report = pick(run_app(app_exe, paired_root, work / "out" / f"s6-paired-{i + 1}", work, {"MYRIAGENT_SYNC_DEV_TRUST": "1"} | idle_env | app_env, idle_timeout))
            s6.append(report)
            log(f"S6 paired #{i + 1}：state {report['state']}、CPU {report['idleCpuWindows']}%、心跳 {report['heartbeatsDuringSample']}、"
                f"安定 {report['settleSeconds']} 秒（逾時 {report['settleTimedOut']}）、進 Live 時 {report['memoryAtLiveMb']} MB、閒置 {report['appPrivateMb']} MB、"
                f"晚取 {report['memoryIdleLateMb']} MB（{report['idleLateSeconds']} 秒後、配置 {report['idleAllocMbPerHour']} MB/h）、gc {report['gc']}")
        summary["s6Paired"] = s6

        summary["result"] = judge(s5, s6, base_exe is not None, seed_problem)
    finally:
        cleanup = summary["cleanup"] = {}
        blackhole.close()
        errors = []
        pending_left = []
        for root in [paired_root, *backfill_roots]:
            sync_dir = root / "sync"
            try:
                # 不看配對有沒有「成功」：伺服器已收下、device.json 已寫入之後才失敗時也要解除
                if (sync_dir / "device.json").exists():
                    fake_device(root, "unpair")
                    cleanup[f"unpair:{root.name}"] = "ok"
                    log(f"{root.name} 已解除配對（刪掉裝置金鑰）")
                elif (sync_dir / "pending-key.json").exists():
                    fake_device(root, "status")
                    cleanup[f"status:{root.name}"] = "ok"
                    log(f"{root.name} 只剩 pending-key.json：跑一次 status 讓配對清理刪掉")
                if (sync_dir / "pending-key.json").exists():
                    pending_left.append(root.name)
            except Exception as ex:  # noqa: BLE001 - 收尾失敗記下來，結束碼照樣是 1
                log(f"{root.name} 解除配對失敗：{ex}")
                errors.append(f"{root.name}：{ex}")
        cleanup["pendingKeyLeft"] = pending_left
        if errors:
            cleanup["error"] = "；".join(errors)
        stop = subprocess.run([str(hub_exe), "admin", "stop", "--wait", "20", "--data", str(hub_data)], capture_output=True, text=True, encoding="utf-8", env=clean_env())
        try:
            hub.wait(timeout=30)
        except subprocess.TimeoutExpired:
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(hub.pid)], capture_output=True)
            summary["hubForcedKill"] = True
        hub_log.close()
        summary["hubStopExit"] = stop.returncode
        ours, others = new_device_keys(keys_before, [FAKE_DEVICE, work])
        summary["orphanDeviceKeys"] = ours
        summary["otherNewDeviceKeys"] = others  # 別的程式（例如同時在跑的測試）建的，只記錄
        if ours:
            log(f"留下 {len(ours)} 個這次建的裝置金鑰檔：{ours}")
        (work / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=1), encoding="utf-8")

    result = summary["result"]
    print(json.dumps({k: result.get(k) for k in PASS_KEYS} | {"invalid": result["invalid"], "orphanDeviceKeys": summary["orphanDeviceKeys"]}, ensure_ascii=False))
    if any(result.get(k) is not True for k in PASS_KEYS) or summary["orphanDeviceKeys"] or cleanup.get("error") or cleanup.get("pendingKeyLeft"):
        sys.exit(1)


if __name__ == "__main__":
    main()

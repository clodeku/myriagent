"""measure_sync.py 的純函式測試（WP4.9 步 1、2 與合併前審查）：--app-env 的驗證、子程序剝掉的環境變數（含 COMPlus_，三份清單相同）、
判定 judge 的前提、S6 記憶體以兩次閒置取樣的較大值判定與 +≤30 MB 的邊界、pick 讀第二次取樣。

執行：python -X utf8 -m unittest discover -s tools/fake-device -p "test_*.py"
不啟動 app、MyriagentHub 或 fake-device（只有 --app-env 的結束碼那一項以子程序跑腳本本身，在驗參數時就結束）。
"""

import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import measure_sync  # noqa: E402

SYNC = list(measure_sync.SYNC_ASSEMBLIES)


def rnd(**over):
    """一回合 pick() 的結果（預設是 ok、已配對而且連上、安定、有心跳的 S6 回合）"""
    base = {
        "ok": True,
        "error": None,
        "uiReadyMs": 900,
        "idleCpuPercent": 0.01,
        "idleCpuWindows": [0.01, 0.01, 0.0],
        "appPrivateMb": 60.0,
        "memoryAtLiveMb": 90.0,
        "memoryIdleLateMb": 62.0,
        "gc": {"heapMb": 20.0, "committedMb": 30.0, "fragmentedMb": 1.0, "nativeEstimateMb": 30.0},
        "gcLate": {"heapMb": 20.0, "committedMb": 32.0, "fragmentedMb": 1.0, "nativeEstimateMb": 30.0},
        "idleLateSeconds": 30.0,
        "idleAllocMbPerHour": 40.0,
        "gen0MaxBudgetMb": 48.0,
        "settleSeconds": 3.2,
        "settleTimedOut": False,
        "paired": True,
        "state": "online",
        "notOnline": None,
        "heartbeatsDuringSample": 2,
        "loadedForbidden": SYNC + ["e_sqlite3.dll"],
    }
    base.update(over)
    return base


def unpaired(**over):
    values = {"paired": False, "state": None, "loadedForbidden": [], "appPrivateMb": 40.0, "memoryAtLiveMb": 41.0, "memoryIdleLateMb": 41.0,
              "settleSeconds": None, "settleTimedOut": None, "heartbeatsDuringSample": None}
    values.update(over)
    return rnd(**values)


def offline(**over):
    values = {"state": "offline", "notOnline": True, "settleSeconds": None, "settleTimedOut": None, "heartbeatsDuringSample": None}
    values.update(over)
    return rnd(**values)


def blackhole(**over):
    values = {"state": "connecting"}
    values.update(over)
    return offline(**values)


def backfilling(**over):
    values = {"pairBackfill": {"conversationsLeft": 300, "bytesLeft": 8_000_000}, "seed": 1001}
    values.update(over)
    return rnd(**values)


BASE_MS = 880  # base-unpaired 的中位數（判定的基準）


def make(group, ui_ready=900, **over):
    """某一組的一回合（預設的介面就緒 900 ms ≤ 基準 880＋50）"""
    factory = {
        "head-unpaired": unpaired,
        "head-paired-online": rnd,
        "head-paired-offline": offline,
        "head-paired-blackhole": blackhole,
        "head-paired-backfilling": backfilling,
        "base-unpaired": unpaired,
    }[group]
    return factory(uiReadyMs=ui_ready, **over)


def s5_groups(**overrides):
    """S5 的五個 head 組與基準組各三回合；overrides 以組名換掉整組"""
    groups = {group: [make(group) for _ in range(3)] for group in measure_sync.HEAD_GROUPS}
    groups["base-unpaired"] = [make("base-unpaired", BASE_MS) for _ in range(3)]
    groups.update(overrides)
    return groups


class AppEnvTests(unittest.TestCase):
    def test_只收DOTNET_GC開頭_不分大小寫(self):
        env = measure_sync.parse_app_env(["DOTNET_GCgen0size=0x400000", "dotnet_gcConserveMemory=5"])
        self.assertEqual(env, {"DOTNET_GCgen0size": "0x400000", "dotnet_gcConserveMemory": "5"})
        self.assertEqual(measure_sync.parse_app_env([]), {})

    def test_其他的鍵或格式不對都丟ValueError(self):
        for bad in ["MYRIAGENT_X=1", "DOTNET_gcServer", "DOTNET_GCX=", "=1", "COMPlus_GCgen0size=1", "XDOTNET_GC=1"]:
            with self.subTest(bad=bad):
                with self.assertRaises(ValueError):
                    measure_sync.parse_app_env([bad])

    def test_父程序的DOTNET_GC不傳給子程序(self):
        self.assertTrue("DOTNET_GCGEN0SIZE".startswith(measure_sync.STRIP_PREFIXES), "子程序只拿得到 --app-env 給的 GC 設定")

    def test_父程序的COMPlus_GC也不傳給子程序(self):
        # COMPlus_ 是舊的前綴、.NET 10 照樣採用（COMPlus_GCgen0MaxBudget、COMPlus_gcServer 都生效；合併前審查 F3）
        saved = dict(measure_sync.os.environ)
        try:
            measure_sync.os.environ.update({"COMPlus_GCgen0size": "0x400000", "COMPlus_gcServer": "1", "DOTNET_gcServer": "1", "KEEP_ME_MEASURE": "1"})
            env = measure_sync.clean_env(DOTNET_GCgen0size="0x600000")
        finally:
            measure_sync.os.environ.clear()
            measure_sync.os.environ.update(saved)
        # Windows 的 os.environ 把鍵轉成大寫：一律不分大小寫比
        upper = {k.upper() for k in env}
        self.assertFalse(any(k.startswith("COMPLUS_") for k in upper), sorted(k for k in upper if "COMPLUS" in k))
        self.assertNotIn("DOTNET_GCSERVER", upper)
        self.assertIn("KEEP_ME_MEASURE", upper)
        self.assertEqual(env["DOTNET_GCgen0size"], "0x600000", "明確給的（--app-env）照樣放回")

    def test_剝掉的清單和hub_e2e與hub_probe相同(self):
        tools = Path(measure_sync.__file__).resolve().parents[1]
        js = (tools / "hub-e2e" / "lib" / "child-env.mjs").read_text(encoding="utf-8")
        cs = (tools / "hub-probe" / "ProbeClient" / "ChildEnvironment.cs").read_text(encoding="utf-8")
        js_list = re.findall(r"'([^']+)'", re.search(r"STRIP_PREFIXES = \[([^\]]*)\]", js).group(1))
        cs_list = re.findall(r'"([^"]+)"', re.search(r"StripPrefixes =\s*\[([^\]]*)\]", cs).group(1))
        self.assertEqual(list(measure_sync.STRIP_PREFIXES), js_list)
        self.assertEqual(list(measure_sync.STRIP_PREFIXES), cs_list)

    def test_腳本帶不合格的app_env_結束碼2_不碰工作資料夾(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp) / "work"
            r = subprocess.run([sys.executable, "-X", "utf8", str(Path(measure_sync.__file__)), "--work", str(work), "--app-env", "MYRIAGENT_X=1"],
                               capture_output=True, text=True, encoding="utf-8", timeout=60)
            self.assertEqual(r.returncode, 2, r.stderr)
            self.assertIn("--app-env", r.stderr)
            self.assertFalse(work.exists(), "驗參數時就結束：還沒建工作資料夾")


class JudgeSettleTests(unittest.TestCase):
    def test_全部前提成立_S6記憶體有判定_進Live的差值另列(self):
        result = measure_sync.judge(s5_groups(), [rnd() for _ in range(3)], has_base=True)
        self.assertEqual(result["invalid"], {})
        self.assertIs(result["s6MemoryPass"], True)
        self.assertEqual(result["s6PrivateDeltaMb"], 20.0)
        self.assertEqual(result["s6AtLiveDeltaMb"], 49.0, "進 Live 當下的差值（舊量法）只供對照")
        self.assertEqual(result["s6Gc"]["paired"]["heapMb"], 20.0)
        self.assertEqual(result["s6SettleSeconds"], [3.2, 3.2, 3.2])

    def test_任一S6回合安定逾時_記憶體判定是None(self):
        s6 = [rnd(), rnd(settleTimedOut=True), rnd()]
        result = measure_sync.judge(s5_groups(), s6, has_base=True)
        self.assertIsNone(result["s6MemoryPass"])
        self.assertTrue(any("settleTimedOut" in reason for reason in result["invalid"]["s6Memory"]))
        self.assertIs(result["s6CpuPass"], True, "CPU 的判定不受影響")

    def test_S6回合沒有記安定_也算無效(self):
        result = measure_sync.judge(s5_groups(), [rnd(settleTimedOut=None) for _ in range(3)], has_base=True)
        self.assertIsNone(result["s6MemoryPass"], "沒有等安定的建置量到的不是新量法")


class JudgeS6MemoryTests(unittest.TestCase):
    """WP4.9 合併前審查 F1、F7：每一回合取兩次閒置取樣的較大值判定、上限是 +≤30 MB"""

    def judge(self, s6, unpaired_rounds=None):
        groups = s5_groups(**({"head-unpaired": unpaired_rounds} if unpaired_rounds is not None else {}))
        return measure_sync.judge(groups, s6, has_base=True)

    # head-unpaired 預設兩次是 40.0／41.0：每一回合的較大值 41.0

    def test_剛好多30MB_通過(self):
        result = self.judge([rnd(appPrivateMb=71.0, memoryIdleLateMb=71.0) for _ in range(3)])
        self.assertEqual(result["s6JudgedDeltaMb"], 30.0)
        self.assertIs(result["s6MemoryPass"], True, "設計是 +≤30 MB")

    def test_多30點1MB_不過(self):
        result = self.judge([rnd(appPrivateMb=71.1, memoryIdleLateMb=71.1) for _ in range(3)])
        self.assertEqual(result["s6JudgedDeltaMb"], 30.1)
        self.assertIs(result["s6MemoryPass"], False)

    def test_晚取的那次長回來_以較大值判定(self):
        # 第一次在積極回收之後 5 秒（低點）、第二次 gen0 的已提交長回來：只看第一次會判過
        result = self.judge([rnd(appPrivateMb=60.0, memoryIdleLateMb=95.0) for _ in range(3)])
        self.assertEqual(result["s6PrivateDeltaMb"], 20.0, "第一次的差值另列")
        self.assertEqual(result["s6LateDeltaMb"], 54.0)
        self.assertEqual(result["s6JudgedDeltaMb"], 54.0)
        self.assertIs(result["s6MemoryPass"], False)

    def test_未配對晚取的較大_也照較大值(self):
        result = self.judge([rnd(appPrivateMb=60.0, memoryIdleLateMb=60.0) for _ in range(3)],
                            [unpaired(appPrivateMb=30.0, memoryIdleLateMb=45.0) for _ in range(3)])
        self.assertEqual(result["s6PrivateDeltaMb"], 30.0)
        self.assertEqual(result["s6JudgedDeltaMb"], 15.0, "兩邊都取每一回合的較大值")
        self.assertIs(result["s6MemoryPass"], True)

    def test_沒有晚取的那次_記憶體判定是None(self):
        for s6, unpaired_rounds in [([rnd(), rnd(memoryIdleLateMb=None), rnd()], None),
                                    ([rnd() for _ in range(3)], [unpaired(), unpaired(memoryIdleLateMb=None), unpaired()])]:
            with self.subTest(s6=[r["memoryIdleLateMb"] for r in s6]):
                result = self.judge(s6, unpaired_rounds)
                self.assertIsNone(result["s6MemoryPass"])
                self.assertTrue(any("memoryIdleLateMb" in reason for reason in result["invalid"]["s6Memory"]))
                self.assertIs(result["s6CpuPass"], True, "CPU 的判定不受影響")

    def test_配置速率與gen0預算另列(self):
        result = self.judge([rnd() for _ in range(3)])
        self.assertEqual(result["s6IdleAllocMbPerHour"], {"unpaired": 40.0, "paired": 40.0})
        self.assertEqual(result["s6Gen0MaxBudgetMb"], 48.0)
        self.assertEqual(result["s6GcLate"]["paired"]["committedMb"], 32.0)


class S5IdleMemoryTests(unittest.TestCase):
    """WP4.9 步 6b：S5 各組的閒置記憶體與減 head-unpaired 的差值（只供參考，不判定）"""

    def test_連不上的兩組閒置比未配對多多少_第一次與每一回合較大值各列一個(self):
        groups = s5_groups(**{
            "head-paired-offline": [make("head-paired-offline", appPrivateMb=mb, memoryIdleLateMb=mb + 2) for mb in (47.0, 49.0, 52.0)],
            "head-paired-blackhole": [make("head-paired-blackhole", appPrivateMb=50.0, memoryIdleLateMb=None) for _ in range(3)],
        })
        result = measure_sync.judge(groups, [rnd() for _ in range(3)], has_base=True)
        # head-unpaired 預設兩次是 40.0／41.0
        self.assertEqual(result["s5IdleMemoryMb"]["head-unpaired"], {"idle": 40.0, "late": 41.0, "max": 41.0})
        self.assertEqual(result["s5IdleMemoryMb"]["head-paired-offline"], {"idle": 49.0, "late": 51.0, "max": 51.0})
        self.assertEqual(result["s5IdleMinusUnpairedMb"]["head-paired-offline"], {"idle": 9.0, "max": 10.0})
        self.assertEqual(result["s5IdleMemoryMb"]["head-paired-blackhole"], {"idle": 50.0, "late": None, "max": None}, "沒有第二次取樣：較大值是 None")
        self.assertEqual(result["s5IdleMinusUnpairedMb"]["head-paired-blackhole"], {"idle": 10.0, "max": None})
        self.assertNotIn("head-unpaired", result["s5IdleMinusUnpairedMb"])
        self.assertIn("base-unpaired", result["s5IdleMinusUnpairedMb"], "基準建置也列（只供參考）")
        self.assertEqual(result["invalid"], {}, "只供參考：不影響任何判定")
        self.assertIs(result["s5Pass"], True)


class PickTests(unittest.TestCase):
    def test_第二次閒置取樣與配置速率從report取(self):
        report = {
            "ok": True,
            "memoryIdleMb": {"appPrivateMb": 35.2, "gc": {"heapMb": 8.8}},
            "memoryIdleLateMb": {"appPrivateMb": 51.0, "gc": {"heapMb": 9.0, "committedMb": 40.1}},
            "idleLate": {"extraSeconds": 1800, "elapsedSeconds": 1830.2, "allocatedMb": 20.3, "allocMbPerHour": 39.9, "gen0MaxBudgetMb": 48.0},
            "sync": {"appPrivateMb": 35.2},
        }
        picked = measure_sync.pick(report)
        self.assertEqual(picked["appPrivateMb"], 35.2)
        self.assertEqual(picked["memoryIdleLateMb"], 51.0)
        self.assertEqual(picked["gcLate"]["committedMb"], 40.1)
        self.assertEqual(picked["idleLateSeconds"], 1830.2)
        self.assertEqual(picked["idleAllocMbPerHour"], 39.9)
        self.assertEqual(picked["gen0MaxBudgetMb"], 48.0)
        self.assertEqual(measure_sync.idle_max(picked), 51.0)

    def test_idle_extra_seconds超出範圍_結束碼2_不碰工作資料夾(self):
        with tempfile.TemporaryDirectory() as tmp:
            work = Path(tmp) / "work"
            r = subprocess.run([sys.executable, "-X", "utf8", str(Path(measure_sync.__file__)), "--work", str(work), "--idle-extra-seconds", "-1"],
                               capture_output=True, text=True, encoding="utf-8", timeout=60)
            self.assertEqual(r.returncode, 2, r.stderr)
            self.assertIn("--idle-extra-seconds", r.stderr)
            self.assertFalse(work.exists())


class JudgeS5Tests(unittest.TestCase):
    """WP4.9 步 2：五個 head 組都和基準比、各組自己的前提"""

    def judge(self, **overrides):
        return measure_sync.judge(s5_groups(**overrides), [rnd() for _ in range(3)], has_base=True)

    def test_全部在基準加50ms以內_通過(self):
        result = self.judge()
        self.assertEqual(result["invalid"], {})
        self.assertEqual(result["s5HeadGroups"], list(measure_sync.HEAD_GROUPS))
        self.assertEqual(result["s5ReferenceMs"], BASE_MS)
        self.assertIs(result["s5Pass"], True)

    def test_每一個head組超過基準加50ms_s5Pass為假(self):
        for group in measure_sync.HEAD_GROUPS:
            with self.subTest(group=group):
                result = self.judge(**{group: [make(group, BASE_MS + 51) for _ in range(3)]})
                self.assertEqual(result["invalid"], {}, "前提都成立，只有這一組慢")
                self.assertIs(result["s5Pass"], False, f"只有 {group} 超過")
                self.assertEqual(result["s5HeadMinusReferenceMs"][group], 51)

    def test_剛好等於基準加50ms_通過(self):
        for group in measure_sync.HEAD_GROUPS:
            with self.subTest(group=group):
                self.assertIs(self.judge(**{group: [make(group, BASE_MS + 50) for _ in range(3)]})["s5Pass"], True)

    def test_沒有回補中那一組_其他四組照判(self):
        groups = s5_groups()
        del groups["head-paired-backfilling"]
        result = measure_sync.judge(groups, [rnd() for _ in range(3)], has_base=True)
        self.assertEqual(result["s5HeadGroups"], [g for g in measure_sync.HEAD_GROUPS if g != "head-paired-backfilling"])
        self.assertIs(result["s5Pass"], True)

    def test_blackhole組沒載入SignalR_判定為null(self):
        result = self.judge(**{"head-paired-blackhole": [blackhole(loadedForbidden=[]) for _ in range(3)]})
        self.assertIsNone(result["s5Pass"])
        self.assertTrue(any("head-paired-blackhole" in reason for reason in result["invalid"]["s5"]))

    def test_blackhole組連上了_判定為null(self):
        result = self.judge(**{"head-paired-blackhole": [blackhole(state="online") for _ in range(3)]})
        self.assertIsNone(result["s5Pass"], "吞封包的伺服器不可能連上：沒有指到監聽")

    def test_online組不是online_判定為null(self):
        result = self.judge(**{"head-paired-online": [rnd(), rnd(state="connecting"), rnd()]})
        self.assertIsNone(result["s5Pass"])
        self.assertTrue(any("head-paired-online" in reason for reason in result["invalid"]["s5"]))

    def test_backfilling組backfill是null_判定為null(self):
        result = self.judge(**{"head-paired-backfilling": [backfilling(), backfilling(pairBackfill=None), backfilling()]})
        self.assertIsNone(result["s5Pass"])
        self.assertTrue(any("head-paired-backfilling" in reason for reason in result["invalid"]["s5"]))

    def test_offline組的前提照舊(self):
        result = self.judge(**{"head-paired-offline": [offline(paired=False) for _ in range(3)]})
        self.assertIsNone(result["s5Pass"])


class BlackholeTests(unittest.TestCase):
    def test_接受連線_從不回應_關掉之後連不上(self):
        import socket

        hole = measure_sync.Blackhole()
        try:
            client = socket.create_connection(("127.0.0.1", hole.port), timeout=2)
            client.sendall(b"\x16\x03\x01 hello")
            client.settimeout(0.5)
            with self.assertRaises(socket.timeout, msg="接受了連線，但什麼都不回"):
                client.recv(1)
        finally:
            hole.close()
        client.settimeout(2)
        data = b"x"
        try:
            data = client.recv(1)
        except OSError:
            data = b""
        finally:
            client.close()
        self.assertEqual(data, b"", "關掉之後連線結束")
        self.assertGreaterEqual(hole.accepted, 1)
        with self.assertRaises(OSError):
            socket.create_connection(("127.0.0.1", hole.port), timeout=1).close()


if __name__ == "__main__":
    unittest.main()

"""load_stats.py 的測試（WP4.9 步 6b）：load-sampler.ps1 一行的格式、平均與範圍、超過 50% 的次數、沒出現的程序、看不懂的行。

執行：python -X utf8 -m unittest discover -s tools/fake-device -p "test_*.py"
"""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import load_stats  # noqa: E402


class SummarizeTests(unittest.TestCase):
    def test_平均範圍與超過50的次數_沒出現的程序是0(self):
        text = "\n".join([
            "2026-10-21T20:10:00.1234567+08:00 cpu=3 dotnet=4 node=40 MyriagentHub=1",
            "雜訊：看不懂的行略過",
            "2026-10-21T20:10:30.1234567+08:00 cpu=51 dotnet=8 node=42 MyriagentHub=1 testhost=1",
            "2026-10-21T20:11:00.1234567+08:00 cpu=50 dotnet=6 node=41",
            "",
        ])
        result = load_stats.summarize(text)
        self.assertEqual(result["samples"], 3)
        self.assertEqual((result["from"], result["to"]), ("20:10", "20:11"))
        self.assertEqual(result["cpuMean"], 34.7)
        self.assertEqual((result["cpuMin"], result["cpuMax"]), (3.0, 51.0))
        self.assertEqual(result["cpuOverBusy"], 1, "剛好 50% 不算")
        self.assertEqual(result["dotnet"], [4, 8])
        self.assertEqual(result["MyriagentHub"], [0, 1], "沒出現的那一行算 0")
        self.assertEqual(result["testhost"], [0, 1])
        self.assertEqual(result["chrome"], [0, 0])

    def test_小數的CPU也認得(self):
        self.assertEqual(load_stats.summarize("2026-10-21T20:10:00+08:00 cpu=7.5 dotnet=1")["cpuMean"], 7.5)

    def test_空的檔_取樣數0(self):
        result = load_stats.summarize("")
        self.assertEqual(result["samples"], 0)
        self.assertIsNone(result["cpuMean"])
        self.assertIsNone(result["dotnet"])


if __name__ == "__main__":
    unittest.main()

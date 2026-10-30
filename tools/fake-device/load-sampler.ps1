param(
    [Parameter(Mandatory = $true)][string]$Out,
    [Parameter(Mandatory = $true)][string]$Stop,
    [int]$Seconds = 30
)
# 量測期間的整機負載（WP4.9：measure_sync 的數字旁邊要記當時的負載，S5 的時間照負載判讀）。
# 每 $Seconds 秒寫一行「<ISO 時間> cpu=<整機 CPU 百分比> <程序名>=<數量> ...」到 $Out，$Stop 這個檔出現就結束；
# 程序數含量測自己啟動的 MyriagentHub、app、fake-device、WebView2，與別的 session 的 dotnet、testhost、node、chrome。
# 摘要（平均、範圍、>50% 的次數、各程序數的範圍）用 load_stats.py。只讀程序清單與 CPU 計數器，不碰任何程序。
# 用法（量測的同時在背景跑）：
#   $job = Start-Job -FilePath tools/fake-device/load-sampler.ps1 -ArgumentList <out.txt>, <stop 檔>, 30
#   python -X utf8 tools/fake-device/measure_sync.py ...
#   New-Item -ItemType File <stop 檔>; Wait-Job $job -Timeout 90; Remove-Job -Force $job
$pattern = '^(dotnet|testhost|MyriagentHub|node|FakeDevice|Myriagent|msedgewebview2|python|pythonw|chrome)$'
while (-not (Test-Path $Stop)) {
    $groups = Get-Process | Where-Object { $_.ProcessName -match $pattern } | Group-Object ProcessName | ForEach-Object { "$($_.Name)=$($_.Count)" }
    $cpu = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    "$(Get-Date -Format o) cpu=$cpu $($groups -join ' ')" | Add-Content -Encoding utf8 $Out
    Start-Sleep -Seconds $Seconds
}

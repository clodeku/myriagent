# desk-probe：遠端桌面的擷取探針

回報「這台現在擷取得到什麼」，查遠端桌面看不到畫面時在**被看的那台**執行（`docs/sync-protocol.md` 14.16）。只呼叫桌面組件的 `RemoteDesk/DeskDiagnostics`，所以用的是 app 真正的擷取程式（`DxgiDeskScreen`、`GdiDeskScreen`、鎖定判斷）。

不進 `Myriagent.slnx`，各自建置：

```bash
dotnet build tools/desk-probe -c Release -nodeReuse:false -p:UseSharedCompilation=false
tools/desk-probe/bin/Release/net10.0-windows/win-x64/desk-probe.exe [--wake] [--all] [--seconds N] [--out <檔案>]
```

| 選項 | 做什麼 |
|---|---|
| （沒有） | 回報鎖定、輸入桌面、螢幕開關；GDI 直接讀一張；DXGI 擷取幾秒（不叫醒螢幕） |
| `--wake` | 最後再以 app 實際的方式擷取一次：一直沒有第一張畫面時送一個來回 1 像素的滑鼠移動叫醒螢幕。**會真的把那台的螢幕叫醒** |
| `--all` | 每個螢幕都做（預設只做主螢幕） |
| `--seconds N` | 每種方式擷取多久（預設 3） |
| `--out <檔案>` | 另外寫進檔案（UTF-8） |

除了 `--wake` 的叫醒之外不送任何輸入、不存畫面，只數取樣點裡不是黑色的有幾點。這台有原始碼時也可以跑同一份回報的測試：`MYRIAGENT_DESK_IT=1 dotnet test tests/Myriagent.Tests/Myriagent.Tests.csproj -c Release --filter "FullyQualifiedName~回報這台現在擷取得到什麼"`（`MYRIAGENT_DESK_WAKE=1` 等於 `--wake`）。

## 在別台執行

建置輸出的資料夾帶著整份桌面的組件（約 19 MB），整個複製過去就能執行；那台要有 .NET 10 桌面執行階段（裝了 app 的電腦都有）。**要在那台登入的工作階段裡執行**——ssh 的工作階段看不到桌面，擷取一律失敗：

1. 把輸出資料夾壓成 zip 送過去（有 ssh 的電腦用 `scp -O`：Windows 的 OpenSSH 預設殼層是 PowerShell 時新式的 scp 會「Connection closed」），在那台解開。
2. 以 `myriagent-remote run <電腦> -- "C:\路徑\desk-probe.exe"` 執行（那台要開「允許其他電腦的代理在這台執行指令」）：它在那台 app 的工作階段、以一般權限執行，和 app 擷取時的條件一樣。
3. 看完把那台的探針刪掉。

## 怎麼讀

```
工作階段：連線狀態 0、鎖定旗標 1（0 鎖定、1 沒鎖）；登入畫面的程序不在；判斷成鎖定：False
輸入桌面是 Default：True；螢幕：關閉
GDI 直接讀 螢幕 0（1680x1050、主螢幕） → 1280x800：Ok（145 ms）；取樣 10557 點裡不是黑的 837 點
DXGI（不叫醒）螢幕 0 … → 1280x800：553 ms Retry〔DXGI〕 → 1675 ms Retry〔GDI〕 → 1814 ms Ok〔GDI〕；第一張 1814 ms；…
DXGI（叫醒螢幕）螢幕 0 … → 840x525：528 ms Retry〔DXGI〕 → 617 ms Ok〔DXGI〕 → …；第一張 617 ms；…
螢幕：開啟
```

- 「判斷成鎖定」是 app 用的判斷（系統回報鎖定＋登入畫面的程序在）。True 時 DXGI 那幾行一律是 `Locked`：app 不擷取、只告訴看的一方「那台電腦目前鎖定中」。
- 每一步是「經過時間 結果〔走的方式〕」。`Retry〔DXGI〕` 是桌面複製還沒有第一張畫面，`Retry〔GDI〕` 是剛改用 GDI，`Unchanged` 是畫面沒有變化。
- 「螢幕」是這個工作階段的螢幕開關（`GUID_SESSION_DISPLAY_STATUS`）。

## 已知的行為

- 鎖定時輸入桌面的名稱可能照樣是 Default，DXGI 一張畫面都不給，GDI 讀到全黑或鎖定前的畫面（各台不一樣）——所以鎖定要問系統，不能靠輸入桌面的名稱或畫面是不是黑的判斷。
- 螢幕關閉（沒鎖定）時 DXGI 也一張都不給，GDI 讀得到。「螢幕不要關」的要求（`ES_DISPLAY_REQUIRED`）叫不醒已經關閉的螢幕；送滑鼠移動之後 DXGI 才給第一張。

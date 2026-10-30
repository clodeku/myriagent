# myriagent-release：桌面版的發佈工具

app 自我更新（`docs/plans/2026-10-16-app-self-update.md` 第 6 節）的版本計算、組件檢查、打包、簽章、驗證與產生金鑰。開發機的 `tools/release/Publish-DesktopReleaseFromDev.ps1`（2026-10-25 起；之前是 `.github/workflows/desktop-release.yml` 的 build job，已停用）依序呼叫 `version`、`check-assembly`、`package`、`sign`、`verify`；開發機的金鑰檔以 `keygen --dpapi-file` 產生一次（GitHub secret 版本經 `tools/release/New-DesktopReleaseKey.ps1` 呼叫 `keygen --set-secret`）。manifest、zip、簽章與版本的規則直接用 Protocol 的 `ReleaseManifestRules`、`ReleasePackageRules`、`ReleaseIdentity`（伺服器收件與桌面同一份），所以不另寫 PowerShell 版本（U2 設計 D2）。

不進 `Myriagent.slnx`，各自建置：

```bash
dotnet build tools/release-tool -c Release -nodeReuse:false -p:UseSharedCompilation=false
dotnet run --project tools/release-tool -c Release --no-build -- selftest   # 約 2 秒；改了 Protocol 的 Update/ 或這個工具就要跑
tools/release-tool/bin/Release/net10.0/myriagent-release.exe --help
```

## 指令

| 指令 | 做什麼 | GITHUB_OUTPUT |
|---|---|---|
| `version --csproj <路徑> --count <N> --commit <40 碼>` | 讀 csproj 唯一的 `<Version>`（必須是「主.次.0」），算出 version＝主.次.N、build＝提交前 7 碼、release＝version-build（通過 `ReleaseIdentity.TryParseRelease`）。N 只收 1～65534（組件版本每段的上限），commit 必須是 40 個小寫十六進位字 | `version`、`build`、`release` |
| `check-assembly --publish-dir <資料夾> --release <release> --commit <40 碼>` | 發佈資料夾要有 `Myriagent.exe`；以 System.Reflection.Metadata 讀 `Myriagent.dll`（不載入）：組件名稱是 Myriagent、沒有測試金鑰的中繼資料 `MyriagentUpdateTestKeySpki`（鍵不分大小寫）、InformationalVersion 正好是「version+完整雜湊」，而且 `ReleaseIdentity.FromInformationalVersion` 解出同一個非 dev 的 release | — |
| `package --publish-dir <資料夾> --release <release> --commit <40 碼> --out <資料夾> [--created-at <yyyy-MM-ddTHH:mm:ssZ>]` | 打包成 `myriagent-<release>.zip` 與 `.manifest.json`：檔案依相對路徑的序數排序、斜線分隔，zip 項目同順序；manifest 由 `ReleaseManifestRules.Build` 產生（protocolVersion＝`SyncProtocol.Version`、entry＝`Myriagent.exe`、minRuntime 照發佈資料夾的 `Myriagent.runtimeconfig.json` 列的 Microsoft.WindowsDesktop.App 版本——沒有這個檔、self-contained 或沒列桌面框架就拒絕，見 `ReleaseRuntimeRules`），再跑發佈政策與 zip 自我檢查 | `zip_name`、`zip_sha256`、`manifest_name`、`manifest_sha256`、`file_count`、`total_bytes` |
| `sign --manifest <路徑> --out <簽章檔路徑> [--key-file <金鑰檔>]` | 私鑰從 `--key-file`（`keygen --dpapi-file` 寫的 DPAPI 金鑰檔，不讀環境變數）讀，沒給就從環境變數 `DESKTOP_RELEASE_KEY`（PKCS#8 的 base64）讀；金鑰必須是 P-256 而且在 `ReleaseKeys.Production` 裡；簽完以同一份清單自我驗證，寫出 `myriagent-<release>.manifest.sig`（檔名不對就拒絕） | `sig_name`、`sig_sha256`、`kid` |
| `verify --dir <資料夾> --release <release>` | 讀檔前先比大小，以 `ReleaseKeys.Production` 跑 `ReleasePackageRules.VerifyAsync`（撤銷清單一律是空的），再確認 manifest 的 release 就是檔名的 | — |
| `keygen --set-secret <名稱> --repo <owner/repo>` | 產生 P-256；私鑰只寫進 `gh secret set <名稱> --repo <repo>` 的 stdin；標準輸出只有公開的一行 `new ReleaseKey("<kid>", "<spki>")` | — |
| `keygen --dpapi-file <金鑰檔>` | 產生 P-256；私鑰以 DPAPI（這台電腦的這個 Windows 使用者＋固定的 entropy）加密寫進新檔（已經存在就拒絕、不覆蓋）；標準輸出只有公開的那一行 | — |
| `keygen --dry-run` | 同上但不呼叫 gh、不寫檔、私鑰直接丟棄，那一行只供檢查格式 | — |
| `selftest` | 見下 | — |

- 結束碼：0 成功、1 檢查沒過、2 用法錯（缺參數、不認得的指令或參數、值不是數字或格式不對）。
- 有設定 `GITHUB_OUTPUT` 時，輸出值附加成 `key=value`（無 BOM 的 UTF-8、換行 `\n`），同一行也印到標準輸出；值含換行一律拒絕。
- `package`：同名的舊輸出（含舊的 `.manifest.sig`）先刪；失敗不留下半成品；輸出資料夾不能在發佈資料夾裡面；zip 項目的時間都是 createdAt（沒指定就用現在的 UTC 整秒），同樣的輸入與 createdAt 得到同一個 zip 與 manifest。
- manifest 一律以原文位元組傳遞：`package` 產生之後，`sign`、`verify` 只讀不寫，任何一段都不重新序列化。

## 私鑰

- `sign`：從 `--key-file` 或 `DESKTOP_RELEASE_KEY` 讀；解出、解碼後的位元組用完就清掉（環境變數本身是 .NET 字串，清不掉，程序結束就消失）。金鑰檔解不開（別的使用者或電腦產生的、損毀、沒加密的 PKCS#8 原文）一律拒絕。錯誤訊息都是固定的文字，不帶輸入內容。
- `keygen`：PKCS#8 直接編碼成 base64 位元組寫進 gh 的 stdin，不經字串，寫完就清掉；gh 的訊息轉到標準錯誤，標準輸出只有公開的那一行。gh 失敗時結束碼 1、不印公開的那一行，不會有人提交一把私鑰沒存進 secret 的公鑰。
- 一律經 `tools/release/New-DesktopReleaseKey.ps1` 執行（先檢查 `-Repo`、`-SecretName`、gh 有沒有裝，以及 github.com 作用中帳戶的 `gh auth status --hostname github.com --active`；`-DryRun` 只跑 `keygen --dry-run`）。私鑰不經對話。

## 發佈金鑰（U2 設計 D1）

- `sign` 只認 `ReleaseKeys.Production`；伺服器收件也只認它，沒有任何設定或指令能加信任金鑰。Production 是空的時候（產生正式金鑰之前）`sign` 一律失敗。
- 換金鑰：`New-DesktopReleaseKey.ps1 -SecretName DESKTOP_RELEASE_KEY_NEXT` 把新私鑰寫進第二個 secret（secret 只能寫、讀不回來），三步見 `docs/release.md` 5.2。
- 金鑰外洩：先在版本頁撤回外洩的金鑰簽的、不能確定是自己發佈的版本（移除金鑰、重新部署不會讓已經收進來的版本失效）→ 以 `New-DesktopReleaseKey.ps1 -SecretName <簽章那一步現在讀的 secret>` 產生新金鑰、直接覆蓋外洩的那把 → 把新公鑰提交進 `ReleaseKeys.Production`、移除外洩的那一把 → 等 MyriagentHub 部署完成 → 發佈新版 → 舊版桌面不認新金鑰，每台電腦手動重裝（`docs/release.md` 5.3）。

## selftest

在 `%TEMP%\myriagent-release-selftest-<guid>` 以暫時金鑰把每個指令經同一份 `ToolCommands` 跑一遍，逐項印出 `[通過]`、`[失敗]`，最後一行是「selftest：N 項通過、M 項失敗」（目前 85 項）；全部通過就刪掉暫存資料夾，有失敗就保留並印出位置。不碰 repo、不讀 `DESKTOP_RELEASE_KEY`、不寫真正的 GITHUB_OUTPUT、不呼叫真的 gh，金鑰檔只寫在暫存資料夾。涵蓋：

- 命令列：沒有指令、不認得的指令或參數是用法錯；`--help`；GITHUB_OUTPUT 拒絕含換行的值。
- 版本：0.2.0 加 N 的三個輸出；N＝65534 收、65535 與 0 拒絕；commit 有大寫或只有 39 碼拒絕；N 不是數字、少了參數是用法錯；csproj 不是「主.次.0」或有兩個 `<Version>` 拒絕。
- 打包、簽章、驗證成功：輸出值與實際檔案的 SHA-256 相符；序數排序、zip 同順序、項目時間；manifest 沒有 BOM、換行 `\n`；打包兩次逐位元組相同；簽章檔不超過 1024 位元組；任何輸出都不含私鑰；`sign`、`verify` 不改 manifest。
- 竄改各自在正確的步驟擋下：manifest 改一個位元組（signature）、zip 多一項或少一項（zip_hash）、manifest 照實簽了竄改後 zip 的雜湊（zip_entries）、不認得的 kid（signature）。
- `sign` 的守門：金鑰不在正式清單、正式清單是空的、不是 P-256、沒設定或不是 base64（不回顯內容）、protocolVersion 不支援、manifest 超過 4 MiB、簽章檔名不對。`verify`：簽章檔超過 1024 位元組、檔名的 release 與 manifest 不符。
- `package` 的守門：保留檔名 `release.manifest.json`（發佈政策）、沒有 `Myriagent.exe`、輸出資料夾在發佈資料夾裡面、commit 不以 build 開頭、`--created-at` 格式錯。
- `check-assembly`：以 `PersistedAssemblyBuilder` 產生的組件——正向對照（讀得出名稱、InformationalVersion 與測試金鑰）、乾淨的通過、帶測試金鑰拒絕、別的提交（build 相同）、開發版、別的版本、沒有 exe。
- 開發機的金鑰檔：`keygen --dpapi-file` 寫出 DPAPI 加密的檔（不是 PKCS#8 原文）、已經存在就拒絕、不能和 `--set-secret` 一起用；`sign --key-file` 簽成功（不讀環境變數）、金鑰不在正式清單、找不到檔、不是 DPAPI 的資料、沒加密的 PKCS#8 原文都拒絕。
- `keygen`：`--dry-run` 只輸出一行、kid 等於 `ComputeKid(spki)`；參數組合錯是用法錯；以假的 gh（Windows PowerShell 腳本，記下 stdin 與參數）驗證執行的是 `gh secret set DESKTOP_RELEASE_KEY --repo …`、stdin 收到對應印出公鑰的 PKCS#8 base64、輸出不含私鑰、gh 失敗時不印公開的那一行、名稱不合規則時連 gh 都不叫。需要 `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`；其他指令的 gh 指向暫存資料夾裡不存在的檔，碰不到真的 gh。

selftest 只在本機跑（原本 `.github/workflows/ci.yml` 的 `release-tool` job 也跑，ci.yml 2026-10-25 已移除）。

## 檔案位置

- bin、obj 在 `tools/release-tool` 底下；不可搬進 `tools/release`（`Test-Hub 主機Scripts.ps1` 掃那整個資料夾、不分副檔名）。
- Protocol 的 `InternalsVisibleTo` 有 `myriagent-release`（這個工具的組件名稱）。

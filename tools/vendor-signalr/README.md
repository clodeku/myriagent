# vendor-signalr：SignalR JS 用戶端的取檔與檢查

瀏覽器版（`/app/`）的傳輸層用 SignalR 的 JavaScript 用戶端連 `/hubs/v1/browser`（`docs/web-protocol.md` 第六節）。伺服器的 CSP 是 `script-src 'self'`、不用 CDN，所以程式庫**逐位元組**放在 `src/MyriagentHub/wwwroot/lib/`，由伺服器在 `index.html` 的 `app.js` 之前插入（`src/MyriagentHub/Web/AssetServerRules.cs` 的 `SelectInjected`：`lib/` 要正好一支 `signalr-主.次.修.min.js`）。

`fetch-signalr.mjs` 只用 Node 標準庫（`https`、`zlib`、`crypto`、自寫的 ustar 解析），不經 npm、不留 `node_modules`。

```bash
node tools/vendor-signalr/fetch-signalr.mjs            # --check（預設）：不連網，檢查 repo 裡的兩個檔
node tools/vendor-signalr/fetch-signalr.mjs --fetch    # 下載、驗證後寫入（檔案已存在就拒絕；覆寫加 --force），寫完自己再 --check
```

結束碼：0 通過、1 檢查或驗證失敗、2 參數不對。

## 固定的版本與雜湊

| 項目 | 值 |
|---|---|
| 套件 | npm `@microsoft/signalr` **10.0.11**（2026-08-04 發佈；10.x 只有 10.0.0 與 10.0.11） |
| tarball | `https://registry.npmjs.org/@microsoft/signalr/-/signalr-10.0.11.tgz`，integrity `sha512-FulOJ2EEtKvLQcswe/U7v8pzyXyk7Jua5xgWJPPwyQU/2Z9ORvKcVjyO75VvLsem+CJ1ORRexJ+7Bz1FCei2aw==` |
| 程式庫 | tarball 的 `package/dist/browser/signalr.min.js` → `wwwroot/lib/signalr-10.0.11.min.js`：47,668 位元組、SHA-256 `97e9b97e642a72e5a470917147a2bf79f86cad829a5c6786adb10614d248bb95`（0 個 CR、結尾沒有換行，最後一行是 `//# sourceMappingURL=signalr.min.js.map`；UMD，掛 `self.signalR`） |
| 授權全文 | `https://raw.githubusercontent.com/dotnet/aspnetcore/v10.0.11/LICENSE.txt`（MIT）：1,116 位元組、SHA-256 `cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310`。套件本身只有 `license: "MIT"`，沒有 LICENSE 檔 |
| 第三方聲明 | tarball 的 `package/src/third-party-notices.txt`：1,595 位元組、CRLF、SHA-256 `960aca06f3691a21af07472d0d56cdb6a420cf4afa4f5fe283bd8482db2d42e0` |

`wwwroot/lib/signalr-10.0.11.LICENSE.txt`＝檔頭（兩個來源網址與原檔的 SHA-256）＋`LICENSE.txt` 原文＋third-party-notices（換行正規化成 LF；repo 的文字檔一律 LF）。`--check` 把兩段拆出來各自比對來源雜湊（third-party-notices 換回 CRLF 再比），並確認重組後和檔案逐位元組相同。

### 為什麼是 10.0.11

伺服器用的 SignalR 是 10.0.13（`src/MyriagentHub/MyriagentHub.csproj`），但 npm 沒有發 10.0.13 的 JS 用戶端；JSON 協定 v1 跨修訂版相容，用 npm 上 10.x 最新的 10.0.11（WP3.6 計畫 Q8）。

### 程式庫裡要知道的地方

- 唯一的 `new Function` 在取全域物件的備援分支（`if("object"==typeof globalThis)return globalThis;try{return this||new Function("return this")()}…`），瀏覽器都有 `globalThis`，跑不到；就算跑到，CSP 沒有 `unsafe-eval` 也只會被擋。
- 有 4 個網址字串（learn.microsoft.com、aka.ms 的錯誤說明、字面的 `"https://"`、`"http://"`），不是載入外站資源。
- `CspTests` 的 JS 掃描（`eval(`、`new Function`、外站網址…）因此略過 `wwwroot/lib/`，改由三個測試把關：`wwwroot_lib的JS都在固定SHA256清單`、`SignalR程式庫唯一的new_Function在globalThis備援分支`、`SignalR程式庫沒有eval與HTML注入`。
- 結尾的 `sourceMappingURL` 在開著 DevTools 時會請求 `.map`（404）；不改檔（雜湊固定），`lib/` 也不放 `.map`。

## `--check` 檢查什麼

1. `lib/` 只有程式庫與授權檔兩個檔：正好一支 `signalr-*.min.js`、沒有 `.map`、沒有未壓縮的 `signalr-*.js`、沒有其他檔。
2. 程式庫的大小與 SHA-256 和固定值一致。
3. 授權檔是 LF、檔頭與分段標記和工具產生的相同、兩段的來源雜湊一致、重組後逐位元組相同。

## 換版程序

1. 到 npm registry 查新版的 `dist.integrity`、`dist.tarball`，下載後算 `package/dist/browser/signalr.min.js` 與 `package/src/third-party-notices.txt` 的大小與 SHA-256；aspnetcore 對應標籤的 `LICENSE.txt` 也一樣。新版的 `signalr.min.js` 要重新確認：沒有 `eval(`、`innerHTML`、`outerHTML`、`insertAdjacentHTML`、`document.write`，`new Function` 仍然只有 globalThis 備援那一處。
2. 改 `fetch-signalr.mjs` 的 `PIN`（版本、網址、雜湊、大小、檔名）。
3. 刪掉 `wwwroot/lib/` 的舊檔（`lib/` 只能有一支程式庫，兩支會讓 `/app/*` 回 503），再 `--fetch`。
4. 同一個 commit 改 `tests/MyriagentHub.Tests/Security/CspTests.cs` 的 `LibraryHashes`（只留新版）；舊檔殘留在測試輸出資料夾時重新建置前先清掉 `bin`。
5. 驗證：
   ```bash
   node tools/vendor-signalr/fetch-signalr.mjs --check
   dotnet build Myriagent.slnx -c Release -nodeReuse:false -p:UseSharedCompilation=false
   dotnet test tests/MyriagentHub.Tests/MyriagentHub.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CspTests|FullyQualifiedName~AssetServer|FullyQualifiedName~TransportHubFileTests"
   (cd tools/ui-probe && node remote-probe.mjs)                  # 假 hub（頁內 routeWebSocket）＋wwwroot 裡真的程式庫與 transport-hub.js
   dotnet build tools/fake-device -c Release -nodeReuse:false -p:UseSharedCompilation=false
   (cd tools/hub-e2e && node gateway.mjs && node pairing.mjs)    # 真的 MyriagentHub（先建置 src/MyriagentHub 的 Release）
   ```
   `AssetServerTests` 的 `repo的wwwroot注入組合合法` 一定要過：組合錯誤只會讓 `/app/` 503，部署的健康閘門只看 `/healthz`，不會回滾。remote-probe 的 `harnessInjectionOrder` 從 `lib/` 實際的檔名組插入順序，換版不必改探針；gateway 從 `/app/` 的 HTML 驗插入的檔名與順序。

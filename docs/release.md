# 發佈桌面版與自動更新

配對到 MyriagentHub 的電腦會自動更新：你把新版送進 Hub，Hub 通知所有在線的電腦，各台在背景下載、驗證，等你沒在使用時換版。這份文件說明怎麼設定自己的簽章金鑰，以及怎麼發佈一版。

## 運作方式

```
建置的電腦                         Hub 主機                          每台電腦
New-DesktopRelease.ps1  ──三個檔──▶  admin-inbox\app-release\  ──通知──▶  下載 zip（只給配對過的電腦）
  打包＋以你的金鑰簽章              驗簽章、逐檔雜湊、版本            驗簽章與逐檔雜湊
                                    收進 app-releases\                沒在使用時讓對話停在安全點
                                                                      換版、健康檢查、失敗自動退回
```

- 一版有三個檔：`myriagent-<版本>.zip`、`.manifest.json`（每個檔的大小與 SHA-256）、`.manifest.sig`（manifest 的 ECDSA P-256 簽章）。
- 桌面與 Hub **只信任編譯進程式的公鑰**（`src/Myriagent.Protocol/Update/ReleaseKeys.cs` 的 `ReleaseKeys.Production`），沒有任何設定或指令可以另外加入信任的金鑰。即使 Hub 主機被入侵，也不能推送沒有你簽章的版本。
- 公開版的 `ReleaseKeys.Production` 是空的：在你加入自己的金鑰之前，Hub 不收任何版本，桌面也不會自動更新。
- 版本號是 `src/Myriagent/Myriagent.csproj` 的「主.次」加上 git 的提交數（例如 `0.2.825`），所以每個新提交的版本都比較大。版本的全名另外帶提交的前 7 碼，例如 `0.2.100-1a2b3c4`。

## 一次性設定：建立你的簽章金鑰

1. 在**以後要用來發佈的那台電腦**，以平常的 Windows 帳號（不要用系統管理員）建立金鑰：

   ```powershell
   dotnet run --project tools/release-tool -c Release -- keygen --dpapi-file "$env:LOCALAPPDATA\MyriagentRelease\desktop-release-key.dpapi"
   ```

   私鑰以 DPAPI 加密寫進這個檔，只有這台電腦的這個 Windows 帳號解得開；畫面上不會印出私鑰。最後一行是公開的公鑰，例如：

   ```
   new ReleaseKey("0123456789abcdef", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...")
   ```

2. 把這一行加進 `src/Myriagent.Protocol/Update/ReleaseKeys.cs` 的 `Production`，提交到你自己的 repo（fork）：

   ```csharp
   public static IReadOnlyList<ReleaseKey> Production { get; } =
   [
       new ReleaseKey("0123456789abcdef", "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE..."),
   ];
   ```

3. 以這個提交更新 Hub（[hub-setup.md](hub-setup.md) 第 7 節），Hub 才認得你的金鑰。
4. 每台電腦**手動安裝一次**含有這把公鑰的版本（[install.md](install.md) 第 4 節，或照下一節建出 zip 之後，在那台解壓並執行「安裝到這台電腦」），並且配對到 Hub。之後的版本就會自動更新。

## 發佈一版

確認要發佈的提交已經建置、測試都通過（`dotnet test Myriagent.slnx -c Release`），然後在有金鑰檔的那台電腦執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\New-DesktopRelease.ps1
```

腳本依序計算版本號、發佈桌面 app、確認沒有內嵌測試金鑰、打包、以你的金鑰簽章，再以 `ReleaseKeys.Production` 驗證一次。三個檔放在 `out\desktop-release\`。

**送進 Hub：**

- 建置的電腦就是 Hub 主機時，以系統管理員加上 `-Submit`，一次完成：

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\New-DesktopRelease.ps1 -Submit
  ```

- 不是同一台時，把 `out\desktop-release\` 複製到 Hub 主機，以系統管理員執行腳本最後印出的那一行：

  ```powershell
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\release\Publish-DesktopRelease.ps1 -ArtifactDir <資料夾> -Release <版本> -ZipSha256 <…> -ManifestSha256 <…> -SigSha256 <…>
  ```

`Publish-DesktopRelease.ps1` 把三個檔放進 `C:\ProgramData\MyriagentHub\admin-inbox\app-release\`，然後等服務的回執。結束碼 0 表示收下了（`ingested`；同一版重送是 `already_ingested`）。收不下時，原因代碼寫在畫面上，詳細原因寫在 Hub 的服務記錄。常見的原因有：簽章不是內建的金鑰、版本沒有比已收的新、檔案雜湊不符。

## 之後會發生什麼事

- Hub 收下之後，通知所有在線的電腦。離線的電腦下次連線時才看到。
- 每台電腦在背景下載、驗證，等你沒在使用時才換版：桌面沒人操作，也沒有從手機或網頁在用。執行中的對話會停在安全點（工具跑完，或正在等你回應）；背景工作還在跑時，會等它們結束。
- 新版第一次啟動要通過健康檢查才算數；失敗或之後連續閃退，會自動退回前一版。
- 換版後，停下的對話、排隊中的訊息、輸入框的草稿與彈出視窗都會恢復。
- 「設定 → 版本與更新」可以看狀態，也可以按「立即重啟」或「取消」。不想自動更新的電腦，可以在這裡關掉「自動更新 Clode Myriagent」。

## 版本頁與撤回

在 Hub 的網頁打開 `https://<你的網域>/#releases`，可以看到：

- 收進來的各版本與目前提供給電腦的版本；
- 每台電腦的 app 版本與更新狀態；
- 下載 zip（新電腦第一次安裝時用）；
- 撤回一版。

也可以在 Hub 主機執行：

```powershell
C:\Services\MyriagentHub\current\MyriagentHub.exe admin app-releases
C:\Services\MyriagentHub\current\MyriagentHub.exe admin app-devices
C:\Services\MyriagentHub\current\MyriagentHub.exe admin app-release-withdraw 0.2.100-1a2b3c4
```

撤回目前提供的版本時，電腦會改成下載前一個可用的版本。撤回不能復原，要修正請發佈一個更新的版本。

## 更換金鑰

桌面只信任自己內建的公鑰，所以換金鑰要分兩步：

1. 建立新金鑰，把新公鑰**加進** `ReleaseKeys.Production`（保留舊的），以**舊金鑰**簽章發佈一版（過渡版）。
2. 用 `admin app-devices` 確認每台電腦都更新到過渡版之後，往後的版本改用新金鑰簽章（`New-DesktopRelease.ps1 -KeyFile <新金鑰檔>`）。之後可以從清單移除舊公鑰。

**金鑰檔遺失或外洩**：遺失時不能再用它簽章；外洩時任何人都可能簽出你的電腦會接受的版本，請立刻處理：

1. 建立新金鑰，並以**只含新公鑰**的 `ReleaseKeys.Production` 更新 Hub（Hub 就不再收舊金鑰簽的版本）。
2. 每台電腦手動重新安裝含新公鑰的版本。

金鑰檔沒辦法複製到別台電腦使用（DPAPI 綁定這台電腦與這個帳號）。這是刻意的設計：只有這台電腦能發佈。

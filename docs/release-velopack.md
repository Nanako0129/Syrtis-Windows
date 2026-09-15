# Syrtis Velopack Release Contract

當前的發佈合約，涵蓋 `v0.2.2` 起的所有 Velopack 安裝版發佈。

[`docs/release.md`](release.md) 是 `v0.1.0` 無簽章可攜版的**已封存紀錄**，不再更新；
它預告的「Phase 11 自己的 release contract」就是本文件。

## 核心原則

> **CI 綠燈不是驗收。** Hosted CI 能證明結構、版本、PE architecture 與 hash，
> 不能證明安裝包裝得起來、app 打得開、資料出得來。使用者可見的功能，驗收條件
> 是**在乾淨機器上啟動並看到它運作**。

這條原則不是慣例，是 2026-08-07 花了一整天換來的。當時九個 CI 綠燈、四輪程式碼
審查、兩個獨立審查者，全部沒有發現 `v0.2.1` 在乾淨 Windows 上**裝得起來、開得起來、
永遠不顯示任何資料**——因為每一台跑過測試的機器都被污染了：x64 測試機裝了
Visual Studio 工具鏈，GitHub 的 `windows-latest` runner 預裝 VC++ Redistributable，
`tb_core_ffi.dll` 依賴的 `vcruntime140.dll` 一直都在。

該缺陷在乾淨主機以外的地方**結構上不可見**。

## 發佈通道

四條，永久並存。安裝之後通道就烙在該安裝上，彼此不可互相更新。

| Channel | 模式 | .NET runtime | 用途 |
|---|---|---|---|
| `win-x64` | Full | 內含 | x64 預設 |
| `win-x64-lite` | Lite | 安裝時取得 | 已有 .NET 10 的機器 |
| `win-arm64` | Full | 內含 | ARM64 預設 |
| `win-arm64-lite` | Lite | 安裝時取得 | 同上 |

> Full 與 Lite 的安裝**不能互相更新**——Lite 的更新包會移除 `coreclr.dll`，
> 反之亦然。切換通道等於移除重裝。`UpdateFlow.AcceptedChannels` 只認這四個
> 字串，其餘一律 fail closed。

Lite 在乾淨機器上**不是節省**：47 MB 的下載加上它取得的 runtime，總量接近
Full 的 79 MB。它對已有 .NET 10 的機器才划算。分流政策見
[`docs/lite-distribution.md`](lite-distribution.md)。

## 如何發一版

```
1. 版本契約推進（三處，見下）並合併到 main
2. git tag -a vX.Y.Z <commit> && git push origin vX.Y.Z
3. Release workflow 自動執行 → 產出草稿
4. 從草稿下載安裝包，在乾淨 VM 上驗證
5. gh release edit vX.Y.Z --draft=false --latest
6. scripts/update-scoop-manifest.sh vX.Y.Z  → 提交 bucket/syrtis.json
```

第 6 步不能提前。Scoop bucket 記的是 `.nupkg` 的雜湊，而那些檔案要等封裝作業跑完
才存在，所以它排在發佈之後，不在 release.yml 發佈前的版本契約閘門裡。

**漏掉它的後果是無聲的**：`bucket/syrtis.json` 裡的 `checkver`／`autoupdate` 是給維護者
的 `scoop checkver <app> -u` 用的，client 端的 `scoop update syrtis` 只會重讀 bucket、
看到 bucket 寫的版本。沒更新 bucket，Scoop 使用者就永遠停在上一次提交的版本，而且
不會有任何錯誤訊息。

驗證用 `scripts/update-scoop-manifest.sh --check vX.Y.Z`，檔案過時就以非零結束。

**Tag 是建置的授權，不是發佈的授權。** workflow 一律產出草稿；是否對外由人決定。

重跑（例如上傳中斷）用 `workflow_dispatch`：

```bash
gh workflow run release.yml --ref main -f tag=vX.Y.Z
```

Tag 指向的 commit 若帶著舊版 workflow，重推 tag 只會重現同樣的失敗；dispatch 走
`main` 的 workflow 檔，這正是那個輸入存在的理由。

## 版本契約

三處，任一不同步就建置失敗（`build-app-artifact.ps1:86` 斷言 props 與腳本常數一致）。

| 檔案 | 欄位 |
|---|---|
| `Directory.Build.props` | `TbSemanticVersion`、`TbAssemblyVersion` |
| `src/TokenBar.App/app.manifest` | `assemblyIdentity version` |
| `scripts/build-app-artifact.ps1` | `$ExpectedSemanticVersion`、`$ExpectedAssemblyVersion` |

Workflow 另外斷言 **tag 名稱等於 `TbSemanticVersion`**。在還寫著 0.2.2 的樹上打
`v0.3.0` 會被擋下，否則第一個症狀會是已安裝的客戶端拒絕更新。

## 產物

每條通道四個，共 16 個，外加 `SHA256SUMS.txt`。

| 檔案 | 用途 |
|---|---|
| `Nyanako.Syrtis-<ver>-<ch>-full.nupkg` | 更新器下載的套件 |
| `Nyanako.Syrtis-<ch>-Setup.exe` | 使用者下載的安裝器 |
| `releases.<ch>.json` | 客戶端讀的 feed，含 SHA256 |
| `RELEASES-<ch>` | 舊 Squirrel 格式 |

`vpk` 另外產出兩種**刻意不發佈**的檔案：

| 檔案 | 為什麼不發 |
|---|---|
| `<ch>-Portable.zip` | 安裝器是支援形式；兩個都發會導致裝這個更新那個 |
| `assets.<ch>.json` | 本機建置清單，`UpdateFlow` 不讀它 |

> Workflow **列舉**要發的檔名而非計數。第一次執行就是因為計數而失敗——那個
> 「16」是數 `v0.2.1` 發佈了什麼推出來的，把前一個人的挑選當成了工具的契約。

**符號封存**（`Syrtis-Symbols-<ver>-<rid>-<mode>.zip`）以 workflow artifact 保存
90 天，**不是** release 資產——它帶著 PDB，屬於診斷崩潰的人，不屬於公開下載頁。
沒有它，出貨的二進位就沒有可對應的符號。

## v0.2.2 的驗證紀錄

| 項目 | x64 | ARM64 |
|---|---|---|
| 乾淨主機（無 .NET、無 VC++ redist） | ✅ Hyper-V VM，已快照 | ✅ Fusion VM，手動移除 redist |
| 安裝 exit 0 | ✅ | ✅ |
| 版本身分（FileVersion / ARP） | ✅ 0.2.2 / Syrtis | ✅ |
| 原生 DLL 在無 redist 下載入 | ✅ | ✅ `0xAA64` 確認原生非模擬 |
| Lite runtime bootstrap | ✅ 觀察到安裝 .NET 10.0.10 | 未測 |
| 托盤與儀表板（互動式） | ✅ | ✅ |
| 解除安裝零殘留 | 未測 | ✅ |
| 從 GitHub 下載的產物雜湊 | ✅ 與 `SHA256SUMS.txt` 一致 | 未測 |

> **未驗證：** 由 CI 建置、從草稿下載的那一份 x64 產物，自動檢查全過
> （雜湊、安裝、版本、原生載入），但**互動式啟動未觀察**。同一份原始碼的本機
> 建置先前在兩個架構上都確認正常，兩者只差六個純 CI 設定的 commit。

## 已知限制

**packId 已凍結為 `Nyanako.Syrtis`。** 它決定安裝目錄、產物檔名，以及已安裝的
客戶端會接受哪個套件。改它會讓每一個既有安裝**靜默**停止更新——客戶端用
`StringComparison.Ordinal` 比對，回報「沒有可用更新」而不是失敗。

**0.2.0 / 0.2.1 無法更新到 0.2.2。** packId 在 `864aa03` 從 `Nyanako.TokenBar`
改過來，所以那兩版的安裝需要一次手動移除重裝。這是一次性的邊界，0.2.2 之後正常。

**未簽章。** SmartScreen 會在首次執行時警告。簽章方向見專案計劃。

**ARM64 實戰覆蓋遠少於 x64。** 一台 VM、一輪驗證。

## 非目標

winget / Scoop 上架（Phase 12，且 Scoop 交付形式未拍板）、程式碼簽章、
delta update 調校、`v0.1.0` 可攜版的任何相容性承諾。

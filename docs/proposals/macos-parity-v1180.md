# macOS parity 落差調查（對照 v1.18.0）

> **這份文件會過期。** 它記錄兩個特定 commit 之間的差集，不是「現況」。
> 使用前先跑下面的查證指令；若 macOS 已前進，重跑調查而不是相信這裡。
> 取代 [`macos-parity-v1143.md`](macos-parity-v1143.md)——那份的對照基準落後
> 240 個 commit、四個小版本，兩個方向都已失真。

| 項目 | 值 |
|---|---|
| macOS 對照基準 | `52fdd53a`（`origin/main` ＝ v1.18.0） |
| Windows 基準 | `59ed3d4`（`main`，v0.3.0 發佈後） |
| 引擎 pin | `d6512f5ae62c2be6751ed93adb9391ffe3f91579` |
| 調查日期 | 2026-09-17 |

## 最關鍵的發現：引擎已完全對齊

**兩邊 pin 的是同一個 commit。**

> 下面每個讀 macOS 的指令都用 `$NATIVE` 指向
> [`Nanako0129/TokenBar`](https://github.com/Nanako0129/TokenBar) 的一份 clone。
> 沒有的話現開一份就好，這些指令全是唯讀的：
>
> ```bash
> NATIVE=$(mktemp -d)/TokenBar
> git clone --filter=blob:none https://github.com/Nanako0129/TokenBar.git "$NATIVE"
> ```

```bash
git -C "$NATIVE" ls-tree v1.18.0 vendor/tokscale-core
#   → 160000 commit d6512f5ae62c…  vendor/tokscale-core
git submodule status vendor/tokscale-core
#   →  d6512f5ae62c… vendor/tokscale-core
```

所以 v1.17 的四項計價修正、v1.18 的 Codex turn 計數修正（`item_completed`
攜帶 `UserMessage`，parser_version 5→6）**都已經在 Windows 出貨的引擎裡**：

```
vendor/tokscale-core/src/sessions/codex.rs:534
vendor/tokscale-core/src/message_cache.rs:848
```

引擎層零落差。剩下的全部住在 UI 層與 `crates/tb_core_ffi`（Windows 自有）。

## 舊文件列的落差，現在已經關掉的

| 舊切片 | 現況 |
|---|---|
| 3 Quota 資料出口 | **已完成** — `include/ctb.h` 匯出 15 個函式，含 `tb_quota_history`、`tb_window_usage` |
| 4 Quota lens 三張卡 | **已完成** — `src/TokenBar.App/DashboardView.Quota.cs`（strip card ＋ heatmap） |
| 用量歸因整頁 | **已完成** — `UsageAttributionPage`，掛在 `SettingsWindow.cs:74` |
| Check now | **已完成**（舊文件已記） |
| 兩個新客戶端 | **已完成**（舊文件已記） |

v1.17 的 Codex Spark 時間窗消歧（#286）也已移植：
`src/TokenBar.Core/AgentUsageQualifier.cs`。

## 落差清單（全部親自查證過）

### A. Quota provider —— 住在 `crates/tb_core_ffi`，Windows 自有

目前有五個 provider（`crates/tb_core_ffi/src/lib.rs:636`）：
codex / claude / antigravity / copilot / grok。v1.18 新增的三個都缺：

| 能力 | macOS PR | Windows 現況 |
|---|---|---|
| OpenCode Go 方案額度卡 | #301 | 缺。`opencode_integrations.rs` 只處理 opencode 內的 GitHub Copilot 憑證 |
| Kiro 月額度卡 | #305 | 缺。Kiro 只作為 attribution 客戶端存在，沒有額度抓取 |
| Grok Bot 週額度 | #316 | 缺。`agent_grok.rs` 只打 `cli-chat-proxy.grok.com`（Grok Build） |
| Grok Bot 憑證同意流程 | #338 | 缺，且**不能照抄**——macOS 是 Keychain 授權對話框，Windows 要用 DPAPI／Credential Manager。`agent_storage_windows.rs` 是現成的先例 |

> Grok Bot 那條要讀使用者憑證並送往 `api2.cursor.sh`，觸及 pilotfish 的
> security 風險門：pre-approval 走唯讀 `security-reviewer`，實作走
> `security-executor`。

### B. 選單列／托盤

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 可設定的額度文字顏色 | v1.15 #265：三段閾值（>25%、10–25%、≤10%）、16 個預設色、`#RRGGBB` 欄位；切回 Automatic 保留三色 | 缺（`SettingsWindow.cs` 零相關字串） |
| 每個色票各自的編輯器 | v1.16 #268 | 依賴上一項；macOS 的 popover 錨點 bug 在 WinUI 不會原樣出現，移植時重新判斷 |

### C. 本地化

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 簡體中文 | v1.16 #268，488 個 key | 缺。只有 `src/TokenBar.App/Assets/strings-zh-Hant.json` |
| 執行期組出的字串進翻譯表 | v1.16：時間窗標題、分頁行、token 總計、連續天數、載入錯誤 | 未核對。Windows 的 zh-Hant 是否有同樣的漏網字串要另外查 |

### D. Models lens

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 不合理成本警示 | v1.15 #264：閾值 50 倍，警示旁 tooltip 說明是本地估算的幾倍；不修正數字 | 缺 |
| Models 清單共用 hover tooltip | v1.15 #264 | 未核對。`HoverTip.cs` 存在且 Quota/3D 有用，Models 清單有沒有要另查 |

### E. Quota lens 卡片

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 時間窗歷史超過 12 列 | v1.18 #334：`Show N more`，每次 +12，上限 32（引擎 fold 的盡頭） | 缺。無分頁機制 |
| 「從未記錄」旗標依訂閱判定 | v1.18 #322：把「本機沒記錄」與「有記錄但未分類」拆開，原本三處各自算錯 | **未判定**。`DashboardView.Quota.cs:56` 有相關語義的註解，但 Windows 是否踩同一個坑要開檔核對三個計算點 |
| 長模型名撐破卡片 | v1.18 #336：改成依所在列量測，讓單行截斷發揮作用 | 未核對（WinUI 版面模型不同，可能不適用） |

### F. 多帳號與掃描根目錄

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 第二個 Claude 帳號分帳 | v1.15 #261：每個帳號對自己註冊的 root 各自掃描 | 缺 |
| 自訂掃描根目錄 `CLAUDE_CONFIG_DIR` | v1.14.x | 缺（全 repo 零命中） |

> 這兩項碰認證與路徑，走 security 風險門。

### G. 舊文件就有、至今仍缺

| 能力 | Windows 現況 |
|---|---|
| Discord Rich Presence | `grep -rni discord src/ --include=*.cs --include=*.xaml` → 0 |
| Beta 更新通道 | `src/TokenBar.App/UpdateFlow.cs:120` 寫死 `prerelease: false` |
| Individual tray items | **明列非目標**，不計入落差 |

### H. 引擎等價但與 UI 相關的 v1.15 修正

v1.15 #260（等價行的除數改成「讀數實際走過的距離」）與 admission 門檻、
rising runs 的處理：macOS 在 `TokenBarCore/WindowEquivalence.swift`，
Windows 在 `src/TokenBar.Core/WindowEquivalence.cs`、`QuotaEquivalenceFold.cs`。
**未逐行對照**——Windows 這兩個檔是在 v1.15 之後寫的，可能已含正確語義，
也可能是獨立實作。動這條之前先對讀兩邊。

## 切片順序建議

| # | 切片 | 理由 |
|---|---|---|
| 1 | Beta 更新通道 | 最小；一個布林加一個設定，UpdateFlow 已有完整結構 |
| 2 | 不合理成本警示（D） | 純 UI ＋ 一個閾值，無新資料來源 |
| 3 | 時間窗歷史分頁（E） | 純 UI；資料已在 `tb_quota_history`（引擎 fold 上限 32） |
| 4 | 選單列文字顏色（B） | 純設定 ＋ 托盤繪製，無網路無憑證 |
| 5 | 三個新 quota provider（A，不含 Grok Bot 憑證） | 要寫 Rust fetcher；OpenCode Go 與 Kiro 不碰系統憑證庫 |
| 6 | 簡體中文（C） | 488 個 key，量大但機械；需要審過的譯文而非字元轉換 |
| 7 | 多帳號／自訂掃描根目錄（F） | security 風險門 |
| 8 | Grok Bot 憑證同意（A 尾項） | security 風險門；Windows 的同意 UX 要重新設計 |
| 9 | Discord RPC（G） | 最大；對外發布使用者資料，security 風險門 |

先做的四片都是純 UI 或純設定，不碰 FFI、不碰憑證、不碰引擎 pin。

## 待核對（本次沒查、不要當成「沒落差」）

- E 的「從未記錄」旗標語義（#322）——要開三個計算點對讀
- C 的執行期字串是否也在 Windows 漏掉翻譯
- D 的 Models 清單 hover tooltip
- H 的 `WindowEquivalence` 兩邊逐行對照
- v1.15–v1.18 的 240 個 commit 中，只讀了 release notes 與 commit subject；
  沒有進 release notes 的內部重構不在本清單內

## 查證指令

```bash
# 基準是否還成立（$NATIVE 見上面「最關鍵的發現」一節的 clone 指令）
git -C "$NATIVE" fetch origin
git -C "$NATIVE" log --oneline -1 origin/main   # 應為 52fdd53a
git -C "$NATIVE" rev-list --count 5b894b63..origin/main

# 引擎是否仍對齊
git -C "$NATIVE" ls-tree origin/main vendor/tokscale-core
git submodule status vendor/tokscale-core

# 落差
grep -rni "discord" src/ --include=*.cs --include=*.xaml     # → 0
grep -n "prerelease" src/TokenBar.App/UpdateFlow.cs          # → :120 prerelease: false
grep -rni "CLAUDE_CONFIG_DIR" src/                           # → 0
ls src/TokenBar.App/Assets/strings-*.json                    # → 只有 zh-Hant
grep -n "codex/claude/antigravity/copilot/grok" crates/tb_core_ffi/src/lib.rs
```

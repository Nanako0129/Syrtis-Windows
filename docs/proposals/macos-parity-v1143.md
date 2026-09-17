# macOS parity 落差調查（對照 v1.14.3）

> **已作廢（2026-09-17）。** macOS 自本文的對照基準 `5b894b63` 起前進了 240 個
> commit、四個小版本，到 v1.18.0；Windows 這側切片 3、4 與用量歸因整頁也都已完成。
> 兩個方向都已失真。現行清單見
> [`macos-parity-v1180.md`](macos-parity-v1180.md)。本文保留，因為它記錄的是
> 當時的調查方法與幾個仍然成立的結論（Rust 側已有一半、`tb_core_ffi` 的
> ownership、托盤已對等）。

> **這份文件會過期。** 它記錄的是兩個特定 commit 之間的差集，不是「現況」。
> 使用前先確認下面的對照基準是否仍然成立；若 macOS 已前進，重跑調查而不是相信這裡。

| 項目 | 值 |
|---|---|
| macOS 對照基準 | `5b894b63`（`origin/main`，v1.14.3） |
| Windows 基準 | `240663d`（`main`，S4 i18n 完成後） |
| 引擎 pin | `6a9de8ca5b35ed96e5c56e219e9b95a98633372d`，`ENGINE.md` 標為對齊 `v1.12.0 → v1.13.1` |
| 調查日期 | 2026-08-27 |

## 這份調查怎麼做的（以及上一份為什麼作廢）

上一次調查得到的結論是「macOS 最近三個月沒有新功能」。那是錯的：本地
`~/side-project/TokenBar-Native` 的 clone **落後 153 個 commit、四個小版本**，
而調查從未 `fetch`。看到的最新提交是 v1.13.3 的 release notes，所以「全是 chore」
是發版前快照的假象。

這次的做法：

```bash
cd ~/side-project/TokenBar-Native && git fetch origin
git archive origin/main Sources | tar -x -C <scratch>/macos-v1143
```

用 `git archive` 取快照而非 `pull`，不動使用者的工作目錄。五個平行 scout 各自
分到**互斥**的檔案範圍，brief 裡明文禁止讀那棵過期的 checkout。

> **五份報告有三份含實質錯誤**，由主 session 逐一核對修正：一份宣稱 Windows 已有
> quota curve 的 FFI binding（C# 端零 binding）；一份宣稱 Windows 完全沒有 quota
> history（Rust 端早就有——該 brief 只指了 C# 檔案，是 brief 的疏漏）；一份把
> macOS 的托盤動畫樣式數成 2 種（實際 5 種，與 Windows 同）。委派可以擴大覆蓋面，
> 不能取代對決策關鍵事實的親自查證。

## 最關鍵的發現：Rust 那側已經有一半

`crates/tb_core_ffi/src/agent_quota_history.rs` 存在且是活的，6690 行。
`agent_usage.rs` 正在呼叫它：

| 用途 | 呼叫點 |
|---|---|
| 記錄觀測並評估 | `agent_usage.rs:3378` `record_observations_and_evaluate` |
| 舊格式遷移 | `agent_usage.rs:1600` `migrate_codex_v2` |
| 歷史檔案 | `agent_quota_history::HISTORY_FILE_NAME`、`MAX_SERIES` |

那就是 Windows 現行「歷史配速」模式的資料來源。**缺的是把序列讀回來的出口**：
`include/ctb.h` 匯出 13 個函式，沒有一個讀 quota 曲線；`tb_agent_usage()` 回傳的是
評估後的投影（`HistoricalPace`），不是序列本身。

`ENGINE.md` 的 ownership 表寫明 `crates/tb_core_ffi` 屬於 TokenBar for Windows，
因此**新增匯出不必經過 `tokscale-core` 的審查流程**。

> 這改變了 Quota lens 的成本估算：是「新增 FFI 匯出 → C# binding → UI」，
> 不是「從零建一套持久化」。

## 落差清單

### v1.14.x 新增（Windows 全缺）

| 能力 | 使用者看到什麼 | macOS |
|---|---|---|
| **Quota lens** | 第八個 lens，排在 Overview 之後第二位；可隱藏 | `DashboardModel.swift:8` |
| 單客戶端歷史卡 | 近 12 個週期，每列額度長條＋模型分段花費長條，可展開 | `Views/QuotaHistoryCard.swift` |
| 全 agent 條帶卡 | 每個時間窗一條，週期由舊到新；峰值％、「從未耗盡」 | `Views/QuotaHistoryStripCard.swift` |
| 熱圖卡 | 7×24（星期×小時），依消耗％漸層；下拉選時間窗 | `Views/QuotaHeatmapCard.swift` |
| Overview 摘要行 | 一句話：哪個訂閱最吃緊＋重置倒數＋今日總計 | `Views/QuotaSummaryLine.swift` |
| In-window 用量卡 | 額度曲線疊本地用量長條；兩階段載入 | `Views/WindowUsageCard.swift` |
| 訂閱等價 | 「10% 額度 ≈ N tokens · $X，±Z%」，每次現算 | `TokenBarCore/WindowEquivalence.swift` |
| 訂閱趨勢卡 | 每日花費依宣告的訂閱堆疊 | `Views/SubscriptionTrendCard.swift` |
| 趨勢指標 | 每列方向箭頭，取時間窗尾端 25% 算斜率 | `TokenBarCore/QuotaTrend.swift` |
| 多帳號 | 第二個 Claude 帳號；`accountKey` 進入 window key | `TokenBarCore/AccountIdentity.swift` |
| 自訂掃描根目錄 | 使用者可加 `CLAUDE_CONFIG_DIR` | `ClaudeExtraRoots.swift` |
| 可隱藏 Overview 卡 | `tokenbar.overview.hidden`；**與 lens 隱藏是兩套機制** | `TokenBarCore/OverviewCard.swift` |
| 模型範圍時間窗 | 範圍窗只計該模型 | `TokenBarCore/ModelScope.swift` |

### 既有落差（v1.14 之前就缺）

| 能力 | Windows 現況 |
|---|---|
| Discord Rich Presence | 原始碼零實作；macOS 1674 行三個檔 |
| 用量歸因整頁 | 零實作；macOS 是設定的第五頁 |
| 「立即檢查更新」按鈕 | 設定裡沒有任何更新入口 |
| Beta 更新通道 | `UpdateFlow.cs:43` 寫死 `prerelease: false` |
| Individual tray items | **明列非目標**，不計入落差 |

> **落差的查證指令**（範圍要寫清楚，否則讀者重跑得到不同結果）：
>
> ```bash
> # Discord：限定原始碼，不含 docs——docs/proposals/settings-sidebar.md
> # 本來就有四處提到它（第 7、104、145、150 行），全 repo 搜尋不會是零。
> grep -rni "discord" src/ --include=*.cs --include=*.xaml   # → 0
>
> # 更新入口
> grep -n "prerelease" src/TokenBar.App/UpdateFlow.cs        # → :43 prerelease: false
>
> # 客戶端差集
> # macOS Sources/TokenBarCore/ClientRegistry.swift 34 筆 vs
> # src/TokenBar.Core/ClientRegistry.cs 32 筆
> ```

### 客戶端註冊表

macOS 34 筆、Windows 32 筆，差集正好兩筆：

```swift
"junie": ("Junie", "#6b7280"),
"opencodereview": ("OpenCodeReview", "#6b7280"),
```

反向差集為空（Windows 沒有 macOS 缺的客戶端）。

### Windows 獨有

無。先前一度以為 3D 貢獻圖是 Windows 獨有，錯了——macOS 有
`Charts/ContributionGraph3D.swift`，兩邊都有 2D 熱圖與 3D 兩種呈現。

### 托盤

完全對等：7 種標題模式、3 種儀表樣式（`bars`/`ring`/`popsicle`）、2 組動畫幀
（cat／parrot）、3 種上色模式。

## 切片順序

> **狀態更新 2026-08-31**：切片 1 與切片 2 的前半已經完成，這份清單在那之前寫成。
> 重讀時先跑一次查證指令，不要照著表格直接排工作。

| # | 切片 | 理由 |
|---|---|---|
| ~~**1**~~ | ~~兩個新客戶端~~ | **已完成** — `ClientRegistry.cs:35-36` 已註冊 `junie` 與 `opencodereview` |
| **2** | ~~Check now~~ ＋ Beta 通道 | **Check now 已完成**（`TrayService.CheckForUpdates`、`SettingsWindow.cs:561`，已中文化）。Beta 通道仍缺：`UpdateFlow.cs:100` 寫死 `prerelease: false` |
| **3** | Quota 資料出口：FFI 匯出 ＋ C# binding ＋ Overview 摘要行 | **整條鏈的瓶頸**；打通後每張卡都變純 UI |
| **4** | Quota lens 三張卡 | 依賴 3 |
| **5** | 多帳號／自訂掃描根目錄 | 碰認證與路徑，需安全審查流程 |
| **6** | Discord／用量歸因 | 最大；Discord 對外發布使用者資料，需安全審查流程 |

第 5、6 兩片觸及 pilotfish 的 security 風險門：pre-approval 走唯讀
`security-reviewer`，實作走 `security-executor`，不用一般 executor。

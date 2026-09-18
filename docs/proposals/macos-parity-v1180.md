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
| 時間窗歷史超過 12 列 | v1.18 #334：`Show N more`，每次 +12，上限 32（引擎 fold 的盡頭） | **進行中**（PR #112，沿用本 app 自己的「顯示更多（還有 N 筆）」控制項） |
| 「從未記錄」旗標依訂閱判定 | v1.18 #322：把「本機沒記錄」與「有記錄但未分類」拆開，原本三處各自算錯 | **無落差**。Windows 從來沒有這個缺陷；判定與 macOS 不同而非更嚴——見下方「已核對完畢」 |
| 長模型名撐破卡片 | v1.18 #336：改成依所在列量測，讓單行截斷發揮作用 | 未核對（WinUI 版面模型不同，可能不適用） |

### F. 多帳號與掃描根目錄

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 第二個 Claude 帳號分帳 | v1.15 #261：每個帳號對自己註冊的 root 各自掃描 | 缺 |
| 自訂掃描根目錄 `CLAUDE_CONFIG_DIR` | v1.14.x | 缺。repo-wide 零命中（只有這兩份 parity 文件提到它） |

> 這兩項碰認證與路徑，走 security 風險門。

### G. 舊文件就有、至今仍缺

| 能力 | Windows 現況 |
|---|---|
| Discord Rich Presence | 零實作。repo-wide `git grep -in discord -- .` 只有兩筆，都是散文宣告它不存在（`README.md` 的 Known limitations、`.github/release-notes/v0.3.0.md`） |
| Beta 更新通道 | `src/TokenBar.App/UpdateFlow.cs` 寫死 `prerelease: false`（出貨路徑唯一一處） |
| Individual tray items | **明列非目標**，不計入落差 |

### H. 引擎等價但與 UI 相關的 v1.15 修正

v1.15 #260（等價行的除數改成「讀數實際走過的距離」）與 admission 門檻、
rising runs 的處理：macOS 在 `TokenBarCore/WindowEquivalence.swift`，
Windows 在 `src/TokenBar.Core/WindowEquivalence.cs`、`QuotaEquivalenceFold.cs`。
**無落差**——已逐行對照，Windows 兩個檔都已是修正後的語義。見下方「已核對完畢」。

### I. 這份調查原本漏掉的四項（2026-09-19 補）

**來源不是 macOS，是這個 repo 自己的 `README.md`。** 下面「這份調查怎麼漏的」一節
說明為什麼。四項都親自對過兩邊：

| 能力 | macOS | Windows 現況 |
|---|---|---|
| 平面貢獻熱圖（第三個圖表模式） | `Views/UsageChartCard.swift` 的 `enum ChartView` 有**三個** case：`bars = "2d"`、`heatmap = "heat"`、`threeD = "3d"`。`Charts/ContributionHeatmap.swift` 是整年、週日起始，與 3D **同一份 `GridLayout`**、只是不同 renderer | 缺第三個模式。Windows 的 `SetChartView(bool use3D)`（`DashboardView.xaml.cs`）是**二元**的，同一個 store key `tokenbar.chart.view` 只寫 `"2d"`／`"3d"`：2D＝30 天堆疊長條（可依 Model／Agent 堆疊、Tokens／Cost），3D＝整年貢獻圖。缺的是「整年、平面」這一格 |
> 舊文件寫「兩邊都有 2D 熱圖與 3D 兩種呈現」。那句話**對錯各半**，而我第一次
> 查證時只看了檔名就把它整句判錯——Windows 確實有 2D／3D 兩種呈現，只是它的 2D
> 是 30 天長條而非整年熱圖；`QuotaHeatmap.cs` 的 7×24 又是第三張無關的圖。
> 真正的落差是 macOS 多出來的那個 `heat` 模式。

| Agent 品牌圖示 | `Views/AgentIconView.swift`——品牌色圓盤上放 SVG，mono 描白、full 用原設計，無圖示者退回首字母 | 缺。客戶端畫成 `GlowingDisc` 純色點 |
| Agent-limits sparkline／圖表版面 | 8 個檔提到 `Sparkline` | 缺。`git grep -il sparkline -- 'src/**/*.cs'` → 0 |
| Stats 歸因細分卡 | 6 個檔提到 `AttributionBreakdown` | 缺。同樣 → 0 |

### 這份調查怎麼漏的

上面的落差清單是從**兩個來源**推出來的：macOS v1.15–v1.18 的 release notes，加上
被取代的 `macos-parity-v1143.md`。這四項在兩個來源裡都沒有——它們早於 v1.14，而舊
文件也沒列。

但 `README.md` 的 Known limitations 一直列著它們，`.github/release-notes/v0.3.0.md`
也是。**我從變更紀錄和前一份調查推清單，卻沒有問這個 repo 自己已經記下了什麼。**
它們是在稽核一道無關指令（Discord 那道 grep 的範圍）時掉出來的。

下次重跑這份調查，第一步應該是 `git grep -n "parity" -- . | grep -v "^docs/"`，
把 repo 自己的說法先收齊，再去比對 macOS。

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

I 的四項未排序——它們是新發現，成本還沒估過。直覺上 sparkline 與歸因細分卡
偏純 UI，平面貢獻熱圖要新 renderer，品牌圖示要處理 SVG 資產與授權。

先做的四片都是純 UI 或純設定，不碰 FFI、不碰憑證、不碰引擎 pin。

## 已核對完畢（2026-09-18）

兩個正確性疑點都查完了，**兩題都是 Windows 已經有了**。

### #322 的「從未記錄」旗標：Windows 從來沒有這個缺陷

macOS 的缺陷是把 `declared` 算成 `!records.isEmpty`——那是在問**表**，不是在問
**這個訂閱**，所以宣告了任何一個 client 就等於替其他每一個都回答了。三個計算點
都這樣寫。修法是新增 `UsageAttribution.declares(subscription:records:)`。

Windows 有**兩個**入口，兩個都吃 `providerId`、都收斂到同一個
`DeclaredSpanCore`：

| 入口 | 呼叫點 | 給誰用 |
|---|---|---|
| `QuotaEquivalenceFold.Declared` | `QuotaLensProjection.BuildHistory` | 歷史卡（一串已完成週期） |
| `QuotaEquivalenceFold.DeclaredSpan` | `QuotaLensProjection.BuildClient` | 即時窗卡（一個進行中週期的取樣跨距） |

`Declared` 只是對每個週期跑一次 `DeclaredSpanCore` 的 OR，所以兩條路問的是
同一個問題。

要證明「沒有任何一處用表的空與非空來算」，光 grep `Declared` 是不夠的——那只找得到
入口，找不到別處自己算出來的 bool。要從**消費端**反推：`declared` 這個 bool 只有
兩個型別會吃（`WindowEquivalence.Aggregate` 與 `LiveRow`），而整個 repo 只有
三個呼叫點，每一個的值都來自 `QuotaEquivalenceFold` 的 providerId-scoped 判定。

指令要涵蓋整個 repo，`git grep` 而非 `grep -r src/ --include=*.cs`：後者只看
`src/` 底下的 C#，證不出「沒有別的地方」——這份文件的前一版就是用它撐一個它撐
不起來的句子。

```bash
git grep -n "WindowEquivalence.Aggregate\|WindowEquivalence.LiveRow" -- . \
  | grep -v "^src/TokenBar.Core.Tests/" | grep -v "///" | grep -v "^docs/"
```

| 檔案 | 呼叫 | `declared` 從哪來 |
|---|---|---|
| `QuotaEquivalenceFold.cs` | `Aggregate` | 同檔上一行的 `DeclaredCore` |
| `WindowHistoryText.cs` | `Aggregate`（在 `Equivalence` 裡） | 參數；唯一呼叫點是 `QuotaLensProjection` 的 `BuildHistory` → `Declared` |
| `WindowCardText.cs` | `LiveRow`（在 `LiveEquivalence` 裡） | 參數；唯一呼叫點是 `QuotaLensProjection` 的 `BuildClient` → `DeclaredSpan` |

測試專案與這份文件自己被排掉，理由不同：測試不是出貨路徑，文件命中的是它引用
自己的那兩行。

> 這裡刻意只寫檔名與符號、不寫行號。實作檔會動——這份文件的前一版就釘了一組
> 在另一個分支量到的行號，對這條分支根本不成立——而一個會說謊的查證步驟比
> 沒有查證更糟。上面那道 grep 每次都會給出當下的行號。

反向再查一次「有沒有人從 record 數量算 bool」：
`git grep -n "Records\.\(Count\|Any\)\|records\.\(Count\|Any\)" -- . | grep -v "^src/TokenBar.Core.Tests/" | grep -v "^docs/"`
命中五處，全部與 declaration 無關——`MaxEntries` 上限驗證、重複 source key 偵測、
以及設定頁 `AcceptAll` 的空清單早退。

判定本身**不是「更嚴」而是「不同」**——範圍更窄，但認的狀態更多：

| | macOS `declares` | Windows `DeclaredSpanCore` | 哪邊寬 |
|---|---|---|---|
| 範圍 | 整張表 | 該週期的取樣跨距內 | ← macOS 這邊寬 |
| 認的狀態 | 只認 `.assigned(subscription)` | `.assigned(providerId)` **或** `.excluded` | ← Windows 這邊寬 |

Windows 的 `Declared` 註解記著它自己走過一輪更嚴的修正（round 11 的 P2）：
「assigned 到別的訂閱」曾經也算數，但 session 窗和 weekly 窗在時間上重疊，
共用跨距裡一則指給**另一個**訂閱的訊息會讓這一個讀成 declared。

**一處真實分歧**：`.excluded` 在 Windows 算 declared，在 macOS 明確不算
（「排除是一種分類，但它不把任何東西導向這裡」）。兩邊的理由都成立，但因為
Windows 是按跨距判定的，它看得到「這段跨距裡的訊息被使用者排除了」——那個零
是有交代的；macOS 的表層判定看不到這件事。**這題該進 macOS 的待辦，不是 Windows 的。**

### v1.15 #260 的除數：Windows 已經是修正後的語義

`src/TokenBar.Core/WindowEquivalence.cs` 的註解就寫著
「The distance the readings travelled, not `last - first`」，並在同一個函式裡取
`QuotaHistoryFold.RisingRuns(readings)`。#260 的後半（誤差改成每次上升一個
量化步，而非整段位移一個步）也在，是這個式子：
`QuantisationHalfStep * cycles.Sum(cycle => cycle.RisingRuns) / anyMovement`。
三者都可以直接 `git grep` 那段文字或那個式子找到。

## 待核對（本次沒查、不要當成「沒落差」）

- C 的執行期字串是否也在 Windows 漏掉翻譯
- D 的 Models 清單 hover tooltip
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

# 落差——每一道都涵蓋它所支持的宣稱的範圍，所以是 repo-wide 的 git grep，
# 不是 grep -r src/。排除項只有 docs/（這份文件會命中自己）。
git grep -in "discord" -- . | grep -v "^docs/"          # → 2，都是散文，零實作
git grep -n "prerelease: false" -- 'src/**/*.cs'        # → UpdateFlow.cs 一處（出貨路徑）
git grep -n "CLAUDE_CONFIG_DIR" -- . | grep -v "^docs/" # → 0
git ls-files | grep -i "strings-.*\.json"              # → 只有 zh-Hant
git grep -il "sparkline\|AttributionBreakdown" -- 'src/**/*.cs'  # → 0（見 I）
git grep -n "SetChartView" -- 'src/**/*.cs'              # → 二元 bool，無第三個模式（見 I）
```

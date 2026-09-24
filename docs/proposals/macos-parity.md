# macOS parity 落差調查（對照 v1.20.2）

> **這份文件會過期。** 它記錄兩個特定 commit 之間的差集，不是「現況」。
> 使用前先跑下面的查證指令；若 macOS 已前進，重跑調查而不是相信這裡。
> 這份文件原名 `macos-parity-v1180.md`（對照 v1.18.0），本次重跑把對照基準
> 前進到 v1.20.2 並改名去掉版號，往後每次重跑直接覆蓋本檔，不再另開新檔案。
> 也取代 [`macos-parity-v1143.md`](macos-parity-v1143.md)——那份的對照基準更早、
> 已作廢。

| 項目 | 值 |
|---|---|
| macOS 對照基準 | `eab7d6a2`（`origin/main` ＝ v1.20.2，準確說是 v1.20.2 之後 4 個 commit：`v1.20.2-4-geab7d6a2`） |
| Windows 基準 | `a6655cb`（`origin/main`，v0.4.1 發佈後） |
| 引擎 pin | `d6512f5ae62c2be6751ed93adb9391ffe3f91579` |
| 調查日期 | 2026-09-25 |

## 最關鍵的發現：引擎仍完全對齊，但 `tb_core_ffi` 落差比想像大

**兩邊 pin 的還是同一個 commit**（見下方查證指令）。

但這次往下多挖了一層：`crates/tb_core_ffi` 是 Windows 自己維護的複本，不是
vendor 進來的東西，而它自 2026-07-20 起就沒有跟著 macOS 的同一份程式碼走。細節見
下面「`tb_core_ffi`（Rust 額度層）的落差」一節——那是本次調查最大的發現，前一版
（對照 v1.18.0 的那份）完全沒有量到。

> 下面每個讀 macOS 的指令都用 `$NATIVE` 指向
> [`Nanako0129/TokenBar`](https://github.com/Nanako0129/TokenBar) 的一份 clone。
> 沒有的話現開一份就好，這些指令全是唯讀的：
>
> ```bash
> NATIVE=$(mktemp -d)/TokenBar
> git clone --filter=blob:none https://github.com/Nanako0129/TokenBar.git "$NATIVE"
> ```

## 舊文件列的落差，現在已經關掉的

| 舊切片 | 現況 |
|---|---|
| 3 Quota 資料出口 | **已完成** — `include/ctb.h` 匯出 15 個函式，含 `tb_quota_history`、`tb_window_usage` |
| 4 Quota lens 三張卡 | **已完成** — `src/TokenBar.App/DashboardView.Quota.cs`（strip card ＋ heatmap） |
| 用量歸因整頁 | **已完成** — `UsageAttributionPage`，掛在 `SettingsWindow.cs:74` |
| Check now | **已完成**（舊文件已記） |
| 兩個新客戶端 | **已完成**（舊文件已記） |
| 時間窗歷史分頁（原 E 節） | **已完成**（PR #112，沿用本 app 自己的「顯示更多（還有 N 筆）」控制項） |
| 不合理成本警示（原 D 節，v1.15 #264） | **已完成** — Windows PR #118，隨 v0.4.0 出貨 |
| 未定價項目顯示「—」 | **已完成** — Windows PR #116；macOS 後來在 #375 也做了同一件事，所以這項在兩邊都已收斂，不再是誰追誰 |
| agy CLI 額度指令 | **已完成** — Windows PR #122（v0.4.1），帶 Credential Manager 閘門；macOS 在 #378 獨立做出同等的閘門，兩邊各自收斂到同一個設計 |

v1.17 的 Codex Spark 時間窗消歧（#286）也已移植：
`src/TokenBar.Core/AgentUsageQualifier.cs`。

## `tb_core_ffi`（Rust 額度層）的落差

前一版調查只查到「引擎（tokscale-core）已對齊」就停了，沒有再往下查
`crates/tb_core_ffi`——這個 crate 不是 vendor 進來的，是 Windows 自己維護的
一份複本，理論上要跟著 macOS 那邊的同一顆心臟一起走，但自 2026-07-20 起，
兩邊各自加了自己的 commit：

```bash
git -C "$NATIVE" log --oneline --no-merges --since=2026-07-20 eab7d6a2 -- crates/tb_core_ffi | wc -l
#   → 159
git log --oneline --no-merges --since=2026-07-20 HEAD -- crates/tb_core_ffi | wc -l
#   → 37
```

macOS 159 個 commit，Windows 37 個——四倍以上的落差。對應到檔案行數：

| 檔案 | macOS | Windows |
|---|---:|---:|
| `agent_quota_history.rs` | 11,887 | 7,039 |
| `agent_usage.rs` | 13,200 | 8,973 |
| `lib.rs` | 2,612 | 1,181 |
| `window_usage.rs` | 996 | 647 |
| `agent_antigravity.rs` | 4,171 | 3,787 |

只存在 macOS 的檔案：`agent_grokbot.rs`、`agent_kiro.rs`、
`agent_opencode_go.rs`、`kiro_integrations.rs`、`keychain_consent.rs`、
`macos_safe_storage.rs`、`claude_config_dirs.rs`、`extra_scan_paths.rs`。

只存在 Windows 的檔案：`agent_history.rs`——macOS 在 2ea81af1（#121，2026-07-31）
退休了這個模組（沒有出貨路徑呼叫它），把裡面唯一還在用的
`weighted_median` 搬進了 `agent_quota_history.rs`；Windows 沒跟進這次退休，
現在只呼叫它的 `weighted_median`，其餘 1,206 行在 macOS 那邊已經不存在。

### 落差項目與對應的計畫切片

| 能力 | macOS commit(s) | Windows 現況 | 計畫切片 |
|---|---|---|---|
| 額度歷史 store：schema 3→4 lazy migration、hour buckets + nesting、clock 修復、drop sample not file、irregular reset、clock after response、修復而非拒寫、保留無法建模的讀數、record-only retention | `71eb5d79`、`a1356c06`、`1b16182c`、`5e6e6b99`、`ab8fb989`、`71bbe789`、`49aff48c`、`bf7a6b92`、`e69c08d6`、`cc1534ad`、`8821d9d6`、`1cfa3143`、`2c236e1a` | Windows 停在 schema 3（`HISTORY_SCHEMA_VERSION = 3`），早於 macOS 的 `2bea9912`（2026-08-02） | S1 |
| `HistoryScope`（換憑證不斷歷史） | `76e0ff38` | 缺；x64 實機量到 Claude 14 條系列、8 個 scope，與 macOS 在 `76e0ff38` 之前量到的斷裂同一種 | S1（含一次性合併舊系列，Windows 自寫，不能照搬 macOS 的遷移程式） |
| 沒憑證的卡片不佔分頁：`required_card_source` / `RemoteCredentialError` | `2a78996c`、`ed11e4fc`（#345／#348） | 缺 | S3 |
| OAuth client id 最後連字號錨點 | （未附獨立 commit） | 缺 | S4 |
| Grok 週額度改從 credit pool 讀，pace 系列 v2 | `774e6410`、`822bf5e8`、`b18fe7cf` | 缺 | S4 |
| Antigravity OAuth client 候選路徑只列 macOS `.app` 路徑 | — | Windows 上無法 refresh；這是 Windows 自己的缺口，不是落後 macOS，macOS 那邊本來就沒有這個問題要解 | S5 |
| Kiro／OpenCode Go 額度卡 | `3f543ed2`、`37b52739` | 缺；Windows 端的憑證存放位置還沒量測過 | S6 |
| Grok Bot（Keychain + Electron `safeStorage` v10） | — | 缺；Windows 需要對應的 DPAPI 設計，不能照搬 Keychain | 暫緩 |
| 多帳號（`extra_scan_paths`、`CLAUDE_CONFIG_DIR`、`tb_window_usage` 的 `account_key`、`tb_quota_curve`） | — | 缺 | 暫緩 |

```bash
git grep -n 'HistoryScope\|required_card_source\|RemoteCredentialError' -- crates/tb_core_ffi
#   （無輸出）
```

### Windows 領先／刻意分歧（保留，不是落差）

- `usage_graph.rs` 的 `run_local_first` ＋ `meta.pricingMode`/`costCoverage`
- `window_usage.rs` 的快取設計——比 macOS 同等機制更穩健，見該檔的模組註解
- `tb_quota_history` 匯出
- `agent_storage_windows.rs`
- agy 的 Credential Manager 閘門

## 落差清單（全部親自查證過）

### A. Quota provider —— 住在 `crates/tb_core_ffi`，Windows 自有

已併入上面「`tb_core_ffi`（Rust 額度層）的落差」一節，這裡不再重複列。
Grok Bot 憑證同意流程那條要讀使用者憑證並送往對應服務，觸及 pilotfish 的
security 風險門：pre-approval 走唯讀 `security-reviewer`，實作走
`security-executor`。

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
| Models 清單共用 hover tooltip | v1.15 #264 | 未核對。`HoverTip.cs` 存在且 Quota/3D 有用，Models 清單有沒有要另查 |

### E. macOS shell（Swift）在 v1.18.0 之後新增的 UI 功能

這節是這次重跑補進去的——前一份調查的對照基準是 v1.18.0，沒看到之後的
四個 UI 改動：

| 能力 | macOS PR |
|---|---|
| 時間窗歷史列 hover 明細 | #368 |
| 重開 app 時額度頁四張卡從快取先畫出，不等網路回來才有畫面 | #361／#362／#364 |
| 單一 provider 讀取失敗不清掉其他 provider 已有的歷史 | #356／#360 |
| Antigravity IDE 與 CLI 併成一個分頁 | #349 |

四項都還沒核對 Windows 現況（未核對，不要當成「沒落差」）。

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
| Beta 更新通道 | **明列非目標**（2026-09-23 決定不做），不計入落差。`src/TokenBar.App/UpdateFlow.cs` 寫死 `prerelease: false` 是刻意的：app 內更新只走正式版 |
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

Rust 核心重新對齊排在最前面——上面「`tb_core_ffi` 的落差」一節量到的
四倍 commit 落差，是這次重跑之後才浮現的新發現，而 B～I 的每一項幾乎都疊在
這個 crate 之上，先對齊地基比先做任何一片 UI 都划算。細節在本機的
`.agent-local/plan-core-resync.md`（未追蹤進 repo，這裡不連結），大致順序是：
先把額度歷史 store 追到最新 schema／lazy migration（S1，含 `HistoryScope`），
再補「沒憑證的卡片不佔分頁」（S3），再補 OAuth client id 錨點與 Grok credit-pool
週額度（S4），再處理 Antigravity OAuth 候選路徑的 Windows 自有缺口（S5），
最後是 Kiro／OpenCode Go 額度卡（S6）；Grok Bot 憑證與多帳號兩項暫緩。

| # | 切片 | 理由 |
|---|---|---|
| — | Rust 核心重新對齊（S1／S3／S4／S5／S6，見上段與 `.agent-local/plan-core-resync.md`） | 地基；B～I 大多依賴它 |
| ~~—~~ | ~~Beta 更新通道~~ | **不做**（2026-09-23 決定），見 G |
| ~~—~~ | ~~不合理成本警示（舊 D）~~ | **已完成**（PR #118，v0.4.0） |
| ~~—~~ | ~~未定價顯示「—」~~ | **已完成**（PR #116） |
| ~~—~~ | ~~時間窗歷史分頁（舊 E）~~ | **已完成**（PR #112） |
| ~~—~~ | ~~agy CLI 額度指令~~ | **已完成**（PR #122，v0.4.1） |
| 下一片 | 選單列文字顏色（B） | 純設定 ＋ 托盤繪製，無網路無憑證，不依賴 Rust 核心重新對齊 |
| 之後 | 簡體中文（C） | 488 個 key，量大但機械；需要審過的譯文而非字元轉換 |
| 之後 | 多帳號／自訂掃描根目錄（F） | security 風險門；等 Rust 核心的 S1（歷史 store）先落地 |
| 之後 | Grok Bot 憑證同意（暫緩項） | security 風險門；Windows 的同意 UX 要重新設計 |
| 之後 | Discord RPC（G） | 最大；對外發布使用者資料，security 風險門 |

E 節（macOS shell 在 v1.18.0 之後新增的四個 UI 功能）與 I 的四項都還未排序——
都是新發現，成本還沒估過。I 當中直覺上 sparkline 與歸因細分卡偏純 UI，平面
貢獻熱圖要新 renderer，品牌圖示要處理 SVG 資產與授權。

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
>
> **這份文件學到的教訓，寫在這裡給下一個編輯它的人**：前四輪審查的每一則發現
> 都是同一個形狀——指令證明 A，旁邊的句子宣稱 B。修掉一個實例，下一輪就在隔壁
> 一行找到同一個。所以最後的做法是把每一道指令的**真實輸出**貼在它下面（見文末
> 查證區塊），讓讀者比對輸出而不是比對我的形容詞。寫新宣稱時請照做：先跑指令，
> 貼輸出，再讓輸出自己說話。

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
- E 節（macOS shell v1.18.0 之後新增的四個 UI 功能）在 Windows 的現況
- OAuth client id 最後連字號錨點缺少對應的 macOS commit 編號，還沒回頭找
- v1.15–v1.20 之間，只讀了 release notes 與 commit subject；
  沒有進 release notes 的內部重構不在本清單內

## 查證指令

```bash
# 基準是否還成立（$NATIVE 見上面「最關鍵的發現」一節的 clone 指令）
git -C "$NATIVE" fetch origin
git -C "$NATIVE" log --oneline -1 eab7d6a2   # 應為「Merge pull request #373 …」
git -C "$NATIVE" describe --tags eab7d6a2    # 應為 v1.20.2-4-geab7d6a2

# 引擎是否仍對齊
git -C "$NATIVE" ls-tree eab7d6a2 vendor/tokscale-core
git ls-tree HEAD vendor/tokscale-core
```

```
160000 commit d6512f5ae62c2be6751ed93adb9391ffe3f91579	vendor/tokscale-core
160000 commit d6512f5ae62c2be6751ed93adb9391ffe3f91579	vendor/tokscale-core
```

```bash
# tb_core_ffi 的 commit 落差（一定要 --no-merges，不然 macOS 這邊會灌到 174，
# 因為 macOS 這段期間開的合併 PR 比 Windows 多得多，不是真的多做了那麼多事）
git -C "$NATIVE" log --oneline --no-merges --since=2026-07-20 eab7d6a2 -- crates/tb_core_ffi | wc -l
git log --oneline --no-merges --since=2026-07-20 HEAD -- crates/tb_core_ffi | wc -l
```

```
     159
      37
```

```bash
# 行數落差，逐檔
for f in agent_quota_history.rs agent_usage.rs lib.rs window_usage.rs agent_antigravity.rs; do
  echo "== $f =="
  git -C "$NATIVE" show eab7d6a2:crates/tb_core_ffi/src/$f | wc -l
  git show HEAD:crates/tb_core_ffi/src/$f | wc -l
done
```

```
== agent_quota_history.rs ==
   11887
    7039
== agent_usage.rs ==
   13200
    8973
== lib.rs ==
    2612
    1181
== window_usage.rs ==
     996
     647
== agent_antigravity.rs ==
    4171
    3787
```

```bash
# 只存在一邊的檔案
comm -23 \
  <(git -C "$NATIVE" ls-tree -r --name-only eab7d6a2 -- crates/tb_core_ffi/src | sort) \
  <(git ls-tree -r --name-only HEAD -- crates/tb_core_ffi/src | sort)
echo "--- Windows only ---"
comm -13 \
  <(git -C "$NATIVE" ls-tree -r --name-only eab7d6a2 -- crates/tb_core_ffi/src | sort) \
  <(git ls-tree -r --name-only HEAD -- crates/tb_core_ffi/src | sort)
```

```
crates/tb_core_ffi/src/agent_grokbot.rs
crates/tb_core_ffi/src/agent_kiro.rs
crates/tb_core_ffi/src/agent_opencode_go.rs
crates/tb_core_ffi/src/claude_config_dirs.rs
crates/tb_core_ffi/src/extra_scan_paths.rs
crates/tb_core_ffi/src/keychain_consent.rs
crates/tb_core_ffi/src/kiro_integrations.rs
crates/tb_core_ffi/src/macos_safe_storage.rs
--- Windows only ---
crates/tb_core_ffi/src/agent_history.rs
```

```bash
# HistoryScope／required_card_source／RemoteCredentialError 在 Windows 缺席
git grep -n 'HistoryScope\|required_card_source\|RemoteCredentialError' -- crates/tb_core_ffi
```

```
（無輸出）
```

```bash
# Windows 的歷史 store 還停在 schema 3
git grep -n "HISTORY_SCHEMA_VERSION" -- crates/tb_core_ffi/src/agent_quota_history.rs
```

```
crates/tb_core_ffi/src/agent_quota_history.rs:22:pub(crate) const HISTORY_SCHEMA_VERSION: u32 = 3;
```

```bash
# agent_history.rs 在 macOS 的退休 commit，以及 Windows 現在仍掛著它
git -C "$NATIVE" show 2ea81af1 --stat | head -5
git grep -n "mod agent_history" -- crates/tb_core_ffi/src/lib.rs
```

```
commit 2ea81af176c7db7b586f5d4ec1333e6e4af406b5
Author: Nyanako <44753291+Nanako0129@users.noreply.github.com>
Date:   Fri Jul 31 10:12:56 2026 +0800

    refactor(pace): retire the unused Codex v2 evaluator (#121)
crates/tb_core_ffi/src/lib.rs:21:mod agent_history;
```

```bash
# 落差。每一道下面貼的是它未經刪節的輸出，逐字，不是我對輸出的轉述——
# 前四輪審查抓到的每一則，都是「指令證明 A、旁邊的句子宣稱 B」。
# 讀者要比對的是輸出，不是我的形容詞。行號會漂，形狀不會。
#
# 這些輸出是我在 2026-09-19 撰寫該節時跑出來的（2026-09-25 補上 tb_core_ffi
# 的部分）。那是執行日期，不是任何可以從 repo 反推的東西——commit 時間戳證明不了
# 指令何時跑過，所以這裡只宣稱它是什麼：一次人工執行的結果，讀者重跑就能比對。

git grep -in "discord" -- . | grep -v "^docs/"
#   .github/release-notes/v0.3.0.md:69:Present on the macOS build, not yet here: Discord Rich Presence, per-client tray items, Simplified Chinese, menu-bar font colour, the flat-heatmap chart mode, agent brand icons (clients render as coloured discs), the Agent-limits sparkline and chart layout, the Stats attribution-breakdown card, and support for multiple Claude accounts.
#   README.md:187:Windows does not yet have macOS parity on: Discord Rich Presence, per-client tray items,
#   兩筆都是散文在宣告它不存在。零實作。

git grep -n "prerelease: false" -- 'src/**/*.cs'
#   src/TokenBar.App/UpdateFlow.cs:120:            prerelease: false,
#   出貨路徑唯一一處。Beta 通道是明列非目標，所以這一行是刻意的，不是落差。

git grep -n "CLAUDE_CONFIG_DIR" -- . | grep -v "^docs/"
#   （無輸出）

git ls-files | grep -i "strings-.*\.json"
#   src/TokenBar.App/Assets/strings-zh-Hant.json

git grep -il "sparkline\|AttributionBreakdown" -- 'src/**/*.cs'
#   （無輸出）

git grep -n "SetChartView(bool\|tokenbar.chart.view" -- 'src/**/*.cs'
#   src/TokenBar.App/DashboardView.xaml.cs:43:        AppSettings.Store.GetString("tokenbar.chart.view", "2d") == "3d";
#   src/TokenBar.App/DashboardView.xaml.cs:272:    private void SetChartView(bool use3D)
#   src/TokenBar.App/DashboardView.xaml.cs:281:        AppSettings.Store.SetString("tokenbar.chart.view", use3D ? "3d" : "2d");
#   唯一讀取點、bool 簽名、唯一寫入點且只寫兩個值——所以是二元，沒有第三個模式。
```

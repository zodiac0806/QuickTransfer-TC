# QuickTransfer-TC

## OVERVIEW

Shift／Ctrl／Alt＋右鍵點道具，就直接觸發遊戲**本來就有的**右鍵選單項目（交給僱員保管、放入陸行鳥鞍囊、放入兵裝庫、出售、交易、拆分…），省掉「右鍵→找選項→點下去」那三下。插件本身不搬格子，只是幫你按選單——唯一的例外是公會儲物櫃的存入，那條走遊戲內部的 move 函式（跟拖放同一支）。

上游：[Knack117/QuickTransfer](https://github.com/Knack117/QuickTransfer)（作者 flick，另有 puni.sh 上的 Kagekazu 版）。上游是 API14／`net10.0-windows`，本 fork 降到台服的 **API13／`net9.0-windows`**。

## STRUCTURE

```
QuickTransfer.csproj      Dalamud.NET.Sdk/13.0.0 + net9.0-windows（TC 改動）
QuickTransfer.json        插件資訊清單，DalamudApiLevel=13（TC 改動）
QuickTransfer.cs          主體（~4600 行）：服務注入、滑鼠／按鍵狀態機、
                          addon 可見性判斷、公會儲物櫃的存入／整理流程
ContextMenuHandler.cs     右鍵選單項目的「比對 → 選取 → 關閉」邏輯
GameStrings.cs            ★TC 新增：從 Addon／LogMessage 資料表取當前語言字串
BulkTransfer.cs           ★TC 新增：批次搬運狀態機（Ctrl＋Shift＋右鍵）
InventoryHelpers.cs       容器類型／可見性判斷
DragDropHelpers.cs        拖放與 slot 解析
AtkValueHelpers.cs        AtkValue 字串讀取與 callback 產生
QuickTransferWindow.cs    設定視窗（已繁中化）
.github/workflows/        build-check.yml / release.yml（已換成 TC pinned Dalamud）
```

## WHERE TO LOOK

| 要做的事 | 去哪裡 |
|---|---|
| 某個選單項目在台服比對不到 | `GameStrings.cs` 的 row id 表 |
| 新增一種可自動點的選單動作 | `ContextMenuHandler.AutoContextAction` ＋ `GameStrings.ActionRows` |
| Shift／Ctrl／Alt 的行為分支 | `ContextMenuHandler.TryAutoSelectAndClose` |
| 公會儲物櫃存入／取出／整理 | `QuickTransfer.cs` 搜 `companyChest` |
| 哪些 addon 名稱算「容器開著」 | `InventoryHelpers.cs`、`QuickTransfer.cs` 的 `AddVisible(...)` |
| 設定視窗文案 | `QuickTransferWindow.cs` |
| 批次搬運的方向判斷／掃格順序 | `BulkTransfer.cs` 的 `TryBuildBulkPlan`／`TryGetCurrentBulkSource` |
| 數量視窗自動確認 | `QuickTransfer.cs` 的 `OnInputNumericPreSetup`／`TrySetInputNumericToMax` |

## CONVENTIONS

- **選單文字一律用遊戲資料表比對，不要寫死字串。** 上游原本寫死英文（`"Entrust to Retainer"`…），台服選單是繁中，一個都對不上。TC fork 的做法是 `GameStrings.MatchesClientLabel()`：用 Addon 資料表的 row id 取出**當前客戶端語言**的正式字串再比。這不是「翻成中文」，是語言中立——切成日文、英文一樣能用。
- 資料表比對**失敗時會往下走上游那套英文啟發式**，所以國際服行為完全不變，上游合併也不會打架。
- TC 專屬修改一律標 `// [TC]` 註解並寫清楚為什麼，方便 `sync-upstream` 衝突時判斷。
- `GameStrings.cs` 裡每個 row id 後面都註明台服實際文字，日後驗證直接查 Addon 資料表該 row。

## 已核對的 Addon／LogMessage row id（台服 7.20）

| row | 台服文字 | 對應動作 |
|---|---|---|
| Addon 92 | 拆分 | Split |
| Addon 93 | 出售 | Sell |
| Addon 95 | 交易 | Trade |
| Addon 97 | 交給僱員保管 | EntrustToRetainer |
| Addon 98 | 從僱員處取回 | RetrieveFromRetainer |
| Addon 881 | 放入陸行鳥鞍囊 | AddAllToSaddlebag |
| Addon 887 | 從陸行鳥鞍囊中取回 | RemoveAllFromSaddlebag |
| Addon 1387 | 放入兵裝庫 | PlaceInArmouryChest |
| Addon 1388 | 放入背包 | ReturnToInventory |
| Addon 1389 | 自動整理 | Sort |
| Addon 1390 | 撤銷整理 | Undo Sort（已整理的訊號） |
| Addon 2950 | 取出 | RemoveFromCompanyChest |
| Addon 2897 / 2898 | 請設定放入的數量。／請設定取出的數量。 | 儲物櫃數量視窗 |
| Addon 915 / 914 | 請選擇要保管的數量。／請選擇要取出的數量。 | 僱員數量視窗 |
| Addon 890 / 889 | 請選擇要放入的數量。／請選擇要取出的數量。 | 鞍囊數量視窗 |
| LogMessage 1861 | 處理公會儲物櫃失敗。 | 儲物櫃忙碌退避 |
| LogMessage 1873 | 無法保存道具，其他玩家正在使用儲物櫃。 | 同上 |
| LogMessage 1874 | 無法取出道具，其他玩家正在使用儲物櫃。 | 同上 |

dump 方式：用 Lumina 開 `<GamePath>\game\sqpack`，`LuminaOptions.DefaultExcelLanguage = Language.TraditionalChinese`（台服 exd 的語言標記是 TC Lumina fork 新增的 `TraditionalChinese = 8`，不設就 `GetExcelSheet` 回傳 null）。

## 批次搬運（TC 新增功能）

`Ctrl＋Shift＋右鍵` 一格 → 以該格為起點，往後整批搬到對面的容器。方向由「右鍵的是哪個容器」推出來，不用另外選；搬幾格看設定的 `BulkTransferCount`（0 = 到最後一格）。跑到一半再按一次同樣組合鍵就中止。

實作要點：

- **掃格順序一定要用畫面顯示順序，不能用實體 slot 編號。** 背包的實體格號是伺服器給的，跟背包視窗看到的排列無關；沒「自動整理」過的背包兩者差很多，拿實體編號掃的結果就是使用者看到的「跳著搬」。顯示順序在 `ItemOrderModule` 的 sorter：entry 是 `(Page, Slot)`，實際容器 = `sorter->InventoryType + Page`。背包用 `InventorySorter`，僱員用 `RetainerSorter[ActiveRetainerId]`，公會儲物櫃沒有客戶端排序（顯示順序＝實體順序）。取不到 sorter 時退回實體順序。
- **搬運用 `TryCompanyChestMoveItem`（＝`RaptureAtkModule::HandleItemMove`）**，跟拖放同一支。遊戲自己的檢查（綁定、裝備中、掛市場中…）全都還在。
- **一次只送一筆，然後等來源格真的變動**才繼續（`HasSlotChanged`）。不要用固定 delay 硬送，會被伺服器擋掉而且無法察覺。
- **「格」才是配額單位**：併堆只搬走一部分時同一格會再跑一次，此時不扣 `Remaining`。
- 同一格送 `BulkMaxStuckRetries` 次還是不動 → 判定這個道具搬不動（綁定、裝備中、掛市場…），**直接中止並在聊天視窗點名是哪個道具**。不要改回「跳過繼續跑」：使用者要的是停下來，而且跳過很可能一路撞上一整排都搬不動的東西。
- 數量視窗開著時整個狀態機會讓路，但有 `BulkInputNumericStallMs` 看門狗：超時沒被處理就中止，視窗留給使用者。少了這個，自動確認一旦失效（例如提示字串沒認出來）整批就會每個 frame 都 return，表現出來就是卡死。
- 儲物櫃**只掃右鍵的那一頁**：其他分頁沒開過就沒載入，硬掃會讀到空資料。僱員與背包則用 `IsContainerLoaded` 過濾後全掃。
- 搬運中途僱員／儲物櫃視窗關掉 → 立刻停手。

## 公會儲物櫃：怎麼知道使用者點了哪一格（踩過一輪才找到的做法）

儲物櫃的右鍵選單走 `MenuType.Default`，**不經過 `OpenForItemSlot`**，所以拿不到現成的 (容器, 格號)。以下是實測結論，不要再走回頭路：

| 方法 | 結果 |
|---|---|
| hover 事件（`TryGetDragDropInterfaceFromReceiveEvent`） | ❌ 儲物櫃不發它認得的事件 |
| 節點 payload（`GetPayloadContainer()` 的 Int1/Int2） | ❌ **沒被填**。三次實測讀到 37/30、37/5、0/34，Int1 一下 37 一下 0，是殘值。payload 只有真的開始拖曳才會填 |
| 上游的三條分頁解析 | ❌ 全部依賴 payload，所以在台服一起失效（`hover=無 addon=失敗 atkvalues=失敗`） |
| `AgentFreeCompanyChest` 的 Context 欄位 | ⚠️ API13 的 FFXIVClientStructs 沒這結構，要自己寫死位移（OmenTools 寫 6956/6960，那是別的客戶端版本）。讀錯會搬到不相干的格子，比「沒反應」危險，**沒採用** |

**實際採用的做法（兩半分開取）：**

- **格號** ← 收集 addon 裡所有格子節點，**依尺寸分組取最大那一群**（實測收到 68 個節點但只有 50 格是真的），照螢幕位置由上而下、由左而右排序，游標壓著的那一格排第幾就是第幾格。儲物櫃沒有客戶端排序，顯示順序＝實體順序。
- **分頁** ← 分頁鈕 `AtkComponentRadioButton` 的 `IsChecked`（`IsSelected` 實測全是 false）。**必須先照 NodeId 排序**：節點樹是倒序走訪的（實測收到 16,15,14,13,12,11,10），直接用收集順序當分頁索引會選錯頁。7 顆＝5 個道具分頁＋水晶＋Gil，索引超出道具分頁數代表開在水晶/Gil 頁。
- 最後一定要驗證 `(分頁, 格號)` 真的有東西才算數，並在開始訊息印出該格道具名讓使用者當場核對。

## 公會儲物櫃：伺服器節流不是失敗

儲物櫃有伺服器端節流，連續送出**正常就會**被擋，訊息是 `LogMessage 1861`「處理公會儲物櫃失敗。」。這不是「這個道具搬不動」：

- 涉及儲物櫃時：送出後等 1 秒才判定沒反應、被擋退避 1.5 秒、上限 8 次（背包／僱員維持 3 次快速中止）。
- **起跑要延遲**：右鍵選單要 50ms 後才關，選單還開著時儲物櫃會回絕操作——第一筆固定失敗就是這個原因。起跑等 400ms，且每個 tick 檢查 `ContextMenu` 是否還可見。
- 使用者主動起跑時清掉 `companyChestBusyHits`／`companyChestBusyUntilMs`，否則上次的失敗退避（5 秒起跳，連續失敗翻倍到 60 秒）會拖累這次。

## ANTI-PATTERNS

- ❌ 在 `ContextLabelMatches` 裡加寫死的中文字串。改資料表 row id，否則國際服玩家或日後台服改譯名就壞掉。
- ❌ 自己算 slot 然後硬搬道具。這個插件的賣點就是「只點遊戲既有選單」，繞過選單＝繞過遊戲自己的合法性檢查。
- ❌ 把 `TransferCooldownMs` 設成 0 然後回報「重複搬運」——那個冷卻就是防手滑的。
- ❌ 批次搬運改成「固定間隔連續送」。一定要等來源格變動再送下一筆，否則伺服器擋掉時插件完全不知道，會一路空轉到逾時。
- ❌ 在 `OnInputNumericPreSetup`／`TrySetInputNumericToMax` 裡用英文關鍵字判斷數量視窗種類。台服比不到，自動確認會整組失效——改用 `GameStrings.ClassifyQuantityPrompt`。

## 共用函式庫

無。這個插件只依賴 Dalamud 本體（含隨 Dalamud 出貨的 FFXIVClientStructs／Lumina），沒有任何 submodule 或第三方 NuGet，所以**不受 ECommons 版本不變式影響**。規則見 [`../DalamudPluginsTC/docs/plugin-fork-standards.md`](../DalamudPluginsTC/docs/plugin-fork-standards.md)。

## REQUIRED PLUGINS

無。不對外提供 IPC，也不依賴其他插件。

## COMMANDS

- 建置：`dotnet build QuickTransfer.csproj -c Release`
  - 需要台服 pinned Dalamud 在 `%APPDATA%\xivlauncher\Addon\Hooks\dev\` 或 `%APPDATA%\FFXIVSimpleLauncher\Dalamud\Injector\`
  - 本機若 `DALAMUD_HOME` 指向別的 Dalamud，用 `-p:DalamudLibPath=...` 或設環境變數覆蓋
- 遊戲內：`/qt` 開設定視窗

## NOTES

- 上游的 `.github/workflows/release.yml` 原本抓 goatcorp `api14/latest.zip`，已換成 `ffxiv-tc-port/DalamudPluginsTC` 的 `dalamud-pin-v13.0.0.16`。
- `packages.lock.json` 已移除（上游鎖的是 API14 的套件集）。要重新啟用鎖檔就在台服環境下重跑一次 restore。
- 還沒接 `sync-upstream.yml`：要等 repo 進 `ffxiv-tc-port` org、確定基底分支名稱之後再補。
- 上游 README 的支援清單沒寫清楚僱員也吃 Shift＋右鍵，但程式裡 `TryAutoSelectAndClose` 確實有 `entrustIdx`／`retrieveIdx` 兩條分支——僱員是支援的。

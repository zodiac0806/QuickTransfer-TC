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
| LogMessage 1861 | 處理公會儲物櫃失敗。 | 儲物櫃忙碌退避 |
| LogMessage 1873 | 無法保存道具，其他玩家正在使用儲物櫃。 | 同上 |
| LogMessage 1874 | 無法取出道具，其他玩家正在使用儲物櫃。 | 同上 |

dump 方式：用 Lumina 開 `<GamePath>\game\sqpack`，`LuminaOptions.DefaultExcelLanguage = Language.TraditionalChinese`（台服 exd 的語言標記是 TC Lumina fork 新增的 `TraditionalChinese = 8`，不設就 `GetExcelSheet` 回傳 null）。

## ANTI-PATTERNS

- ❌ 在 `ContextLabelMatches` 裡加寫死的中文字串。改資料表 row id，否則國際服玩家或日後台服改譯名就壞掉。
- ❌ 自己算 slot 然後硬搬道具。這個插件的賣點就是「只點遊戲既有選單」，繞過選單＝繞過遊戲自己的合法性檢查。
- ❌ 把 `TransferCooldownMs` 設成 0 然後回報「重複搬運」——那個冷卻就是防手滑的。

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

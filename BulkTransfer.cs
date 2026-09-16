using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace QuickTransfer;

/// <summary>
/// [TC] 批次搬運：Ctrl＋Shift＋右鍵，以點到的那一格為起點，把它之後的東西整批搬到對面的容器。
///
/// 方向是從「你右鍵的是哪個容器」推出來的，不用另外選：
///   背包的格子    → 搬去僱員背包／公會儲物櫃（看當下開著哪一個）
///   僱員的格子    → 搬回背包
///   儲物櫃的格子  → 搬回背包
///
/// 搬幾格由設定的「批次搬運格數」決定，0 = 一路搬到最後一格。
///
/// ⚠ 順序一定要用**畫面顯示順序**，不能用 InventoryType/Slot 的實體編號。
///    背包的實體格號是伺服器給的，跟你在背包視窗看到的排列無關（沒整理過的背包差很多），
///    拿實體編號來掃就會變成「跳著搬」。顯示順序在 ItemOrderModule 的 sorter 裡，
///    「自動整理」改寫的就是它。細節見 <see cref="TryBuildDisplayOrder"/>。
///
/// 搬運本身走跟拖放同一支遊戲函式（RaptureAtkModule::HandleItemMove），
/// 所以遊戲自己的合法性檢查（綁定、裝備中、市場委託中…）全都還在，插件不會繞過去。
/// </summary>
public sealed unsafe partial class Plugin
{
    private struct BulkTransferState
    {
        public bool Active;

        /// <summary>要掃的格子，已經照畫面顯示順序排好。</summary>
        public (InventoryType Type, uint Slot)[] Order;
        public int OrderIndex;

        /// <summary>目標容器清單，找位置時依序試。</summary>
        public InventoryType[] DestTypes;

        /// <summary>還能搬幾格；int.MaxValue 代表「到底為止」。</summary>
        public int Remaining;

        public int Moved;

        public long NextAttemptAtMs;
        public long ExpiresAtMs;

        /// <summary>已經送出搬運指令、正在等這一格真的變動。</summary>
        public bool WaitingForMove;
        public uint PendingItemId;
        public uint PendingQty;
        public InventoryType PendingType;
        public uint PendingSlot;
        public int StuckCount;

        /// <summary>數量視窗是什麼時候開始擋路的；0 = 現在沒擋。</summary>
        public long InputNumericSeenAtMs;
    }

    private BulkTransferState bulk;

    /// <summary>一次 tick 最多跳過幾個空格，免得整個背包都空的時候一格一格慢慢爬。</summary>
    private const int BulkEmptySlotScanPerTick = 40;

    /// <summary>同一格送了幾次還是沒動，就判定這個道具搬不動。</summary>
    private const int BulkMaxStuckRetries = 3;

    /// <summary>
    /// 公會儲物櫃的重試次數要放寬很多。它有伺服器端節流，連續送出**正常就會**被擋掉，
    /// 那不是「這個道具搬不動」。用背包那種 3 次就中止的標準會把正常節流誤判成失敗。
    /// （對照 SND 腳本的做法：5 次、每次最多等 10 秒、被擋再額外等 1.5 秒。）
    /// </summary>
    private const int BulkMaxStuckRetriesCompanyChest = 8;

    /// <summary>被擋之後的退避間隔，明顯拉長，不要繼續用一般的每格間隔去頂。</summary>
    private const int BulkThrottleBackoffMs = 1500;

    /// <summary>按下組合鍵到送出第一筆之間的緩衝，讓右鍵選單先收掉。</summary>
    private const int BulkStartDelayMs = 400;

    /// <summary>
    /// 數量視窗開著超過這個時間還沒被處理掉，就判定卡住了。
    /// 會發生在自動確認被關掉、或這個視窗不是我們預期的那一種（於是沒人去按確定）。
    /// </summary>
    private const int BulkInputNumericStallMs = 3000;

    private const int BulkSlotCap = 80;

    /// <summary>
    /// 解析「使用者點的是公會儲物櫃哪一格」。儲物櫃的右鍵選單走 MenuType.Default，
    /// 不經過 OpenForItemSlot，所以拿不到現成的 (容器, 格號)。
    ///
    /// 三條路，由可靠到不可靠：
    ///   1. 用游標座標去命中儲物櫃格子的節點，直接讀該節點的 payload。
    ///      不依賴任何事件、也不依賴寫死的結構位移，是唯一穩的做法。
    ///   2. hover 事件當下存下來的格子。
    ///   3. hover 事件當下存下來的 AtkDragDropInterface 指標（事後讀，可能已失效）。
    /// </summary>
    private bool TryResolveHoveredSlot(long now, out InventoryType invType, out int slot)
        => TryResolveHoveredSlot(now, out invType, out slot, out _);

    private bool TryResolveHoveredSlot(long now, out InventoryType invType, out int slot, out string diag)
    {
        invType = default;
        slot = -1;

        // 1) 游標命中測試。
        if (TryResolveCompanyChestCellUnderCursor(out invType, out slot, out diag))
            return true;

        // 2) hover 當下 decode 好的格子。
        var cell = lastHoverCompanyChestCell;
        if (cell != null && now - cell.Value.SeenAtMs <= 5000 && cell.Value.Slot >= 0)
        {
            invType = cell.Value.Type;
            slot = cell.Value.Slot;
            return true;
        }

        // 3) 事後讀 hover 指標。
        var hover = lastHoverDdi;
        if (hover == null || now - hover.Value.SeenAtMs > 3000)
        {
            diag += hover == null ? "；hover 無記錄" : $"；hover 過期 {now - hover.Value.SeenAtMs}ms";
            return false;
        }

        try
        {
            var ddi = (AtkDragDropInterface*)hover.Value.DdiPtr;
            return TryGetSlotFromDragDropInterface(ddi, out invType, out slot) && slot >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 找出游標壓著的是儲物櫃哪一格。
    ///
    /// 不用節點 payload。實測三次分別讀到 37/30、37/5、0/34——Int1 一下 37 一下 0，
    /// 代表那個 payload container 根本沒被填（只有真的開始拖曳時才會填），讀到的是殘值。
    /// 上游的三條分頁解析也全部依賴 payload，所以在台服同樣全部失敗。
    ///
    /// 改用兩個不依賴 payload 的來源：
    ///   格號 ← 把整個 addon 的格子節點收集起來，照螢幕位置由上而下、由左而右排序，
    ///          游標壓著的那一格排第幾就是第幾格。儲物櫃沒有客戶端排序，顯示順序＝實體順序。
    ///   分頁 ← 讀分頁鈕（AtkComponentRadioButton）哪一顆是 checked。
    ///
    /// 最後一定要驗證 (分頁, 格號) 真的有東西才算數。
    /// </summary>
    private bool TryResolveCompanyChestCellUnderCursor(out InventoryType invType, out int slot, out string diag)
    {
        invType = default;
        slot = -1;
        diag = string.Empty;

        try
        {
            if (!TryGetVisibleAddon(FreeCompanyChestAddonName, out var addon, WideAddonSearchMaxIndex) || addon == null)
            {
                diag = "找不到 FreeCompanyChest addon";
                return false;
            }

            if (!TryGetClientCursorPos(out var mouseX, out var mouseY))
            {
                diag = "取不到游標座標";
                return false;
            }

            var scale = addon->Scale <= 0 ? 1f : addon->Scale;

            var allCells = new List<(float X, float Y, float W, float H)>();
            var radios = new List<(uint NodeId, bool Checked, bool Selected)>();
            CollectChestNodes(&addon->UldManager, scale, 0, allCells, radios);

            if (allCells.Count == 0)
            {
                diag = $"找不到任何格子節點（游標 {mouseX},{mouseY}）";
                return false;
            }

            // 實測收到 68 個格子節點，但儲物櫃只有 50 格——多出來的是別的東西。
            // 真正的格子尺寸一致且數量最多，用尺寸分組取最大那一群，避免排名被雜訊擠掉。
            var cells = FilterGridCells(allCells);

            // 游標壓著哪一格：有重疊時取面積最小的。
            var hit = -1;
            var hitArea = float.MaxValue;
            for (var i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                if (mouseX < c.X || mouseX > c.X + c.W || mouseY < c.Y || mouseY > c.Y + c.H)
                    continue;

                var area = c.W * c.H;
                if (area < hitArea)
                {
                    hitArea = area;
                    hit = i;
                }
            }

            if (hit < 0)
            {
                diag = $"游標({mouseX},{mouseY}) 不在任何格子上（共 {cells.Count} 格）";
                return false;
            }

            // 排名：由上而下、由左而右。同一列的 Y 會有些微差異，用格高的一半當容差分組。
            var rowTolerance = Math.Max(1f, cells[hit].H * 0.5f);
            var order = new List<int>(cells.Count);
            for (var i = 0; i < cells.Count; i++)
                order.Add(i);

            order.Sort((a, b) =>
            {
                var ra = (int)Math.Round(cells[a].Y / rowTolerance);
                var rb = (int)Math.Round(cells[b].Y / rowTolerance);
                if (ra != rb)
                    return ra.CompareTo(rb);
                return cells[a].X.CompareTo(cells[b].X);
            });

            var rank = order.IndexOf(hit);
            if (rank < 0)
            {
                diag = "格子排序失敗";
                return false;
            }

            var pageCandidates = new List<InventoryType>();
            foreach (var t in Enum.GetValues<InventoryType>())
            {
                if (IsCompanyChestType(t))
                    pageCandidates.Add(t);
            }

            pageCandidates.Sort(static (a, b) => ((int)a).CompareTo((int)b));

            if (TryResolveChestPageIndex(addon, radios, out var pageIndex, out var how) &&
                pageIndex >= 0)
            {
                if (pageIndex >= pageCandidates.Count)
                {
                    // 選到水晶或 Gil 的分頁，那裡沒有道具格可搬。
                    diag = $"目前開的是水晶／Gil 分頁（{how}），沒有道具格可搬";
                    return false;
                }

                var page = pageCandidates[pageIndex];
                if (TryGetItemInfo(page, rank, out var itemId, out _, out var qty) && itemId != 0 && qty != 0)
                {
                    invType = page;
                    slot = rank;
                    diag = $"{page}/{rank}＝{DescribeItem(itemId)}x{qty}（格子 {cells.Count}，分頁 {how}）";
                    return true;
                }

                diag = $"分頁 {(int)page}（{how}）的第 {rank} 格是空的（格子 {cells.Count}）";
                DumpCompanyChestState(addon, rank);
                DumpRadios(radios);
                return false;
            }

            diag = $"讀不到目前分頁（格子 {cells.Count}，排名 {rank}）";
            DumpCompanyChestState(addon, rank);
            DumpRadios(radios);
            return false;
        }
        catch (Exception ex)
        {
            diag = $"例外 {ex.GetType().Name}";
            Log.Warning(ex, "[QuickTransfer] 儲物櫃格子解析失敗。");
            return false;
        }
    }

    /// <summary>
    /// 從一堆節點裡挑出真正的格子：格子尺寸一致且數量最多，取尺寸相同的最大那一群。
    /// </summary>
    private static List<(float X, float Y, float W, float H)> FilterGridCells(
        List<(float X, float Y, float W, float H)> all)
    {
        var groups = new Dictionary<(int, int), List<(float X, float Y, float W, float H)>>();
        foreach (var c in all)
        {
            var key = ((int)Math.Round(c.W), (int)Math.Round(c.H));
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(c);
        }

        var best = all;
        var bestCount = 0;
        foreach (var kv in groups)
        {
            if (kv.Value.Count > bestCount)
            {
                bestCount = kv.Value.Count;
                best = kv.Value;
            }
        }

        return best;
    }

    /// <summary>
    /// 問出現在開在第幾個分頁。分頁鈕的 IsSelected 實測全是 false，所以多備幾條路，
    /// 哪條先給出答案就用哪條；全部失敗時呼叫端會把細節倒出來。
    /// </summary>
    private bool TryResolveChestPageIndex(
        AtkUnitBase* addon,
        List<(uint NodeId, bool Checked, bool Selected)> radios,
        out int pageIndex,
        out string how)
    {
        pageIndex = -1;
        how = string.Empty;

        // 分頁鈕必須照 node id 排序才對得上分頁順序。
        // 節點樹的走訪順序是倒的（實測收到 16,15,14,13,12,11,10），直接用收集順序當索引會錯頁。
        // 另外 7 顆 = 5 個道具分頁 + 水晶 + Gil，所以索引超出道具分頁數就是選到水晶/Gil，不是道具頁。
        var sorted = new List<(uint NodeId, bool Checked, bool Selected)>(radios);
        sorted.Sort(static (a, b) => a.NodeId.CompareTo(b.NodeId));

        // 1) 哪一顆 IsChecked。
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!sorted[i].Checked)
                continue;

            pageIndex = i;
            how = $"node {sorted[i].NodeId} → 第 {i + 1} 頁";
            return true;
        }

        // 2) 退而求其次，IsSelected。
        for (var i = 0; i < sorted.Count; i++)
        {
            if (!sorted[i].Selected)
                continue;

            pageIndex = i;
            how = $"node {sorted[i].NodeId} → 第 {i + 1} 頁 (IsSelected)";
            return true;
        }

        // 3) 直接照 node id 找分頁鈕。DailyRoutines 對儲物櫃分頁用的是 nodeId 10+index，
        //    這裡掃一段範圍而不是寫死 5 顆，順便容忍版本差異。
        for (uint nodeId = 8; nodeId <= 20; nodeId++)
        {
            try
            {
                var node = addon->UldManager.SearchNodeById(nodeId);
                if (node == null)
                    continue;

                var compNode = node->GetAsAtkComponentNode();
                if (compNode == null || compNode->Component == null)
                    continue;

                if (compNode->Component->GetComponentType() != ComponentType.RadioButton)
                    continue;

                var radio = (AtkComponentRadioButton*)compNode->Component;
                if (!radio->IsChecked && !radio->IsSelected)
                    continue;

                // 找到被選中的那顆之後，往回推它是第幾顆分頁鈕。
                var index = 0;
                for (uint probe = 8; probe < nodeId; probe++)
                {
                    var n2 = addon->UldManager.SearchNodeById(probe);
                    if (n2 == null)
                        continue;

                    var c2 = n2->GetAsAtkComponentNode();
                    if (c2 != null && c2->Component != null &&
                        c2->Component->GetComponentType() == ComponentType.RadioButton)
                        index++;
                }

                pageIndex = index;
                how = $"nodeId {nodeId} → 第 {index} 顆";
                return true;
            }
            catch
            {
                // 下一個
            }
        }

        return false;
    }

    private void DumpRadios(List<(uint NodeId, bool Checked, bool Selected)> radios)
    {
        try
        {
            var parts = new List<string>();
            foreach (var r in radios)
                parts.Add($"{r.NodeId}:{(r.Checked ? "C" : "-")}{(r.Selected ? "S" : "-")}");

            ChatGui.Print($"[QT/dump] 分頁鈕 {radios.Count} 顆 [{string.Join(" ", parts)}]");
        }
        catch
        {
            // 診斷失敗不影響主流程
        }
    }

    /// <summary>
    /// 解析失敗時把實際狀態倒到聊天視窗。這台機器的 dalamud.log 寫不進去，所以走 ChatGui。
    /// </summary>
    private void DumpCompanyChestState(AtkUnitBase* addon, int slotGuess)
    {
        try
        {
            var inv = InventoryManager.Instance();
            if (inv == null)
                return;

            var pageInfo = new List<string>();
            foreach (var t in Enum.GetValues<InventoryType>())
            {
                if (!IsCompanyChestType(t))
                    continue;

                var container = inv->GetInventoryContainer(t);
                if (container == null)
                {
                    pageInfo.Add($"{(int)t}:無");
                    continue;
                }

                var size = (int)container->Size;
                var used = 0;
                for (var i = 0; i < size; i++)
                {
                    var it = container->GetInventorySlot(i);
                    if (it != null && it->ItemId != 0)
                        used++;
                }

                pageInfo.Add($"{(int)t}:{used}/{size}");
            }

            ChatGui.Print($"[QT/dump] 分頁內容 {string.Join(" ", pageInfo)}");

            if (slotGuess >= 0)
            {
                var matches = new List<string>();
                foreach (var t in Enum.GetValues<InventoryType>())
                {
                    if (!IsCompanyChestType(t))
                        continue;

                    if (TryGetItemInfo(t, slotGuess, out var id, out _, out var q) && id != 0)
                        matches.Add($"{(int)t}→{DescribeItem(id)}x{q}");
                }

                ChatGui.Print($"[QT/dump] 第 {slotGuess} 格在各頁對應到：{(matches.Count > 0 ? string.Join(" ", matches) : "都是空的")}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] 儲物櫃狀態傾印失敗。");
        }
    }

    /// <summary>
    /// 走一次 addon 的節點樹，把格子（DragDrop／ListItemRenderer）的螢幕矩形，
    /// 以及分頁鈕的 checked 狀態，各收集成一份。
    /// </summary>
    private static void CollectChestNodes(
        AtkUldManager* uld,
        float scale,
        int depth,
        List<(float X, float Y, float W, float H)> cells,
        List<(uint NodeId, bool Checked, bool Selected)> radios)
    {
        if (uld == null || depth > 10)
            return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null || !node->IsVisible())
                continue;

            var compNode = node->GetAsAtkComponentNode();
            if (compNode == null || compNode->Component == null)
                continue;

            var component = compNode->Component;
            var type = component->GetComponentType();

            switch (type)
            {
                case ComponentType.DragDrop:
                case ComponentType.ListItemRenderer:
                {
                    var w = node->Width * scale;
                    var h = node->Height * scale;
                    if (w > 0 && h > 0)
                        cells.Add((node->ScreenX, node->ScreenY, w, h));
                    break;
                }

                case ComponentType.RadioButton:
                {
                    // IsSelected 在儲物櫃分頁鈕上實測全是 false，所以兩個旗標都收，哪個有反應用哪個。
                    try
                    {
                        var radio = (AtkComponentRadioButton*)component;
                        radios.Add((node->NodeId, radio->IsChecked, radio->IsSelected));
                    }
                    catch
                    {
                        radios.Add((node->NodeId, false, false));
                    }

                    break;
                }

                case ComponentType.List:
                {
                    var list = (AtkComponentList*)component;
                    for (var r = 0; r < 512; r++)
                    {
                        AtkComponentListItemRenderer* renderer;
                        try { renderer = list->GetItemRenderer(r); }
                        catch { break; }

                        if (renderer == null)
                            break;

                        var ownerNode = (AtkResNode*)renderer->OwnerNode;
                        if (ownerNode == null || !ownerNode->IsVisible())
                            continue;

                        var w = ownerNode->Width * scale;
                        var h = ownerNode->Height * scale;
                        if (w > 0 && h > 0)
                            cells.Add((ownerNode->ScreenX, ownerNode->ScreenY, w, h));
                    }

                    break;
                }
            }

            CollectChestNodes(&component->UldManager, scale, depth + 1, cells, radios);
        }
    }



    private bool StartBulkTransfer(InventoryType sourceType, uint sourceSlot, long now)
    {
        if (bulk.Active)
        {
            // 再按一次＝中止，比讓它跑完直覺。
            StopBulkTransfer("已中止。");
            return true;
        }

        if (!TryBuildBulkPlan(sourceType, sourceSlot, out var order, out var startIndex, out var destTypes, out var reason))
        {
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] 批次搬運未啟動：{reason} (src={sourceType} slot={sourceSlot})");
            return false;
        }

        var count = Configuration.BulkTransferCount;
        bulk = new BulkTransferState
        {
            Active = true,
            Order = order,
            OrderIndex = startIndex,
            DestTypes = destTypes,
            Remaining = count <= 0 ? int.MaxValue : count,
            Moved = 0,

            // 不要馬上送第一筆。右鍵選單此時還開著（要再過 50ms 才關），儲物櫃在
            // 選單還開著時會把操作回絕成「處理公會儲物櫃失敗」——實測第一筆固定失敗
            // 就是這個原因。等選單收掉、addon 狀態穩定再動手。
            NextAttemptAtMs = now + BulkStartDelayMs,
            // 整包背包最壞情況也就一兩分鐘；給寬一點但不要無上限。
            ExpiresAtMs = now + 180000,
        };

        // 這是使用者主動按下的新一輪操作，把先前累積的儲物櫃退避清掉，
        // 否則上一次的失敗會讓這次一開始就先乾等好幾秒。
        companyChestBusyHits = 0;
        companyChestBusyUntilMs = 0;

        var scope = count <= 0 ? "到最後一格" : $"{count} 格";

        // 把起點那格的道具名印出來：抓錯格子的話使用者一眼就看得出來，可以馬上再按一次中止。
        var startItem = TryGetItemInfo(sourceType, (int)sourceSlot, out var startItemId, out _, out var startQty) && startItemId != 0
            ? $"（{DescribeItem(startItemId)}x{startQty}）"
            : string.Empty;

        ChatGui.Print($"[QuickTransfer] 開始批次搬運：{DescribeContainer(sourceType)} 畫面順序第 {startIndex + 1} 格{startItem}起，{scope}。再按一次 Ctrl＋Shift＋右鍵可中止。");
        return true;
    }

    /// <summary>決定來源要掃哪些格子（含順序與起點）、目標可以放哪些容器。</summary>
    private bool TryBuildBulkPlan(
        InventoryType sourceType,
        uint sourceSlot,
        out (InventoryType Type, uint Slot)[] order,
        out int startIndex,
        out InventoryType[] destTypes,
        out string reason)
    {
        order = [];
        startIndex = -1;
        destTypes = [];
        reason = string.Empty;

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            reason = "InventoryManager null";
            return false;
        }

        InventoryType[] sourceTypes;

        if (IsPlayerInventoryType(sourceType))
        {
            // 背包 → 僱員／儲物櫃。兩個都開著的話以僱員優先（僱員視窗是模態的，比較不會誤判）。
            sourceTypes = FilterLoaded(inv, PlayerInventoryTypes);

            if (IsRetainerOpen())
            {
                destTypes = FilterLoaded(inv, RetainerInventoryTypes);
                if (destTypes.Length == 0)
                {
                    reason = "僱員背包尚未載入";
                    return false;
                }
            }
            else if (IsCompanyChestOpen() && Configuration.EnableCompanyChest)
            {
                destTypes = FilterLoaded(inv, GetCompanyChestInventoryTypes());
                if (destTypes.Length == 0)
                {
                    reason = "儲物櫃分頁尚未載入";
                    return false;
                }
            }
            else
            {
                reason = "沒有開著僱員或公會儲物櫃";
                return false;
            }
        }
        else if (IsRetainerType(sourceType))
        {
            sourceTypes = FilterLoaded(inv, RetainerInventoryTypes);
            destTypes = FilterLoaded(inv, PlayerInventoryTypes);
        }
        else if (IsCompanyChestType(sourceType))
        {
            // 儲物櫃只掃右鍵的那一頁：其他分頁沒開過就沒載入，硬掃會讀到空資料。
            // 儲物櫃也沒有客戶端排序，顯示順序就是實體順序。
            sourceTypes = [sourceType];
            destTypes = FilterLoaded(inv, PlayerInventoryTypes);
        }
        else
        {
            reason = $"不支援的來源容器 {sourceType}";
            return false;
        }

        if (sourceTypes.Length == 0 || destTypes.Length == 0)
        {
            reason = "容器尚未載入";
            return false;
        }

        var usedSorter = false;
        order = TryBuildDisplayOrder(sourceTypes, out usedSorter) ?? BuildRawOrder(inv, sourceTypes);
        startIndex = Array.IndexOf(order, (sourceType, sourceSlot));

        if (startIndex < 0)
        {
            // 顯示順序裡找不到這一格（sorter 落後了之類），退回實體順序再找一次。
            order = BuildRawOrder(inv, sourceTypes);
            startIndex = Array.IndexOf(order, (sourceType, sourceSlot));
            usedSorter = false;
        }

        if (startIndex < 0)
        {
            reason = "在來源容器裡找不到起點格";
            return false;
        }

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] 批次搬運順序：{(usedSorter ? "畫面順序 (ItemOrderModule)" : "實體順序")}, 共 {order.Length} 格, 起點 index={startIndex}");

        return true;
    }

    /// <summary>
    /// 從 <see cref="ItemOrderModule"/> 取出畫面顯示順序。
    ///
    /// sorter 的每個 entry 是 (Page, Slot)，實際容器 = sorter-&gt;InventoryType + Page。
    /// 「自動整理」改寫的就是這份順序，實體格號完全不動——這正是不能拿實體格號來掃的原因。
    ///
    /// 取不到就回傳 null，呼叫端會退回實體順序。
    /// </summary>
    private (InventoryType Type, uint Slot)[]? TryBuildDisplayOrder(InventoryType[] sourceTypes, out bool usedSorter)
    {
        usedSorter = false;

        try
        {
            var module = ItemOrderModule.Instance();
            if (module == null || sourceTypes.Length == 0)
                return null;

            ItemOrderModuleSorter* sorter = null;

            if (IsPlayerInventoryType(sourceTypes[0]))
            {
                sorter = module->InventorySorter;
            }
            else if (IsRetainerType(sourceTypes[0]))
            {
                // 每個僱員有自己的排序，用目前開著的那個。
                if (module->RetainerSorter.TryGetValue(module->ActiveRetainerId, out var retainerSorter, false))
                    sorter = retainerSorter.Value;
            }

            if (sorter == null)
                return null;

            var allowed = new HashSet<InventoryType>(sourceTypes);
            var baseType = (uint)sorter->InventoryType;
            var count = sorter->Items.LongCount;
            if (count <= 0)
                return null;

            var result = new List<(InventoryType, uint)>((int)count);
            for (long i = 0; i < count; i++)
            {
                var entry = sorter->Items[i].Value;
                if (entry == null)
                    continue;

                var type = (InventoryType)(baseType + entry->Page);
                if (!allowed.Contains(type))
                    continue;

                result.Add((type, entry->Slot));
            }

            if (result.Count == 0)
                return null;

            usedSorter = true;
            return result.ToArray();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] 讀取 ItemOrderModule 顯示順序失敗，退回實體順序。");
            return null;
        }
    }

    /// <summary>實體順序：容器依序、每個容器 slot 0..Size-1。沒有 sorter 時的退路。</summary>
    private static (InventoryType Type, uint Slot)[] BuildRawOrder(InventoryManager* inv, InventoryType[] sourceTypes)
    {
        var result = new List<(InventoryType, uint)>();
        foreach (var type in sourceTypes)
        {
            var container = inv->GetInventoryContainer(type);
            if (container == null)
                continue;

            for (var i = 0; i < container->Size; i++)
                result.Add((type, (uint)i));
        }

        return result.ToArray();
    }

    private static InventoryType[] FilterLoaded(InventoryManager* inv, InventoryType[] types)
    {
        var result = new List<InventoryType>(types.Length);
        foreach (var t in types)
        {
            if (IsContainerLoaded(inv, t))
                result.Add(t);
        }

        return result.ToArray();
    }

    private void ProcessBulkTransfer(long now)
    {
        if (!bulk.Active)
            return;

        if (now > bulk.ExpiresAtMs)
        {
            StopBulkTransfer("逾時中止。");
            return;
        }

        // 來源／目標的視窗被關掉就停手，不然會對著已經失效的容器亂搬。
        if (!IsRetainerOpen() && !IsCompanyChestOpen())
        {
            StopBulkTransfer("容器視窗已關閉。");
            return;
        }

        // 右鍵選單還開著就先等。儲物櫃在選單開啟期間會回絕操作（訊息是「處理公會儲物櫃失敗」），
        // 那不是真的失敗，純粹是時機不對。
        if (TryGetVisibleAddon(ContextMenuAddonName, out _))
        {
            bulk.NextAttemptAtMs = Math.Max(bulk.NextAttemptAtMs, now + 150);
            return;
        }

        // 數量視窗開著的時候什麼都別做，交給既有的自動確認流程。
        // 但不能無條件等下去：自動確認若被關掉、或這個視窗不是我們預期的那一種，
        // 就沒有人會去按確定，這裡會每個 frame 都 return，整個批次看起來就是卡死。
        if (TryGetVisibleAddon(InputNumericAddonName, out _))
        {
            if (bulk.InputNumericSeenAtMs == 0)
                bulk.InputNumericSeenAtMs = now;
            else if (now - bulk.InputNumericSeenAtMs > BulkInputNumericStallMs)
                StopBulkTransfer("數量視窗沒有被處理（自動確認沒作用？），中止。視窗留給你自己決定。");

            return;
        }

        bulk.InputNumericSeenAtMs = 0;

        // 儲物櫃被別人佔用時的退避，沿用既有的 busy 判斷。
        if (now < companyChestBusyUntilMs)
            return;

        if (bulk.WaitingForMove)
        {
            if (HasSlotChanged(bulk.PendingType, bulk.PendingSlot, bulk.PendingItemId, bulk.PendingQty))
            {
                bulk.WaitingForMove = false;
                bulk.StuckCount = 0;

                // 這一格可能只搬走一部分（併堆時被目標剩餘空間卡住），所以不要無條件往前跳：
                // 真的空了才算這一格搬完、才前進、才扣配額；沒空就對同一格再來一次。
                if (IsSlotEmpty(bulk.PendingType, bulk.PendingSlot))
                {
                    bulk.Moved++;
                    if (bulk.Remaining != int.MaxValue)
                        bulk.Remaining--;
                    AdvanceBulkSlot();
                }

                bulk.NextAttemptAtMs = now + Math.Max(50, Configuration.BulkTransferDelayMs);
                return;
            }

            if (now < bulk.NextAttemptAtMs)
                return;

            bulk.StuckCount++;

            // 公會儲物櫃：被擋是常態，退避拉長、次數放寬。
            var involvesChest = IsCompanyChestType(bulk.PendingType) ||
                                (bulk.DestTypes.Length > 0 && IsCompanyChestType(bulk.DestTypes[0]));
            var maxRetries = involvesChest ? BulkMaxStuckRetriesCompanyChest : BulkMaxStuckRetries;
            var retryDelay = involvesChest
                ? BulkThrottleBackoffMs
                : Math.Max(50, Configuration.BulkTransferDelayMs);

            if (bulk.StuckCount >= maxRetries)
            {
                // 這一格搬不動（綁定道具、裝備中、掛在市場、目標拒收…）。
                // 依使用者要求：直接中止，不要跳過繼續跑——跳過會讓人搞不清楚到底停在哪、
                // 也可能一路撞上一整排都搬不動的東西。
                var stuckName = DescribeItem(bulk.PendingItemId);
                StopBulkTransfer($"「{stuckName}」試了 {maxRetries} 次都搬不動，中止。");
                return;
            }

            // 還沒放棄：重送同一格。公會儲物櫃這裡會等比較久。
            bulk.WaitingForMove = false;
            bulk.NextAttemptAtMs = now + retryDelay;

            if (involvesChest && bulk.StuckCount == 1)
                ChatGui.Print($"[QuickTransfer] 儲物櫃回應慢（伺服器節流），放慢重試中……");

            return;
        }

        if (bulk.Remaining <= 0)
        {
            StopBulkTransfer("完成。");
            return;
        }

        if (now < bulk.NextAttemptAtMs)
            return;

        // 跳過空格（一次跳一批，不要一格一個 frame）。
        for (var scanned = 0; scanned < BulkEmptySlotScanPerTick; scanned++)
        {
            if (!TryGetCurrentBulkSource(out var srcType, out var srcSlot))
            {
                StopBulkTransfer("已掃到最後一格。");
                return;
            }

            if (!TryGetItemInfo(srcType, (int)srcSlot, out var itemId, out var isHq, out var qty) ||
                itemId == 0 || qty == 0)
            {
                AdvanceBulkSlot();
                continue;
            }

            var maxStack = GetItemStackSize(itemId);
            if (!TryFindBulkDestSlot(bulk.DestTypes, itemId, isHq, maxStack, out var dstType, out var dstSlot))
            {
                StopBulkTransfer($"目標已滿（{DescribeContainer(bulk.DestTypes.Length > 0 ? bulk.DestTypes[0] : srcType)} 沒有空位）。");
                return;
            }

            // 併堆或整堆搬都可能跳數量視窗，先把自動確認武裝起來。
            if (Configuration.AutoConfirmCompanyChestQuantity)
            {
                pendingCompanyChestNumericConfirmUntilMs = now + 2000;
                pendingCompanyChestNumericConfirmAttempts = 0;
                pendingCompanyChestNumericArmed = true;
                pendingNumericKind = PendingNumericKind.Move;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                ArmSuppressInputNumeric(now, 2000);
            }

            if (!TryCompanyChestMoveItem(srcType, srcSlot, dstType, dstSlot, keepAliveForInputNumeric: true))
            {
                StopBulkTransfer($"送出搬運失敗（「{DescribeItem(itemId)}」），中止。");
                return;
            }

            bulk.WaitingForMove = true;
            bulk.PendingType = srcType;
            bulk.PendingSlot = srcSlot;
            bulk.PendingItemId = itemId;
            bulk.PendingQty = qty;

            // 送出後要等多久才判定「沒反應」。儲物櫃走伺服器往返，250ms 太短會誤判。
            var involvesChestNow = IsCompanyChestType(srcType) || IsCompanyChestType(dstType);
            bulk.NextAttemptAtMs = now + (involvesChestNow
                ? Math.Max(1000, Configuration.BulkTransferDelayMs)
                : Math.Max(50, Configuration.BulkTransferDelayMs));

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] 批次搬運：#{bulk.OrderIndex + 1} {srcType}/{srcSlot} (item={itemId} qty={qty}) -> {dstType}/{dstSlot}");

            return;
        }
    }

    private bool TryGetCurrentBulkSource(out InventoryType srcType, out uint srcSlot)
    {
        srcType = default;
        srcSlot = 0;

        if (bulk.Order == null || bulk.OrderIndex < 0 || bulk.OrderIndex >= bulk.Order.Length)
            return false;

        (srcType, srcSlot) = bulk.Order[bulk.OrderIndex];
        return true;
    }

    private void AdvanceBulkSlot() => bulk.OrderIndex++;

    private static bool IsSlotEmpty(InventoryType type, uint slot)
        => !TryGetItemInfo(type, (int)slot, out var itemId, out _, out var qty) || itemId == 0 || qty == 0;

    private static bool HasSlotChanged(InventoryType type, uint slot, uint expectedItemId, uint expectedQty)
    {
        if (!TryGetItemInfo(type, (int)slot, out var itemId, out _, out var qty))
            return true;

        return itemId != expectedItemId || qty != expectedQty;
    }

    /// <summary>
    /// 找放得下的位置：先找同款未滿的堆疊（併堆最省格子），再找空格。
    /// </summary>
    private static bool TryFindBulkDestSlot(
        InventoryType[] destTypes,
        uint itemId,
        bool isHq,
        uint maxStack,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        var inv = InventoryManager.Instance();
        if (inv == null || destTypes.Length == 0)
            return false;

        if (maxStack > 1)
        {
            var bestFree = 0;
            foreach (var t in destTypes)
            {
                for (var i = 0; i < BulkSlotCap; i++)
                {
                    var it = inv->GetInventorySlot(t, i);
                    if (it == null)
                        break;

                    if (it->ItemId != itemId)
                        continue;

                    if (it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != isHq)
                        continue;

                    var qty = it->Quantity;
                    if (qty <= 0)
                        continue;

                    var free = (int)maxStack - qty;
                    if (free > bestFree)
                    {
                        bestFree = free;
                        destType = t;
                        destSlot = (uint)i;
                    }
                }
            }

            if (bestFree > 0)
                return true;
        }

        foreach (var t in destTypes)
        {
            for (var i = 0; i < BulkSlotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;

                if (it->ItemId != 0)
                    continue;

                destType = t;
                destSlot = (uint)i;
                return true;
            }
        }

        return false;
    }

    private void StopBulkTransfer(string why)
    {
        if (!bulk.Active)
            return;

        var moved = bulk.Moved;
        bulk = default;

        ChatGui.Print($"[QuickTransfer] 批次搬運結束：{why} 共搬了 {moved} 格。");
    }

    /// <summary>取道具名稱，純粹是為了讓中止訊息看得懂是卡在哪一個東西上。</summary>
    private static string DescribeItem(uint itemId)
    {
        if (itemId == 0)
            return "未知道具";

        try
        {
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (sheet != null && sheet.TryGetRow(itemId, out var row))
            {
                var name = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
            // 名字拿不到不是什麼大事，照樣回報。
        }

        return $"道具#{itemId}";
    }

    private static string DescribeContainer(InventoryType type)
    {
        if (IsPlayerInventoryType(type))
            return "背包";
        if (IsRetainerType(type))
            return "僱員背包";
        if (IsCompanyChestType(type))
            return "公會儲物櫃";
        if (IsSaddlebagType(type))
            return "陸行鳥鞍囊";
        if (IsArmouryType(type))
            return "兵裝庫";
        return type.ToString();
    }
}

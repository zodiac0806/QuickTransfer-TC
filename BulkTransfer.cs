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
    /// 在 FreeCompanyChest 這個 addon 裡，找出游標正壓著的儲物櫃格子，讀出它的 (容器, 格號)。
    ///
    /// 做法是把游標壓到的所有候選節點都收集起來、由小到大排序，再逐一讀 payload，
    /// 取第一個 payload 真的是 FreeCompanyPage 的。三個理由：
    ///   - 格子可能是 DragDrop 也可能是 ListItemRenderer（不同 addon 佈局不一樣），兩種都要收。
    ///   - 節點是巢狀的，外層容器也可能被游標壓到；取面積最小的才是真正的那一格。
    ///   - payload 不是每個節點都是 (InventoryType, slot)，有些帶的是別的東西
    ///     （實測撞到過 37/30，37 根本不是 InventoryType）。所以一定要驗證，不能拿了就用。
    ///
    /// 為什麼不用 AgentFreeCompanyChest 的 ContextInventoryType／ContextInventorySlot：
    /// API13 的 FFXIVClientStructs 沒有這個結構，要用就得自己寫死欄位位移。位移跟著客戶端版本走，
    /// 台服跟其他服不見得一樣，讀錯了會搬到完全不相干的格子——這種錯誤比「沒反應」危險得多。
    /// 節點 payload 沒有這個問題，是版本無關的。
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

            // 用 Win32 取游標，不要在非繪製時機呼叫 ImGui。上游已經有這個 helper。
            if (!TryGetClientCursorPos(out var mouseX, out var mouseY))
            {
                diag = "取不到游標座標";
                return false;
            }

            var scale = addon->Scale <= 0 ? 1f : addon->Scale;

            var candidates = new List<(nint Ddi, float Area)>();
            var inspected = 0;
            CollectCellsUnderCursor(&addon->UldManager, mouseX, mouseY, scale, 0, candidates, ref inspected);

            if (candidates.Count == 0)
            {
                diag = $"游標({mouseX},{mouseY}) scale={scale:F2} 未命中任何格子節點，掃過 {inspected} 個";
                return false;
            }

            // 由小到大：最內層、最小的那個才是真正的格子。
            candidates.Sort(static (a, b) => a.Area.CompareTo(b.Area));

            var seen = new List<string>();
            foreach (var (ddiPtr, _) in candidates)
            {
                if (!TryGetSlotFromDragDropInterface((AtkDragDropInterface*)ddiPtr, out var t, out var sl) || sl < 0)
                    continue;

                if (seen.Count < 6)
                    seen.Add($"{(int)t}/{sl}");

                if (!IsCompanyChestType(t))
                    continue;

                invType = t;
                slot = sl;
                diag = $"{t}/{sl}（候選 {candidates.Count}，掃過 {inspected}）";
                return true;
            }

            diag = $"游標({mouseX},{mouseY}) 命中 {candidates.Count} 個節點但沒有一個是儲物櫃格子，掃過 {inspected} 個，payload=[{string.Join(" ", seen)}]";
            return false;
        }
        catch (Exception ex)
        {
            diag = $"例外 {ex.GetType().Name}";
            Log.Warning(ex, "[QuickTransfer] 儲物櫃命中測試失敗。");
            return false;
        }
    }

    /// <summary>
    /// 遞迴走 uld 樹，把游標壓著的候選格子全部收集起來（不要一命中就收工，外層容器也會被壓到）。
    /// DragDrop 與 ListItemRenderer 兩種元件都收；碰到 List 就直接問它要 item renderer。
    /// </summary>
    private static void CollectCellsUnderCursor(
        AtkUldManager* uld,
        float mouseX,
        float mouseY,
        float scale,
        int depth,
        List<(nint Ddi, float Area)> candidates,
        ref int inspected)
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

            if (type is ComponentType.DragDrop or ComponentType.ListItemRenderer)
            {
                inspected++;
                TryAddCandidate(node, GetDdi(component, type), mouseX, mouseY, scale, candidates);
            }
            else if (type == ComponentType.List)
            {
                // List 的 item renderer 不一定掛在 UldManager 的 NodeList 上，直接跟 List 要。
                var list = (AtkComponentList*)component;
                for (var r = 0; r < 512; r++)
                {
                    AtkComponentListItemRenderer* renderer;
                    try { renderer = list->GetItemRenderer(r); }
                    catch { break; }

                    if (renderer == null)
                        break;

                    var ownerNode = renderer->OwnerNode;
                    if (ownerNode == null)
                        continue;

                    var resNode = (AtkResNode*)ownerNode;
                    if (!resNode->IsVisible())
                        continue;

                    inspected++;
                    TryAddCandidate(resNode, GetRendererDdi(renderer), mouseX, mouseY, scale, candidates);
                }
            }

            CollectCellsUnderCursor(&component->UldManager, mouseX, mouseY, scale, depth + 1, candidates, ref inspected);
        }
    }

    /// <summary>list item renderer 身上有兩個 DDI：內嵌的 DragDrop 子元件優先，沒有才用自己那個。</summary>
    private static AtkDragDropInterface* GetRendererDdi(AtkComponentListItemRenderer* renderer)
    {
        if (renderer == null)
            return null;

        if (renderer->DragDropComponent != null)
            return &renderer->DragDropComponent->AtkDragDropInterface;

        return &renderer->AtkDragDropInterface;
    }

    private static AtkDragDropInterface* GetDdi(AtkComponentBase* component, ComponentType type)
    {
        if (component == null)
            return null;

        return type switch
        {
            ComponentType.DragDrop => &((AtkComponentDragDrop*)component)->AtkDragDropInterface,
            ComponentType.ListItemRenderer => GetRendererDdi((AtkComponentListItemRenderer*)component),
            _ => null,
        };
    }

    private static void TryAddCandidate(
        AtkResNode* node,
        AtkDragDropInterface* ddi,
        float mouseX,
        float mouseY,
        float scale,
        List<(nint Ddi, float Area)> candidates)
    {
        if (node == null || ddi == null)
            return;

        var x = node->ScreenX;
        var y = node->ScreenY;
        var w = node->Width * scale;
        var h = node->Height * scale;

        if (w <= 0 || h <= 0)
            return;

        if (mouseX < x || mouseX > x + w || mouseY < y || mouseY > y + h)
            return;

        candidates.Add(((nint)ddi, w * h));
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
            NextAttemptAtMs = now,
            // 整包背包最壞情況也就一兩分鐘；給寬一點但不要無上限。
            ExpiresAtMs = now + 180000,
        };

        var scope = count <= 0 ? "到最後一格" : $"{count} 格";
        ChatGui.Print($"[QuickTransfer] 開始批次搬運：{DescribeContainer(sourceType)} 畫面順序第 {startIndex + 1} 格起，{scope}。再按一次 Ctrl＋Shift＋右鍵可中止。");
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
            if (bulk.StuckCount >= BulkMaxStuckRetries)
            {
                // 這一格搬不動（綁定道具、裝備中、掛在市場、目標拒收…）。
                // 依使用者要求：直接中止，不要跳過繼續跑——跳過會讓人搞不清楚到底停在哪、
                // 也可能一路撞上一整排都搬不動的東西。
                var stuckName = DescribeItem(bulk.PendingItemId);
                StopBulkTransfer($"「{stuckName}」搬不動，中止。");
                return;
            }

            bulk.WaitingForMove = false;
            bulk.NextAttemptAtMs = now + Math.Max(50, Configuration.BulkTransferDelayMs);
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
            bulk.NextAttemptAtMs = now + Math.Max(50, Configuration.BulkTransferDelayMs);

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

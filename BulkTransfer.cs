using System;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
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
/// 搬幾格由設定的「批次搬運格數」決定，0 = 一路搬到該容器最後一格。
/// 搬運本身走跟拖放同一支遊戲函式（RaptureAtkModule::HandleItemMove），
/// 所以遊戲自己的合法性檢查（綁定、裝備中、市場委託中…）全都還在，插件不會繞過去。
/// </summary>
public sealed unsafe partial class Plugin
{
    private struct BulkTransferState
    {
        public bool Active;

        /// <summary>來源容器清單，依序掃。</summary>
        public InventoryType[] SourceTypes;
        public int SourceTypeIndex;
        public int SourceSlot;

        /// <summary>目標容器清單，找位置時依序試。</summary>
        public InventoryType[] DestTypes;

        /// <summary>還能搬幾格；int.MaxValue 代表「到底為止」。</summary>
        public int Remaining;

        public int Moved;
        public int Failed;

        public long NextAttemptAtMs;
        public long ExpiresAtMs;

        /// <summary>已經送出搬運指令、正在等這一格真的變動。</summary>
        public bool WaitingForMove;
        public uint PendingItemId;
        public uint PendingQty;
        public InventoryType PendingType;
        public uint PendingSlot;
        public int StuckCount;
    }

    private BulkTransferState bulk;

    /// <summary>一次 tick 最多跳過幾個空格，免得整個背包都空的時候一格一格慢慢爬。</summary>
    private const int BulkEmptySlotScanPerTick = 40;

    /// <summary>同一格重試幾次還是沒動就放棄，避免無限卡。</summary>
    private const int BulkMaxStuckRetries = 6;

    private const int BulkSlotCap = 80;

    /// <summary>
    /// 從滑鼠最後停留的格子解析出容器與格號。公會儲物櫃的右鍵選單不經過 OpenForItemSlot，
    /// 只能靠這個。
    /// </summary>
    private bool TryResolveHoveredSlot(long now, out InventoryType invType, out int slot)
    {
        invType = default;
        slot = -1;

        var hover = lastHoverDdi;
        if (hover == null || now - hover.Value.SeenAtMs > 3000)
            return false;

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

    private bool StartBulkTransfer(InventoryType sourceType, uint sourceSlot, long now)
    {
        if (bulk.Active)
        {
            // 再按一次＝中止，比讓它跑完直覺。
            StopBulkTransfer("已中止。");
            return true;
        }

        if (!TryBuildBulkPlan(sourceType, out var sourceTypes, out var destTypes, out var reason))
        {
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] 批次搬運未啟動：{reason} (src={sourceType} slot={sourceSlot})");
            return false;
        }

        var startTypeIndex = Array.IndexOf(sourceTypes, sourceType);
        if (startTypeIndex < 0)
            return false;

        var count = Configuration.BulkTransferCount;
        bulk = new BulkTransferState
        {
            Active = true,
            SourceTypes = sourceTypes,
            SourceTypeIndex = startTypeIndex,
            SourceSlot = (int)sourceSlot,
            DestTypes = destTypes,
            Remaining = count <= 0 ? int.MaxValue : count,
            Moved = 0,
            Failed = 0,
            NextAttemptAtMs = now,
            // 整包背包最壞情況也就一兩分鐘；給寬一點但不要無上限。
            ExpiresAtMs = now + 180000,
        };

        var scope = count <= 0 ? "到最後一格" : $"{count} 格";
        ChatGui.Print($"[QuickTransfer] 開始批次搬運：{DescribeContainer(sourceType)} 第 {sourceSlot + 1} 格起，{scope}。再按一次 Ctrl＋Shift＋右鍵可中止。");
        return true;
    }

    /// <summary>決定來源要掃哪些容器、目標可以放哪些容器。</summary>
    private bool TryBuildBulkPlan(
        InventoryType sourceType,
        out InventoryType[] sourceTypes,
        out InventoryType[] destTypes,
        out string reason)
    {
        sourceTypes = [];
        destTypes = [];
        reason = string.Empty;

        var inv = InventoryManager.Instance();
        if (inv == null)
        {
            reason = "InventoryManager null";
            return false;
        }

        if (IsPlayerInventoryType(sourceType))
        {
            // 背包 → 僱員／儲物櫃。兩個都開著的話以僱員優先（僱員視窗是模態的，比較不會誤判）。
            sourceTypes = PlayerInventoryTypes;

            if (IsRetainerOpen())
            {
                destTypes = FilterLoaded(inv, RetainerInventoryTypes);
                if (destTypes.Length == 0)
                {
                    reason = "僱員背包尚未載入";
                    return false;
                }

                return true;
            }

            if (IsCompanyChestOpen() && Configuration.EnableCompanyChest)
            {
                destTypes = FilterLoaded(inv, GetCompanyChestInventoryTypes());
                if (destTypes.Length == 0)
                {
                    reason = "儲物櫃分頁尚未載入";
                    return false;
                }

                return true;
            }

            reason = "沒有開著僱員或公會儲物櫃";
            return false;
        }

        if (IsRetainerType(sourceType))
        {
            sourceTypes = FilterLoaded(inv, RetainerInventoryTypes);
            destTypes = FilterLoaded(inv, PlayerInventoryTypes);
            if (sourceTypes.Length == 0 || destTypes.Length == 0)
            {
                reason = "容器尚未載入";
                return false;
            }

            return true;
        }

        if (IsCompanyChestType(sourceType))
        {
            // 儲物櫃只掃右鍵的那一頁。其他分頁沒開過就沒載入，硬掃會讀到空資料。
            sourceTypes = [sourceType];
            destTypes = FilterLoaded(inv, PlayerInventoryTypes);
            if (destTypes.Length == 0)
            {
                reason = "背包尚未載入";
                return false;
            }

            return true;
        }

        reason = $"不支援的來源容器 {sourceType}";
        return false;
    }

    private static InventoryType[] FilterLoaded(InventoryManager* inv, InventoryType[] types)
    {
        var result = new System.Collections.Generic.List<InventoryType>(types.Length);
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
        if (TryGetVisibleAddon(InputNumericAddonName, out _))
            return;

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
                // 這一格搬不動（綁定道具、市場委託中、目標拒收…），跳過去繼續下一格。
                bulk.Failed++;
                bulk.WaitingForMove = false;
                bulk.StuckCount = 0;
                AdvanceBulkSlot();
                bulk.NextAttemptAtMs = now + Math.Max(50, Configuration.BulkTransferDelayMs);
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
                bulk.Failed++;
                AdvanceBulkSlot();
                bulk.NextAttemptAtMs = now + Math.Max(50, Configuration.BulkTransferDelayMs);
                return;
            }

            bulk.WaitingForMove = true;
            bulk.PendingType = srcType;
            bulk.PendingSlot = srcSlot;
            bulk.PendingItemId = itemId;
            bulk.PendingQty = qty;
            bulk.NextAttemptAtMs = now + Math.Max(50, Configuration.BulkTransferDelayMs);

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] 批次搬運：{srcType}#{srcSlot} (item={itemId} qty={qty}) -> {dstType}#{dstSlot}");

            return;
        }
    }

    private bool TryGetCurrentBulkSource(out InventoryType srcType, out uint srcSlot)
    {
        srcType = default;
        srcSlot = 0;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        while (bulk.SourceTypeIndex < bulk.SourceTypes.Length)
        {
            var type = bulk.SourceTypes[bulk.SourceTypeIndex];
            var container = inv->GetInventoryContainer(type);
            var size = container != null ? (int)container->Size : 0;

            if (bulk.SourceSlot < size)
            {
                srcType = type;
                srcSlot = (uint)bulk.SourceSlot;
                return true;
            }

            bulk.SourceTypeIndex++;
            bulk.SourceSlot = 0;
        }

        return false;
    }

    private void AdvanceBulkSlot() => bulk.SourceSlot++;

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
        var failed = bulk.Failed;
        bulk = default;

        var tail = failed > 0 ? $"，{failed} 格搬不動（已跳過）" : string.Empty;
        ChatGui.Print($"[QuickTransfer] 批次搬運結束：{why} 共搬了 {moved} 格{tail}。");
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

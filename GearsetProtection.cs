using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace QuickTransfer;

/// <summary>
/// [TC] 判斷一件裝備有沒有被任何配裝（裝備套）用到。
///
/// 用來支援「把某個部位的兵裝庫裡、沒被任何配裝納入的裝備取出來」——也就是清掉備品，
/// 但不會動到任何一套配裝需要的東西。
///
/// 比對一律用 base item id（HQ 的 id 帶 +1000000 偏移，配裝存的就是帶偏移的版本）。
/// </summary>
public sealed unsafe partial class Plugin
{
    /// <summary>
    /// 掃過所有配裝，收集「不可以被搬走」的 item id。
    ///
    /// 同時收 <c>ItemId</c> 與 <c>GlamourId</c>：投影用的那件物品也可能是實體道具，
    /// 漏掉會把它當備品搬走。這裡寧可多保護幾件——多留東西只是沒清乾淨，
    /// 少留東西是把人家配裝要用的裝備搬掉了，兩者代價差很多。
    /// </summary>
    private static HashSet<uint> BuildGearsetProtectedIds(out int gearsetCount)
    {
        var protectedIds = new HashSet<uint>();
        gearsetCount = 0;

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return protectedIds;

            for (var i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var entry = module->GetGearset(i);
                if (entry == null)
                    continue;

                gearsetCount++;

                foreach (var item in entry->Items)
                {
                    if (item.ItemId != 0)
                        protectedIds.Add(NormalizeItemId(item.ItemId));

                    if (item.GlamourId != 0)
                        protectedIds.Add(NormalizeItemId(item.GlamourId));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to read gearsets.");
        }

        return protectedIds;
    }

    /// <summary>
    /// 起動「取出未納入配裝的裝備」。範圍是右鍵的那一個兵裝庫部位的全部格子，
    /// 不是從點到的那一格開始——使用者要的是「清掉這個部位的備品」。
    /// </summary>
    private bool StartUnusedGearPull(InventoryType armouryType, long now)
    {
        if (bulk.Active)
        {
            StopBulkTransfer("Aborted.".L());
            return true;
        }

        if (!IsArmouryType(armouryType))
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        var destTypes = FilterLoaded(inv, PlayerInventoryTypes);
        if (destTypes.Length == 0)
            return false;

        InventoryType[] sourceTypes = [armouryType];
        var order = TryBuildDisplayOrder(sourceTypes, out _) ?? BuildRawOrder(inv, sourceTypes);
        if (order.Length == 0)
            return false;

        var protectedIds = BuildGearsetProtectedIds(out var gearsetCount);
        if (gearsetCount == 0)
        {
            // 一套配裝都讀不到時不要動手：那多半是還沒載入，而不是「真的沒有配裝」。
            // 這種狀態下整個部位都會被當成備品搬走，後果太大。
            ChatGui.Print("[QuickTransfer] No gearsets could be read; not touching the Armoury.".L());
            return false;
        }

        companyChestBusyHits = 0;
        companyChestBusyUntilMs = 0;

        bulk = new BulkTransferState
        {
            Active = true,
            Order = order,
            OrderIndex = 0,
            DestTypes = destTypes,
            Remaining = int.MaxValue,
            Moved = 0,
            UnusedGearOnly = true,
            ProtectedItemIds = protectedIds,
            NextAttemptAtMs = now + BulkStartDelayMs,
            ExpiresAtMs = now + 180000,
        };

        ChatGui.Print("[QuickTransfer] Pulling gear not used by any gearset out of ?? (?? gearsets checked). Press Alt+Shift+RClick again to abort."
            .L(DescribeContainer(armouryType), gearsetCount));
        return true;
    }

    /// <summary>這件裝備是否被任何配裝用到。</summary>
    private bool IsProtectedByGearset(uint itemId)
        => bulk.ProtectedItemIds != null && bulk.ProtectedItemIds.Contains(NormalizeItemId(itemId));
}

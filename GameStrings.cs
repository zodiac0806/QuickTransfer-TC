using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace QuickTransfer;

/// <summary>
/// [TC] 上游是用寫死的英文字串去比對右鍵選單項目（"Entrust to Retainer"、"Place in Armoury Chest" …），
/// 台服客戶端的選單是繁體中文，一個都對不上，整個插件會變成按了沒反應。
///
/// 這裡不是「把英文換成中文」——那樣只是把問題換一個語言重來一次。做法是從遊戲自己的
/// Addon／LogMessage 資料表，用 row id 取出**當前客戶端語言**的正式字串來比對，
/// 所以同一份程式碼在台服、國際服、日服都成立，玩家切語言也不會壞。
///
/// row id 是拿台服 7.20 的 exd（<GamePath>\game\sqpack）實際 dump 出來核對過的，
/// 註解裡的中文就是該 row 在台服的實際文字，日後要驗證直接對 Addon 資料表查該 row 即可。
/// </summary>
internal static class GameStrings
{
    // ---- Addon 資料表（右鍵選單項目文字）----
    private const uint AddonSplit                  = 92;   // 拆分
    private const uint AddonSell                   = 93;   // 出售
    private const uint AddonTrade                  = 95;   // 交易
    private const uint AddonEntrustToRetainer      = 97;   // 交給僱員保管
    private const uint AddonRetrieveFromRetainer   = 98;   // 從僱員處取回
    private const uint AddonAddToSaddlebag         = 881;  // 放入陸行鳥鞍囊
    private const uint AddonRemoveFromSaddlebag    = 887;  // 從陸行鳥鞍囊中取回
    private const uint AddonPlaceInArmouryChest    = 1387; // 放入兵裝庫
    private const uint AddonReturnToInventory      = 1388; // 放入背包
    private const uint AddonSort                   = 1389; // 自動整理
    private const uint AddonUndoSort               = 1390; // 撤銷整理
    private const uint AddonRemoveFromCompanyChest = 2950; // 取出（部隊儲物櫃）

    // ---- LogMessage 資料表（部隊儲物櫃的錯誤訊息）----
    private const uint LogCompanyChestActionFailed = 1861; // 處理公會儲物櫃失敗。
    private const uint LogCompanyChestStoreBusy    = 1873; // 無法保存道具，其他玩家正在使用儲物櫃。
    private const uint LogCompanyChestRemoveBusy   = 1874; // 無法取出道具，其他玩家正在使用儲物櫃。

    private static readonly Dictionary<ContextMenuHandler.AutoContextAction, uint> ActionRows = new()
    {
        [ContextMenuHandler.AutoContextAction.AddAllToSaddlebag]      = AddonAddToSaddlebag,
        [ContextMenuHandler.AutoContextAction.RemoveAllFromSaddlebag] = AddonRemoveFromSaddlebag,
        [ContextMenuHandler.AutoContextAction.PlaceInArmouryChest]    = AddonPlaceInArmouryChest,
        [ContextMenuHandler.AutoContextAction.ReturnToInventory]      = AddonReturnToInventory,
        [ContextMenuHandler.AutoContextAction.EntrustToRetainer]      = AddonEntrustToRetainer,
        [ContextMenuHandler.AutoContextAction.RetrieveFromRetainer]   = AddonRetrieveFromRetainer,
        [ContextMenuHandler.AutoContextAction.RemoveFromCompanyChest] = AddonRemoveFromCompanyChest,
        [ContextMenuHandler.AutoContextAction.Split]                  = AddonSplit,
        [ContextMenuHandler.AutoContextAction.Sort]                   = AddonSort,
        [ContextMenuHandler.AutoContextAction.Trade]                  = AddonTrade,
        [ContextMenuHandler.AutoContextAction.Sell]                   = AddonSell,
    };

    private static readonly Dictionary<uint, string?> AddonCache = new();
    private static readonly Dictionary<uint, string?> LogCache = new();

    private static string? GetAddon(uint rowId)
    {
        lock (AddonCache)
        {
            if (AddonCache.TryGetValue(rowId, out var cached))
                return cached;

            string? text = null;
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<Addon>();
                if (sheet != null && sheet.TryGetRow(rowId, out var row))
                {
                    var t = row.Text.ExtractText()?.Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                        text = t;
                }
            }
            catch (Exception ex)
            {
                // 讀不到就退回上游的英文啟發式，不要讓整個右鍵流程炸掉。
                try { Plugin.Log.Warning(ex, $"[QuickTransfer] 讀取 Addon#{rowId} 失敗。"); } catch { /* ignore */ }
            }

            AddonCache[rowId] = text;
            return text;
        }
    }

    private static string? GetLogMessage(uint rowId)
    {
        lock (LogCache)
        {
            if (LogCache.TryGetValue(rowId, out var cached))
                return cached;

            string? text = null;
            try
            {
                var sheet = Plugin.DataManager.GetExcelSheet<LogMessage>();
                if (sheet != null && sheet.TryGetRow(rowId, out var row))
                {
                    var t = row.Text.ExtractText()?.Trim();
                    if (!string.IsNullOrWhiteSpace(t))
                        text = t;
                }
            }
            catch (Exception ex)
            {
                try { Plugin.Log.Warning(ex, $"[QuickTransfer] 讀取 LogMessage#{rowId} 失敗。"); } catch { /* ignore */ }
            }

            LogCache[rowId] = text;
            return text;
        }
    }

    /// <summary>
    /// 選單文字是否等於該動作在當前客戶端語言下的正式字串。
    /// 回傳 false 不代表不匹配，只代表「用資料表比對不出來」，呼叫端要再走上游的英文啟發式。
    /// </summary>
    public static bool MatchesClientLabel(ContextMenuHandler.AutoContextAction action, string menuText)
    {
        if (!ActionRows.TryGetValue(action, out var rowId))
            return false;

        var expected = GetAddon(rowId);
        return expected != null && menuText.Trim().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>選單文字是否為「撤銷整理」（已經整理過了的訊號）。</summary>
    public static bool IsUndoSortLabel(string menuText)
    {
        var expected = GetAddon(AddonUndoSort);
        var t = menuText.Trim();
        return (expected != null && t.Equals(expected, StringComparison.OrdinalIgnoreCase)) ||
               t.Equals("Undo Sort", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>部隊儲物櫃「別人正在用／操作失敗」的訊息（含上游的英文版，兩邊都認）。</summary>
    public static bool IsCompanyChestBusyMessage(string text)
    {
        foreach (var rowId in new[] { LogCompanyChestActionFailed, LogCompanyChestStoreBusy, LogCompanyChestRemoveBusy })
        {
            var expected = GetLogMessage(rowId);
            if (expected != null && text.Contains(expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return text.Contains("Another player is using the chest", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unable to store item", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unable to complete company chest action", StringComparison.OrdinalIgnoreCase);
    }
}

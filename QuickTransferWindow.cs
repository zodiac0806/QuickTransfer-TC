using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace QuickTransfer;

public class QuickTransferWindow : Window, IDisposable
{
    private readonly Configuration _config;

    // [TC] 清單在 UI 上是純文字，存檔才轉成 id 陣列。打字打到一半不會是合法 id，
    //      所以不能每按一個鍵就往回寫設定。
    private string _excludeText;
    private string _includeText;

    public QuickTransferWindow(Configuration config)
        : base("QuickTransfer Settings".L() + "###QuickTransferConfig")
    {
        _config = config;
        _excludeText = FormatIds(config.BulkExcludeItemIds);
        _includeText = FormatIds(config.BulkIncludeItemIds);

        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(620, 620);
    }

    private static string FormatIds(List<uint> ids)
        => ids == null || ids.Count == 0 ? string.Empty : string.Join(", ", ids);

    /// <summary>
    /// 從自由文字抓出所有數字，這樣可以直接貼 SND 腳本那種帶註解的清單。
    /// </summary>
    private static List<uint> ParseIds(string text)
    {
        var result = new List<uint>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        var current = new StringBuilder();
        foreach (var ch in text + " ")
        {
            if (char.IsDigit(ch))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                if (uint.TryParse(current.ToString(), out var id) && id > 0 && !result.Contains(id))
                    result.Add(id);

                current.Clear();
            }
        }

        return result;
    }

    /// <summary>把清單裡前幾個 id 翻成名字，讓使用者確認自己填對了。</summary>
    private static string PreviewIds(List<uint> ids)
    {
        if (ids.Count == 0)
            return "(empty)".L();

        var names = ids.Take(4).Select(Plugin.DescribeItem);
        var more = ids.Count > 4 ? " " + "...?? total".L(ids.Count) : string.Empty;
        return string.Join(", ", names) + more;
    }

    public void Dispose()
    {
        // no-op
    }

    public override void Draw()
    {
            // Main settings
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "QuickTransfer Configuration".L());
            ImGui.Separator();
            
            // Enable/Disable
            var enabled = _config.Enabled;
            if (ImGui.Checkbox("Enabled".L() + "###Enabled", ref enabled))
            {
                _config.Enabled = enabled;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), _config.Enabled ? "(Active)".L() : "(Disabled)".L());
            
            ImGui.Spacing();
            
            // Debug mode
            var debugMode = _config.DebugMode;
            if (ImGui.Checkbox("Debug Mode".L() + "###DebugMode", ref debugMode))
            {
                _config.DebugMode = debugMode;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Logs to chat - for troubleshooting)".L());
            
            ImGui.Spacing();

            // Middle-click sort
            var mmbSort = _config.EnableMiddleClickSort;
            if (ImGui.Checkbox("Enable Middle-Click Sort".L() + "###EnableMiddleClickSort", ref mmbSort))
            {
                _config.EnableMiddleClickSort = mmbSort;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(MMB on an item: auto-select \"Sort\" when available)");

            // Company Chest
            var enableCompanyChest = _config.EnableCompanyChest;
            if (ImGui.Checkbox("Enable Company Chest (Free Company Chest)".L() + "###EnableCompanyChest", ref enableCompanyChest))
            {
                _config.EnableCompanyChest = enableCompanyChest;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Shift/Alt: deposit/withdraw while FC chest is open)".L());

            var mmbCompanyOrganize = _config.EnableCompanyChestMiddleClickOrganize;
            if (ImGui.Checkbox("Company Chest: Middle-Click Organize".L() + "###EnableCompanyChestMiddleClickOrganize", ref mmbCompanyOrganize))
            {
                _config.EnableCompanyChestMiddleClickOrganize = mmbCompanyOrganize;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(MMB: auto-stack + compact in FC chest)".L());

            var autoConfirmQty = _config.AutoConfirmCompanyChestQuantity;
            if (ImGui.Checkbox("Auto-confirm quantity prompts (Company Chest / Split)".L() + "###AutoConfirmCompanyChestQty", ref autoConfirmQty))
            {
                _config.AutoConfirmCompanyChestQuantity = autoConfirmQty;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 0.9f), "(Best effort; disable if it misbehaves)".L());

            // Vendor Quick Sell
            var enableVendorQuickSell = _config.EnableVendorQuickSell;
            if (ImGui.Checkbox("Enable Vendor Quick Sell".L() + "###EnableVendorQuickSell", ref enableVendorQuickSell))
            {
                _config.EnableVendorQuickSell = enableVendorQuickSell;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Shift+RClick: auto-select \"Sell\" when vendor is open)");

            var autoConfirmVendorSell = _config.AutoConfirmVendorSell;
            if (ImGui.Checkbox("Auto-confirm vendor sell dialogs".L() + "###AutoConfirmVendorSell", ref autoConfirmVendorSell))
            {
                _config.AutoConfirmVendorSell = autoConfirmVendorSell;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Auto-fill quantity, confirm \"How many?\", and click OK on \"Are you certain?\")");
            
            // [TC] 批次搬運
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "Bulk Transfer (Ctrl+Shift+RClick)".L());

            var enableBulk = _config.EnableBulkTransfer;
            if (ImGui.Checkbox("Enable Bulk Transfer".L() + "###EnableBulkTransfer", ref enableBulk))
            {
                _config.EnableBulkTransfer = enableBulk;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(starts at the slot you right-click)".L());

            ImGui.Text("Slots to move from the start point:".L());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var bulkCount = _config.BulkTransferCount;
            if (ImGui.InputInt("###BulkCount", ref bulkCount))
            {
                _config.BulkTransferCount = Math.Max(0, Math.Min(400, bulkCount));
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(0 = until the last slot)".L());

            ImGui.Text("Delay between slots (ms):".L());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var bulkDelay = _config.BulkTransferDelayMs;
            if (ImGui.InputInt("###BulkDelay", ref bulkDelay))
            {
                _config.BulkTransferDelayMs = Math.Max(50, Math.Min(2000, bulkDelay));
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 0.9f), "(too low gets throttled by the server)".L());

            ImGui.Spacing();
            ImGui.Text("Exclude list (move everything except these):".L());
            if (ImGui.InputTextMultiline("###BulkExclude", ref _excludeText, 4096, new Vector2(-1, 60)))
            {
                _config.BulkExcludeItemIds = ParseIds(_excludeText);
                _config.Save();
            }
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.9f), PreviewIds(_config.BulkExcludeItemIds));

            ImGui.Spacing();
            ImGui.Text("Include list (move only these; overrides the exclude list):".L());
            if (ImGui.InputTextMultiline("###BulkInclude", ref _includeText, 4096, new Vector2(-1, 60)))
            {
                _config.BulkIncludeItemIds = ParseIds(_includeText);
                _config.Save();
            }
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.9f), PreviewIds(_config.BulkIncludeItemIds));

            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "(Item IDs, separated by commas or newlines. Non-digits are ignored, so you can paste a script. HQ and NQ count as the same item.)".L());

            // Transfer cooldown
            ImGui.Spacing();
            ImGui.Text("Transfer Cooldown (ms):".L());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var cooldown = _config.TransferCooldownMs;
            if (ImGui.InputInt("###Cooldown", ref cooldown))
            {
                _config.TransferCooldownMs = Math.Max(0, Math.Min(1000, cooldown));
                _config.Save();
            }
            
            ImGui.Spacing();
            ImGui.Separator();
            
            // Instructions
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "How to Use:".L());
            ImGui.BulletText("Hold SHIFT and RIGHT-CLICK to use the open container's quick action".L());
            ImGui.BulletText("Hold CTRL and RIGHT-CLICK to use Armoury actions when a Saddlebag, Retainer, or Company Chest is open (Inventory ↔ Armoury)".L());
            ImGui.BulletText("Hold ALT and RIGHT-CLICK to split a stack in half (or remove half from Company Chest)".L());
            ImGui.BulletText("Hold CTRL+SHIFT and RIGHT-CLICK to bulk-transfer from that slot onward. Press again to abort.".L());
            ImGui.BulletText("Bulk direction follows whatever is open: Inventory to (Retainer > Company Chest > Saddlebag > Armoury), or back to Inventory.".L());
            ImGui.BulletText("Depositing into the Armoury routes each piece to its own compartment; non-gear is skipped.".L());
            ImGui.BulletText("Slots skipped by the include/exclude list do not count against the slot budget.".L());
            ImGui.BulletText("Inventory + Saddlebags: Inventory → \"Add All to Saddlebag\", Saddlebags → \"Remove All from Saddlebag\"".L());
            ImGui.BulletText("Armoury + Saddlebags: Armoury → \"Add All to Saddlebag\"".L());
            ImGui.BulletText("Inventory + Retainer: Inventory → \"Entrust to Retainer\", Retainer → \"Retrieve from Retainer\"".L());
            ImGui.BulletText("Armoury + Retainer: Armoury → \"Entrust to Retainer\", Retainer → \"Retrieve from Retainer\"".L());
            ImGui.BulletText("Retainer + Saddlebags: Retainer → \"Add All to Saddlebag\", Saddlebags → \"Entrust to Retainer\"".L());
            ImGui.BulletText("Inventory + Armoury (no special container): (Gear) Inventory → \"Place in Armoury Chest\", Armoury → \"Return to Inventory\"".L());
            ImGui.BulletText("Company Chest (FreeCompanyChest) open: Shift+RClick Inventory/Armoury deposits, Shift+RClick Company Chest withdraws (\"Remove\")".L());
            ImGui.BulletText("Vendor Shop open: Shift+RClick to auto-select \"Sell\"; enable \"Auto-confirm vendor sell\" to auto-fill quantity and confirm.".L());
            ImGui.BulletText("Middle-Click: Sort the clicked container when a \"Sort\" menu entry exists. In Company Chest, MMB runs an organize pass (stack + compact).".L());
            ImGui.BulletText("Use /qt or click 'Open Config' in plugin list to reopen this window".L());
            
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.4f, 1f), "Notes:".L());
            ImGui.BulletText("This uses the game's existing context menu options (no manual slot moving).".L());
            ImGui.BulletText("If an option isn't available for the clicked item, nothing happens.".L());
            ImGui.BulletText("If you tap Shift briefly, the action still triggers (it is captured when the menu opens).".L());
            ImGui.BulletText("For Company Chest deposits, this uses the same UI move function as drag+drop would.".L());
            ImGui.BulletText("Menu entries are matched against the game's own data sheets, so switching client language keeps working.".L());
            ImGui.BulletText("Bulk transfer stops and names the item when something cannot be moved.".L());
            ImGui.Spacing();
            
            // Save button
            if (ImGui.Button("Save & Close".L() + "###SaveClose"))
            {
                _config.Save();
                IsOpen = false;
            }
    }
}

using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace QuickTransfer;

public class QuickTransferWindow : Window, IDisposable
{
    private readonly Configuration _config;

    public QuickTransferWindow(Configuration config)
        : base("QuickTransfer 設定###QuickTransferConfig")
    {
        _config = config;

        SizeCondition = ImGuiCond.FirstUseEver;
        Size = new Vector2(560, 440);
    }

    public void Dispose()
    {
        // no-op
    }

    public override void Draw()
    {
            // 主要設定
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "QuickTransfer 設定");
            ImGui.Separator();

            // 啟用／停用
            var enabled = _config.Enabled;
            if (ImGui.Checkbox("啟用###Enabled", ref enabled))
            {
                _config.Enabled = enabled;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), _config.Enabled ? "（運作中）" : "（已停用）");

            ImGui.Spacing();

            // 除錯模式
            var debugMode = _config.DebugMode;
            if (ImGui.Checkbox("除錯模式###DebugMode", ref debugMode))
            {
                _config.DebugMode = debugMode;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（把過程輸出到聊天視窗，排查問題用）");

            ImGui.Spacing();

            // 中鍵整理
            var mmbSort = _config.EnableMiddleClickSort;
            if (ImGui.Checkbox("啟用中鍵自動整理###EnableMiddleClickSort", ref mmbSort))
            {
                _config.EnableMiddleClickSort = mmbSort;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（中鍵點道具：有「自動整理」就自動選）");

            // 公會儲物櫃
            var enableCompanyChest = _config.EnableCompanyChest;
            if (ImGui.Checkbox("啟用公會儲物櫃支援###EnableCompanyChest", ref enableCompanyChest))
            {
                _config.EnableCompanyChest = enableCompanyChest;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（儲物櫃開著時，Shift／Alt 直接存入或取出）");

            var mmbCompanyOrganize = _config.EnableCompanyChestMiddleClickOrganize;
            if (ImGui.Checkbox("公會儲物櫃：中鍵整理###EnableCompanyChestMiddleClickOrganize", ref mmbCompanyOrganize))
            {
                _config.EnableCompanyChestMiddleClickOrganize = mmbCompanyOrganize;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（中鍵：自動疊堆並壓縮空格）");

            var autoConfirmQty = _config.AutoConfirmCompanyChestQuantity;
            if (ImGui.Checkbox("自動確認數量視窗（公會儲物櫃／拆分）###AutoConfirmCompanyChestQty", ref autoConfirmQty))
            {
                _config.AutoConfirmCompanyChestQuantity = autoConfirmQty;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 0.9f), "（盡力而為，怪怪的就關掉）");

            // 商店快速出售
            var enableVendorQuickSell = _config.EnableVendorQuickSell;
            if (ImGui.Checkbox("啟用商店快速出售###EnableVendorQuickSell", ref enableVendorQuickSell))
            {
                _config.EnableVendorQuickSell = enableVendorQuickSell;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（商店開著時 Shift＋右鍵：自動選「出售」）");

            var autoConfirmVendorSell = _config.AutoConfirmVendorSell;
            if (ImGui.Checkbox("自動確認出售視窗###AutoConfirmVendorSell", ref autoConfirmVendorSell))
            {
                _config.AutoConfirmVendorSell = autoConfirmVendorSell;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（自動填數量並按下確定）");

            // 批次搬運
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "批次搬運（Ctrl＋Shift＋右鍵）");

            var enableBulk = _config.EnableBulkTransfer;
            if (ImGui.Checkbox("啟用批次搬運###EnableBulkTransfer", ref enableBulk))
            {
                _config.EnableBulkTransfer = enableBulk;
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（以右鍵那一格為起點，往後整批搬）");

            ImGui.Text("從起點往後搬幾格：");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var bulkCount = _config.BulkTransferCount;
            if (ImGui.InputInt("###BulkCount", ref bulkCount))
            {
                _config.BulkTransferCount = Math.Max(0, Math.Min(400, bulkCount));
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（0 = 一路搬到最後一格）");

            ImGui.Text("每格間隔（毫秒）：");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var bulkDelay = _config.BulkTransferDelayMs;
            if (ImGui.InputInt("###BulkDelay", ref bulkDelay))
            {
                _config.BulkTransferDelayMs = Math.Max(50, Math.Min(2000, bulkDelay));
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.85f, 0.75f, 0.45f, 0.9f), "（調太低會被伺服器當連點擋掉）");

            // 操作冷卻
            ImGui.Spacing();
            ImGui.Text("操作冷卻（毫秒）：");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            var cooldown = _config.TransferCooldownMs;
            if (ImGui.InputInt("###Cooldown", ref cooldown))
            {
                _config.TransferCooldownMs = Math.Max(0, Math.Min(1000, cooldown));
                _config.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 0.7f), "（防手滑連點造成重複搬運）");

            ImGui.Spacing();
            ImGui.Separator();

            // 使用說明
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1f, 1f), "怎麼用：");
            ImGui.BulletText("Shift＋右鍵：對目前開著的容器執行對應的搬運動作");
            ImGui.BulletText("Ctrl＋右鍵：鞍囊／僱員／公會儲物櫃開著時，優先做背包 ↔ 兵裝庫");
            ImGui.BulletText("Alt＋右鍵：把一堆道具對半拆分（在公會儲物櫃則是取出一半）");
            ImGui.BulletText("Ctrl＋Shift＋右鍵：以這一格為起點整批搬（背包→僱員／儲物櫃，或反向搬回背包）。跑到一半再按一次就中止。");
            ImGui.BulletText("背包＋陸行鳥鞍囊：背包 →「放入陸行鳥鞍囊」，鞍囊 →「從陸行鳥鞍囊中取回」");
            ImGui.BulletText("兵裝庫＋陸行鳥鞍囊：兵裝庫 →「放入陸行鳥鞍囊」");
            ImGui.BulletText("背包＋僱員：背包 →「交給僱員保管」，僱員 →「從僱員處取回」");
            ImGui.BulletText("兵裝庫＋僱員：兵裝庫 →「交給僱員保管」，僱員 →「從僱員處取回」");
            ImGui.BulletText("僱員＋陸行鳥鞍囊：僱員 →「放入陸行鳥鞍囊」，鞍囊 →「交給僱員保管」");
            ImGui.BulletText("背包＋兵裝庫（沒開其他容器時）：裝備 →「放入兵裝庫」，兵裝庫 →「放入背包」");
            ImGui.BulletText("公會儲物櫃開著：Shift＋右鍵背包／兵裝庫＝存入，Shift＋右鍵儲物櫃＝取出");
            ImGui.BulletText("商店開著：Shift＋右鍵自動選「出售」，開啟自動確認就會連數量與確認視窗一起處理");
            ImGui.BulletText("中鍵：該容器有「自動整理」就直接整理；在公會儲物櫃則是執行疊堆＋壓縮");
            ImGui.BulletText("用 /qt 或插件清單的「開啟設定」可以叫回這個視窗");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.4f, 1f), "注意事項：");
            ImGui.BulletText("這個插件只是幫你點遊戲本來就有的右鍵選單，不會自己搬格子。");
            ImGui.BulletText("如果該道具沒有對應的選單項目，按了就是沒反應，這是正常的。");
            ImGui.BulletText("Shift 只按一下就放開也沒關係，按鍵狀態是在選單開啟的瞬間就記下來的。");
            ImGui.BulletText("公會儲物櫃的存入走的是跟拖放同一個遊戲內部函式。");
            ImGui.BulletText("選單項目是用遊戲資料表比對的，切換客戶端語言一樣能用。");
            ImGui.BulletText("批次搬運走的也是拖放那支函式，遊戲自己的檢查都還在；搬不動的格子會跳過並回報。");
            ImGui.BulletText("批次搬運途中把僱員或儲物櫃視窗關掉，會立刻停手。");
            ImGui.Spacing();

            // 儲存
            if (ImGui.Button("儲存並關閉###SaveClose"))
            {
                _config.Save();
                IsOpen = false;
            }
    }
}

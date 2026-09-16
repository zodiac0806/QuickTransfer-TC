using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace QuickTransfer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public bool Enabled { get; set; } = true;
    // Default OFF (explicitly requested).
    public bool DebugMode { get; set; } = false;
    public int TransferCooldownMs { get; set; } = 200;

    public bool EnableMiddleClickSort { get; set; } = true;
    public bool EnableCompanyChestMiddleClickOrganize { get; set; } = true;

    public bool EnableCompanyChest { get; set; } = true;
    public bool AutoConfirmCompanyChestQuantity { get; set; } = true;
    public int CompanyChestCompartments { get; set; } = 3; // 3..5 (default game starts at 3)

    public bool EnableVendorQuickSell { get; set; } = true;
    public bool AutoConfirmVendorSell { get; set; } = true;

    // [TC] 批次搬運：Ctrl＋Shift＋右鍵，從點到的那一格開始往後整批搬。
    public bool EnableBulkTransfer { get; set; } = true;

    // 從起點往後搬幾格（是格數，不是道具數）。0 = 一路搬到該容器最後一格。
    public int BulkTransferCount { get; set; } = 0;

    // 每次搬運之間的間隔。太短會被伺服器當成連點擋下來。
    public int BulkTransferDelayMs { get; set; } = 250;

    // 排除清單：這些道具不搬（其他全搬）。
    public List<uint> BulkExcludeItemIds { get; set; } = [];

    // 指定清單：只搬這些道具。非空時排除清單不生效。
    public List<uint> BulkIncludeItemIds { get; set; } = [];

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider InteropProvider { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/qt";

    public Configuration Configuration { get; }

    private readonly WindowSystem windowSystem = new("QuickTransfer");
    private readonly QuickTransferWindow configWindow;

    private long lastActionTickMs;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredMenuClick;
    private (string AddonName, long EnqueuedAtMs, ModifierMode Mode)? pendingDeferredDefaultMenu;
    private (nint AgentPtr, nint AddonPtr, long EnqueuedAtMs)? pendingDeferredSortMenuClick;
    private long pendingMiddleClickSortUntilMs;
    private (InventoryType Type, int Slot, uint AddonId, long EnqueuedAtMs)? pendingMiddleClickSortRequest;
    private long lastMiddleClickSortMs;
    private long lastReceiveEventDebugLogMs;
    private long lastFcChestTabUnmappedLogMs;
    private bool debugPrintedReceiveEventHook;
    private (nint DdiPtr, uint AddonId, long SeenAtMs)? lastHoverDdi;
    private string lastHoverAddonName = string.Empty;
    private (string AddonName, uint AddonId, long SeenAtMs)? lastHoverAddon;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastHoverCompanyChestPage;

    // [TC] 上游在 hover 當下只 decode 了「分頁」就把格號丟掉（out _）。
    //      批次搬運需要格號，而 AtkDragDropInterface 的 payload 事後再讀可能已經失效
    //      （右鍵開選單的過程本身就會把 hover 狀態清掉），所以這裡比照辦理，趁新鮮一起存。
    private (InventoryType Type, int Slot, uint AddonId, long SeenAtMs)? lastHoverCompanyChestCell;
    private (InventoryType Page, uint AddonId, long SeenAtMs)? lastSelectedCompanyChestPage;
    private int companyChestSelectedTabAtkValueIndex = -1;
    private readonly Dictionary<int, Dictionary<int, InventoryType>> companyChestSelectedTabCandidates = new();
    private long companyChestBusyUntilMs;
    private int companyChestBusyHits;
    private long lastCompanyChestOrganizeSkipLogMs;
    private string lastCompanyChestOrganizeSkipReason = string.Empty;
    private bool lastVkLButtonDown;
    private bool lastVkRButtonDown;
    private bool lastVkMButtonDown;
    private bool lastVkX1ButtonDown;
    private bool lastVkX2ButtonDown;
    private long lastCursorHitTestLogMs;
    private const int WideAddonSearchMaxIndex = 50;

    // Cache the "a4" parameter observed when the game opens inventory context menus.
    // Some UIs (notably ArmouryBoard on some builds) appear to require a non-zero a4 to actually populate items.
    private readonly Dictionary<(uint OwnerAddonId, uint InventoryType), int> observedContextA4 = new();
    private long lastObservedA4LogMs;

    // Cache a known-good (type, slot, a4) that successfully produced a populated inventory context menu for a given addon.
    // This allows MMB to "Sort" even when hover payloads are weird/un-decodable, because Sort applies to the container.
    private readonly Dictionary<uint, (InventoryType Type, int Slot, int A4)> lastGoodContextTargetByAddonId = new();

    // Win32: reliable mouse button state (works even when Dalamud KeyState doesn't report mouse buttons).
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    // Heuristic: ignore "pointers" that look like 32-bit values.
    // Real UI heap pointers in a 64-bit process are typically well above 4GB.
    private const long MinLikelyPointer = 0x1_0000_0000; // 4GB

    // ArmouryBoardIndexToType moved to DragDropHelpers.cs

    private static readonly InventoryType[] PlayerInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] SaddlebagInventoryTypes =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] RetainerInventoryTypes =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static bool IsVkDown(int vKey)
    {
        try
        {
            return (GetAsyncKeyState(vKey) & 0x8000) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClientCursorPos(out short x, out short y)
    {
        x = 0;
        y = 0;
        try
        {
            if (!GetCursorPos(out var p))
                return false;

            var hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                return false;

            if (!ScreenToClient(hwnd, ref p))
                return false;

            if (p.X < short.MinValue || p.X > short.MaxValue || p.Y < short.MinValue || p.Y > short.MaxValue)
                return false;

            x = (short)p.X;
            y = (short)p.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryUpdateLastHoverAddonFromCollisionManager(long now)
    {
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->AtkCollisionManager == null)
                return false;

            var hit = stage->AtkCollisionManager->IntersectingAddon;
            if (hit == null || hit->Id == 0)
                return false;

            // We can't reliably compare addon pointers here:
            // - The collision manager can report child addons/overlays
            // - Some users have addon indices > 6
            //
            // Instead, map via Id/HostId/ParentId to a known *owner* addon window.
            bool TryGetVisibleAddonId(string name, out uint id)
            {
                id = 0;
                try
                {
                    if (TryGetVisibleAddon(name, out var a, WideAddonSearchMaxIndex) && a != null && a->Id != 0)
                    {
                        id = a->Id;
                        return true;
                    }
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            var visibleById = new Dictionary<uint, string>(capacity: 16);
            void AddVisible(string name)
            {
                if (TryGetVisibleAddonId(name, out var id) && id != 0)
                {
                    // Don't overwrite an existing mapping. This prevents rare mis-labeling if an alias query
                    // accidentally returns an unexpected addon that reuses an id already mapped earlier.
                    if (!visibleById.ContainsKey(id))
                        visibleById[id] = name;
                }
            }

            AddVisible("Inventory");
            AddVisible("InventoryBuddy");
            AddVisible("InventoryBuddy2");
            AddVisible("RetainerGrid0");
            AddVisible("RetainerGrid");
            AddVisible("RetainerSellList");
            AddVisible(FreeCompanyChestAddonName);
            foreach (var n in ArmouryAddonNames)
                AddVisible(n);

            var hitId = (uint)hit->Id;
            var hostId = (uint)hit->HostId;
            var parentId = (uint)hit->ParentId;

            uint ownerId = 0;
            string ownerName = string.Empty;
            string ownerSource = string.Empty;

            bool Pick(uint id)
            {
                if (id == 0)
                    return false;
                if (!visibleById.TryGetValue(id, out var n))
                    return false;
                ownerId = id;
                ownerName = n;
                ownerSource = "visible";
                return true;
            }

            // Prefer direct hit, then host, then parent.
            if (!Pick(hitId) && !Pick(hostId) && !Pick(parentId))
            {
                static string InferOwnerNameFromInvType(InventoryType t)
                {
                    if (IsPlayerInventoryType(t))
                        return "Inventory";
                    if (IsSaddlebagType(t))
                        return "InventoryBuddy";
                    if (IsArmouryType(t))
                        return "ArmouryBoard";
                    if (IsCompanyChestType(t))
                        return FreeCompanyChestAddonName;
                    if (IsRetainerType(t))
                        return "RetainerGrid0";
                    return string.Empty;
                }

                bool PickFromLastGood(uint id)
                {
                    if (id == 0)
                        return false;
                    if (!lastGoodContextTargetByAddonId.TryGetValue(id, out var good))
                        return false;

                    var inferred = InferOwnerNameFromInvType(good.Type);
                    if (string.IsNullOrEmpty(inferred))
                        return false;

                    ownerId = id;
                    ownerName = inferred;
                    ownerSource = "lastGood";
                    return true;
                }

                // If GameGui can't see the owner window (common for Inventory), fall back to previously observed "good" targets.
                if (!PickFromLastGood(hitId) && !PickFromLastGood(hostId) && !PickFromLastGood(parentId))
                {
                    // Heuristic: Inventory's owner addon id is commonly 17, while collision hits are child ids (e.g. 108/110)
                    // with HostId/ParentId pointing at 17. Prefer that to make MMB Inventory sort work even without a prior RClick.
                    const uint inventoryOwnerId = 17;
                    if (hostId == inventoryOwnerId || parentId == inventoryOwnerId)
                    {
                        ownerId = inventoryOwnerId;
                        ownerName = "Inventory";
                        ownerSource = "heuristic17";
                    }
                    else
                    {
                        // If we couldn't map to a known owner addon, still log what we saw to help diagnose.
                        if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
                        {
                            lastCursorHitTestLogMs = now;
                            Log.Information($"[QuickTransfer] (MMB) CollisionManager hit addonId={hitId} hostId={hostId} parentId={parentId} (unmapped). Visible owners=[{string.Join(", ", visibleById.Select(kv => $"{kv.Value}:{kv.Key}"))}] lastGoodOwnerIds=[{string.Join(", ", lastGoodContextTargetByAddonId.Keys.Take(24))}]");
                        }
                        return false;
                    }
                }
            }

            lastHoverAddon = (ownerName, ownerId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) CollisionManager picked addon '{ownerName}' (ownerAddonId={ownerId}, hitAddonId={hitId}, source={ownerSource}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryUpdateLastHoverAddonFromCursorHitTest(long now)
    {
        try
        {
            // Prefer the game's own collision manager; it already knows what addon is under the cursor.
            if (TryUpdateLastHoverAddonFromCollisionManager(now))
                return true;

            if (!TryGetClientCursorPos(out var x, out var y))
                return false;

            AtkUnitBase* best = null;
            string bestName = string.Empty;
            uint bestId = 0;
            uint bestDepth = 0;
            ushort bestDraw = 0;

            void Consider(string name, AtkUnitBase* a)
            {
                if (a == null)
                    return;

                try
                {
                    if (!a->IsVisible || !a->IsReady)
                        return;

                    if (!a->CheckWindowCollisionAtCoords(x, y))
                        return;

                    var depth = a->DepthLayer;
                    var draw = a->DrawOrderIndex;
                    if (best == null || depth > bestDepth || (depth == bestDepth && draw > bestDraw))
                    {
                        best = a;
                        bestName = name ?? string.Empty;
                        bestId = a->Id;
                        bestDepth = depth;
                        bestDraw = draw;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (TryGetVisibleAddon("Inventory", out var inv) && inv != null)
                Consider("Inventory", inv);

            if (TryGetVisibleAddon("InventoryBuddy", out var sb) && sb != null)
                Consider("InventoryBuddy", sb);
            if (TryGetVisibleAddon("InventoryBuddy2", out var sb2) && sb2 != null)
                Consider("InventoryBuddy2", sb2);

            if (TryGetVisibleAddon("RetainerGrid0", out var rg0, WideAddonSearchMaxIndex) && rg0 != null)
                Consider("RetainerGrid0", rg0);
            if (TryGetVisibleAddon("RetainerGrid", out var rg, WideAddonSearchMaxIndex) && rg != null)
                Consider("RetainerGrid", rg);
            if (TryGetVisibleAddon("RetainerSellList", out var rsl, WideAddonSearchMaxIndex) && rsl != null)
                Consider("RetainerSellList", rsl);

            if (TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex) && fcc != null)
                Consider(FreeCompanyChestAddonName, fcc);

            foreach (var n in ArmouryAddonNames)
            {
                if (TryGetVisibleAddon(n, out var ab) && ab != null)
                    Consider(n, ab);
            }

            if (best == null || bestId == 0 || string.IsNullOrEmpty(bestName))
                return false;

            lastHoverAddon = (bestName, bestId, now);

            if (Configuration.DebugMode && now - lastCursorHitTestLogMs >= 1000)
            {
                lastCursorHitTestLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) Cursor hit-test picked addon '{bestName}' (addonId={bestId}) at ({x},{y}).");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Use DragDropHelpers
    private static bool TryGetDragDropInterfaceFromReceiveEvent(
        AddonArgs args,
        AddonReceiveEventArgs recv,
        AtkEventType eventType,
        AtkEventData* eventData,
        out uint addonId,
        out AtkDragDropInterface* ddi)
        => DragDropHelpers.TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out addonId, out ddi);

    private static bool TryGetSlotFromDragDropInterface(
        AtkDragDropInterface* ddi,
        out InventoryType invType,
        out int slot)
        => DragDropHelpers.TryGetSlotFromDragDropInterface(ddi, out invType, out slot);

    private static bool TryGetSlotFromDragDropInterfaceForAddon(
        AtkDragDropInterface* ddi,
        string addonName,
        uint addonId,
        out InventoryType invType,
        out int slot,
        out int rawInt1,
        out int rawInt2,
        out uint rawFlags)
        => DragDropHelpers.TryGetSlotFromDragDropInterfaceForAddon(ddi, addonName, addonId, out invType, out slot, out rawInt1, out rawInt2, out rawFlags);

    private static int PickContextMenuSlot(InventoryType type, int preferredSlot)
        => DragDropHelpers.PickContextMenuSlot(type, preferredSlot);

    private static bool TryResolveTargetFromWeirdPayload(
        ReadOnlySpan<InventoryType> containers,
        int rawInt1,
        int rawInt2,
        short refIdx,
        out InventoryType type,
        out int slot)
    {
        type = default;
        slot = -1;

        try
        {
            if (containers.Length == 0)
                return false;

            var inv = InventoryManager.Instance();
            if (inv == null)
                return false;

            // Try a few plausible slot candidates first (fast path).
            // Observed weird payloads often still include a real slot index in one of these fields.
            var candidates = new List<int>(capacity: 4) { rawInt2, rawInt1, refIdx };
            foreach (var s in candidates.Distinct())
            {
                if (s < 0 || s > 500)
                    continue;

                foreach (var t in containers)
                {
                    var it = inv->GetInventorySlot(t, s);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = s;
                        return true;
                    }
                }
            }

            // Last resort: pick the first container that has any items,
            // and then pick its first non-empty slot.
            foreach (var t in containers)
            {
                var c = inv->GetInventoryContainer(t);
                if (c == null || !c->IsLoaded || c->Size <= 0)
                    continue;

                for (var i = 0; i < c->Size; i++)
                {
                    var it = c->GetInventorySlot(i);
                    if (it != null && it->ItemId != 0)
                    {
                        type = t;
                        slot = i;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private bool TryQueueMiddleClickSortFromVisibleWindows(long now)
    {
        try
        {
            // If multiple inventory windows are open, we can't know which one the cursor is over without a hover DDI.
            // In that case, refuse and require hover capture.
            var visibleCount = 0;

            InventoryType chosenType = default;
            var chosenSlot = -1;
            uint chosenAddonId = 0;

            // ArmouryBoard
            if (TryGetVisibleAddon("ArmouryBoard", out var ab, WideAddonSearchMaxIndex) && ab != null)
            {
                if (TryResolveTargetFromWeirdPayload(DragDropHelpers.ArmouryBoardIndexToType, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = ab->Id;
                }
            }

            // Saddlebags
            if (TryGetVisibleAddon("InventoryBuddy", out var sb, WideAddonSearchMaxIndex) && sb != null)
            {
                if (TryResolveTargetFromWeirdPayload(SaddlebagInventoryTypes, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = sb->Id;
                }
            }
            else if (TryGetVisibleAddon("InventoryBuddy2", out var sb2, WideAddonSearchMaxIndex) && sb2 != null)
            {
                if (TryResolveTargetFromWeirdPayload(SaddlebagInventoryTypes, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = sb2->Id;
                }
            }

            // Player inventory
            if (TryGetVisibleAddon("Inventory", out var inv, WideAddonSearchMaxIndex) && inv != null)
            {
                if (TryResolveTargetFromWeirdPayload(PlayerInventoryTypes, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = inv->Id;
                }
            }

            // Retainer inventory
            if (TryGetVisibleAddon("RetainerGrid0", out var rg0, WideAddonSearchMaxIndex) && rg0 != null)
            {
                if (TryResolveTargetFromWeirdPayload(RetainerInventoryTypes, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rg0->Id;
                }
            }
            else if (TryGetVisibleAddon("RetainerGrid", out var rg, WideAddonSearchMaxIndex) && rg != null)
            {
                if (TryResolveTargetFromWeirdPayload(RetainerInventoryTypes, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rg->Id;
                }
            }
            else if (TryGetVisibleAddon("RetainerSellList", out var rsl, WideAddonSearchMaxIndex) && rsl != null)
            {
                if (TryResolveTargetFromWeirdPayload(RetainerInventoryTypes, -1, -1, -1, out var t, out var s))
                {
                    visibleCount++;
                    chosenType = t;
                    chosenSlot = s;
                    chosenAddonId = rsl->Id;
                }
            }

            // Free Company Chest (no native Sort context menu; MMB triggers our organize pass)
            if (TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex) && fcc != null)
            {
                var lp = lastHoverCompanyChestPage;
                if (lp != null && lp.Value.AddonId == fcc->Id && now - lp.Value.SeenAtMs <= 20000 && IsCompanyChestType(lp.Value.Page))
                {
                    visibleCount++;
                    chosenType = lp.Value.Page;
                    chosenSlot = 0;
                    chosenAddonId = fcc->Id;
                }
                else
                {
                    var sp = lastSelectedCompanyChestPage;
                    if (sp != null && sp.Value.AddonId == fcc->Id && now - sp.Value.SeenAtMs <= 20000 && IsCompanyChestType(sp.Value.Page))
                    {
                        visibleCount++;
                        chosenType = sp.Value.Page;
                        chosenSlot = 0;
                        chosenAddonId = fcc->Id;
                    }
                    else
                    {
                    var pages = GetCompanyChestInventoryTypes();
                    if (pages.Length > 0)
                    {
                        visibleCount++;
                        chosenType = pages[0];
                        chosenSlot = 0;
                        chosenAddonId = fcc->Id;
                    }
                    }
                }
            }

            if (visibleCount != 1 || chosenAddonId == 0 || chosenSlot < 0)
                return false;

            var openSlot = PickContextMenuSlot(chosenType, chosenSlot);
            pendingMiddleClickSortRequest = (chosenType, openSlot, chosenAddonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) No hover DDI; bootstrapped from visible window: {chosenType} slot={openSlot} addonId={chosenAddonId}");

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryQueueMiddleClickSortFromLastHoverAddon(long now)
    {
        try
        {
            var h = lastHoverAddon;
            if (h == null || now - h.Value.SeenAtMs > 20000)
                return false;

            var addonName = h.Value.AddonName ?? string.Empty;
            var addonId = h.Value.AddonId;

            ReadOnlySpan<InventoryType> containers = default;
            if (addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase))
                containers = PlayerInventoryTypes;
            else if (addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) || addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase))
                containers = SaddlebagInventoryTypes;
            else if (addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                     addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                     addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase))
                containers = RetainerInventoryTypes;
            else if (addonName.Equals(FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
            {
                const long companyChestTabMaxAgeMs = 180000; // 3 minutes

                // FC Chest has no native "Sort"; MMB triggers our organize pass.
                // Run only on the currently selected tab, approximated as the most recently hovered/clicked FreeCompanyPage payload.

                // First preference: read the currently displayed page directly from the addon via a payload probe.
                // This avoids relying on tab ButtonClick params, which vary across clients/builds.
                if (TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex) &&
                    fcc != null &&
                    fcc->Id == addonId &&
                    TryResolveCompanyChestPageFromAddon(fcc, out var curPage) &&
                    IsCompanyChestType(curPage))
                {
                    pendingMiddleClickSortRequest = (curPage, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Resolved active Company Chest tab from payload: {curPage} (addonId={addonId})");
                    return true;
                }
                else if (Configuration.DebugMode && TryGetVisibleAddon(FreeCompanyChestAddonName, out var fccDbg, WideAddonSearchMaxIndex) && fccDbg != null && fccDbg->Id == addonId)
                {
                    // Diagnostic: we expected to be able to infer the active page from visible payloads, but couldn't.
                    // This helps identify whether the probe is failing entirely or just returning a non-page payload.
                    Log.Information("[QuickTransfer] (MMB) Company Chest payload tab probe failed; falling back to hover/selected tab.");
                }

                var lp = lastHoverCompanyChestPage;
                if (lp != null && lp.Value.AddonId == addonId && now - lp.Value.SeenAtMs <= companyChestTabMaxAgeMs && IsCompanyChestType(lp.Value.Page))
                {
                    pendingMiddleClickSortRequest = (lp.Value.Page, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using last-hovered Company Chest tab: {lp.Value.Page} slot=0 addonId={addonId}");
                    return true;
                }

                var sp = lastSelectedCompanyChestPage;
                if (sp != null && sp.Value.AddonId == addonId && now - sp.Value.SeenAtMs <= companyChestTabMaxAgeMs && IsCompanyChestType(sp.Value.Page))
                {
                    pendingMiddleClickSortRequest = (sp.Value.Page, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using selected Company Chest tab: {sp.Value.Page} slot=0 addonId={addonId}");
                    return true;
                }

                if (TryResolveCompanyChestSelectedPageFromAtkValues(addonId, out var atkPage))
                {
                    pendingMiddleClickSortRequest = (atkPage, 0, addonId, now);
                    pendingMiddleClickSortUntilMs = now + 1500;
                    lastMiddleClickSortMs = now;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Using Company Chest tab from AtkValues: {atkPage} slot=0 addonId={addonId}");
                    return true;
                }

                // If we couldn't infer the selected tab, do NOT guess (guessing Page1 is what causes "I clicked tab 2 but it sorted tab 1").
                if (Configuration.DebugMode)
                    Log.Information("[QuickTransfer] (MMB) Company Chest tab unknown; no action taken (waiting for a tab click or hover).");
                return false;
            }
            else if (ArmouryAddonNames.Any(n => addonName.Equals(n, StringComparison.OrdinalIgnoreCase)))
                containers = DragDropHelpers.ArmouryBoardIndexToType;

            if (containers.Length == 0 || addonId == 0)
                return false;

            // Prefer last known-good context target when available (more likely to produce a menu).
            if (lastGoodContextTargetByAddonId.TryGetValue(addonId, out var good) &&
                (IsPlayerInventoryType(good.Type) || IsArmouryType(good.Type) || IsSaddlebagType(good.Type) || IsRetainerType(good.Type) || IsCompanyChestType(good.Type)))
            {
                var openSlot = PickContextMenuSlot(good.Type, good.Slot);
                pendingMiddleClickSortRequest = (good.Type, openSlot, addonId, now);
                pendingMiddleClickSortUntilMs = now + 1500;
                lastMiddleClickSortMs = now;
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Using last-good target for hovered addon '{addonName}': {good.Type} slot={openSlot} addonId={addonId}");
                return true;
            }

            if (!TryResolveTargetFromWeirdPayload(containers, -1, -1, -1, out var type, out var slot))
                return false;

            var openSlot2 = PickContextMenuSlot(type, slot);
            pendingMiddleClickSortRequest = (type, openSlot2, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) Bootstrapped from hovered addon '{addonName}': {type} slot={openSlot2} addonId={addonId}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void TryQueueMiddleClickSortFromHover(long now)
    {
        if (!Configuration.Enabled || !Configuration.EnableMiddleClickSort)
            return;

        if (now - lastMiddleClickSortMs < 250)
            return;

        var hDdi = lastHoverDdi;
        // Rollover events only fire when moving the cursor; keep a generous window so MMB works while stationary.
        if (hDdi == null || now - hDdi.Value.SeenAtMs > 20000)
        {
            // Inventory sometimes does not emit hover events; fall back to a window hit-test at the cursor.
            // This also lets us disambiguate which window is being targeted when multiple are open.
            if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
                return;

            if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                return;
            if (TryQueueMiddleClickSortFromVisibleWindows(now))
                return;
            if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) No recent hover slot/dragdrop captured; cannot queue sort.");
            return;
        }

        try
        {
            var ddiAddonId = hDdi.Value.AddonId;

            // Key rule for stability across windows:
            // - A stored hover DDI can be stale if the UI doesn't emit MouseOut/RollOut events (common for Inventory/Saddlebags).
            // - Therefore, if the DDI wasn't updated very recently, prefer a live hit-test (collision manager) to determine
            //   which window is actually under the cursor right now.
            //
            // Armoury remains stable because the collision manager typically also reports it correctly, and we no longer
            // let stale "lastHoverAddon" from other windows override a fresh cursor hit-test.
            var ddiFresh = now - hDdi.Value.SeenAtMs <= 250;
            if (!ddiFresh)
            {
                if (TryUpdateLastHoverAddonFromCursorHitTest(now) && TryQueueMiddleClickSortFromLastHoverAddon(now))
                    return;
            }

            // Otherwise, use the DDI's addon id and cached addon name as the target.
            if (!string.IsNullOrWhiteSpace(lastHoverAddonName))
            {
                lastHoverAddon = (lastHoverAddonName, ddiAddonId, now);
                if (TryQueueMiddleClickSortFromLastHoverAddon(now))
                    return;
            }

            // As a fallback, still allow using the last-good target for this addon id.
            if (lastGoodContextTargetByAddonId.TryGetValue(ddiAddonId, out var good2))
            {
                var openSlot = PickContextMenuSlot(good2.Type, good2.Slot);
                pendingMiddleClickSortRequest = (good2.Type, openSlot, ddiAddonId, now);
                pendingMiddleClickSortUntilMs = now + 1500;
                lastMiddleClickSortMs = now;
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Used last-good target by addonId (no hover metadata): {good2.Type} slot={openSlot} addonId={ddiAddonId}");
                return;
            }

            // If we can't decide safely, do nothing.
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;
        }
        catch (Exception ex)
        {
            // Best-effort only; avoid crashing the client if the hovered pointer becomes invalid.
            Log.Warning(ex, "[QuickTransfer] (MMB) Failed to queue sort from hover dragdrop.");
        }
    }

    private long pendingCompanyChestNumericConfirmUntilMs;
    private int pendingCompanyChestNumericConfirmAttempts;
    private long pendingCloseContextMenuAtMs;
    private bool pendingCompanyChestNumericArmed;
    private bool pendingCompanyChestNumericValueSet;
    private long pendingCompanyChestNumericValueSetAtMs;
    private uint pendingCompanyChestNumericDesired;
    private bool pendingCompanyChestNumericHalf;

    // Extra safety for inventory Split dialogs (InventoryExpansion / non-English prompts):
    // When we arm a Split, record the expected "max" value (usually qty-1).
    // Then we can recognize the correct InputNumeric without relying on prompt text.
    private uint pendingSplitExpectedMax;
    private long pendingSplitExpectedUntilMs;
    private enum PendingNumericKind { None, Store, Remove, Move, Split, Trade, Sell }
    private PendingNumericKind pendingNumericKind;

    private long lastShiftSeenMs;
    private long lastCtrlSeenMs;
    private long lastAltSeenMs;

    // For stack moves that open InputNumeric, the native operation state must stay alive.
    // If it's stack-allocated, the resulting InputNumeric buttons can become "dead".
    private nint pendingMoveOutValuePtr;
    private long pendingMoveOutValueFreeAtMs;
    private nint pendingMoveAtkValuesPtr;
    private long pendingMoveCreatedAtMs;
    private bool pendingMoveSawInputNumeric;
    private static readonly Dictionary<uint, uint> StackSizeCache = new();
    private static readonly Dictionary<uint, uint> ItemUiCategoryCache = new();

    private struct CompanyChestDepositState
    {
        public bool Active;
        public InventoryType SourceType;
        public uint SourceSlot;
        public uint ItemId;
        public bool IsHq;
        public long NextAttemptAtMs;
        public long ExpiresAtMs;
        public int Steps;
        public uint LastQty;
        public long WaitForQtyChangeUntilMs;
    }

    private CompanyChestDepositState companyChestDeposit;

    private struct CompanyChestOrganizeState
    {
        public bool Active;
        public uint OwnerAddonId;
        public long NextAttemptAtMs;
        public long ExpiresAtMs;
        public int Steps;
        public int Phase; // 0=stack, 1=compact
        public InventoryType[] Pages;

        // Throttle: wait for the last move to apply before issuing another.
        public bool WaitingForApply;
        public InventoryType WaitSrcType;
        public uint WaitSrcSlot;
        public uint WaitSrcItemId;
        public int WaitSrcQty;
        public InventoryType WaitDstType;
        public uint WaitDstSlot;
        public uint WaitDstItemId;
        public int WaitDstQty;
        public long WaitUntilMs;
        public int WaitStuckCount;
        public long WaitObservedChangeAtMs;
    }

    private static bool TryGetSlotSnapshot(
        InventoryManager* inv,
        InventoryType type,
        uint slot,
        out uint itemId,
        out int qty)
    {
        itemId = 0;
        qty = 0;
        try
        {
            if (inv == null)
                return false;
            var it = inv->GetInventorySlot(type, (int)slot);
            if (it == null)
                return false;
            itemId = it->ItemId;
            qty = it->Quantity;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsContainerLoaded(InventoryManager* inv, InventoryType type)
    {
        try
        {
            if (inv == null)
                return false;
            var c = inv->GetInventoryContainer(type);
            return c != null && c->IsLoaded && c->Size > 0;
        }
        catch
        {
            return false;
        }
    }

    private CompanyChestOrganizeState companyChestOrganize;

    private enum ModifierMode
    {
        Shift,
        Ctrl,
        Alt,

        // [TC] Ctrl＋Shift 同時按住＝批次搬運。這個 mode 不走選單文字比對，
        //      所以不需要在 ContextMenuHandler.ModifierMode 也加一份。
        Bulk,
    }

    // Inventory/armoury uses this; saddlebags often do not, so we also use IContextMenu fallback.
    // Use ClientStructs delegate for better compatibility (per Discord feedback).
    private Hook<AgentInventoryContext.Delegates.OpenForItemSlot>? openForItemSlotHook = null;

    // AtkUnitBase.Close: Use IAddonLifecycle service instead of manual signature (recommended by Dalamud team).
    // We use Hide() calls directly where needed, and IAddonLifecycle for addon lifecycle events.

    // NOTE: For inventory transfers (including Free Company Chest), use the client callback handler:
    // RaptureAtkModule::HandleItemMove(AtkValue* returnValue, AtkValue* values, uint valueCount)
    // This is exposed directly by FFXIVClientStructs as a member function, so we do not signature-scan it ourselves.

    // Use ContextMenuHandler.AutoContextAction
    private enum AutoContextAction
    {
        AddAllToSaddlebag = ContextMenuHandler.AutoContextAction.AddAllToSaddlebag,
        RemoveAllFromSaddlebag = ContextMenuHandler.AutoContextAction.RemoveAllFromSaddlebag,
        PlaceInArmouryChest = ContextMenuHandler.AutoContextAction.PlaceInArmouryChest,
        ReturnToInventory = ContextMenuHandler.AutoContextAction.ReturnToInventory,
        EntrustToRetainer = ContextMenuHandler.AutoContextAction.EntrustToRetainer,
        RetrieveFromRetainer = ContextMenuHandler.AutoContextAction.RetrieveFromRetainer,
        RemoveFromCompanyChest = ContextMenuHandler.AutoContextAction.RemoveFromCompanyChest,
        Split = ContextMenuHandler.AutoContextAction.Split,
        Sort = ContextMenuHandler.AutoContextAction.Sort,
        Trade = ContextMenuHandler.AutoContextAction.Trade,
        Sell = ContextMenuHandler.AutoContextAction.Sell,
    }

    private static readonly string[] ArmouryAddonNames =
    [
        // Common internal names used by the Armoury Chest window across patches.
        "ArmouryBoard",
        "ArmoryBoard",
        "Armoury",
        "Armory",
        "ArmouryChest",
        "ArmoryChest",
    ];

    private static readonly string[] ReceiveEventAddonNames =
    [
        // Player inventory
        "Inventory",

        // Saddlebags
        "InventoryBuddy",
        "InventoryBuddy2",

        // Armoury chest (aliases vary by patch)
        ..ArmouryAddonNames,

        // Retainer inventory
        "RetainerGrid0",
        "RetainerSellList",
        "RetainerGrid",

        // Company chest
        FreeCompanyChestAddonName,
    ];

    private const string FreeCompanyChestAddonName = "FreeCompanyChest";
    private const string InputNumericAddonName = "InputNumeric";
    private const string ContextMenuAddonName = "ContextMenu";
    private const string SelectYesnoAddonName = "SelectYesno";

    // IMPORTANT:
    // We suppress by forcing alpha to 0 in PreDraw, which can "stick" because the same addon instance is reused.
    // Therefore we track suppression windows and also restore alpha when not suppressing.
    private long suppressContextMenuUntilMs;
    private long suppressInputNumericUntilMs;

    private void ArmSuppressContextMenu(long now, int durationMs = 250)
        => suppressContextMenuUntilMs = Math.Max(suppressContextMenuUntilMs, now + durationMs);

    private void ArmSuppressInputNumeric(long now, int durationMs = 1500)
        => suppressInputNumericUntilMs = Math.Max(suppressInputNumericUntilMs, now + durationMs);

    private InventoryType[] GetCompanyChestInventoryTypes()
    {
        // Don't hardcode enum names; discover them by name at runtime so we don't break across patches/structs.
        // Limit to the configured number of item compartments (default 3; can be upgraded to 5).
        var max = Math.Clamp(Configuration.CompanyChestCompartments, 3, 5);
        return Enum.GetValues<InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .Take(max)
            .ToArray();
    }

    private static InventoryType[] GetAllCompanyChestItemPages()
        => Enum.GetValues<InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .Take(5)
            .ToArray();

    private static AtkDragDropInterface* TryGetDdiFromListIndex(AtkComponentList* list, int idx)
    {
        if (list == null)
            return null;
        if (idx < 0 || idx > 512)
            return null;
        try
        {
            var r = list->GetItemRenderer(idx);
            return r != null ? &r->AtkDragDropInterface : null;
        }
        catch
        {
            return null;
        }
    }

    private static AtkDragDropInterface* TryGetDdiFromComponent(AtkComponentBase* component, int preferredListIndex = 0)
    {
        if (component == null)
            return null;

        try
        {
            var t = component->GetComponentType();
            return t switch
            {
                ComponentType.DragDrop => &((AtkComponentDragDrop*)component)->AtkDragDropInterface,
                ComponentType.ListItemRenderer => &((AtkComponentListItemRenderer*)component)->AtkDragDropInterface,
                ComponentType.List => TryGetDdiFromListIndex((AtkComponentList*)component, preferredListIndex),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private bool TryResolveCompanyChestPageFromAddon(AtkUnitBase* addon, out InventoryType page)
    {
        page = default;
        try
        {
            if (addon == null)
                return false;

            // Scan component nodes for any DragDrop/List that yields a FreeCompanyPageX payload.
            var nodeCount = addon->UldManager.NodeListCount;
            if (nodeCount <= 0)
                return false;

            var maxNodes = Math.Min((int)nodeCount, 2000);
            var bestPage = default(InventoryType);
            var bestHits = 0;

            // Track the most frequently observed FreeCompanyPageX among *visible* nodes.
            // Rationale: the FC chest addon often keeps nodes for other tabs alive but hidden; a "first match wins"
            // scan can return the wrong tab (observed off-by-one behavior).
            var hitsByPage = new Dictionary<InventoryType, int>(8);
            for (var i = 0; i < maxNodes; i++)
            {
                var n = addon->UldManager.NodeList[i];
                if (n == null)
                    continue;

                // Skip hidden nodes (inactive tabs commonly force alpha to 0).
                try
                {
                    if (n->Alpha_2 == 0 || n->Color.A == 0)
                        continue;
                }
                catch
                {
                    // ignore; continue scanning
                }

                AtkComponentNode* compNode;
                try { compNode = n->GetAsAtkComponentNode(); }
                catch { continue; }
                if (compNode == null || compNode->Component == null)
                    continue;

                var component = compNode->Component;
                var ct = component->GetComponentType();
                if (ct == ComponentType.List)
                {
                    var list = (AtkComponentList*)component;
                    // Try a few indices; FC chest lists usually expose items here.
                    var observed = 0;
                    for (var li = 0; li < 30; li++)
                    {
                        var ddi = TryGetDdiFromListIndex(list, li);
                        if (ddi == null || (nint)ddi < MinLikelyPointer)
                            continue;

                        if (TryGetSlotFromDragDropInterface(ddi, out var invType, out _))
                        {
                            if (IsCompanyChestType(invType))
                            {
                                hitsByPage.TryGetValue(invType, out var cur);
                                cur++;
                                hitsByPage[invType] = cur;
                                if (cur > bestHits)
                                {
                                    bestHits = cur;
                                    bestPage = invType;
                                }

                                // Don't over-scan; we just need enough evidence to pick the visible page.
                                observed++;
                                if (observed >= 6)
                                    break;
                            }
                        }
                    }
                }
                else
                {
                    var ddi = TryGetDdiFromComponent(component, preferredListIndex: 0);
                    if (ddi == null || (nint)ddi < MinLikelyPointer)
                        continue;

                    if (TryGetSlotFromDragDropInterface(ddi, out var invType, out _))
                    {
                        if (IsCompanyChestType(invType))
                        {
                            hitsByPage.TryGetValue(invType, out var cur);
                            cur++;
                            hitsByPage[invType] = cur;
                            if (cur > bestHits)
                            {
                                bestHits = cur;
                                bestPage = invType;
                            }
                        }
                    }
                }
            }

            if (bestHits > 0 && IsCompanyChestType(bestPage))
            {
                page = bestPage;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TryGetAtkValueInt(AtkValue* values, int count, int idx, out int value)
    {
        value = 0;
        try
        {
            if (values == null || idx < 0 || idx >= count)
                return false;
            var v = values + idx;
            if (v->Type == AtkValueType.Int)
            {
                value = v->Int;
                return true;
            }
            if (v->Type == AtkValueType.UInt)
            {
                value = unchecked((int)v->UInt);
                return true;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }

    private void ObserveCompanyChestTabFromAtkValues(AtkUnitBase* addon, InventoryType selectedPage)
    {
        try
        {
            if (addon == null || addon->AtkValues == null || addon->AtkValuesCount <= 0)
                return;

            var values = addon->AtkValues;
            var count = (int)addon->AtkValuesCount;
            var max = Math.Min(count, 80);

            for (var i = 0; i < max; i++)
            {
                if (!TryGetAtkValueInt(values, max, i, out var n))
                    continue;

                // Only small integers are plausible "tab indices".
                if (n < 0 || n > 10)
                    continue;

                if (!companyChestSelectedTabCandidates.TryGetValue(i, out var map))
                {
                    map = new Dictionary<int, InventoryType>(8);
                    companyChestSelectedTabCandidates[i] = map;
                }

                // If we see conflicting mappings for the same (index,value), drop this candidate index.
                if (map.TryGetValue(n, out var existing) && existing != selectedPage)
                {
                    companyChestSelectedTabCandidates.Remove(i);
                    continue;
                }

                map[n] = selectedPage;
            }

            // Pick the best candidate index (most distinct pages mapped).
            var bestIdx = -1;
            var bestDistinct = 0;
            foreach (var kv in companyChestSelectedTabCandidates)
            {
                var distinct = kv.Value.Values.Distinct().Count();
                if (distinct > bestDistinct)
                {
                    bestDistinct = distinct;
                    bestIdx = kv.Key;
                }
            }

            if (bestIdx >= 0 && bestDistinct >= 2)
            {
                if (companyChestSelectedTabAtkValueIndex != bestIdx)
                {
                    companyChestSelectedTabAtkValueIndex = bestIdx;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] FC Chest AtkValues selected-tab index inferred: idx={bestIdx} (mappedPages={bestDistinct}).");
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private bool TryResolveCompanyChestSelectedPageFromAtkValues(uint addonId, out InventoryType page)
    {
        page = default;
        try
        {
            if (companyChestSelectedTabAtkValueIndex < 0)
                return false;

            if (!TryGetVisibleAddon(FreeCompanyChestAddonName, out var addon, WideAddonSearchMaxIndex) || addon == null || addon->Id != addonId)
                return false;

            if (addon->AtkValues == null || addon->AtkValuesCount <= 0)
                return false;

            if (!companyChestSelectedTabCandidates.TryGetValue(companyChestSelectedTabAtkValueIndex, out var map) || map.Count == 0)
                return false;

            if (!TryGetAtkValueInt(addon->AtkValues, (int)addon->AtkValuesCount, companyChestSelectedTabAtkValueIndex, out var n))
                return false;

            if (!map.TryGetValue(n, out var p))
                return false;

            if (!IsCompanyChestType(p))
                return false;

            page = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Config migration: ensure DebugMode defaults to OFF even for existing installs.
        try
        {
            if (Configuration.Version < 3)
            {
                Configuration.DebugMode = false;
                Configuration.Version = 3;
                Configuration.Save();
            }
            else if (Configuration.Version > 3)
            {
                // If the user downgrades, don't overwrite their config; just keep their stored values.
            }
            else
            {
                // Version == 3: still ensure debug isn't accidentally on by default after updates.
                // (User can re-enable it explicitly.)
                // No auto-save here to avoid writing config every startup.
            }
        }
        catch
        {
            // ignore
        }

        configWindow = new QuickTransferWindow(Configuration);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open QuickTransfer settings",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        InteropProvider.InitializeFromAttributes(this);
        
        // Hook using ClientStructs delegate (per Discord feedback for better compatibility)
        try
        {
            // AgentInventoryContext.Delegates.OpenForItemSlot is the delegate type
            // We need to get the function pointer from MemberFunctionPointers to hook it
            var funcPtr = AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot;
            if (funcPtr != null)
            {
                openForItemSlotHook = InteropProvider.HookFromAddress<AgentInventoryContext.Delegates.OpenForItemSlot>(
                    (nint)funcPtr,
                    OpenForItemSlotDetour);
                openForItemSlotHook?.Enable();
            }
            else
            {
                Log.Warning("[QuickTransfer] AgentInventoryContext.MemberFunctionPointers.OpenForItemSlot is null - signature may not be resolved");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to hook OpenForItemSlot using ClientStructs delegate - falling back to manual signature");
            // Fallback: try manual signature hook
            try
            {
                openForItemSlotHook = InteropProvider.HookFromSignature<AgentInventoryContext.Delegates.OpenForItemSlot>(
                    "83 B9 ?? ?? ?? ?? ?? 7E ?? 39 91",
                    OpenForItemSlotDetour);
                openForItemSlotHook?.Enable();
            }
            catch (Exception ex2)
            {
                Log.Error(ex2, "[QuickTransfer] Failed to hook OpenForItemSlot with fallback signature");
            }
        }

        // Saddlebags can bypass OpenForItemSlot, so use a safe deferred click via context menu events.
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Framework.Update += OnFrameworkUpdate;

        // Lifecycle hooks:
        // Register with explicit addon names; wildcard registration is not reliable across Dalamud versions/builds.
        AddonLifecycle.RegisterListener(AddonEvent.PreSetup, InputNumericAddonName, OnInputNumericPreSetup);
        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, ContextMenuAddonName, OnAddonPreDraw);
        AddonLifecycle.RegisterListener(AddonEvent.PreDraw, InputNumericAddonName, OnAddonPreDraw);
        foreach (var name in ReceiveEventAddonNames)
            AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);

        // Listen for system error messages (e.g. "Another player is using the chest") so we can stop FC chest organize/deposit
        // instead of spamming actions.
        ChatGui.ChatMessage += OnChatMessage;

        Log.Information($"Loaded {PluginInterface.Manifest.Name}.");
        Log.Information(
            $"[QuickTransfer] DebugMode={Configuration.DebugMode}, Enabled={Configuration.Enabled}, " +
            $"EnableMiddleClickSort={Configuration.EnableMiddleClickSort}, " +
            $"EnableCompanyChest={Configuration.EnableCompanyChest}, " +
            $"EnableCompanyChestMiddleClickOrganize={Configuration.EnableCompanyChestMiddleClickOrganize}");
        if (Configuration.DebugMode)
        {
            try
            {
                var matches = Enum.GetNames<InventoryType>()
                    .Where(n => n.Contains("FreeCompany", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Company", StringComparison.OrdinalIgnoreCase) ||
                                n.Contains("Chest", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Log.Information($"[QuickTransfer] InventoryType names containing Company/Chest: {string.Join(", ", matches)}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[QuickTransfer] Failed to enumerate InventoryType names (debug).");
            }
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        ChatGui.ChatMessage -= OnChatMessage;
        AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, InputNumericAddonName, OnInputNumericPreSetup);
        AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, ContextMenuAddonName, OnAddonPreDraw);
        AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, InputNumericAddonName, OnAddonPreDraw);
        foreach (var name in ReceiveEventAddonNames)
            AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, name, OnAddonReceiveEvent);

        openForItemSlotHook?.Disable();
        openForItemSlotHook?.Dispose();

        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;

        windowSystem.RemoveAllWindows();
        configWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args) => OpenConfigUi();

    private void OpenConfigUi() => configWindow.IsOpen = true;

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return;

            // Only care while FC chest features are active; avoid doing extra work on every chat line.
            if (!companyChestOrganize.Active && !companyChestDeposit.Active)
                return;

            var text = message.TextValue ?? string.Empty;
            if (text.Length == 0)
                return;

            // These strings appear as system error toasts and (typically) also in the log/chat.
            // If we see them, stop the state machine and back off for a few seconds.
            // [TC] 這幾句在台服是中文（LogMessage#1861/1873/1874），改用資料表比對，英文版仍然照舊認得。
            if (GameStrings.IsCompanyChestBusyMessage(text))
            {
                var now = Environment.TickCount64;
                companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
                var backoffMs = (long)Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4))); // 5s,10s,20s,40s,60s cap
                companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);

                // If the chest is busy repeatedly, stop the run and let the user try later.
                if (companyChestOrganize.Active && companyChestBusyHits >= 3)
                {
                    companyChestOrganize.Active = false;
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) FC Chest busy hit {companyChestBusyHits}; stopping organize run. msg='{text}'");
                }
                else if (companyChestOrganize.Active)
                {
                    // Pause and retry later.
                    companyChestOrganize.WaitingForApply = false;
                    companyChestOrganize.WaitObservedChangeAtMs = 0;
                    companyChestOrganize.NextAttemptAtMs = Math.Max(companyChestOrganize.NextAttemptAtMs, companyChestBusyUntilMs + 750);
                    companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, companyChestBusyUntilMs + 20000);
                    companyChestOrganize.WaitStuckCount = 0;
                }

                // Deposit is interactive; stop it outright on busy.
                companyChestDeposit.Active = false;
                pendingCompanyChestNumericConfirmUntilMs = 0;
                pendingCompanyChestNumericArmed = false;
                pendingNumericKind = PendingNumericKind.None;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) FC Chest busy detected from chat; backoff={backoffMs}ms (hit {companyChestBusyHits}). msg='{text}'");
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OpenForItemSlotDetour(
        AgentInventoryContext* agent,
        InventoryType inventoryType,
        int slot,
        int a4,
        uint addonId)
    {
        openForItemSlotHook?.Original(agent, inventoryType, slot, a4, addonId);

        if (!Configuration.Enabled)
            return;

        // Record observed a4 values so we can reuse them for MMB-driven opens.
        try
        {
            observedContextA4[(addonId, (uint)inventoryType)] = a4;

            // If this call actually produced a context menu, remember it as a safe fallback for MMB sorting.
            if (agent != null && agent->ContextItemCount > 0)
                lastGoodContextTargetByAddonId[addonId] = (inventoryType, slot, a4);

            if (Configuration.DebugMode && Environment.TickCount64 - lastObservedA4LogMs >= 1000)
            {
                lastObservedA4LogMs = Environment.TickCount64;
                Log.Information($"[QuickTransfer] Observed OpenForItemSlot: type={inventoryType} slot={slot} a4={a4} addonId={addonId} ctxCount={(agent != null ? agent->ContextItemCount : -1)}");
            }
        }
        catch
        {
            // ignore
        }

        // Modifier: Ctrl+RClick (special) or Shift+RClick (default).
        // Ctrl takes priority if both are held. Use a short "latch" so quick taps still work.
        var mode = GetModifierModeLatched(Environment.TickCount64);

        if (mode == null)
            return;

        var saddlebagOpen = IsSaddlebagOpen();
        var retainerOpen = IsRetainerOpen();
        var companyChestOpen = IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        // [TC] Ctrl＋Shift＋右鍵：以點到的那一格為起點，整批搬。
        if (mode == ModifierMode.Bulk)
        {
            // 兵裝庫不在上游的 specialOpen 定義裡（它原本只管鞍囊／僱員／儲物櫃），
            // 但批次搬運支援背包 ↔ 兵裝庫，所以這裡要另外算進來。
            if (!specialOpen && !IsArmouryOpen())
                return;

            var bulkNow = Environment.TickCount64;
            if (StartBulkTransfer(inventoryType, (uint)slot, bulkNow))
            {
                lastActionTickMs = bulkNow;
                TryCloseCurrentContextMenu(agent);
            }

            return;
        }

        // Ctrl is only enabled while a "special" container is open (Saddlebag or Retainer),
        // so Shift/Ctrl can be used to disambiguate behaviors.
        if (mode == ModifierMode.Ctrl && !specialOpen)
            return;

        // Never run Ctrl-mode from saddlebag slots.
        if (mode == ModifierMode.Ctrl && IsSaddlebagType(inventoryType))
            return;

        // Never run Ctrl-mode from retainer slots.
        if (mode == ModifierMode.Ctrl && IsRetainerType(inventoryType))
            return;

        // Never run Ctrl-mode from Company Chest slots.
        if (mode == ModifierMode.Ctrl && IsCompanyChestType(inventoryType))
            return;

        var now = Environment.TickCount64;
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
            return;

        // For Alt (Split), prefer the deferred OnMenuOpened path (more reliable than firing callbacks during OpenForItemSlot).
        if (mode == ModifierMode.Alt)
            return;

        if (mode == ModifierMode.Shift && companyChestOpen && Configuration.EnableCompanyChest)
        {
            // If a quantity dialog is already open, don't start another move.
            if (TryGetVisibleAddon(InputNumericAddonName, out _))
                return;

            // Deposit: Inventory -> Company Chest (UI-driven move).
            // This is handled as a small state machine so stacks can top-off existing stacks and spill into new stacks.
            if (IsPlayerInventoryType(inventoryType) && StartCompanyChestDeposit(inventoryType, (uint)slot))
            {
                lastActionTickMs = now;
                TryCloseCurrentContextMenu(agent);
                return;
            }
        }

        if (TryAutoSelectAndClose(agent, mode.Value, out var chosenText, out var chosenIndex))
        {
            lastActionTickMs = now;
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] ({mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via OpenForItemSlot.");
            
            // Set up Trade quantity auto-confirm if Trade was selected
            if (mode == ModifierMode.Shift &&
                chosenText.Length > 0 &&
                ContextLabelMatches(AutoContextAction.Trade, chosenText) &&
                IsTradeOpen())
            {
                pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                pendingCompanyChestNumericConfirmAttempts = 0;
                pendingCompanyChestNumericArmed = true;
                pendingNumericKind = PendingNumericKind.Trade;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                ArmSuppressInputNumeric(now, 1500);
            }
            // Set up vendor Sell quantity auto-confirm if Sell was selected
            if (Configuration.AutoConfirmVendorSell &&
                mode == ModifierMode.Shift &&
                chosenText.Length > 0 &&
                ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
                IsVendorOpen())
            {
                pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                pendingCompanyChestNumericConfirmAttempts = 0;
                pendingCompanyChestNumericArmed = true;
                pendingNumericKind = PendingNumericKind.Sell;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
                ArmSuppressInputNumeric(now, 1500);
            }
        }
        else if (Configuration.DebugMode && mode == ModifierMode.Ctrl)
        {
            Log.Information("[QuickTransfer] (Ctrl + RClick) No matching armoury action found in context menu.");
            DebugDumpContextMenu(agent, maxItems: 24);
        }
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (!Configuration.Enabled)
            return;

        var now = Environment.TickCount64;
        var middleSortActive = pendingMiddleClickSortUntilMs > 0 && now <= pendingMiddleClickSortUntilMs;
        var mode = middleSortActive ? null : GetModifierModeLatched(now);

        if (!middleSortActive && mode == null)
            return;

        var saddlebagOpen = IsSaddlebagOpen();
        var retainerOpen = IsRetainerOpen();
        var companyChestOpen = IsCompanyChestOpen();
        var specialOpen = saddlebagOpen || retainerOpen || companyChestOpen;

        if (!middleSortActive && mode == ModifierMode.Ctrl && !specialOpen)
            return;

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] OnMenuOpened: AddonName='{args.AddonName}', MenuType={args.MenuType}, AgentPtr=0x{args.AgentPtr.ToInt64():X}, AddonPtr=0x{args.AddonPtr.ToInt64():X}");

        // Middle-click "Sort" uses an inventory context menu, but does not require Shift/Ctrl.
        if (middleSortActive && args.MenuType == ContextMenuType.Inventory)
        {
            if (args.AgentPtr != IntPtr.Zero && args.AddonPtr != IntPtr.Zero)
            {
                pendingDeferredSortMenuClick = ((nint)args.AgentPtr, (nint)args.AddonPtr, now);
                return;
            }
        }

        // [TC] 公會儲物櫃的格子走 MenuType.Default，不經過 OpenForItemSlot，
        //      所以批次搬運要在這裡另外接一次，起點用滑鼠最後停留的那一格解析。
        if (args.MenuType == ContextMenuType.Default &&
            mode == ModifierMode.Bulk &&
            Configuration.EnableBulkTransfer &&
            string.Equals(args.AddonName, FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveHoveredSlot(now, out var hoverType, out var hoverSlot, out var hoverDiag) ||
                !IsCompanyChestType(hoverType))
            {
                // 解析不到起點格就講出來，不然使用者只會看到「按了沒反應」。
                // 診斷一律印在聊天視窗，不要只寫 log——log 檔可能早就滿了寫不進去。
                ChatGui.Print($"[QuickTransfer/v2] 批次搬運：抓不到儲物櫃格子。{hoverDiag}");
                return;
            }

            if (StartBulkTransfer(hoverType, (uint)hoverSlot, now))
            {
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 1500);
                pendingCloseContextMenuAtMs = now + 50;
            }

            return;
        }

        // Free Company Chest uses MenuType.Default (not Inventory).
        if (args.MenuType == ContextMenuType.Default &&
            (mode == ModifierMode.Shift || mode == ModifierMode.Alt) &&
            Configuration.EnableCompanyChest &&
            string.Equals(args.AddonName, FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
        {
            pendingDeferredDefaultMenu = (args.AddonName ?? string.Empty, now, mode.Value);
            return;
        }

        // Only deal with inventory context menus otherwise.
        if (args.MenuType != ContextMenuType.Inventory)
            return;

        if (args.AgentPtr == IntPtr.Zero || args.AddonPtr == IntPtr.Zero)
            return;

        if (mode == null)
            return;

        // IMPORTANT: Do not click inside the open event (re-entrancy risk).
        pendingDeferredMenuClick = ((nint)args.AgentPtr, (nint)args.AddonPtr, Environment.TickCount64, mode.Value);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!Configuration.Enabled)
            return;

        var now = Environment.TickCount64;

        // Poll mouse button state from Win32 and log transitions in DebugMode.
        // This helps diagnose cases where the game doesn't emit click events for MMB.
        var lDown = IsVkDown(0x01); // VK_LBUTTON
        var rDown = IsVkDown(0x02); // VK_RBUTTON
        var mDown = IsVkDown(0x04); // VK_MBUTTON
        var x1Down = IsVkDown(0x05); // VK_XBUTTON1
        var x2Down = IsVkDown(0x06); // VK_XBUTTON2

        var prevL = lastVkLButtonDown;
        var prevR = lastVkRButtonDown;
        var prevM = lastVkMButtonDown;
        var prevX1 = lastVkX1ButtonDown;
        var prevX2 = lastVkX2ButtonDown;

        if (Configuration.DebugMode && (lDown != prevL || rDown != prevR || mDown != prevM || x1Down != prevX1 || x2Down != prevX2))
            Log.Information($"[QuickTransfer] Win32 mouse state: L={(lDown ? 1 : 0)} R={(rDown ? 1 : 0)} M={(mDown ? 1 : 0)} X1={(x1Down ? 1 : 0)} X2={(x2Down ? 1 : 0)}");

        lastVkLButtonDown = lDown;
        lastVkRButtonDown = rDown;
        lastVkMButtonDown = mDown;
        lastVkX1ButtonDown = x1Down;
        lastVkX2ButtonDown = x2Down;

        // If a "middle-ish" button is pressed (rising edge), queue a sort using the last hovered slot.
        // This works even if the client doesn't generate a distinct UI click event on this build.
        var middleEdge = (mDown && !prevM) || (x1Down && !prevX1) || (x2Down && !prevX2);
        if (middleEdge)
        {
            if (Configuration.EnableMiddleClickSort)
                TryQueueMiddleClickSortFromHover(now);
            else if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) Press detected, but EnableMiddleClickSort is disabled.");
        }

        // Modifier latch (helps cases where the user taps Shift/Ctrl quickly).
        if (KeyState[VirtualKey.SHIFT])
            lastShiftSeenMs = now;
        if (KeyState[VirtualKey.CONTROL])
            lastCtrlSeenMs = now;
        if (KeyState[VirtualKey.MENU])
            lastAltSeenMs = now;

        // Quantity prompt auto-confirm (best effort).
        // Trade and Split always auto-confirm; Company Chest and Vendor Sell respect their config settings.
        var shouldAutoConfirm = pendingNumericKind == PendingNumericKind.Trade ||
                                pendingNumericKind == PendingNumericKind.Split ||
                                (Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell) ||
                                (Configuration.AutoConfirmCompanyChestQuantity && pendingNumericKind != PendingNumericKind.None);
        
        if (shouldAutoConfirm &&
            pendingNumericKind != PendingNumericKind.None &&
            pendingCompanyChestNumericConfirmUntilMs > 0 &&
            now <= pendingCompanyChestNumericConfirmUntilMs)
        {
            if (TryGetVisibleAddon(InputNumericAddonName, out var inputNumeric))
            {
                ArmSuppressInputNumeric(now);
                // Phase 1: set max (and wait a frame so the component commits the value internally).
                if (!pendingCompanyChestNumericValueSet)
                {
                    if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                    {
                        if (Configuration.DebugMode)
                        {
                            try
                            {
                                var promptVal = inputNumeric->AtkValues + 6;
                                var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(*promptVal) : string.Empty;
                                var minVal = inputNumeric->AtkValues + 2;
                                var maxVal = inputNumeric->AtkValues + 3;
                                var min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
                                var max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
                                Log.Information($"[QuickTransfer] Auto-confirm InputNumeric skipped (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
                            }
                            catch
                            {
                                Log.Information($"[QuickTransfer] Auto-confirm InputNumeric skipped (kind={pendingNumericKind}).");
                            }
                        }
                        // Prompt doesn't match expectation; stop (prevents confirming wrong dialogs).
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericHalf = false;
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
                        suppressInputNumericUntilMs = 0;
                    }
                    else
                    {
                        pendingCompanyChestNumericValueSet = true;
                        pendingCompanyChestNumericValueSetAtMs = now;
                    }
                    return;
                }

                // Phase 2: confirm after a short delay (next frame / ~50ms).
                if (now - pendingCompanyChestNumericValueSetAtMs < 50)
                    return;

                // Re-check prompt + re-apply max right before confirming (cheap + safer).
                if (!TrySetInputNumericToMax(inputNumeric, pendingNumericKind))
                {
                    if (Configuration.DebugMode)
                    {
                        try
                        {
                            var promptVal = inputNumeric->AtkValues + 6;
                            var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(*promptVal) : string.Empty;
                            var minVal = inputNumeric->AtkValues + 2;
                            var maxVal = inputNumeric->AtkValues + 3;
                            var min = minVal->Type == AtkValueType.UInt ? minVal->UInt : 0U;
                            var max = maxVal->Type == AtkValueType.UInt ? maxVal->UInt : 0U;
                            Log.Information($"[QuickTransfer] Auto-confirm InputNumeric aborted (kind={pendingNumericKind}, prompt='{prompt}', min={min}, max={max}, expectedSplitMax={pendingSplitExpectedMax}).");
                        }
                        catch
                        {
                            Log.Information($"[QuickTransfer] Auto-confirm InputNumeric aborted (kind={pendingNumericKind}).");
                        }
                    }
                    pendingCompanyChestNumericConfirmUntilMs = 0;
                    pendingCompanyChestNumericArmed = false;
                    pendingNumericKind = PendingNumericKind.None;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericHalf = false;
                    pendingSplitExpectedMax = 0;
                    pendingSplitExpectedUntilMs = 0;
                    return;
                }

                try
                {
                    if (pendingCompanyChestNumericConfirmAttempts == 0)
                    {
                        // IMPORTANT:
                        // For InputNumeric, FireCallbackInt(value) is interpreted as the quantity being confirmed on this client build.
                        // Passing "2" causes exactly the observed behavior: moving 2 every time.
                        var toConfirm = pendingCompanyChestNumericDesired;
                        if (toConfirm == 0)
                        {
                            // Default: confirm max (we already set the numeric input to max above).
                            try
                            {
                                var maxVal = inputNumeric->AtkValues + 3;
                                if (maxVal->Type == AtkValueType.UInt)
                                    toConfirm = maxVal->UInt;
                                else if (maxVal->Type == AtkValueType.Int)
                                    toConfirm = (uint)Math.Max(0, maxVal->Int);
                            }
                            catch
                            {
                                // ignore
                            }
                            if (toConfirm == 0)
                                toConfirm = 1;
                        }
                        inputNumeric->FireCallbackInt((int)toConfirm);
                        pendingCompanyChestNumericConfirmAttempts = 1;
                        if (Configuration.DebugMode)
                            Log.Information($"[QuickTransfer] Auto-confirmed InputNumeric attempt 1 (kind={pendingNumericKind}, FireCallbackInt={toConfirm}).");

                        // Clear state after issuing confirm; the dialog should close itself.
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericValueSet = false;
                        pendingCompanyChestNumericValueSetAtMs = 0;
                        pendingCompanyChestNumericDesired = 0;
                        pendingCompanyChestNumericHalf = false;
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
                        suppressInputNumericUntilMs = 0;
                    }
                    else
                    {
                        pendingCompanyChestNumericConfirmUntilMs = 0;
                        pendingCompanyChestNumericArmed = false;
                        pendingNumericKind = PendingNumericKind.None;
                        pendingCompanyChestNumericValueSet = false;
                        pendingCompanyChestNumericValueSetAtMs = 0;
                        pendingCompanyChestNumericDesired = 0;
                        pendingCompanyChestNumericHalf = false;
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
                        suppressInputNumericUntilMs = 0;
                    }
                }
                catch (Exception ex)
                {
                    pendingCompanyChestNumericConfirmUntilMs = 0;
                    pendingCompanyChestNumericArmed = false;
                    pendingNumericKind = PendingNumericKind.None;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    pendingSplitExpectedMax = 0;
                    pendingSplitExpectedUntilMs = 0;
                    suppressInputNumericUntilMs = 0;
                    Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm InputNumeric.");
                }
            }
            else if (Configuration.AutoConfirmVendorSell && pendingNumericKind == PendingNumericKind.Sell && IsVendorOpen() &&
                     TryGetVisibleAddon(SelectYesnoAddonName, out var selectYesno) && selectYesno != null)
            {
                // "Are you certain you wish to sell it?" (unique/untradable) — click OK/Yes.
                try
                {
                    selectYesno->FireCallbackInt(0); // 0 = OK/Yes
                    if (Configuration.DebugMode)
                        Log.Information("[QuickTransfer] Auto-confirmed vendor sell Yes/No dialog (SelectYesno).");
                }
                catch (Exception ex)
                {
                    if (Configuration.DebugMode)
                        Log.Warning(ex, "[QuickTransfer] Failed to auto-confirm SelectYesno.");
                }
                pendingCompanyChestNumericConfirmUntilMs = 0;
                pendingCompanyChestNumericArmed = false;
                pendingNumericKind = PendingNumericKind.None;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = false;
            }
        }
        else if (pendingCompanyChestNumericConfirmUntilMs > 0 && now > pendingCompanyChestNumericConfirmUntilMs)
        {
            pendingCompanyChestNumericConfirmUntilMs = 0;
            pendingCompanyChestNumericArmed = false;
            pendingNumericKind = PendingNumericKind.None;
            pendingCompanyChestNumericValueSet = false;
            pendingCompanyChestNumericValueSetAtMs = 0;
            pendingCompanyChestNumericDesired = 0;
            pendingCompanyChestNumericHalf = false;
            pendingSplitExpectedMax = 0;
            pendingSplitExpectedUntilMs = 0;
            suppressInputNumericUntilMs = 0;
        }

        // If we have a pending "move outValue" buffer for InputNumeric-driven moves, free it once the dialog is gone,
        // or when the timeout expires.
        if (pendingMoveOutValuePtr != 0 || pendingMoveAtkValuesPtr != 0)
        {
            var inputVisible = TryGetVisibleAddon(InputNumericAddonName, out _);
            if (inputVisible)
                pendingMoveSawInputNumeric = true;

            // Important: InputNumeric often appears on a subsequent frame.
            // Do NOT free the buffers immediately just because it's not visible yet.
            var graceExpired = pendingMoveCreatedAtMs > 0 && now - pendingMoveCreatedAtMs >= 1500;
            if ((pendingMoveSawInputNumeric && !inputVisible) || now >= pendingMoveOutValueFreeAtMs || (!inputVisible && graceExpired))
            {
                try { if (pendingMoveOutValuePtr != 0) Marshal.FreeHGlobal(pendingMoveOutValuePtr); } catch { /* ignore */ }
                try { if (pendingMoveAtkValuesPtr != 0) Marshal.FreeHGlobal(pendingMoveAtkValuesPtr); } catch { /* ignore */ }
                pendingMoveOutValuePtr = 0;
                pendingMoveOutValueFreeAtMs = 0;
                pendingMoveAtkValuesPtr = 0;
                pendingMoveCreatedAtMs = 0;
                pendingMoveSawInputNumeric = false;
            }
        }

        // [TC] 批次搬運狀態機（Ctrl＋Shift＋右鍵起動）。
        if (Configuration.EnableBulkTransfer)
            ProcessBulkTransfer(now);

        // Company Chest deposit state machine (Inventory -> FC Chest).
        if (Configuration.EnableCompanyChest)
            ProcessCompanyChestDeposit(now);

        // Company Chest organize (MMB): auto-stack + compact items within FC chest pages.
        if (Configuration.EnableCompanyChest && Configuration.EnableCompanyChestMiddleClickOrganize)
            ProcessCompanyChestOrganize(now);

        // Middle-click sort: open the context menu on the clicked slot, then auto-select "Sort".
        var mmb = pendingMiddleClickSortRequest;
        if (Configuration.EnableMiddleClickSort && mmb != null && now - mmb.Value.EnqueuedAtMs <= 1500)
        {
            // If the request was for Company Chest, run organize instead (there is no Sort entry on the item menu).
            if (IsCompanyChestType(mmb.Value.Type) && Configuration.EnableCompanyChest && Configuration.EnableCompanyChestMiddleClickOrganize)
            {
                // Only organize the currently selected tab (we use mmb.Value.Type as the selected FreeCompanyPage).
                StartCompanyChestOrganize(now, mmb.Value.Type);
                pendingMiddleClickSortRequest = null;
                pendingMiddleClickSortUntilMs = 0;
            }
            else
            {
                // Safety: never call OpenForItemSlot with unknown inventory types; this can crash the game client.
                if (!IsPlayerInventoryType(mmb.Value.Type) && !IsArmouryType(mmb.Value.Type) && !IsSaddlebagType(mmb.Value.Type) &&
                    !IsRetainerType(mmb.Value.Type) && !IsCompanyChestType(mmb.Value.Type))
                {
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] (MMB) Refusing to call OpenForItemSlot for unrecognized inventory type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId} (crash-prevention).");
                    pendingMiddleClickSortRequest = null;
                    pendingMiddleClickSortUntilMs = 0;
                    return;
                }

                // Open context menu for that slot. Our OnMenuOpened handler will enqueue the deferred sort selection.
                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    var agent = agentModule->GetAgentByInternalId(AgentId.InventoryContext);
                    var invCtx = (AgentInventoryContext*)agent;
                    if (invCtx != null)
                    {
                        try
                        {
                            ArmSuppressContextMenu(now, 250);
                            if (Configuration.DebugMode)
                                Log.Information($"[QuickTransfer] (MMB) Calling OpenForItemSlot: type={mmb.Value.Type} slot={mmb.Value.Slot} addonId={mmb.Value.AddonId}");

                            // Try to open the inventory context menu using the same mysterious "a4" value the game uses.
                            // If we don't have a recorded value yet, try a small set of common candidates.
                            int[] candidates;
                            if (!observedContextA4.TryGetValue((mmb.Value.AddonId, (uint)mmb.Value.Type), out var observedA4))
                            {
                                // Heuristic: armoury boards often need a non-zero a4; try 1 first.
                                candidates = IsArmouryType(mmb.Value.Type) ? [1, 0, 2] : [0, 1, 2];
                            }
                            else
                            {
                                candidates = [observedA4, 0, 1, 2];
                            }

                            var opened = false;
                            var usedA4 = 0;
                            foreach (var a4 in candidates.Distinct())
                            {
                                invCtx->OpenForItemSlot(mmb.Value.Type, mmb.Value.Slot, a4, mmb.Value.AddonId);
                                usedA4 = a4;
                                if (invCtx->ContextItemCount > 0)
                                {
                                    opened = true;
                                    observedContextA4[(mmb.Value.AddonId, (uint)mmb.Value.Type)] = a4;
                                    break;
                                }
                            }

                            // Fallback: don't rely solely on OnMenuOpened firing.
                            try
                            {
                                var cm = GameGui.GetAddonByName("ContextMenu", 1);
                                pendingDeferredSortMenuClick = ((nint)invCtx, cm.IsNull ? 0 : (nint)cm.Address, now);
                            }
                            catch
                            {
                                pendingDeferredSortMenuClick = ((nint)invCtx, 0, now);
                            }

                            if (Configuration.DebugMode)
                            {
                                try
                                {
                                    Log.Information(
                                        $"[QuickTransfer] (MMB) Post OpenForItemSlot: opened={(opened ? 1 : 0)} usedA4={usedA4} ContextItemCount={invCtx->ContextItemCount}, " +
                                        $"OwnerAddonId={invCtx->OwnerAddonId}, BlockingAddonId={invCtx->BlockingAddonId}, " +
                                        $"TargetInv={invCtx->TargetInventoryId}, TargetSlot={invCtx->TargetInventorySlotId}");
                                }
                                catch
                                {
                                    // ignore
                                }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }

                // Only try once; selection happens via deferred menu click.
                pendingMiddleClickSortRequest = null;
            }
        }

        // Delay-close the context menu slightly; closing immediately can cancel some default-menu actions.
        if (pendingCloseContextMenuAtMs > 0 && now >= pendingCloseContextMenuAtMs)
        {
            pendingCloseContextMenuAtMs = 0;
            try
            {
                var cm = (AtkUnitBase*)GameGui.GetAddonByName("ContextMenu", 1).Address;
                if (cm != null)
                {
                    try { cm->Hide(false, true, 0); } catch { /* ignore */ }
                }
            }
            catch
            {
                // ignore
            }
        }

        // Handle deferred "default" context menus (e.g., FreeCompanyChest).
        var pendingDefault = pendingDeferredDefaultMenu;
        if (pendingDefault != null)
        {
            pendingDeferredDefaultMenu = null;

            if (now - pendingDefault.Value.EnqueuedAtMs <= 1500 &&
                (pendingDefault.Value.Mode == ModifierMode.Shift || pendingDefault.Value.Mode == ModifierMode.Alt) &&
                pendingDefault.Value.AddonName.Equals(FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
                Configuration.EnableCompanyChest)
            {
                ArmSuppressContextMenu(now, 1500);
                if (TrySelectRemoveFromCompanyChestContextMenu())
                {
                    lastActionTickMs = now;
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 1500 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Remove;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = pendingDefault.Value.Mode == ModifierMode.Alt;
                    ArmSuppressInputNumeric(now, 1500);
                }
                // Keep suppression on while the remove dialog is being handled.
            }
        }

        var pending = pendingDeferredMenuClick;
        if (pending == null)
        {
            // Process deferred middle-click sort selection even if no normal deferred click.
            ProcessDeferredSortMenuClick(now);
            return;
        }

        if (now - pending.Value.EnqueuedAtMs > 1500)
        {
            // Consume (timed out).
            pendingDeferredMenuClick = null;
            return;
        }

        // Give the context menu a moment to populate after OnMenuOpened (InventoryExpansion often needs a frame).
        if (now - pending.Value.EnqueuedAtMs < 50)
            return;

        // Consume (only try once after the short delay).
        pendingDeferredMenuClick = null;

        // If we already acted this tick/window via OpenForItemSlot, don't deref pointers.
        if (now - lastActionTickMs < Configuration.TransferCooldownMs)
            return;

        try
        {
            var agent = (AgentInventoryContext*)pending.Value.AgentPtr;
            // NOTE:
            // IMenuOpenedArgs.AddonPtr/AddOnName refers to the addon that *opened* the menu (e.g. Inventory/InventoryExpansion),
            // not the context menu addon itself. We must fire callbacks on the actual ContextMenu addon.
            AtkUnitBase* addon = null;
            try
            {
                var cm = GameGui.GetAddonByName(ContextMenuAddonName, 1);
                if (!cm.IsNull)
                    addon = (AtkUnitBase*)cm.Address;
            }
            catch
            {
                // ignore
            }

            // Fallback: keep whatever we were given (older Dalamud builds may have provided the context menu pointer).
            if (addon == null)
                addon = (AtkUnitBase*)pending.Value.AddonPtr;

            if (TryAutoSelectAndClose(agent, addon, pending.Value.Mode, out var chosenText, out var chosenIndex))
            {
                lastActionTickMs = now;
                // Split is finicky: keep the menu suppressed longer so it can't be cancelled by an early close/visibility change.
                var suppressMs = (pending.Value.Mode == ModifierMode.Alt && chosenText.Length > 0 && ContextLabelMatches(AutoContextAction.Split, chosenText))
                    ? 3000
                    : 1500;
                ArmSuppressContextMenu(now, suppressMs);
                if (pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextLabelMatches(AutoContextAction.Trade, chosenText))
                {
                    // Trade: auto-confirm max quantity when InputNumeric appears
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Trade;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now, 1500);
                }
                if (Configuration.AutoConfirmVendorSell &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextLabelMatches(AutoContextAction.Sell, chosenText) &&
                    IsVendorOpen())
                {
                    // Vendor Sell: auto-confirm max quantity when InputNumeric appears
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Sell;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now, 1500);
                }
                if (Configuration.EnableCompanyChest &&
                    pending.Value.Mode == ModifierMode.Shift &&
                    chosenText.Length > 0 &&
                    ContextLabelMatches(AutoContextAction.RemoveFromCompanyChest, chosenText))
                {
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 1500 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Remove;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now, 1500);
                }
                if (pending.Value.Mode == ModifierMode.Alt &&
                    chosenText.Length > 0 &&
                    ContextLabelMatches(AutoContextAction.Split, chosenText))
                {
                    // InventoryExpansion can delay InputNumeric slightly; allow a longer window.
                    pendingCompanyChestNumericConfirmUntilMs = Configuration.AutoConfirmCompanyChestQuantity ? now + 5000 : 0;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Split;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = true;
                    ArmSuppressInputNumeric(now, 5000);

                    // Record expected split max (qty-1) to recognize the right dialog even if the prompt isn't English.
                    try
                    {
                        var srcType = (InventoryType)agent->TargetInventoryId;
                        var srcSlot = (int)agent->TargetInventorySlotId;
                        if (TryGetItemInfo(srcType, srcSlot, out _, out _, out var qty) && qty > 1)
                        {
                            pendingSplitExpectedMax = qty - 1;
                            pendingSplitExpectedUntilMs = now + 5000;
                        }
                        else
                        {
                            pendingSplitExpectedMax = 0;
                            pendingSplitExpectedUntilMs = 0;
                        }
                    }
                    catch
                    {
                        pendingSplitExpectedMax = 0;
                        pendingSplitExpectedUntilMs = 0;
                    }
                }
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] ({pending.Value.Mode} + RClick) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
            }
            else if (Configuration.DebugMode && pending.Value.Mode == ModifierMode.Ctrl)
            {
                Log.Information("[QuickTransfer] (Ctrl + RClick) Deferred menu opened but no matching 'Place in Armoury Chest' action was found.");
                DebugDumpContextMenu(agent, maxItems: 24);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Deferred menu select failed.");
        }

        // Also process a pending sort click (if any) after normal transfers.
        ProcessDeferredSortMenuClick(now);
    }

    private void ProcessDeferredSortMenuClick(long now)
    {
        var pendingSort = pendingDeferredSortMenuClick;
        if (pendingSort == null)
            return;

        // Give the context menu a moment to populate after OpenForItemSlot.
        if (now - pendingSort.Value.EnqueuedAtMs < 50)
            return;

        if (now - pendingSort.Value.EnqueuedAtMs > 1500)
        {
            if (Configuration.DebugMode)
            {
                try
                {
                    var agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
                    var count = agent != null ? agent->ContextItemCount : -1;
                    Log.Information($"[QuickTransfer] (MMB) Deferred sort timed out (ContextItemCount={count}).");
                }
                catch
                {
                    Log.Information("[QuickTransfer] (MMB) Deferred sort timed out.");
                }
            }
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            return;
        }

        try
        {
            var agent = (AgentInventoryContext*)pendingSort.Value.AgentPtr;
            var addon = (AtkUnitBase*)pendingSort.Value.AddonPtr;

            // If we didn't have the ContextMenu addon pointer yet, try to resolve it now.
            if (addon == null)
            {
                try
                {
                    var cm = GameGui.GetAddonByName("ContextMenu", 1);
                    if (!cm.IsNull)
                    {
                        addon = (AtkUnitBase*)cm.Address;
                        pendingDeferredSortMenuClick = (pendingSort.Value.AgentPtr, (nint)addon, pendingSort.Value.EnqueuedAtMs);
                        pendingSort = pendingDeferredSortMenuClick;
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // If the menu hasn't populated yet, keep waiting.
            // AgentInventoryContext::ContextItemCount tends to remain 0 for a frame or two after OpenForItemSlot.
            if (agent == null || agent->ContextItemCount <= 0)
                return;

            if (addon == null)
                return;

            if (TrySelectSortAndClose(agent, addon, out var chosenText, out var chosenIndex))
            {
                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                lastActionTickMs = now;
                ArmSuppressContextMenu(now, 500);
                if (Configuration.DebugMode)
                {
                    if (chosenIndex >= 0)
                        Log.Information($"[QuickTransfer] (MMB) Selected context action '{chosenText}' (idx={chosenIndex}) via deferred OnMenuOpened.");
                    else
                        Log.Information("[QuickTransfer] (MMB) Already sorted (Undo Sort present); no action taken.");
                }
            }
            else
            {
                // If we opened a menu but didn't find Sort, wait briefly in case the menu is still updating.
                // After ~300ms, give up and close it to avoid leaving a hidden menu behind.
                if (now - pendingSort.Value.EnqueuedAtMs < 300)
                    return;

                if (Configuration.DebugMode)
                {
                    Log.Information($"[QuickTransfer] (MMB) Context menu opened but no 'Sort' entry was found (count={agent->ContextItemCount}).");
                    DebugDumpContextMenu(agent, maxItems: 32);
                }

                pendingDeferredSortMenuClick = null;
                pendingMiddleClickSortUntilMs = 0;
                try { if (addon != null) CloseContextMenuAddon(agent, addon); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            pendingDeferredSortMenuClick = null;
            pendingMiddleClickSortUntilMs = 0;
            Log.Warning(ex, "[QuickTransfer] Deferred sort select failed.");
        }
    }

    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            var name = args.AddonName ?? string.Empty;
            var now = Environment.TickCount64;

            if (string.Equals(name, ContextMenuAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressContextMenuUntilMs)
                    MakeAddonInvisible(addon);
                else
                    MakeAddonVisible(addon);
            }

            if (string.Equals(name, InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
            {
                var addon = (AtkUnitBase*)args.Addon.Address;
                if (now <= suppressInputNumericUntilMs)
                    MakeAddonInvisible(addon);
                else
                    MakeAddonVisible(addon);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void OnAddonReceiveEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            // [TC] 批次搬運靠這裡追蹤滑鼠停在哪一格（公會儲物櫃的右鍵選單沒有別的來源），
            //      所以不能只綁在「中鍵整理」的開關上。
            if (!Configuration.Enabled || (!Configuration.EnableMiddleClickSort && !Configuration.EnableBulkTransfer))
                return;

            if (args is not AddonReceiveEventArgs recv)
                return;

            var now = Environment.TickCount64;
            if (Configuration.DebugMode && !debugPrintedReceiveEventHook)
            {
                debugPrintedReceiveEventHook = true;
                try { ChatGui.Print("[QuickTransfer] ReceiveEvent hook active (MMB debug)."); } catch { /* ignore */ }
                Log.Information("[QuickTransfer] ReceiveEvent hook active (MMB debug).");
            }

            var eventType = (AtkEventType)recv.AtkEventType;
            // [TC] API13 的 AddonReceiveEventArgs 還沒有 AtkEventData 這個屬性，事件資料指標叫 Data；
            //      API14 才改名為 AtkEventData。指向的東西一樣，只是名字不同。
            var eventData = (AtkEventData*)recv.Data;
            var mouseButtonId = eventData != null ? eventData->MouseData.ButtonId : (byte)255;
            var dragDropMouseButtonId = eventData != null ? eventData->DragDropData.MouseButtonId : (byte)255;

            // Track last-hovered dragdrop (for polling-based triggers).
            // IMPORTANT:
            // - For ArmouryBoard, only capture from drag-drop rollover/click (avoids bad union reads on some builds).
            // - For Inventory/Saddlebags, we also allow MouseOver by resolving the DDI from atkEvent->Node (safe path).
            var addonName = args.AddonName ?? string.Empty;
            var allowMouseOverCapture =
                addonName.Equals("Inventory", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("InventoryBuddy2", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid0", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerGrid", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals("RetainerSellList", StringComparison.OrdinalIgnoreCase) ||
                addonName.Equals(FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase);

            // Always track which addon the cursor is currently interacting with, even if we can't resolve a DDI.
            // This enables a safe MMB "Sort" path that doesn't dereference drag/drop pointers.
            if (eventType is AtkEventType.MouseOver or AtkEventType.MouseOut or AtkEventType.DragDropRollOver or AtkEventType.DragDropRollOut ||
                eventType is AtkEventType.ListItemRollOver or AtkEventType.ListItemRollOut)
            {
                try
                {
                    var ab = (AtkUnitBase*)args.Addon.Address;
                    var id = ab != null ? ab->Id : 0u;
                    if (eventType is AtkEventType.MouseOut or AtkEventType.DragDropRollOut or AtkEventType.ListItemRollOut)
                    {
                        lastHoverAddon = null;
                    }
                    else if (id != 0)
                    {
                        lastHoverAddon = (addonName, id, now);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // FreeCompanyChest: remember which compartment tab is selected based on its button click param.
            // This allows MMB organize to operate ONLY on the active tab, even if you don't hover an item slot first.
            if (addonName.Equals(FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase) &&
                eventType == AtkEventType.ButtonClick)
            {
                try
                {
                    var ab = (AtkUnitBase*)args.Addon.Address;
                    var id = ab != null ? ab->Id : 0u;
                    if (id != 0 && TryMapCompanyChestTabParamToPage(recv.EventParam, out var selectedPage))
                    {
                        lastSelectedCompanyChestPage = (selectedPage, id, now);
                        ObserveCompanyChestTabFromAtkValues(ab, selectedPage);
                        if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                            Log.Information($"[QuickTransfer] FC Chest selected tab: param={recv.EventParam} -> {selectedPage} (addonId={id})");

                        // If we're currently organizing a different tab, stop immediately.
                        if (companyChestOrganize.Active &&
                            (companyChestOrganize.OwnerAddonId == 0 || companyChestOrganize.OwnerAddonId == id) &&
                            companyChestOrganize.Pages is { Length: 1 } &&
                            companyChestOrganize.Pages[0] != selectedPage)
                        {
                            companyChestOrganize.Active = false;
                            companyChestOrganize.WaitingForApply = false;
                            companyChestOrganize.WaitObservedChangeAtMs = 0;
                            if (Configuration.DebugMode)
                                Log.Information($"[QuickTransfer] (MMB) Company Chest tab changed to {selectedPage}; stopping previous organize run.");
                        }
                    }
                    else if (Configuration.DebugMode && id != 0 && now - lastFcChestTabUnmappedLogMs >= 250)
                    {
                        lastFcChestTabUnmappedLogMs = now;
                        Log.Information($"[QuickTransfer] FC Chest tab param unmapped: param={recv.EventParam} (addonId={id})");
                    }
                }
                catch
                {
                    // ignore
                }
            }

            if (eventType is AtkEventType.DragDropRollOut || (allowMouseOverCapture && eventType is AtkEventType.MouseOut))
            {
                lastHoverDdi = null;
                lastHoverAddonName = string.Empty;
            }
            else if (eventType is AtkEventType.DragDropRollOver or AtkEventType.DragDropClick ||
                     (allowMouseOverCapture && eventType is AtkEventType.MouseOver))
            {
                if (TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var hAddonId, out var hDdi) && hDdi != null)
                {
                    var ptr = (nint)hDdi;
                    if (ptr >= MinLikelyPointer)
                    {
                        lastHoverDdi = (ptr, hAddonId, now);
                        lastHoverAddonName = addonName;
                    }

                    // For FC Chest, decode the hovered page while the payload is fresh.
                    if (addonName.Equals(FreeCompanyChestAddonName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            if (TryGetSlotFromDragDropInterface(hDdi, out var hoverInvType, out var hoverSlotIndex))
                            {
                                if (IsCompanyChestType(hoverInvType))
                                {
                                    lastHoverCompanyChestPage = (hoverInvType, hAddonId, now);

                                    // [TC] 批次搬運要用到格號，趁 payload 還有效先存下來。
                                    if (hoverSlotIndex >= 0)
                                        lastHoverCompanyChestCell = (hoverInvType, hoverSlotIndex, hAddonId, now);
                                }
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    // Debug: confirm hover capture occasionally.
                    if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
                    {
                        Log.Information($"[QuickTransfer] HoverCapture: Addon='{args.AddonName}', EventType={eventType}, Param={recv.EventParam}, DDI=0x{((nint)hDdi):X}");
                    }
                }
            }

            // Determine middle-click reliably:
            // - Prefer Win32 VK_MBUTTON (works regardless of client button id mapping)
            // - Fall back to the event's button flags (some builds use bitmask: middle=0x04)
            // - As a final fallback, try Dalamud KeyState (may not include mouse buttons on some builds)
            bool? middleDown = null;
            try
            {
                const VirtualKey vkMButton = (VirtualKey)0x04; // VK_MBUTTON
                middleDown = KeyState[vkMButton];
            }
            catch
            {
                // ignore
            }

            var asyncMiddleDown = IsVkDown(0x04); // VK_MBUTTON
            var isMiddleByMask = ((mouseButtonId & 0x04) != 0) || ((dragDropMouseButtonId & 0x04) != 0);
            var isMiddle = asyncMiddleDown || isMiddleByMask || middleDown == true;

            // Always log (rate-limited) in DebugMode so we can see which event types fire on MMB for this client.
            if (Configuration.DebugMode && now - lastReceiveEventDebugLogMs >= 250)
            {
                lastReceiveEventDebugLogMs = now;
                Log.Information(
                    $"[QuickTransfer] PreReceiveEvent: Addon='{args.AddonName}', Type={eventType}, Param={recv.EventParam}, " +
                    $"MouseBtn={mouseButtonId} (0x{mouseButtonId:X2}), DragBtn={dragDropMouseButtonId} (0x{dragDropMouseButtonId:X2}), " +
                    $"MaskMiddle={(isMiddleByMask ? "1" : "0")}, AsyncMiddle={(asyncMiddleDown ? "1" : "0")}, KeyStateMiddle={(middleDown?.ToString() ?? "n/a")}");
            }

            if (now - lastMiddleClickSortMs < 250)
                return;

            // Only proceed on click events; other events can be noisy and don't carry slot payloads.
            if (eventType != AtkEventType.DragDropClick &&
                eventType != AtkEventType.MouseClick &&
                eventType != AtkEventType.MouseDown)
                return;

            if (!isMiddle)
                return;

            if (!TryGetDragDropInterfaceFromReceiveEvent(args, recv, eventType, eventData, out var addonId, out var ddi))
                return;
            if (!TryGetSlotFromDragDropInterface(ddi, out var invType, out var slot))
                return;

            // Do not require a non-empty slot; "Sort" can be invoked from empty slots/spaces.

            pendingMiddleClickSortRequest = (invType, slot, addonId, now);
            pendingMiddleClickSortUntilMs = now + 1500;
            lastMiddleClickSortMs = now;

            // Prevent the underlying UI from processing the click further.
            var atkEvent2 = (AtkEvent*)recv.AtkEvent;
            if (atkEvent2 != null)
                atkEvent2->SetEventIsHandled();
        }
        catch
        {
            // ignore
        }
    }

    // Use AtkValueHelpers (implementation moved to AtkValueHelpers.cs)

    private void OnInputNumericPreSetup(AddonEvent type, AddonArgs args)
    {
        try
        {
            // [TC] 批次搬運也會用到僱員／鞍囊的數量視窗，不能只綁在公會儲物櫃的開關上。
            if (!Configuration.EnableCompanyChest && !Configuration.EnableBulkTransfer)
                return;

            if (!pendingCompanyChestNumericArmed)
                return;

            if (!string.Equals(args.AddonName, InputNumericAddonName, StringComparison.OrdinalIgnoreCase))
                return;

            // Only touch this dialog while one of the containers we drive is open
            // (avoid affecting unrelated InputNumeric uses).
            // [TC] 上游只認公會儲物櫃，所以僱員／鞍囊的數量視窗永遠不會被自動確認，批次搬運會卡在這裡。
            if (!IsCompanyChestOpen() && !IsRetainerOpen() && !IsSaddlebagOpen())
                return;

            if (args is not AddonSetupArgs setup)
                return;

            var values = (AtkValue*)setup.AtkValues;
            var count = (int)setup.AtkValueCount;
            if (values == null || count < 7)
                return;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] InputNumeric PreSetup (armed): AtkValueCount={count}");

            // Guard against cross-confirmation: only touch the prompt we intended (store/remove/sell).
            if (pendingNumericKind != PendingNumericKind.None)
            {
                var prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(values[6]) : string.Empty;
                // [TC] 同上：先用資料表分類，認不出來才退回英文判斷。
                var promptKind = GameStrings.ClassifyQuantityPrompt(prompt);
                if (pendingNumericKind == PendingNumericKind.Store)
                {
                    if (promptKind == GameStrings.QuantityPromptKind.Remove)
                        return;
                    if (promptKind == GameStrings.QuantityPromptKind.Unknown &&
                        !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                if (pendingNumericKind == PendingNumericKind.Remove)
                {
                    if (promptKind == GameStrings.QuantityPromptKind.Store)
                        return;
                    if (promptKind == GameStrings.QuantityPromptKind.Unknown &&
                        !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                if (pendingNumericKind == PendingNumericKind.Sell &&
                    promptKind == GameStrings.QuantityPromptKind.Unknown &&
                    !prompt.Contains("sell", StringComparison.OrdinalIgnoreCase))
                    return;
                // For "Move" we accept any prompt while the Company Chest is open (used for internal stack/organize moves).
            }

            // Standard InputNumeric layout (also used by SimpleTweaks):
            // [2]=min (UInt), [3]=max (UInt), [4]=default (UInt), [6]=prompt text (String)
            if (values[2].Type != AtkValueType.UInt || values[3].Type != AtkValueType.UInt || values[4].Type != AtkValueType.UInt)
            {
                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] InputNumeric PreSetup: unexpected types: [2]={values[2].Type}, [3]={values[3].Type}, [4]={values[4].Type}");
                return;
            }

            var min = values[2].UInt;
            var max = values[3].UInt;
            var desired = max < min ? min : max;

            // Log current/default if present.
            if (Configuration.DebugMode)
            {
                var curStr = (count > 5 && values[5].Type == AtkValueType.UInt) ? values[5].UInt.ToString() : "n/a";
                Log.Information($"[QuickTransfer] InputNumeric PreSetup: min={min}, max={max}, default={values[4].UInt}, current={curStr}");
            }

            values[4].UInt = desired; // default
            if (count > 5)
            {
                if (values[5].Type == AtkValueType.UInt)
                    values[5].UInt = desired; // some layouts have current (UInt)
                else if (values[5].Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8)
                    WriteUtf8InPlace(values[5].String, desired.ToString()); // some builds use String current
            }

            if (Configuration.DebugMode)
            {
                var prompt = values[6].Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(values[6]) : string.Empty;
                Log.Information($"[QuickTransfer] InputNumeric PreSetup: prompt='{prompt}', min={min}, max={max}, setDefault={desired}");
            }

        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] InputNumeric PreSetup failed.");
        }
    }

    // Use ContextMenuHandler
    private bool TryAutoSelectAndClose(AgentInventoryContext* agent, ModifierMode mode, out string chosenText, out int chosenIndex)
    {
        chosenText = string.Empty;
        chosenIndex = -1;

        var agentAddonId = agent->AgentInterface.GetAddonId();
        if (agentAddonId == 0)
            return false;

        var addon = GetAddonById(agentAddonId);
        if (addon == null)
        {
            var cm = GameGui.GetAddonByName("ContextMenu", 1);
            addon = (AtkUnitBase*)cm.Address;
        }

        if (addon == null)
            return false;

        return TryAutoSelectAndClose(agent, addon, mode, out chosenText, out chosenIndex);
    }

    private bool TryAutoSelectAndClose(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon, ModifierMode mode, out string chosenText, out int chosenIndex)
    {
        return ContextMenuHandler.TryAutoSelectAndClose(
            agent,
            contextMenuAddon,
            (ContextMenuHandler.ModifierMode)mode,
            Configuration,
            out chosenText,
            out chosenIndex,
            ref pendingCloseContextMenuAtMs);
    }

    // Use ContextMenuHandler
    private bool TrySelectSortAndClose(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon, out string chosenText, out int chosenIndex)
        => ContextMenuHandler.TrySelectSortAndClose(agent, contextMenuAddon, out chosenText, out chosenIndex);

    private bool StartCompanyChestDeposit(InventoryType sourceType, uint sourceSlot)
    {
        try
        {
            if (!Configuration.EnableCompanyChest)
                return false;
            if (RaptureAtkModule.Instance() == null)
                return false;
            if (!IsCompanyChestOpen())
                return false;
            if (!IsPlayerInventoryType(sourceType))
                return false;

            if (!TryGetItemInfo(sourceType, (int)sourceSlot, out var itemId, out var isHq, out var qty))
                return false;

            var now = Environment.TickCount64;
            companyChestDeposit = new CompanyChestDepositState
            {
                Active = true,
                SourceType = sourceType,
                SourceSlot = sourceSlot,
                ItemId = itemId,
                IsHq = isHq,
                NextAttemptAtMs = now,
                ExpiresAtMs = now + 12000,
                Steps = 0,
                LastQty = qty,
                WaitForQtyChangeUntilMs = 0,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ProcessCompanyChestDeposit(long now)
    {
        if (!companyChestDeposit.Active)
            return;

        // Stop if conditions no longer apply.
        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !IsCompanyChestOpen())
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (now >= companyChestDeposit.ExpiresAtMs || companyChestDeposit.Steps >= 40)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // If we just issued a move, wait for the source stack quantity to change (or for the dialog to appear).
        // This prevents spamming the same move over and over when the game hasn't applied it yet.
        if (companyChestDeposit.WaitForQtyChangeUntilMs > 0 && now <= companyChestDeposit.WaitForQtyChangeUntilMs)
        {
            if (TryGetVisibleAddon(InputNumericAddonName, out _))
                return;

            if (TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var _, out var _, out var qNow) &&
                qNow != companyChestDeposit.LastQty)
            {
                companyChestDeposit.LastQty = qNow;
                companyChestDeposit.WaitForQtyChangeUntilMs = 0;
            }
            else
            {
                return;
            }
        }

        // Don't issue a new move while the quantity dialog is open.
        if (TryGetVisibleAddon(InputNumericAddonName, out _))
            return;

        if (now < companyChestDeposit.NextAttemptAtMs)
            return;

        if (!TryGetItemInfo(companyChestDeposit.SourceType, (int)companyChestDeposit.SourceSlot, out var itemId, out var isHq, out var qty) ||
            itemId == 0 ||
            qty == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        // If the slot changed (user moved/split), stop to avoid moving the wrong thing.
        if (itemId != companyChestDeposit.ItemId || isHq != companyChestDeposit.IsHq)
        {
            companyChestDeposit.Active = false;
            return;
        }

        var pages = GetCompanyChestInventoryTypes();
        if (pages.Length == 0)
        {
            companyChestDeposit.Active = false;
            return;
        }

        var maxStack = GetItemStackSize(itemId);
        var needsQuantityConfirm = qty > 1 && maxStack > 1;

        // Prefer stacking into an existing stack; otherwise use the first empty slot.
        if (!TryFindCompanyChestBestStackSlot(pages, itemId, isHq, maxStack, out var destType, out var destSlot) &&
            !TryFindCompanyChestFirstEmptySlot(pages, out destType, out destSlot))
        {
            companyChestDeposit.Active = false;
            return;
        }

        if (!TryCompanyChestMoveItem(companyChestDeposit.SourceType, companyChestDeposit.SourceSlot, destType, destSlot, needsQuantityConfirm))
        {
            companyChestDeposit.Active = false;
            return;
        }

        companyChestDeposit.Steps++;
        companyChestDeposit.NextAttemptAtMs = now + (needsQuantityConfirm ? 600 : 350);
        companyChestDeposit.LastQty = qty;
        companyChestDeposit.WaitForQtyChangeUntilMs = now + (needsQuantityConfirm ? 2000 : 1200);

        if (Configuration.AutoConfirmCompanyChestQuantity && needsQuantityConfirm)
        {
            pendingCompanyChestNumericConfirmUntilMs = now + 1500;
            pendingCompanyChestNumericConfirmAttempts = 0;
            pendingCompanyChestNumericArmed = true;
            pendingNumericKind = PendingNumericKind.Store;
            pendingCompanyChestNumericValueSet = false;
            pendingCompanyChestNumericValueSetAtMs = 0;
            pendingCompanyChestNumericDesired = 0;
            pendingCompanyChestNumericHalf = false;
        }

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (Shift+RClick) Company Chest deposit step {companyChestDeposit.Steps}: {companyChestDeposit.SourceType} slot={companyChestDeposit.SourceSlot} -> {destType} slot={destSlot} (qty={qty}, stackMax={maxStack}).");
    }

    private void StartCompanyChestOrganize(long now)
    {
        if (!Configuration.EnableCompanyChest || !IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
            return;

        if (now <= companyChestBusyUntilMs)
            return;

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            // Already running; don't reset progress on repeated MMB presses.
            companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, now + 20000);
            if (Configuration.DebugMode)
                Log.Information("[QuickTransfer] (MMB) Company Chest organize already running; ignoring restart.");
            return;
        }

        companyChestBusyHits = 0;

        var ownerAddonId = 0u;
        try
        {
            if (TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex) && fcc != null)
                ownerAddonId = fcc->Id;
        }
        catch
        {
            // ignore
        }

        var pages = GetCompanyChestInventoryTypes();
        if (pages.Length == 0)
            return;

        companyChestOrganize = new CompanyChestOrganizeState
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Steps = 0,
            Phase = 0, // Stack merge -> compact -> sort
            Pages = pages,
            WaitingForApply = false,
            WaitUntilMs = 0,
            WaitStuckCount = 0,
            WaitObservedChangeAtMs = 0,
        };

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (pages=[{string.Join(", ", pages)}]).");
    }

    private void StartCompanyChestOrganize(long now, InventoryType selectedPage)
    {
        if (!IsCompanyChestType(selectedPage))
        {
            StartCompanyChestOrganize(now);
            return;
        }

        if (!Configuration.EnableCompanyChest || !IsCompanyChestOpen() || RaptureAtkModule.Instance() == null)
            return;

        if (now <= companyChestBusyUntilMs)
            return;

        if (companyChestOrganize.Active && now < companyChestOrganize.ExpiresAtMs)
        {
            // If a different tab is requested, stop the old run and restart on the new tab.
            if (companyChestOrganize.Pages is { Length: 1 } && companyChestOrganize.Pages[0] != selectedPage)
            {
                companyChestOrganize.Active = false;
            }
            else
            {
                // Same tab: extend expiry but don't reset progress.
                companyChestOrganize.ExpiresAtMs = Math.Max(companyChestOrganize.ExpiresAtMs, now + 20000);
                if (Configuration.DebugMode)
                    Log.Information("[QuickTransfer] (MMB) Company Chest organize already running; ignoring restart.");
                return;
            }
        }

        companyChestBusyHits = 0;

        var ownerAddonId = 0u;
        try
        {
            if (TryGetVisibleAddon(FreeCompanyChestAddonName, out var fcc, WideAddonSearchMaxIndex) && fcc != null)
                ownerAddonId = fcc->Id;
        }
        catch
        {
            // ignore
        }

        companyChestOrganize = new CompanyChestOrganizeState
        {
            Active = true,
            OwnerAddonId = ownerAddonId,
            NextAttemptAtMs = now,
            ExpiresAtMs = now + 60000,
            Steps = 0,
            Phase = 0, // Stack merge -> compact -> sort
            Pages = new[] { selectedPage },
            WaitingForApply = false,
            WaitUntilMs = 0,
            WaitStuckCount = 0,
            WaitObservedChangeAtMs = 0,
        };

        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (MMB) Company Chest organize started (selectedPage={selectedPage}).");
    }

    private void ProcessCompanyChestOrganize(long now)
    {
        void LogSkip(string reason)
        {
            if (!Configuration.DebugMode)
                return;

            // Rate-limit skip logs; only log when the reason changes or every 2s.
            if (!string.Equals(lastCompanyChestOrganizeSkipReason, reason, StringComparison.Ordinal) ||
                now - lastCompanyChestOrganizeSkipLogMs >= 2000)
            {
                lastCompanyChestOrganizeSkipReason = reason;
                lastCompanyChestOrganizeSkipLogMs = now;
                Log.Information($"[QuickTransfer] (MMB) Company Chest organize waiting: {reason}");
            }
        }

        (int stepDelayMs, int stabilizeMs, int applyTimeoutMs, int noApplyBackoffMs, int pageRetryMs, int numericStepDelayMs, int numericApplyTimeoutMs) GetTimings()
        {
            // Start fast, but if the server begins rejecting actions (busyHits>0), automatically slow down.
            var tier = Math.Clamp(companyChestBusyHits, 0, 2);
            return tier switch
            {
                0 => (750, 300, 1300, 650, 350, 1500, 3200),
                1 => (1000, 450, 1800, 900, 500, 2200, 4500),
                _ => (1300, 650, 2500, 1200, 750, 3000, 6000),
            };
        }

        if (!companyChestOrganize.Active)
            return;

        if (now <= companyChestBusyUntilMs)
        {
            LogSkip("busy backoff");
            return;
        }

        if (!Configuration.EnableCompanyChest || RaptureAtkModule.Instance() == null || !IsCompanyChestOpen())
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (now >= companyChestOrganize.ExpiresAtMs || companyChestOrganize.Steps >= 140)
        {
            companyChestOrganize.Active = false;
            return;
        }

        if (TryGetVisibleAddon(InputNumericAddonName, out _))
        {
            LogSkip("InputNumeric visible");
            return;
        }

        // If the selected page isn't loaded yet (loading spinner), wait.
        try
        {
            var pages0 = companyChestOrganize.Pages ?? Array.Empty<InventoryType>();
            var inv0 = InventoryManager.Instance();
            if (inv0 != null && pages0.Length > 0)
            {
                var allLoaded = true;
                foreach (var p in pages0)
                {
                    if (!IsContainerLoaded(inv0, p))
                    {
                        allLoaded = false;
                        break;
                    }

                    // Extra readiness guard: even if the container reports loaded, slot pointers can be null for a bit.
                    // If we treat that as "no moves", the organizer will instantly finish without doing anything.
                    if (inv0->GetInventorySlot(p, 0) == null)
                    {
                        allLoaded = false;
                        break;
                    }
                }

                if (!allLoaded)
                {
                    var t = GetTimings();
                    companyChestOrganize.NextAttemptAtMs = now + t.pageRetryMs;
                    LogSkip($"pages not ready yet; waiting. pages=[{string.Join(", ", pages0)}]");
                    return;
                }
            }
        }
        catch
        {
            // ignore
        }

        // Wait for the previous move to apply (Company Chest actions can lag and will fail/spam errors if we spam moves).
        if (companyChestOrganize.WaitingForApply)
        {
            try
            {
                var inv = InventoryManager.Instance();
                if (inv != null)
                {
                    var s = inv->GetInventorySlot(companyChestOrganize.WaitSrcType, (int)companyChestOrganize.WaitSrcSlot);
                    var d = inv->GetInventorySlot(companyChestOrganize.WaitDstType, (int)companyChestOrganize.WaitDstSlot);

                    var sId = s != null ? s->ItemId : 0u;
                    var sQty = s != null ? s->Quantity : 0;
                    var dId = d != null ? d->ItemId : 0u;
                    var dQty = d != null ? d->Quantity : 0;

                    var applied =
                        sId != companyChestOrganize.WaitSrcItemId ||
                        sQty != companyChestOrganize.WaitSrcQty ||
                        dId != companyChestOrganize.WaitDstItemId ||
                        dQty != companyChestOrganize.WaitDstQty;

                    if (applied)
                    {
                        var t = GetTimings();
                        // We saw a change; wait a short stabilization window in case the server rejects and rolls back.
                        if (companyChestOrganize.WaitObservedChangeAtMs == 0)
                        {
                            companyChestOrganize.WaitObservedChangeAtMs = now;
                            LogSkip("waiting for apply (stabilize)");
                            return;
                        }

                        if (now - companyChestOrganize.WaitObservedChangeAtMs < t.stabilizeMs)
                        {
                            LogSkip("waiting for apply (stabilize)");
                            return;
                        }

                        // Stable: allow next move.
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitUntilMs = 0;
                        companyChestOrganize.WaitStuckCount = 0;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;
                    }
                    else if (companyChestOrganize.WaitObservedChangeAtMs != 0)
                    {
                        // We previously saw a change, but now we're back to the pre-snapshot: likely a server rejection rollback.
                        companyChestBusyHits = Math.Min(companyChestBusyHits + 1, 10);
                        var backoffMs = (long)Math.Min(60000, 5000 * (1 << Math.Min(companyChestBusyHits - 1, 4)));
                        companyChestBusyUntilMs = Math.Max(companyChestBusyUntilMs, now + backoffMs);
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;

                        if (Configuration.DebugMode)
                            Log.Information($"[QuickTransfer] (MMB) Company Chest move rolled back; treating as busy. backoff={backoffMs}ms (hit {companyChestBusyHits}).");

                        if (companyChestBusyHits >= 3)
                            companyChestOrganize.Active = false;
                        return;
                    }
                    else if (now <= companyChestOrganize.WaitUntilMs)
                    {
                        LogSkip("waiting for apply");
                        return;
                    }
                    else
                    {
                        companyChestOrganize.WaitStuckCount++;
                        companyChestOrganize.WaitingForApply = false;
                        companyChestOrganize.WaitObservedChangeAtMs = 0;
                        if (companyChestOrganize.WaitStuckCount >= 3)
                        {
                            companyChestOrganize.Active = false;
                            if (Configuration.DebugMode)
                            {
                                Log.Information("[QuickTransfer] (MMB) Company Chest organize stalled (no inventory change observed); stopping to avoid spam.");
                                Log.Information(
                                    $"[QuickTransfer] (MMB) Stall snapshot: src={companyChestOrganize.WaitSrcType} slot={companyChestOrganize.WaitSrcSlot} " +
                                    $"was(id={companyChestOrganize.WaitSrcItemId},qty={companyChestOrganize.WaitSrcQty}) now(id={sId},qty={sQty}); " +
                                    $"dst={companyChestOrganize.WaitDstType} slot={companyChestOrganize.WaitDstSlot} " +
                                    $"was(id={companyChestOrganize.WaitDstItemId},qty={companyChestOrganize.WaitDstQty}) now(id={dId},qty={dQty});");
                            }
                            return;
                        }

                        // Back off a bit and retry.
                        var t = GetTimings();
                        companyChestOrganize.NextAttemptAtMs = now + t.noApplyBackoffMs;
                        LogSkip("no apply observed; backoff");
                        return;
                    }
                }
            }
            catch
            {
                // ignore; fall through
            }
        }

        if (now < companyChestOrganize.NextAttemptAtMs)
        {
            LogSkip("cooldown");
            return;
        }

        var pages = companyChestOrganize.Pages ?? Array.Empty<InventoryType>();
        if (pages.Length == 0)
        {
            companyChestOrganize.Active = false;
            return;
        }

        // Phase 0: merge stacks where possible. (Disabled for FC chest by starting at Phase=1.)
        if (companyChestOrganize.Phase == 0)
        {
            if (TryFindCompanyChestMergeMove(pages, out var srcType, out var srcSlot, out var dstType, out var dstSlot, out var needsNumeric))
            {
                // Snapshot BEFORE issuing the move (so we can detect when it applies).
                var preSrcId = 0u;
                var preDstId = 0u;
                var preSrcQty = 0;
                var preDstQty = 0;
                try
                {
                    var inv = InventoryManager.Instance();
                    if (inv != null)
                    {
                        TryGetSlotSnapshot(inv, srcType, srcSlot, out preSrcId, out preSrcQty);
                        TryGetSlotSnapshot(inv, dstType, dstSlot, out preDstId, out preDstQty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!TryCompanyChestMoveItem(srcType, srcSlot, dstType, dstSlot, needsNumeric))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.WaitingForApply = true;
                companyChestOrganize.WaitSrcType = srcType;
                companyChestOrganize.WaitSrcSlot = srcSlot;
                companyChestOrganize.WaitSrcItemId = preSrcId;
                companyChestOrganize.WaitSrcQty = preSrcQty;
                companyChestOrganize.WaitDstType = dstType;
                companyChestOrganize.WaitDstSlot = dstSlot;
                companyChestOrganize.WaitDstItemId = preDstId;
                companyChestOrganize.WaitDstQty = preDstQty;
                var t = GetTimings();
                companyChestOrganize.WaitUntilMs = now + (needsNumeric ? t.numericApplyTimeoutMs : t.applyTimeoutMs);
                companyChestOrganize.WaitObservedChangeAtMs = 0;

                companyChestOrganize.Steps++;
                // Even after a move applies, add a small delay; Company Chest actions are more latency-sensitive.
                companyChestOrganize.NextAttemptAtMs = now + (needsNumeric ? t.numericStepDelayMs : t.stepDelayMs);

                if (Configuration.AutoConfirmCompanyChestQuantity && needsNumeric)
                {
                    pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                    pendingCompanyChestNumericConfirmAttempts = 0;
                    pendingCompanyChestNumericArmed = true;
                    pendingNumericKind = PendingNumericKind.Move;
                    pendingCompanyChestNumericValueSet = false;
                    pendingCompanyChestNumericValueSetAtMs = 0;
                    pendingCompanyChestNumericDesired = 0;
                    pendingCompanyChestNumericHalf = false;
                    ArmSuppressInputNumeric(now, 1500);
                }

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {srcType} slot={srcSlot} -> {dstType} slot={dstSlot} (phase=stack, numeric={needsNumeric}).");
                return;
            }

            // No more merges; move on to compaction.
            companyChestOrganize.Phase = 1;
        }

        // Phase 1: compact items to fill empty slots from the start.
        if (TryFindCompanyChestCompactionMove(pages, out var cSrcType, out var cSrcSlot, out var cDstType, out var cDstSlot))
        {
            // Snapshot BEFORE issuing the move (so we can detect when it applies).
            var preSrcId = 0u;
            var preDstId = 0u;
            var preSrcQty = 0;
            var preDstQty = 0;
            try
            {
                var inv = InventoryManager.Instance();
                if (inv != null)
                {
                    TryGetSlotSnapshot(inv, cSrcType, cSrcSlot, out preSrcId, out preSrcQty);
                    TryGetSlotSnapshot(inv, cDstType, cDstSlot, out preDstId, out preDstQty);
                }
            }
            catch
            {
                // ignore
            }

            if (!TryCompanyChestMoveItem(cSrcType, cSrcSlot, cDstType, cDstSlot, keepAliveForInputNumeric: false))
            {
                companyChestOrganize.Active = false;
                return;
            }

            companyChestOrganize.WaitingForApply = true;
            companyChestOrganize.WaitSrcType = cSrcType;
            companyChestOrganize.WaitSrcSlot = cSrcSlot;
            companyChestOrganize.WaitSrcItemId = preSrcId;
            companyChestOrganize.WaitSrcQty = preSrcQty;
            companyChestOrganize.WaitDstType = cDstType;
            companyChestOrganize.WaitDstSlot = cDstSlot;
            companyChestOrganize.WaitDstItemId = preDstId;
            companyChestOrganize.WaitDstQty = preDstQty;
            var t = GetTimings();
            companyChestOrganize.WaitUntilMs = now + t.applyTimeoutMs;
            companyChestOrganize.WaitObservedChangeAtMs = 0;

            companyChestOrganize.Steps++;
            companyChestOrganize.NextAttemptAtMs = now + t.stepDelayMs;

            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {cSrcType} slot={cSrcSlot} -> {cDstType} slot={cDstSlot} (phase=compact).");
            return;
        }

        // No more compaction moves; proceed to sorting.
        if (companyChestOrganize.Phase == 1)
            companyChestOrganize.Phase = 2;

        // Phase 2: reorder stacks by (UI category, itemId, HQ), mimicking the feel of Sort/itemsort.
        if (companyChestOrganize.Phase == 2)
        {
            if (TryFindCompanyChestSortMove(pages, out var sSrcType, out var sSrcSlot, out var sDstType, out var sDstSlot))
            {
                // Snapshot BEFORE issuing the move (so we can detect when it applies).
                var preSrcId = 0u;
                var preDstId = 0u;
                var preSrcQty = 0;
                var preDstQty = 0;
                try
                {
                    var inv = InventoryManager.Instance();
                    if (inv != null)
                    {
                        TryGetSlotSnapshot(inv, sSrcType, sSrcSlot, out preSrcId, out preSrcQty);
                        TryGetSlotSnapshot(inv, sDstType, sDstSlot, out preDstId, out preDstQty);
                    }
                }
                catch
                {
                    // ignore
                }

                if (!TryCompanyChestMoveItem(sSrcType, sSrcSlot, sDstType, sDstSlot, keepAliveForInputNumeric: false))
                {
                    companyChestOrganize.Active = false;
                    return;
                }

                companyChestOrganize.WaitingForApply = true;
                companyChestOrganize.WaitSrcType = sSrcType;
                companyChestOrganize.WaitSrcSlot = sSrcSlot;
                companyChestOrganize.WaitSrcItemId = preSrcId;
                companyChestOrganize.WaitSrcQty = preSrcQty;
                companyChestOrganize.WaitDstType = sDstType;
                companyChestOrganize.WaitDstSlot = sDstSlot;
                companyChestOrganize.WaitDstItemId = preDstId;
                companyChestOrganize.WaitDstQty = preDstQty;
                var t = GetTimings();
                companyChestOrganize.WaitUntilMs = now + t.applyTimeoutMs;
                companyChestOrganize.WaitObservedChangeAtMs = 0;

                companyChestOrganize.Steps++;
                companyChestOrganize.NextAttemptAtMs = now + t.stepDelayMs;

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] (MMB) Company Chest organize step {companyChestOrganize.Steps}: {sSrcType} slot={sSrcSlot} -> {sDstType} slot={sDstSlot} (phase=sort).");
                return;
            }
        }

        // Done (no more moves).
        if (Configuration.DebugMode)
            Log.Information($"[QuickTransfer] (MMB) Company Chest organize done; no moves found. pages=[{string.Join(", ", pages)}]");
        companyChestOrganize.Active = false;
    }

    private bool TryFindCompanyChestMergeMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot,
        out bool needsNumeric)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;
        needsNumeric = false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;

        // Find a destination stack with free space, then a later source stack of same item to merge.
        foreach (var dt in pages)
        {
            for (var di = 0; di < slotCap; di++)
            {
                var d = inv->GetInventorySlot(dt, di);
                if (d == null)
                    break;
                if (d->ItemId == 0 || d->Quantity <= 0)
                    continue;

                var itemId = d->ItemId;
                var isHq = d->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var maxStack = GetItemStackSize(itemId);
                if (maxStack <= 1)
                    continue;

                var free = (int)maxStack - d->Quantity;
                if (free <= 0)
                    continue;

                // Find a later stack to merge into this one.
                var foundDest = false;
                var destGlobalIndex = 0;
                for (var pi = 0; pi < pages.Length; pi++)
                {
                    if (pages[pi] != dt) continue;
                    destGlobalIndex = pi * slotCap + di;
                    foundDest = true;
                    break;
                }
                if (!foundDest)
                    continue;

                for (var p = 0; p < pages.Length; p++)
                {
                    var st = pages[p];
                    for (var si = 0; si < slotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                            break;
                        if (s->ItemId == 0 || s->Quantity <= 0)
                            continue;
                        if (s->ItemId != itemId)
                            continue;
                        var sHq = s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                        if (sHq != isHq)
                            continue;

                        var srcGlobalIndex = p * slotCap + si;
                        if (srcGlobalIndex <= destGlobalIndex)
                            continue;
                        if (st == dt && si == di)
                            continue;

                        // Merging stacks usually prompts for quantity.
                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        // Be conservative: if the client shows InputNumeric for this move, we must keep the move state alive.
                        // We auto-confirm max, so this will stack as much as possible.
                        needsNumeric = true;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestCompactionMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;

        // Find first empty, then next non-empty after it.
        for (var dp = 0; dp < pages.Length; dp++)
        {
            var dt = pages[dp];
            for (var di = 0; di < slotCap; di++)
            {
                var d = inv->GetInventorySlot(dt, di);
                if (d == null)
                    break;
                if (d->ItemId != 0)
                    continue;

                // Found empty destination.
                for (var sp = dp; sp < pages.Length; sp++)
                {
                    var st = pages[sp];
                    var start = sp == dp ? di + 1 : 0;
                    for (var si = start; si < slotCap; si++)
                    {
                        var s = inv->GetInventorySlot(st, si);
                        if (s == null)
                            break;
                        if (s->ItemId == 0 || s->Quantity <= 0)
                            continue;

                        srcType = st;
                        srcSlot = (uint)si;
                        dstType = dt;
                        dstSlot = (uint)di;
                        return true;
                    }
                }

                return false;
            }
        }

        return false;
    }

    private readonly struct ChestSortKey : IComparable<ChestSortKey>
    {
        public readonly uint Category;
        public readonly uint ItemId;
        public readonly byte Hq;
        public ChestSortKey(uint category, uint itemId, bool isHq)
        {
            Category = category;
            ItemId = itemId;
            Hq = (byte)(isHq ? 1 : 0);
        }

        public int CompareTo(ChestSortKey other)
        {
            var c = Category.CompareTo(other.Category);
            if (c != 0) return c;
            c = ItemId.CompareTo(other.ItemId);
            if (c != 0) return c;
            return Hq.CompareTo(other.Hq); // NQ first, HQ after
        }
    }

    private static bool TryFindCompanyChestSortMove(
        InventoryType[] pages,
        out InventoryType srcType,
        out uint srcSlot,
        out InventoryType dstType,
        out uint dstSlot)
    {
        srcType = default;
        srcSlot = 0;
        dstType = default;
        dstSlot = 0;

        if (pages.Length != 1)
            return false;

        var page = pages[0];
        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        InventoryContainer* c;
        try { c = inv->GetInventoryContainer(page); }
        catch { return false; }
        if (c == null || !c->IsLoaded || c->Size <= 0)
            return false;

        var size = (int)c->Size;
        if (size <= 1)
            return false;

        // Build keys for current slots.
        var keys = new ChestSortKey[size];
        var empty = new bool[size];
        for (var i = 0; i < size; i++)
        {
            var it = c->GetInventorySlot(i);
            if (it == null || it->ItemId == 0 || it->Quantity <= 0)
            {
                empty[i] = true;
                keys[i] = default;
                continue;
            }

            var id = it->ItemId;
            var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            var cat = GetItemUiCategory(id);
            keys[i] = new ChestSortKey(cat, id, hq);
        }

        // Ensure empties are at the end (safety; compaction phase should mostly handle this).
        for (var i = 0; i < size; i++)
        {
            if (!empty[i]) continue;
            for (var j = i + 1; j < size; j++)
            {
                if (!empty[j])
                {
                    srcType = page;
                    srcSlot = (uint)j;
                    dstType = page;
                    dstSlot = (uint)i;
                    return true;
                }
            }
            break;
        }

        // Selection-sort step: for first index i, if there is a smaller key later, swap/move it into i.
        // This uses HandleItemMove's swap behavior for occupied destinations.
        for (var i = 0; i < size; i++)
        {
            if (empty[i])
                break;

            var best = i;
            for (var j = i + 1; j < size; j++)
            {
                if (empty[j]) break; // empties at end
                if (keys[j].CompareTo(keys[best]) < 0)
                    best = j;
            }

            if (best != i && keys[best].CompareTo(keys[i]) < 0)
            {
                srcType = page;
                srcSlot = (uint)best;
                dstType = page;
                dstSlot = (uint)i;
                return true;
            }
        }

        return false;
    }

    private bool TryCompanyChestMoveItem(
        InventoryType sourceType,
        uint sourceSlot,
        InventoryType destType,
        uint destSlot,
        bool keepAliveForInputNumeric)
    {
        var module = RaptureAtkModule.Instance();
        if (module == null)
            return false;

        // IMPORTANT:
        // HandleItemMove expects InventoryType values (e.g. Inventory1=0, FreeCompanyPage1=20000),
        // not "container ids" like 48/57.
        var srcInvType = (uint)sourceType;
        var dstInvType = (uint)destType;

        nint localValuesAlloc = 0;
        nint localRetAlloc = 0;
        try
        {
            AtkValue* values;
            AtkValue* ret;
            if (keepAliveForInputNumeric)
            {
                // Keep alive across the InputNumeric dialog.
                if (pendingMoveOutValuePtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveOutValuePtr); } catch { /* ignore */ }
                    pendingMoveOutValuePtr = 0;
                }
                if (pendingMoveAtkValuesPtr != 0)
                {
                    try { Marshal.FreeHGlobal(pendingMoveAtkValuesPtr); } catch { /* ignore */ }
                    pendingMoveAtkValuesPtr = 0;
                }

                pendingMoveOutValuePtr = Marshal.AllocHGlobal(sizeof(AtkValue));
                pendingMoveAtkValuesPtr = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                pendingMoveCreatedAtMs = Environment.TickCount64;
                pendingMoveSawInputNumeric = false;
                pendingMoveOutValueFreeAtMs = pendingMoveCreatedAtMs + 8000;

                ret = (AtkValue*)pendingMoveOutValuePtr;
                values = (AtkValue*)pendingMoveAtkValuesPtr;
            }
            else
            {
                localRetAlloc = Marshal.AllocHGlobal(sizeof(AtkValue));
                localValuesAlloc = Marshal.AllocHGlobal(sizeof(AtkValue) * 4);
                ret = (AtkValue*)localRetAlloc;
                values = (AtkValue*)localValuesAlloc;
            }

            ret->Type = AtkValueType.Int;
            ret->Int = 0;

            for (var i = 0; i < 4; i++) values[i].Type = AtkValueType.UInt;
            values[0].UInt = srcInvType;
            values[1].UInt = sourceSlot;
            values[2].UInt = dstInvType;
            values[3].UInt = destSlot;

            module->HandleItemMove(ret, values, 4);

            if (Configuration.DebugMode)
            {
                var inv = InventoryManager.Instance();
                TryGetSlotSnapshot(inv, sourceType, sourceSlot, out var sId, out var sQty);
                TryGetSlotSnapshot(inv, destType, destSlot, out var dId, out var dQty);
                Log.Information(
                    $"[QuickTransfer] (MMB) CompanyChest HandleItemMove: retInt={ret->Int}, " +
                    $"src={sourceType} slot={sourceSlot} (id={sId},qty={sQty}) -> dst={destType} slot={destSlot} (id={dId},qty={dQty}), keepAlive={keepAliveForInputNumeric}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Company Chest HandleItemMove failed.");
            return false;
        }
        finally
        {
            if (localRetAlloc != 0) { try { Marshal.FreeHGlobal(localRetAlloc); } catch { /* ignore */ } }
            if (localValuesAlloc != 0) { try { Marshal.FreeHGlobal(localValuesAlloc); } catch { /* ignore */ } }
        }
    }

    private static InventoryType[] GetSplitCandidateTypes(InventoryType sourceType)
    {
        // Prefer the same container first, then fall back to other pages of the same "kind".
        // This mirrors the game's "Split" behavior which places the new stack into an empty slot in the same inventory group.
        var tmp = new List<InventoryType>(capacity: 8);
        void Add(InventoryType t)
        {
            for (var i = 0; i < tmp.Count; i++)
                if (tmp[i] == t)
                    return;
            tmp.Add(t);
        }

        Add(sourceType);
        if (IsPlayerInventoryType(sourceType))
        {
            Add(InventoryType.Inventory1);
            Add(InventoryType.Inventory2);
            Add(InventoryType.Inventory3);
            Add(InventoryType.Inventory4);
        }
        else if (IsSaddlebagType(sourceType))
        {
            Add(InventoryType.SaddleBag1);
            Add(InventoryType.SaddleBag2);
            Add(InventoryType.PremiumSaddleBag1);
            Add(InventoryType.PremiumSaddleBag2);
        }
        else if (IsRetainerType(sourceType))
        {
            Add(InventoryType.RetainerPage1);
            Add(InventoryType.RetainerPage2);
            Add(InventoryType.RetainerPage3);
            Add(InventoryType.RetainerPage4);
            Add(InventoryType.RetainerPage5);
            Add(InventoryType.RetainerPage6);
            Add(InventoryType.RetainerPage7);
        }

        return tmp.ToArray();
    }

    private static bool TryFindFirstEmptySlotForSplit(
        InventoryManager* inv,
        InventoryType[] candidates,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;
        if (inv == null || candidates.Length == 0)
            return false;

        const int slotCap = 80;
        foreach (var t in candidates)
        {
            if (!IsContainerLoaded(inv, t))
                continue;

            for (var i = 0; i < slotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;
                if (it->ItemId == 0)
                {
                    destType = t;
                    destSlot = (uint)i;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryStartSplitHalfMove(
        InventoryType sourceType,
        uint sourceSlot,
        long now,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            // Only supported for inventory-like containers.
            if (!IsPlayerInventoryType(sourceType) && !IsSaddlebagType(sourceType) && !IsRetainerType(sourceType))
            {
                reason = "unsupported container";
                return false;
            }

            if (TryGetVisibleAddon(InputNumericAddonName, out _))
            {
                reason = "InputNumeric visible";
                return false;
            }

            if (!TryGetItemInfo(sourceType, (int)sourceSlot, out var itemId, out _, out var qty) || qty <= 1)
            {
                reason = "no stack";
                return false;
            }

            var maxStack = GetItemStackSize(itemId);
            if (maxStack <= 1)
            {
                reason = "not stackable";
                return false;
            }

            var inv = InventoryManager.Instance();
            if (inv == null)
            {
                reason = "InventoryManager null";
                return false;
            }

            var candidates = GetSplitCandidateTypes(sourceType);
            if (!TryFindFirstEmptySlotForSplit(inv, candidates, out var destType, out var destSlot))
            {
                reason = "no empty slot";
                return false;
            }

            // Trigger a move to an empty slot; the game will prompt for quantity.
            if (!TryCompanyChestMoveItem(sourceType, sourceSlot, destType, destSlot, keepAliveForInputNumeric: true))
            {
                reason = "HandleItemMove failed";
                return false;
            }

            // Auto-confirm half (best-effort). We reuse the existing InputNumeric handler.
            if (Configuration.AutoConfirmCompanyChestQuantity)
            {
                pendingCompanyChestNumericConfirmUntilMs = now + 1500;
                pendingCompanyChestNumericConfirmAttempts = 0;
                pendingCompanyChestNumericArmed = true;
                pendingNumericKind = PendingNumericKind.Move;
                pendingCompanyChestNumericValueSet = false;
                pendingCompanyChestNumericValueSetAtMs = 0;
                pendingCompanyChestNumericDesired = 0;
                pendingCompanyChestNumericHalf = true;
            }

            reason = $"dst={destType} slot={destSlot}";
            return true;
        }
        catch
        {
            reason = "exception";
            return false;
        }
    }

    private static bool TryFindCompanyChestFirstEmptySlot(
        InventoryType[] pages,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        foreach (var t in pages)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var item = inv->GetInventorySlot(t, i);
                if (item == null)
                    break;
                if (item->ItemId == 0)
                {
                    destType = t;
                    destSlot = (uint)i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestBestStackSlot(
        InventoryType[] pages,
        uint itemId,
        bool isHq,
        uint maxStack,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        if (pages.Length == 0 || itemId == 0 || maxStack <= 1)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        var bestFree = 0;
        foreach (var t in pages)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;

                if (it->ItemId != itemId)
                    continue;

                var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq != isHq)
                    continue;

                var qty = it->Quantity;
                if (qty <= 0)
                    continue;

                var free = (int)maxStack - qty;
                if (free <= 0)
                    continue;

                if (free > bestFree)
                {
                    bestFree = free;
                    destType = t;
                    destSlot = (uint)i;
                }
            }
        }

        return bestFree > 0;
    }

    private bool TrySetInputNumericToMax(AtkUnitBase* inputNumeric, PendingNumericKind kind)
    {
        try
        {
            if (inputNumeric == null)
                return false;
            if (inputNumeric->AtkValues == null || inputNumeric->AtkValuesCount < 7)
                return false;

            var minValue = inputNumeric->AtkValues + 2;
            var maxValue = inputNumeric->AtkValues + 3;
            var defaultValue = inputNumeric->AtkValues + 4;
            var currentValue = inputNumeric->AtkValuesCount > 5 ? (inputNumeric->AtkValues + 5) : null;
            var promptVal = inputNumeric->AtkValues + 6;
            var prompt = promptVal->Type is AtkValueType.String or AtkValueType.ManagedString ? ReadAtkValueString(*promptVal) : string.Empty;

            // Guard: only confirm prompts we expect.
            // [TC] 先用遊戲資料表判斷這是「放入」還是「取出」的數量視窗；
            //      認得出來就以它為準，認不出來（非台服／未知提示）才退回上游的英文判斷。
            var promptKind = GameStrings.ClassifyQuantityPrompt(prompt);
            if (kind == PendingNumericKind.Store)
            {
                if (promptKind == GameStrings.QuantityPromptKind.Remove)
                    return false;
                if (promptKind == GameStrings.QuantityPromptKind.Unknown &&
                    !prompt.Contains("store", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (kind == PendingNumericKind.Remove)
            {
                if (promptKind == GameStrings.QuantityPromptKind.Store)
                    return false;
                if (promptKind == GameStrings.QuantityPromptKind.Unknown &&
                    !prompt.Contains("remove", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            // Trade dialogs may be localized; if we're in Trade mode and Trade window is open, accept it
            // (similar to how Split works - we trust the context rather than requiring exact prompt text)
            if (kind == PendingNumericKind.Trade && !prompt.Contains("trade", StringComparison.OrdinalIgnoreCase))
            {
                // Fallback: if Trade window is open and we're expecting Trade, accept it anyway
                // (prompt might be localized or say "How many would you like to trade?" etc.)
                if (!IsTradeOpen())
                    return false;
            }
            // Vendor sell dialogs may be localized; accept if prompt contains "sell" or vendor is open.
            if (kind == PendingNumericKind.Sell && !prompt.Contains("sell", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsVendorOpen())
                    return false;
            }

            if (minValue->Type != AtkValueType.UInt || maxValue->Type != AtkValueType.UInt || defaultValue->Type != AtkValueType.UInt)
                return false;

            var min = minValue->UInt;
            var max = maxValue->UInt;

            // Split dialogs are localized and can also be emitted by InventoryExpansion without "split" in the prompt.
            // Accept if either:
            // - prompt contains "split" (English), OR
            // - max matches the expected qty-1 we recorded when arming the Split.
            if (kind == PendingNumericKind.Split && !prompt.Contains("split", StringComparison.OrdinalIgnoreCase))
            {
                var nowMs = Environment.TickCount64;
                var expectedMax = pendingSplitExpectedMax;
                var okByExpected = expectedMax != 0 && nowMs <= pendingSplitExpectedUntilMs && max == expectedMax;
                if (!okByExpected)
                    return false;
            }
            uint desired;
            if (pendingCompanyChestNumericHalf)
            {
                // Split/remove half as evenly as possible.
                // - Split: max is usually (qty-1), so use (max+1)/2.
                // - Remove: max is usually qty, so use max/2.
                if (kind == PendingNumericKind.Remove && max <= 1)
                    return false;
                if (kind == PendingNumericKind.Split && max == 0)
                    return false;
                desired = kind == PendingNumericKind.Remove ? (max / 2) : ((max + 1) / 2);
                pendingCompanyChestNumericHalf = false;
            }
            else if (pendingCompanyChestNumericDesired != 0)
            {
                desired = pendingCompanyChestNumericDesired;
            }
            else
            {
                // Default: max (clamped).
                desired = max < min ? min : max;
            }

            if (desired < min)
                desired = min;
            if (desired > max)
                desired = max;
            if (desired == 0 && min > 0)
                desired = min;

            pendingCompanyChestNumericDesired = desired;

            var beforeDefault = defaultValue->UInt;
            var beforeCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
            var beforeCurrentStr = (currentValue != null && currentValue->Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                ? ReadAtkValueString(*currentValue)
                : string.Empty;

            // Many InputNumeric uses have both "default" and "current" values; set both so OK uses max.
            defaultValue->UInt = desired;
            if (currentValue != null)
            {
                if (currentValue->Type == AtkValueType.UInt)
                {
                    currentValue->UInt = desired;
                }
                else if (currentValue->Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                {
                    // This dialog uses a String "current quantity" slot on your client build.
                    // Overwrite the existing buffer in-place (max is <= 999 so this is safe).
                    var s = desired.ToString();
                    WriteUtf8InPlace(currentValue->String, s);
                }
            }

            // Critical: Some builds don't actually use AtkValues for the editable quantity; they use the NumericInput component's Raw/Evaluated strings.
            // Set that too, if present, so the OK action applies "desired" instead of a stale value (e.g. 2).
            TrySetInputNumericComponentValue(inputNumeric, desired);

            if (Configuration.DebugMode)
            {
                var curType = currentValue != null ? currentValue->Type.ToString() : "n/a";
                var afterCurrentUInt = (currentValue != null && currentValue->Type == AtkValueType.UInt) ? currentValue->UInt : 0U;
                var afterCurrentStr = (currentValue != null && currentValue->Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                    ? ReadAtkValueString(*currentValue)
                    : string.Empty;
                Log.Information($"[QuickTransfer] InputNumeric(Update): prompt='{prompt}', min={min}, max={max}, default {beforeDefault}->{defaultValue->UInt}, currentUInt {beforeCurrentUInt}->{afterCurrentUInt}, currentStr '{beforeCurrentStr}'->'{afterCurrentStr}' (idx5 type {curType})");
            }

            return true;
        }
        catch
        {
            // ignore
            return false;
        }
    }

    private static void TrySetInputNumericComponentValue(AtkUnitBase* inputNumeric, uint desired)
    {
        try
        {
            if (inputNumeric == null)
                return;
            if (inputNumeric->UldManager.NodeList == null)
                return;

            var desiredStr = desired.ToString();

            for (var i = 0; i < inputNumeric->UldManager.NodeListCount; i++)
            {
                var node = inputNumeric->UldManager.NodeList[i];
                if (node == null)
                    continue;

                if ((int)node->Type < 1000)
                    continue;

                var compNode = (AtkComponentNode*)node;
                var comp = compNode->Component;
                if (comp == null)
                    continue;

                if (comp->GetComponentType() != ComponentType.NumericInput)
                    continue;

                var ni = (AtkComponentNumericInput*)comp;

                // RawString / EvaluatedString are Utf8String.
                WriteUtf8StringInPlace(&ni->RawString, desiredStr);
                WriteUtf8StringInPlace(&ni->EvaluatedString, desiredStr);

                // The authoritative value used by OK is the numeric input's internal Value.
                // Setting strings alone can leave the internal Value at its old value (commonly 2).
                ni->SetValue((int)desired);

                // Update cursor to end.
                ni->CursorPos = (ushort)desiredStr.Length;
                ni->SelectionStart = ni->CursorPos;
                ni->SelectionEnd = ni->CursorPos;

                // Only need first numeric input.
                return;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteUtf8StringInPlace(FFXIVClientStructs.FFXIV.Client.System.String.Utf8String* s, string value)
    {
        if (s == null)
            return;

        WriteUtf8InPlace(s->StringPtr, value);
        s->StringLength = value.Length;
        s->BufUsed = value.Length + 1;
    }

    private static void WriteUtf8InPlace(byte* dst, string value)
    {
        if (dst == null)
            return;

        var bytes = Encoding.UTF8.GetBytes(value);
        // write bytes + null terminator
        for (var i = 0; i < bytes.Length; i++)
            dst[i] = bytes[i];
        dst[bytes.Length] = 0;
    }

    private bool TrySelectRemoveFromCompanyChestContextMenu()
    {
        try
        {
            var ctxAddr = GameGui.GetAddonByName("ContextMenu", 1).Address;
            if (ctxAddr == nint.Zero)
                return false;

            var ctxMenu = (AddonContextMenu*)ctxAddr;
            if (ctxMenu == null)
                return false;

            // Find the list component and pick the row whose label is "Remove".
            // FreeCompanyChest uses a Default context menu, so the AgentInventoryContext index-based selection does not apply.
            for (uint listId = 1; listId <= 6; listId++)
            {
                var list = ctxMenu->GetComponentListById(listId);
                if (list == null)
                    continue;

                var itemCount = list->GetItemCount();
                if (itemCount <= 0 || itemCount > 64)
                    continue;

                for (var i = 0; i < itemCount; i++)
                {
                    var labelPtr = list->GetItemLabel(i);
                    if (labelPtr == null)
                        continue;

                    var label = Marshal.PtrToStringUTF8(new IntPtr(labelPtr))?.TrimEnd('\0') ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] ContextMenu listId={listId} row={i} label='{label}'");

                    // [TC] 部隊儲物櫃的「取出」在台服是 Addon#2950，走同一套語言中立比對。
                    if (!ContextMenuHandler.ContextLabelMatches(ContextMenuHandler.AutoContextAction.RemoveFromCompanyChest, label))
                        continue;

                    // Trigger via callback payload (matches the inventory context menu pattern).
                    GenerateCallback((AtkUnitBase*)ctxMenu, 0, i, 0U, 0, 0);

                    // Close slightly later (immediate close can cancel the action).
                    pendingCloseContextMenuAtMs = Environment.TickCount64 + 50;

                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] Triggered Company Chest 'Remove' (listId={listId}, row={i}).");
                    return true;
                }
            }

            // Fallback: keep old string-scan (helpful for debugging), but don't attempt a blind click.
            return ContextMenuContainsString((AtkUnitBase*)ctxMenu, "Remove");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to select Remove from Company Chest context menu.");
            return false;
        }
    }

    private bool ContextMenuContainsString(AtkUnitBase* ctxAddon, string needle)
    {
        try
        {
            if (ctxAddon == null || ctxAddon->AtkValues == null || ctxAddon->AtkValuesCount <= 0)
                return false;

            var count = Math.Min((int)ctxAddon->AtkValuesCount, 128);
            if (Configuration.DebugMode)
                Log.Information($"[QuickTransfer] ContextMenu AtkValuesCount={ctxAddon->AtkValuesCount} (scanning {count}).");
            for (var i = 0; i < count; i++)
            {
                var v = ctxAddon->AtkValues[i];
                if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8))
                    continue;

                var s = ReadAtkValueString(v);
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                if (Configuration.DebugMode)
                    Log.Information($"[QuickTransfer] ContextMenu AtkValue[{i}] = '{s}'");

                if (s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    if (Configuration.DebugMode)
                        Log.Information($"[QuickTransfer] ContextMenu contains '{needle}' (found '{s}' at AtkValue[{i}]).");
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    // (removed) GetMoveContainerId:
    // Company Chest transfers must use InventoryType values directly with RaptureAtkModule.HandleItemMove.
    private static bool TryFindFirstCompanyChestEmptySlot(
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        // This method is static; use all known pages as a fallback, callers should prefer GetCompanyChestInventoryTypes().
        var invTypes = Enum.GetValues<InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .ToArray();
        if (invTypes.Length == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        // Most FC chest tabs are 50 slots; we keep a conservative cap.
        const int slotCap = 80;

        foreach (var t in invTypes)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var item = inv->GetInventorySlot(t, i);
                if (item == null)
                    break;

                if (item->ItemId == 0)
                {
                    destType = t;
                    destSlot = (uint)i;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindCompanyChestExistingStackSlot(
        uint itemId,
        bool isHq,
        uint maxStack,
        out InventoryType destType,
        out uint destSlot)
    {
        destType = default;
        destSlot = 0;

        var invTypes = Enum.GetValues<InventoryType>()
            .Where(IsCompanyChestType)
            .OrderBy(v => (int)v)
            .ToArray();
        if (invTypes.Length == 0)
            return false;

        var inv = InventoryManager.Instance();
        if (inv == null)
            return false;

        const int slotCap = 80;
        var bestFree = 0;
        foreach (var t in invTypes)
        {
            for (var i = 0; i < slotCap; i++)
            {
                var it = inv->GetInventorySlot(t, i);
                if (it == null)
                    break;

                if (it->ItemId != itemId)
                continue;

                var hq = it->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if (hq != isHq)
                    continue;

                if (maxStack <= 1)
                    continue;

                var qty = it->Quantity;
                if (qty <= 0)
                    continue;

                var free = (int)maxStack - qty;
                if (free <= 0)
                    continue;

                // Pick the stack with the most free space, to reduce "moves 1 then repeats" behavior.
                if (free > bestFree)
                {
                    bestFree = free;
                    destType = t;
                    destSlot = (uint)i;
                }
            }
        }

        return bestFree > 0;
    }

    // Use InventoryHelpers
    private static uint GetItemStackSize(uint itemId) => InventoryHelpers.GetItemStackSize(itemId);
    private static uint GetItemUiCategory(uint itemId) => InventoryHelpers.GetItemUiCategory(itemId);

    // Use InventoryHelpers
    private static bool TryGetItemInfo(InventoryType type, int slot, out uint itemId, out bool isHq, out uint quantity)
        => InventoryHelpers.TryGetItemInfo(type, slot, out itemId, out isHq, out quantity);

    private ModifierMode? GetModifierModeLatched(long nowMs)
    {
        const int latchWindowMs = 180;
        var altDown = KeyState[VirtualKey.MENU] || nowMs - lastAltSeenMs <= latchWindowMs;
        var ctrlDown = KeyState[VirtualKey.CONTROL] || nowMs - lastCtrlSeenMs <= latchWindowMs;
        var shiftDown = KeyState[VirtualKey.SHIFT] || nowMs - lastShiftSeenMs <= latchWindowMs;

        // [TC] Ctrl＋Shift 要排在最前面判斷，否則會被下面的 Ctrl 分支先吃掉。
        if (Configuration.EnableBulkTransfer && ctrlDown && shiftDown && !altDown)
            return ModifierMode.Bulk;

        if (altDown)
            return ModifierMode.Alt;
        if (ctrlDown)
            return ModifierMode.Ctrl;
        if (shiftDown)
            return ModifierMode.Shift;
        return null;
    }

    private void TryCloseCurrentContextMenu(AgentInventoryContext* agent)
    {
        try
        {
            var agentAddonId = agent->AgentInterface.GetAddonId();
            if (agentAddonId != 0)
            {
                var addon = GetAddonById(agentAddonId);
                if (addon != null)
                {
                    CloseContextMenuAddon(agent, addon);
                    return;
                }
            }
        }
        catch
        {
            // ignore
        }

        // Fallback attempt.
        try
        {
            var cm = GameGui.GetAddonByName("ContextMenu", 1);
            if (!cm.IsNull)
                CloseContextMenuAddon(agent, (AtkUnitBase*)cm.Address);
        }
        catch
        {
            // ignore
        }
    }

    private void DebugDumpContextMenu(AgentInventoryContext* agent, int maxItems)
    {
        try
        {
            var max = Math.Min(Math.Min(agent->ContextItemCount, 64), maxItems);
            for (var i = 0; i < max; i++)
            {
                var param = agent->EventParams[agent->ContexItemStartIndex + i];
                if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString))
                    continue;

                var text = ReadAtkValueString(param);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                Log.Information($"[QuickTransfer] Menu idx={i}: '{text}'");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[QuickTransfer] Failed to dump context menu.");
        }
    }

    // Use InventoryHelpers for these functions
    /// <summary>兵裝庫視窗是否開著。上游沒有這個 helper，因為它原本不需要。</summary>
    private static bool IsArmouryOpen() => InventoryHelpers.IsAnyAddonVisibleAnyIndex(ArmouryAddonNames);

    private static bool IsPlayerInventoryType(InventoryType inventoryType) => InventoryHelpers.IsPlayerInventoryType(inventoryType);
    private static bool IsArmouryType(InventoryType inventoryType) => InventoryHelpers.IsArmouryType(inventoryType);
    private static bool IsSaddlebagOpen() => InventoryHelpers.IsSaddlebagOpen();
    private static bool IsSaddlebagType(InventoryType inventoryType) => InventoryHelpers.IsSaddlebagType(inventoryType);
    private static bool IsRetainerOpen() => InventoryHelpers.IsRetainerOpen();
    private static bool IsRetainerType(InventoryType inventoryType) => InventoryHelpers.IsRetainerType(inventoryType);
    private static bool IsCompanyChestOpen() => InventoryHelpers.IsCompanyChestOpen();
    private static bool IsTradeOpen() => InventoryHelpers.IsTradeOpen();
    private static bool IsVendorOpen() => InventoryHelpers.IsVendorOpen();
    private static bool IsCompanyChestType(InventoryType inventoryType) => InventoryHelpers.IsCompanyChestType(inventoryType);
    private static bool IsAddonVisibleAnyIndex(string addonName, int maxIndex = 6) => InventoryHelpers.IsAddonVisibleAnyIndex(addonName, maxIndex);
    private static bool TryGetVisibleAddon(string addonName, out AtkUnitBase* addon, int maxIndex = 6) => InventoryHelpers.TryGetVisibleAddon(addonName, out addon, maxIndex);

    private bool TryMapCompanyChestTabParamToPage(int eventParam, out InventoryType page)
    {
        page = default;
        try
        {
            // IMPORTANT: this mapping is about tab clicks, not how many compartments we *want* to operate on.
            // So we always consider all possible item pages (up to 5), even if the user configured fewer.
            var pages = GetAllCompanyChestItemPages();
            if (pages.Length == 0)
                return false;

            // Free Company Chest (your UI):
            // param=1 -> Items tab 1 (FreeCompanyPage1)
            // param=2 -> Items tab 2 (FreeCompanyPage2)
            // param=3 -> Items tab 3 (FreeCompanyPage3)
            // param=4 -> Items tab 4 (FreeCompanyPage4) [FC rank unlock]
            // param=5 -> Items tab 5 (FreeCompanyPage5) [FC rank unlock]
            // param=6 -> Crystals (NOT an item page)
            if (eventParam < 1 || eventParam > pages.Length)
                return false;

            page = pages[eventParam - 1];
            return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    // Use InventoryHelpers (implementation moved to InventoryHelpers.cs)

    // Use ContextMenuHandler
    private void CloseContextMenuAddon(AgentInventoryContext* agent, AtkUnitBase* contextMenuAddon)
        => ContextMenuHandler.CloseContextMenuAddon(agent, contextMenuAddon);

    // Use ContextMenuHandler
    private static bool ContextLabelMatches(AutoContextAction desiredAction, string menuText)
        => ContextMenuHandler.ContextLabelMatches((ContextMenuHandler.AutoContextAction)desiredAction, menuText);

    // Use AtkValueHelpers
    private static string ReadAtkValueString(AtkValue v) => AtkValueHelpers.ReadAtkValueString(v);
    private static string ReadUtf8(byte* ptr) => AtkValueHelpers.ReadUtf8(ptr);
    private static AtkUnitBase* GetAddonById(uint id) => AtkValueHelpers.GetAddonById(id);
    private static void GenerateCallback(AtkUnitBase* unitBase, params object[] values) => AtkValueHelpers.GenerateCallback(unitBase, values);
    private static void MakeAddonInvisible(AtkUnitBase* addon) => AtkValueHelpers.MakeAddonInvisible(addon);
    private static void MakeAddonVisible(AtkUnitBase* addon) => AtkValueHelpers.MakeAddonVisible(addon);
}


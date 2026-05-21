using System.Collections.Generic;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using RealisticSurveying.GameContent;
using RealisticSurveying.Network;

namespace RealisticSurveying;

public sealed class RealisticSurveyingModSystem : ModSystem
{
    private const int    TheoHighlightGroupId = 1338;
    private const string LabelChannelName     = "realisticsurveying-labels";

    private ICoreClientAPI?           _capi;
    private IClientNetworkChannel?    _clientLabelChannel;
    private GuiDialogTheodoliteScope? _scopeDialog;
    private GuiDialogRopeHud?         _ropeHud;

    /// <summary>
    /// True after the player explicitly closes the scope (Esc) while a measurement is still in progress.  
    /// Prevents the tick from immediately re-opening it.
    /// Cleared automatically when the measurement state returns to idle.
    /// </summary>
    private bool _scopeSuppressed;

    // Extended picking range while theodolite is measuring
    private float _origPickingRange;
    private bool  _rangeExtended;

    /// <summary>
    /// Exposed so <see cref="GuiDialogSurveyMap"/> can send label-update packets.
    /// </summary>
    public IClientNetworkChannel? ClientLabelChannel => _clientLabelChannel;

    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockClass("BlockSurveyMarker", typeof(BlockSurveyMarker));
        api.RegisterItemClass("ItemTopographicMap", typeof(ItemTopographicMap));
        api.RegisterItemClass("ItemSurveyRope",     typeof(ItemSurveyRope));
        api.RegisterItemClass("ItemTheodolite",     typeof(ItemTheodolite));
        api.RegisterItemClass("ItemSextant",        typeof(ItemSextant));
    }

    public override void StartServerSide(ICoreServerAPI sapi)
    {
        sapi.Network
            .RegisterChannel(LabelChannelName)
            .RegisterMessageType<NodeLabelPacket>()
            .SetMessageHandler<NodeLabelPacket>(OnNodeLabelPacket)
            .RegisterMessageType<MapNamePacket>()
            .SetMessageHandler<MapNamePacket>(OnMapNamePacket)
            .RegisterMessageType<DeleteNodePacket>()
            .SetMessageHandler<DeleteNodePacket>(OnDeleteNodePacket)
            .RegisterMessageType<DeleteEdgePacket>()
            .SetMessageHandler<DeleteEdgePacket>(OnDeleteEdgePacket)
            .RegisterMessageType<MapStrokePacket>()
            .SetMessageHandler<MapStrokePacket>(OnMapStrokePacket)
            .RegisterMessageType<StrokeUndoPacket>()
            .SetMessageHandler<StrokeUndoPacket>(OnStrokeUndoPacket);
    }

    public override void StartClientSide(ICoreClientAPI capi)
    {
        _capi = capi;

        new Harmony("realisticsurveying").PatchAll(GetType().Assembly);

        _clientLabelChannel = capi.Network
            .RegisterChannel(LabelChannelName)
            .RegisterMessageType<NodeLabelPacket>()
            .RegisterMessageType<MapNamePacket>()
            .RegisterMessageType<DeleteNodePacket>()
            .RegisterMessageType<DeleteEdgePacket>()
            .RegisterMessageType<MapStrokePacket>()
            .RegisterMessageType<StrokeUndoPacket>();

        capi.Event.KeyDown += OnKeyDown;
        capi.World.RegisterGameTickListener(_ => UpdateTheoHighlight(capi), 200);
        capi.World.RegisterGameTickListener(_ => UpdateRopeHud(capi), 100);
    }

    // Packet handlers

    private static void OnMapNamePacket(IServerPlayer fromPlayer, MapNamePacket packet)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap mapItem) return;

        mapItem.SetMapName(mapSlot.Itemstack, packet.Name ?? "");
        mapSlot.MarkDirty();
    }

    private static void OnDeleteNodePacket(IServerPlayer fromPlayer, DeleteNodePacket packet)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap mapItem) return;
        if (packet.NodeIndex < 0 || packet.NodeIndex >= mapItem.NodeCount(mapSlot.Itemstack)) return;

        mapItem.DeleteNode(mapSlot.Itemstack, packet.NodeIndex);
        mapSlot.MarkDirty();
    }

    private static void OnDeleteEdgePacket(IServerPlayer fromPlayer, DeleteEdgePacket packet)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap mapItem) return;
        if (packet.EdgeIndex < 0 || packet.EdgeIndex >= mapItem.EdgeCount(mapSlot.Itemstack)) return;

        mapItem.DeleteEdge(mapSlot.Itemstack, packet.EdgeIndex);
        mapSlot.MarkDirty();
    }

    private static void OnNodeLabelPacket(IServerPlayer fromPlayer, NodeLabelPacket packet)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap mapItem) return;
        if (packet.NodeIndex < 0 || packet.NodeIndex >= mapItem.NodeCount(mapSlot.Itemstack)) return;

        mapItem.SetNodeLabel(mapSlot.Itemstack, packet.NodeIndex, packet.Label ?? "");
        mapSlot.MarkDirty();
    }

    private static void OnMapStrokePacket(IServerPlayer fromPlayer, MapStrokePacket packet)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap mapItem) return;
        mapItem.AddStroke(mapSlot.Itemstack, packet.ColorIndex, packet.Width, packet.Points);
        mapSlot.MarkDirty();
    }

    private static void OnStrokeUndoPacket(IServerPlayer fromPlayer, StrokeUndoPacket _)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap mapItem) return;
        mapItem.RemoveLastStroke(mapSlot.Itemstack);
        mapSlot.MarkDirty();
    }

    private static ItemSlot? FindMapSlot(IPlayer player)
    {
        IInventory? hotbar = player.InventoryManager.GetHotbarInventory();

        // 1. Prefer the explicitly selected slot
        int sel = player.Entity.WatchedAttributes.GetInt(ItemTopographicMap.KeySelectedSlot, -2);
        if (sel >= -1)
        {
            ItemSlot? selSlot = sel == -1
                ? player.InventoryManager.OffhandHotbarSlot
                : hotbar?[sel];
            if (selSlot?.Itemstack?.Item is ItemTopographicMap sm && sm.IsInitialized(selSlot.Itemstack))
                return selSlot;
        }

        // 2. Fallback: offhand, then left-to-right hotbar scan
        ItemSlot offhand = player.InventoryManager.OffhandHotbarSlot;
        if (offhand?.Itemstack?.Item is ItemTopographicMap om && om.IsInitialized(offhand.Itemstack))
            return offhand;

        if (hotbar == null) return null;
        for (int i = 0; i < hotbar.Count; i++)
        {
            ItemSlot s = hotbar[i];
            if (s?.Itemstack?.Item is ItemTopographicMap hm && hm.IsInitialized(s.Itemstack))
                return s;
        }
        return null;
    }

    public override void Dispose()
    {
        _ropeHud?.TryClose();
        _ropeHud?.Dispose();
        _ropeHud = null;
        base.Dispose();
    }

    /// <summary>
    /// Boosts the client's picking range to 100 blocks so the game's built-in block-selection raycast can reach 
    /// distant survey markers while the theodolite is in an active measurement state.  
    /// The extended block selection is sent to the server and used in <see cref="ItemTheodolite.OnHeldInteractStart"/> without server-side distance validation 
    /// (item interactions are not distance-checked by VS, only block break/place is).
    /// </summary>
    private void ExtendPickingRange(ICoreClientAPI capi)
    {
        if (_rangeExtended) return;
        _origPickingRange = capi.World.Player.WorldData.PickingRange;
        capi.World.Player.WorldData.PickingRange = 200f;
        _rangeExtended = true;
    }

    private void RestorePickingRange(ICoreClientAPI capi)
    {
        if (!_rangeExtended) return;
        capi.World.Player.WorldData.PickingRange = _origPickingRange;
        _rangeExtended = false;
    }

    // Client: Esc key handler

    private void OnKeyDown(KeyEvent e)
    {
        // GlKeys.Escape == 50
        if (e.KeyCode != (int)GlKeys.Escape) return;
        if (_scopeDialog?.IsOpened() != true) return;

        _scopeDialog.TryClose();
        e.Handled = true;
    }

    // Client: scope close callback

    /// <summary>
    /// Called by <see cref="GuiDialogTheodoliteScope.OnGuiClosed"/>.
    /// If the measurement is still in progress when the scope closes (i.e. the player pressed Esc), suppress auto-reopening until the measurement resets to idle.
    /// </summary>
    private void OnScopeDialogClosed()
    {
        if (_capi == null) return;
        IClientPlayer? player = _capi.World.Player;
        int theoState = player?.Entity?.WatchedAttributes.GetInt(ItemTheodolite.KeyState, 0) ?? 0;
        if (theoState >= 1)
            _scopeSuppressed = true;
    }

    // Client: rope distance HUD (100 ms)

    private void UpdateRopeHud(ICoreClientAPI capi)
    {
        IClientPlayer player = capi.World.Player;
        if (player == null) { CloseRopeHud(); return; }

        ItemStack? stack = player.InventoryManager?.ActiveHotbarSlot?.Itemstack;
        int ropeState = player.Entity.WatchedAttributes.GetInt(ItemSurveyRope.KeyState, 0);

        if (stack?.Item is not ItemSurveyRope || ropeState != 1)
        {
            CloseRopeHud();
            return;
        }

        int ax = player.Entity.WatchedAttributes.GetInt(ItemSurveyRope.KeyMarkerAX);
        int ay = player.Entity.WatchedAttributes.GetInt(ItemSurveyRope.KeyMarkerAY);
        int az = player.Entity.WatchedAttributes.GetInt(ItemSurveyRope.KeyMarkerAZ);

        double dx = player.Entity.Pos.X - (ax + 0.5);
        double dy = player.Entity.Pos.Y - (ay + 0.5);
        double dz = player.Entity.Pos.Z - (az + 0.5);
        float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

        if (_ropeHud == null)
        {
            _ropeHud = new GuiDialogRopeHud(capi);
            _ropeHud.TryOpen();
        }
        _ropeHud.UpdateDistance(dist);
    }

    private void CloseRopeHud()
    {
        if (_ropeHud == null) return;
        _ropeHud.TryClose();
        _ropeHud.Dispose();
        _ropeHud = null;
    }

    // Client: theodolite highlight + scope tick (200 ms)

    private void UpdateTheoHighlight(ICoreClientAPI capi)
    {
        IClientPlayer player = capi.World.Player;
        if (player == null) return;

        ItemStack? stack = player.InventoryManager?.ActiveHotbarSlot?.Itemstack;

        if (stack?.Item is ItemTheodolite)
        {
            // Scope: auto-open when station is planted, auto-close when idle
            int theoState = player.Entity.WatchedAttributes.GetInt(ItemTheodolite.KeyState, 0);

            if (theoState == 0)
            {
                // Measurement returned to idle (complete or cancelled) to reset suppression and close any open scope.
                _scopeSuppressed = false;
                RestorePickingRange(capi);
                if (_scopeDialog?.IsOpened() == true)
                    _scopeDialog.TryClose();
            }
            else
            {
                // In an active measurement state (1 = station planted, 2 = first sight done).
                // Extend picking range so distant markers are reachable.
                ExtendPickingRange(capi);

                if (!_scopeSuppressed && _scopeDialog?.IsOpened() != true)
                {
                    _scopeDialog = new GuiDialogTheodoliteScope(capi, OnScopeDialogClosed);
                    _scopeDialog.TryOpen();
                }
            }

            // Block highlight: outline target-C when phase 1 is active
            int phase = stack.Attributes.GetInt(ItemTheodolite.KeyPhase, 0);
            if (phase == 1)
            {
                int cX = stack.Attributes.GetInt(ItemTheodolite.KeyTCX);
                int cY = stack.Attributes.GetInt(ItemTheodolite.KeyTCY);
                int cZ = stack.Attributes.GetInt(ItemTheodolite.KeyTCZ);

                capi.World.HighlightBlocks(
                    player,
                    TheoHighlightGroupId,
                    new List<BlockPos> { new BlockPos(cX, cY, cZ) },
                    EnumHighlightBlocksMode.Absolute,
                    EnumHighlightShape.Arbitrary);
                return;
            }
        }
        else
        {
            // Player switched away from the theodolite — close scope and reset state.
            _scopeSuppressed = false;
            RestorePickingRange(capi);
            if (_scopeDialog?.IsOpened() == true)
                _scopeDialog.TryClose();
        }

        // No active target — clear any lingering highlight
        capi.World.HighlightBlocks(player, TheoHighlightGroupId, new List<BlockPos>());
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RealisticSurveying;

public sealed class RealisticSurveyingModSystem : ModSystem
{
    private const int    TheoHighlightGroupId = 1338;
    private const string LabelChannelName     = "realisticsurveying-labels";

    private ICoreClientAPI?           _capi;
    private ICoreServerAPI?           _sapi;
    private IClientNetworkChannel?    _clientLabelChannel;
    private GuiDialogTheodoliteScope? _scopeDialog;
    private GuiDialogRopeHud?         _ropeHud;

    /// <summary>
    /// Server-side registry: LinkId, serialized map data snapshot.
    /// Persistent with the world save.
    /// </summary>
    private Dictionary<string, byte[]> _linkRegistry = new();

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
        _sapi = sapi;

        sapi.Network
            .RegisterChannel(LabelChannelName)
            .RegisterMessageType<NodeLabelPacket>()
            .SetMessageHandler<NodeLabelPacket>(OnNodeLabelPacket)
            .RegisterMessageType<MapNamePacket>()
            .SetMessageHandler<MapNamePacket>(OnMapNamePacket)
            .RegisterMessageType<DeleteNodePacket>()
            .SetMessageHandler<DeleteNodePacket>(OnDeleteNodePacket)
            .RegisterMessageType<MapStrokePacket>()
            .SetMessageHandler<MapStrokePacket>(OnMapStrokePacket)
            .RegisterMessageType<StrokeUndoPacket>()
            .SetMessageHandler<StrokeUndoPacket>(OnStrokeUndoPacket)
            .RegisterMessageType<ViewStatePacket>()
            .SetMessageHandler<ViewStatePacket>(OnViewStatePacket);

        sapi.Event.SaveGameLoaded += () => LoadLinkRegistry(sapi);
        sapi.Event.GameWorldSave  += () => SaveLinkRegistry(sapi);
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
            .RegisterMessageType<MapStrokePacket>()
            .RegisterMessageType<StrokeUndoPacket>()
            .RegisterMessageType<ViewStatePacket>();

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

    private static void OnViewStatePacket(IServerPlayer fromPlayer, ViewStatePacket packet)
    {
        ItemSlot? mapSlot = FindMapSlot(fromPlayer);
        if (mapSlot?.Itemstack?.Item is not ItemTopographicMap) return;

        ITreeAttribute attr = mapSlot.Itemstack.Attributes;
        attr.SetDouble("rsViewZoom", packet.Zoom);
        attr.SetDouble("rsViewPanX", packet.PanX);
        attr.SetDouble("rsViewPanZ", packet.PanZ);
        attr.SetBool("rsShowFaces",  packet.ShowFaces);
        attr.SetBool("rsShowEdges",  packet.ShowEdges);
        attr.SetBool("rsShowNodes",  packet.ShowNodes);
        attr.SetBool("rsShowLabels", packet.ShowLabels);
        attr.SetBool("rsShowCoords", packet.ShowCoords);
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

    // ── Link registry: persistence ─────────────────────────────────────────

    private const string RegistrySaveKey = "rs-linkregistry";

    private void SaveLinkRegistry(ICoreServerAPI sapi)
    {
        if (_linkRegistry.Count == 0) return;
        try
        {
            using MemoryStream ms = new MemoryStream();
            using BinaryWriter w  = new BinaryWriter(ms);
            w.Write(_linkRegistry.Count);
            foreach (KeyValuePair<string, byte[]> kv in _linkRegistry)
            {
                w.Write(kv.Key);
                w.Write(kv.Value.Length);
                w.Write(kv.Value);
            }
            sapi.WorldManager.SaveGame.StoreData(RegistrySaveKey, ms.ToArray());
        }
        catch (Exception e)
        {
            sapi.World.Logger.Warning("[RealisticSurveying] Failed to save link registry: " + e.Message);
        }
    }

    private void LoadLinkRegistry(ICoreServerAPI sapi)
    {
        _linkRegistry.Clear();
        try
        {
            byte[]? raw = sapi.WorldManager.SaveGame.GetData(RegistrySaveKey);
            if (raw == null || raw.Length == 0) return;

            using MemoryStream ms = new MemoryStream(raw);
            using BinaryReader r  = new BinaryReader(ms);
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key     = r.ReadString();
                int    dataLen = r.ReadInt32();
                byte[] data    = r.ReadBytes(dataLen);
                _linkRegistry[key] = data;
            }
        }
        catch (Exception e)
        {
            sapi.World.Logger.Warning("[RealisticSurveying] Failed to load link registry: " + e.Message);
        }
    }

    // ── Link registry: update + apply ─────────────────────────────────────

    /// <summary>
    /// Records the current map data of <paramref name="sourceStack"/> in the registry so that
    /// offline players can receive the update when they next join.
    /// Called by <see cref="ItemTopographicMap.PropagateToLinkedMaps"/> after each successful survey.
    /// </summary>
    internal void UpdateLinkRegistry(string linkId, ItemStack sourceStack)
    {
        _linkRegistry[linkId] = SerializeMapData(sourceStack);
    }

    /// <summary>
    /// Called by <see cref="ItemTopographicMap.OnHeldInteractStart"/> on the server side whenever
    /// a player opens a linked map copy.  Pulls mapdata from the server side registry to sync the maps.
    /// </summary>
    public void TryApplyRegistryUpdate(ItemSlot mapSlot)
    {
        if (mapSlot?.Itemstack == null) return;

        string linkId  = mapSlot.Itemstack.Attributes.GetString("LinkId", "");
        bool   isSource = mapSlot.Itemstack.Attributes.GetBool("IsLinkSource", false);

        if (string.IsNullOrEmpty(linkId) || isSource) return;
        if (!_linkRegistry.TryGetValue(linkId, out byte[]? snapshot)) return;

        try
        {
            ApplyMapData(mapSlot.Itemstack, snapshot);
            mapSlot.MarkDirty();
        }
        catch (Exception e)
        {
            _sapi?.Logger.Warning($"[RealisticSurveying] Failed to apply link registry for '{linkId}': {e.Message}");
        }
    }

    // ── Map Data Serialization ─────────────────────────────────────────────

    /// <summary>
    /// Serialises the map data of <paramref name="stack"/> to a compact binary blob.
    /// Faces are intentionally omitted: they are fully derived from edges by
    /// <see cref="ItemTopographicMap.AddEdgeAndDetectFace"/> and will be re-detected on merge.
    /// </summary>
    private static byte[] SerializeMapData(ItemStack stack)
    {
        float[] nodes = (stack.Attributes["Nodes"] as FloatArrayAttribute)?.value ?? Array.Empty<float>();
        int[]   edges = (stack.Attributes["Edges"] as IntArrayAttribute)?.value  ?? Array.Empty<int>();
        int nodeCount = nodes.Length / 3;

        using MemoryStream ms = new MemoryStream();
        using BinaryWriter w  = new BinaryWriter(ms);

        w.Write(nodes.Length);
        foreach (float f in nodes) w.Write(f);

        w.Write(edges.Length);
        foreach (int v in edges) w.Write(v);

        // Labels: one string per node (empty string = no label)
        w.Write(nodeCount);
        for (int i = 0; i < nodeCount; i++)
            w.Write(stack.Attributes.GetString($"NodeLabel_{i}", ""));

        // Source map name
        w.Write(stack.Attributes.GetString("MapName", ""));

        return ms.ToArray();
    }

    /// <summary>
    /// Merges source map data from <paramref name="data"/> into <paramref name="stack"/>.
    /// Nodes and edges present in the source but absent from the copy are added;
    /// everything already on the copy is left untouched.
    /// Labels are only written when the copy node has no label of its own.
    /// </summary>
    private static void ApplyMapData(ItemStack stack, byte[] data)
    {
        using MemoryStream ms = new MemoryStream(data);
        using BinaryReader r  = new BinaryReader(ms);

        // Deserialise source snapshot
        int nodeLen = r.ReadInt32();
        float[] srcNodes = new float[nodeLen];
        for (int i = 0; i < nodeLen; i++) srcNodes[i] = r.ReadSingle();
        int srcNodeCount = nodeLen / 3;

        int edgeLen = r.ReadInt32();
        int[] srcEdges = new int[edgeLen];
        for (int i = 0; i < edgeLen; i++) srcEdges[i] = r.ReadInt32();

        int labelCount = r.ReadInt32();
        string[] srcLabels = new string[labelCount];
        for (int i = 0; i < labelCount; i++) srcLabels[i] = r.ReadString();

        // Source map name
        try
        {
            string sourceName = r.ReadString();
            if (!string.IsNullOrEmpty(sourceName))
                stack.Attributes.SetString("LinkSourceName", sourceName);
            else
                stack.Attributes.RemoveAttribute("LinkSourceName");
        }
        catch (EndOfStreamException) { }

        // Merge nodes
        // Map each source node index to the corresponding index on the copy,
        // adding the node if it isn't already there.
        ItemTopographicMap map = (ItemTopographicMap)stack.Item;
        int[] indexMap = new int[srcNodeCount];
        for (int i = 0; i < srcNodeCount; i++)
        {
            Vec3f pos = new Vec3f(srcNodes[i * 3], srcNodes[i * 3 + 1], srcNodes[i * 3 + 2]);
            indexMap[i] = map.FindOrAddNode(stack, pos);
        }

        // Merge labels
        // Only set a label when the copy node has none of its own; a label on a copy map takes precedence.
        for (int i = 0; i < labelCount; i++)
        {
            if (string.IsNullOrEmpty(srcLabels[i])) continue;
            int copyIdx = indexMap[i];
            if (string.IsNullOrEmpty(map.GetNodeLabel(stack, copyIdx)))
                map.SetNodeLabel(stack, copyIdx, srcLabels[i]);
        }

        // Merge edges 
        for (int i = 0; i < edgeLen; i += 2)
            map.AddEdgeAndDetectFace(stack, indexMap[srcEdges[i]], indexMap[srcEdges[i + 1]]);
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
    /// Boosts the client's picking range to the last approved render distance so the game's built-in block-selection raycast can reach 
    /// distant survey markers while the theodolite is in an active measurement state.  
    /// The extended block selection is sent to the server and used in <see cref="ItemTheodolite.OnHeldInteractStart"/> without server-side distance validation 
    /// (item interactions are not distance-checked by VS, only block break/place is).
    /// </summary>
    private void ExtendPickingRange(ICoreClientAPI capi)
    {
        if (_rangeExtended) return;
        _origPickingRange = capi.World.Player.WorldData.PickingRange;
        int desired  = capi.World.Player.WorldData.DesiredViewDistance;
        int approved = capi.World.Player.WorldData.LastApprovedViewDistance;

        // LastApprovedViewDistance is only written when the server overrides the client's
        // requested value, so 0 means no restriction.
        float effectiveRange;
        if (approved > 0)
            effectiveRange = Math.Min(approved, desired);
        else
            effectiveRange = desired;

        capi.World.Player.WorldData.PickingRange = effectiveRange;
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

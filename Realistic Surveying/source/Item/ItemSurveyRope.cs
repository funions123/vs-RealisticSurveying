using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace RealisticSurveying.GameContent;

public class ItemSurveyRope : Item
{
    // Entity-attribute keys for the two-state machine (WatchedAttributes so the client can read them for the distance HUD)
    internal const string KeyState     = "ropeState";
    internal const string KeyMarkerAX  = "ropeMarkerAX";
    internal const string KeyMarkerAY  = "ropeMarkerAY";
    internal const string KeyMarkerAZ  = "ropeMarkerAZ";

    internal const string KeyClothId   = "ropeClothId";

    private const double MaxDistanceBlocks = 100.0;
    private const string ActiveMeshCacheKey = "surveyrope-active-mesh";

    private ClothManager? _clothManager;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        _clothManager = api.ModLoader.GetModSystem<ClothManager>();
    }

    public override void OnBeforeRender(ICoreClientAPI capi, ItemStack itemstack, EnumItemRenderTarget target, ref ItemRenderInfo renderinfo)
    {
        base.OnBeforeRender(capi, itemstack, target, ref renderinfo);

        int state = capi.World.Player?.Entity?.WatchedAttributes.GetInt(KeyState, 0) ?? 0;
        if (state != 1) return;

        MultiTextureMeshRef fallback = renderinfo.ModelRef;
        renderinfo.ModelRef = ObjectCacheUtil.GetOrCreate<MultiTextureMeshRef>(capi, ActiveMeshCacheKey, () =>
        {
            var shape = Vintagestory.API.Common.Shape.TryGet(capi, new AssetLocation("game:shapes/item/ropesection.json"));
            if (shape == null) return fallback;
            capi.Tesselator.TesselateShape(this, shape, out MeshData mesh);
            return capi.Render.UploadMultiTextureMesh(mesh);
        });
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;

        handling = EnumHandHandling.PreventDefault;

        // Not targeting a survey marker — only act if the player is sneaking to cancel
        if (blockSel == null || byEntity.World.BlockAccessor.GetBlock(blockSel.Position) is not BlockSurveyMarker)
        {
            if (byEntity.Controls.Sneak && api.Side == EnumAppSide.Server && byEntity.WatchedAttributes.GetInt(KeyState, 0) != 0)
            {
                ClearState(byEntity);
                (byPlayer as IServerPlayer)?.SendMessage(
                    GlobalConstants.InfoLogChatGroup,
                    "Rope measurement cancelled.",
                    EnumChatType.Notification);
            }
            return;
        }

        if (api.Side != EnumAppSide.Server) return;

        int state = byEntity.WatchedAttributes.GetInt(KeyState, 0);
        BlockPos targetPos = blockSel.Position;

        if (state == 0)
        {
            // State 0 -> 1: select marker A
            byEntity.WatchedAttributes.SetInt(KeyMarkerAX, targetPos.X);
            byEntity.WatchedAttributes.SetInt(KeyMarkerAY, targetPos.Y);
            byEntity.WatchedAttributes.SetInt(KeyMarkerAZ, targetPos.Z);
            byEntity.WatchedAttributes.SetInt(KeyState, 1);

            CreateClothRope(byEntity, targetPos);

            (byPlayer as IServerPlayer)?.SendMessage(
                GlobalConstants.InfoLogChatGroup,
                "Rope attached to Marker A. Right-click a second marker to measure.",
                EnumChatType.Notification);
        }
        else
        {
            // State 1: measure to marker B and record
            BlockPos markerA = new BlockPos(
                byEntity.WatchedAttributes.GetInt(KeyMarkerAX),
                byEntity.WatchedAttributes.GetInt(KeyMarkerAY),
                byEntity.WatchedAttributes.GetInt(KeyMarkerAZ));

            if (targetPos.Equals(markerA))
            {
                (byPlayer as IServerPlayer)?.SendMessage(
                    GlobalConstants.InfoLogChatGroup,
                    "Marker B must be different from Marker A.",
                    EnumChatType.Notification);
                return;
            }

            float dist = markerA.DistanceTo(targetPos);
            if (dist > MaxDistanceBlocks)
            {
                (byPlayer as IServerPlayer)?.SendMessage(
                    GlobalConstants.InfoLogChatGroup,
                    $"Markers are {dist:F1} blocks apart — maximum is {MaxDistanceBlocks}.",
                    EnumChatType.Notification);
                return;
            }

            ItemSlot? mapSlot = FindMapSlot(byPlayer);
            if (mapSlot == null)
            {
                ClearState(byEntity);
                (byPlayer as IServerPlayer)?.SendMessage(
                    GlobalConstants.InfoLogChatGroup,
                    "No initialized survey map found in hotbar.",
                    EnumChatType.Notification);
                return;
            }

            ItemTopographicMap map = (ItemTopographicMap)mapSlot.Itemstack.Item;
            ItemStack mapStack = mapSlot.Itemstack;
            BlockPos origin = map.GetOrigin(mapStack);

            Vec3f offsetA = BlockToOffset(markerA, origin);
            Vec3f offsetB = BlockToOffset(targetPos, origin);

            if (!map.NodeExists(mapStack, offsetA) && !map.NodeExists(mapStack, offsetB))
            {
                ClearState(byEntity);
                (byPlayer as IServerPlayer)?.SendMessage(
                    GlobalConstants.InfoLogChatGroup,
                    "Neither marker is on the map yet. At least one endpoint must already be mapped — start from the center marker.",
                    EnumChatType.Notification);
                return;
            }

            int indexA = map.FindOrAddNode(mapStack, offsetA);
            int indexB = map.FindOrAddNode(mapStack, offsetB);
            map.AddEdgeAndDetectFace(mapStack, indexA, indexB);
            mapSlot.MarkDirty();

            ClearState(byEntity);

            (byPlayer as IServerPlayer)?.SendMessage(
                GlobalConstants.InfoLogChatGroup,
                $"Markers and distance charted.",
                EnumChatType.Notification);
        }
    }

    // Helpers

    private static Vec3f BlockToOffset(BlockPos pos, BlockPos origin) =>
        new Vec3f(pos.X - origin.X, pos.Y - origin.Y, pos.Z - origin.Z);

    private void ClearState(EntityAgent entity)
    {
        DestroyClothRope(entity);
        entity.WatchedAttributes.SetInt(KeyState, 0);
        entity.WatchedAttributes.RemoveAttribute(KeyMarkerAX);
        entity.WatchedAttributes.RemoveAttribute(KeyMarkerAY);
        entity.WatchedAttributes.RemoveAttribute(KeyMarkerAZ);
    }

    private void CreateClothRope(EntityAgent entity, BlockPos markerPos)
    {
        if (_clothManager == null) return;

        Vec3d playerPos = entity.Pos.XYZ;
        Vec3d targetPos = new Vec3d(markerPos.X + 0.5, markerPos.Y + 0.5, markerPos.Z + 0.5);

        ClothSystem sys = ClothSystem.CreateRope(api, _clothManager, playerPos, targetPos, null);

        // Pin first end to the player's hand
        Vec3d lpos = new Vec3d(0, entity.LocalEyePos.Y - 0.3, 0);
        Vec3d aheadPos = lpos.AheadCopy(0.1, entity.SidedPos.Pitch, entity.SidedPos.Yaw)
            .AheadCopy(0.4, entity.SidedPos.Pitch, entity.SidedPos.Yaw - GameMath.PIHALF);
        sys.FirstPoint.PinTo(entity, aheadPos.ToVec3f());

        // Pin last end to the marker block center
        sys.LastPoint.PinTo(markerPos, new Vec3f(0.5f, 0.5f, 0.5f));

        _clothManager.RegisterCloth(sys);
        entity.WatchedAttributes.SetInt(KeyClothId, sys.ClothId);
    }

    private void DestroyClothRope(EntityAgent entity)
    {
        if (_clothManager == null) return;

        int clothId = entity.WatchedAttributes.GetInt(KeyClothId, 0);
        if (clothId == 0) return;

        ClothSystem? sys = _clothManager.GetClothSystem(clothId);
        if (sys != null)
            _clothManager.UnregisterCloth(clothId);

        entity.WatchedAttributes.RemoveAttribute(KeyClothId);
    }

    /// <summary>
    /// Returns the slot holding the player's selected topographic map.
    /// Prefers the slot recorded in WatchedAttributes, then falls back to offhand -> hotbar scan.
    /// </summary>
    private static ItemSlot? FindMapSlot(IPlayer? player)
    {
        if (player == null) return null;

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
}

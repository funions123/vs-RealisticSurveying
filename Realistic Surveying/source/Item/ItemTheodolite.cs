using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RealisticSurveying.GameContent;

/// <summary>
/// Two-station marker triangulation instrument.
/// </summary>
public class ItemTheodolite : Item
{
    // Entity attribute keys (temporary; cleared after each station completes)
    // KeyState uses WatchedAttributes so the client can read it for scope auto-open/close.
    internal const string KeyState   = "theoState";   // 0 = idle, 1 = station planted, 2 = first sight done
    private const string KeyStnX    = "theoStnX";   // planted station block X (int)
    private const string KeyStnY    = "theoStnY";   // planted station block Y (int)
    private const string KeyStnZ    = "theoStnZ";   // planted station block Z (int)
    private const string KeySight1X = "theoSight1X";
    private const string KeySight1Y = "theoSight1Y";
    private const string KeySight1Z = "theoSight1Z";

    // Item attribute keys (persistent across sessions)
    internal const string KeyPhase = "theoPhase"; // 0 = station A incomplete, 1 = station A done
    private  const string KeyAngleA = "theoAngleA";
    private  const string KeySAX    = "theoSAX";  // Marker A block X
    private  const string KeySAY    = "theoSAY";  // Marker A block Y
    private  const string KeySAZ    = "theoSAZ";  // Marker A block Z
    private  const string KeyPBX    = "theoPBX";  // Marker B block X
    private  const string KeyPBY    = "theoPBY";  // Marker B block Y
    private  const string KeyPBZ    = "theoPBZ";  // Marker B block Z
    internal const string KeyTCX    = "theoTCX";  // target C block X (also read client-side for highlight)
    internal const string KeyTCY    = "theoTCY";
    internal const string KeyTCZ    = "theoTCZ";

    public override void OnHeldInteractStart(
        ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel,
        EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        int state = byEntity.WatchedAttributes.GetInt(KeyState, 0);

        // Sneak+right-click in air = full reset
        if (byEntity.Controls.Sneak && blockSel == null)
        {
            if (api.Side == EnumAppSide.Server)
            {
                if (state != 0 || slot.Itemstack.Attributes.GetInt(KeyPhase, 0) != 0)
                {
                    ClearEntityState(byEntity);
                    ClearItemState(slot);
                    SendMsg(byPlayer, "Theodolite reset.");
                }
            }
            return;
        }

        if (blockSel == null) return;

        // In idle state, only consume the interaction when the player is sneaking
        // (otherwise let other items/blocks handle plain right-clicks normally)
        if (state == 0 && !byEntity.Controls.Sneak) return;

        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server) return;

        int      phase     = slot.Itemstack.Attributes.GetInt(KeyPhase, 0);
        BlockPos targetPos = blockSel.Position;
        bool     isMarker  = byEntity.World.BlockAccessor.GetBlock(targetPos) is BlockSurveyMarker;

        switch (state)
        {
            case 0: HandlePlantStation(byEntity, slot, targetPos, isMarker, phase, byPlayer); break;
            case 1: HandleFirstSight  (byEntity, slot, targetPos,           phase, byPlayer); break;
            case 2: HandleSecondSight (byEntity, slot, targetPos, isMarker, phase, byPlayer); break;
        }
    }

    // State handlers

    /// <summary>
    /// State 0 -> 1: sneak+right-click a survey marker to plant the theodolite.
    /// In phase 1 the marker must be the Marker B that was recorded at station A.
    /// </summary>
    private void HandlePlantStation(
        EntityAgent byEntity, ItemSlot slot, BlockPos targetPos,
        bool isMarker, int phase, IPlayer? byPlayer)
    {
        if (!isMarker)
        {
            SendMsg(byPlayer, "Sneak+right-click a survey marker to plant the theodolite.");
            return;
        }

        if (phase == 1)
        {
            int pbX = slot.Itemstack.Attributes.GetInt(KeyPBX);
            int pbZ = slot.Itemstack.Attributes.GetInt(KeyPBZ);
            if (targetPos.X != pbX || targetPos.Z != pbZ)
            {
                SendMsg(byPlayer,
                    $"Walk to Marker B and sneak+right-click it to set Station B.");
                return;
            }
        }

        byEntity.Attributes.SetInt(KeyStnX, targetPos.X);
        byEntity.Attributes.SetInt(KeyStnY, targetPos.Y);
        byEntity.Attributes.SetInt(KeyStnZ, targetPos.Z);
        byEntity.WatchedAttributes.SetInt(KeyState, 1);

        SendMsg(byPlayer, phase == 0
            ? "Station A planted. Right-click the distant target (Marker C)."
            : "Station B planted. Right-click the same distant target (Marker C) again.");
    }

    /// <summary>
    /// State 1 -> 2: right-click any block as the first sight (Marker C).
    /// Sneak+right-click cancels the current station without resetting cross-station data.
    /// In phase 1 the sighted block must match the Marker C recorded at station A.
    /// </summary>
    private void HandleFirstSight(
        EntityAgent byEntity, ItemSlot slot, BlockPos targetPos,
        int phase, IPlayer? byPlayer)
    {
        if (byEntity.Controls.Sneak)
        {
            ClearEntityState(byEntity);
            SendMsg(byPlayer, phase == 0
                ? "Station A cancelled. Sneak+right-click Marker A again to restart."
                : "Station B cancelled. Sneak+right-click Marker B again to restart.");
            return;
        }

        if (phase == 1)
        {
            int cX = slot.Itemstack.Attributes.GetInt(KeyTCX);
            int cZ = slot.Itemstack.Attributes.GetInt(KeyTCZ);
            if (targetPos.X != cX || targetPos.Z != cZ)
            {
                SendMsg(byPlayer,
                    $"Sight the same target as at Station A");
                return;
            }
        }

        byEntity.Attributes.SetInt(KeySight1X, targetPos.X);
        byEntity.Attributes.SetInt(KeySight1Y, targetPos.Y);
        byEntity.Attributes.SetInt(KeySight1Z, targetPos.Z);
        byEntity.WatchedAttributes.SetInt(KeyState, 2);

        SendMsg(byPlayer, phase == 0
            ? "Marker C sighted. Now right-click the reference marker (Marker B)."
            : "Marker C sighted. Now right-click Marker A to complete the measurement.");
    }

    /// <summary>
    /// State 2: right-click a survey marker as the second sight; compute the interior angle.
    /// Phase 0 -> records angle A and advances to phase 1.
    /// Phase 1 -> validates it's Marker A, then triangulates.
    /// </summary>
    private void HandleSecondSight(
        EntityAgent byEntity, ItemSlot slot, BlockPos targetPos,
        bool isMarker, int phase, IPlayer? byPlayer)
    {
        if (!isMarker)
        {
            SendMsg(byPlayer, "Right-click a survey marker to complete the angle measurement.");
            return;
        }

        int stnX = byEntity.Attributes.GetInt(KeyStnX);
        int stnY = byEntity.Attributes.GetInt(KeyStnY);
        int stnZ = byEntity.Attributes.GetInt(KeyStnZ);
        int s1X  = byEntity.Attributes.GetInt(KeySight1X);
        int s1Y  = byEntity.Attributes.GetInt(KeySight1Y);
        int s1Z  = byEntity.Attributes.GetInt(KeySight1Z);

        if (targetPos.X == stnX && targetPos.Z == stnZ)
        {
            SendMsg(byPlayer, "The second sight must be a different marker from the station.");
            return;
        }
        if (targetPos.X == s1X && targetPos.Z == s1Z)
        {
            SendMsg(byPlayer, "The second sight must differ from the first sight.");
            return;
        }

        if (phase == 0)
        {
            // targetPos = Marker B
            float angleA = InteriorAngle(stnX + 0.5, stnZ + 0.5, s1X, s1Z, targetPos.X, targetPos.Z);

            var attr = slot.Itemstack.Attributes;
            attr.SetFloat(KeyAngleA, angleA);
            attr.SetInt(KeySAX, stnX);
            attr.SetInt(KeySAY, stnY);
            attr.SetInt(KeySAZ, stnZ);
            attr.SetInt(KeyTCX, s1X);
            attr.SetInt(KeyTCY, s1Y);
            attr.SetInt(KeyTCZ, s1Z);
            attr.SetInt(KeyPBX, targetPos.X);
            attr.SetInt(KeyPBY, targetPos.Y);
            attr.SetInt(KeyPBZ, targetPos.Z);
            attr.SetInt(KeyPhase, 1);
            slot.MarkDirty();

            ClearEntityState(byEntity);

            SendMsg(byPlayer,
                $"Angle A = {angleA:F1}°. Walk to Marker B for Station B.");
        }
        else
        {
            // targetPos should be Marker A
            int saX = slot.Itemstack.Attributes.GetInt(KeySAX);
            int saZ = slot.Itemstack.Attributes.GetInt(KeySAZ);
            if (targetPos.X != saX || targetPos.Z != saZ)
            {
                SendMsg(byPlayer,
                    $"Right-click Marker A to complete the measurement.");
                return;
            }

            CompleteTriangulation(byEntity, slot, stnX, stnZ, s1X, s1Z, targetPos, byPlayer);
        }
    }

    // Triangulation

    private void CompleteTriangulation(
        EntityAgent byEntity, ItemSlot slot,
        int stnX, int stnZ,   // station B block position
        int s1X,  int s1Z,    // Marker C (first sight at B)
        BlockPos markerA,     // confirmed = stored SAX/SAZ
        IPlayer? byPlayer)
    {
        float angleA = slot.Itemstack.Attributes.GetFloat(KeyAngleA);
        int   saX    = slot.Itemstack.Attributes.GetInt(KeySAX);
        int   saY    = slot.Itemstack.Attributes.GetInt(KeySAY);
        int   saZ    = slot.Itemstack.Attributes.GetInt(KeySAZ);
        int   pbX    = slot.Itemstack.Attributes.GetInt(KeyPBX);
        int   pbY    = slot.Itemstack.Attributes.GetInt(KeyPBY);
        int   pbZ    = slot.Itemstack.Attributes.GetInt(KeyPBZ);
        int   cX     = slot.Itemstack.Attributes.GetInt(KeyTCX);
        int   cY     = slot.Itemstack.Attributes.GetInt(KeyTCY);
        int   cZ     = slot.Itemstack.Attributes.GetInt(KeyTCZ);

        float angleB = InteriorAngle(stnX + 0.5, stnZ + 0.5, s1X, s1Z, markerA.X, markerA.Z);

        ItemSlot? mapSlot = FindMapSlot(byPlayer);
        if (mapSlot == null)
        {
            ClearItemState(slot);
            SendMsg(byPlayer, "No initialized topographic map found in offhand or hotbar.");
            return;
        }

        ItemTopographicMap map      = (ItemTopographicMap)mapSlot.Itemstack.Item;
        ItemStack          mapStack = mapSlot.Itemstack;
        BlockPos           origin   = map.GetOrigin(mapStack);

        if (!Triangulate(saX + 0.5, saZ + 0.5, stnX + 0.5, stnZ + 0.5,
                         cX, cZ, cY, angleA, angleB, origin,
                         out Vec3f cOffset, out string? err))
        {
            SendMsg(byPlayer, err ?? "Triangulation failed.");
            return;
        }

        Vec3f offsetA = new Vec3f(saX - origin.X, saY - origin.Y, saZ - origin.Z);
        Vec3f offsetB = new Vec3f(pbX - origin.X, pbY - origin.Y, pbZ - origin.Z);

        if (!map.NodeExists(mapStack, offsetA))
        {
            SendMsg(byPlayer,
                $"Marker A is not on the map yet. " +
                "Both reference markers must be mapped before triangulating a new point.");
            return;
        }
        if (!map.NodeExists(mapStack, offsetB))
        {
            SendMsg(byPlayer,
                $"Marker B is not on the map yet. " +
                "Both reference markers must be mapped before triangulating a new point.");
            return;
        }

        int idxA = map.FindOrAddNode(mapStack, offsetA);
        int idxB = map.FindOrAddNode(mapStack, offsetB);
        int idxC = map.FindOrAddNode(mapStack, cOffset);

        map.AddEdgeAndDetectFace(mapStack, idxA, idxC);
        map.AddEdgeAndDetectFace(mapStack, idxB, idxC);
        mapSlot.MarkDirty();

        ClearEntityState(byEntity);
        ClearItemState(slot);

        SendMsg(byPlayer,
            $"Triangulation complete. Marker C placed at offset ({cOffset.X:F1}, {cOffset.Y:F0}, {cOffset.Z:F1}) from origin marker. ");
    }

    // Geometry

    /// <summary>
    /// Angle (0–180 degrees) at the station between the directions to two sighted blocks.
    /// </summary>
    private static float InteriorAngle(
        double stationX, double stationZ,
        int refX, int refZ,
        int targetX, int targetZ)
    {
        double rx = refX    - stationX, rz = refZ    - stationZ;
        double tx = targetX - stationX, tz = targetZ - stationZ;

        double magR = Math.Sqrt(rx * rx + rz * rz);
        double magT = Math.Sqrt(tx * tx + tz * tz);
        if (magR < 0.001 || magT < 0.001) return 0f;

        double cosA = Math.Clamp((rx * tx + rz * tz) / (magR * magT), -1.0, 1.0);
        return (float)(Math.Acos(cosA) * (180.0 / Math.PI));
    }

    /// <summary>
    /// Triangulate C's position as an offset from the origin node.
    /// </summary>
    private static bool Triangulate(
        double sAX, double sAZ,
        double sBX, double sBZ,
        int    cX,  int cZ, int cY,
        float  angleA, float angleB,
        BlockPos origin,
        out Vec3f cOffset, out string? error)
    {
        cOffset = Vec3f.Zero;

        double dX       = sBX - sAX, dZ = sBZ - sAZ;
        double baseline = Math.Sqrt(dX * dX + dZ * dZ);
        
        // currently basically never comes up
        if (baseline < 1.0)
        {
            error = "Stations A and B are too close together. Move further away before the second measurement.";
            return false;
        }

        double angleC = 180.0 - angleA - angleB;
        if (angleC < 0.5)
        {
            error = $"Invalid geometry: angles A ({angleA:F1}°) + B ({angleB:F1}°) ≥ 180°. The stations and target must form a valid triangle.";
            return false;
        }

        double sinC = Math.Sin(angleC * Math.PI / 180.0);
        double b    = baseline * Math.Sin(angleB * Math.PI / 180.0) / sinC;

        double dirX   = cX - sAX, dirZ = cZ - sAZ;
        double dirLen = Math.Sqrt(dirX * dirX + dirZ * dirZ);
        if (dirLen < 0.001)
        {
            error = "Target C is at the same XZ position as Station A.";
            return false;
        }
        dirX /= dirLen; dirZ /= dirLen;

        cOffset = new Vec3f(
            (float)(sAX + b * dirX - origin.X),
            cY - origin.Y,
            (float)(sAZ + b * dirZ - origin.Z));

        error = null;
        return true;
    }

    // Helpers 

    private static void SendMsg(IPlayer? player, string msg) =>
        (player as IServerPlayer)?.SendMessage(
            GlobalConstants.InfoLogChatGroup, msg, EnumChatType.Notification);

    private static void ClearEntityState(EntityAgent entity)
    {
        entity.WatchedAttributes.SetInt(KeyState, 0);
        entity.Attributes.RemoveAttribute(KeyStnX);
        entity.Attributes.RemoveAttribute(KeyStnY);
        entity.Attributes.RemoveAttribute(KeyStnZ);
        entity.Attributes.RemoveAttribute(KeySight1X);
        entity.Attributes.RemoveAttribute(KeySight1Y);
        entity.Attributes.RemoveAttribute(KeySight1Z);
    }

    private static void ClearItemState(ItemSlot slot)
    {
        var attr = slot.Itemstack.Attributes;
        attr.SetInt(KeyPhase, 0);
        foreach (string key in new[]
            { KeyAngleA, KeySAX, KeySAY, KeySAZ, KeyPBX, KeyPBY, KeyPBZ, KeyTCX, KeyTCY, KeyTCZ })
            attr.RemoveAttribute(key);
        slot.MarkDirty();
    }

    private static ItemSlot? FindMapSlot(IPlayer? player)
    {
        if (player == null) return null;

        ItemSlot offhand = player.InventoryManager.OffhandHotbarSlot;
        if (offhand?.Itemstack?.Item is ItemTopographicMap om && om.IsInitialized(offhand.Itemstack))
            return offhand;

        IInventory hotbar = player.InventoryManager.GetHotbarInventory();
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

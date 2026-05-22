using System;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace RealisticSurveying;

/// <summary>
/// Navigation instrument that estimates the player's XZ position.  
/// Requires an unobstructed view of the sky and clear weather.
/// Coordinates are rounded to the nearest 100 blocks.
/// </summary>
public class ItemSextant : Item
{
    public override void OnHeldInteractStart(
        ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel,
        EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server) return;

        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;
        BlockPos pos = byEntity.Pos.AsBlockPos;

        // Check sky visibility — rain map height is the topmost block that blocks rain at this XZ column.
        // If something is above the player, rainMapHeight > pos.Y.
        int rainMapHeight = api.World.BlockAccessor.GetRainMapHeightAt(pos);
        if (rainMapHeight > pos.Y)
        {
            SendMsg(byPlayer, "You need an unobstructed view of the sky to take a sextant reading.");
            return;
        }

        // Check current precipitation.  ClimateCondition.Rainfall with NowValues reflects the live weather state (same value used by fire-extinguish logic).
        ClimateCondition? conds = api.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        if (conds != null && conds.Rainfall > 0.04f)
        {
            SendMsg(byPlayer, "The sextant cannot be used in rainy weather.");
            return;
        }

        // Coordinates relative to world spawn (same origin as the F3 debug display).
        double spawnX = api.World.DefaultSpawnPosition.X;
        double spawnZ = api.World.DefaultSpawnPosition.Z;

        int measuredX = (int)Math.Round((byEntity.Pos.X - spawnX) / 100.0) * 100;
        int measuredZ = (int)Math.Round((byEntity.Pos.Z - spawnZ) / 100.0) * 100;

        SendMsg(byPlayer, $"Estimated position: X = {measuredX}, Z = {measuredZ}");
    }

    private static void SendMsg(IPlayer? player, string msg) =>
        (player as IServerPlayer)?.SendMessage(
            GlobalConstants.InfoLogChatGroup, msg, EnumChatType.Notification);
}

using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace RealisticSurveying.Patches;

/// <summary>
/// Intercepts every call to <see cref="ClientMain.Set3DProjection"/> and replaces the FOV parameter while the theodolite scope is zoomed.
/// </summary>
[HarmonyPatch(typeof(ClientMain), "Set3DProjection", new[] { typeof(float), typeof(float) })]
internal static class Set3DProjectionPatch
{
    /// <summary>
    /// Desired FOV in <b>radians</b> while the scope is zoomed.
    /// <c>null</c> = no override (normal game FOV is used).
    /// Set by <see cref="GameContent.GuiDialogTheodoliteScope"/>.
    /// </summary>
    internal static float? OverrideFovRad;

    static void Prefix(ref float fov)
    {
        if (OverrideFovRad.HasValue)
            fov = OverrideFovRad.Value;
    }
}

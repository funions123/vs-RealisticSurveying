using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace RealisticSurveying;

/// <summary>
/// Scales the mouse-look delta contributed to <c>ClientMain.MouseDeltaX/Y</c> by <see cref="ScaleFactor"/> while the theodolite scope is zoomed.
/// </summary>
[HarmonyPatch(typeof(ClientMain), "OnMouseMove", new[] { typeof(MouseEvent) })]
internal static class MouseSensitivityPatch
{
    /// <summary>
    /// Mouse delta multiplier while the scope is active (0 &lt; value <= 1.0).
    /// <c>null</c> = no override; normal sensitivity is used.
    /// Set and cleared by <see cref="GameContent.GuiDialogTheodoliteScope"/>.
    /// </summary>
    internal static float? ScaleFactor;

    private static double _preX, _preY;

    static void Prefix(ClientMain __instance)
    {
        _preX = __instance.MouseDeltaX;
        _preY = __instance.MouseDeltaY;
    }

    static void Postfix(ClientMain __instance)
    {
        if (!ScaleFactor.HasValue || ScaleFactor.Value >= 1f) return;

        double s    = ScaleFactor.Value;
        double addX = __instance.MouseDeltaX - _preX;
        double addY = __instance.MouseDeltaY - _preY;

        __instance.MouseDeltaX = _preX + addX * s;
        __instance.MouseDeltaY = _preY + addY * s;
    }
}

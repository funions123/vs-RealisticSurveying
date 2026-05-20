using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace RealisticSurveying.GameContent;

/// <summary>
/// Small HUD overlay that shows the player's current distance from the roped survey marker
/// while the rope is in state 1 (Marker A selected, waiting for Marker B).
/// </summary>
public class GuiDialogRopeHud : HudElement
{
    private const float MaxDistance = 100f;

    private float _distance;

    public GuiDialogRopeHud(ICoreClientAPI capi) : base(capi)
    {
        SetupDialog();
    }

    /// <summary>Updates the displayed distance and redraws the HUD.</summary>
    public void UpdateDistance(float distance)
    {
        _distance = distance;
        SingleComposer?.GetCustomDraw("ropeHud")?.Redraw();
    }

    private void SetupDialog()
    {
        double w = 170.0 / RuntimeEnv.GUIScale;
        double h = 32.0  / RuntimeEnv.GUIScale;

        // Position at dead center of the screen + 30 px below
        double screenW = capi.Render.FrameWidth  / RuntimeEnv.GUIScale;
        double screenH = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
        double fixedX  = (screenW - w) / 2.0;
        double fixedY  = (screenH - h) / 2.0 + 30.0 / RuntimeEnv.GUIScale;

        ElementBounds dialogBounds = ElementBounds.Fixed(fixedX, fixedY, w, h);
        ElementBounds drawBounds   = ElementBounds.Fixed(0, 0, w, h);

        SingleComposer = capi.Gui
            .CreateCompo("realisticsurveying-ropehud", dialogBounds)
            .AddDynamicCustomDraw(drawBounds, DrawHud, "ropeHud")
            .Compose();
    }

    private void DrawHud(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        double w = bounds.InnerWidth;
        double h = bounds.InnerHeight;

        // Background pill
        double r = h / 2.0;
        ctx.NewPath();
        ctx.Arc(r, r, r, Math.PI / 2.0, 3.0 * Math.PI / 2.0);
        ctx.Arc(w - r, r, r, -Math.PI / 2.0, Math.PI / 2.0);
        ctx.ClosePath();
        ctx.SetSourceRGBA(0.05, 0.05, 0.05, 0.35);
        ctx.Fill();

        // Distance colour: green → yellow → red as distance approaches max
        float t = Math.Clamp(_distance / MaxDistance, 0f, 1f);
        double cr, cg;
        if (t < 0.5f)
        {
            cr = t * 2.0;
            cg = 0.85;
        }
        else
        {
            cr = 0.95;
            cg = 0.85 * (1.0 - (t - 0.5) * 2.0);
        }

        // Text
        string text = $"{_distance:F1} / {MaxDistance:F0} blocks";
        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold);
        ctx.SetFontSize(13.0);
        TextExtents te = ctx.TextExtents(text);
        ctx.MoveTo((w - te.Width) / 2.0, (h + te.Height) / 2.0);
        ctx.SetSourceRGBA(cr, cg, 0.10, 0.95);
        ctx.ShowText(text);
    }

    // Pass-through: don't consume any mouse input
    public override void OnMouseDown(MouseEvent args) { }
    public override void OnMouseUp  (MouseEvent args) { }
    public override void OnMouseMove(MouseEvent args) { }

    public override string ToggleKeyCombinationCode => null;
}

using System;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using RealisticSurveying.Patches;

namespace RealisticSurveying.GameContent;

/// <summary>
/// Full-screen scope overlay rendered as a <see cref="HudElement"/> so the camera stays active while the scope is open.
/// </summary>
public class GuiDialogTheodoliteScope : HudElement
{
    private const float MinFovDeg = 5f;
    private const float ZoomStep  = 5f;   // degrees per scroll notch

    private readonly float   _defaultFovDeg;
    private          float   _currentFovDeg;
    private readonly Action? _onClosed;

    public override string ToggleKeyCombinationCode => null!;
    public override bool   Focusable                => false;
    public override double InputOrder               => 1.1;

    public GuiDialogTheodoliteScope(ICoreClientAPI capi, Action? onClosed = null) : base(capi)
    {
        _defaultFovDeg = ClientSettings.FieldOfView;
        _currentFovDeg = _defaultFovDeg;
        _onClosed      = onClosed;
        SetupDialog();
    }

    // Dialog composition
    private void SetupDialog()
    {
        // Convert screen pixels -> virtual GUI units so the canvas covers the screen.
        double sw = capi.Render.FrameWidth  / RuntimeEnv.GUIScale;
        double sh = capi.Render.FrameHeight / RuntimeEnv.GUIScale;

        ElementBounds dialogBounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, 0, 0, sw, sh);
        ElementBounds drawBounds   = ElementBounds.Fixed(0, 0, sw, sh);

        SingleComposer = capi.Gui
            .CreateCompo("realisticsurveying-scope", dialogBounds)
            .AddDynamicCustomDraw(drawBounds, DrawScope, "scopeCanvas")
            .Compose();
    }

    // Mouse passthrough
    // GuiDialog.OnMouseDown sets args.Handled = true for any click whose pixel coordinates fall inside the composer bounds.
    // The canvas covers the full screen, so without these empty overrides EVERY mouse click is eaten and right-click world interactions stop working entirely.
    public override void OnMouseDown(MouseEvent args) { }
    public override void OnMouseUp  (MouseEvent args) { }
    public override void OnMouseMove(MouseEvent args) { }

    // Zoom control
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!IsOpened()) return;

        // Scroll up (deltaPrecise > 0) = zoom in = smaller FOV angle.
        float step = args.deltaPrecise > 0 ? -ZoomStep : ZoomStep;
        _currentFovDeg = Math.Clamp(_currentFovDeg + step, MinFovDeg, _defaultFovDeg);
        ApplyFov();
        SingleComposer?.GetCustomDraw("scopeCanvas")?.Redraw();
        args.SetHandled(true);
    }

    private void ApplyFov()
    {
        if (_currentFovDeg >= _defaultFovDeg - 0.01f)
        {
            Set3DProjectionPatch.OverrideFovRad  = null;   // restore normal FOV
            MouseSensitivityPatch.ScaleFactor    = null;   // restore normal sensitivity
            return;
        }
        Set3DProjectionPatch.OverrideFovRad = _currentFovDeg * (float)(Math.PI / 180.0);
        // Scale mouse speed inversely to zoom so angular velocity stays constant.
        MouseSensitivityPatch.ScaleFactor   = _currentFovDeg / _defaultFovDeg;
    }

    // Cleanup

    public override void OnGuiClosed()
    {
        Set3DProjectionPatch.OverrideFovRad = null;   // clear FOV override first
        MouseSensitivityPatch.ScaleFactor   = null;   // and sensitivity override
        capi.Render.Reset3DProjection();              // then force an immediate restore
        _currentFovDeg = _defaultFovDeg;
        _onClosed?.Invoke();
        base.OnGuiClosed();
    }

    // Drawing

    private void DrawScope(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        double w  = bounds.InnerWidth;
        double h  = bounds.InnerHeight;
        double cx = w / 2.0;
        double cy = h / 2.0;
        double r  = Math.Min(w, h) * 0.38;   // scope circle radius

        // Dark vignette with transparent circular cutout
        // Cairo's EvenOdd fill rule: areas covered by an odd number of sub-paths
        // are filled; even (or zero) are left clear.  The rectangle covers the
        // whole canvas (1 crossing); the circle sub-path adds a second crossing
        // inside it, making the interior even → transparent.
        ctx.Rectangle(0, 0, w, h);
        ctx.NewSubPath();
        ctx.Arc(cx, cy, r, 0, Math.PI * 2);
        ctx.FillRule = FillRule.EvenOdd;
        ctx.SetSourceRGBA(0.02, 0.02, 0.02, 0.90);
        ctx.Fill();
        ctx.FillRule = FillRule.Winding;   // restore default

        // Scope ring 
        ctx.SetSourceRGBA(0.48, 0.38, 0.18, 1.0);
        ctx.LineWidth = 5.0;
        ctx.NewPath();
        ctx.Arc(cx, cy, r, 0, Math.PI * 2);
        ctx.Stroke();

        // Crosshair 
        const double GapR = 8.0;
        double arm = r * 0.88;
        ctx.SetSourceRGBA(0.94, 0.92, 0.58, 0.88);
        ctx.LineWidth = 1.0;
        ctx.NewPath();
        ctx.MoveTo(cx - arm, cy);  ctx.LineTo(cx - GapR, cy);
        ctx.MoveTo(cx + GapR, cy); ctx.LineTo(cx + arm,  cy);
        ctx.MoveTo(cx, cy - arm);  ctx.LineTo(cx, cy - GapR);
        ctx.MoveTo(cx, cy + GapR); ctx.LineTo(cx, cy + arm);
        ctx.Stroke();

        // Center dot
        ctx.NewPath();
        ctx.Arc(cx, cy, 2.5, 0, Math.PI * 2);
        ctx.SetSourceRGBA(0.94, 0.92, 0.58, 1.0);
        ctx.Fill();

        // Zoom level
        float  mult     = _defaultFovDeg / _currentFovDeg;
        string zoomText = $"{mult:F1}\u00d7";   // e.g. "2.0×"
        ctx.SelectFontFace("monospace", FontSlant.Normal, FontWeight.Bold);
        ctx.SetFontSize(13.0);
        ctx.SetSourceRGBA(0.94, 0.92, 0.58, 0.85);
        ctx.MoveTo(cx + r * 0.40, cy + r * 0.82);
        ctx.ShowText(zoomText);

        // Hint text
        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Normal);
        ctx.SetFontSize(10.5);
        ctx.SetSourceRGBA(0.78, 0.78, 0.58, 0.60);
        const string Hint = "Esc to close  \u2022  Scroll to zoom";
        TextExtents te = ctx.TextExtents(Hint);
        ctx.MoveTo((w - te.Width) / 2.0, h - 22.0);
        ctx.ShowText(Hint);
    }
}

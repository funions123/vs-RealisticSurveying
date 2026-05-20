using Cairo;
using RealisticSurveying.Network;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace RealisticSurveying.GameContent;

/// <summary>
/// Renders the survey map as a top-down 2D projection.
/// Supports scroll-wheel zoom (centred on cursor) and left-drag pan.
/// A short left-click on a node opens the label editor.
/// The world origin marker always sits at canvas centre when zoom = 1 and pan = 0.
/// </summary>
public class GuiDialogSurveyMap : GuiDialog
{
    private readonly ItemStack              _mapStack;
    private readonly ItemTopographicMap     _mapItem;
    private readonly IClientNetworkChannel? _labelChannel;

    private bool _showFaces  = true;
    private bool _showEdges  = true;
    private bool _showNodes  = true;
    private bool _showLabels = false;
    private bool _showCoords = false;

    /// <summary>
    /// World blocks spanned by the canvas at zoom = 1.
    /// </summary>
    private const double BlocksPerMap    = 1024.0;

    /// <summary>
    /// Canvas pixel size.
    /// </summary>
    private const double CanvasPixels    = 512.0;

    private const double MinZoom         = 0.125;
    private const double MaxZoom         = 64.0;

    /// <summary>
    /// Pixels a drag must travel before it counts as a pan (not a click).
    /// </summary>
    private const double DragThreshold   = 4.0;

    /// <summary>
    /// Canvas-pixel radius within which a click is attributed to a node.
    /// </summary>
    private const double NodeClickRadius = 8.0;

    /// <summary>
    /// Current zoom multiplier (1 = full 1024-block view).
    /// </summary>
    private double _zoom = 1.0;

    // Canvas centre position in world-block coordinates.
    private double _panX = 0.0;
    private double _panZ = 0.0;

    // Drag/pan input state
    private bool   _isDragging;
    private bool   _dragThresholdExceeded;
    private double _dragStartMouseX;
    private double _dragStartMouseZ;
    private double _dragStartPanX;
    private double _dragStartPanZ;

    // Retained so mouse handlers can compute canvas-local coordinates.
    private ElementBounds _canvasBounds = null!;

    // Draw mode
    private bool   _drawMode    = false;
    private int    _colorIndex  = 3;      // Dark ink default
    private float  _strokeWidth = 1.5f;

    // Local stroke list (loaded from item stack on open + appended as user draws)
    private readonly List<(int colorIdx, float width, float[] pts)> _localStrokes = new();

    // In-progress stroke being drawn
    private List<float>? _currentPoints;
    private double       _lastDrawScreenX, _lastDrawScreenZ;

    private ElementBounds _paletteBounds = null!;

    // Preset palette colours (Cairo doubles)
    private static readonly (double r, double g, double b)[] PaletteColors =
    {
        (0.80, 0.15, 0.10),  // Red
        (0.15, 0.25, 0.80),  // Blue
        (0.10, 0.55, 0.15),  // Green
        (0.10, 0.08, 0.05),  // Dark ink
        (0.45, 0.25, 0.10),  // Brown
    };

    public override string ToggleKeyCombinationCode => null;

    public GuiDialogSurveyMap(ICoreClientAPI capi, ItemStack mapStack) : base(capi)
    {
        _mapStack     = mapStack;
        _mapItem      = (ItemTopographicMap)mapStack.Item;
        _labelChannel = capi.ModLoader
            .GetModSystem<RealisticSurveyingModSystem>()?.ClientLabelChannel;

        int sc = _mapItem.StrokeCount(_mapStack);
        for (int i = 0; i < sc; i++)
            _localStrokes.Add(_mapItem.GetStroke(_mapStack, i));

        SetupDialog();
    }

    // Dialog composition
    private void SetupDialog()
    {
        const double canvasW  = CanvasPixels;
        const double canvasH  = CanvasPixels;
        const double swSize   = 25.0;
        const double rowH     = 34.0;
        const double labelX   = swSize + 6.0;
        const double labelW   = 120.0;
        const double gapY     = 10.0;
        const double btnW          = 90.0;
        const double btnH          = 28.0;
        const double canvasOffsetY = 18.0;

        _canvasBounds = ElementBounds.Fixed(0, canvasOffsetY, canvasW, canvasH);

        double checkY = canvasOffsetY + canvasH + gapY;

        ElementBounds swFaces  = ElementBounds.Fixed(0, checkY,            swSize, swSize);
        ElementBounds swEdges  = ElementBounds.Fixed(0, checkY + rowH,     swSize, swSize);
        ElementBounds swNodes  = ElementBounds.Fixed(0, checkY + rowH * 2, swSize, swSize);
        ElementBounds swLabels = ElementBounds.Fixed(0, checkY + rowH * 3, swSize, swSize);
        ElementBounds swCoords = ElementBounds.Fixed(0, checkY + rowH * 4, swSize, swSize);

        ElementBounds lbFaces  = ElementBounds.Fixed(labelX, checkY + 2,            labelW, swSize);
        ElementBounds lbEdges  = ElementBounds.Fixed(labelX, checkY + rowH + 2,     labelW, swSize);
        ElementBounds lbNodes  = ElementBounds.Fixed(labelX, checkY + rowH * 2 + 2, labelW, swSize);
        ElementBounds lbLabels = ElementBounds.Fixed(labelX, checkY + rowH * 3 + 2, labelW, swSize);
        ElementBounds lbCoords = ElementBounds.Fixed(labelX, checkY + rowH * 4 + 2, labelW, swSize);

        // Rename button
        double btnY = checkY + rowH * 4 + (rowH - btnH) / 2.0;
        ElementBounds renameBounds = ElementBounds.Fixed(canvasW - btnW, btnY, btnW, btnH);

        // Second column for drawing controls
        const double col2X = 180.0;
        ElementBounds swDraw      = ElementBounds.Fixed(col2X,                checkY,               swSize, swSize);
        ElementBounds lbDraw      = ElementBounds.Fixed(col2X + labelX,       checkY + 2,            80,     swSize);
        _paletteBounds            = ElementBounds.Fixed(col2X,                checkY + rowH + 4,     102,    22);
        ElementBounds widthBounds = ElementBounds.Fixed(col2X,                checkY + rowH * 2 + 3, 90,     25);
        ElementBounds undoBounds  = ElementBounds.Fixed(col2X,                checkY + rowH * 3 + 3, 60,     28);

        double totalH = checkY + rowH * 5;
        ElementBounds containerBounds = ElementBounds.Fixed(0, 0, canvasW, totalH);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(containerBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        string title = _mapItem.GetMapName(_mapStack);
        if (string.IsNullOrWhiteSpace(title)) title = "Survey Map";

        SingleComposer = capi.Gui
            .CreateCompo("realisticsurveying-mapgui", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar(title, () => TryClose())
            .BeginChildElements(bgBounds)
                .AddDynamicCustomDraw(_canvasBounds, DrawMap, "mapCanvas")
                .AddSwitch(v => { _showFaces  = v; RedrawMap(); }, swFaces,  "swFaces",  swSize)
                .AddSwitch(v => { _showEdges  = v; RedrawMap(); }, swEdges,  "swEdges",  swSize)
                .AddSwitch(v => { _showNodes  = v; RedrawMap(); }, swNodes,  "swNodes",  swSize)
                .AddSwitch(v => { _showLabels = v; RedrawMap(); }, swLabels, "swLabels", swSize)
                .AddSwitch(v => { _showCoords = v; RedrawMap(); }, swCoords, "swCoords", swSize)
                .AddStaticText("Show Faces",  CairoFont.WhiteSmallText(), lbFaces)
                .AddStaticText("Show Edges",  CairoFont.WhiteSmallText(), lbEdges)
                .AddStaticText("Show Nodes",  CairoFont.WhiteSmallText(), lbNodes)
                .AddStaticText("Show Names",  CairoFont.WhiteSmallText(), lbLabels)
                .AddStaticText("Show Coords", CairoFont.WhiteSmallText(), lbCoords)
                .AddButton("Rename", OpenRenameDialog, renameBounds)
                .AddSwitch(v => { _drawMode = v; RedrawMap(); }, swDraw, "swDraw", swSize)
                .AddStaticText("Draw Mode", CairoFont.WhiteSmallText(), lbDraw)
                .AddDynamicCustomDraw(_paletteBounds, DrawPalette, "paletteCanvas")
                .AddDropDown(
                    new[] { "thin", "medium", "thick" },
                    new[] { "Thin", "Medium", "Thick" },
                    0,
                    (code, _) => {
                        _strokeWidth = code switch { "medium" => 3.0f, "thick" => 6.0f, _ => 1.5f };
                    },
                    widthBounds, CairoFont.WhiteSmallText(), "widthDrop")
                .AddButton("Undo", OnUndoClick, undoBounds)
            .EndChildElements()
            .Compose();

        SingleComposer.GetSwitch("swFaces").On  = _showFaces;
        SingleComposer.GetSwitch("swEdges").On  = _showEdges;
        SingleComposer.GetSwitch("swNodes").On  = _showNodes;
        SingleComposer.GetSwitch("swLabels").On = _showLabels;
        SingleComposer.GetSwitch("swCoords").On = _showCoords;
        SingleComposer.GetSwitch("swDraw").On   = _drawMode;
    }

    private bool OpenRenameDialog()
    {
        string current = _mapItem.GetMapName(_mapStack);
        new GuiDialogMapName(capi, current, newName =>
        {
            _mapItem.SetMapName(_mapStack, newName);
            _labelChannel?.SendPacket(new MapNamePacket { Name = newName });
            SetupDialog();   // rebuild title bar with new name
        }).TryOpen();
        return true;
    }

    private void RedrawMap() =>
        SingleComposer?.GetCustomDraw("mapCanvas")?.Redraw();

    // Mouse: zoom (scroll wheel)
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        int mouseX = capi.Input.MouseX;
        int mouseY = capi.Input.MouseY;

        if (!IsOverCanvas(mouseX, mouseY))
        {
            base.OnMouseWheel(args);
            args.SetHandled(true);
            return;
        }

        double w = _canvasBounds.InnerWidth;
        double h = _canvasBounds.InnerHeight;
        double oldScale = w / BlocksPerMap * _zoom;

        double factor = args.deltaPrecise > 0 ? 1.25 : 1.0 / 1.25;
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        double newScale = w / BlocksPerMap * _zoom;

        // Zoom centred on the cursor so the block under it stays fixed
        double mx = mouseX - _canvasBounds.absX;
        double mz = mouseY - _canvasBounds.absY;
        _panX += (mx - w / 2.0) * (1.0 / oldScale - 1.0 / newScale);
        _panZ += (mz - h / 2.0) * (1.0 / oldScale - 1.0 / newScale);

        RedrawMap();
        args.SetHandled(true);
    }

    // Mouse: pan (left-drag) and node selection (left-click)
    public override void OnMouseDown(MouseEvent args)
    {
        // Palette click in draw mode
        if (_drawMode && IsOverBounds(_paletteBounds, args.X, args.Y))
        {
            double lx   = args.X - _paletteBounds.absX;
            _colorIndex = Math.Clamp((int)(lx / 21.0), 0, PaletteColors.Length - 1);
            SingleComposer.GetCustomDraw("paletteCanvas")?.Redraw();
            args.Handled = true;
            return;
        }

        // Start drawing stroke in draw mode
        if (_drawMode && args.Button == EnumMouseButton.Left && IsOverCanvas(args.X, args.Y))
        {
            _currentPoints   = new List<float>();
            _lastDrawScreenX = args.X;
            _lastDrawScreenZ = args.Y;
            RecordDrawPoint(args.X, args.Y);
            args.Handled = true;
            return;
        }

        if (!args.Handled && args.Button == EnumMouseButton.Left && IsOverCanvas(args.X, args.Y))
        {
            _isDragging            = true;
            _dragThresholdExceeded = false;
            _dragStartMouseX       = args.X;
            _dragStartMouseZ       = args.Y;
            _dragStartPanX         = _panX;
            _dragStartPanZ         = _panZ;
            args.Handled           = true;
            return;
        }
        base.OnMouseDown(args);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (_currentPoints != null)
        {
            double ddx = args.X - _lastDrawScreenX;
            double ddz = args.Y - _lastDrawScreenZ;
            if (ddx * ddx + ddz * ddz >= 9.0)  // 3px threshold
            {
                RecordDrawPoint(args.X, args.Y);
                _lastDrawScreenX = args.X;
                _lastDrawScreenZ = args.Y;
                RedrawMap();
            }
            return;
        }

        if (_isDragging)
        {
            double dx = args.X - _dragStartMouseX;
            double dz = args.Y - _dragStartMouseZ;

            if (!_dragThresholdExceeded && dx * dx + dz * dz > DragThreshold * DragThreshold)
                _dragThresholdExceeded = true;

            if (_dragThresholdExceeded)
            {
                double scale = _canvasBounds.InnerWidth / BlocksPerMap * _zoom;
                _panX = _dragStartPanX - dx / scale;
                _panZ = _dragStartPanZ - dz / scale;
                RedrawMap();
            }
            return;
        }
        base.OnMouseMove(args);
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (_currentPoints != null && args.Button == EnumMouseButton.Left)
        {
            if (_currentPoints.Count >= 4)   // need at least 2 points
            {
                float[] pts = _currentPoints.ToArray();
                _localStrokes.Add((_colorIndex, _strokeWidth, pts));
                _labelChannel?.SendPacket(new MapStrokePacket
                    { ColorIndex = _colorIndex, Width = _strokeWidth, Points = pts });
                RedrawMap();
            }
            _currentPoints = null;
            args.Handled   = true;
            return;
        }

        if (_isDragging && !_dragThresholdExceeded && args.Button == EnumMouseButton.Left)
            TrySelectNode(args.X, args.Y);

        _isDragging = false;
        base.OnMouseUp(args);
    }

    // ── Node selection ────────────────────────────────────────────────────────

    /// <summary>
    /// Checks whether a click at mouse coords lands within NodeClickRadius pixels of any node. If so, opens the label editor for that node.
    /// </summary>
    private void TrySelectNode(double mouseX, double mouseY)
    {
        double cx = mouseX - _canvasBounds.absX;
        double cz = mouseY - _canvasBounds.absY;

        double w       = _canvasBounds.InnerWidth;
        double h       = _canvasBounds.InnerHeight;
        double scale   = w / BlocksPerMap * _zoom;
        double originX = w / 2.0 - _panX * scale;
        double originZ = h / 2.0 - _panZ * scale;

        int nodeCount = _mapItem.NodeCount(_mapStack);
        for (int i = 0; i < nodeCount; i++)
        {
            Vec3f node = _mapItem.GetNode(_mapStack, i);
            (double nx, double nz) = NodeToCanvas(node, originX, originZ, scale);

            double dx = cx - nx, dz = cz - nz;
            if (dx * dx + dz * dz <= NodeClickRadius * NodeClickRadius)
            {
                OpenLabelEditor(i);
                return;
            }
        }
    }

    private void OpenLabelEditor(int nodeIndex)
    {
        Vec3f    node         = _mapItem.GetNode(_mapStack, nodeIndex);
        BlockPos origin       = _mapItem.GetOrigin(_mapStack);
        string   currentLabel = _mapItem.GetNodeLabel(_mapStack, nodeIndex);
        int      absY         = origin.Y + (int)Math.Round(node.Y);
        string   coordsStr    = FormatCoords(node, absY);

        new GuiDialogNodeLabel(capi, nodeIndex, coordsStr, currentLabel,
            onSave: (idx, label) =>
            {
                _mapItem.SetNodeLabel(_mapStack, idx, label);
                _labelChannel?.SendPacket(new NodeLabelPacket { NodeIndex = idx, Label = label });
                RedrawMap();
            },
            onDelete: idx =>
            {
                _mapItem.DeleteNode(_mapStack, idx);
                _labelChannel?.SendPacket(new DeleteNodePacket { NodeIndex = idx });
                RedrawMap();
            }
        ).TryOpen();
    }

    private void DrawMap(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        double w = bounds.InnerWidth;
        double h = bounds.InnerHeight;

        // Pixels per block at the current zoom level
        double scale = w / BlocksPerMap * _zoom;

        // Canvas coordinates of the world origin block (node offset = 0,0,0)
        double originX = w / 2.0 - _panX * scale;
        double originZ = h / 2.0 - _panZ * scale;

        // Parchment Background
        ctx.SetSourceRGBA(0.90, 0.85, 0.70, 1.0);
        ctx.Rectangle(0, 0, w, h);
        ctx.Fill();

        DrawGrid(ctx, w, h, originX, originZ, scale);
        DrawOriginCrosshair(ctx, originX, originZ, w, h);
        DrawStrokes(ctx, originX, originZ, scale);

        int nodeCount = _mapItem.NodeCount(_mapStack);

        if (nodeCount == 0)
        {
            DrawHintText(ctx, w, h);
            DrawBorder(ctx, w, h);
            return;
        }

        // Faces
        if (_showFaces)
        {
            int[] faces = _mapItem.GetFaces(_mapStack);
            for (int i = 0; i < faces.Length; i += 3)
            {
                Vec3f a = _mapItem.GetNode(_mapStack, faces[i]);
                Vec3f b = _mapItem.GetNode(_mapStack, faces[i + 1]);
                Vec3f c = _mapItem.GetNode(_mapStack, faces[i + 2]);

                (double ax, double az)   = NodeToCanvas(a, originX, originZ, scale);
                (double bx, double bz)   = NodeToCanvas(b, originX, originZ, scale);
                (double ccx, double ccz) = NodeToCanvas(c, originX, originZ, scale);

                Color col = ElevationColor((a.Y + b.Y + c.Y) / 3f);

                ctx.NewPath();
                ctx.MoveTo(ax, az);
                ctx.LineTo(bx, bz);
                ctx.LineTo(ccx, ccz);
                ctx.ClosePath();
                ctx.SetSourceRGBA(col.R, col.G, col.B, 0.75);
                ctx.Fill();
            }
        }

        // Edges
        if (_showEdges)
        {
            ctx.SetSourceRGBA(0.25, 0.15, 0.05, 0.9);
            ctx.LineWidth = 1.5;

            int[] edges = _mapItem.GetEdges(_mapStack);
            for (int i = 0; i < edges.Length; i += 2)
            {
                Vec3f a = _mapItem.GetNode(_mapStack, edges[i]);
                Vec3f b = _mapItem.GetNode(_mapStack, edges[i + 1]);

                (double ax, double az) = NodeToCanvas(a, originX, originZ, scale);
                (double bx, double bz) = NodeToCanvas(b, originX, originZ, scale);

                ctx.NewPath();
                ctx.MoveTo(ax, az);
                ctx.LineTo(bx, bz);
                ctx.Stroke();
            }
        }

        // Nodes
        if (_showNodes)
        {
            for (int i = 0; i < nodeCount; i++)
            {
                Vec3f node = _mapItem.GetNode(_mapStack, i);
                (double px, double pz) = NodeToCanvas(node, originX, originZ, scale);

                ctx.NewPath();
                ctx.Arc(px, pz, 3.5, 0, Math.PI * 2);
                ctx.SetSourceRGBA(0.75, 0.10, 0.10, 1.0);
                ctx.Fill();

                ctx.NewPath();
                ctx.Arc(px, pz, 3.5, 0, Math.PI * 2);
                ctx.SetSourceRGBA(1.0, 1.0, 1.0, 0.6);
                ctx.LineWidth = 1.0;
                ctx.Stroke();
            }
        }

        // Labels (names and/or coordinates)
        if (_showLabels || _showCoords)
        {
            BlockPos origin = _mapItem.GetOrigin(_mapStack);

            for (int i = 0; i < nodeCount; i++)
            {
                Vec3f node = _mapItem.GetNode(_mapStack, i);
                (double px, double pz) = NodeToCanvas(node, originX, originZ, scale);

                double textX = px + 6;
                double textY = pz - 3;

                if (_showLabels)
                {
                    string customLabel = _mapItem.GetNodeLabel(_mapStack, i);
                    if (!string.IsNullOrEmpty(customLabel))
                    {
                        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Bold);
                        ctx.SetFontSize(9.5);
                        ctx.SetSourceRGBA(0.10, 0.05, 0.00, 0.95);
                        ctx.MoveTo(textX, textY);
                        ctx.ShowText(customLabel);
                        textY += 11.0;
                    }
                }

                if (_showCoords)
                {
                    int    absY      = origin.Y + (int)Math.Round(node.Y);
                    string coordLine = FormatCoords(node, absY);

                    ctx.SelectFontFace("monospace", FontSlant.Normal, FontWeight.Normal);
                    ctx.SetFontSize(8.5);
                    ctx.SetSourceRGBA(0.30, 0.20, 0.10, 0.80);
                    ctx.MoveTo(textX, textY);
                    ctx.ShowText(coordLine);
                }
            }
        }

        DrawBorder(ctx, w, h);
    }

    // Draw mode helpers

    private void DrawPalette(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        const int sw = 18, gap = 3;
        for (int i = 0; i < PaletteColors.Length; i++)
        {
            double x = i * (sw + gap);
            var (r, g, b) = PaletteColors[i];
            ctx.Rectangle(x, 0, sw, sw);
            ctx.SetSourceRGBA(r, g, b, 1.0);
            ctx.Fill();

            if (i == _colorIndex)
            {
                ctx.Rectangle(x + 0.5, 0.5, sw - 1, sw - 1);
                ctx.SetSourceRGBA(1, 1, 1, 0.9);
                ctx.LineWidth = 2.0;
                ctx.Stroke();
            }
        }
    }

    private bool OnUndoClick()
    {
        if (_localStrokes.Count > 0)
        {
            _localStrokes.RemoveAt(_localStrokes.Count - 1);
            _labelChannel?.SendPacket(new StrokeUndoPacket());
            RedrawMap();
        }
        return true;
    }

    private void RecordDrawPoint(double mouseX, double mouseY)
    {
        double w      = _canvasBounds.InnerWidth;
        double h      = _canvasBounds.InnerHeight;
        double scale  = w / BlocksPerMap * _zoom;
        double originX = w / 2.0 - _panX * scale;
        double originZ = h / 2.0 - _panZ * scale;
        double cx = mouseX - _canvasBounds.absX;
        double cz = mouseY - _canvasBounds.absY;
        _currentPoints!.Add((float)((cx - originX) / scale));
        _currentPoints!.Add((float)((cz - originZ) / scale));
    }

    private static bool IsOverBounds(ElementBounds b, double mx, double my) =>
        mx >= b.absX && mx <= b.absX + b.OuterWidth &&
        my >= b.absY && my <= b.absY + b.OuterHeight;

    private void DrawStrokes(Context ctx, double originX, double originZ, double scale)
    {
        foreach (var (colorIdx, width, pts) in _localStrokes)
            DrawSingleStroke(ctx, pts, originX, originZ, scale, colorIdx, width);

        if (_currentPoints is { Count: >= 4 })
            DrawSingleStroke(ctx, _currentPoints.ToArray(), originX, originZ, scale,
                             _colorIndex, _strokeWidth);
    }

    private static void DrawSingleStroke(Context ctx, float[] pts,
        double ox, double oz, double scale, int colorIdx, float width)
    {
        if (pts.Length < 4) return;
        var (r, g, b) = PaletteColors[Math.Clamp(colorIdx, 0, PaletteColors.Length - 1)];
        ctx.SetSourceRGBA(r, g, b, 0.85);
        ctx.LineWidth = width;
        ctx.LineCap   = LineCap.Round;
        ctx.LineJoin  = LineJoin.Round;
        ctx.NewPath();
        ctx.MoveTo(ox + pts[0] * scale, oz + pts[1] * scale);
        for (int i = 2; i < pts.Length; i += 2)
            ctx.LineTo(ox + pts[i] * scale, oz + pts[i + 1] * scale);
        ctx.Stroke();
    }

    /// <summary>
    /// Draws a world-aligned grid. Step size adapts so lines are always 40–200 px apart.
    /// </summary>
    private static void DrawGrid(Context ctx, double w, double h,
                                  double originX, double originZ, double scale)
    {
        double stepBlocks = PickGridStep(scale);
        double stepPx     = stepBlocks * scale;

        ctx.SetSourceRGBA(0.68, 0.62, 0.52, 0.40);
        ctx.LineWidth = 0.5;

        // Vertical lines: index of first visible grid line (may be negative)
        double firstN = Math.Ceiling(-originX / stepPx);
        for (double n = firstN; ; n++)
        {
            double cx = originX + n * stepPx;
            if (cx > w) break;
            ctx.NewPath(); ctx.MoveTo(cx, 0); ctx.LineTo(cx, h); ctx.Stroke();
        }

        // Horizontal lines
        firstN = Math.Ceiling(-originZ / stepPx);
        for (double n = firstN; ; n++)
        {
            double cz = originZ + n * stepPx;
            if (cz > h) break;
            ctx.NewPath(); ctx.MoveTo(0, cz); ctx.LineTo(w, cz); ctx.Stroke();
        }
    }

    /// <summary>
    /// Small crosshair drawn at the world origin, hidden when scrolled off-canvas.
    /// </summary>
    private static void DrawOriginCrosshair(Context ctx, double originX, double originZ,
                                             double w, double h)
    {
        if (originX < -20 || originX > w + 20 || originZ < -20 || originZ > h + 20) return;

        ctx.SetSourceRGBA(0.40, 0.30, 0.20, 0.70);
        ctx.LineWidth = 1.0;

        ctx.NewPath(); ctx.MoveTo(originX - 9, originZ); ctx.LineTo(originX + 9, originZ); ctx.Stroke();
        ctx.NewPath(); ctx.MoveTo(originX, originZ - 9); ctx.LineTo(originX, originZ + 9); ctx.Stroke();
    }

    private static void DrawBorder(Context ctx, double w, double h)
    {
        ctx.SetSourceRGBA(0.35, 0.25, 0.15, 1.0);
        ctx.LineWidth = 2.0;
        ctx.Rectangle(0, 0, w, h);
        ctx.Stroke();
    }

    private static void DrawHintText(Context ctx, double w, double h)
    {
        ctx.SetSourceRGBA(0.40, 0.30, 0.20, 0.55);
        ctx.SelectFontFace("sans-serif", FontSlant.Normal, FontWeight.Normal);
        ctx.SetFontSize(13.0);
        const string msg = "No survey data recorded.";
        TextExtents ext = ctx.TextExtents(msg);
        ctx.MoveTo((w - ext.Width) / 2.0, h / 2.0);
        ctx.ShowText(msg);
    }

    // Math & input helpers

    private bool IsOverCanvas(int mouseX, int mouseY) =>
        mouseX >= _canvasBounds.absX
        && mouseX <= _canvasBounds.absX + _canvasBounds.OuterWidth
        && mouseY >= _canvasBounds.absY
        && mouseY <= _canvasBounds.absY + _canvasBounds.OuterHeight;

    /// <summary>
    /// Converts a node's block offset from world origin to canvas pixel coords.
    /// </summary>
    private static (double px, double pz) NodeToCanvas(Vec3f node, double originX, double originZ, double scale) =>
        (originX + node.X * scale, originZ + node.Z * scale);

    /// <summary>
    /// Picks the smallest round block step so that adjacent grid lines are ≥ 40 px apart.
    /// </summary>
    private static double PickGridStep(double pixelsPerBlock)
    {
        double[] candidates = { 5, 10, 25, 50, 100, 250, 500, 1000 };
        foreach (double s in candidates)
        {
            if (s * pixelsPerBlock >= 40.0) return s;
        }
        return candidates[^1];
    }

    /// <summary>
    /// Maps a Y-axis block offset from the map origin to a terrain-style colour.
    /// Gradient: deep-green (low) → green → yellow-brown → brown → grey-white (high).
    /// Normalized so that ~100 blocks spans the full range.
    /// </summary>
    private static Color ElevationColor(float dY)
    {
        float t = GameMath.Clamp((dY + 100f) / 200f, 0f, 1f);

        double r, g, b;
        if (t < 0.25)
        {
            double s = t / 0.25;
            r = Lerp(0.05, 0.15, s); g = Lerp(0.30, 0.60, s); b = Lerp(0.10, 0.15, s);
        }
        else if (t < 0.50)
        {
            double s = (t - 0.25) / 0.25;
            r = Lerp(0.15, 0.55, s); g = Lerp(0.60, 0.55, s); b = Lerp(0.15, 0.10, s);
        }
        else if (t < 0.75)
        {
            double s = (t - 0.50) / 0.25;
            r = Lerp(0.55, 0.50, s); g = Lerp(0.55, 0.35, s); b = 0.10;
        }
        else
        {
            double s = (t - 0.75) / 0.25;
            r = Lerp(0.50, 0.90, s); g = Lerp(0.35, 0.90, s); b = Lerp(0.10, 0.90, s);
        }

        return new Color(r, g, b);
    }

    /// <summary>
    /// Formats a node's label coordinate string.
    /// X and Z are relative to the map origin (signed integer block offsets).
    /// Y is the absolute world block height.
    /// </summary>
    private static string FormatCoords(Vec3f node, int absY)
    {
        int rx = (int)Math.Round(node.X);
        int rz = (int)Math.Round(node.Z);
        string xStr = rx >= 0 ? $"+{rx}" : $"{rx}";
        string zStr = rz >= 0 ? $"+{rz}" : $"{rz}";
        return $"X{xStr}  Y{absY}  Z{zStr}";
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

using System;
using Vintagestory.API.Client;

namespace RealisticSurveying;

/// <summary>
/// Small dialog that lets the player enter or edit a text label for a survey node.
/// </summary>
public class GuiDialogNodeLabel : GuiDialog
{
    private readonly int                  _nodeIndex;
    private readonly Action<int, string>  _onSave;
    private readonly Action<int>?         _onDelete;
    private          string               _labelText;

    public override string ToggleKeyCombinationCode => null;

    public GuiDialogNodeLabel(
        ICoreClientAPI      capi,
        int                 nodeIndex,
        string              coordsDisplay,
        string              currentLabel,
        Action<int, string> onSave,
        Action<int>?        onDelete = null) : base(capi)
    {
        _nodeIndex = nodeIndex;
        _onSave    = onSave;
        _onDelete  = onDelete;
        _labelText = currentLabel;
        SetupDialog(coordsDisplay, currentLabel);
    }

    private void SetupDialog(string coordsDisplay, string currentLabel)
    {
        const double w = 280.0;

        ElementBounds coordsBounds = ElementBounds.Fixed(0,   0,   w,   22);
        ElementBounds inputBounds  = ElementBounds.Fixed(0,   30,  w,   30);
        ElementBounds saveBounds   = ElementBounds.Fixed(0,   70,  80,  30);
        ElementBounds deleteBounds = ElementBounds.Fixed(100, 70,  80,  30);
        ElementBounds cancelBounds = ElementBounds.Fixed(200, 70,  80,  30);

        ElementBounds containerBounds = ElementBounds.Fixed(0, 0, w, 100);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(containerBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        SingleComposer = capi.Gui
            .CreateCompo("realisticsurveying-nodelabel", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Edit Label", () => TryClose())
            .BeginChildElements(bgBounds)
                .AddStaticText(coordsDisplay, CairoFont.WhiteSmallText(), coordsBounds)
                .AddTextInput(inputBounds, t => _labelText = t, CairoFont.WhiteSmallText(), "labelInput")
                .AddButton("Save",   OnSave,   saveBounds)
                .AddButton("Delete", OnDelete, deleteBounds)
                .AddButton("Cancel", TryClose, cancelBounds)
            .EndChildElements()
            .Compose();

        SingleComposer.GetTextInput("labelInput").SetValue(currentLabel);
    }

    private bool OnSave()
    {
        _onSave(_nodeIndex, _labelText);
        return TryClose();
    }

    private bool OnDelete()
    {
        TryClose();
        new GuiDialogConfirm(capi, "Delete marker?",
            () => _onDelete?.Invoke(_nodeIndex)).TryOpen();
        return true;
    }
}

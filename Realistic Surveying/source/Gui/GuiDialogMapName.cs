using System;
using Vintagestory.API.Client;

namespace RealisticSurveying;

/// <summary>
/// Dialog for renaming maps.
/// </summary>
public class GuiDialogMapName : GuiDialog
{
    private readonly Action<string> _onSave;
    private          string         _nameText;

    public override string ToggleKeyCombinationCode => null;

    public GuiDialogMapName(ICoreClientAPI capi, string currentName, Action<string> onSave) : base(capi)
    {
        _onSave   = onSave;
        _nameText = currentName;
        SetupDialog(currentName);
    }

    private void SetupDialog(string currentName)
    {
        const double w = 280.0;

        ElementBounds inputBounds  = ElementBounds.Fixed(0,   0,   w,   30);
        ElementBounds saveBounds   = ElementBounds.Fixed(0,   40,  130, 30);
        ElementBounds cancelBounds = ElementBounds.Fixed(150, 40,  130, 30);

        ElementBounds containerBounds = ElementBounds.Fixed(0, 0, w, 70);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(containerBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        SingleComposer = capi.Gui
            .CreateCompo("realisticsurveying-mapname", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Rename Map", () => TryClose())
            .BeginChildElements(bgBounds)
                .AddTextInput(inputBounds, t => _nameText = t, CairoFont.WhiteSmallText(), "nameInput")
                .AddButton("Save",   OnSave,   saveBounds)
                .AddButton("Cancel", TryClose, cancelBounds)
            .EndChildElements()
            .Compose();

        SingleComposer.GetTextInput("nameInput").SetValue(currentName);
    }

    private bool OnSave()
    {
        _onSave(_nameText);
        return TryClose();
    }
}

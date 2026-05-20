using System;
using Vintagestory.API.Client;

namespace RealisticSurveying.GameContent;

/// <summary>
/// Generic two-button confirmation dialog.
/// </summary>
public class GuiDialogConfirm : GuiDialog
{
    private readonly Action _onConfirm;

    public override string ToggleKeyCombinationCode => null;

    public GuiDialogConfirm(ICoreClientAPI capi, string message, Action onConfirm) : base(capi)
    {
        _onConfirm = onConfirm;
        SetupDialog(message);
    }

    private void SetupDialog(string message)
    {
        const double w = 280.0;

        ElementBounds msgBounds     = ElementBounds.Fixed(0,   0,   w,   40);
        ElementBounds confirmBounds = ElementBounds.Fixed(0,   50,  130, 30);
        ElementBounds cancelBounds  = ElementBounds.Fixed(150, 50,  130, 30);

        ElementBounds containerBounds = ElementBounds.Fixed(0, 0, w, 80);

        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        bgBounds.WithChildren(containerBounds);

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        SingleComposer = capi.Gui
            .CreateCompo("realisticsurveying-confirm", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Confirm", () => TryClose())
            .BeginChildElements(bgBounds)
                .AddStaticText(message, CairoFont.WhiteSmallText(), msgBounds)
                .AddButton("Confirm", OnConfirm, confirmBounds)
                .AddButton("Cancel",  TryClose,  cancelBounds)
            .EndChildElements()
            .Compose();
    }

    private bool OnConfirm()
    {
        _onConfirm();
        return TryClose();
    }
}

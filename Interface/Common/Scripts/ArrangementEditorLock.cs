#nullable enable
using System.Collections.Generic;
using Godot;

public partial class DrumScorePresenter
{
    private readonly List<(Control Target, ColorRect Shade)> _arrangementEditorShades = new();
    private bool _arrangementEditorLocked;
    private Control _arrangementShadeLayer = null!;

    private void InitializeArrangementEditorLock()
    {
        _arrangementShadeLayer = new Control { Name = "ArrangementEditorShadeLayer", MouseFilter = MouseFilterEnum.Ignore, ZIndex = 101 };
        AddChild(_arrangementShadeLayer);
        _arrangementShadeLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Separate shades leave the arrangement transport accessible in the same toolbar.
        foreach (var nodeName in new[] { "MenuBar", "Toolbar", "Sections", "CentralControls" })
        {
            var target = GetNode<Control>($"%{nodeName}");
            var shade = new ColorRect { Name = "ArrangementEditorShade", Color = new Color(0.025f, 0.02f, 0.045f, 0.55f), MouseFilter = MouseFilterEnum.Stop, Visible = false,
                TooltipText = Tr("The editor is locked while the arrangement plays. Stop playback to edit.") };
            _arrangementShadeLayer.AddChild(shade);
            _arrangementEditorShades.Add((target, shade));
        }
    }

    private void RefreshArrangementEditorLockTooltips()
    {
        foreach (var (_, shade) in _arrangementEditorShades)
            shade.TooltipText = Tr("The editor is locked while the arrangement plays. Stop playback to edit.");
    }

    private void UpdateArrangementEditorLock()
    {
        bool locked = _arrangementAudio is not null && _arrangementAudio.IsScorePlaying;
        if (locked && !_arrangementEditorLocked)
            GetViewport().GuiGetFocusOwner()?.ReleaseFocus();
        _arrangementEditorLocked = locked;
        foreach (var (target, shade) in _arrangementEditorShades)
        {
            shade.Visible = locked && target.IsVisibleInTree();
            if (!shade.Visible) continue;
            var rect = target.GetGlobalRect();
            shade.Position = rect.Position - _arrangementShadeLayer.GlobalPosition;
            shade.Size = rect.Size;
        }
    }
}

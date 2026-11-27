#nullable enable
using System;
using Godot;

/// <summary>Reusable folded-row title controls for reordering a score track.</summary>
public partial class TrackReorderTitleControls : HBoxContainer
{
    [Signal] public delegate void MoveRequestedEventHandler(string trackId, int direction);
    [Signal] public delegate void DropRequestedEventHandler(string sourceId, string targetId, bool before);

    private string _trackId = string.Empty;
    private int _position;
    private int _count;
    private Label _handle = null!;
    private Button _up = null!;
    private Button _down = null!;

    public override void _Ready()
    {
        _handle = GetNode<Label>("Handle");
        _up = GetNode<Button>("MoveUp");
        _down = GetNode<Button>("MoveDown");
        _up.Pressed += () => EmitSignal(SignalName.MoveRequested, _trackId, -1);
        _down.Pressed += () => EmitSignal(SignalName.MoveRequested, _trackId, 1);
        MouseExited += () => SetDropTargetActive(false, false);
        ApplyState();
    }

    public void Configure(Guid trackId, int position, int count)
    {
        _trackId = trackId.ToString("N");
        _position = position;
        _count = count;
        ApplyState();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationTranslationChanged && IsInsideTree()) ApplyState();
        else if (what == NotificationDragEnd) SetDropTargetActive(false, false);
    }

    private void ApplyState()
    {
        if (!IsNodeReady()) return;
        _up.Disabled = _position <= 0;
        _down.Disabled = _position >= _count - 1;
        TooltipText = Tr("Drag to reorder this track. Drop above or below another track handle.");
        _handle.TooltipText = TooltipText;
        _up.TooltipText = Tr("Move track up.");
        _down.TooltipText = Tr("Move track down.");
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        if (string.IsNullOrEmpty(_trackId)) return default;
        var preview = new Label { Text = _handle.Text, MouseFilter = Control.MouseFilterEnum.Ignore };
        SetDragPreview(preview);
        return new Godot.Collections.Dictionary { ["kind"] = "score_track", ["id"] = _trackId };
    }

    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.Dictionary) return false;
        var dictionary = data.AsGodotDictionary();
        var accepts = dictionary.ContainsKey("kind") && dictionary.ContainsKey("id")
            && dictionary["kind"].AsString() == "score_track" && dictionary["id"].AsString() != _trackId;
        SetDropTargetActive(accepts, atPosition.Y < Size.Y / 2f);
        return accepts;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        var sourceId = data.AsGodotDictionary()["id"].AsString();
        SetDropTargetActive(false, false);
        EmitSignal(SignalName.DropRequested, sourceId, _trackId, atPosition.Y < Size.Y / 2f);
    }

    private void SetDropTargetActive(bool active, bool before)
    {
        if (_handle is null || !GodotObject.IsInstanceValid(_handle)) return;
        _handle.Text = active ? (before ? "↑" : "↓") : "⠿";
        _handle.Modulate = active ? new Color(0.65f, 0.9f, 1f) : Colors.White;
    }
}

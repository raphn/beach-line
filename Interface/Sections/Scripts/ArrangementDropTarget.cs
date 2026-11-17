#nullable enable
using System;
using Godot;

public partial class ArrangementDropTarget : PanelContainer
{
    public Action<int>? Dropped { get; set; }
    private bool _highlight;
    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        _highlight = data.VariantType == Variant.Type.Int; QueueRedraw(); return _highlight;
    }
    public override void _DropData(Vector2 atPosition, Variant data) { _highlight = false; QueueRedraw(); Dropped?.Invoke((int)data); }
    public override void _Notification(int what) { if (what == NotificationDragEnd || what == NotificationMouseExit) { _highlight = false; QueueRedraw(); } }
    public override void _Draw()
    {
        if (!_highlight) return;
        var color = HasThemeColor("drop_target_highlight", "AppIndicators")
            ? GetThemeColor("drop_target_highlight", "AppIndicators") : new Color(0.70f, 0.55f, 1f, 1f);
        DrawRect(new Rect2(Vector2.Zero, Size), color, false, 3);
    }
}

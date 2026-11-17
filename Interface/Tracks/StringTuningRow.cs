#nullable enable
using Godot;
using System;

/// <summary>One editable string row with accessible ordering and removal controls.</summary>
public partial class StringTuningRow : PanelContainer
{
	private long _listId;
	private int _index;
	private int _dropEdge;
	public Action<int, int>? MoveRequested { get; set; }
	public Action<int>? RemoveRequested { get; set; }
	public NotePitchSelector Selector { get; private set; } = null!;

	public void Setup(long listId, int index, int count, int midiPitch)
	{
		_listId = listId; _index = index;
		var label = GetNode<Label>("StringRowContent/StringNumber");
		Selector = GetNode<NotePitchSelector>("StringRowContent/Pitch");
		var handle = GetNode<Button>("StringRowContent/DragHandle");
		var up = GetNode<Button>("StringRowContent/MoveUp");
		var down = GetNode<Button>("StringRowContent/MoveDown");
		var remove = GetNode<Button>("StringRowContent/RemoveString");
		label.Text = (index + 1).ToString();
		RefreshLabels();
		up.Disabled = index == 0;
		down.Disabled = index == count - 1;
		remove.Disabled = count <= 1;
		up.Pressed += () => MoveRequested?.Invoke(_index, _index - 1);
		down.Pressed += () => MoveRequested?.Invoke(_index, _index + 1);
		remove.Pressed += () => RemoveRequested?.Invoke(_index);
		Selector.MidiPitch = midiPitch;
	}

	private void RefreshLabels()
	{
		GetNode<Button>("StringRowContent/DragHandle").TooltipText = string.Format(Tr("String {0}. Drag to reorder."), _index + 1);
		GetNode<Button>("StringRowContent/MoveUp").TooltipText = string.Format(Tr("Move string {0} up"), _index + 1);
		GetNode<Button>("StringRowContent/MoveDown").TooltipText = string.Format(Tr("Move string {0} down"), _index + 1);
		GetNode<Button>("StringRowContent/RemoveString").TooltipText = string.Format(Tr("Remove string {0}"), _index + 1);
	}

	public override void _Notification(int what)
	{
		if (what == NotificationTranslationChanged && IsInsideTree()) RefreshLabels();
		if (what == NotificationDragEnd || what == NotificationMouseExit) { _dropEdge = 0; QueueRedraw(); }
	}

	public override Variant _GetDragData(Vector2 atPosition)
	{
		var preview = new Label { Text = string.Format(Tr("String {0}"), _index + 1), Modulate = new Color(1, 1, 1, 0.8f) };
		SetDragPreview(preview);
		return Variant.From(new Godot.Collections.Dictionary { ["type"] = "string_tuning_row", ["list"] = _listId, ["index"] = _index });
	}

	public override bool _CanDropData(Vector2 atPosition, Variant data)
	{
		if (data.VariantType != Variant.Type.Dictionary) return false;
		var payload = data.AsGodotDictionary();
		if (!payload.ContainsKey("type") || !payload.ContainsKey("list") || !payload.ContainsKey("index") || payload["type"].AsString() != "string_tuning_row" || payload["list"].AsInt64() != _listId) return false;
		_dropEdge = atPosition.Y < Size.Y / 2 ? -1 : 1; QueueRedraw(); return true;
	}

	public override void _DropData(Vector2 atPosition, Variant data)
	{
		var source = data.AsGodotDictionary()["index"].AsInt32();
		int destination = _index + (atPosition.Y < Size.Y / 2 ? 0 : 1);
		if (source < destination) destination--;
		_dropEdge = 0; QueueRedraw();
		MoveRequested?.Invoke(source, destination);
	}

	public override void _Draw()
	{
		if (_dropEdge == 0) return;
		Color color = HasThemeColor("drop_target_highlight", "AppIndicators") ? GetThemeColor("drop_target_highlight", "AppIndicators") : new Color(0.70f, 0.55f, 1f, 1f);
		float y = _dropEdge < 0 ? 1 : Size.Y - 1;
		DrawLine(new Vector2(0, y), new Vector2(Size.X, y), color, 3);
	}
}

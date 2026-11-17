#nullable enable
using System;
using Godot;

/// <summary>One arrangement entry, with a stepwise repeat handle and drag insertion feedback.</summary>
public partial class SectionThumbnail : PanelContainer
{
	public const float UnitWidth = 56;
	public int Index { get; set; }
	public int Repetitions { get; private set; } = 1;
	public Action<int, int>? RepetitionChanged { get; set; }
	public Action<int, int>? Reordered { get; set; }
	private ProgressBar _progress = null!;
	private bool _resizing;
	private float _resizeStart;
	private int _startRepetitions;
	private int _dropSide;
	private double _playbackProgress;

	public void Setup(string symbol, int repetitions)
	{
		Repetitions = repetitions;
		_progress = GetNode<ProgressBar>("ProgressBar");
		GetNode<Label>("SectionSymbol").Text = symbol;
		GetNode<Label>("Repetitions").Text = $"{repetitions}x";
		CustomMinimumSize = new Vector2(UnitWidth * repetitions, 44);
		UpdateTooltip();
	}

	public void SetPlaybackProgress(double percent)
	{
		if (_progress is null || !GodotObject.IsInstanceValid(_progress)) return;
		var value = Math.Clamp(percent, 0.0, 100.0);
		if (value < 100.0) value = Math.Min(value, 99.99);
		_playbackProgress = value;
		_progress.Value = value;
		UpdateTooltip();
	}

	private void UpdateTooltip() => TooltipText = string.Format(
		Tr("Playback {0}% across {1} repetitions. Drag the body to reorder; drag the right edge to change repetitions."),
		_playbackProgress >= 100.0 ? 100 : (int)Math.Floor(_playbackProgress), Repetitions);

	public override void _GuiInput(InputEvent e)
	{
		if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
		{
			if (button.Pressed && button.Position.X >= Size.X - 9)
			{ _resizing = true; _resizeStart = button.GlobalPosition.X; _startRepetitions = Repetitions; AcceptEvent(); }
			else if (!button.Pressed && _resizing)
			{ _resizing = false; RepetitionChanged?.Invoke(Index, Repetitions); AcceptEvent(); }
		}
		else if (e is InputEventMouseMotion motion && _resizing)
		{
			var steps = (int)Math.Round((motion.GlobalPosition.X - _resizeStart) / UnitWidth, MidpointRounding.AwayFromZero);
			Setup(GetNode<Label>("SectionSymbol").Text, Math.Clamp(_startRepetitions + steps, 1, OpenTab.Music.ArrangementEditing.MaxRepetitions));
			AcceptEvent();
		}
		else if (e is InputEventMouseMotion motionOverThumbnail)
		{
			UpdateCursor(motionOverThumbnail.Position.X);
		}
	}

	private void UpdateCursor(float x)
	{
		MouseDefaultCursorShape = x >= Size.X - 9 ? CursorShape.Hsize : CursorShape.Move;
	}

	public override Variant _GetDragData(Vector2 atPosition)
	{
		if (_resizing || atPosition.X >= Size.X - 9) return default;
		var preview = new Label { Text = GetNode<Label>("SectionSymbol").Text + $"  {Repetitions}x", Modulate = new Color(1, 1, 1, 0.8f) };
		SetDragPreview(preview);
		return Variant.From(Index);
	}

	public override bool _CanDropData(Vector2 atPosition, Variant data)
	{
		if (data.VariantType != Variant.Type.Int) return false;
		_dropSide = atPosition.X < Size.X / 2 ? -1 : 1;
		QueueRedraw();
		return true;
	}
	public override void _DropData(Vector2 atPosition, Variant data)
	{
		var destination = Index + (atPosition.X < Size.X / 2 ? 0 : 1);
		_dropSide = 0; QueueRedraw(); Reordered?.Invoke((int)data, destination);
	}
	public override void _Notification(int what)
	{
		if (what == NotificationTranslationChanged) UpdateTooltip();
		if (what == NotificationMouseEnter) UpdateCursor(GetLocalMousePosition().X);
		if (what == NotificationDragEnd || what == NotificationMouseExit)
		{
			_dropSide = 0;
			if (what == NotificationMouseExit && !_resizing) MouseDefaultCursorShape = CursorShape.Arrow;
			QueueRedraw();
		}
	}
	public override void _Draw()
	{
		if (_dropSide == 0) return;
		var color = HasThemeColor("drop_target_highlight", "AppIndicators")
			? GetThemeColor("drop_target_highlight", "AppIndicators") : new Color(0.70f, 0.55f, 1f, 1f);
		DrawLine(new Vector2(_dropSide < 0 ? 1 : Size.X - 1, 0), new Vector2(_dropSide < 0 ? 1 : Size.X - 1, Size.Y), color, 3);
	}
}

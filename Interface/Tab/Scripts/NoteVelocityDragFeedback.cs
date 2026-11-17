#nullable enable
using Godot;
using System;

/// <summary>Passive, non-interactive velocity feedback shown while dragging over notes.</summary>
public partial class NoteVelocityDragFeedback : CanvasLayer
{
	private const int MarginFromPointer = 14;
	private PanelContainer _panel = null!;
	private Label _label = null!;
	private Vector2 _lastPosition;
	private int? _lastLevel;
	private int _lastCount = 1;
	private int? _lastMaximumLevel;

	public override void _Ready()
	{
		_panel = GetNode<PanelContainer>("%FeedbackPanel");
		_label = GetNode<Label>("%FeedbackLabel");
		SetControlsMouseFilterIgnore(_panel);
		_panel.Hide();
	}

	/// <summary>Shows feedback beside a viewport-space pointer position. Null level means mixed levels.</summary>
	public void ShowAt(Vector2 viewportPosition, int? velocityLevel, int selectionCount = 1, int? maximumLevel = null)
	{
		UpdateFeedback(viewportPosition, velocityLevel, selectionCount, maximumLevel);
		_panel.Show();
	}

	/// <summary>Moves and updates visible feedback without taking focus or mouse input.</summary>
	public void UpdateFeedback(Vector2 viewportPosition, int? velocityLevel, int selectionCount = 1, int? maximumLevel = null)
	{
		if (_panel is null || !GodotObject.IsInstanceValid(_panel)) return;
		_lastPosition = viewportPosition;
		_lastLevel = velocityLevel;
		_lastCount = selectionCount;
		_lastMaximumLevel = maximumLevel;
		var levelText = velocityLevel is null ? "Mixed" : maximumLevel is not null && maximumLevel != velocityLevel
			? $"{Math.Clamp(velocityLevel.Value, 1, 5)}–{Math.Clamp(maximumLevel.Value, 1, 5)}"
			: Math.Clamp(velocityLevel.Value, 1, 5).ToString();
		var count = Math.Max(1, selectionCount);
		_label.Text = count > 1 ? string.Format(Tr("Velocity {0} · {1} notes"), levelText, count) : string.Format(Tr("Velocity {0}"), levelText);

		var viewport = GetViewport();
		var bounds = viewport.GetVisibleRect();
		var desired = viewportPosition + new Vector2(MarginFromPointer, MarginFromPointer);
		var size = _panel.GetCombinedMinimumSize();
		if (desired.X + size.X > bounds.End.X) desired.X = viewportPosition.X - size.X - MarginFromPointer;
		if (desired.Y + size.Y > bounds.End.Y) desired.Y = viewportPosition.Y - size.Y - MarginFromPointer;
		_panel.Position = new Vector2(
			Mathf.Clamp(desired.X, bounds.Position.X, Mathf.Max(bounds.Position.X, bounds.End.X - size.X)),
			Mathf.Clamp(desired.Y, bounds.Position.Y, Mathf.Max(bounds.Position.Y, bounds.End.Y - size.Y)));
	}

	public override void _Notification(int what)
	{
		if (what == NotificationTranslationChanged && _panel is not null && GodotObject.IsInstanceValid(_panel) && _panel.Visible)
			UpdateFeedback(_lastPosition, _lastLevel, _lastCount, _lastMaximumLevel);
	}

	public void Dismiss() => _panel?.Hide();

	private static void SetControlsMouseFilterIgnore(Node node)
	{
		if (node is Control control) control.MouseFilter = Control.MouseFilterEnum.Ignore;
		foreach (var child in node.GetChildren()) SetControlsMouseFilterIgnore(child);
	}
}

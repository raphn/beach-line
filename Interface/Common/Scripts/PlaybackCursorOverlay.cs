using Godot;
using System.Collections.Generic;
using System.Linq;

/// <summary>Transparent, input-pass-through overlay for score playback positions.</summary>
public partial class PlaybackCursorOverlay : Control
{
	public readonly record struct CursorLine(float X, float Top, float Bottom, bool IsStart);
	private CursorLine[] _lines = System.Array.Empty<CursorLine>();

	/// <summary>Replace the visible vertical marker segments, in this overlay's local coordinates.</summary>
	public void SetLines(IReadOnlyList<CursorLine> lines)
	{
		if (_lines.SequenceEqual(lines)) return;
		_lines = lines.ToArray();
		QueueRedraw();
	}

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
	}

	public override void _Notification(int what)
	{
		if (what == NotificationThemeChanged || what == NotificationResized) QueueRedraw();
	}

	public override void _Draw()
	{
		foreach (var line in _lines)
		{
			float x = Mathf.Clamp(line.X, 0, Size.X);
			float top = Mathf.Clamp(line.Top, 0, Size.Y);
			float bottom = Mathf.Clamp(line.Bottom, top, Size.Y);
			var color = TabColor(line.IsStart ? "playback_start_cursor_color" : "playback_cursor_color",
				line.IsStart ? new Color(0.28f, 0.86f, 0.91f, 0.9f) : new Color(1f, 0.72f, 0.32f, 0.96f));
			DrawLine(new Vector2(x, top), new Vector2(x, bottom), color, line.IsStart ? 2f : 3f, true);
			DrawLine(new Vector2(x - (line.IsStart ? 4 : 5), top + 1),
				new Vector2(x + (line.IsStart ? 4 : 5), top + 1), color, line.IsStart ? 3f : 4f, true);
		}
	}

	private Color TabColor(string token, Color fallback) => HasThemeColor(token, "TabMeasureView")
		? GetThemeColor(token, "TabMeasureView") : fallback;
}

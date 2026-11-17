#nullable enable
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music;

/// <summary>Presentation-only tablature measure. Event times are exact rational quarter-note units.</summary>
public partial class MeasureTabView : Control
{
	/// <summary>Exact musical time in whole-note units.</summary>
	public readonly record struct ExactTime(long Numerator, long Denominator)
	{
		public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;
		public ExactTime Reduced()
		{
			long a = Math.Abs(Numerator), b = Math.Abs(Denominator);
			while (b != 0) (a, b) = (b, a % b);
			long gcd = Math.Max(1, a);
			return new ExactTime(Numerator / gcd, Denominator / gcd);
		}
	}

	public sealed record ExpressionMarker(Guid Id, ExpressionKind Kind);
	public sealed record TabNoteViewData(string Id, int StringNumber, int Fret, ExactTime Onset, ExactTime Duration, bool IsContinuation = false, bool IsClipped = false, int VelocityLevel = 3, int TupletActual = 0, IReadOnlyList<ExpressionMarker>? Expressions = null, StringArticulation IncomingArticulation = StringArticulation.None);
	/// <param name="MeasureDuration">Exact length in whole-note units (a 4/4 bar is 1/1).</param>
	public sealed record TabMeasureViewData(IReadOnlyList<int> OpenStringMidiPitches, int FretLimit, ExactTime MeasureDuration,
		IReadOnlyList<TabNoteViewData> Notes, int BeatNumerator = 4, int BeatDenominator = 4, IReadOnlyList<string>? StringLabels = null,
		bool IsRepeat = false, int RepeatSourceMeasureNumber = 0, bool IsPercussion = false, uint TrackColorRgba = 0xffffffff);
	public sealed record TabEditIntent(string Action, string EventId, int StringNumber, int Fret, ExactTime Onset, ExactTime Duration,
		int NavigateHorizontal = 0, int NavigateVertical = 0, IReadOnlyList<TabNoteViewData>? Notes = null, int TupletActual = 0);
	/// <summary>Serializable resumable editor state for this measure view. Transient pointer gestures are excluded.</summary>
	public sealed record EditorSnapshot(string[] SelectedEventIds, string SelectedEventId, string SelectionAnchorId,
		int ActiveStringNumber, ExactTime ActiveOnset, string EditingNoteId, string FretDigits);

	[Signal] public delegate void EditRequestedEventHandler(string action, string eventId, int stringNumber, int fret,
		long onsetNumerator, long onsetDenominator, long durationNumerator, long durationDenominator);
	[Signal] public delegate void CollisionFeedbackEventHandler(string message);

	public event Action<TabEditIntent>? EditIntent;
	/// <summary>Raised on primary click so the presenter can select this cell's authored source.</summary>
	public event Action? MeasureSelected;
	/// <summary>Raised whenever the visible note selection changes.</summary>
	public event Action<IReadOnlyList<TabNoteViewData>>? SelectionChanged;
	/// <summary>Alt-left-drag velocity gesture. Level delta is signed: up is positive, in five discrete levels per full step.</summary>
	public event Action<IReadOnlyList<TabNoteViewData>, Vector2>? VelocityGestureStarted;
	/// <summary>Reports current pointer position, movement since the previous update, and signed discrete level change since the previous update.</summary>
	public event Action<IReadOnlyList<TabNoteViewData>, Vector2, Vector2, int>? VelocityGestureUpdated;
	/// <summary>Reports the selected notes and total signed level change when the gesture ends.</summary>
	public event Action<IReadOnlyList<TabNoteViewData>, int>? VelocityGestureEnded;
	/// <summary>Reports the selected notes when an in-progress gesture is canceled.</summary>
	public event Action<IReadOnlyList<TabNoteViewData>>? VelocityGestureCanceled;
	/// <summary>Raised for a right-click on an existing note or hit, with its global pointer position.</summary>
	public event Action<TabNoteViewData, Vector2>? NoteContextRequested;
	/// <summary>Raised after user editing state changes, including selection and uncommitted fret entry.</summary>
	public new event Action? EditorStateChanged;
	/// <summary>Optional synchronous edit handler. Return false to keep the current entry open.</summary>
	public Func<TabEditIntent, bool>? EditIntentHandler { get; set; }
	/// <summary>Consumes a left click when an external placement mode owns tab input.</summary>
	public Func<Vector2, bool>? PlacementClickHandler { get; set; }
	public TabMeasureViewData? ViewData { get; private set; }
	/// <summary>Track tint for note and hit fills. White preserves the original untinted grayscale appearance.</summary>
	public uint TrackColorRgba { get; private set; } = 0xffffffff;
	public string SelectedEventId { get; set; } = "";
	/// <summary>Selected visible note snapshots, including IDs and onset/string positions for presenter copy operations.</summary>
	public IReadOnlyList<TabNoteViewData> SelectedNotes => ViewData is null ? Array.Empty<TabNoteViewData>() :
		ViewData.Notes.Where(n => _selectedEventIds.Contains(n.Id)).ToArray();
	/// <summary>The selected note nearest the last click or drag-box start, used as the presenter's paste anchor.</summary>
	public TabNoteViewData? SelectionAnchorNote => ViewData?.Notes.FirstOrDefault(n => n.Id == _selectionAnchorId);
	/// <summary>Whether a fret value is being typed and must not be discarded by a clipboard action.</summary>
	public bool HasUncommittedFretInput => (_pendingString >= 0 || _fretEditNote is not null) && _fretDigits.Length > 0;
	public int SnapDenominator { get; set; } = 4;
	/// <summary>Vertical pointer travel in pixels per one of the five velocity levels.</summary>
	public float VelocityPixelsPerLevel { get; set; } = 12f;
	/// <summary>Tuplet ratio used for new entries: zero for straight, or 3, 5, or 6.</summary>
	public float MeasureWidth { get => Size.X; set { CustomMinimumSize = new Vector2(value, CustomMinimumSize.Y); QueueRedraw(); } }
	public float RowHeight { get; set; } = 34f;
	/// <summary>Row height from the shared theme/defaults before any parent layout override.</summary>
	public float ThemeRowHeight => GetThemeRowHeight();
	public float NoteHeight { get; private set; } = 23f;
	/// <summary>Vertical start of the note lanes inside the measure view.</summary>
	public float RowStart => Top;
	/// <summary>Optional shared row height supplied by a parent layout, such as percussion kit headers.</summary>
	public float? EffectiveRowHeightOverride { get; private set; }
	/// <summary>Raised after theme or bound-data changes recalculate this view's minimum geometry.</summary>
	public event Action? LayoutGeometryChanged;

	private const float Left = 32f;
	private const float Right = 8f;
	private const float Top = 22f;
	private const float DefaultRowHeight = 34f;
	private const float DefaultPercussionRowHeight = 42f;
	private const float DefaultNoteHeight = 23f;
	private const float MinimumRowHeight = 8f;
	private const float MaximumRowHeight = 200f;
	private const float MinimumNoteHeight = 8f;
	private const float MaximumNoteHeight = 100f;
	private TabNoteViewData? _dragNote;
	private bool _resize;
	private bool _dragging;
	private bool _movedBeyondThreshold;
	private Vector2 _press;
	private TabNoteViewData? _preview;
	private IReadOnlyList<TabNoteViewData> _movePreviews = Array.Empty<TabNoteViewData>();
	private int _targetString;
	private int _pendingString = -1;
	private double _pendingOnset;
	private string _fretDigits = "";
	private TabNoteViewData? _fretEditNote;
	private bool _collision;
	private string _collisionMessage = "";
	private bool _hasHover;
	private Vector2 _hoverPosition;
	private TabNoteViewData? _hoverNote;
	private readonly HashSet<string> _selectedEventIds = new(StringComparer.Ordinal);
	private string _selectionAnchorId = "";
	private bool _boxSelecting;
	private bool _velocityGesture;
	private Vector2 _lastVelocityPointer;
	private float _velocityStepRemainder;
	private int _velocityTotalLevelDelta;
	private bool _boxAdditive;
	private Rect2 _selectionBox;
	private bool _showPlaybackStart;
	private double? _playbackFraction;
	private readonly Dictionary<(string Style, int Level), StyleBoxFlat> _velocityStyles = new();

	public override void _Ready()
	{
		MouseFilter = MouseFilterEnum.Stop;
		FocusMode = FocusModeEnum.All;
		MouseExited += ClearHover;
		TreeExiting += CancelVelocityGesture;
		AppPreferences.BindingsChanged += RefreshTooltip;
		RefreshThemeGeometry();
		RefreshTooltip();
	}

	public override void _Notification(int what)
	{
		if (what == NotificationThemeChanged) { _velocityStyles.Clear(); RefreshThemeGeometry(); }
		if (what == NotificationTranslationChanged) RefreshTooltip();
		if (what == NotificationThemeChanged || what == NotificationResized) QueueRedraw();
	}

	public override void _ExitTree()
	{
		AppPreferences.BindingsChanged -= RefreshTooltip;
	}

	private void RefreshThemeGeometry()
	{
		float rowHeight = GetThemeRowHeight();
		RowHeight = EffectiveRowHeightOverride is { } effective
			? Math.Max(MinimumRowHeight, effective)
			: Math.Clamp(float.IsFinite(rowHeight) ? rowHeight : DefaultRowHeight, MinimumRowHeight, MaximumRowHeight);
		float noteHeight = HasThemeConstant("note_height", "TabMeasureView")
			? GetThemeConstant("note_height", "TabMeasureView") : DefaultNoteHeight;
		NoteHeight = Math.Min(Math.Clamp(float.IsFinite(noteHeight) ? noteHeight : DefaultNoteHeight,
			MinimumNoteHeight, MaximumNoteHeight), Math.Max(1f, RowHeight - 8f));
		int strings = ViewData?.OpenStringMidiPitches.Count ?? 6;
		float requiredHeight = Top + strings * RowHeight + 8;
		CustomMinimumSize = new Vector2(Math.Max(CustomMinimumSize.X, GetMinimumMeasureWidth()), requiredHeight);
		if (GetParent() is Control content)
		{
			content.CustomMinimumSize = new Vector2(content.CustomMinimumSize.X, requiredHeight);
			if (content.GetParent() is Control column)
			{
				var header = column.GetNodeOrNull<Control>("MeasureHeader");
				float headerHeight = header?.GetCombinedMinimumSize().Y ?? 0f;
				column.CustomMinimumSize = new Vector2(Math.Max(column.CustomMinimumSize.X, GetMinimumMeasureWidth()), headerHeight + requiredHeight);
			}
		}
		QueueRedraw();
		LayoutGeometryChanged?.Invoke();
	}

	private float GetThemeRowHeight()
	{
		float rowHeight = ViewData?.IsPercussion == true ? DefaultPercussionRowHeight : DefaultRowHeight;
		if (HasThemeConstant("row_height", "TabMeasureView")) rowHeight = GetThemeConstant("row_height", "TabMeasureView");
		if (ViewData?.IsPercussion == true && HasThemeConstant("percussion_row_height", "TabMeasureView"))
			rowHeight = GetThemeConstant("percussion_row_height", "TabMeasureView");
		return Math.Clamp(float.IsFinite(rowHeight) ? rowHeight : DefaultRowHeight, MinimumRowHeight, MaximumRowHeight);
	}

	/// <summary>Apply or clear a parent-computed row height without changing the shared theme baseline.</summary>
	public void SetEffectiveRowHeight(float? height)
	{
		float? next = height is { } value && float.IsFinite(value)
			? Math.Max(MinimumRowHeight, value)
			: null;
		if (EffectiveRowHeightOverride == next) return;
		EffectiveRowHeightOverride = next;
		RefreshThemeGeometry();
	}

	/// <summary>Pure geometry rule used by percussion kit layout and suitable for focused checks.</summary>
	public static float ResolveSharedRowHeight(float themeBaselineHeight, float naturalHeaderMinimumHeight)
	{
		float themeHeight = float.IsFinite(themeBaselineHeight)
			? Math.Clamp(themeBaselineHeight, MinimumRowHeight, MaximumRowHeight)
			: DefaultPercussionRowHeight;
		float headerHeight = float.IsFinite(naturalHeaderMinimumHeight) ? Math.Max(0, naturalHeaderMinimumHeight) : 0;
		return MathF.Ceiling(Math.Max(MinimumRowHeight, Math.Max(themeHeight, headerHeight)));
	}

	/// <summary>Pure spacer rule that aligns the first header row to a rendered lane origin.</summary>
	public static float ResolveHeaderSpacerHeight(float laneGlobalY, float rowStart, float headersGlobalY)
	{
		if (!float.IsFinite(laneGlobalY) || !float.IsFinite(rowStart) || !float.IsFinite(headersGlobalY)) return 0;
		return MathF.Ceiling(Math.Max(0, laneGlobalY + rowStart - headersGlobalY));
	}

	private float GetMinimumMeasureWidth()
	{
		float width = HasThemeConstant("minimum_measure_width", "TabMeasureView")
			? GetThemeConstant("minimum_measure_width", "TabMeasureView") : 180f;
		return Math.Clamp(float.IsFinite(width) ? width : 180f, 80f, 2000f);
	}

	public float MinimumCellWidth
	{
		get
		{
			float width = HasThemeConstant("minimum_cell_width", "TabMeasureView")
				? GetThemeConstant("minimum_cell_width", "TabMeasureView") : 24f;
			return Math.Clamp(float.IsFinite(width) ? width : 24f, 1f, 500f);
		}
	}

	private void ClearHover()
	{
		_hasHover = false;
		_hoverNote = null;
		QueueRedraw();
	}

	/// <summary>Replace the displayed snapshot. The view never mutates this data.</summary>
	public void Bind(TabMeasureViewData data)
	{
		SetTrackDisplayColor(data.TrackColorRgba);
		ViewData = data;
		RefreshTooltip();
		if (data.IsPercussion) { _pendingString = -1; _fretEditNote = null; _fretDigits = ""; }
		RefreshThemeGeometry();
		_selectedEventIds.RemoveWhere(id => !data.Notes.Any(note => note.Id == id));
		if (!data.Notes.Any(note => note.Id == _selectionAnchorId)) _selectionAnchorId = "";
		if (!string.IsNullOrEmpty(SelectedEventId) && data.Notes.Any(note => note.Id == SelectedEventId)) _selectedEventIds.Add(SelectedEventId);
		QueueRedraw();
	}

	private void RefreshTooltip()
	{
		var copy = AppPreferences.FormatBinding("copy_notes");
		var paste = AppPreferences.FormatBinding("paste_notes");
		TooltipText = ViewData?.IsPercussion == true
			? string.Format(Tr("Click a measure to set the playback start.\nDouble-click empty space or Ctrl-click to add a hit.\nDrag hits to move or resize; Alt-drag up or down to change velocity.\nDrag empty space to select. Shift-drag adds to selection.\n{0} copies; hover a destination and press {1}."), copy, paste)
			: string.Format(Tr("Click a measure to set the playback start (green); orange shows playback.\nAlt-drag a note up or down to change velocity.\nDrag empty space to select notes; Shift-drag adds to selection.\nCtrl+Left/Right changes selected frets; Ctrl+Up/Down moves selected notes between strings.\n{0} copies; hover a destination and press {1}.\nCtrl-click adds an open-string note.\nDouble-click to edit a fret; Enter or an arrow confirms. Escape cancels."), copy, paste);
	}

	/// <summary>Update this existing view's note tint without rebinding its measure data.</summary>
	public void SetTrackDisplayColor(uint rgba)
	{
		if (TrackColorRgba == rgba) return;
		TrackColorRgba = rgba;
		_velocityStyles.Clear();
		QueueRedraw();
	}

	/// <summary>Set the optional stationary bar-start marker and moving playback position.</summary>
	/// <param name="start">Whether to show the stationary marker at the start of this measure.</param>
	/// <param name="fraction">Playback position from 0 to 1 within this measure, or null to hide it.</param>
	public void SetPlaybackCursor(bool start, double? fraction)
	{
		var next = fraction is { } value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : (double?)null;
		if (_showPlaybackStart == start && _playbackFraction == next) return;
		_showPlaybackStart = start;
		_playbackFraction = next;
		QueueRedraw();
	}

	/// <summary>Start fret entry at a snapped string and onset, usually after keyboard navigation.</summary>
	public void BeginCursor(int stringNumber, ExactTime onset)
	{
		if (ViewData is null || ViewData.IsPercussion || ViewData.OpenStringMidiPitches.Count == 0) return;
		int targetString = Math.Clamp(stringNumber, 1, ViewData.OpenStringMidiPitches.Count);
		double targetOnset = Snap(onset.Value);
		var existing = ViewData.Notes.FirstOrDefault(n => !n.IsContinuation && n.StringNumber == targetString && Math.Abs(n.Onset.Value - targetOnset) < 1e-9);
		if (existing is not null) BeginFretEdit(existing);
		else
		{
			_pendingString = targetString - 1;
			_pendingOnset = targetOnset;
			_fretEditNote = null;
			_fretDigits = "";
			_collision = false;
			QueueRedraw();
		}
		GrabFocus();
		RaiseEditorStateChanged();
	}

	/// <summary>Capture selection and resumable fret-entry state without including an in-progress pointer gesture.</summary>
	public EditorSnapshot CaptureEditorSnapshot() => new(
		_selectedEventIds.ToArray(), SelectedEventId, _selectionAnchorId,
		_pendingString >= 0 ? _pendingString + 1 : _fretEditNote?.StringNumber ?? 0,
		_fretEditNote?.Onset ?? (_pendingString >= 0
			? new ExactTime((long)Math.Round(_pendingOnset * Math.Max(1, SnapDenominator)), Math.Max(1, SnapDenominator)).Reduced()
			: new ExactTime(0, 1)),
		_fretEditNote?.Id ?? string.Empty, _fretDigits);

	/// <summary>Restore only selection and fret entry that remain valid in the currently bound view.</summary>
	/// <returns>True when a valid view was available; stale IDs and invalid cursor values are ignored.</returns>
	public bool RestoreEditorSnapshot(EditorSnapshot? snapshot, bool focus)
	{
		if (ViewData is null || snapshot is null) return false;
		var visibleIds = ViewData.Notes.Select(note => note.Id).ToHashSet(StringComparer.Ordinal);
		var selected = (snapshot.SelectedEventIds ?? Array.Empty<string>()).Where(id => !string.IsNullOrEmpty(id) && visibleIds.Contains(id))
			.ToHashSet(StringComparer.Ordinal);
		var primary = selected.Contains(snapshot.SelectedEventId) ? snapshot.SelectedEventId : selected.FirstOrDefault() ?? string.Empty;
		var anchor = selected.Contains(snapshot.SelectionAnchorId) ? snapshot.SelectionAnchorId : primary;
		_selectedEventIds.Clear();
		_selectedEventIds.UnionWith(selected);
		SelectedEventId = primary;
		_selectionAnchorId = anchor;

		_pendingString = -1;
		_fretEditNote = null;
		_fretDigits = string.Empty;
		var digitsValid = snapshot.FretDigits is not null && snapshot.FretDigits.Length <= 3 && snapshot.FretDigits.All(char.IsAsciiDigit);
		var onset = snapshot.ActiveOnset;
		var activePositionValid = snapshot.ActiveStringNumber >= 1 &&
			snapshot.ActiveStringNumber <= ViewData.OpenStringMidiPitches.Count && onset.Denominator > 0 &&
			onset.Value >= 0 && onset.Value < ViewData.MeasureDuration.Value;
		if (!ViewData.IsPercussion && digitsValid && activePositionValid)
		{
			var editingNote = string.IsNullOrEmpty(snapshot.EditingNoteId) ? null :
				ViewData.Notes.FirstOrDefault(note => note.Id == snapshot.EditingNoteId && !note.IsContinuation);
			if (!string.IsNullOrEmpty(snapshot.EditingNoteId))
			{
				if (editingNote is not null && editingNote.StringNumber == snapshot.ActiveStringNumber &&
					Math.Abs(editingNote.Onset.Value - onset.Value) < 1e-9)
				{
					_fretEditNote = editingNote;
					SelectedEventId = editingNote.Id;
					_selectedEventIds.Add(editingNote.Id);
				}
			}
			else
			{
				_pendingString = snapshot.ActiveStringNumber - 1;
				_pendingOnset = onset.Value;
			}
			if (_pendingString >= 0 || _fretEditNote is not null) _fretDigits = snapshot.FretDigits ?? string.Empty;
		}
		_collision = false;
		_collisionMessage = string.Empty;
		QueueRedraw();
		if (focus && IsInsideTree()) GrabFocus();
		return true;
	}

	private void RaiseEditorStateChanged()
	{
		if (IsInsideTree() && !IsQueuedForDeletion()) EditorStateChanged?.Invoke();
	}

	private void CancelVelocityGesture()
	{
		if (!_velocityGesture) return;
		_velocityGesture = false;
		_dragNote = null;
		VelocityGestureCanceled?.Invoke(SelectedNotes.ToArray());
	}

	private void UpdateVelocityGesture(Vector2 position)
	{
		if (!_velocityGesture) return;
		Vector2 movement = position - _lastVelocityPointer;
		_lastVelocityPointer = position;
		_velocityStepRemainder += movement.Y;
		float pixelsPerLevel = Math.Max(1f, VelocityPixelsPerLevel);
		int downwardLevels = (int)(_velocityStepRemainder / pixelsPerLevel);
		if (downwardLevels != 0) _velocityStepRemainder -= downwardLevels * pixelsPerLevel;
		int levelDelta = -downwardLevels;
		_velocityTotalLevelDelta += levelDelta;
		VelocityGestureUpdated?.Invoke(SelectedNotes.ToArray(), position, movement, levelDelta);
	}

	private void EndVelocityGesture()
	{
		if (!_velocityGesture) return;
		_velocityGesture = false;
		_dragNote = null;
		VelocityGestureEnded?.Invoke(SelectedNotes.ToArray(), _velocityTotalLevelDelta);
	}

	public override void _Draw()
	{
		var data = ViewData;
		var font = GetThemeDefaultFont() ?? ThemeDB.FallbackFont;
		int count = data?.OpenStringMidiPitches.Count ?? 6;
		float timelineWidth = Math.Max(1, Size.X - Left - Right);
		float lineThickness = HasThemeConstant("line_thickness", "TabMeasureView")
			? Math.Max(1, GetThemeConstant("line_thickness", "TabMeasureView")) : 1;
		DrawStyleBox(GetThemeStylebox("panel", "TabMeasureView"), new Rect2(Vector2.Zero, Size));
		if (data?.IsRepeat == true)
		{
			DrawRect(new Rect2(2, 2, Math.Max(0, Size.X - 4), Math.Max(0, Size.Y - 4)), TabColor("repeat_overlay_tint", new Color(0.137352f, 0.101968f, 0.2328f, 1f)));
			DrawRect(new Rect2(2, 2, Math.Max(0, Size.X - 4), Math.Max(0, Size.Y - 4)), TabColor("repeat_overlay_outline", new Color(0.70f, 0.55f, 1f, 1f)), false, 3);
		}
		DrawRect(new Rect2(Left, Top - 15, timelineWidth, 1), TabColor("timeline"));
		float duration = (float)(data?.MeasureDuration.Value ?? 1);
		if (duration <= 0) duration = 1;
		int beatDenom = data?.BeatDenominator ?? 4;
		float beatLength = 1f / Math.Max(1, beatDenom);
		int beats = Math.Max(1, (int)Math.Ceiling(duration / beatLength));
		for (int i = 0; i <= beats; i++)
		{
			float t = Math.Min(duration, i * beatLength);
			float x = Left + timelineWidth * t / duration;
			DrawLine(new Vector2(x, Top - 18), new Vector2(x, Top + count * RowHeight), i == 0 || t >= duration ? TabColor("bar_line") : TabColor("grid"), (i == 0 || t >= duration ? 2 : 1) * lineThickness);
			if (i < beats) DrawString(font, new Vector2(x + 3, Top - 4), (i + 1).ToString(), HorizontalAlignment.Left, -1, 15, TabColor("beat_text"));
		}
		int snapDenominator = Math.Max(1, SnapDenominator);
		double snapUnit = 1d / snapDenominator;
		int snapSteps = Math.Max(1, (int)Math.Ceiling(duration / snapUnit));
		for (int i = 1; i < snapSteps; i++)
		{
			float t = Math.Min(duration, (float)(i * snapUnit));
			float x = Left + timelineWidth * t / duration;
			bool beatBoundary = Math.Abs(t / beatLength - Math.Round(t / beatLength)) < 0.0001f;
			if (beatBoundary) continue;
			int divisor = i, denominator = snapDenominator;
			while (denominator != 0) (divisor, denominator) = (denominator, divisor % denominator);
			int reducedDenominator = snapDenominator / Math.Max(1, divisor);
			int level = Math.Max(1, (int)Math.Ceiling(Math.Log2((double)reducedDenominator / Math.Max(1, beatDenom))));
			var subdivision = TabColor("subdivision");
			subdivision.A *= MathF.Pow(0.7f, level - 1);
			DrawLine(new Vector2(x, Top - 12), new Vector2(x, Top + count * RowHeight), subdivision, Math.Max(0.5f, lineThickness * MathF.Pow(0.8f, level)));
		}
		for (int row = 0; row < count; row++)
		{
			float y = Top + row * RowHeight + RowHeight / 2;
			DrawLine(new Vector2(Left, y), new Vector2(Size.X - Right, y), TabColor("string_line"), (row == 0 || row == count - 1 ? 1.5f : 1f) * lineThickness);
			int midi = data?.OpenStringMidiPitches.ElementAtOrDefault(row) ?? 0;
			string label = data?.StringLabels?.ElementAtOrDefault(row) ?? $"{row + 1} · {MidiName(midi)}";
			DrawString(font, new Vector2(2, y + 4), label, HorizontalAlignment.Left, Left - 4, 11, TabColor("string_text"));
		}
		if (data is null) return;
		DrawArticulationLinks(data.Notes, timelineWidth, duration);
		foreach (var note in data.Notes) DrawNote(note, timelineWidth, duration, count, _selectedEventIds.Contains(note.Id), false);
		if (_hasHover && _hoverNote is not null)
		{
			var hoverRect = NoteRect(_hoverNote, timelineWidth, duration);
			var hoverFill = HoverColor(); hoverFill.A = 0.20f;
			DrawRect(hoverRect, hoverFill, true);
			DrawRect(hoverRect, HoverColor(), false, 2f);
		}
		if (_hasHover && TryGetHoverCell(_hoverPosition, out int hoverRow, out double hoverOnset))
		{
			float cellX = Left + timelineWidth * (float)(hoverOnset / duration);
			float cellY = Top + hoverRow * RowHeight;
			float cellRight = Math.Min(Left + timelineWidth, cellX + timelineWidth * (float)(1d / Math.Max(1, SnapDenominator) / duration));
			var cellFill = HoverColor(); cellFill.A = 0.16f;
			DrawRect(new Rect2(cellX, cellY + 1, Math.Max(2f, cellRight - cellX), RowHeight - 2), cellFill, true);
			DrawLine(new Vector2(cellRight, cellY + 1), new Vector2(cellRight, cellY + RowHeight - 1), HoverColor(), 2f);
			DrawCircle(new Vector2(cellRight, cellY + RowHeight / 2), 4f, HoverColor());
		}
		if (data.IsRepeat)
			DrawString(font, new Vector2(Math.Max(Left, Size.X - 172), 15), $"↻ REPEAT · M{data.RepeatSourceMeasureNumber}", HorizontalAlignment.Left, 164, 12, TabColor("repeat_overlay_label_color", new Color(0.88f, 0.84f, 1f, 1f)));
		if (_resize && _movePreviews.Count > 0)
			foreach (var note in _movePreviews) DrawNote(note, timelineWidth, duration, count, true, _collision);
		else if (_resize && _preview is not null) DrawNote(_preview, timelineWidth, duration, count, true, _collision);
		else foreach (var note in _movePreviews) DrawNote(note, timelineWidth, duration, count, true, _collision);
		if (_boxSelecting)
		{
			var boxFill = HoverColor(); boxFill.A = 0.14f;
			DrawRect(_selectionBox, boxFill, true);
			DrawRect(_selectionBox, HoverColor(), false, 1.5f);
		}
		if (_pendingString >= 0 || _fretEditNote is not null)
		{
			var entryOnset = _fretEditNote?.Onset.Value ?? _pendingOnset;
			var entryRow = _fretEditNote is null ? _pendingString : _fretEditNote.StringNumber - 1;
			float entryX = Math.Clamp(Left + (float)(entryOnset / duration) * timelineWidth, 0, Math.Max(0, Size.X - 62));
			float entryY = Top + entryRow * RowHeight + 4;
			DrawStyleBox(GetThemeStylebox("entry", "TabMeasureView"), new Rect2(entryX, entryY, 62, NoteHeight));
			string entryText = _fretDigits.Length == 0 ? "Fret: " : $"Fret: {_fretDigits}";
			float textX = entryX + 4;
			DrawString(font, new Vector2(textX, entryY + 16), entryText, HorizontalAlignment.Left, 54, 12, TabColor("text"));
			float caretX = textX + font.GetStringSize(entryText, HorizontalAlignment.Left, -1, 12).X + 1;
			DrawLine(new Vector2(caretX, entryY + 4), new Vector2(caretX, entryY + NoteHeight - 4), TabColor("caret"), 2);
			DrawString(font, new Vector2(entryX, entryY + NoteHeight + 13), "Arrows: save and move · Enter: save · Esc: cancel", HorizontalAlignment.Left, Math.Max(80, Size.X - entryX - Right), 10, TabColor("hint"));
		}
		if (_collision) DrawString(font, new Vector2(Left + 3, Size.Y - 4), _collisionMessage, HorizontalAlignment.Left, Size.X - Left, 12, TabColor("error"));
		if (_showPlaybackStart)
		{
			var marker = TabColor("playback_start_cursor_color", new Color(0.28f, 0.86f, 0.91f, 0.9f));
			DrawLine(new Vector2(Left, Top - 18), new Vector2(Left, Top + count * RowHeight), marker, 2f, true);
			DrawLine(new Vector2(Left - 4, Top - 19), new Vector2(Left + 4, Top - 19), marker, 3f, true);
		}
		if (_playbackFraction is { } fraction)
		{
			float playbackX = Left + timelineWidth * (float)fraction;
			var cursor = TabColor("playback_cursor_color", new Color(1f, 0.72f, 0.32f, 0.96f));
			DrawLine(new Vector2(playbackX, Top - 18), new Vector2(playbackX, Top + count * RowHeight), cursor, 3f, true);
			DrawLine(new Vector2(playbackX - 5, Top - 19), new Vector2(playbackX + 5, Top - 19), cursor, 4f, true);
		}
	}

	/// <summary>Returns the current pointer's snapped cell for paste or other presenter actions.</summary>
	public bool TryGetHoverCellContext(out int stringNumber, out ExactTime onset)
	{
		if (_hasHover && TryGetHoverCell(_hoverPosition, out int row, out double value))
		{
			stringNumber = row + 1;
			onset = FromDouble(value);
			return true;
		}
		stringNumber = 0;
		onset = new ExactTime(0, 1);
		return false;
	}

	private void SetSelection(IEnumerable<string> ids, string primary, string? anchor = null)
	{
		var next = ids.Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
		if (_selectedEventIds.SetEquals(next) && SelectedEventId == primary && (anchor is null || _selectionAnchorId == anchor)) return;
		_selectedEventIds.Clear();
		_selectedEventIds.UnionWith(next);
		SelectedEventId = _selectedEventIds.Contains(primary) ? primary : _selectedEventIds.FirstOrDefault() ?? "";
		_selectionAnchorId = anchor is not null && _selectedEventIds.Contains(anchor) ? anchor : SelectedEventId;
		SelectionChanged?.Invoke(SelectedNotes);
		QueueRedraw();
		RaiseEditorStateChanged();
	}

	private void DrawNote(TabNoteViewData note, float width, float measureDuration, int rows, bool selected, bool collision)
	{
		if (note.StringNumber < 1 || note.StringNumber > rows) return;
		double start = Math.Max(0, note.Onset.Value);
		double end = start + Math.Max(0, note.Duration.Value);
		float x = Left + width * (float)(start / measureDuration);
		float right = Left + width * (float)(Math.Min(measureDuration, end) / measureDuration);
		float y = NoteTop(note.StringNumber);
		var rect = new Rect2(x, y, Math.Max(8, right - x), NoteHeight);
		var noteStyle = collision ? "note_error" : selected ? "note_selected" : "note";
		var style = GetVelocityStyle(noteStyle, note.VelocityLevel, out var background);
		DrawStyleBox(style, rect);
		float brightness = 0.299f * background.R + 0.587f * background.G + 0.114f * background.B;
		string tupletMark = note.TupletActual switch { 3 => "³", 5 => "⁵", 6 => "⁶", _ => "" };
		if (ViewData?.IsPercussion == true)
		{
			if (tupletMark.Length > 0) DrawString(GetThemeDefaultFont() ?? ThemeDB.FallbackFont, new Vector2(x + 2, y + 14), tupletMark, HorizontalAlignment.Left, Math.Max(12, rect.Size.X - 4), 11, brightness > 0.5f ? Colors.Black : Colors.White);
		}
		else DrawString(GetThemeDefaultFont() ?? ThemeDB.FallbackFont, new Vector2(x + 4, y + 16), note.Fret + tupletMark, HorizontalAlignment.Left, Math.Max(20, rect.Size.X - 8), 13, brightness > 0.5f ? Colors.Black : Colors.White);
		if (ViewData?.IsPercussion != true && !note.IsContinuation && note.IncomingArticulation != StringArticulation.None)
		{
			var mark = note.IncomingArticulation == StringArticulation.HammerOn ? "H" : "P";
			var markColor = note.IncomingArticulation == StringArticulation.HammerOn ? new Color(0.35f, 0.96f, 0.65f) : new Color(0.55f, 0.78f, 1f);
			DrawString(GetThemeDefaultFont() ?? ThemeDB.FallbackFont, new Vector2(Math.Max(x + 16, right - 13), y + 9), mark, HorizontalAlignment.Left, 12, 9, markColor);
		}
		if (note.Expressions is { Count: > 0 })
		{
			// Each effect has a color; linked curves share its exact shade.
			var marks = note.Expressions.OrderBy(mark => mark.Kind).ThenBy(mark => mark.Id).ToArray();
			float left = rect.Position.X, segmentWidth = rect.Size.X / marks.Length;
			for (int i = 0; i < marks.Length; i++)
			{
				var mark = marks[i];
				var baseColor = mark.Kind switch { ExpressionKind.Velocity => new Color(0.25f, 0.9f, 0.35f), ExpressionKind.Pitch => new Color(0.35f, 0.7f, 1f), ExpressionKind.Vibrato => new Color(0.83f, 0.48f, 1f), _ => new Color(1f, 0.66f, 0.25f) };
				var bytes = mark.Id.ToByteArray();
				float shade = 0.68f + (bytes[0] / 255f) * 0.32f;
				DrawLine(new Vector2(left + i * segmentWidth, y + NoteHeight + 1), new Vector2(left + (i + 1) * segmentWidth, y + NoteHeight + 1), baseColor * new Color(shade, shade, shade, 1), 2f);
			}
		}
		if (note.IsContinuation || note.IsClipped || end > measureDuration)
		{
			float edge = end >= measureDuration ? Left + width : right;
			DrawLine(new Vector2(edge - 5, y + 4), new Vector2(edge, y + NoteHeight / 2), TabColor("continuation"), 2);
			DrawLine(new Vector2(edge, y + NoteHeight / 2), new Vector2(edge - 5, y + NoteHeight - 4), TabColor("continuation"), 2);
		}
	}

	private void DrawArticulationLinks(IReadOnlyList<TabNoteViewData> notes, float width, float measureDuration)
	{
		if (ViewData?.IsPercussion == true || measureDuration <= 0) return;
		var ordered = notes.Where(note => !note.IsContinuation && note.IncomingArticulation != StringArticulation.None)
			.OrderBy(note => note.StringNumber).ThenBy(note => note.Onset.Value).ToArray();
		foreach (var target in ordered)
		{
			var source = notes.Where(note => note.StringNumber == target.StringNumber && note.Onset.Value < target.Onset.Value)
				.OrderByDescending(note => note.Onset.Value).FirstOrDefault();
			if (source is null) continue;
			float targetX = Left + width * (float)(target.Onset.Value / measureDuration) + 2;
			float sourceX = Math.Min(targetX, Left + width * (float)(source.Onset.Value / measureDuration) + 10);
			float y = NoteTop(target.StringNumber) - 2;
			var color = target.IncomingArticulation == StringArticulation.HammerOn ? new Color(0.35f, 0.96f, 0.65f, 0.9f) : new Color(0.55f, 0.78f, 1f, 0.9f);
			float bend = Math.Min(8f, Math.Max(3f, (targetX - sourceX) * 0.2f));
			DrawLine(new Vector2(sourceX, y), new Vector2((sourceX + targetX) / 2, y - bend), color, 1.5f, true);
			DrawLine(new Vector2((sourceX + targetX) / 2, y - bend), new Vector2(targetX, y), color, 1.5f, true);
		}
	}

	private StyleBox GetVelocityStyle(string name, int velocityLevel, out Color background)
	{
		int level = Math.Clamp(velocityLevel, 1, 5);
		var style = GetThemeStylebox(name, "TabMeasureView");
		if (style is not StyleBoxFlat flat) { background = Colors.Black; return style; }
		if (!_velocityStyles.TryGetValue((name, level), out var adjusted))
		{
			// Duplicate preserves selection/error outlines; only the fill is replaced.
			adjusted = (StyleBoxFlat)flat.Duplicate();
			var velocityFill = (GetThemeStylebox("note", "TabMeasureView") as StyleBoxFlat)?.BgColor ?? flat.BgColor;
			adjusted.BgColor = CalculateNoteFill(velocityFill, level, TrackColorRgba);
			_velocityStyles[(name, level)] = adjusted;
		}
		background = adjusted.BgColor;
		return adjusted;
	}

	/// <summary>Apply the five-level value scale and track RGBA tint used for note and hit fills.</summary>
	public static Color CalculateNoteFill(Color themedFill, int velocityLevel, uint trackColorRgba)
	{
		int level = Math.Clamp(velocityLevel, 1, 5);
		var velocityFill = Color.FromHsv(themedFill.H, themedFill.S, 0.13f + (level - 1) * 0.205f, themedFill.A);
		return velocityFill * new Color(trackColorRgba);
	}

	private Rect2 NoteRect(TabNoteViewData note, float width, float measureDuration)
	{
		double start = Math.Max(0, note.Onset.Value);
		double end = start + Math.Max(0, note.Duration.Value);
		float x = Left + width * (float)(start / measureDuration);
		float right = Left + width * (float)(Math.Min(measureDuration, end) / measureDuration);
		float y = NoteTop(note.StringNumber);
		return new Rect2(x, y, Math.Max(8, right - x), NoteHeight);
	}

	private float NoteTop(int stringNumber) => Top + (stringNumber - 1) * RowHeight +
		(ViewData?.IsPercussion == true ? (RowHeight - NoteHeight) / 2f : 4f);

	private Color HoverColor()
	{
		var color = HasThemeColor("note_selected_border", "TabMeasureView")
			? GetThemeColor("note_selected_border", "TabMeasureView")
			: HasThemeColor("caret", "TabMeasureView") ? GetThemeColor("caret", "TabMeasureView") : Colors.White;
		return color.A < 0.2f ? Colors.White : color;
	}

	private Color TabColor(string token) => GetThemeColor(token, "TabMeasureView");

	private Color TabColor(string token, Color fallback) => HasThemeColor(token, "TabMeasureView")
		? GetThemeColor(token, "TabMeasureView") : fallback;

	public override void _GuiInput(InputEvent e)
	{
		if (e is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.CtrlPressed && TryGetNavigation(key.Keycode, out int fretStep, out int stringStep))
			{
				if (!(ViewData?.IsPercussion == true && fretStep != 0) && _pendingString < 0 && _fretEditNote is null && SelectedNotes.Count > 0)
				{
					var notes = SelectedNotes.ToArray();
					var anchor = notes.FirstOrDefault(n => n.Id == SelectedEventId) ?? notes[0];
					var target = anchor with { Fret = anchor.Fret + fretStep, StringNumber = anchor.StringNumber + stringStep };
					EmitIntent(fretStep != 0 ? "shift_fret" : "move", target, notes: notes);
				}
				AcceptEvent(); return;
			}
			if (key.Keycode == Key.Delete)
			{
				if (_pendingString < 0 && _fretEditNote is null && SelectedNotes.Count > 0) { EmitSignal(SignalName.EditRequested, "delete", SelectedEventId, 0, 0, 0L, 1L, 0L, 1L); DispatchIntent(new TabEditIntent("delete", SelectedEventId, 0, 0, new ExactTime(0, 1), new ExactTime(0, 1), Notes: SelectedNotes.ToArray())); }
				AcceptEvent(); return;
			}
			if (key.Keycode == Key.F2 && ViewData is not null && !ViewData.IsPercussion) { var selected = ViewData.Notes.FirstOrDefault(n => n.Id == SelectedEventId); if (selected is not null) { BeginFretEdit(selected); AcceptEvent(); return; } }
			if (key.Keycode == Key.Escape) { bool changed = _pendingString >= 0 || _fretEditNote is not null || _fretDigits.Length > 0 || _selectedEventIds.Count > 0; CancelVelocityGesture(); _preview = null; _movePreviews = Array.Empty<TabNoteViewData>(); _dragging = false; _boxSelecting = false; _movedBeyondThreshold = false; _fretDigits = ""; _pendingString = -1; _fretEditNote = null; _collision = false; QueueRedraw(); if (changed) RaiseEditorStateChanged(); AcceptEvent(); return; }
			if (_pendingString >= 0 || _fretEditNote is not null)
			{
				if (TryGetDigit(key.Keycode, out int digit) && _fretDigits.Length < 3) { _fretDigits += digit.ToString(); QueueRedraw(); RaiseEditorStateChanged(); AcceptEvent(); return; }
				if (key.Keycode == Key.Backspace && _fretDigits.Length > 0) { _fretDigits = _fretDigits[..^1]; QueueRedraw(); RaiseEditorStateChanged(); AcceptEvent(); return; }
				if ((key.Keycode == Key.Enter || key.Keycode == Key.KpEnter) && _fretDigits.Length > 0) { if (_fretEditNote is not null) CommitFretEdit(); else CommitNewNote(); AcceptEvent(); return; }
				if (TryGetNavigation(key.Keycode, out int horizontal, out int vertical))
				{
					if (_fretDigits.Length > 0)
					{
						if (_fretEditNote is not null) CommitFretEdit(horizontal, vertical); else CommitNewNote(horizontal, vertical);
					}
					else EmitNavigation(horizontal, vertical);
					AcceptEvent(); return;
				}
			}
		}
		if (e is InputEventMouseButton rightMouse && rightMouse.ButtonIndex == MouseButton.Right && rightMouse.Pressed)
		{
			var note = HitNote(rightMouse.Position, out _);
			if (note is not null)
			{
				if (!_selectedEventIds.Contains(note.Id)) SetSelection(new[] { note.Id }, note.Id, note.Id);
				NoteContextRequested?.Invoke(note, GetViewport().GetMousePosition());
				AcceptEvent();
			}
			return;
		}
		if (e is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
		{
			if (mouse.Pressed)
			{
				if (PlacementClickHandler?.Invoke(mouse.Position) == true) { AcceptEvent(); return; }
				if (mouse.CtrlPressed)
				{
					// Preserve unfinished fret input and prevent normal drag/double-click behavior.
					if ((_pendingString >= 0 || _fretEditNote is not null) && _fretDigits.Length > 0)
					{
						ReportCollision("Finish or cancel the current fret entry first");
						AcceptEvent();
						return;
					}
					MeasureSelected?.Invoke();
					GrabFocus();
					AcceptEvent();
					TryInsertOpenStringNote(mouse.Position);
					return;
				}
				MeasureSelected?.Invoke();
				GrabFocus(); _press = mouse.Position;
				_dragNote = HitNote(mouse.Position, out _resize);
				if (mouse.AltPressed && _dragNote is not null)
				{
					if (!_selectedEventIds.Contains(_dragNote.Id)) SetSelection(new[] { _dragNote.Id }, _dragNote.Id, _dragNote.Id);
					else SetSelection(_selectedEventIds, _dragNote.Id, _dragNote.Id);
					_velocityGesture = true;
					_lastVelocityPointer = mouse.Position;
					_velocityStepRemainder = 0;
					_velocityTotalLevelDelta = 0;
					_dragging = false; _boxSelecting = false; _preview = null; _movePreviews = Array.Empty<TabNoteViewData>();
					VelocityGestureStarted?.Invoke(SelectedNotes.ToArray(), mouse.Position);
					QueueRedraw(); AcceptEvent(); return;
				}
				if (mouse.DoubleClick)
				{
					_boxSelecting = false;
					if (ViewData?.IsPercussion == true) { if (_dragNote is null) TryInsertOpenStringNote(mouse.Position); }
					else if (_dragNote is not null) BeginFretEdit(_dragNote);
					else BeginNote(mouse.Position);
					_dragging = false; _preview = null; _dragNote = null;
				}
				else if (_dragNote is not null)
				{
					if (mouse.ShiftPressed)
					{
						var ids = _selectedEventIds.ToHashSet(StringComparer.Ordinal);
						if (!ids.Add(_dragNote.Id)) ids.Remove(_dragNote.Id);
						SetSelection(ids, _dragNote.Id, _dragNote.Id);
						_dragging = false; _preview = null; _dragNote = null; QueueRedraw();
						AcceptEvent(); return;
					}
					else if (!_selectedEventIds.Contains(_dragNote.Id)) SetSelection(new[] { _dragNote.Id }, _dragNote.Id, _dragNote.Id);
					else SetSelection(_selectedEventIds, _dragNote.Id, _dragNote.Id);
					_dragging = true; _movedBeyondThreshold = false; _preview = _dragNote; QueueRedraw();
				}
				else
				{
					_boxSelecting = true;
					_boxAdditive = mouse.ShiftPressed;
					_movedBeyondThreshold = false;
					_selectionBox = new Rect2(_press, Vector2.Zero);
				}
				AcceptEvent();
			}
			else if (_velocityGesture) { EndVelocityGesture(); AcceptEvent(); }
			else if (_boxSelecting)
			{
				if (_movedBeyondThreshold) ApplyBoxSelection();
				else if (!_boxAdditive) SetSelection(Array.Empty<string>(), "", "");
				_boxSelecting = false; _movedBeyondThreshold = false; QueueRedraw(); AcceptEvent();
			}
			else if (_dragging && _preview is not null) { if (_movedBeyondThreshold && !_collision) EmitIntent(_resize ? "resize" : "move", _preview, notes: SelectedNotes.ToArray()); _dragging = false; _movedBeyondThreshold = false; _preview = null; _movePreviews = Array.Empty<TabNoteViewData>(); _dragNote = null; QueueRedraw(); AcceptEvent(); }
		}
		if (e is InputEventMouseMotion motion)
		{
			_hoverPosition = motion.Position;
			_hasHover = new Rect2(Vector2.Zero, Size).HasPoint(motion.Position);
			bool resizeHover = false;
			_hoverNote = _hasHover ? HitNote(motion.Position, out resizeHover) : null;
			MouseDefaultCursorShape = _hoverNote is null ? CursorShape.Arrow : resizeHover ? CursorShape.Hsize : CursorShape.Move;
			if (_velocityGesture) UpdateVelocityGesture(motion.Position);
			else if (_dragging && _dragNote is not null) UpdateDrag(motion.Position);
			else if (_boxSelecting)
			{
				if (motion.Position.DistanceTo(_press) > 4f) _movedBeyondThreshold = true;
				_selectionBox = MakeRect(_press, motion.Position);
				QueueRedraw();
			}
			else QueueRedraw();
		}
	}

	private static Rect2 MakeRect(Vector2 a, Vector2 b) => new(new Vector2(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Vector2(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
	private void ApplyBoxSelection()
	{
		if (ViewData is null) return;
		var ids = _boxAdditive ? _selectedEventIds.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
		foreach (var note in ViewData.Notes)
		{
			if (note.StringNumber < 1 || note.StringNumber > ViewData.OpenStringMidiPitches.Count) continue;
			if (_selectionBox.Intersects(NoteRect(note, Size.X - Left - Right, (float)ViewData.MeasureDuration.Value), true)) ids.Add(note.Id);
		}
		var anchor = ViewData.Notes.Where(n => ids.Contains(n.Id))
			.OrderBy(n => NoteRect(n, Size.X - Left - Right, (float)ViewData.MeasureDuration.Value).GetCenter().DistanceTo(_press))
			.FirstOrDefault()?.Id ?? "";
		SetSelection(ids, anchor, anchor);
	}

	private void TryInsertOpenStringNote(Vector2 position)
	{
		if (ViewData is null || !TryGetHoverCell(position, out int row, out double onset)) return;
		var duration = new ExactTime(1, Math.Max(1, SnapDenominator)).Reduced();
		var created = new TabNoteViewData("", row + 1, 0, FromDouble(onset), duration);
		if (WouldCollide(created, ""))
		{
			ReportCollision(ViewData.IsPercussion ? "That lane already has a hit at this onset" : "That string already has a note at this onset");
			return;
		}
		if (!EmitIntent("insert", created))
		{
			_collision = true;
			_collisionMessage = "Edit was rejected";
			QueueRedraw();
		}
	}

	private bool TryGetHoverCell(Vector2 position, out int row, out double onset)
	{
		if (ViewData is null || !TryMapPointerToCell(position, Size, ViewData.OpenStringMidiPitches.Count, RowHeight, SnapDenominator,
			ViewData.MeasureDuration, out row, out var exactOnset)) { row = -1; onset = 0; return false; }
		onset = exactOnset.Value;
		return true;
	}

	/// <summary>Pure pointer-to-cell mapping shared by hover and insertion; the measure end is never a valid attack.</summary>
	public static bool TryMapPointerToCell(Vector2 pointer, Vector2 size, int stringCount, float rowHeight, int snapDenominator,
		ExactTime measureDuration, out int row, out ExactTime onset, int tupletActual = 0)
	{
		row = -1;
		onset = new ExactTime(0, 1);
		if (stringCount <= 0 || rowHeight <= 0 || pointer.X < Left || pointer.X >= size.X - Right ||
			pointer.Y < Top || pointer.Y >= Top + stringCount * rowHeight || measureDuration.Value <= 0) return false;
		row = Math.Clamp((int)((pointer.Y - Top) / rowHeight), 0, stringCount - 1);
		double duration = measureDuration.Value;
		var ratio = TupletRatio(tupletActual);
		double unit = (double)ratio.Numerator / (Math.Max(1, snapDenominator) * ratio.Denominator);
		long lastIndex = (long)Math.Ceiling(duration / unit - 1e-10) - 1;
		if (lastIndex < 0) { row = -1; return false; }
		double timelineWidth = Math.Max(1, size.X - Left - Right);
		double pointedTime = Math.Clamp((pointer.X - Left) / timelineWidth * duration, 0, duration * 64);
		double snapped = Math.Clamp(Math.Floor(pointedTime / unit + 1e-10), 0, lastIndex) * unit;
		if (snapped < 0 || snapped >= duration) { row = -1; return false; }
		long steps = (long)Math.Round(snapped / unit, MidpointRounding.AwayFromZero);
		onset = new ExactTime(steps * ratio.Numerator, (long)Math.Max(1, snapDenominator) * ratio.Denominator).Reduced();
		return true;
	}

	private void BeginNote(Vector2 p)
	{
		if (ViewData is null || !TryGetHoverCell(p, out int row, out double onset)) return;
		_pendingString = row;
		_pendingOnset = onset; _fretDigits = ""; _fretEditNote = null; _collision = false; QueueRedraw(); RaiseEditorStateChanged();
	}
	private void BeginFretEdit(TabNoteViewData note)
	{
		SelectedEventId = note.Id;
		_fretEditNote = note;
		_pendingString = -1;
		_fretDigits = "";
		_collision = false;
		QueueRedraw();
		RaiseEditorStateChanged();
	}
	private void CommitNewNote(int navigateHorizontal = 0, int navigateVertical = 0)
	{
		if (!int.TryParse(_fretDigits, out int fret) || ViewData is null || fret < 0 || fret > ViewData.FretLimit) { _collision = true; _collisionMessage = "Fret is outside the configured range"; QueueRedraw(); return; }
		var dur = new ExactTime(1, Math.Max(1, SnapDenominator)).Reduced();
		var created = new TabNoteViewData("", _pendingString + 1, fret, FromDouble(_pendingOnset), dur);
		if (WouldCollide(created, "")) { ReportCollision("That string already has a note at this onset"); return; }
		var previousString = _pendingString; var previousDigits = _fretDigits;
		_pendingString = -1; _fretDigits = "";
		if (!EmitIntent("insert", created, navigateHorizontal, navigateVertical)) { _pendingString = previousString; _fretDigits = previousDigits; _collision = true; _collisionMessage = "Edit was rejected; correct it or retry"; QueueRedraw(); return; }
		RaiseEditorStateChanged();
	}
	private void CommitFretEdit(int navigateHorizontal = 0, int navigateVertical = 0)
	{
		if (ViewData is null || _fretEditNote is null || !int.TryParse(_fretDigits, out int fret) || fret < 0 || fret > ViewData.FretLimit) { ReportCollision("Fret is outside the configured range"); return; }
		var previousNote = _fretEditNote; var previousDigits = _fretDigits;
		_fretEditNote = null; _fretDigits = "";
		if (!EmitIntent("fret", previousNote with { Fret = fret }, navigateHorizontal, navigateVertical)) { _fretEditNote = previousNote; _fretDigits = previousDigits; _collision = true; _collisionMessage = "Edit was rejected; correct it or retry"; QueueRedraw(); return; }
		RaiseEditorStateChanged();
	}
	private void EmitNavigation(int horizontal, int vertical)
	{
		if (ViewData is null) return;
		int stringNumber = _fretEditNote?.StringNumber ?? _pendingString + 1;
		var onset = _fretEditNote?.Onset ?? FromDouble(_pendingOnset);
		var unit = new ExactTime(1, Math.Max(1, SnapDenominator)).Reduced();
		var duration = new ExactTime(1, Math.Max(1, SnapDenominator)).Reduced();
		var intent = new TabEditIntent("navigate", "", stringNumber, 0, onset, duration, horizontal, vertical);
		if (!DispatchIntent(intent)) { _collision = true; _collisionMessage = "Navigation was rejected"; QueueRedraw(); }
	}
	private void UpdateDrag(Vector2 p)
	{
		if (ViewData is null || _dragNote is null) return;
		if (p.DistanceTo(_press) > 4f) _movedBeyondThreshold = true;
		if (!_movedBeyondThreshold) return;
		float duration = (float)ViewData.MeasureDuration.Value;
		int dragTuplet = _dragNote.TupletActual is 3 or 5 or 6 ? _dragNote.TupletActual : 0;
		double onset = _resize ? _dragNote.Onset.Value : Snap(TimeAt(p.X) - TimeAt(_press.X) + _dragNote.Onset.Value, dragTuplet);
		double len = _resize ? Snap(TimeAt(p.X) - TimeAt(_press.X) + _dragNote.Duration.Value, dragTuplet) : _dragNote.Duration.Value;
		int row = _resize ? _dragNote.StringNumber : (int)Math.Floor((p.Y - Top) / RowHeight) + 1;
		if (_resize)
		{
			var selected = SelectedNotes;
			var resizedAnchor = FromGrid(Math.Max(GridStep(dragTuplet), len), dragTuplet);
			var deltaDuration = Subtract(resizedAnchor, _dragNote.Duration);
			_movePreviews = selected.Select(n => n with { Duration = Add(n.Duration, deltaDuration) }).ToArray();
			_preview = _movePreviews.FirstOrDefault(n => n.Id == _dragNote.Id);
			if (_preview is null) _preview = _dragNote with { Duration = resizedAnchor };
			_collision = _movePreviews.Any(n => n.Duration.Value <= 0)
				|| WouldCollide(_preview, _dragNote.Id);
		}
		else
		{
			var selected = SelectedNotes;
			int deltaRow = row - _dragNote.StringNumber;
			var snappedOnset = FromGrid(onset, dragTuplet);
			var deltaTime = Subtract(snappedOnset, _dragNote.Onset);
			_movePreviews = selected.Select(n => n with { StringNumber = n.StringNumber + deltaRow, Onset = Add(n.Onset, deltaTime) }).ToArray();
			_preview = _movePreviews.FirstOrDefault(n => n.Id == _dragNote.Id);
			_collision = _movePreviews.Any(n => n.StringNumber < 1 || n.StringNumber > ViewData.OpenStringMidiPitches.Count || n.Onset.Value < 0 || n.Onset.Value >= ViewData.MeasureDuration.Value || n.IsContinuation);
		}
		_collisionMessage = _collision ? (_resize ? "Resize must keep every selected duration valid" : "Move must remain within the measure and available strings") : "";
		QueueRedraw();
	}
	private TabNoteViewData? HitNote(Vector2 p, out bool resize)
	{
		resize = false; if (ViewData is null) return null;
		float w = Size.X - Left - Right, measure = (float)ViewData.MeasureDuration.Value;
		foreach (var n in ViewData.Notes.Reverse())
		{
			if (n.StringNumber < 1 || n.StringNumber > ViewData.OpenStringMidiPitches.Count) continue;
			float x = Left + w * (float)(n.Onset.Value / measure), end = Left + w * (float)((n.Onset.Value + n.Duration.Value) / measure);
			float y = NoteTop(n.StringNumber);
			if (p.X >= x - 5 && p.X <= end + 5 && p.Y >= y - 4 && p.Y <= y + NoteHeight + 4) { resize = p.X >= end - 9; return n; }
		}
		return null;
	}
	private bool WouldCollide(TabNoteViewData candidate, string exclude)
	{
		if (ViewData is null) return false;
		double a = candidate.Onset.Value, b = a + candidate.Duration.Value;
		return ViewData.Notes.Any(n => n.Id != exclude && !n.IsContinuation && n.StringNumber == candidate.StringNumber && Math.Abs(a - n.Onset.Value) < 1e-9);
	}
	private void ReportCollision(string message) { _collision = true; _collisionMessage = message; EmitSignal(SignalName.CollisionFeedback, message); QueueRedraw(); }
	private bool EmitIntent(string action, TabNoteViewData n, int navigateHorizontal = 0, int navigateVertical = 0, IReadOnlyList<TabNoteViewData>? notes = null)
	{
		var o = n.Onset.Reduced(); var d = n.Duration.Reduced();
		EmitSignal(SignalName.EditRequested, action, n.Id, n.StringNumber, n.Fret, o.Numerator, o.Denominator, d.Numerator, d.Denominator);
		return DispatchIntent(new TabEditIntent(action, n.Id, n.StringNumber, n.Fret, o, d, navigateHorizontal, navigateVertical, notes, n.TupletActual));
	}
	private bool DispatchIntent(TabEditIntent intent)
	{
		if (EditIntentHandler is not null) return EditIntentHandler(intent);
		EditIntent?.Invoke(intent);
		return true;
	}
	private static bool TryGetDigit(Key key, out int digit)
	{
		if (key >= Key.Key0 && key <= Key.Key9) { digit = (int)key - (int)Key.Key0; return true; }
		if (key >= Key.Kp0 && key <= Key.Kp9) { digit = (int)key - (int)Key.Kp0; return true; }
		digit = 0; return false;
	}
	private static bool TryGetNavigation(Key key, out int horizontal, out int vertical)
	{
		horizontal = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
		vertical = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
		return horizontal != 0 || vertical != 0;
	}
	private double TimeAt(float x) => ViewData is null ? 0 : Math.Clamp((x - Left) / Math.Max(1, Size.X - Left - Right) * ViewData.MeasureDuration.Value, 0, ViewData.MeasureDuration.Value * 64);
	private double Snap(double time, int tupletActual = 0) { double unit = GridStep(tupletActual); return Math.Round(time / unit, MidpointRounding.AwayFromZero) * unit; }
	private double GridStep(int tupletActual) { var ratio = TupletRatio(tupletActual); return (double)ratio.Numerator / (Math.Max(1, SnapDenominator) * ratio.Denominator); }
	private ExactTime FromGrid(double value, int tupletActual)
	{
		var ratio = TupletRatio(tupletActual);
		double unit = (double)ratio.Numerator / (Math.Max(1, SnapDenominator) * ratio.Denominator);
		long steps = (long)Math.Round(value / unit, MidpointRounding.AwayFromZero);
		return new ExactTime(steps * ratio.Numerator, (long)Math.Max(1, SnapDenominator) * ratio.Denominator).Reduced();
	}
	private static ExactTime Add(ExactTime a, ExactTime b) => new ExactTime(a.Numerator * b.Denominator + b.Numerator * a.Denominator, a.Denominator * b.Denominator).Reduced();
	private static ExactTime Subtract(ExactTime a, ExactTime b) => new ExactTime(a.Numerator * b.Denominator - b.Numerator * a.Denominator, a.Denominator * b.Denominator).Reduced();
	private static (int Numerator, int Denominator) TupletRatio(int actual) => actual switch { 3 => (2, 3), 5 or 6 => (4, actual), _ => (1, 1) };
	private static ExactTime FromDouble(double v) { const long den = 1920; return new ExactTime((long)Math.Round(v * den), den).Reduced(); }
	private static string MidiName(int midi) { string[] names = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"]; return $"{names[((midi % 12) + 12) % 12]}{midi / 12 - 1}"; }
}

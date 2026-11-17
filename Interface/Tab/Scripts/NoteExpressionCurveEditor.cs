#nullable enable
using Godot;
using OpenTab.Music;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Reusable editor for portable per-note expression curves.</summary>
public partial class NoteExpressionCurveEditor : Window
{
	private OptionButton _kindOption = null!;
	private OptionButton _presetOption = null!;
	private ExpressionCurveGraph _graph = null!;
	private Label _status = null!;
	private Label _legend = null!;
	private SpinBox _value = null!;
	private Button _remove = null!;
	private Button _previewButton = null!;
	private CheckBox _loopButton = null!;
	private SpinBox _pitchMin = null!;
	private SpinBox _pitchMax = null!;
	private OptionButton _division = null!;
	private CheckBox _snap = null!;
	private CheckBox _snapPitch = null!;
	private CheckBox _stepSegment = null!;
	private Control _pitchRangeRow = null!;
	private double _pitchDuration = 1;
	private double _velocitySpan = 1;
	private bool _durationVaries;
	private readonly Dictionary<ExpressionKind, (double Min, double Max)> _ranges = new();
	private readonly int[] _divisionDenominators = { 4, 8, 16, 32, 64, 12, 24 };
	private IReadOnlyList<IReadOnlyList<NoteExpression>> _assignments = Array.Empty<IReadOnlyList<NoteExpression>>();
	private readonly Dictionary<ExpressionKind, List<ExpressionPoint>> _drafts = new();
	private readonly Dictionary<ExpressionKind, NoteExpression?> _uniformExisting = new();
	private readonly Dictionary<ExpressionKind, bool> _hasAnyAssignment = new();
	private readonly Dictionary<ExpressionKind, bool> _isMixed = new();
	private ExpressionKind _kind;
	private int _selectedPoint = -1;
	private bool _suppress;
	private bool _previewing;

	public event Action<NoteExpression>? Applied;
	public event Action<ExpressionKind>? Removed;
	public event Action? Cancelled;
	public event Action? Closed;
	/// <summary>Raised when preview starts or its draft changes. The value is an unapplied copy.</summary>
	public event Action<NoteExpression, bool>? PreviewRequested;
	public event Action? PreviewStopped;
	public event Action<bool>? LoopChanged;
	public bool IsLoopEnabled => _loopButton?.ButtonPressed ?? true;
	/// <summary>Set status text from the preview owner.</summary>
	public void SetPreviewStatus(string text) => _status.Text = Tr(text);
	/// <summary>Reset the preview toggle after playback ends naturally.</summary>
	public void SetPreviewEnded()
	{
		_previewing = false;
		if (_previewButton is not null) _previewButton.Text = Tr("Preview");
	}

	public override void _Ready()
	{
		_kindOption = GetNode<OptionButton>("%KindOption");
		_presetOption = GetNode<OptionButton>("%PresetOption");
		_graph = GetNode<ExpressionCurveGraph>("%Graph");
		_status = GetNode<Label>("%Status");
		_legend = GetNode<Label>("%Legend");
		_value = GetNode<SpinBox>("%PointValue");
		_remove = GetNode<Button>("%RemoveButton");
		_previewButton = GetNode<Button>("%PreviewButton");
		_loopButton = GetNode<CheckBox>("%LoopButton");
		_pitchMin = GetNode<SpinBox>("%PitchMin");
		_pitchMax = GetNode<SpinBox>("%PitchMax");
		_division = GetNode<OptionButton>("%HorizontalDivision");
		_division.Select(4); // A sixteenth-note event then starts with four editable cells.
		_snap = GetNode<CheckBox>("%Snap");
		_snapPitch = GetNode<CheckBox>("%SnapPitch");
		_stepSegment = GetNode<CheckBox>("%StepSegment");
		_pitchRangeRow = GetNode<Control>("%PitchRangeRow");
		_kindOption.ItemSelected += OnKindSelected;
		_presetOption.ItemSelected += OnPresetSelected;
		_graph.PointsChanged += OnPointsChanged;
		_graph.PointSelected += OnPointSelected;
		_value.ValueChanged += OnValueChanged;
		GetNode<Button>("%ApplyButton").Pressed += Apply;
		_remove.Pressed += () => { Removed?.Invoke(_kind); Hide(); };
		GetNode<Button>("%CancelButton").Pressed += Cancel;
		_previewButton.Pressed += TogglePreview;
		_loopButton.Toggled += OnLoopToggled;
		_pitchMin.ValueChanged += _ => OnPitchRangeChanged();
		_pitchMax.ValueChanged += _ => OnPitchRangeChanged();
		_division.ItemSelected += _ => ConfigureGraph();
		_snap.Toggled += _ => ConfigureGraph();
		_snapPitch.Toggled += _ => ConfigureGraph();
		_stepSegment.Toggled += enabled => { if (!_suppress) _graph.SetSelectedSegmentMode(enabled ? ExpressionSegmentMode.Step : ExpressionSegmentMode.Linear); };
		CloseRequested += Cancel;
		VisibilityChanged += OnVisibilityChanged;
		RefreshLocalizedLabels();
		SetKindOption(ExpressionKind.Pitch);
	}

	/// <summary>Initialize from each selected note's complete expression list. Empty per-note lists mean no assigned expression.</summary>
	public void Initialize(IReadOnlyList<IReadOnlyList<NoteExpression>> perNoteExpressions, ExpressionKind initialKind, double pitchDuration = 1, double velocitySpan = 1, bool durationVaries = false)
	{
		if (_graph is null) throw new InvalidOperationException("Add NoteExpressionCurveEditor to the scene tree before initializing it.");
		_assignments = perNoteExpressions ?? Array.Empty<IReadOnlyList<NoteExpression>>();
		_pitchDuration = Math.Max(0.0001, pitchDuration);
		_velocitySpan = Math.Max(0.0001, velocitySpan);
		_durationVaries = durationVaries;
		if (_assignments.Count == 0) _assignments = new[] { (IReadOnlyList<NoteExpression>)Array.Empty<NoteExpression>() };
		_drafts.Clear();
		_uniformExisting.Clear();
		_hasAnyAssignment.Clear();
		_isMixed.Clear();
		foreach (ExpressionKind kind in Enum.GetValues(typeof(ExpressionKind)))
		{
			var matches = _assignments.Select(expressions => expressions.FirstOrDefault(e => e.Kind == kind)).ToArray();
			bool same = matches.All(item => item is not null) && matches.Skip(1).All(item => item!.Id == matches[0]!.Id && item.MinSemitones == matches[0]!.MinSemitones && item.MaxSemitones == matches[0]!.MaxSemitones && SamePoints(item.Points, matches[0]!.Points));
			_hasAnyAssignment[kind] = matches.Any(item => item is not null);
			_isMixed[kind] = !same && _hasAnyAssignment[kind];
			var source = same ? matches[0] : null;
			_uniformExisting[kind] = source;
			_drafts[kind] = source is null ? PresetPoints(kind, 0) : source.Points.Select(point => new ExpressionPoint(point.X, point.Y, point.SegmentMode)).ToList();
			_ranges[kind] = source is null ? (-2, 2) : (source.MinSemitones, source.MaxSemitones);
		}
		_kind = initialKind;
		SetKindOption(initialKind);
		RefreshKind();
	}

	/// <summary>Open near viewport coordinates, clamping to visible bounds.</summary>
	public void OpenAt(Vector2 viewportPosition)
	{
		var viewport = GetParent().GetViewport();
		var embedded = viewport.GuiEmbedSubwindows;
		var position = embedded ? viewportPosition : (Vector2)DisplayServer.MouseGetPosition();
		var bounds = embedded ? viewport.GetVisibleRect() : (Rect2)DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenWithMouseFocus);
		var size = (Vector2)Size;
		var desired = position + new Vector2(8, 8);
		if (desired.X + size.X > bounds.End.X) desired.X = position.X - size.X - 8;
		if (desired.Y + size.Y > bounds.End.Y) desired.Y = position.Y - size.Y - 8;
		desired.X = Mathf.Clamp(desired.X, bounds.Position.X, Mathf.Max(bounds.Position.X, bounds.End.X - size.X));
		desired.Y = Mathf.Clamp(desired.Y, bounds.Position.Y, Mathf.Max(bounds.Position.Y, bounds.End.Y - size.Y));
		Popup(new Rect2I((Vector2I)desired, (Vector2I)Size));
	}

	private void SetKindOption(ExpressionKind kind)
	{
		_kindOption.Select((int)kind);
	}
	private void OnKindSelected(long index)
	{
		_kind = (ExpressionKind)index;
		RefreshKind();
		RefreshPreviewIfActive();
	}
	private void RefreshKind()
	{
		if (_graph is null) return;
		_selectedPoint = -1;
		_suppress = true;
		ConfigureGraph();
		_graph.SetCurve(_kind, _drafts[_kind]);
		_stepSegment.Disabled = true;
		_snapPitch.Disabled = _kind is not (ExpressionKind.Pitch or ExpressionKind.Vibrato);
		_pitchRangeRow.Visible = _kind is ExpressionKind.Pitch or ExpressionKind.Vibrato;
		_pitchMin.SetValueNoSignal(_ranges[_kind].Min);
		_pitchMax.SetValueNoSignal(_ranges[_kind].Max);
		_remove.Disabled = !_hasAnyAssignment[_kind];
		string curveStatus = _isMixed[_kind] ? string.Format(Tr("Mixed across {0} notes · Apply shares this curve"), _assignments.Count) : _uniformExisting[_kind] is null ? Tr("No curve assigned · choose a preset or add points") : Tr("Shared curve · edits affect all linked notes");
		_status.Text = _durationVaries && _kind != ExpressionKind.Velocity ? curveStatus + Tr(" · Timing uses the first note's span") : curveStatus;
		_legend.Text = Legend(_kind, _ranges[_kind]);
		GetNode<Label>("%GraphHint").Text = Tr("Left-click to add/drag · right-click a point to remove");
		_stepSegment.SetPressedNoSignal(_selectedPoint >= 0 && _selectedPoint < _drafts[_kind].Count && _drafts[_kind][_selectedPoint].SegmentMode == ExpressionSegmentMode.Step);
		var domain = Domain(_kind);
		_value.MinValue = _kind is ExpressionKind.Pitch or ExpressionKind.Vibrato ? _ranges[_kind].Min : domain.Min;
		_value.MaxValue = _kind is ExpressionKind.Pitch or ExpressionKind.Vibrato ? _ranges[_kind].Max : domain.Max;
		_value.SetValueNoSignal(Math.Clamp(domain.DefaultValue, _value.MinValue, _value.MaxValue));
		_value.Step = _kind is ExpressionKind.Tremolo ? .01 : .05;
		_value.Suffix = _kind switch { ExpressionKind.Pitch or ExpressionKind.Vibrato => " st", ExpressionKind.Velocity => "×", _ => "" };
		SetPresetLabels(_kind);
		_suppress = false;
	}

	private void OnPresetSelected(long index)
	{
		_drafts[_kind] = PresetPoints(_kind, (int)index);
		if (_kind is ExpressionKind.Pitch or ExpressionKind.Vibrato)
		{
			double min = Math.Min(_ranges[_kind].Min, _drafts[_kind].Min(point => point.Y));
			double max = Math.Max(_ranges[_kind].Max, _drafts[_kind].Max(point => point.Y));
			_ranges[_kind] = (Math.Max(-24, min), Math.Min(24, max));
		}
		RefreshKind();
		_presetOption.Select(0);
		RefreshPreviewIfActive();
	}
	private void OnPointsChanged(IReadOnlyList<ExpressionPoint> points)
	{
		_drafts[_kind] = points.ToList();
		RefreshPreviewIfActive();
	}
	private void OnPointSelected(int index, ExpressionPoint point)
	{
		_selectedPoint = index;
		_suppress = true;
		if (index >= 0) _value.SetValueNoSignal(point.Y);
		_stepSegment.Disabled = index < 0 || index >= _drafts[_kind].Count - 1;
		_stepSegment.SetPressedNoSignal(index >= 0 && point.SegmentMode == ExpressionSegmentMode.Step);
		_suppress = false;
	}
	private void OnValueChanged(double value)
	{
		if (_suppress || _selectedPoint < 0) return;
		_graph.SetSelectedValue(value);
	}
	private void TogglePreview()
	{
		if (_previewing) StopPreview();
		else StartPreview();
	}
	private void StartPreview()
	{
		_previewing = true;
		_previewButton.Text = Tr("Stop");
		PreviewRequested?.Invoke(CreateDraftExpression(), IsLoopEnabled);
	}
	private void StopPreview()
	{
		if (!_previewing) return;
		_previewing = false;
		_previewButton.Text = Tr("Preview");
		PreviewStopped?.Invoke();
	}
	private void RefreshPreviewIfActive()
	{
		if (_previewing) PreviewRequested?.Invoke(CreateDraftExpression(), IsLoopEnabled);
	}
	private NoteExpression CreateDraftExpression()
	{
		var existing = _uniformExisting[_kind];
		return new NoteExpression
		{
			Id = existing?.Id ?? Guid.NewGuid(),
			Kind = _kind,
			Position = existing?.Position ?? 0,
			Points = _drafts[_kind].OrderBy(point => point.X).Select(point => new ExpressionPoint(point.X, point.Y, point.SegmentMode)).ToList(),
			MinSemitones = _ranges[_kind].Min,
			MaxSemitones = _ranges[_kind].Max
		};
	}
	private void OnLoopToggled(bool enabled)
	{
		LoopChanged?.Invoke(enabled);
	}
	private void Apply()
	{
		var result = CreateDraftExpression();
		try { result.Validate(); }
		catch (Exception e) when (e is ArgumentException or ArgumentOutOfRangeException) { _status.Text = Tr(e.Message); return; }
		Applied?.Invoke(result);
		_uniformExisting[_kind] = result;
		_hasAnyAssignment[_kind] = true;
		_isMixed[_kind] = false;
		Hide();
	}
	private void Cancel() { Cancelled?.Invoke(); Hide(); }
	private void OnVisibilityChanged()
	{
		if (Visible) _loopButton.SetPressedNoSignal(true);
		else { StopPreview(); Closed?.Invoke(); }
	}
	private void OnPitchRangeChanged()
	{
		if (_suppress || !(_kind is ExpressionKind.Pitch or ExpressionKind.Vibrato)) return;
		double min = _pitchMin.Value, max = _pitchMax.Value;
		if (min >= max || _drafts[_kind].Any(point => point.Y < min || point.Y > max))
		{
			_status.Text = Tr(min >= max ? "Minimum must be below maximum." : "Range cannot exclude existing curve points.");
			_suppress = true;
			_pitchMin.SetValueNoSignal(_ranges[_kind].Min);
			_pitchMax.SetValueNoSignal(_ranges[_kind].Max);
			_suppress = false;
			return;
		}
		_ranges[_kind] = (min, max);
		_value.MinValue = min;
		_value.MaxValue = max;
		_legend.Text = Legend(_kind, _ranges[_kind]);
		ConfigureGraph();
		RefreshPreviewIfActive();
	}
	private void ConfigureGraph()
	{
		if (_graph is null || _division is null || _ranges.Count == 0) return;
		var domain = Domain(_kind);
		var range = _kind is ExpressionKind.Pitch or ExpressionKind.Vibrato ? _ranges[_kind] : (domain.Min, domain.Max);
		double duration = _kind == ExpressionKind.Velocity ? _velocitySpan : _pitchDuration;
		int selectedDivision = Math.Clamp(_division.Selected, 0, _divisionDenominators.Length - 1);
		_graph.Configure(_kind, range.Item1, range.Item2, duration, _divisionDenominators[selectedDivision], _snap.ButtonPressed, _snapPitch.ButtonPressed);
	}
	private static bool SamePoints(IReadOnlyList<ExpressionPoint> a, IReadOnlyList<ExpressionPoint> b) => a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);

	private static (double Min, double Max, double DefaultValue) Domain(ExpressionKind kind) => kind switch
	{
		ExpressionKind.Pitch => (-2, 2, 0),
		ExpressionKind.Vibrato => (-2, 2, 0),
		ExpressionKind.Tremolo => (0, 1, 1),
		_ => (0, 2, 1)
	};
	private string Legend(ExpressionKind kind, (double Min, double Max) range) => kind switch
	{
		ExpressionKind.Pitch => string.Format(Tr("Pitch · semitones ({0} to {1}) · X = note progress"), range.Min.ToString("0.##"), range.Max.ToString("0.##")),
		ExpressionKind.Vibrato => string.Format(Tr("Vibrato · semitone offset ({0} to {1}) · X = note progress"), range.Min.ToString("0.##"), range.Max.ToString("0.##")),
		ExpressionKind.Tremolo => Tr("Tremolo · amplitude (0 to 1) · X = note progress"),
		_ => Tr("Velocity · multiplier (0 to 2) · X = selected attack span")
	};
	private void SetPresetLabels(ExpressionKind kind)
	{
		string[] labels = kind switch
		{
			ExpressionKind.Pitch => new[] { "Choose…", "Bend", "Release", "Slide" },
			ExpressionKind.Vibrato => new[] { "Choose…", "Vibrato", "Subtle vibrato", "Reset" },
			ExpressionKind.Tremolo => new[] { "Choose…", "Tremolo", "Deep tremolo", "Reset" },
			_ => new[] { "Choose…", "Velocity rise", "Velocity fall", "Reset" }
		};
		for (var i = 0; i < labels.Length; i++) _presetOption.SetItemText(i, Tr(labels[i]));
	}

	public override void _Notification(int what)
	{
		if (what != NotificationTranslationChanged || !IsInsideTree()) return;
		RefreshLocalizedLabels();
		if (_drafts.Count > 0) RefreshKind();
	}

	private void RefreshLocalizedLabels()
	{
		if (_previewButton is not null) _previewButton.Text = Tr(_previewing ? "Stop" : "Preview");
		if (_presetOption is not null) SetPresetLabels(_kind);
	}
	private static List<ExpressionPoint> PresetPoints(ExpressionKind kind, int preset)
	{
		ExpressionPoint P(double x, double y) => new(x, y);
		if (kind == ExpressionKind.Pitch) return preset switch
		{
			1 => new() { P(0, 0), P(.2, 0), P(.8, 1), P(1, 1) }, // bend
			2 => new() { P(0, 0), P(.75, 0), P(1, -2) }, // release
			3 => new() { P(0, -2), P(.15, 0), P(1, 0) }, // slide
			_ => new() { P(0, 0), P(1, 0) }
		};
		if (kind == ExpressionKind.Vibrato) return preset == 1
			? new() { P(0, 0), P(.1, .5), P(.2, -.5), P(.3, .5), P(.4, -.5), P(.5, .5), P(.6, -.5), P(.7, .5), P(.8, -.5), P(.9, .5), P(1, 0) }
			: new() { P(0, 0), P(1, 0) };
		if (kind == ExpressionKind.Tremolo) return preset == 1
			? new() { P(0, 1), P(.1, .35), P(.2, 1), P(.3, .35), P(.4, 1), P(.5, .35), P(.6, 1), P(.7, .35), P(.8, 1), P(.9, .35), P(1, 1) }
			: new() { P(0, 1), P(1, 1) };
		return preset == 1 ? new() { P(0, .4), P(.5, 1.4), P(1, 1.4) }
			: preset == 2 ? new() { P(0, 1.4), P(.5, 1.4), P(1, .4) }
			: new() { P(0, 1), P(1, 1) };
	}
}


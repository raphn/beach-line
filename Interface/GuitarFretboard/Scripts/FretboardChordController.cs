#nullable enable
using Godot;
using OpenTab.Music;
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Builds a track-specific fretboard and manages chord composition/placement state.</summary>
public partial class FretboardChordController : Window
{
	private static readonly string[] PitchClasses = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
	private static readonly HashSet<int> MarkedFrets = [3, 5, 7, 9, 12, 15, 17, 19, 21, 24];
	private readonly List<(int StringNumber, int Fret)> _selectedNotes = new();
	private readonly IReadOnlyList<(int StringNumber, int Fret)> _selectedNotesView;
	private readonly Dictionary<(int StringNumber, int Fret), Button> _fretButtons = new();
	private readonly Dictionary<int, (int StringNumber, int Fret)> _selectionByString = new();
	private readonly PackedScene _stringScene = GD.Load<PackedScene>("res://Interface/GuitarFretboard/Scenes/string.tscn");
	private readonly PackedScene _extraFretButtonScene = GD.Load<PackedScene>("res://Interface/GuitarFretboard/Scenes/extra_fret_button.tscn");
	private VBoxContainer _strings = null!;
	private ScrollContainer _fretScroll = null!;
	private VBoxContainer _layout = null!;
	private PanelContainer _panel = null!;
	private BoxContainer _options = null!;
	private Button _createToggle = null!;
	private Button _insertToggle = null!;
	private Button _resetButton = null!;
	private ScoreTrack? _track;
	private int[] _configuredTuning = Array.Empty<int>();
	private int _configuredMaxFret;
	private bool _suppressToggleEvents;
	private bool _isArmed;
	private bool _fitQueued;

	public FretboardChordController() => _selectedNotesView = _selectedNotes.AsReadOnly();

	public event Action? PlacementStateChanged;
	public bool IsArmed => _isArmed;
	public IReadOnlyList<(int StringNumber, int Fret)> SelectedNotes => _selectedNotesView;

	public override void _Ready()
	{
		_strings = GetNode<VBoxContainer>("%Strings");
		_fretScroll = GetNode<ScrollContainer>("%FretScroll");
		_layout = GetNode<VBoxContainer>("Panel/Layout");
		_panel = GetNode<PanelContainer>("Panel");
		_options = GetNode<BoxContainer>("Panel/Layout/ChordInsertingOptions");
		_createToggle = GetNode<Button>("Panel/Layout/ChordInsertingOptions/CreateModeToggle");
		_insertToggle = GetNode<Button>("Panel/Layout/ChordInsertingOptions/InsertChordToggle");
		_resetButton = GetNode<Button>("Panel/Layout/ChordInsertingOptions/ResetButton");
		_createToggle.Toggled += OnCreateToggled;
		_insertToggle.Toggled += OnInsertToggled;
		_resetButton.Pressed += ResetComposition;
		BuildBoard();
		UpdateOptions();
		QueueWindowFit();
	}

	public override void _UnhandledKeyInput(InputEvent @event)
	{
		if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && _isArmed)
		{
			CancelPlacement();
			GetViewport().SetInputAsHandled();
		}
	}

	/// <summary>Configures the fretboard from the active guitar/bass track.</summary>
	public void Configure(ScoreTrack? track)
	{
		var tuning = track?.TabTuningMidiPitches.ToArray() ?? Array.Empty<int>();
		var maxFret = track?.MaxFret ?? 0;
		var changed = !ReferenceEquals(track, _track) || maxFret != _configuredMaxFret || !_configuredTuning.SequenceEqual(tuning);
		_track = track;
		_configuredTuning = tuning;
		_configuredMaxFret = maxFret;
		if (changed) ClearComposition();
		BuildBoard();
		UpdateOptions();
		if (changed) QueueWindowFit();
		if (changed) PlacementStateChanged?.Invoke();
	}

	private void QueueWindowFit()
	{
		if (_fitQueued || !IsNodeReady()) return;
		_fitQueued = true;
		CallDeferred(MethodName.FitWindowToContents);
	}

	private void FitWindowToContents()
	{
		_fitQueued = false;
		if (!GodotObject.IsInstanceValid(this)) return;

		// Container minimum sizes update after children are added or removed. This
		// deferred pass measures the generated rows, their theme spacing and panel margins.
		var fretSize = _strings.GetCombinedMinimumSize();
		var optionsSize = _options.GetCombinedMinimumSize();
		var separation = _layout.GetThemeConstant("separation");
		var panelStyle = _panel.GetThemeStylebox("panel");
		var panelMargins = panelStyle?.GetMinimumSize() ?? Vector2.Zero;
		var desired = new Vector2(
			Mathf.Max(fretSize.X, optionsSize.X) + panelMargins.X,
			fretSize.Y + optionsSize.Y + (fretSize.Y > 0 ? separation : 0) + panelMargins.Y);

		var parentViewport = GetParent().GetViewport();
		var embedded = parentViewport.GuiEmbedSubwindows;
		Rect2I available;
		if (embedded)
		{
			var viewportRect = parentViewport.GetVisibleRect();
			var viewportSize = new Vector2I(Mathf.CeilToInt(viewportRect.Size.X), Mathf.CeilToInt(viewportRect.Size.Y));
			available = new Rect2I(Vector2I.Zero, new Vector2I(Mathf.Max(1, viewportSize.X), Mathf.Max(1, viewportSize.Y)));
		}
		else
		{
			available = DisplayServer.ScreenGetUsableRect(CurrentScreen);
			if (available.Size.X <= 0 || available.Size.Y <= 0)
				available = DisplayServer.ScreenGetUsableRect((int)DisplayServer.ScreenOfMainWindow);
		}
		if (available.Size.X <= 0 || available.Size.Y <= 0) return;

		var availableSize = new Vector2I(Mathf.Max(1, available.Size.X), Mathf.Max(1, available.Size.Y));
		// The scene's minimum is lowered when the screen is smaller, so it cannot
		// prevent the window from respecting the display/embedding bounds.
		MinSize = new Vector2I(Mathf.Min(MinSize.X, availableSize.X), Mathf.Min(MinSize.Y, availableSize.Y));
		var minimum = MinSize;
		var target = new Vector2I(
			Mathf.Clamp(Mathf.CeilToInt(desired.X), minimum.X, availableSize.X),
			Mathf.Clamp(Mathf.CeilToInt(desired.Y), minimum.Y, availableSize.Y));

		// Reserve the actual themed scrollbar thickness when clipping fret rows.
		// If one bar appears, it can force the other, so resolve both once more.
		var horizontalBarHeight = _fretScroll.GetHScrollBar().GetCombinedMinimumSize().Y;
		var verticalBarWidth = _fretScroll.GetVScrollBar().GetCombinedMinimumSize().X;
		var fretViewportWidth = Mathf.Max(0, target.X - panelMargins.X);
		var fretViewportHeight = Mathf.Max(0, target.Y - optionsSize.Y - (fretSize.Y > 0 ? separation : 0) - panelMargins.Y);
		var horizontalNeeded = fretSize.X > fretViewportWidth;
		var verticalNeeded = fretSize.Y > fretViewportHeight;
		if (verticalNeeded) horizontalNeeded |= fretSize.X > Mathf.Max(0, fretViewportWidth - verticalBarWidth);
		if (horizontalNeeded && target.Y < availableSize.Y)
			target.Y = Mathf.Min(availableSize.Y, Mathf.Max(target.Y, Mathf.CeilToInt(target.Y + horizontalBarHeight)));
		if (horizontalNeeded) verticalNeeded |= fretSize.Y > Mathf.Max(0, fretViewportHeight - horizontalBarHeight);
		if (verticalNeeded && target.X < availableSize.X)
			target.X = Mathf.Min(availableSize.X, Mathf.Max(target.X, Mathf.CeilToInt(target.X + verticalBarWidth)));
		var oldPosition = Position;
		Size = target;
		var maxPosition = available.Position + availableSize - target;
		Position = new Vector2I(
			Mathf.Clamp(oldPosition.X, available.Position.X, maxPosition.X),
			Mathf.Clamp(oldPosition.Y, available.Position.Y, maxPosition.Y));
	}

	/// <summary>Disarms placement while retaining Create mode and the composed notes.</summary>
	public void CancelPlacement()
	{
		if (!_isArmed && (!IsNodeReady() || !_insertToggle.ButtonPressed)) return;
		_isArmed = false;
		if (IsNodeReady())
		{
			_suppressToggleEvents = true;
			_insertToggle.ButtonPressed = false;
			_suppressToggleEvents = false;
		}
		UpdateOptions();
		PlacementStateChanged?.Invoke();
	}

	/// <summary>Clears Create mode, placement mode, and all selected fret positions.</summary>
	public void ResetComposition()
	{
		var hadState = _selectedNotes.Count > 0 || _isArmed || (IsNodeReady() && (_createToggle.ButtonPressed || _insertToggle.ButtonPressed));
		ClearComposition();
		UpdateOptions();
		if (hadState) PlacementStateChanged?.Invoke();
	}

	private void ClearComposition()
	{
		_isArmed = false;
		_selectedNotes.Clear();
		_selectionByString.Clear();
		_suppressToggleEvents = true;
		if (IsNodeReady())
		{
			_createToggle.ButtonPressed = false;
			_insertToggle.ButtonPressed = false;
			foreach (var button in _fretButtons.Values) button.ButtonPressed = false;
		}
		_suppressToggleEvents = false;
	}

	private void BuildBoard()
	{
		if (!IsNodeReady()) return;
		foreach (var child in _strings.GetChildren())
		{
			_strings.RemoveChild(child);
			child.QueueFree();
		}
		_fretButtons.Clear();
		if (_track is null || _configuredTuning.Length == 0) return;

		for (var stringIndex = 0; stringIndex < _configuredTuning.Length; stringIndex++)
		{
			var stringNumber = stringIndex + 1;
			var openPitch = _configuredTuning[stringIndex];
			var row = (HBoxContainer)_stringScene.Instantiate();
			row.Name = $"String{stringNumber}";
			_strings.AddChild(row);
			var label = row.GetNode<Label>("StringLabel");
			label.Text = $"{stringNumber}. {MidiName(openPitch)}";
			foreach (var child in row.GetChildren().ToArray())
			{
				if (child is not Button button || !int.TryParse(button.Name.ToString(), out var fret)) continue;
				var pitch = openPitch + fret;
				if (fret > _configuredMaxFret || pitch is < 0 or > 127)
				{
					row.RemoveChild(button);
					button.QueueFree();
					continue;
				}
				button.Text = MidiName(pitch);
				button.TooltipText = string.Format(TranslationServer.Translate("String {0}, fret {1}: {2}"), stringNumber, fret, MidiName(pitch));
				button.ThemeTypeVariation = fret == 0 ? "HandFret" : (MarkedFrets.Contains(fret) ? "MarkFret" : "Fret");
				button.ButtonPressed = _selectionByString.TryGetValue(stringNumber, out var selected) && selected.Fret == fret;
				button.Disabled = !_createToggle.ButtonPressed || _isArmed;
				button.CustomMinimumSize = new Vector2(38, 24);
				button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
				var key = (stringNumber, fret);
				button.Toggled += pressed => OnFretToggled(key, pressed);
				_fretButtons[key] = button;
			}
			var existingFrets = _fretButtons.Keys.Where(key => key.StringNumber == stringNumber).Select(key => key.Fret).ToHashSet();
			for (var fret = 0; fret <= _configuredMaxFret; fret++)
			{
				var pitch = openPitch + fret;
				if (pitch is < 0 or > 127 || existingFrets.Contains(fret)) continue;
				var button = (Button)_extraFretButtonScene.Instantiate();
				button.Name = $"Fret{fret}";
				button.Text = MidiName(pitch);
				button.TooltipText = string.Format(TranslationServer.Translate("String {0}, fret {1}: {2}"), stringNumber, fret, MidiName(pitch));
				button.ThemeTypeVariation = fret == 0 ? "HandFret" : (MarkedFrets.Contains(fret) ? "MarkFret" : "Fret");
				button.ButtonPressed = _selectionByString.TryGetValue(stringNumber, out var selected) && selected.Fret == fret;
				button.Disabled = !_createToggle.ButtonPressed || _isArmed;
				var key = (stringNumber, fret);
				button.Toggled += pressed => OnFretToggled(key, pressed);
				row.AddChild(button);
				_fretButtons[key] = button;
			}
		}
		QueueWindowFit();
	}

	private void OnFretToggled((int StringNumber, int Fret) key, bool pressed)
	{
		if (_suppressToggleEvents) return;
		var button = _fretButtons[key];
		if (!_createToggle.ButtonPressed || _isArmed)
		{
			button.SetPressedNoSignal(false);
			return;
		}
		if (_selectionByString.TryGetValue(key.StringNumber, out var previous))
		{
			if (previous == key && !pressed)
			{
				_selectionByString.Remove(key.StringNumber);
				_selectedNotes.Remove(key);
			}
			else if (previous != key && pressed)
			{
				_fretButtons[previous].SetPressedNoSignal(false);
				_selectedNotes.Remove(previous);
				_selectionByString[key.StringNumber] = key;
				_selectedNotes.Add(key);
			}
			else if (previous == key && pressed) button.SetPressedNoSignal(true);
		}
		else if (pressed)
		{
			_selectionByString[key.StringNumber] = key;
			_selectedNotes.Add(key);
		}
		UpdateOptions();
		PlacementStateChanged?.Invoke();
	}

	private void OnCreateToggled(bool pressed)
	{
		if (_suppressToggleEvents) return;
		if (!pressed) ClearNotesAndDisarm();
		UpdateOptions();
		PlacementStateChanged?.Invoke();
	}

	private void OnInsertToggled(bool pressed)
	{
		if (_suppressToggleEvents) return;
		if (pressed && (!_createToggle.ButtonPressed || _selectedNotes.Count < 2))
		{
			_suppressToggleEvents = true;
			_insertToggle.ButtonPressed = false;
			_suppressToggleEvents = false;
			return;
		}
		_isArmed = pressed;
		UpdateOptions();
		PlacementStateChanged?.Invoke();
	}

	private void UpdateOptions()
	{
		if (!IsNodeReady()) return;
		_insertToggle.Disabled = !_createToggle.ButtonPressed || _selectedNotes.Count < 2;
		foreach (var button in _fretButtons.Values) button.Disabled = !_createToggle.ButtonPressed || _isArmed;
	}

	private void ClearNotesAndDisarm()
	{
		_isArmed = false;
		_selectedNotes.Clear();
		_selectionByString.Clear();
		_suppressToggleEvents = true;
		_insertToggle.ButtonPressed = false;
		foreach (var button in _fretButtons.Values) button.ButtonPressed = false;
		_suppressToggleEvents = false;
	}

	private static string MidiName(int pitch) => $"{PitchClasses[((pitch % 12) + 12) % 12]}{pitch / 12 - 1}";
}

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
	private readonly HashSet<(int StringNumber, int Fret)> _tabHighlights = new();
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
	private Button _writeToggle = null!;
	private Button _autoMoveToggle = null!;
	private ScoreTrack? _track;
	private int[] _configuredTuning = Array.Empty<int>();
	private int _configuredMaxFret;
	private bool _suppressToggleEvents;
	private bool _isArmed;
	private bool _fitQueued;

	public FretboardChordController() => _selectedNotesView = _selectedNotes.AsReadOnly();

	public event Action? PlacementStateChanged;
	public event Action<int, int>? DirectNoteRequested;
	public bool IsWriteEnabled => IsNodeReady() && _writeToggle.ButtonPressed && _track is not null;
	public bool AutoMoveEnabled => IsNodeReady() && _autoMoveToggle.ButtonPressed;
	public bool IsArmed => _isArmed;
	public IReadOnlyList<(int StringNumber, int Fret)> SelectedNotes => _selectedNotesView;

	/// <summary>Sets presentation-only TAB highlights without changing fret selection or pressed state.</summary>
	public void SetTabHighlights(IEnumerable<(int StringNumber, int Fret)> notes)
	{
		_tabHighlights.Clear();
		if (notes is not null) _tabHighlights.UnionWith(notes);
		ApplyTabHighlights();
	}

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
		_writeToggle = GetNode<Button>("Panel/Layout/ChordInsertingOptions/WriteToggle");
		_autoMoveToggle = GetNode<Button>("Panel/Layout/ChordInsertingOptions/AutoMoveToggle");
		_writeToggle.Toggled += OnWriteToggled;
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

	/// <summary>Clears the inserted notes and returns to Create mode.</summary>
	public void CompletePlacement()
	{
		ClearNotesAndDisarm();
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
			_writeToggle.SetPressedNoSignal(false);
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
				button.TooltipText = FretTooltip(stringNumber, fret, pitch, _tabHighlights.Contains((stringNumber, fret)));
				button.ThemeTypeVariation = fret == 0 ? "HandFret" : (MarkedFrets.Contains(fret) ? "MarkFret" : "Fret");
				button.ButtonPressed = _selectionByString.TryGetValue(stringNumber, out var selected) && selected.Fret == fret;
				button.Disabled = (!_createToggle.ButtonPressed && !_writeToggle.ButtonPressed) || _isArmed;
				button.CustomMinimumSize = new Vector2(38, 24);
				button.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
				var key = (stringNumber, fret);
				button.Toggled += pressed => OnFretToggled(key, pressed);
				_fretButtons[key] = button;
				ApplyTabHighlight(button, key);
			}
			var existingFrets = _fretButtons.Keys.Where(key => key.StringNumber == stringNumber).Select(key => key.Fret).ToHashSet();
			for (var fret = 0; fret <= _configuredMaxFret; fret++)
			{
				var pitch = openPitch + fret;
				if (pitch is < 0 or > 127 || existingFrets.Contains(fret)) continue;
				var button = (Button)_extraFretButtonScene.Instantiate();
				button.Name = $"Fret{fret}";
				button.Text = MidiName(pitch);
				button.TooltipText = FretTooltip(stringNumber, fret, pitch, _tabHighlights.Contains((stringNumber, fret)));
				button.ThemeTypeVariation = fret == 0 ? "HandFret" : (MarkedFrets.Contains(fret) ? "MarkFret" : "Fret");
				button.ButtonPressed = _selectionByString.TryGetValue(stringNumber, out var selected) && selected.Fret == fret;
				button.Disabled = (!_createToggle.ButtonPressed && !_writeToggle.ButtonPressed) || _isArmed;
				var key = (stringNumber, fret);
				button.Toggled += pressed => OnFretToggled(key, pressed);
				row.AddChild(button);
				_fretButtons[key] = button;
				ApplyTabHighlight(button, key);
			}
		}
		QueueWindowFit();
	}

	private void ApplyTabHighlights()
	{
		if (!IsNodeReady()) return;
		foreach (var pair in _fretButtons)
		{
			ApplyTabHighlight(pair.Value, pair.Key);
			var fret = pair.Key.Fret;
			var pitch = _configuredTuning[pair.Key.StringNumber - 1] + fret;
			pair.Value.TooltipText = FretTooltip(pair.Key.StringNumber, fret, pitch, _tabHighlights.Contains(pair.Key));
		}
	}

	private string FretTooltip(int stringNumber, int fret, int pitch, bool highlighted = false)
	{
		var tooltip = string.Format(TranslationServer.Translate("String {0}, fret {1}: {2}"), stringNumber, fret, MidiName(pitch));
		return highlighted ? $"{tooltip}\n{TranslationServer.Translate("TAB highlight: presentation only")}" : tooltip;
	}

	private void ApplyTabHighlight(Button button, (int StringNumber, int Fret) key)
	{
		var highlighted = _tabHighlights.Contains(key);
		if (highlighted)
		{
			button.AddThemeColorOverride("font_outline_color", new Color(0.337255f, 0.894118f, 0.921569f, 1f));
			button.AddThemeConstantOverride("outline_size", 2);
		}
		else
		{
			button.RemoveThemeColorOverride("font_outline_color");
			button.RemoveThemeConstantOverride("outline_size");
		}
		foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
		{
			if (!highlighted)
			{
				button.RemoveThemeStyleboxOverride(state);
				continue;
			}
			if (button.GetThemeStylebox(state) is not StyleBoxFlat source) continue;
			var style = (StyleBoxFlat)source.Duplicate();
			style.BorderWidthLeft = 3;
			style.BorderWidthTop = 3;
			style.BorderWidthRight = 3;
			style.BorderWidthBottom = 3;
			style.BorderColor = new Color(0.337255f, 0.894118f, 0.921569f, 1f);
			button.AddThemeStyleboxOverride(state, style);
		}
	}

	private void OnFretToggled((int StringNumber, int Fret) key, bool pressed)
	{
		if (_suppressToggleEvents) return;
		var button = _fretButtons[key];
		if (IsWriteEnabled)
		{
			button.SetPressedNoSignal(_selectionByString.TryGetValue(key.StringNumber, out var selected) && selected == key);
			DirectNoteRequested?.Invoke(key.StringNumber, key.Fret);
			return;
		}
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
		if (pressed) _writeToggle.SetPressedNoSignal(false);
		else ClearNotesAndDisarm();
		UpdateOptions();
		PlacementStateChanged?.Invoke();
	}

	private void OnWriteToggled(bool pressed)
	{
		if (pressed)
		{
			CancelPlacement();
			_createToggle.SetPressedNoSignal(false);
		}
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
		_writeToggle.Disabled = _track is null;
		_insertToggle.Disabled = !_createToggle.ButtonPressed || _selectedNotes.Count < 2;
		foreach (var button in _fretButtons.Values) button.Disabled = (!_createToggle.ButtonPressed && !_writeToggle.ButtonPressed) || _isArmed;
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

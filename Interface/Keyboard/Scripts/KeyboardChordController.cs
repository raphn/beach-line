#nullable enable
using Godot;
using OpenTab.Music;
using System;
using System.Collections.Generic;

/// <summary>Composes absolute MIDI pitches for placement in a Full Tab track.</summary>
public partial class KeyboardChordController : Window
{
    private static readonly string[] PitchNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
    private static readonly string[] KeyPaths = ["DoReMi/Whites/Key", "DoReMi/Blacks/Key2", "DoReMi/Whites/Key3", "DoReMi/Blacks/Key4", "DoReMi/Whites/Key5", "FaSolLaSi/Whites/Key6", "FaSolLaSi/Blacks/Key7", "FaSolLaSi/Whites/Key8", "FaSolLaSi/Blacks/Key9", "FaSolLaSi/Whites/Key10", "FaSolLaSi/Blacks/Key11", "FaSolLaSi/Whites/Key12"];
    private readonly PackedScene _octaveScene = GD.Load<PackedScene>("res://Interface/Keyboard/Scenes/octave.tscn");
    private readonly List<int> _selected = new();
    private readonly IReadOnlyList<int> _selectedView;
    private readonly Dictionary<int, Button> _keys = new();
    private readonly HashSet<int> _tabHighlights = new();
    private HBoxContainer _octaves = null!;
    private SpinBox _startControl = null!, _countControl = null!;
    private Button _create = null!, _insert = null!, _write = null!, _autoMove = null!;
    private Label _selectedCount = null!;
    private bool _keysBuilt, _updatingRange;
    private ScoreTrack? _track;
    private bool _armed, _suppress;
    private int _startOctave = 2, _octaveCount = 6;

    public KeyboardChordController() => _selectedView = _selected.AsReadOnly();
    public event Action? PlacementStateChanged;
    public event Action<int>? DirectNoteRequested;
    public bool IsWriteEnabled => IsNodeReady() && _write.ButtonPressed && _track is not null;
    public bool AutoMoveEnabled => IsNodeReady() && _autoMove.ButtonPressed;
    public bool IsArmed => _armed;
    public IReadOnlyList<int> SelectedPitches => _selectedView;
    public int OctaveCount => _octaveCount;
    public int StartOctave => _startOctave;

    /// <summary>Sets presentation-only TAB highlights without changing key selection or pressed state.</summary>
    public void SetTabHighlights(IEnumerable<int> pitches)
    {
        _tabHighlights.Clear();
        if (pitches is not null) _tabHighlights.UnionWith(pitches);
        ApplyTabHighlights();
    }

    public override void _Ready()
    {
        _octaves = GetNode<HBoxContainer>("Keyboard/KeysAndChords/KeyScroll/Octaves");
        _startControl = GetNode<SpinBox>("Keyboard/KeysAndChords/Range/StartOctave");
        _countControl = GetNode<SpinBox>("Keyboard/KeysAndChords/Range/OctaveCount");
        _selectedCount = GetNode<Label>("Keyboard/KeysAndChords/Range/SelectedCount");
        _create = GetNode<Button>("Keyboard/KeysAndChords/ChordInsertingOptions/CreateModeToggle");
        _insert = GetNode<Button>("Keyboard/KeysAndChords/ChordInsertingOptions/InsertChordToggle");
        _write = GetNode<Button>("Keyboard/KeysAndChords/ChordInsertingOptions/WriteToggle");
        _autoMove = GetNode<Button>("Keyboard/KeysAndChords/ChordInsertingOptions/AutoMoveToggle");
        _write.Toggled += OnWriteToggled;
        GetNode<Button>("Keyboard/KeysAndChords/ChordInsertingOptions/ResetButton").Pressed += ResetComposition;
        _startControl.ValueChanged += value => { if (!_updatingRange) SetRange((int)value, _octaveCount); };
        _countControl.ValueChanged += value => { if (!_updatingRange) SetRange(_startOctave, (int)value); };
        _create.Toggled += OnCreateToggled;
        _insert.Toggled += OnInsertToggled;
        SetRange(_startOctave, _octaveCount);
    }

    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape } && _armed)
        {
            CancelPlacement();
            GetViewport().SetInputAsHandled();
        }
    }

    public void Configure(ScoreTrack? track)
    {
        if (track?.UsesFullTab != true) track = null;
        bool changed = !ReferenceEquals(track, _track);
        _track = track;
        if (changed) ResetComposition();
        UpdateOptions();
    }

    public void SetRange(int startOctave, int octaveCount)
    {
        int count = Math.Clamp(octaveCount, 1, 8);
        int start = Math.Clamp(startOctave, -1, 10 - count);
        bool changed = start != _startOctave || count != _octaveCount;
        _startOctave = start;
        _octaveCount = count;
        if (!IsNodeReady()) return;
        _updatingRange = true;
        _startControl.MaxValue = 10 - count;
        _startControl.SetValueNoSignal(start);
        _countControl.SetValueNoSignal(count);
        _updatingRange = false;
        if (changed || !_keysBuilt) BuildKeys();
    }

    public void ResetComposition()
    {
        bool changed = _selected.Count > 0 || _armed || (IsNodeReady() && _create.ButtonPressed);
        ClearNotes();
        if (IsNodeReady()) { _create.SetPressedNoSignal(false); _write.SetPressedNoSignal(false); }
        UpdateOptions();
        if (changed) PlacementStateChanged?.Invoke();
    }

    public void CancelPlacement()
    {
        if (!_armed) return;
        _armed = false;
        if (IsNodeReady()) _insert.SetPressedNoSignal(false);
        UpdateOptions();
        PlacementStateChanged?.Invoke();
    }

    public void CompletePlacement()
    {
        ClearNotes();
        UpdateOptions();
        PlacementStateChanged?.Invoke();
    }

    private void BuildKeys()
    {
        foreach (Node child in _octaves.GetChildren()) { _octaves.RemoveChild(child); child.QueueFree(); }
        _keys.Clear();
        _keysBuilt = true;
        for (int octave = 0; octave < _octaveCount; octave++)
        {
            var row = _octaveScene.Instantiate<Control>();
            _octaves.AddChild(row);
            for (int offset = 0; offset < 12; offset++)
            {
                int pitch = (_startOctave + octave + 1) * 12 + offset;
                var button = row.GetNode<Button>(KeyPaths[offset]);
                if (pitch is < 0 or > 127) { button.Disabled = true; continue; }
                button.TooltipText = KeyTooltip(offset, octave, pitch);
                button.SetPressedNoSignal(_selected.Contains(pitch));
                button.Toggled += pressed => OnKeyToggled(pitch, pressed);
                _keys.Add(pitch, button);
                ApplyTabHighlight(button, pitch);
            }
        }
        UpdateOptions();
    }

    private void ApplyTabHighlights()
    {
        if (!IsNodeReady()) return;
        foreach (var pair in _keys)
        {
            ApplyTabHighlight(pair.Value, pair.Key);
            var offset = ((pair.Key % 12) + 12) % 12;
            var octave = pair.Key / 12 - 1 - _startOctave;
            pair.Value.TooltipText = KeyTooltip(offset, octave, pair.Key);
        }
    }

    private string KeyTooltip(int offset, int octaveOffset, int pitch)
    {
        var tooltip = $"{PitchNames[offset]}{_startOctave + octaveOffset} (MIDI {pitch})";
        return _tabHighlights.Contains(pitch) ? $"{tooltip}\n{TranslationServer.Translate("TAB highlight: presentation only")}" : tooltip;
    }

    private void ApplyTabHighlight(Button button, int pitch)
    {
        var highlighted = _tabHighlights.Contains(pitch);
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

    private void OnKeyToggled(int pitch, bool pressed)
    {
        if (_suppress) return;
        if (IsWriteEnabled)
        {
            _keys[pitch].SetPressedNoSignal(_selected.Contains(pitch));
            DirectNoteRequested?.Invoke(pitch);
            return;
        }
        if (_track is null || !_create.ButtonPressed || _armed)
        {
            _keys[pitch].SetPressedNoSignal(_selected.Contains(pitch));
            return;
        }
        if (pressed) { if (!_selected.Contains(pitch)) _selected.Add(pitch); _selected.Sort(); }
        else _selected.Remove(pitch);
        UpdateOptions();
        PlacementStateChanged?.Invoke();
    }

    private void OnCreateToggled(bool pressed)
    {
        if (_suppress) return;
        if (pressed) _write.SetPressedNoSignal(false);
        else ClearNotes();
        UpdateOptions();
        PlacementStateChanged?.Invoke();
    }

    private void OnWriteToggled(bool pressed)
    {
        if (pressed)
        {
            CancelPlacement();
            _create.SetPressedNoSignal(false);
        }
        UpdateOptions();
        PlacementStateChanged?.Invoke();
    }

    private void OnInsertToggled(bool pressed)
    {
        if (_suppress) return;
        if (pressed && (_track is null || !_create.ButtonPressed || _selected.Count < 2))
        {
            _insert.SetPressedNoSignal(false);
            return;
        }
        _armed = pressed;
        UpdateOptions();
        PlacementStateChanged?.Invoke();
    }

    private void ClearNotes()
    {
        _selected.Clear();
        _armed = false;
        if (!IsNodeReady()) return;
        _suppress = true;
        _insert.SetPressedNoSignal(false);
        foreach (var button in _keys.Values) button.SetPressedNoSignal(false);
        _suppress = false;
    }

    private void UpdateOptions()
    {
        if (!IsNodeReady()) return;
        _selectedCount.Text = string.Format(TranslationServer.Translate("Selected: {0}"), _selected.Count);
        _create.Disabled = _track is null;
        _write.Disabled = _track is null;
        _insert.Disabled = _track is null || !_create.ButtonPressed || _selected.Count < 2;
        foreach (var button in _keys.Values) button.Disabled = _track is null || (!_create.ButtonPressed && !_write.ButtonPressed) || _armed;
    }
}




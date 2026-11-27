#nullable enable
using Godot;
using System;
using System.Globalization;

/// <summary>A compact editable note-name control with bounded semitone stepping.</summary>
public partial class NotePitchSelector : HBoxContainer
{
	private LineEdit? _edit;
	private Button? _up;
	private Button? _down;
	private int _midiPitch = 60;
	private bool _isValid = true;
	private bool _updatingText;

	/// <summary>MIDI pitch, where C-1 is 0 and G9 is 127. Invalid text leaves the last value intact but IsValid is false.</summary>
	public int MidiPitch
	{
		get => _midiPitch;
		set
		{
			if (value is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(value), "MIDI pitch must be between 0 and 127.");
			bool changed = _midiPitch != value || !_isValid;
			_midiPitch = value;
			_isValid = true;
			if (_edit is not null) SetEditText(FormatPitch(value));
			UpdateValidationStyle();
			if (changed) PitchChanged?.Invoke();
		}
	}
	public bool IsValid => _isValid;
	public event Action? PitchChanged;

	public override void _Ready()
	{
		var edit = GetNode<LineEdit>("PitchEdit");
		var up = GetNode<Button>("StepButtons/StepUp");
		var down = GetNode<Button>("StepButtons/StepDown");
		edit.Text = FormatPitch(_midiPitch);
		_edit = edit; _up = up; _down = down;
		edit.TextChanged += OnTextChanged;
		edit.TextSubmitted += _ => CommitAndNormalize();
		edit.FocusExited += CommitAndNormalize;
		up.Pressed += () => Step(1);
		down.Pressed += () => Step(-1);
		UpdateValidationStyle();
	}

	private void OnTextChanged(string text)
	{
		if (_updatingText) return;
		bool wasValid = _isValid;
		if (TryParsePitch(text, out int parsed))
		{
			bool valueChanged = _midiPitch != parsed;
			_midiPitch = parsed;
			_isValid = true;
			UpdateValidationStyle();
			if (valueChanged || !wasValid) PitchChanged?.Invoke();
		}
		else
		{
			_isValid = false;
			UpdateValidationStyle();
			if (wasValid) PitchChanged?.Invoke();
		}
	}

	private void CommitAndNormalize()
	{
		if (_edit is null || !_isValid) return;
		SetEditText(FormatPitch(_midiPitch));
	}

	private void Step(int delta)
	{
		// Re-parse the current edit so an invalid draft cannot be overwritten by a button press.
		if (_edit is null || !TryParsePitch(_edit.Text, out int current)) return;
		int next = Math.Clamp(current + delta, 0, 127);
		MidiPitch = next;
	}

	private void SetEditText(string text)
	{
		if (_edit is null || _edit.Text == text) return;
		_updatingText = true;
		_edit.Text = text;
		_updatingText = false;
	}

	private void UpdateValidationStyle()
	{
		if (_edit is null) return;
		_edit.TooltipText = Tr(_isValid ? "Enter a note name, such as C#4 or E3" : "Invalid pitch. Use A–G, an optional sharp, and an octave from -1 to 9 (MIDI 0–127).");
		_edit.Modulate = _isValid ? Colors.White : new Color(1.0f, 0.72f, 0.72f);
	}

	public override void _Notification(int what)
	{
		if (what == NotificationTranslationChanged) UpdateValidationStyle();
	}

	/// <summary>Parses natural or sharp notes using MIDI's C-1 = 0 octave convention.</summary>
	public static bool TryParsePitch(string? text, out int midiPitch)
	{
		midiPitch = 0;
		if (string.IsNullOrWhiteSpace(text)) return false;
		string value = text.Trim();
		int i = 0;
		char note = char.ToUpperInvariant(value[i++]);
		int semitone = note switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => -1 };
		if (semitone < 0) return false;
		if (i < value.Length && (value[i] == '#' || value[i] == '♯')) { semitone++; i++; }
		if (i < value.Length && (value[i] == 'b' || value[i] == 'B' || value[i] == '♭')) return false;
		string octaveText = value[i..];
		if (!int.TryParse(octaveText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int octave) || octave is < -1 or > 9) return false;
		int result = (octave + 1) * 12 + semitone;
		if (result is < 0 or > 127) return false;
		midiPitch = result;
		return true;
	}

	/// <summary>Formats a MIDI pitch as a sharp-based note name and signed octave.</summary>
	public static string FormatPitch(int midiPitch)
	{
		if (midiPitch is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(midiPitch));
		string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
		int octave = midiPitch / 12 - 1;
		return names[midiPitch % 12] + octave.ToString(CultureInfo.InvariantCulture);
	}
}

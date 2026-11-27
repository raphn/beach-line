using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

// Per-track visibility and placement for the shared keyboard and fretboard windows.
public partial class DrumScorePresenter
{
	public sealed class BoardState
	{
		public bool KeyboardVisible { get; set; }
		public int KeyboardX { get; set; }
		public int KeyboardY { get; set; }
		public int? KeyboardStartOctave { get; set; }
		public int? KeyboardOctaveCount { get; set; }
		public bool FretboardVisible { get; set; }
		public int FretboardX { get; set; }
		public int FretboardY { get; set; }
	}

	private readonly Dictionary<Guid, BoardState> _floatingBoardStates = new();
	private readonly Dictionary<Guid, (WeakReference<Button> Keyboard, WeakReference<Button> Fretboard)> _floatingBoardButtons = new();
	private KeyboardChordController _keyboardPopup = null!;
	private Window _fretboardPopup = null!;
	private FretboardChordController _chordBoard = null!;
	private Vector2I _defaultKeyboardPosition;
	private int _defaultKeyboardStartOctave;
	private int _defaultKeyboardOctaveCount;
	private Vector2I _defaultFretboardPosition;
	private Guid? _activeFloatingBoardTrackId;
	private bool _floatingBoardsInitialized;
	private bool _floatingBoardsPlaybackHidden;
	private bool _boardHighlightsDirty = true;
	private ScoreTrack _boardHighlightTrack;
	private MeasureTabView _boardHighlightView;

	public void InitializeFloatingBoards()
	{
		if (_floatingBoardsInitialized)
			return;

		_keyboardPopup = GetNode<KeyboardChordController>("%KeyboardPopup");
		_fretboardPopup = GetNode<Window>("%GuitarFretBoardPopup");
		_chordBoard = (FretboardChordController)_fretboardPopup;
		_chordBoard.PlacementStateChanged += ShowChordPlacementHint;
		_keyboardPopup.PlacementStateChanged += ShowChordPlacementHint;
		_keyboardPopup.DirectNoteRequested += pitch => WriteBoardNote(true, 128 - pitch, 0);
		_chordBoard.DirectNoteRequested += (stringNumber, fret) => WriteBoardNote(false, stringNumber, fret);
		_defaultKeyboardPosition = _keyboardPopup.Position;
		_defaultKeyboardStartOctave = _keyboardPopup.StartOctave;
		_defaultKeyboardOctaveCount = _keyboardPopup.OctaveCount;
		_defaultFretboardPosition = _fretboardPopup.Position;
		_keyboardPopup.CloseRequested += () => SetBoardVisible(keyboard: true, visible: false);
		_fretboardPopup.CloseRequested += () => SetBoardVisible(keyboard: false, visible: false);

		// Keep the scene's authored locations, but start with both shared boards closed.
		_keyboardPopup.Hide();
		_fretboardPopup.Hide();
		_floatingBoardsInitialized = true;
		RefreshBoardHighlights();
	}

	public void ActivateFloatingBoards(Guid? trackId)
	{
		InitializeFloatingBoards();
		if (_activeFloatingBoardTrackId == trackId)
			return;

		CaptureActiveFloatingBoardState();
		_activeFloatingBoardTrackId = trackId;
		_boardHighlightsDirty = true;
		RefreshChordBoards();
		if (trackId is Guid id && _floatingBoardStates.TryGetValue(id, out var state))
			ApplyFloatingBoardState(state);
		else
		{
			_keyboardPopup.Position = _defaultKeyboardPosition;
			_keyboardPopup.SetRange(_defaultKeyboardStartOctave, _defaultKeyboardOctaveCount);
			_fretboardPopup.Position = _defaultFretboardPosition;
			_keyboardPopup.Hide();
			_fretboardPopup.Hide();
		}
		if (!IsChordTrack(ActiveChordTrack()))
		{
			_fretboardPopup.Hide();
			if (trackId is Guid incompatibleId && _floatingBoardStates.TryGetValue(incompatibleId, out var incompatibleState))
				incompatibleState.FretboardVisible = false;
		}
		if (IsFloatingBoardPlaybackActive)
		{
			_floatingBoardsPlaybackHidden = true;
			_keyboardPopup.Hide();
			_fretboardPopup.Hide();
		}
		SyncActiveFloatingBoardButtons();
		RefreshBoardHighlights();
	}

	public void BindFloatingBoardButtons(Guid trackId, Button keyboardButton, Button fretboardButton)
	{
		InitializeFloatingBoards();
		_floatingBoardButtons[trackId] = (new WeakReference<Button>(keyboardButton), new WeakReference<Button>(fretboardButton));
		keyboardButton.ToggleMode = true;
		fretboardButton.ToggleMode = true;
		var track = _score?.Tracks.FirstOrDefault(item => item.Id == trackId);
		keyboardButton.Disabled = track?.UsesFullTab != true;
		keyboardButton.Visible = !keyboardButton.Disabled;
		keyboardButton.TooltipText = Tr("Open the keyboard. It hides during playback and reopens afterward if it was open.");
		fretboardButton.Disabled = !IsChordTrack(track);
		fretboardButton.TooltipText = fretboardButton.Disabled ? Tr("Fretboard is available for configured string instruments.") : Tr("Open the fretboard. It hides during playback and reopens afterward if it was open.");
		keyboardButton.Toggled += pressed => HandleFloatingBoardToggle(trackId, keyboard: true, pressed);
		fretboardButton.Toggled += pressed => HandleFloatingBoardToggle(trackId, keyboard: false, pressed);

		if (_floatingBoardStates.TryGetValue(trackId, out var state))
		{
			keyboardButton.SetPressedNoSignal(!keyboardButton.Disabled && state.KeyboardVisible);
			fretboardButton.SetPressedNoSignal(!fretboardButton.Disabled && state.FretboardVisible);
		}
		else
		{
			keyboardButton.SetPressedNoSignal(false);
			fretboardButton.SetPressedNoSignal(false);
		}
	}

	public Dictionary<Guid, BoardState> CaptureFloatingBoardStates()
	{
		InitializeFloatingBoards();
		CaptureActiveFloatingBoardState();
		var snapshot = new Dictionary<Guid, BoardState>();
		foreach (var pair in _floatingBoardStates)
			snapshot[pair.Key] = CopyBoardState(pair.Value);
		return snapshot;
	}

	public void RestoreFloatingBoardStates(Dictionary<Guid, BoardState> states)
	{
		InitializeFloatingBoards();
		_floatingBoardStates.Clear();
		foreach (var pair in states)
			_floatingBoardStates[pair.Key] = CopyBoardState(pair.Value);

		if (_activeFloatingBoardTrackId is Guid id && _floatingBoardStates.TryGetValue(id, out var activeState))
		{
			if (!IsChordTrack(ActiveChordTrack())) activeState.FretboardVisible = false;
			ApplyFloatingBoardState(activeState);
		}
		else
		{
			_keyboardPopup.Hide();
			_fretboardPopup.Hide();
		}
		if (IsFloatingBoardPlaybackActive)
		{
			_floatingBoardsPlaybackHidden = true;
			_keyboardPopup.Hide();
			_fretboardPopup.Hide();
		}
		SyncActiveFloatingBoardButtons();
	}

	public void PollFloatingBoards()
	{
		UpdateFloatingBoardPlaybackVisibility();
		if (_floatingBoardsInitialized)
		{
			var previousKeyboardTrack = _keyboardChordTrack;
			var previousChordTrack = _chordTrack;
			RefreshChordBoards();
			if (!ReferenceEquals(previousKeyboardTrack, _keyboardChordTrack) || !ReferenceEquals(previousChordTrack, _chordTrack))
				_boardHighlightsDirty = true;
		}
		if (_floatingBoardsInitialized) RefreshBoardHighlights();
		if (!_floatingBoardsInitialized || _activeFloatingBoardTrackId is not Guid id ||
			!GodotObject.IsInstanceValid(_keyboardPopup) || !GodotObject.IsInstanceValid(_fretboardPopup))
			return;
		if (_floatingBoardsPlaybackHidden)
			return;

		var state = GetOrCreateBoardState(id);
		var changed = false;
		changed |= Update(state.KeyboardVisible, _keyboardPopup.Visible, value => state.KeyboardVisible = value);
		changed |= Update(state.KeyboardX, _keyboardPopup.Position.X, value => state.KeyboardX = value);
		changed |= Update(state.KeyboardY, _keyboardPopup.Position.Y, value => state.KeyboardY = value);
		if (_keyboardChordTrack is not null)
		{
			changed |= Update(state.KeyboardStartOctave ?? _defaultKeyboardStartOctave, _keyboardPopup.StartOctave,
				value => state.KeyboardStartOctave = value);
			changed |= Update(state.KeyboardOctaveCount ?? _defaultKeyboardOctaveCount, _keyboardPopup.OctaveCount,
				value => state.KeyboardOctaveCount = value);
		}
		changed |= Update(state.FretboardVisible, _fretboardPopup.Visible, value => state.FretboardVisible = value);
		changed |= Update(state.FretboardX, _fretboardPopup.Position.X, value => state.FretboardX = value);
		changed |= Update(state.FretboardY, _fretboardPopup.Position.Y, value => state.FretboardY = value);
		if (changed)
		{
			SyncFloatingBoardButtons(id, state);
			MarkEditorStateDirty();
		}
	}

	/// <summary>Refreshes the non-mutating note highlights shown by the shared boards.</summary>
	private void RefreshBoardHighlights()
	{
		if (!_floatingBoardsInitialized || !GodotObject.IsInstanceValid(_keyboardPopup) || !GodotObject.IsInstanceValid(_chordBoard)) return;
		var currentTrack = ActiveChordTrack();
		var currentView = _activeEditorView;
		if (!ReferenceEquals(_boardHighlightTrack, currentTrack) || !ReferenceEquals(_boardHighlightView, currentView))
			_boardHighlightsDirty = true;
		if (!_boardHighlightsDirty) return;
		_boardHighlightsDirty = false;

		var view = currentView;
		var track = currentTrack;
		_boardHighlightTrack = track;
		_boardHighlightView = view;
		if (view is null || !GodotObject.IsInstanceValid(view) || view.ViewData is not { } data || track is null || _editorTabTrackId != track.Id)
		{
			_keyboardPopup.SetTabHighlights(Array.Empty<int>());
			_chordBoard.SetTabHighlights(Array.Empty<(int StringNumber, int Fret)>());
			return;
		}

		var notes = new List<MeasureTabView.TabNoteViewData>();
		foreach (var note in view.SelectedNotes)
			if (note.StringNumber > 0 && note.StringNumber <= data.OpenStringMidiPitches.Count && note.Fret >= 0)
				notes.Add(note);

		// The focused cell is independent from selection. Include its authored note
		// when the cursor is exactly on that note, without changing the selection.
		if (view.TryGetFocusedCell(out var focusString, out var focusOnset))
		{
			var focused = data.Notes.FirstOrDefault(note =>
				note.StringNumber == focusString && SameExactTime(note.Onset, focusOnset) && note.Fret >= 0);
			if (focused is not null && !notes.Any(note => note.StringNumber == focused.StringNumber && SameExactTime(note.Onset, focused.Onset)))
				notes.Add(focused);
		}

		if (track.UsesFullTab)
		{
			var pitches = notes
				.Where(note => note.StringNumber <= track.TabLaneMidiPitches.Count)
				.Select(note => track.TabLaneMidiPitches[note.StringNumber - 1])
				.Where(pitch => pitch is >= 0 and <= 127)
				.Distinct().ToArray();
			_keyboardPopup.SetTabHighlights(pitches);
		}
		else
			_keyboardPopup.SetTabHighlights(Array.Empty<int>());

		if (IsChordTrack(track))
		{
			var frets = notes
				.Where(note => note.StringNumber <= track.TabLaneMidiPitches.Count)
				.Select(note => (note.StringNumber, note.Fret))
				.Distinct().ToArray();
			_chordBoard.SetTabHighlights(frets);
		}
		else
			_chordBoard.SetTabHighlights(Array.Empty<(int StringNumber, int Fret)>());
	}

	private static bool SameExactTime(MeasureTabView.ExactTime left, MeasureTabView.ExactTime right)
	{
		left = left.Reduced(); right = right.Reduced();
		return left.Numerator == right.Numerator && left.Denominator == right.Denominator;
	}

	private void RequestBoardHighlightsRefresh() => _boardHighlightsDirty = true;

	private void HandleFloatingBoardToggle(Guid trackId, bool keyboard, bool pressed)
	{
		InitializeFloatingBoards();
		if (_activeFloatingBoardTrackId != trackId)
			SetActiveEditorTrack(trackId);

		SetBoardVisible(keyboard, pressed);
	}

	private void SetBoardVisible(bool keyboard, bool visible)
	{
		if (!_floatingBoardsInitialized)
			return;
		if (IsFloatingBoardPlaybackActive)
		{
			SyncActiveFloatingBoardButtons();
			return;
		}

		var popup = keyboard ? _keyboardPopup : _fretboardPopup;
		if (keyboard && visible && _keyboardChordTrack is null) return;
		if (!keyboard && visible && !IsChordTrack(ActiveChordTrack())) return;
		if (!visible)
		{
			if (keyboard) _keyboardPopup.ResetComposition();
			else _chordBoard.ResetComposition();
		}
		if (popup.Visible == visible)
			return;

		popup.Visible = visible;
		if (_activeFloatingBoardTrackId is Guid id)
		{
			var state = GetOrCreateBoardState(id);
			if (keyboard) state.KeyboardVisible = visible;
			else state.FretboardVisible = visible;
			SyncFloatingBoardButtons(id, state);
			MarkEditorStateDirty();
		}
	}

	private void CaptureActiveFloatingBoardState()
	{
		if (!_floatingBoardsInitialized || _activeFloatingBoardTrackId is not Guid id ||
			_floatingBoardsPlaybackHidden ||
			!GodotObject.IsInstanceValid(_keyboardPopup) || !GodotObject.IsInstanceValid(_fretboardPopup))
			return;

		var state = GetOrCreateBoardState(id);
		state.KeyboardVisible = _keyboardPopup.Visible;
		state.KeyboardX = _keyboardPopup.Position.X;
		state.KeyboardY = _keyboardPopup.Position.Y;
		if (_keyboardChordTrack is not null)
		{
			state.KeyboardStartOctave = _keyboardPopup.StartOctave;
			state.KeyboardOctaveCount = _keyboardPopup.OctaveCount;
		}
		state.FretboardVisible = _fretboardPopup.Visible;
		state.FretboardX = _fretboardPopup.Position.X;
		state.FretboardY = _fretboardPopup.Position.Y;
	}

	private void ApplyFloatingBoardState(BoardState state)
	{
		_keyboardPopup.Position = new Vector2I(state.KeyboardX, state.KeyboardY);
		_keyboardPopup.SetRange(state.KeyboardStartOctave ?? _defaultKeyboardStartOctave, state.KeyboardOctaveCount ?? _defaultKeyboardOctaveCount);
		_fretboardPopup.Position = new Vector2I(state.FretboardX, state.FretboardY);
		_keyboardPopup.Visible = state.KeyboardVisible && _keyboardChordTrack is not null;
		_fretboardPopup.Visible = state.FretboardVisible && _chordTrack is not null;
	}

	private BoardState GetOrCreateBoardState(Guid trackId)
	{
		if (!_floatingBoardStates.TryGetValue(trackId, out var state))
		{
			state = new BoardState
			{
				KeyboardX = _keyboardPopup.Position.X,
				KeyboardY = _keyboardPopup.Position.Y,
				FretboardX = _fretboardPopup.Position.X,
				FretboardY = _fretboardPopup.Position.Y
			};
			_floatingBoardStates[trackId] = state;
		}
		return state;
	}

	private void SyncActiveFloatingBoardButtons()
	{
		if (_activeFloatingBoardTrackId is Guid id && _floatingBoardStates.TryGetValue(id, out var state))
			SyncFloatingBoardButtons(id, state);
		else if (_activeFloatingBoardTrackId is Guid activeId && _floatingBoardButtons.TryGetValue(activeId, out var buttons))
		{
			SetButtonPressed(buttons.Keyboard, false);
			SetButtonPressed(buttons.Fretboard, false);
		}
		PruneFloatingBoardButtons();
	}

	private void SyncFloatingBoardButtons(Guid trackId, BoardState state)
	{
		if (!_floatingBoardButtons.TryGetValue(trackId, out var buttons))
			return;
		SetButtonPressed(buttons.Keyboard, state.KeyboardVisible);
		SetButtonPressed(buttons.Fretboard, state.FretboardVisible);
	}

	private static void SetButtonPressed(WeakReference<Button> reference, bool pressed)
	{
		if (reference.TryGetTarget(out var button) && GodotObject.IsInstanceValid(button))
			button.SetPressedNoSignal(pressed && !button.Disabled);
	}

	private void PruneFloatingBoardButtons()
	{
		var stale = new List<Guid>();
		foreach (var pair in _floatingBoardButtons)
		{
			var keyboardValid = pair.Value.Keyboard.TryGetTarget(out var keyboard) && GodotObject.IsInstanceValid(keyboard);
			var fretboardValid = pair.Value.Fretboard.TryGetTarget(out var fretboard) && GodotObject.IsInstanceValid(fretboard);
			if (!keyboardValid || !fretboardValid)
				stale.Add(pair.Key);
		}
		foreach (var id in stale)
			_floatingBoardButtons.Remove(id);
	}

	private static BoardState CopyBoardState(BoardState state) => new()
	{
		KeyboardVisible = state.KeyboardVisible,
		KeyboardX = state.KeyboardX,
		KeyboardY = state.KeyboardY,
		KeyboardStartOctave = state.KeyboardStartOctave,
		KeyboardOctaveCount = state.KeyboardOctaveCount,
		FretboardVisible = state.FretboardVisible,
		FretboardX = state.FretboardX,
		FretboardY = state.FretboardY
	};

	private static bool Update<T>(T previous, T value, Action<T> assign) where T : IEquatable<T>
	{
		if (previous.Equals(value)) return false;
		assign(value);
		return true;
	}

	private bool IsFloatingBoardPlaybackActive =>
		(_soundFontAudio is not null && _soundFontAudio.IsScorePlaying) ||
		(_arrangementAudio is not null && _arrangementAudio.IsScorePlaying);

	private void UpdateFloatingBoardPlaybackVisibility()
	{
		if (!_floatingBoardsInitialized || !GodotObject.IsInstanceValid(_keyboardPopup) ||
			!GodotObject.IsInstanceValid(_fretboardPopup))
			return;

		var playbackActive = IsFloatingBoardPlaybackActive;
		if (playbackActive)
		{
			if (_floatingBoardsPlaybackHidden)
				return;
			CaptureActiveFloatingBoardState();
			_floatingBoardsPlaybackHidden = true;
			_keyboardPopup.Hide();
			_fretboardPopup.Hide();
			SyncActiveFloatingBoardButtons();
			return;
		}

		if (!_floatingBoardsPlaybackHidden)
			return;
		_floatingBoardsPlaybackHidden = false;
		if (_activeFloatingBoardTrackId is Guid id && _floatingBoardStates.TryGetValue(id, out var state))
		{
			if (!IsChordTrack(ActiveChordTrack())) state.FretboardVisible = false;
			ApplyFloatingBoardState(state);
		}
		else
		{
			_keyboardPopup.Hide();
			_fretboardPopup.Hide();
		}
		SyncActiveFloatingBoardButtons();
	}
}

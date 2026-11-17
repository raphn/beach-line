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
		public bool FretboardVisible { get; set; }
		public int FretboardX { get; set; }
		public int FretboardY { get; set; }
	}

	private readonly Dictionary<Guid, BoardState> _floatingBoardStates = new();
	private readonly Dictionary<Guid, (WeakReference<Button> Keyboard, WeakReference<Button> Fretboard)> _floatingBoardButtons = new();
	private Window _keyboardPopup = null!;
	private Window _fretboardPopup = null!;
	private FretboardChordController _chordBoard = null!;
	private Vector2I _defaultKeyboardPosition;
	private Vector2I _defaultFretboardPosition;
	private Guid? _activeFloatingBoardTrackId;
	private bool _floatingBoardsInitialized;

	public void InitializeFloatingBoards()
	{
		if (_floatingBoardsInitialized)
			return;

		_keyboardPopup = GetNode<Window>("%KeyboardPopup");
		_fretboardPopup = GetNode<Window>("%GuitarFretBoardPopup");
		_chordBoard = (FretboardChordController)_fretboardPopup;
		_chordBoard.PlacementStateChanged += () =>
		{
			if (_chordBoard.IsArmed && GodotObject.IsInstanceValid(_tabStatus))
				SetTabStatus("Click the desired time in this track's tablature to insert the chord. Escape cancels placement.");
		};
		var keyboardOptions = _keyboardPopup.GetNode<Control>("Keyboard/KeysAndChords/ChordInsertingOptions");
		foreach (var name in new[] { "CreateModeToggle", "InsertChordToggle" })
		{
			var button = keyboardOptions.GetNode<Button>(name);
			button.Disabled = true;
			button.TooltipText = "Keyboard chord entry will be available in the notation editor.";
		}
		_defaultKeyboardPosition = _keyboardPopup.Position;
		_defaultFretboardPosition = _fretboardPopup.Position;
		_keyboardPopup.CloseRequested += () => SetBoardVisible(keyboard: true, visible: false);
		_fretboardPopup.CloseRequested += () => SetBoardVisible(keyboard: false, visible: false);

		// Keep the scene's authored locations, but start with both shared boards closed.
		_keyboardPopup.Hide();
		_fretboardPopup.Hide();
		_floatingBoardsInitialized = true;
	}

	public void ActivateFloatingBoards(Guid? trackId)
	{
		InitializeFloatingBoards();
		if (_activeFloatingBoardTrackId == trackId)
			return;

		CaptureActiveFloatingBoardState();
		_activeFloatingBoardTrackId = trackId;
		RefreshChordBoard();
		if (trackId is Guid id && _floatingBoardStates.TryGetValue(id, out var state))
			ApplyFloatingBoardState(state);
		else
		{
			_keyboardPopup.Position = _defaultKeyboardPosition;
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
		SyncActiveFloatingBoardButtons();
	}

	public void BindFloatingBoardButtons(Guid trackId, Button keyboardButton, Button fretboardButton)
	{
		InitializeFloatingBoards();
		_floatingBoardButtons[trackId] = (new WeakReference<Button>(keyboardButton), new WeakReference<Button>(fretboardButton));
		keyboardButton.ToggleMode = true;
		fretboardButton.ToggleMode = true;
		var track = _score?.Tracks.FirstOrDefault(item => item.Id == trackId);
		fretboardButton.Disabled = !IsChordTrack(track);
		fretboardButton.TooltipText = fretboardButton.Disabled ? "Fretboard is available for configured string instruments." : "Open fretboard";
		keyboardButton.Toggled += pressed => HandleFloatingBoardToggle(trackId, keyboard: true, pressed);
		fretboardButton.Toggled += pressed => HandleFloatingBoardToggle(trackId, keyboard: false, pressed);

		if (_floatingBoardStates.TryGetValue(trackId, out var state))
		{
			keyboardButton.SetPressedNoSignal(state.KeyboardVisible);
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
		SyncActiveFloatingBoardButtons();
	}

	public void PollFloatingBoards()
	{
		if (_floatingBoardsInitialized) RefreshChordBoard();
		if (!_floatingBoardsInitialized || _activeFloatingBoardTrackId is not Guid id ||
			!GodotObject.IsInstanceValid(_keyboardPopup) || !GodotObject.IsInstanceValid(_fretboardPopup))
			return;

		var state = GetOrCreateBoardState(id);
		var changed = false;
		changed |= Update(state.KeyboardVisible, _keyboardPopup.Visible, value => state.KeyboardVisible = value);
		changed |= Update(state.KeyboardX, _keyboardPopup.Position.X, value => state.KeyboardX = value);
		changed |= Update(state.KeyboardY, _keyboardPopup.Position.Y, value => state.KeyboardY = value);
		changed |= Update(state.FretboardVisible, _fretboardPopup.Visible, value => state.FretboardVisible = value);
		changed |= Update(state.FretboardX, _fretboardPopup.Position.X, value => state.FretboardX = value);
		changed |= Update(state.FretboardY, _fretboardPopup.Position.Y, value => state.FretboardY = value);
		if (changed)
		{
			SyncFloatingBoardButtons(id, state);
			MarkEditorStateDirty();
		}
	}

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

		var popup = keyboard ? _keyboardPopup : _fretboardPopup;
		if (!keyboard && visible && !IsChordTrack(ActiveChordTrack())) return;
		if (!keyboard && !visible) _chordBoard.ResetComposition();
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
			!GodotObject.IsInstanceValid(_keyboardPopup) || !GodotObject.IsInstanceValid(_fretboardPopup))
			return;

		var state = GetOrCreateBoardState(id);
		state.KeyboardVisible = _keyboardPopup.Visible;
		state.KeyboardX = _keyboardPopup.Position.X;
		state.KeyboardY = _keyboardPopup.Position.Y;
		state.FretboardVisible = _fretboardPopup.Visible;
		state.FretboardX = _fretboardPopup.Position.X;
		state.FretboardY = _fretboardPopup.Position.Y;
	}

	private void ApplyFloatingBoardState(BoardState state)
	{
		_keyboardPopup.Position = new Vector2I(state.KeyboardX, state.KeyboardY);
		_fretboardPopup.Position = new Vector2I(state.FretboardX, state.FretboardY);
		_keyboardPopup.Visible = state.KeyboardVisible;
		_fretboardPopup.Visible = state.FretboardVisible;
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
			button.SetPressedNoSignal(pressed);
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Globalization;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;
using OpenTab.Music.Persistence;

public partial class DrumScorePresenter : Control
{
	private static readonly int[] NoteDenominators = [1, 2, 4, 8, 16, 32, 64];
	private static readonly string[] NoteValueNames = ["whole", "half", "quarter", "eighth", "sixteenth", "thirty-second", "sixty-fourth"];
	private static readonly TimeSignature[] TimeSignatureChoices =
	[
		new(2, 4),
		new(3, 4),
		new(4, 4),
		new(5, 8),
		new(6, 8),
		new(7, 8)
	];

	private static readonly DrumTrackDefinition[] DrumDefinitions =
	[
		new("Kick", 36, "res://Interface/Common/Graphs/plain-circle_delapouite.png"),
		new("Snare", 38, "res://Interface/Common/Graphs/plain-circle_delapouite.png"),
		new("Closed Hi-Hat", 42, "res://Interface/Common/Graphs/plain-circle_delapouite.png")
	];

	private readonly Dictionary<Guid, DrumTrackDefinition> _drumDefinitions = new();
	private ScoreDocument _score = null!;
	private Button _addFirstMeasureButton = null!;
	private RichTextLabel _sectionInformation = null!;
	private HBoxContainer _measureHeaders = null!;
	private ScrollContainer _beatsScrollContainer = null!;
	private VBoxContainer _trackInfoRows = null!;
	private VBoxContainer _trackBeatsEditors = null!;
	private Control _infoControllers = null!;
	private Control _beatsGuideHeaders = null!;
	private HSlider _percussionMasterVolume = null!;
	private Label _percussionMasterVolumeLabel = null!;
	private bool _alignmentUpdateQueued;
	private bool _synchronizingAlignment;
	private Guid? _percussionControlsSectionId;
	private bool _refreshingPercussionSectionControls;
	private readonly List<(ScoreTrack Track, Button Lead, SpinBox PatternLength)> _percussionSectionControls = new();
	private PackedScene _trackRowScene = null!;
	private PackedScene _beatEditorScene = null!;
	private PackedScene _measureHeaderScene = null!;
	private SoundFontAudioNode _soundFontAudio = null!;
	private Button _playPauseButton = null!;
	private Button _stopButton = null!;
	private LineEdit _tempoEdit = null!;
	private Button _metronomeButton = null!;
	private Button _countInButton = null!;
	private Button _countInEveryLoopButton = null!;
	private readonly List<long> _tempoTapTicks = new();
	private string _tempoEditBaseline = string.Empty;
	private string _tempoInputError = string.Empty;
	private bool _tempoEditCancelled;
	private bool _recordingPauseRequested;
	private int _selectedSectionIndex;
	private const string ExamplePath = "res://_project/examples/drum_editing_demo.otab";
	private string _sessionPath;
	private string _sessionMetadataPath;
	private string _currentFilePath;
	private bool _sessionDirty;
	private bool _hasUnsavedChanges;
	private double _sessionDelay;
	private FileDialog _openDialog;
	private FileDialog _saveDialog;
	private FilePickerMemory _filePickerMemory;
	private RecentScorePaths _recentScorePaths;
	private PopupMenu _recentMenu;
	private PopupMenu _fileMenu;
	private Action _confirmedFileTransition;
	private AcceptDialog _fileErrorDialog;
	private ConfirmationDialog _discardChangesDialog;

	public override void _Ready()
	{
		AppPreferences.Initialize();
		AppPreferences.BindingsChanged += RefreshTabStatusTooltip;
		AppPreferences.BindingsChanged += RefreshTransportShortcutTooltips;
		AppPreferences.BindingsChanged += RefreshPlaybackLoopTooltips;
		BindScene();
		BindExportControls();
		InitializeAppSettings();
		BindTabEditor();
		InitializePlaybackCursor();
		InitializePlaybackLoopControls();
		InitializeFloatingBoards();
		InitializeWorkspacePersistence();
		BindArrangements();
		InitializeArrangementEditorLock();
		LoadInitialScore();
		ResetScoreHistory();
		RestoreEditorWorkspace(File.Exists(WorkspaceStatePath(null)) ? null : _currentFilePath);
		BindSectionNavigation();
		RenderSection();
	}

	public override void _Notification(int what)
	{
		if (what != NotificationTranslationChanged || !IsInsideTree() || _score is null) return;
		RefreshLocalizedRuntimeText();
	}

	public override void _ExitTree()
	{
		_exportCancellation?.Cancel();
		AppPreferences.BindingsChanged -= RefreshTabStatusTooltip;
		AppPreferences.BindingsChanged -= RefreshTransportShortcutTooltips;
		AppPreferences.BindingsChanged -= RefreshPlaybackLoopTooltips;
		SaveEditorWorkspace();
		SaveSession();
		_filePickerMemory?.Save();
	}

	private void RefreshLocalizedRuntimeText()
	{
		RefreshSettingsMenuTranslation();
		if (_fileMenu is not null)
		{
			_fileMenu.Name = Tr("File");
			_fileMenu.SetItemText(0, Tr("New"));
			_fileMenu.SetItemText(1, Tr("Open…"));
			_fileMenu.SetItemText(2, Tr("Save"));
			_fileMenu.SetItemText(3, Tr("Save As…"));
			_fileMenu.SetItemText(4, Tr("Open Recent"));
		}
		if (_historyMenu is not null)
		{
			_historyMenu.Name = Tr("Edit");
			_historyMenu.SetItemText(0, Tr("Undo"));
			_historyMenu.SetItemText(1, Tr("Redo"));
		}
		if (_openDialog is not null) _openDialog.Title = Tr("Open score");
		if (_saveDialog is not null) _saveDialog.Title = Tr("Save score");
		if (_fileErrorDialog is not null) _fileErrorDialog.Title = Tr("File error");
		if (_discardChangesDialog is not null)
		{
			_discardChangesDialog.Title = Tr("Discard unsaved changes?");
			_discardChangesDialog.DialogText = Tr("The current score has unsaved changes. Continue and discard those changes?");
			_discardChangesDialog.GetOkButton().Text = Tr("Discard and Continue");
		}
		if (_tempoEdit is not null) _tempoEdit.TooltipText = string.IsNullOrEmpty(_tempoInputError) ? Tr("Score default tempo in quarter-note BPM. Section defaults and measure tempo events override it at their positions.") : Tr(_tempoInputError);
		RefreshTransportShortcutTooltips();
		RefreshPlaybackLoopTooltips();
		RefreshExportLocalization();
		RefreshArrangementEditorLockTooltips();
		RefreshAudioStatusLocalization();
		if (_stopButton is not null) _stopButton.TooltipText = Tr("Stop playback and return to the selected measure.");
		RefreshTabStatusTooltip();
		RebuildRecentMenuIfReady();
		if (_score.Sections.Count > 0)
		{
			RenderSection();
			RefreshTabStatusLocalization();
			RefreshSectionTempoStatus();
			RefreshFocusedPercussionControls(force: true);
		}
	}

	private void RebuildRecentMenuIfReady()
	{
		if (_recentMenu is not null && _recentScorePaths is not null) RebuildRecentMenu();
	}

	public override void _Process(double delta)
	{
		if (_expressionEditor is not null && GodotObject.IsInstanceValid(_expressionEditor) && _expressionEditor.Visible &&
			!ReferenceEquals(_expressionScore, _score)) _expressionEditor.Hide();
		PollFloatingBoards();
		PollArrangementPlayer();
		if (!_audioRecordingStarting && _audioRecorder?.IsTakeActive != true)
			_recordingPauseRequested = false;
		else if (_recordingPauseRequested)
		{
			if (_recordingIntoArrangement)
			{
				if (_arrangementAudio.IsScorePlaying && !_arrangementAudio.IsScorePaused) PauseArrangementTransport();
			}
			else if (_soundFontAudio.IsScorePlaying && !_soundFontAudio.IsScorePaused)
			{
				_soundFontAudio.PauseScore();
				_playPauseButton.SetPressedNoSignal(false);
			}
			if (_audioRecorder?.State == BeachLine.Music.Audio.MicrophoneWavRecorder.RecorderState.Recording)
				_audioRecorder.Pause(discardPendingSamples: true);
		}
		if (_workspaceDirty && (_workspaceDelay -= delta) <= 0) SaveEditorWorkspace();
		UpdatePlaybackCursor();
		UpdatePlaybackLoopSelection();
		if (_velocityDrag is null && _sessionDirty && (_sessionDelay -= delta) <= 0) SaveSession();
		if (_playPauseButton is not null && _playPauseButton.ButtonPressed
			&& !_soundFontAudio.IsScorePlaying && !_soundFontAudio.IsScorePaused)
		{
			_playPauseButton.SetPressedNoSignal(false);
		}
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (_settingsDialog is not null && _settingsDialog.Visible) return;
		if (TryResizeTracksWithWheel(inputEvent))
		{
			GetViewport().SetInputAsHandled();
			return;
		}
		// During a take, SPACE controls the transport this take is following, regardless
		// of which player normally owns the playback shortcuts.
		if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space,
			CtrlPressed: false, AltPressed: false, ShiftPressed: false, MetaPressed: false } &&
			inputEvent.IsActionPressed("play", false, true) &&
			(_audioRecordingStarting || _audioRecorder?.IsTakeActive == true))
		{
			ToggleRecordingPause();
			GetViewport().SetInputAsHandled();
			return;
		}
		if ((_audioRecordingStarting || _audioRecorder?.IsTakeActive == true) &&
			inputEvent.IsActionPressed("play_from_start", false, true))
		{
			GetViewport().SetInputAsHandled();
			return;
		}
		// Keep the transport shortcut available while all editing shortcuts are blocked.
		if (_arrangementAudio is not null && _arrangementAudio.IsScorePlaying)
		{
			if (inputEvent is InputEventKey)
			{
				if (inputEvent.IsActionPressed("play", false, true) || inputEvent.IsActionPressed("stop", false, true))
					StopArrangement();
				GetViewport().SetInputAsHandled();
			}
			return;
		}
		if (ChordPlacementArmed && inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
		{
			CancelChordPlacement();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } mouse)
		{
			if (!ChordPlacementArmed)
			{
				if (TryExpandCollapsedTrackAt(mouse.GlobalPosition))
				{
					GetViewport().SetInputAsHandled();
					return;
				}
				SelectTrackAtPointer(mouse.GlobalPosition);
				SelectEditingMeasureAt(mouse.GlobalPosition);
			}
		}
		var focusOwner = GetViewport().GuiGetFocusOwner();
		// Keep project-wide shortcuts from replacing or recording a score during a preview.
		// Escape still reaches the editor so it can cancel the gesture.
		if (_velocityDrag is not null && inputEvent is InputEventKey dragKey && dragKey.Keycode != Key.Escape)
		{
			GetViewport().SetInputAsHandled();
			return;
		}
		if (inputEvent is InputEventKey historyKey && HandleScoreHistoryShortcut(historyKey))
		{
			GetViewport().SetInputAsHandled();
			return;
		}
		if (inputEvent.IsActionPressed("new_score", false, true))
		{
			RequestNewScore();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (inputEvent.IsActionPressed("saved", false, true))
		{
			if (focusOwner == _tempoEdit && !CommitScoreTempo())
			{
				GetViewport().SetInputAsHandled();
				return;
			}
			SaveCurrentScore();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (focusOwner is LineEdit or TextEdit)
		{
			if (focusOwner == _tempoEdit && inputEvent is InputEventKey { Pressed: true, Keycode: Key.Escape })
			{
				_tempoEditCancelled = true;
				RefreshTempoEditFromScore(force: true);
				ClearTempoInputError();
				_tempoEdit.ReleaseFocus();
				GetViewport().SetInputAsHandled();
			}
			else if (inputEvent is InputEventKey { Pressed: true, Echo: false, CtrlPressed: false, AltPressed: false } enter &&
				(enter.Keycode == Key.Enter || enter.Keycode == Key.KpEnter))
			{
				// Let LineEdit emit TextSubmitted before its focus-exit handlers run.
				var edit = focusOwner;
				Callable.From(() => { if (GodotObject.IsInstanceValid(edit) && edit.HasFocus()) edit.ReleaseFocus(); }).CallDeferred();
				if (edit is TextEdit) GetViewport().SetInputAsHandled();
			}
			return;
		}
		if (inputEvent.IsActionPressed("repeat_score", false, true) ||
			inputEvent.IsActionPressed("repeat_measure", false, true) ||
			inputEvent.IsActionPressed("repeat_sections", false, true))
		{
			if (!_audioRecordingStarting && _audioRecorder?.IsTakeActive != true)
			{
				if (inputEvent.IsActionPressed("repeat_score", false, true)) SelectPlaybackLoopMode(PlaybackLoopMode.Score);
				else if (inputEvent.IsActionPressed("repeat_measure", false, true)) SelectPlaybackLoopMode(PlaybackLoopMode.Measure);
				else SelectPlaybackLoopMode(PlaybackLoopMode.Sections);
			}
			GetViewport().SetInputAsHandled();
			return;
		}
		if ((inputEvent.IsActionPressed("copy_notes", false, true) || inputEvent.IsActionPressed("paste_notes", false, true)) &&
			HandleTabClipboardShortcut(inputEvent.IsActionPressed("copy_notes", false, true) ? Key.C : Key.V, focusOwner))
		{
			GetViewport().SetInputAsHandled();
			return;
		}
		var playFromStart = inputEvent.IsActionPressed("play_from_start", false, true);
		var playOrStop = inputEvent.IsActionPressed("play", false, true) || inputEvent.IsActionPressed("stop", false, true);
		if (playFromStart)
		{
			if (_arrangementShortcuts) StartArrangement();
			else StartPlayback(fromStart: true);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (playOrStop && _arrangementAudio.IsScorePlaying)
		{
			StopPlayback();
			StopArrangement();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_arrangementShortcuts && playOrStop)
		{
			StartArrangement();
			GetViewport().SetInputAsHandled();
			return;
		}

		if (inputEvent.IsActionPressed("stop", false, true) && _soundFontAudio.IsScorePlaying)
		{
			StopPlayback();
		}
		else if (inputEvent.IsActionPressed("play", false, true))
		{
			if (!_soundFontAudio.IsScorePlaying || _soundFontAudio.IsScorePaused)
				StartPlayback(fromStart: false);
			else
				return;
		}
		else
		{
			return;
		}

		GetViewport().SetInputAsHandled();
	}

	private void ToggleRecordingPause() => SetRecordingPaused(!_recordingPauseRequested);

	private void SetRecordingPaused(bool paused)
	{
		if (_recordingPauseRequested == paused && _audioRecorder?.IsTakeActive != true) return;
		_recordingPauseRequested = paused;
		// Flush/stop capture before pausing the transport, so latency in the UI path
		// cannot append microphone samples from after the musical pause point.
		if (_audioRecorder?.IsTakeActive == true)
		{
			if (paused) _audioRecorder.Pause();
			else _audioRecorder.Resume();
		}
		if (_recordingIntoArrangement)
		{
			if (paused) PauseArrangementTransport();
			else ResumeArrangementTransport();
		}
		else if (paused)
		{
			if (_soundFontAudio.IsScorePlaying && !_soundFontAudio.IsScorePaused) _soundFontAudio.PauseScore();
			_playPauseButton.SetPressedNoSignal(false);
		}
		else if (_soundFontAudio.IsScorePaused)
		{
			_soundFontAudio.ResumeScore();
			_playPauseButton.SetPressedNoSignal(true);
		}
		SetAudioStatus(paused ? "Recording paused. Press Space to resume the same take." : "Recording resumed.");
	}

	private void BindScene()
	{
		_soundFontAudio = GetNode<SoundFontAudioNode>("/root/SoundFontAudio");
		_playPauseButton = GetNode<Button>("%PlayPauseButton");
		var stopButton = _stopButton = GetNode<Button>("%StopButton");
		_tempoEdit = GetNode<LineEdit>("%TempoEdit");
		var tapTempoButton = GetNode<Button>("%TapTempoButton");
		_metronomeButton = GetNode<Button>("%MetronomeButton");
		_countInButton = GetNode<Button>("%CountInButton");
		_countInEveryLoopButton = GetNode<Button>("%CountInEveryLoopButton");
		tapTempoButton.Pressed += RegisterTempoTap;
		_metronomeButton.Toggled += pressed =>
		{
			if (_score is null || _score.MetronomeEnabled == pressed) return;
			_score.MetronomeEnabled = pressed;
			SyncMetronomePlayback();
			NotifyScoreEdited();
		};
		_countInButton.Toggled += pressed =>
		{
			if (_score is null || _score.CountInEnabled == pressed) return;
			_score.CountInEnabled = pressed;
			SyncCountInPlayback();
			NotifyScoreEdited(refreshPlayback: false);
		};
		_countInEveryLoopButton.Toggled += pressed =>
		{
			if (_score is null || _score.CountInEveryLoop == pressed) return;
			_score.CountInEveryLoop = pressed;
			SyncCountInPlayback();
			NotifyScoreEdited(refreshPlayback: false);
		};
		_tempoEdit.TooltipText = Tr("Score default tempo in quarter-note BPM. Section defaults and measure tempo events override it at their positions.");
		_tempoEdit.TextSubmitted += _ => CommitScoreTempo();
		_tempoEdit.FocusExited += () =>
		{
			if (_tempoEditCancelled)
			{
				_tempoEditCancelled = false;
				return;
			}
			if (!CommitScoreTempo())
			{
				var error = _tempoInputError;
				RefreshTempoEditFromScore(force: true);
				SetTempoInputError(error, highlight: false);
			}
		};
		BindFileMenu();
		InitializeScoreHistoryMenu();
		_playPauseButton.ToggleMode = true;
		RefreshTransportShortcutTooltips();
		_playPauseButton.Toggled += pressed =>
		{
			if (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true)
			{
				if (!_recordingIntoArrangement) SetRecordingPaused(!pressed);
				else _playPauseButton.SetPressedNoSignal(_soundFontAudio.IsScorePlaying && !_soundFontAudio.IsScorePaused);
				return;
			}
			if (!pressed)
			{
				_soundFontAudio.PauseScore();
				return;
			}

			StartPlayback(fromStart: false);
		};

		stopButton.TooltipText = Tr("Stop playback and return to the selected measure.");
		stopButton.Pressed += () =>
		{
			if (_audioRecorder?.IsTakeActive == true && !_recordingIntoArrangement) FinishAudioRecording();
			else StopPlayback();
		};

	}

	private void RegisterTempoTap()
	{
		var now = System.Diagnostics.Stopwatch.GetTimestamp();
		if (_tempoTapTicks.Count > 0)
		{
			var elapsed = (now - _tempoTapTicks[^1]) / (double)System.Diagnostics.Stopwatch.Frequency;
			if (elapsed <= 0 || elapsed > 2.5) _tempoTapTicks.Clear();
		}
		_tempoTapTicks.Add(now);
		if (_tempoTapTicks.Count > 4) _tempoTapTicks.RemoveAt(0);
		if (_tempoTapTicks.Count < 4) return;

		var seconds = (_tempoTapTicks[^1] - _tempoTapTicks[0]) / (double)System.Diagnostics.Stopwatch.Frequency;
		var bpm = 180.0 / seconds;
		_tempoTapTicks.Clear();
		if (!double.IsFinite(bpm) || bpm <= 0) return;
		_tempoEdit.Text = bpm.ToString("0.##", CultureInfo.CurrentCulture);
		CommitScoreTempo();
	}

	private void StartPlayback(bool fromStart)
	{
		try
		{
			StopArrangement();
			if (!fromStart && _soundFontAudio.IsScorePaused)
				_soundFontAudio.ResumeScore();
			else
			{
				UpdatePlaybackLoopSelection();
				_playbackStartMeasureId = fromStart ? _score.Sections.SelectMany(section => section.Measures).FirstOrDefault()?.Id : _editingMeasureId;
				_soundFontAudio.PlayScore(_score, _playbackStartMeasureId, versionSelection: CapturePlaybackVersions());
				_playbackStartMeasureId = _soundFontAudio.VisualPosition?.MeasureId ?? _playbackStartMeasureId;
			}
			_playPauseButton.SetPressedNoSignal(true);
		}
		catch (Exception exception)
		{
			_playPauseButton.SetPressedNoSignal(false);
			GD.PushError($"Could not start score playback: {exception.Message}");
		}
	}

	private void StopPlayback()
	{
		_soundFontAudio.StopScore();
		_playPauseButton.SetPressedNoSignal(false);
	}

	private sealed record SessionMetadata(string FilePath, bool Unsaved);

	private void BindFileMenu()
	{
		var menuBar = GetNode<MenuBar>("%MenuBar");
		var fileMenu = new PopupMenu { Name = Tr("File") };
		_fileMenu = fileMenu;
		menuBar.AddChild(fileMenu);
		fileMenu.AddItem(Tr("New"), 3);
		fileMenu.AddItem(Tr("Open…"), 0);
		fileMenu.AddItem(Tr("Save"), 1);
		fileMenu.AddItem(Tr("Save As…"), 2);
		_recentMenu = new PopupMenu { Name = "RecentScores" };
		fileMenu.AddChild(_recentMenu);
		fileMenu.AddSubmenuNodeItem(Tr("Open Recent"), _recentMenu, 4);
		fileMenu.IdPressed += id =>
		{
			if (id == 3) RequestNewScore();
		};
		fileMenu.IdPressed += id =>
		{
			if (id == 0) RequestOpen();
			else if (id == 1) SaveCurrentScore();
			else if (id == 2) _filePickerMemory.Show(_saveDialog);
		};
		_recentMenu.AboutToPopup += RebuildRecentMenu;
		_recentMenu.IdPressed += id =>
		{
			var index = (int)id - 100;
			if (index >= 0 && index < _recentScorePaths.Paths.Count) RequestOpenRecent(_recentScorePaths.Paths[index]);
		};
		_openDialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile, Filters = ["*.otab ; OpenTab score"], Title = "Open score" };
		_saveDialog = new FileDialog { Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.SaveFile, Filters = ["*.otab ; OpenTab score"], Title = "Save score" };
		_fileErrorDialog = new AcceptDialog { Title = "File error" };
		_discardChangesDialog = new ConfirmationDialog { Title = "Discard unsaved changes?", DialogText = "The current score has unsaved changes. Continue and discard those changes?", OkButtonText = "Discard and Continue" };
		AddChild(_openDialog); AddChild(_saveDialog); AddChild(_fileErrorDialog); AddChild(_discardChangesDialog);
		_discardChangesDialog.GetOkButton().ThemeTypeVariation = "AppDestructiveButton";
		_discardChangesDialog.GetCancelButton().ThemeTypeVariation = "AppActionButton";
		_filePickerMemory = new FilePickerMemory(_openDialog, _saveDialog);
		_openDialog.FileSelected += OpenScore;
		_saveDialog.FileSelected += SaveScoreAs;
		_discardChangesDialog.Confirmed += () =>
		{
			_discardChangesDialog.Hide();
			var transition = _confirmedFileTransition;
			_confirmedFileTransition = null;
			Callable.From(() => transition?.Invoke()).CallDeferred();
		};
		_discardChangesDialog.Canceled += () => _confirmedFileTransition = null;
		_recentScorePaths = new RecentScorePaths();
	}

	private void RequestOpen()
	{
		RequestTransition(() => _filePickerMemory.Show(_openDialog));
	}

	private void RequestNewScore() => RequestTransition(CreateNewScore);

	private void RequestOpenRecent(string path) => RequestTransition(() => OpenScore(path));

	private void RequestTransition(Action transition)
	{
		if (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true)
		{
			ShowFileError("Stop or cancel the recording before switching projects", new InvalidOperationException("The recording controls belong to the current track."));
			return;
		}
		if (_hasUnsavedChanges)
		{
			_confirmedFileTransition = transition;
			_discardChangesDialog.PopupCentered();
		}
		else transition();
	}

	private void RebuildRecentMenu()
	{
		_recentMenu.Clear();
		var paths = _recentScorePaths.Paths;
		if (paths.Count == 0) { _recentMenu.AddItem("No recent scores", 99); _recentMenu.SetItemDisabled(0, true); return; }
		for (var index = 0; index < paths.Count; index++) _recentMenu.AddItem($"{Path.GetFileName(paths[index])}  —  {paths[index]}", 100 + index);
	}

	private void CreateNewScore()
	{
		SaveEditorWorkspace();
		StopPlayback();
		StopArrangement();
		_score = new ScoreDocument("Untitled", new TempoMark(100m, new RhythmicValue(4)), new TimeSignature(4, 4));
		ResetScoreHistory();
		_currentFilePath = null;
		_hasUnsavedChanges = false;
		RebuildDrumDefinitions();
		RestoreEditorWorkspace(null, loadSavedState: false);
		RenderSection();
		MarkEditorStateDirty();
		_sessionDirty = true;
		SaveSession();
	}

	private void LoadInitialScore()
	{
		_sessionPath = ProjectSettings.GlobalizePath("user://working_session.otab");
		_sessionMetadataPath = ProjectSettings.GlobalizePath("user://working_session.json");
		if (File.Exists(_sessionPath))
		{
			try
			{
				_score = ScoreFile.Load(_sessionPath);
				// Discard the obsolete drum lane from the restored tester session only.
				var legacyTracks = _score.Tracks.Where(t => t.Kind == TrackKind.Percussion && !t.HasConfiguredPercussionKit).ToArray();
				foreach (var legacy in legacyTracks) _score.RemoveTrack(legacy);
				if (File.Exists(_sessionMetadataPath))
				{
					var metadata = JsonSerializer.Deserialize<SessionMetadata>(File.ReadAllText(_sessionMetadataPath));
					_currentFilePath = metadata?.FilePath;
					_hasUnsavedChanges = metadata?.Unsaved ?? true;
				}
				else _hasUnsavedChanges = true;
				if (legacyTracks.Length != 0) { _hasUnsavedChanges = true; _sessionDirty = true; }
				RebuildDrumDefinitions();
				return;
			}
			catch (Exception exception) { GD.PushWarning($"Could not restore the working session: {exception.Message}"); }
		}
		try
		{
			_score = ScoreFile.Deserialize(Godot.FileAccess.GetFileAsBytes(ExamplePath));
			RebuildDrumDefinitions();
		}
		catch (Exception exception)
		{
			_score = new ScoreDocument("Untitled", new TempoMark(100m, new RhythmicValue(4)), new TimeSignature(4, 4));
			RebuildDrumDefinitions();
			ShowFileError("Could not load the example score", exception);
		}
	}

	private void RebuildDrumDefinitions()
	{
		_drumDefinitions.Clear();
		foreach (var track in _score.Tracks.Where(track => track.Kind == TrackKind.Percussion))
		{
			var definition = DrumDefinitions.FirstOrDefault(d => d.Name == track.Name);
			if (definition.Name is null)
			{
				var pitch = _score.Sections.SelectMany(s => s.Measures)
					.Select(m => m.GetTrackMeasure(track.Id)).Where(p => p is not null)
					.SelectMany(p => p.Voices).SelectMany(v => v.Beats).SelectMany(b => b.Notes)
					.Select(n => n.MidiPitch).DefaultIfEmpty(36).First();
				definition = new DrumTrackDefinition(track.Name, pitch, "res://Interface/Common/Graphs/plain-circle_delapouite.png");
			}
			_drumDefinitions.Add(track.Id, definition);
		}
	}

	private void OpenScore(string path)
	{
		if (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true)
		{
			ShowFileError("Stop or cancel the recording before switching projects", new InvalidOperationException("The recording controls belong to the current track."));
			return;
		}
		try
		{
			var loaded = PortableScoreFile.Load(path, ProjectSettings.GlobalizePath("user://audio_cache"));
			SaveEditorWorkspace();
			StopPlayback();
			StopArrangement();
			_score = loaded;
			ResetScoreHistory();
			_currentFilePath = path;
			_hasUnsavedChanges = false;
			RestoreEditorWorkspace(path);
			RebuildDrumDefinitions();
			RenderSection();
			MarkEditorStateDirty();
			_sessionDirty = true;
			SaveSession();
			_recentScorePaths.Record(path);
		}
		catch (Exception exception) { ShowFileError("Could not open the score", exception); }
	}

	private void SaveCurrentScore()
	{
		if (string.IsNullOrEmpty(_currentFilePath)) _filePickerMemory.Show(_saveDialog);
		else SaveScoreAs(_currentFilePath);
	}

	private void SaveScoreAs(string path)
	{
		try
		{
			if (!path.EndsWith(".otab", StringComparison.OrdinalIgnoreCase)) path += ".otab";
			SaveEditorWorkspace();
			PortableScoreFile.Save(path, _score);
			_currentFilePath = path;
			SaveEditorWorkspace();
			_hasUnsavedChanges = false;
			_sessionDirty = true;
			SaveSession();
			_recentScorePaths.Record(path);
		}
		catch (Exception exception) { ShowFileError("Could not save the score", exception); }
	}

	private void SaveSession()
	{
		if (!_sessionDirty || _score is null) return;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_sessionPath)!);
			ScoreFile.Save(_sessionPath, _score);
			File.WriteAllText(_sessionMetadataPath, JsonSerializer.Serialize(new SessionMetadata(_currentFilePath, _hasUnsavedChanges)));
			_sessionDirty = false;
		}
		catch (Exception exception)
		{
			_sessionDirty = false;
			ShowFileError("Could not save the working session", exception);
		}
	}

	private void ShowFileError(string message, Exception exception)
	{
		GD.PushWarning($"{message}: {exception.Message}");
		_fileErrorDialog.DialogText = $"{Tr(message)}:\n{Tr(exception.Message)}";
		_fileErrorDialog.PopupCentered();
	}

	private void AddSection()
	{
		var nameIndex = 0;
		while (_score.Sections.Any(section => section.Name == MakeSectionName(nameIndex))) nameIndex++;
		var sectionName = MakeSectionName(nameIndex);
		var section = _score.AddSection(sectionName, "New Section");
		InitializeMeasureSnapDefaults(section.Measures[0]);
		NotifyScoreEdited();
		_selectedSectionIndex = _score.Sections.Count - 1;
		ResetVisibleSections();
		SetFocusedSection(_score.Sections[^1]);
		RenderSection();
	}

	private void RenderSection()
	{
		_trackSectionInfo.Clear();
		SyncMetronomePlayback();
		RenderSectionNavigation();
		RenderArrangements();
		BeforeEditorRebuild();
		_drumFocusTargets.Clear();
		if (_score.Sections.Count == 0) return;
		var section = GetSelectedSection();
		if (!section.Measures.Any(measure => measure.Id == _editingMeasureId))
			_editingMeasureId = section.Measures.FirstOrDefault()?.Id;
		_cursorTargets.Clear();
		_cursorTabViews.Clear();
		ClearPlaybackFollowTargets();
		RefreshTempoEditFromScore();
		RenderTabSection(section);
		RestoreWorkspaceScroll();
	}

	private void SyncMetronomePlayback()
	{
		if (_score is null) return;
		var enabled = _score.MetronomeEnabled;
		_metronomeButton?.SetPressedNoSignal(enabled);
		if (_soundFontAudio is not null) _soundFontAudio.MetronomeEnabled = enabled;
		if (_arrangementAudio is not null) _arrangementAudio.MetronomeEnabled = enabled;
		SyncCountInPlayback();
	}

	private void SyncCountInPlayback()
	{
		if (_score is null) return;
		_countInButton?.SetPressedNoSignal(_score.CountInEnabled);
		_countInEveryLoopButton?.SetPressedNoSignal(_score.CountInEveryLoop);
		if (_soundFontAudio is not null)
		{
			_soundFontAudio.CountInEnabled = _score.CountInEnabled;
			_soundFontAudio.CountInEveryLoop = _score.CountInEveryLoop;
		}
		if (_arrangementAudio is not null)
		{
			_arrangementAudio.CountInEnabled = _score.CountInEnabled;
			_arrangementAudio.CountInEveryLoop = _score.CountInEveryLoop;
		}
	}

	private void UpdatePercussionMasterVolumeFeedback(double value)
	{
		_percussionMasterVolumeLabel.Text = $"Master Vol.: {value:0.#} dB";
		_percussionMasterVolumeLabel.TooltipText = "Percussion master volume in decibels.";
	}

	private void QueueAlignmentUpdate()
	{
		if (_alignmentUpdateQueued || _synchronizingAlignment || !IsInsideTree())
		{
			return;
		}

		_alignmentUpdateQueued = true;
		Callable.From(SynchronizeTrackHeights).CallDeferred();
	}

	private void SynchronizeTrackHeights()
	{
		_alignmentUpdateQueued = false;
		_synchronizingAlignment = true;
		try
		{
		MatchMinimumHeight(_infoControllers, _beatsGuideHeaders);
		var rowCount = Math.Min(_trackInfoRows.GetChildCount(), _trackBeatsEditors.GetChildCount());
		for (var index = 0; index < rowCount; index++)
		{
			MatchMinimumHeight(_trackInfoRows.GetChild<Control>(index), _trackBeatsEditors.GetChild<Control>(index));
		}
		var naturalWidths = new float[_measureHeaders.GetChildCount()];
		for (var measureIndex = 0; measureIndex < _measureHeaders.GetChildCount(); measureIndex++)
		{
			var header = _measureHeaders.GetChild<Control>(measureIndex);
			header.CustomMinimumSize = new Vector2(0, header.CustomMinimumSize.Y);
			header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
			// Every track in a bar uses the same pixels per whole note. This keeps
			// short cells at their minimum while retaining exact duration proportions.
			var pixelsPerWholeNote = 0.0;
			foreach (var lane in _trackBeatsEditors.GetChildren().OfType<Control>())
			{
				if (measureIndex >= lane.GetChildCount()) continue;
				var group = lane.GetChild<Control>(measureIndex);
				group.CustomMinimumSize = new Vector2(0, group.CustomMinimumSize.Y);
				group.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
				foreach (var cell in group.GetChildren().OfType<Control>())
				{
					cell.CustomMinimumSize = new Vector2(76, cell.CustomMinimumSize.Y);
					var duration = cell.GetMeta("duration").AsDouble();
					if (duration > 0)
						pixelsPerWholeNote = Math.Max(pixelsPerWholeNote, Math.Max(76, cell.GetCombinedMinimumSize().X) / duration);
				}
			}
			foreach (var lane in _trackBeatsEditors.GetChildren().OfType<Control>())
			{
				if (measureIndex >= lane.GetChildCount()) continue;
				var group = lane.GetChild<Control>(measureIndex);
				foreach (var cell in group.GetChildren().OfType<Control>())
				{
					var duration = cell.GetMeta("duration").AsDouble();
					var cellWidth = (float)Math.Ceiling(duration * pixelsPerWholeNote);
					if (cell.CustomMinimumSize.X != cellWidth)
						cell.CustomMinimumSize = new Vector2(cellWidth, cell.CustomMinimumSize.Y);
				}
			}
			var width = header.GetCombinedMinimumSize().X;
			foreach (var lane in _trackBeatsEditors.GetChildren().OfType<Control>())
			{
				if (measureIndex < lane.GetChildCount())
					width = Math.Max(width, lane.GetChild<Control>(measureIndex).GetCombinedMinimumSize().X);
			}
			naturalWidths[measureIndex] = width;
		}
		var naturalTotal = naturalWidths.Sum();
		var availableWidth = Math.Max(0, _beatsScrollContainer.Size.X);
		var extraPerMeasure = naturalWidths.Length > 0 && naturalTotal < availableWidth
			? (availableWidth - naturalTotal) / naturalWidths.Length
			: 0;
		for (var measureIndex = 0; measureIndex < naturalWidths.Length; measureIndex++)
		{
			var width = naturalWidths[measureIndex] + extraPerMeasure;
			var header = _measureHeaders.GetChild<Control>(measureIndex);
			if (header.CustomMinimumSize.X != width)
				header.CustomMinimumSize = new Vector2(width, header.CustomMinimumSize.Y);
			foreach (var lane in _trackBeatsEditors.GetChildren().OfType<Control>())
			{
				if (measureIndex < lane.GetChildCount())
				{
					var group = lane.GetChild<Control>(measureIndex);
					if (group.CustomMinimumSize.X != width)
						group.CustomMinimumSize = new Vector2(width, group.CustomMinimumSize.Y);
				}
			}
		}
		}
		finally
		{
			_synchronizingAlignment = false;
		}
	}

	private static void MatchMinimumHeight(Control left, Control right)
	{
		var height = Math.Max(left.GetCombinedMinimumSize().Y, right.GetCombinedMinimumSize().Y);
		if (left.CustomMinimumSize.Y != height)
		{
			left.CustomMinimumSize = new Vector2(left.CustomMinimumSize.X, height);
		}
		if (right.CustomMinimumSize.Y != height)
		{
			right.CustomMinimumSize = new Vector2(right.CustomMinimumSize.X, height);
		}
	}

	private void RenderMeasureHeader(ScoreSection section, ScoreMeasure measure, int measureIndex)
	{
		var header = _measureHeaderScene.Instantiate<Control>();
		header.Name = $"MeasureHeader_{section.Name}_{measureIndex + 1}";
		header.SizeFlagsHorizontal = Control.SizeFlags.Fill;
		_measureHeaders.AddChild(header);
		RegisterCursorTarget(measure.Id, header, drum: true);
		if (measureIndex == 0)
		{
			var headerContent = header.GetNode<HBoxContainer>("HeaderContent");
			var sectionLabel = new Label
			{
				Text = $"{section.Name} · {section.Title}",
				TooltipText = string.Format(Tr("Section {0} · {1}"), section.Name, section.Title),
				VerticalAlignment = VerticalAlignment.Center,
				CustomMinimumSize = new Vector2(110, 0)
			};
			headerContent.AddChild(sectionLabel);
			headerContent.MoveChild(sectionLabel, 0);
		}

		var signature = _score.ResolveTimeSignature(section, measure);
		var selector = header.GetNode<OptionButton>("HeaderContent/TimeSignatureSelector");
		var validation = header.GetNode<RichTextLabel>("HeaderContent/ValidationInfo");
		var addMeasureButton = header.GetNode<Button>("HeaderContent/AddMeasureButton");
		var deleteMeasureButton = header.GetNode<Button>("HeaderContent/DeleteMeasureButton");
		var confirmation = header.GetNode<ConfirmationDialog>("DeleteMeasureConfirmation");
		BindMeasurePlacementActions(header, section, measureIndex);
		confirmation.GetOkButton().ThemeTypeVariation = "BeatRemoveButton";
		confirmation.GetCancelButton().ThemeTypeVariation = "DrumActionButton";
		selector.Select(FindSignatureChoice(signature));
		selector.TooltipText = string.Format(Tr("Section {0}: measure {1} time signature"), section.Name, measureIndex + 1);
		addMeasureButton.Pressed += () =>
		{
			_score.InsertMeasureAfter(section, measureIndex);
			NotifyScoreEdited();
			RenderSection();
		};
		confirmation.DialogText = string.Format(Tr("Delete measure {0} from section {1}?\nThis removes its content from all tracks."), measureIndex + 1, section.Name);
		deleteMeasureButton.Pressed += () => confirmation.PopupCentered();
		confirmation.Confirmed += () =>
		{
			_score.RemoveMeasure(section, measure);
			NotifyScoreEdited();
			RenderSection();
		};
		selector.ItemSelected += selectedIndex =>
		{
			var selectedSignature = TimeSignatureChoices[(int)selectedIndex];
			var previousSignature = _score.ResolveTimeSignature(section, measure);
			if (selectedSignature == previousSignature)
			{
				return;
			}

			measure.TimeSignatureOverride = selectedSignature;
			NotifyScoreEdited();
			RenderSection();
		};

		RefreshMeasureValidation(section, measure, measureIndex, validation);
	}

	private void RenderTrackInfo(ScoreSection section, ScoreTrack track, DrumTrackDefinition definition)
	{
		var row = _trackRowScene.Instantiate<Control>();
		row.Name = $"Track_{track.Name.Replace(' ', '_')}";
		row.CustomMinimumSize = new Vector2(0, 100);
		_trackInfoRows.AddChild(row);
		row.MinimumSizeChanged += QueueAlignmentUpdate;

		var icon = row.GetNode<TextureRect>("Row/InstrumentIcon");
		icon.Texture = GD.Load<Texture2D>(definition.IconPath);
		var name = row.GetNode<RichTextLabel>("Row/TrackName");
		name.Text = $"[b]{track.Name}[/b]\n{string.Format(Tr("GM note {0}"), definition.MidiPitch)}";

		var lead = row.GetNode<Button>("Row/AudioControls/SectionControls/LeadButton");
		var isLead = section.LeadTrackId == track.Id;
		lead.Text = Tr(isLead ? "Lead" : "Set lead");
		lead.ToggleMode = true;
		lead.SetPressedNoSignal(isLead);
		lead.ThemeTypeVariation = isLead ? "AppLeadActiveButton" : "AppLeadButton";
		lead.Disabled = isLead;
		lead.Pressed += () =>
		{
			var focused = GetSelectedSection();
			_score.SetLeadTrack(focused, track);
			NotifyScoreEdited();
			RenderSection();
		};
		var patternLength = row.GetNode<SpinBox>("Row/AudioControls/SectionControls/PatternLength");
		patternLength.MinValue = section.Measures.Count == 0 ? 0 : 1;
		patternLength.MaxValue = section.Measures.Count;
		patternLength.Value = _score.GetTrackPatternLength(section, track);
		patternLength.Suffix = Tr(" bars");
		patternLength.Editable = !isLead && section.Measures.Count > 0;
		patternLength.TooltipText = string.Format(Tr("Focused section {0} · {1}: {2}"), section.Name, section.Title, Tr(isLead ? "the lead track determines section length." : "pattern length in measures; shorter patterns repeat."));
		lead.TooltipText = isLead
			? string.Format(Tr("Lead for focused section {0} · {1}; it determines this section's length."), section.Name, section.Title)
			: string.Format(Tr("Make this track lead for focused section {0} · {1}."), section.Name, section.Title);
		_percussionSectionControls.Add((track, lead, patternLength));
		patternLength.ValueChanged += value =>
		{
			if (_refreshingPercussionSectionControls) return;
			var focused = GetSelectedSection();
			if (focused.Measures.Count == 0 || focused.LeadTrackId == track.Id) return;
			_score.SetTrackPatternLength(focused, track, (int)value);
			NotifyScoreEdited();
			RenderSection();
		};

		var volume = row.GetNode<HSlider>("Row/AudioControls/TrackVolume");
		var volumeFeedback = row.GetNode<Label>("Row/AudioControls/LowerLayout/VolumeFeedbackLabel");
		volume.MinValue = -80;
		volume.MaxValue = 20;
		volume.Step = 1;
		volume.Value = track.VolumeDb;
		volume.TooltipText = "Track volume in decibels.";
		volumeFeedback.Text = $"{volume.Value:0.#} dB";
		volume.ValueChanged += value =>
		{
			track.VolumeDb = value;
			volumeFeedback.Text = $"{value:0.#} dB";
			NotifyScoreEdited();
		};

		var mute = row.GetNode<Button>("Row/AudioControls/LowerLayout/MuteButton");
		mute.ToggleMode = true;
		mute.ButtonPressed = track.IsMuted;
		mute.Toggled += pressed => { track.IsMuted = pressed; NotifyScoreEdited(); };

		var solo = row.GetNode<Button>("Row/AudioControls/LowerLayout/SoloButton");
		solo.ToggleMode = true;
		solo.ButtonPressed = track.IsSoloed;
		solo.Toggled += pressed => { track.IsSoloed = pressed; NotifyScoreEdited(); };
	}

	private void RenderTrackGrid(ScoreSection section, ScoreTrack track, DrumTrackDefinition definition)
		=> RenderTrackGrid(new[] { section }, track, definition);

	private void RenderTrackGrid(IReadOnlyList<ScoreSection> visibleSections, ScoreTrack track, DrumTrackDefinition definition)
	{
		var lane = new HBoxContainer
		{
			Name = $"Lane_{track.Name.Replace(' ', '_')}",
			CustomMinimumSize = new Vector2(0, 100),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		_trackBeatsEditors.AddChild(lane);
		lane.AddThemeConstantOverride("separation", 0);
		lane.MinimumSizeChanged += QueueAlignmentUpdate;

		foreach (var section in visibleSections)
		{
			var patternLength = _score.GetTrackPatternLength(section, track);
			for (var measureIndex = 0; measureIndex < section.Measures.Count; measureIndex++)
			{
				var measureGroup = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill };
				measureGroup.AddThemeConstantOverride("separation", 0);
				lane.AddChild(measureGroup);
				var destinationMeasure = section.Measures[measureIndex];
				RegisterCursorTarget(destinationMeasure.Id, measureGroup, drum: true);
				if (patternLength <= 0) continue;
				var sourceIndex = measureIndex % patternLength;
				var measure = section.Measures[sourceIndex];
				var trackMeasure = measure.GetTrackMeasure(track.Id);
				if (trackMeasure is null || trackMeasure.Voices.Count == 0)
				{
					GD.PushWarning($"Measure {measureIndex + 1} is missing a voice for track {track.Name}.");
					continue;
				}

				var voice = trackMeasure.Voices[0];
				for (var beatIndex = 0; beatIndex < voice.Beats.Count; beatIndex++)
				{
					var beat = voice.Beats[beatIndex];
					var cell = _beatEditorScene.Instantiate<Control>();
					cell.Name = $"{section.Name}_M{measureIndex + 1}Beat{beatIndex + 1}";
					cell.CustomMinimumSize = new Vector2(76, 0);
					cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
					SetBeatCellDuration(cell, beat.Duration);
					measureGroup.AddChild(cell);
					RegisterDrumFocusTarget(track.Id, destinationMeasure.Id, beat.Id, cell);
					if (sourceIndex != measureIndex)
					{
						cell.Modulate = HasThemeColor("repeated_beat_modulate", "AppIndicators")
							? GetThemeColor("repeated_beat_modulate", "AppIndicators") : new Color(0.75f, 0.75f, 0.75f, 1f);
						cell.TooltipText = string.Format(Tr("Section {0}: repeats measure {1}; edits change the source pattern."), section.Name, sourceIndex + 1);
					}
					BindBeatEditor(cell, voice, beat, definition, sourceIndex, beatIndex, sourceIndex != measureIndex);
				}

				var signature = _score.ResolveTimeSignature(section, destinationMeasure);
				var total = voice.Beats.Aggregate(ScoreTime.Zero, (sum, beat) => sum + beat.Duration);
				if (!destinationMeasure.IsFreeTime && !destinationMeasure.IsPickup && total < signature.BarDuration)
					AddBeatPlaceholder(measureGroup, section, destinationMeasure, voice, signature, measureIndex, signature.BarDuration - total);
			}
		}
	}

	private static void SetBeatCellDuration(Control cell, ScoreTime duration)
	{
		var value = (double)duration.Numerator / duration.Denominator;
		cell.SetMeta("duration", value);
		cell.SizeFlagsStretchRatio = (float)value;
	}

	private void AddBeatPlaceholder(HBoxContainer group, ScoreSection section, ScoreMeasure measure,
		Voice voice, TimeSignature signature, int measureIndex, ScoreTime remaining)
	{
		var cell = _beatEditorScene.Instantiate<Control>();
		cell.Name = $"M{measureIndex + 1}Placeholder";
		cell.CustomMinimumSize = new Vector2(76, 0);
		cell.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
		SetBeatCellDuration(cell, remaining);
		cell.GetNode<Control>("VBoxContainer").Visible = false;
		cell.GetNode<Button>("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/RemoveThisBeatButton").Visible = false;
		var realize = cell.GetNode<Button>("RealizeBeatButton");
		realize.Visible = true;
		realize.TooltipText = string.Format(Tr("Add a beat in the remaining {0} whole notes. Normal click adds it silently; Ctrl-click copies the last beat's full state when it fits. Empty voices use the normal value."), remaining);
		var candidate = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, signature,
			defaultIntensity: TrackDefaults.IntensityFor(TrackKind.Percussion));
		realize.Disabled = candidate is null;
		if (candidate is null) realize.TooltipText = Tr("The remaining duration cannot be represented by a supported beat.");
		realize.Pressed += () =>
		{
			var next = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice,
				_score.ResolveTimeSignature(section, measure), Input.IsKeyPressed(Key.Ctrl),
				TrackDefaults.IntensityFor(TrackKind.Percussion));
			if (next is null) return;
			voice.Beats.Add(next);
			NotifyScoreEdited();
			RenderSection();
		};
		group.AddChild(cell);
	}

	private void BindBeatEditor(
		Control cell,
		Voice voice,
		Beat beat,
		DrumTrackDefinition definition,
		int measureIndex,
		int beatIndex,
		bool isRepeat)
	{
		var hitButton = cell.GetNode<Button>("VBoxContainer/HBoxContainer/ControlSection/OnOffButton");
		var removeButton = cell.GetNode<Button>("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/RemoveThisBeatButton");
		var dottedButton = cell.GetNode<Button>("VBoxContainer/HBoxContainer/ControlSection/HBoxContainer/DottetNoteButton");
		cell.GetNode<Button>("RealizeBeatButton").Visible = false;
		removeButton.Visible = true;
		removeButton.TooltipText = Tr("Remove this beat.");
		removeButton.Pressed += () =>
		{
			if (!voice.Beats.Remove(beat)) return;
			NotifyScoreEdited();
			RenderSection();
		};
		dottedButton.ToggleMode = true;
		dottedButton.SetPressedNoSignal(beat.RhythmicValue.Dots > 0);
		dottedButton.TooltipText = Tr("Toggle a dotted note (adds half its base duration).");
		dottedButton.Toggled += pressed =>
		{
			var value = beat.RhythmicValue;
			beat.RhythmicValue = new RhythmicValue(value.NoteDenominator, pressed ? 1 : 0, value.TupletActual, value.TupletNormal);
			NotifyScoreEdited();
			RenderSection();
		};
		var intensityContainer = cell.GetNode<VBoxContainer>("VBoxContainer/HBoxContainer/SectionedVolume");
		hitButton.ToggleMode = true;
		hitButton.ButtonPressed = beat.Notes.Any(note => note.MidiPitch == definition.MidiPitch);
		hitButton.Text = string.Empty;
		hitButton.Icon = LoadBeatStateTexture(beat.RhythmicValue.NoteDenominator, hitButton.ButtonPressed);
		hitButton.TooltipText = string.Format(Tr("Toggle {0} at measure {1}, beat {2}."), definition.Name, measureIndex + 1, beatIndex + 1) +
			(isRepeat ? Tr(" This is a linked repetition; edits change the source pattern.") : "");
		hitButton.Toggled += pressed =>
		{
			beat.Notes.RemoveAll(note => note.MidiPitch == definition.MidiPitch);
			if (pressed)
			{
				beat.AddNote(new ScoreNote(definition.MidiPitch, 1));
			}

			hitButton.Icon = LoadBeatStateTexture(beat.RhythmicValue.NoteDenominator, pressed);
			NotifyScoreEdited();
			RenderSection();
		};

		var intensityButtons = Enumerable.Range(1, 5)
			.Select(step => intensityContainer.GetNode<Button>($"Volume{step}"))
			.ToArray();
		for (var step = 1; step <= intensityButtons.Length; step++)
		{
			var button = intensityButtons[step - 1];
			button.ToggleMode = true;
			var selectedIntensity = intensityButtons.Length - step + 1;
			button.ButtonPressed = selectedIntensity <= beat.Intensity;
			button.TooltipText = string.Format(Tr("Set execution intensity to {0} of 5 (MIDI velocity {1})."), selectedIntensity, Beat.MidiVelocityForIntensity(selectedIntensity));
			button.Pressed += () =>
			{
				beat.Intensity = selectedIntensity;
				NotifyScoreEdited();
				RenderSection();
			};
		}

		var rhythmMinusButton = cell.GetNode<Button>("VBoxContainer/BeatTempoContainer/BeatTimeValueMinusButton");
		var rhythmFeedback = cell.GetNode<Label>("VBoxContainer/BeatTempoContainer/BeatTimeValueFeedback");
		var rhythmPlusButton = cell.GetNode<Button>("VBoxContainer/BeatTempoContainer/BeatTimeValuePlusButton");
		var rhythmIndex = Array.IndexOf(NoteDenominators, beat.RhythmicValue.NoteDenominator);
		rhythmIndex = Math.Max(0, rhythmIndex);
		rhythmFeedback.Text = $"{beat.RhythmicValue.NoteDenominator}{new string('.', beat.RhythmicValue.Dots)}";
		rhythmFeedback.TooltipText = string.Format(Tr("Measure {0}, beat {1} rhythmic value."), measureIndex + 1, beatIndex + 1);
		rhythmMinusButton.TooltipText = Tr("Select a longer note value.");
		rhythmPlusButton.TooltipText = Tr("Select a shorter note value.");

		void UpdateRhythmControls()
		{
			rhythmFeedback.Text = $"{NoteDenominators[rhythmIndex]}{new string('.', beat.RhythmicValue.Dots)}";
			rhythmMinusButton.Disabled = rhythmIndex == 0;
			rhythmPlusButton.Disabled = rhythmIndex == NoteDenominators.Length - 1;
		}

		void ChangeRhythmicValue(int step)
		{
			var nextIndex = Math.Clamp(rhythmIndex + step, 0, NoteDenominators.Length - 1);
			if (nextIndex == rhythmIndex)
			{
				return;
			}

			var currentValue = beat.RhythmicValue;
			rhythmIndex = nextIndex;
			beat.RhythmicValue = new RhythmicValue(
				NoteDenominators[rhythmIndex],
				currentValue.Dots,
				currentValue.TupletActual,
				currentValue.TupletNormal);
			NotifyScoreEdited();
			RenderSection();
		}

		rhythmMinusButton.Pressed += () => ChangeRhythmicValue(-1);
		rhythmPlusButton.Pressed += () => ChangeRhythmicValue(1);
		UpdateRhythmControls();
	}

	private static Texture2D LoadBeatStateTexture(int noteDenominator, bool isHit)
	{
		var index = Array.IndexOf(NoteDenominators, noteDenominator);
		if (index < 0)
			throw new ArgumentOutOfRangeException(nameof(noteDenominator), noteDenominator, "Unsupported beat note denominator.");

		var suffix = isHit ? string.Empty : "-rest";
		return GD.Load<Texture2D>($"res://Interface/Common/Graphs/note-values/{NoteValueNames[index]}{suffix}.svg");
	}

	private void NotifyScoreEdited(bool refreshPlayback = true)
	{
		RefreshTrackTitles();
		foreach (var track in _score.Tracks) _trackBanners.RefreshTrack(track);
		QueueFoldedTrackPreviewRefresh();
		RecordScoreHistory();
		MarkEditorStateDirty();
		_hasUnsavedChanges = true;
		_sessionDirty = true;
		_sessionDelay = 0.5;
		if (!refreshPlayback) return;
		try
		{
			_soundFontAudio.RefreshScore(_score, CapturePlaybackVersions());
			_arrangementAudio?.RefreshScore(_score);
		}
		catch (Exception exception)
		{
			GD.PushWarning($"Could not update playback after the edit: {exception.Message}");
		}
	}

	private bool CommitScoreTempo()
	{
		if (_score is null || _tempoEdit is null) return false;
		var raw = _tempoEdit.Text.Trim();
		if (string.Equals(raw, _tempoEditBaseline, StringComparison.Ordinal))
		{
			ClearTempoInputError();
			return true;
		}

		if (!TryParseTempo(raw, out var quarterNotesPerMinute) || quarterNotesPerMinute <= 0)
		{
			const string error = "Enter a positive decimal quarter-note BPM value.";
			SetTempoInputError(error);
			return false;
		}

		try
		{
			var currentQuarterTempo = _score.DefaultTempo.QuarterNotesPerMinute;
			if (quarterNotesPerMinute != currentQuarterTempo)
			{
				_score.DefaultTempo = TempoMark.FromQuarterNotesPerMinute(quarterNotesPerMinute, _score.DefaultTempo.BeatUnit);
				NotifyScoreEdited();
			}
			ClearTempoInputError();
			RefreshTempoEditFromScore(force: true);
			RefreshSectionTempoStatus();
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			const string error = "That tempo is outside the supported decimal range.";
			SetTempoInputError(error);
			return false;
		}
	}

	private void RefreshTempoEditFromScore(bool force = false)
	{
		if (_tempoEdit is null || _score is null || (!force && _tempoEdit.HasFocus())) return;
		var text = FormatTempo(_score.DefaultTempo.QuarterNotesPerMinute);
		_tempoEdit.Text = text;
		_tempoEditBaseline = text;
	}

	private void RefreshSectionTempoStatus()
	{
		if (_score is null || _score.Sections.Count == 0 || _sectionInformation is null) return;
		var section = GetSelectedSection();
		var muted = GetThemeColor("muted", "AppStatus").ToHtml(false);
		_sectionInformation.Text = string.Join("   │   ", GetVisibleSections().Select(visible =>
		{
			var tempo = visible.DefaultTempo ?? _score.DefaultTempo;
			var source = visible.DefaultTempo is null ? Tr("score default") : Tr("section override");
			var focused = visible.Id == section.Id ? "▶ " : string.Empty;
			return $"[b]{focused}{visible.Name} · {visible.Title}[/b]   [color=#{muted}]{FormatTempo(tempo.QuarterNotesPerMinute)} BPM · {source}[/color]";
		}));
	}

	private void UpdatePercussionFocusedSectionControls()
	{
		RefreshFocusedPercussionControls(force: true);
	}

	private void RefreshFocusedPercussionControls(bool force = false)
	{
		if (_score is null || _score.Sections.Count == 0 || _percussionSectionControls.Count == 0) return;
		var section = GetSelectedSection();
		if (!force && _percussionControlsSectionId == section.Id) return;
		_percussionControlsSectionId = section.Id;
		_refreshingPercussionSectionControls = true;
		try
		{
		foreach (var (track, lead, patternLength) in _percussionSectionControls)
		{
			if (!GodotObject.IsInstanceValid(lead) || !GodotObject.IsInstanceValid(patternLength)) continue;
			var isLead = section.LeadTrackId == track.Id;
			lead.Text = Tr(isLead ? "Lead" : "Set lead");
			lead.ToggleMode = true;
			lead.SetPressedNoSignal(isLead);
			lead.ThemeTypeVariation = isLead ? "AppLeadActiveButton" : "AppLeadButton";
			lead.Disabled = isLead;
				lead.TooltipText = isLead
				? string.Format(Tr("Lead for focused section {0} · {1}; it determines this section's length."), section.Name, section.Title)
				: string.Format(Tr("Make this track lead for focused section {0} · {1}."), section.Name, section.Title);
			patternLength.MinValue = section.Measures.Count == 0 ? 0 : 1;
			patternLength.MaxValue = section.Measures.Count;
			patternLength.SetValueNoSignal(_score.GetTrackPatternLength(section, track));
			patternLength.Editable = !isLead && section.Measures.Count > 0;
			patternLength.TooltipText = string.Format(Tr("Focused section {0} · {1}: {2}"), section.Name, section.Title,
				Tr(isLead ? "the lead track determines section length." : "pattern length in measures; shorter patterns repeat."));
		}
		}
		finally
		{
			_refreshingPercussionSectionControls = false;
		}
	}

	private static string FormatTempo(decimal value) => value.ToString("0.############################", CultureInfo.CurrentCulture);

	private static bool TryParseTempo(string text, out decimal value)
	{
		const NumberStyles style = NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign;
		return decimal.TryParse(text, style, CultureInfo.CurrentCulture, out value)
			|| decimal.TryParse(text, style, CultureInfo.InvariantCulture, out value);
	}

	private void SetTempoInputError(string message, bool highlight = true)
	{
		_tempoInputError = message;
		_tempoEdit.TooltipText = Tr(message);
		if (highlight) _tempoEdit.AddThemeColorOverride("font_color", GetThemeColor("error", "AppStatus"));
		else _tempoEdit.RemoveThemeColorOverride("font_color");
	}

	private void ClearTempoInputError()
	{
		_tempoEdit.TooltipText = Tr("Score default tempo in quarter-note BPM. Section defaults and measure tempo events override it at their positions.");
		_tempoInputError = string.Empty;
		_tempoEdit.RemoveThemeColorOverride("font_color");
	}

	private void RefreshMeasureValidation(ScoreSection section, ScoreMeasure measure, int measureIndex, RichTextLabel validation)
	{
		var signature = _score.ResolveTimeSignature(section, measure);
		var issues = MeasureValidator.Validate(measure, signature, _score.Tracks);
		var sectionName = SectionArrangementSymbol(section);
		var context = string.Format(Tr("{0} · M{1}"), sectionName.Replace("[", "[lb]"), measureIndex + 1);
		validation.TooltipText = string.Format(Tr("Measure validation for section {0}, measure {1}."), sectionName, measureIndex + 1);
		if (issues.Count == 0)
		{
			validation.Text = $"{context} · [color=#{GetThemeColor("complete", "AppStatus").ToHtml(false)}]{Tr("Complete")}[/color]";
			return;
		}

		var issue = issues[0];
		var fractionOfBar = issue.ActualDuration / issue.ExpectedDuration;
		var status = Tr(issue.Status == VoiceDurationStatus.Incomplete ? "Incomplete" : "Overfull");
		validation.Text = $"{context} · [color=#{GetThemeColor("warning", "AppStatus").ToHtml(false)}]{status}: {fractionOfBar}[/color]";
	}

	private ScoreSection GetSelectedSection() => _score.Sections[_selectedSectionIndex];

	private static int FindSignatureChoice(TimeSignature signature)
	{
		var index = Array.IndexOf(TimeSignatureChoices, signature);
		return Math.Max(0, index);
	}

	private static string MakeSectionName(int index)
	{
		var letters = string.Empty;
		for (var value = index + 1; value > 0; value = (value - 1) / 26)
		{
			letters = (char)('A' + ((value - 1) % 26)) + letters;
		}

		return letters;
	}

	private static void ClearChildren(Node parent)
	{
		foreach (var child in parent.GetChildren())
		{
			parent.RemoveChild(child);
			child.QueueFree();
		}
	}

	private readonly record struct DrumTrackDefinition(string Name, int MidiPitch, string IconPath);
}

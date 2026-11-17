using System;
using System.Linq;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;

public partial class DrumScorePresenter
{
	private Button _loopAllButton = null!;
	private Button _loopMeasureButton = null!;
	private Button _loopSectionsButton = null!;
	private PlaybackLoopMode _playbackLoopMode = PlaybackLoopMode.Score;
	private PlaybackLoopMode? _appliedLoopMode;
	private Guid? _appliedLoopMeasureId;
	private Guid[] _appliedLoopSectionIds = Array.Empty<Guid>();
	private bool _refreshingLoopButtons;

	private void InitializePlaybackLoopControls()
	{
		
		_loopAllButton = GetNode<Button>("%LoopAllButton");
		_loopMeasureButton = GetNode<Button>("%LoopMeasureButton");
		_loopSectionsButton = GetNode<Button>("%LoopSectionsButton");
		foreach (var button in new[] { _loopAllButton, _loopMeasureButton, _loopSectionsButton })
			button.ToggleMode = true;

		RefreshPlaybackLoopTooltips();
		_loopAllButton.Toggled += pressed => OnLoopButtonToggled(PlaybackLoopMode.Score, pressed);
		_loopMeasureButton.Toggled += pressed => OnLoopButtonToggled(PlaybackLoopMode.Measure, pressed);
		_loopSectionsButton.Toggled += pressed => OnLoopButtonToggled(PlaybackLoopMode.Sections, pressed);
		RefreshPlaybackLoopButtons();
	}

	private void RefreshPlaybackLoopTooltips()
	{
		if (_loopAllButton is null || !GodotObject.IsInstanceValid(_loopAllButton)) return;
		_loopAllButton.TooltipText = string.Format(Tr("Repeat the full score until stopped. Shortcut: {0}."), AppPreferences.FormatBinding("repeat_score"));
		_loopMeasureButton.TooltipText = string.Format(Tr("Repeat the selected measure until stopped. Shortcut: {0}."), AppPreferences.FormatBinding("repeat_measure"));
		_loopSectionsButton.TooltipText = string.Format(Tr("Repeat the selected sections in score order until stopped. Shortcut: {0}."), AppPreferences.FormatBinding("repeat_sections"));
	}

	private bool SelectPlaybackLoopMode(PlaybackLoopMode mode)
	{
		if ((_audioRecordingStarting || _audioRecorder?.IsTakeActive == true) ||
			(_arrangementAudio is not null && _arrangementAudio.IsScorePlaying)) return false;
		if (mode == PlaybackLoopMode.Measure && (_loopMeasureButton is null || _loopMeasureButton.Disabled)) return false;
		if (mode == PlaybackLoopMode.Sections && (_loopSectionsButton is null || _loopSectionsButton.Disabled)) return false;

		_playbackLoopMode = mode;
		MarkEditorStateDirty();
		RefreshPlaybackLoopButtons();
		UpdatePlaybackLoopSelection();
		return true;
	}

	private void OnLoopButtonToggled(PlaybackLoopMode mode, bool pressed)
	{
		if (_refreshingLoopButtons)
			return;

		_playbackLoopMode = pressed ? mode : _playbackLoopMode == mode ? PlaybackLoopMode.None : _playbackLoopMode;
		MarkEditorStateDirty();
		RefreshPlaybackLoopButtons();
		UpdatePlaybackLoopSelection();
	}

	private void RefreshPlaybackLoopButtons()
	{
		if (_loopAllButton is null || !GodotObject.IsInstanceValid(_loopAllButton))
			return;

		var hasMeasure = _score is not null && _editingMeasureId is Guid measureId &&
			_score.Sections.Any(section => section.Measures.Any(measure => measure.Id == measureId));
		var visibleSections = _score is null ? Array.Empty<ScoreSection>() : GetVisibleSections().ToArray();
		var hasSections = visibleSections.Any(section => section.Measures.Count > 0);
		_loopMeasureButton.Disabled = !hasMeasure;
		_loopSectionsButton.Disabled = !hasSections;

		if (_playbackLoopMode == PlaybackLoopMode.Measure && !hasMeasure ||
			_playbackLoopMode == PlaybackLoopMode.Sections && !hasSections)
		{
			if (_soundFontAudio is not null && (_soundFontAudio.IsScorePlaying || _soundFontAudio.IsScorePaused))
				StopPlayback();
			_playbackLoopMode = PlaybackLoopMode.None;
			MarkEditorStateDirty();
		}

		_refreshingLoopButtons = true;
		_loopAllButton.SetPressedNoSignal(_playbackLoopMode == PlaybackLoopMode.Score);
		_loopMeasureButton.SetPressedNoSignal(_playbackLoopMode == PlaybackLoopMode.Measure);
		_loopSectionsButton.SetPressedNoSignal(_playbackLoopMode == PlaybackLoopMode.Sections);
		_refreshingLoopButtons = false;
	}

	private void UpdatePlaybackLoopSelection()
	{
		if (_score is null || _soundFontAudio is null ||
			_loopAllButton is null || !GodotObject.IsInstanceValid(_loopAllButton))
			return;
		// Keep the transport used by a take stable; apply changed selections after it stops.
		if ((_audioRecordingStarting || _audioRecorder?.IsTakeActive == true) && _soundFontAudio.IsScorePlaying)
			return;

		RefreshPlaybackLoopButtons();
		Guid? measureId = _playbackLoopMode == PlaybackLoopMode.Measure ? _editingMeasureId : null;
		var sectionIds = _playbackLoopMode == PlaybackLoopMode.Sections
			? GetVisibleSections().Select(section => section.Id).ToArray()
			: Array.Empty<Guid>();
		if (_appliedLoopMode == _playbackLoopMode && _appliedLoopMeasureId == measureId &&
			_appliedLoopSectionIds.SequenceEqual(sectionIds))
			return;

		_soundFontAudio.SetLoopSelection(_score,
			new PlaybackLoopSelection(_playbackLoopMode, measureId, sectionIds));
		if (_soundFontAudio.IsScorePlaying || _soundFontAudio.IsScorePaused)
			_playbackStartMeasureId = _soundFontAudio.VisualPosition?.MeasureId ?? _playbackStartMeasureId;
		_appliedLoopMode = _playbackLoopMode;
		_appliedLoopMeasureId = measureId;
		_appliedLoopSectionIds = sectionIds;
	}
}

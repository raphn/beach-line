#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;
using BeachLine.Music.Audio;

// Audio and audio-only ruler UI. This deliberately does not route audio tracks
// through notation or instrument settings/rendering.
public partial class DrumScorePresenter
{
    private readonly PackedScene _audioToolbarScene = GD.Load<PackedScene>("res://Interface/Audio/Scenes/audio_track_toolbar.tscn");
    private readonly PackedScene _audioClipControlsScene = GD.Load<PackedScene>("res://Interface/Audio/Scenes/audio_clip_controls.tscn");
    private readonly PackedScene _audioRulerScene = GD.Load<PackedScene>("res://Interface/Audio/Scenes/audio_only_ruler.tscn");
    private readonly PackedScene _audioTimelineShellScene = GD.Load<PackedScene>("res://Interface/Audio/Scenes/audio_timeline_shell.tscn");
    private FileDialog? _audioImportDialog;
    private ScoreDocument? _audioImportScore;
    private ScoreDocument? _recordingScore;
    private readonly Dictionary<Guid, AudioTrackUiState> _audioTrackUi = new();
    private readonly Dictionary<Guid, (Button Stop, Button Cancel)> _recordButtons = new();
    private readonly List<AudioPreviewBinding> _audioPreviewBindings = new();
    private AudioWaveformPreview? _recordingPreview;
    private sealed record AudioPreviewBinding(AudioWaveformPreview Preview, Guid ClipId,
        long SourceOffsetFrames, long DurationFrames, int AssetSampleRate);
    private sealed class AudioTrackUiState
    {
        public double StartQuarter;
        public double LatencyMs;
        public int InputDevice;
        public int Channels = 1;
        public bool ShowGuides = true;
        public bool ScissorsEnabled;
    }
    private Label? _audioUiStatus;
    private string _audioUiStatusSource = "WAV clips play at fixed speed.";
    private MicrophoneWavRecorder? _audioRecorder;
    private string? _recordingPath;
    private Guid? _recordingTrackId;
    private bool _recordingIntoArrangement;
    private ScoreTime _recordingStart;
    private Guid? _recordingSectionId;
    private Guid? _recordingArrangementId;
    private bool _recordingStartCancelled;
    private bool _audioRecordingStarting;
    private TaskCompletionSource<bool>? _recordingReadyWait;
    private void BindAudioTrackEditor()
    {
        // Track creation is global; source and recording actions live on each track row.
        var create = GetNode<Button>("%AddAudioTrackButton");
        create.TooltipText = AudioTranslations.Translate("Add a track for imported WAV audio.");
        create.Pressed += CreateAudioTrack;
        _audioUiStatus = GetNode<Label>("%AudioFeedback");
        SetAudioStatus(_audioUiStatusSource);
        _audioRecorder = new MicrophoneWavRecorder { Name = "MicrophoneWavRecorder" };
        AddChild(_audioRecorder);
        _audioRecorder.RecordingFailed += message => { StopPlayback(); StopArrangement(); SetAudioStatus(AudioTranslations.FormatSource("Recording failed: {0}. The partial take was retained for recovery.", message)); };
        var sectionAudio = GetNode<SoundFontAudioNode>("/root/SoundFontAudio");
        BindAudioPlaybackDiagnostics(sectionAudio);
    }

    private void BindAudioPlaybackDiagnostics(SoundFontAudioNode audio)
    {
        audio.AudioPlaybackWarning += message => SetAudioStatus(AudioTranslations.FormatSource("Audio playback warning: {0}", message));
        audio.AudioPlaybackUnderrun += frames => SetAudioStatus(AudioTranslations.FormatSource("Audio playback underrun: {0} frames were dropped. Playback timing may be affected.", frames));
    }

    private void CreateAudioTrack()
    {
        if (_score is null) return;
        var number = _score.Tracks.Count(track => track.Kind == TrackKind.Audio) + 1;
        try
        {
            _score.AddTrack(new ScoreTrack($"Audio {number}", TrackKind.Audio));
            NotifyScoreEdited();
            RenderSection();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            GD.PushWarning($"Could not add audio track: {error.Message}");
        }
    }

    private void ShowAudioImport(Guid trackId, bool arrangement, double startQuarter)
    {
        if (_score is null) return;
        if (!_score.Tracks.Any(item => item.Id == trackId && item.Kind == TrackKind.Audio)) { SetAudioStatus("This audio track is no longer available."); return; }
        if (arrangement && _score.Arrangements.All(item => item.Id != _selectedArrangementId)) { SetAudioStatus("Create or select an arrangement before importing arrangement audio."); return; }
        _audioImportDialog ??= CreateAudioImportDialog();
        _audioImportScore = _score;
        _audioImportDialog.SetMeta("target_arrangement", arrangement);
        _audioImportDialog.SetMeta("target_track_id", trackId.ToString("N"));
        _audioImportDialog.SetMeta("target_section_id", GetSelectedSection().Id.ToString("N"));
        _audioImportDialog.SetMeta("target_arrangement_id", (_selectedArrangementId ?? Guid.Empty).ToString("N"));
        _audioImportDialog.SetMeta("target_start_quarter", startQuarter);
        _audioImportDialog.PopupCentered(new Vector2I(900, 600));
    }

    private FileDialog CreateAudioImportDialog()
    {
        var dialog = new FileDialog
        {
            Title = AudioTranslations.Translate("Import PCM WAV"),
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.wav ; WAV audio (16-bit PCM, mono or stereo)" }
        };
        AddChild(dialog);
        dialog.FileSelected += ImportAudioFile;
        dialog.Canceled += () => { };
        return dialog;
    }

    private void ImportAudioFile(string sourcePath)
    {
        if (_score is null || _audioImportDialog is null || !ReferenceEquals(_score, _audioImportScore))
        { SetAudioStatus("The project changed before the WAV import completed. Reopen Import WAV on the intended track."); return; }
        try
        {
            var intoArrangement = _audioImportDialog.GetMeta("target_arrangement", false).AsBool();
            var trackId = Guid.Parse(_audioImportDialog.GetMeta("target_track_id").AsString());
            var sectionId = Guid.Parse(_audioImportDialog.GetMeta("target_section_id").AsString());
            var arrangementId = Guid.Parse(_audioImportDialog.GetMeta("target_arrangement_id").AsString());
            var startQuarter = _audioImportDialog.GetMeta("target_start_quarter").AsDouble();
            var targetSection = _score.Sections.FirstOrDefault(item => item.Id == sectionId)
                ?? throw new InvalidOperationException("The target section was removed before import completed.");
            var arrangement = intoArrangement
                ? _score.Arrangements.FirstOrDefault(item => item.Id == arrangementId)
                : null;
            if (intoArrangement && arrangement is null)
                throw new InvalidOperationException("Create or select an arrangement before importing arrangement audio.");
            var cache = ProjectSettings.GlobalizePath("user://audio_assets");
            var asset = WavAssetStore.Import(sourcePath, cache);
            var track = _score.Tracks.FirstOrDefault(item => item.Id == trackId && item.Kind == TrackKind.Audio);
            if (track is null) throw new InvalidOperationException("Add an audio track and select it before importing a WAV.");
            var clip = new AudioClip(asset.Id, track.Id, new ScoreTime((long)Math.Round(startQuarter * 4), 16), 0, asset.FrameCount);
            var targetClips = arrangement?.AudioClips ?? targetSection.AudioClips;
            EnsureAudioClipDoesNotOverlap(targetClips, clip, asset, arrangement is null ? targetSection : null, arrangement);
            _score.AudioAssets.Add(asset);
            if (arrangement is not null) arrangement.AudioClips.Add(clip);
            else targetSection.AudioClips.Add(clip);
            NotifyScoreEdited();
            RenderSection();
            SetAudioStatus(AudioTranslations.FormatSource("Imported {0}.", asset.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException)
        { SetAudioStatus(AudioTranslations.FormatSource("WAV import failed: {0}", error.Message)); GD.PushWarning($"WAV import failed: {error.Message}"); }
    }

    private AudioTrackUiState AudioUi(Guid trackId)
    {
        if (!_audioTrackUi.TryGetValue(trackId, out var state)) _audioTrackUi[trackId] = state = new AudioTrackUiState();
        return state;
    }

    private void SetAudioStatus(string message)
    {
        _audioUiStatusSource = message;
        var visibleMessage = AudioTranslations.Translate(message);
        if (_audioUiStatus is not null) _audioUiStatus.Text = visibleMessage;
        GD.Print(visibleMessage);
    }

    private void RefreshAudioStatusLocalization()
    {
        var create = GetNodeOrNull<Button>("%AddAudioTrackButton");
        if (create is not null) create.TooltipText = AudioTranslations.Translate("Add a track for imported WAV audio.");
        if (_audioUiStatus is not null) _audioUiStatus.Text = AudioTranslations.Translate(_audioUiStatusSource);
    }

    private void SetRecordButtons(Guid trackId, bool stopEnabled, bool cancelEnabled)
    {
        if (!_recordButtons.TryGetValue(trackId, out var buttons)) return;
        if (GodotObject.IsInstanceValid(buttons.Stop)) buttons.Stop.Disabled = !stopEnabled;
        if (GodotObject.IsInstanceValid(buttons.Cancel)) buttons.Cancel.Disabled = !cancelEnabled;
    }

    private async void StartAudioRecording(Guid trackId, Guid sectionId, Guid? arrangementId)
    {
        if (_score is null || _audioRecorder is null) return;
        var arrangement = arrangementId.HasValue;
        var ui = AudioUi(trackId);
        if (_audioRecordingStarting || _audioRecorder.IsTakeActive) return;
        var track = _score.Tracks.FirstOrDefault(item => item.Id == trackId && item.Kind == TrackKind.Audio);
        if (track is null) { SetAudioStatus("Add an audio track and select it before recording."); return; }
        _recordingPauseRequested = false;
        var targetArrangement = arrangement ? _score.Arrangements.FirstOrDefault(item => item.Id == arrangementId) : null;
        if (arrangement && targetArrangement is null) { SetAudioStatus("Create or select an arrangement before recording arrangement audio."); return; }
        var targetSection = _score.Sections.FirstOrDefault(item => item.Id == sectionId);
        if (targetSection is null) { SetAudioStatus("The target section is no longer available."); return; }
        var clips = targetArrangement?.AudioClips ?? targetSection.AudioClips;
        if (clips.Any(item => item.TrackId == track.Id)) { SetAudioStatus("Recording onto a track with existing clips is not supported yet. Choose an empty track."); return; }
        var timing = new AudioTimelineTiming(_score, section: arrangement ? null : targetSection, arrangement: targetArrangement);
        if (!timing.TryGetScoreTimeAtSeconds(ui.LatencyMs / 1000.0, out var recordingStart))
        { SetAudioStatus("The recording offset must be shorter than the selected section or arrangement. Add a measure or reduce the offset."); return; }
        _recordingTrackId = track.Id;
        _recordingScore = _score;
        _recordingIntoArrangement = arrangement;
        _recordingSectionId = arrangement ? null : sectionId;
        _recordingArrangementId = targetArrangement?.Id;
        _recordingStart = recordingStart;
        SetRecordButtons(track.Id, false, true);
        _audioRecordingStarting = true;
        _recordingStartCancelled = false;
        try
        {
            if (!await StartRecordingTransport(targetArrangement, sectionId)) return;
            if (_recordingStartCancelled) { StopPlayback(); StopArrangement(); return; }
            if (!ReferenceEquals(_score, _recordingScore))
            { StopPlayback(); StopArrangement(); SetAudioStatus("The project changed while recording was starting. No take was started."); return; }
            if (!_score.Tracks.Any(item => item.Id == _recordingTrackId && item.Kind == TrackKind.Audio) ||
                (_recordingIntoArrangement && !_score.Arrangements.Any(item => item.Id == _recordingArrangementId)) ||
                (!_recordingIntoArrangement && !_score.Sections.Any(item => item.Id == _recordingSectionId)))
            { StopPlayback(); StopArrangement(); SetAudioStatus("The recording destination changed before transport became ready."); return; }
            var folder = ProjectSettings.GlobalizePath("user://audio_assets");
            Directory.CreateDirectory(folder);
            _recordingPath = Path.Combine(folder, $"take-{Guid.NewGuid():N}.wav");
            var devices = MicrophoneWavRecorder.GetInputDevices();
            var device = ui.InputDevice <= 0 || ui.InputDevice > devices.Length ? "Default" : devices[ui.InputDevice - 1];
            var channels = ui.Channels == 0 ? MicrophoneWavRecorder.ChannelMode.Mono : MicrophoneWavRecorder.ChannelMode.Stereo;
            var result = _audioRecorder.Start(_recordingPath, channels, device);
            if (result != Error.Ok)
            {
                StopPlayback(); StopArrangement();
                SetAudioStatus(_audioRecorder.LastError ?? $"Could not start recording ({result}).");
                _recordingPath = null; _recordingTrackId = null;
                return;
            }
            if (_recordingPauseRequested) _audioRecorder.Pause(discardPendingSamples: true);
            SetRecordButtons(track.Id, true, true);
            SetAudioStatus(_audioRecorder.IsPaused
                ? "Recording paused. Press Space to resume the same take."
                : AudioTranslations.FormatSource("Recording to {0}. Use Stop Recording to finish, or Cancel Recording to discard the take.", track.Name));
            RenderSection();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StopPlayback(); StopArrangement();
            SetRecordButtons(track.Id, false, false);
            _recordingPath = null; _recordingTrackId = null;
            SetAudioStatus(AudioTranslations.FormatSource("Could not start recording: {0}", error.Message));
        }
        finally
        {
            _audioRecordingStarting = false;
            if (_audioRecorder.State != MicrophoneWavRecorder.RecorderState.Recording && _recordingPath is null)
            {
                if (_recordingTrackId is { } id) SetRecordButtons(id, false, false);
                _recordingTrackId = null; _recordingSectionId = null; _recordingArrangementId = null;
            }
        }
    }

    private async Task<bool> StartRecordingTransport(ScoreArrangement? arrangement, Guid sectionId)
    {
        if (_score is null || _soundFontAudio is null) return false;
        try
        {
            StopPlayback(); StopArrangement();
            SoundFontAudioNode audio;
            if (arrangement is not null)
            {
                if (!arrangement.Sections.Any()) { SetAudioStatus("Add a section to the arrangement before recording against its transport."); return false; }
                audio = _arrangementAudio;
                var ready = WaitForTransportReady(audio);
                StartArrangement(recording: true);
                if (!_arrangementAudio.IsScorePlaying) { SetAudioStatus("Arrangement playback could not start, so recording was not started."); return false; }
                if (!await ready) { StopArrangement(); SetAudioStatus("Arrangement transport did not become ready, so recording was not started."); return false; }
            }
            else
            {
                var section = _score.Sections.FirstOrDefault(item => item.Id == sectionId);
                if (section is null) { SetAudioStatus("The target section is no longer available."); return false; }
                var firstMeasure = section.Measures.FirstOrDefault();
                if (firstMeasure is null) { SetAudioStatus("Add a measure to the selected section before recording against its transport."); return false; }
                audio = _soundFontAudio;
                var ready = WaitForTransportReady(audio);
                UpdatePlaybackLoopSelection();
                _soundFontAudio.PlayScore(_score, firstMeasure.Id, recording: true, versionSelection: CapturePlaybackVersions());
                if (!_soundFontAudio.IsScorePlaying) { SetAudioStatus("Section playback could not start, so recording was not started."); return false; }
                _playPauseButton.SetPressedNoSignal(true);
                if (!await ready) { StopPlayback(); SetAudioStatus("Section transport did not become ready, so recording was not started."); return false; }
            }
            if (!audio.IsScoreReady) { SetAudioStatus("Transport stopped before recording could start."); return false; }
            SetAudioStatus("Count-in before recording. Press Space to pause or Cancel Recording to stop.");
            while (audio.IsScorePlaying && !_recordingStartCancelled && audio.IsAudibleCountIn)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (_recordingStartCancelled || !audio.IsScorePlaying) return false;
            return true;
        }
        catch (Exception error)
        {
            StopPlayback(); StopArrangement();
            SetAudioStatus(AudioTranslations.FormatSource("Transport could not start: {0}", error.Message));
            return false;
        }
    }

    private async Task<bool> WaitForTransportReady(SoundFontAudioNode audio)
    {
        if (audio.IsScoreReady) return true;
        var ready = new TaskCompletionSource<bool>();
        _recordingReadyWait = ready;
        void OnReady() => ready.TrySetResult(true);
        audio.ScorePlaybackReady += OnReady;
        try
        {
            if (audio.IsScoreReady) return true;
            return await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) { return false; }
        finally { audio.ScorePlaybackReady -= OnReady; if (ReferenceEquals(_recordingReadyWait, ready)) _recordingReadyWait = null; }
    }

    private void FinishAudioRecording()
    {
        if (_score is null || _audioRecorder is null || _recordingPath is null || _recordingTrackId is null) return;
        var recordingTrackId = _recordingTrackId.Value;
        var path = _audioRecorder.Stop();
        StopPlayback(); StopArrangement();
        if (path is null) { SetAudioStatus(_audioRecorder.LastError ?? "Recording could not be finalized; the partial take was retained."); return; }
        try
        {
            if (!ReferenceEquals(_score, _recordingScore)) throw new InvalidOperationException("The project changed while recording. The completed take remains in the audio cache.");
            var track = _score.Tracks.FirstOrDefault(item => item.Id == _recordingTrackId && item.Kind == TrackKind.Audio)
                ?? throw new InvalidOperationException("The selected audio track was removed while recording. The completed take remains in the audio cache.");
            var asset = WavAssetStore.Import(path, ProjectSettings.GlobalizePath("user://audio_assets"));
            var clips = _recordingIntoArrangement
                ? _score.Arrangements.FirstOrDefault(item => item.Id == _recordingArrangementId)?.AudioClips
                : _score.Sections.FirstOrDefault(item => item.Id == _recordingSectionId)?.AudioClips;
            if (clips is null || clips.Any(item => item.TrackId == track.Id))
                throw new InvalidOperationException("The destination changed or already has clips. The completed take remains in the audio cache.");
            _score.AudioAssets.Add(asset);
            clips.Add(new AudioClip(asset.Id, track.Id, _recordingStart, 0, asset.FrameCount));
            File.Delete(path);
            _recordingPath = null; _recordingTrackId = null;
            NotifyScoreEdited(); RenderSection();
            SetAudioStatus(AudioTranslations.FormatSource("Recording saved as {0}.", asset.FileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or InvalidOperationException)
        { SetAudioStatus(AudioTranslations.FormatSource("Take completed but could not be added: {0}. The take remains in the audio cache.", error.Message)); }
        finally { SetRecordButtons(recordingTrackId, false, false); }
    }

    private void CancelAudioRecording()
    {
        if (_audioRecordingStarting)
        {
            _recordingStartCancelled = true;
            _recordingReadyWait?.TrySetResult(false);
            StopPlayback(); StopArrangement();
            SetAudioStatus("Recording start canceled.");
            return;
        }
        if (_audioRecorder?.IsTakeActive != true) return;
        _audioRecorder.Cancel();
        StopPlayback(); StopArrangement();
        if (_recordingTrackId is { } id) SetRecordButtons(id, false, false);
        _recordingPath = null; _recordingTrackId = null; _recordingPreview = null;
        SetAudioStatus("Recording canceled.");
        RenderSection();
    }

    private void RenderAudioTrack(ScoreTrack track, ScoreSection section)
    {
        if (_recordingTrackId == track.Id) _recordingPreview = null;
        var ui = AudioUi(track.Id);
        var row = _nonPercussionTrackScene.Instantiate<FoldableContainer>();
        row.Name = $"AudioTrack_{track.Id:N}";
        _tabTracks.AddChild(row);

        row.Folded = _trackFoldedStates.GetValueOrDefault(track.Id, row.Folded);
        RegisterTrackRow(track.Id, row);
        row.FoldingChanged += folded => { _trackFoldedStates[track.Id] = folded; MarkEditorStateDirty(); };
        var icon = row.GetNode<TextureRect>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/PanelContainer/TextureRect");
        var delete = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/PanelContainer/DeleteButton");
        var nameLabel = row.GetNode<Label>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/NameContainer/TrackNameLabel");
        var volume = row.GetNode<HSlider>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/VolumeContainer/Volume");
        var volumeLabel = row.GetNode<Label>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/VolumeContainer/Label");
        var mute = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/MuteButton");
        var solo = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/SoloButton");
        var configs = row.GetNode<HBoxContainer>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs");
        icon.Texture = GD.Load<Texture2D>(TrackSettingsDialog.ResolveImagePath(track));
        nameLabel.Text = track.Name;
        var nameEdit = row.GetNode<LineEdit>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/NameContainer/TrackNameEdit");
        nameEdit.Text = track.Name; nameEdit.Visible = false;
        volume.MinValue = -80; volume.MaxValue = 20; volume.Step = 1; volume.Value = track.VolumeDb;
        volumeLabel.Text = $"{track.VolumeDb:0.#} dB";
        mute.ToggleMode = true; mute.ButtonPressed = track.IsMuted;
        solo.ToggleMode = true; solo.ButtonPressed = track.IsSoloed;
        mute.Toggled += value => { SetActiveEditorTrack(track.Id); track.IsMuted = value; NotifyScoreEdited(); };
        solo.Toggled += value => { SetActiveEditorTrack(track.Id); track.IsSoloed = value; NotifyScoreEdited(); };
        volume.ValueChanged += value => { SetActiveEditorTrack(track.Id); track.VolumeDb = value; volumeLabel.Text = $"{value:0.#} dB"; NotifyScoreEdited(); };
        nameLabel.MouseFilter = Control.MouseFilterEnum.Stop;
        nameLabel.TooltipText = AudioTranslations.Translate("Double-click to rename this track.");
        nameLabel.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { Pressed: true, DoubleClick: true }) return;
            SetActiveEditorTrack(track.Id);
            nameLabel.Visible = false; nameEdit.Visible = true; nameEdit.Text = track.Name; nameEdit.GrabFocus(); nameEdit.SelectAll();
        };
        var committingName = false;
        void CommitName()
        {
            if (committingName) return;
            committingName = true;
            var value = nameEdit.Text.Trim();
            var changed = value.Length > 0 && value != track.Name && _score.Tracks.Any(item => item.Id == track.Id);
            if (changed) { track.Name = value; NotifyScoreEdited(); }
            nameLabel.Text = track.Name; RefreshTrackTitles();
            nameEdit.Visible = false; nameLabel.Visible = true;
            committingName = false;
            if (changed) RenderSection();
        }
        nameEdit.TextSubmitted += _ => CommitName();
        nameEdit.FocusExited += CommitName;
        var settings = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/TrackSettingsButton");
        settings.Hide();
        row.GetNode<Control>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/TrackSectionInfo").Hide();
        row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/ToggleKeyboardButton").Hide();
        row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/ToggleFretboardButton").Hide();
        var picker = new ColorPickerButton { Color = new Color(track.DisplayColorRgba), CustomMinimumSize = new Vector2(34, 34), TooltipText = AudioTranslations.Translate("Track color"), EditAlpha = true, ThemeTypeVariation = "AppActionButton" };
        configs.AddChild(picker);
        picker.ColorChanged += color => ApplyTrackDisplayColor(track.Id, PackAudioColor(color));
        picker.PopupClosed += () => { if (_score.Tracks.Any(item => item.Id == track.Id)) NotifyScoreEdited(); };
        var sectionTemplate = row.GetNode<Control>("TrackContainer/HBoxContainer/TabSectionContainer");
        var page = sectionTemplate.GetNode<VBoxContainer>("VBoxContainer");
        var measures = page.GetNode<Control>("Measures");
        page.RemoveChild(measures); measures.QueueFree();
        var header = page.GetNode<HFlowContainer>("Header/HeaderContainer");
        foreach (var child in header.GetChildren()) { header.RemoveChild(child); child.QueueFree(); }
        var timelines = new List<AudioSectionTimeline>();
        var toolbar = _audioToolbarScene.Instantiate<PanelContainer>();
        toolbar.Name = "AudioTrackToolbar";
        toolbar.SelfModulate = new Color(track.DisplayColorRgba);
        var guides = toolbar.GetNode<Button>("Controls/GridGuidesButton");
        var scissors = toolbar.GetNode<Button>("Controls/AudioCutTool");
        var start = toolbar.GetNode<SpinBox>("Controls/NextImportStart");
        var latency = toolbar.GetNode<SpinBox>("Controls/RecordingOffset");
        var importSection = toolbar.GetNode<Button>("Controls/ImportSectionButton");
        var device = toolbar.GetNode<OptionButton>("Controls/InputDevice");
        var channels = toolbar.GetNode<OptionButton>("Controls/RecordingChannels");
        var recordSection = toolbar.GetNode<Button>("Controls/RecordSectionButton");
        var stopRecording = toolbar.GetNode<Button>("Controls/StopRecordingButton");
        var cancelRecording = toolbar.GetNode<Button>("Controls/CancelRecordingButton");
        guides.ButtonPressed = ui.ShowGuides;
        scissors.ButtonPressed = ui.ScissorsEnabled;
        start.Value = ui.StartQuarter;
        latency.Value = ui.LatencyMs;
        // Keep the packed panel and its responsive HFlowContainer intact under the existing header.
        header.AddChild(toolbar);
        guides.Toggled += visible => { ui.ShowGuides = visible; foreach (var timeline in timelines) timeline.SetGuidesVisible(visible); };
        scissors.Toggled += enabled => { ui.ScissorsEnabled = enabled; foreach (var timeline in timelines) timeline.SetScissorsEnabled(enabled); };
        start.ValueChanged += value => ui.StartQuarter = value;
        latency.ValueChanged += value => ui.LatencyMs = value;
        importSection.Pressed += () => ShowAudioImport(track.Id, false, ui.StartQuarter);
        device.AddItem(AudioTranslations.Translate("Default")); foreach (var item in MicrophoneWavRecorder.GetInputDevices()) device.AddItem(item);
        device.Select(Math.Clamp(ui.InputDevice, 0, Math.Max(0, device.ItemCount - 1)));
        device.ItemSelected += index => ui.InputDevice = (int)index;
        channels.AddItem(AudioTranslations.Translate("Mono")); channels.AddItem(AudioTranslations.Translate("Stereo")); channels.Select(Math.Clamp(ui.Channels, 0, 1));
        channels.ItemSelected += index => ui.Channels = (int)index;
        recordSection.Pressed += () => StartAudioRecording(track.Id, section.Id, null);
        stopRecording.Disabled = _recordingTrackId != track.Id || _audioRecorder?.IsTakeActive != true;
        stopRecording.Pressed += FinishAudioRecording;
        cancelRecording.Disabled = _recordingTrackId != track.Id;
        cancelRecording.Pressed += CancelAudioRecording;
        _recordButtons[track.Id] = (stopRecording, cancelRecording);
        var confirmation = new ConfirmationDialog { Title = AudioTranslations.Translate("Delete track?"), OkButtonText = AudioTranslations.Translate("Delete"), DialogText = AudioTranslations.FormatSource("Delete audio track ‘{0}’ and its clips from all sections and arrangements?", track.Name) };
        row.AddChild(confirmation);
        confirmation.GetOkButton().ThemeTypeVariation = "AppDestructiveButton";
        confirmation.GetCancelButton().ThemeTypeVariation = "AppActionButton";
        delete.Pressed += () => confirmation.PopupCentered();
        confirmation.Confirmed += () =>
        {
            if (_recordingTrackId == track.Id && (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true))
            { SetAudioStatus("Stop or cancel this track’s recording before deleting the track."); return; }
            if (_score.Tracks.Any(item => item.Id == track.Id)) DeleteNonPercussionTrack(track);
        };
        RenderAudioClipList(page, track, section.AudioClips, AudioTranslations.Translate("Section"), section, null, ui, timelines);
        if (_recordingTrackId == track.Id && !_recordingIntoArrangement && _recordingSectionId == section.Id)
            AddRecordingPreview(page);
        if (_score.Arrangements.FirstOrDefault(item => item.Id == _selectedArrangementId) is { } arrangement)
        {
            RenderAudioClipList(page, track, arrangement.AudioClips, AudioTranslations.FormatSource("Arrangement · {0}", arrangement.Name), null, arrangement, ui, timelines);
            if (_recordingTrackId == track.Id && _recordingIntoArrangement && _recordingArrangementId == arrangement.Id)
                AddRecordingPreview(page);
        }
        _audioTrackUi.Keys.Where(id => !_score.Tracks.Any(item => item.Id == id)).ToList().ForEach(id => _audioTrackUi.Remove(id));
    }

    private static uint PackAudioColor(Color color) =>
        ((uint)Mathf.RoundToInt(color.R * 255) << 24) | ((uint)Mathf.RoundToInt(color.G * 255) << 16) |
        ((uint)Mathf.RoundToInt(color.B * 255) << 8) | (uint)Mathf.RoundToInt(color.A * 255);

    private static Button AudioActionButton(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = "AppActionButton"
    };

    private void RenderAudioClipList(VBoxContainer parent, ScoreTrack track, List<AudioClip> source, string context,
        ScoreSection? section, ScoreArrangement? arrangement, AudioTrackUiState ui, List<AudioSectionTimeline> timelines)
    {
        var clips = source.Where(item => item.TrackId == track.Id).OrderBy(item => item.Start.Numerator / (double)item.Start.Denominator).ToArray();
        var timing = new AudioTimelineTiming(_score, section, arrangement);
        var shell = _audioTimelineShellScene.Instantiate<VBoxContainer>();
        shell.GetNode<Label>("ContextLabel").Text = AudioTranslations.FormatSource("{0} clips ({1})", context, clips.Length);
        var scroll = shell.GetNode<ScrollContainer>("AudioTimelineScroll");
        var timeline = shell.GetNode<AudioSectionTimeline>("AudioTimelineScroll/AudioTimeline");
        while (shell.GetChildCount() > 0)
        {
            var child = shell.GetChild(0);
            shell.RemoveChild(child);
            parent.AddChild(child);
        }
        shell.QueueFree();
        timeline.SetTimelineDuration(timing.GetDurationSeconds());
        timeline.SetGuidesVisible(ui.ShowGuides);
        timeline.SetScissorsEnabled(ui.ScissorsEnabled);
        SetAudioTimelineGuides(timeline, timing, section, arrangement);
        timelines.Add(timeline);

        foreach (var initialClip in clips)
        {
            var currentClip = initialClip;
            var asset = _score.AudioAssets.FirstOrDefault(item => item.Id == currentClip.AssetId);
            var row = _audioClipControlsScene.Instantiate<HFlowContainer>();
            row.Name = $"ClipControls_{currentClip.Id:N}";
            parent.AddChild(row);
            var fileName = row.GetNode<Label>("AssetFileName");
            fileName.Text = asset?.FileName ?? AudioTranslations.Translate("Missing WAV");
            var start = row.GetNode<SpinBox>("Start");
            var offset = row.GetNode<SpinBox>("Offset");
            var duration = row.GetNode<SpinBox>("Duration");
            var remove = row.GetNode<Button>("Remove");
            var outsideTimeline = row.GetNode<Label>("OutsideTimeline");
            var initialStartQuarter = (double)currentClip.Start.Numerator / currentClip.Start.Denominator * 4;
            start.Value = initialStartQuarter;
            offset.MaxValue = Math.Max(0, (asset?.FrameCount ?? 1) - 1);
            offset.Value = currentClip.SourceOffsetFrames;
            duration.MaxValue = Math.Max(1, asset?.FrameCount ?? 1);
            duration.Value = currentClip.DurationFrames;
            remove.Pressed += () => { if (source.Remove(currentClip)) { NotifyScoreEdited(); RenderSection(); } };

            var preview = new AudioWaveformPreview();
            if (asset is null)
            {
                preview.SetUnavailable(AudioTranslations.Translate("Waveform unavailable: source WAV is missing."));
                preview.QueueFree();
                start.Editable = false; offset.Editable = false; duration.Editable = false;
                continue;
            }
            preview.LoadClip(asset.CachePath, currentClip.SourceOffsetFrames, currentClip.DurationFrames, asset.SampleRate);
            if (timing.TryGetElapsedSeconds(currentClip.Start, out var initialSeconds))
                timeline.AddClip(currentClip.Id, initialSeconds, currentClip.DurationFrames / (double)asset.SampleRate, preview);
            else
            {
                preview.QueueFree();
                outsideTimeline.Visible = true;
                // The numerical controls remain usable for out-of-range clips.
                preview = null!;
            }
            void UpdateBinding()
            {
                if (preview is null || !GodotObject.IsInstanceValid(preview)) return;
                _audioPreviewBindings.RemoveAll(item => ReferenceEquals(item.Preview, preview));
                _audioPreviewBindings.Add(new AudioPreviewBinding(preview, currentClip.Id,
                    currentClip.SourceOffsetFrames, currentClip.DurationFrames, asset.SampleRate));
            }
            UpdateBinding();
            var preferredDuration = currentClip.DurationFrames;
            var capturedScore = _score;

            bool CommitEdit(ScoreTime newStart, long newOffset, long newDuration, bool reloadWaveform)
            {
                if (!ReferenceEquals(_score, capturedScore)) return false;
                if (newStart == currentClip.Start && newOffset == currentClip.SourceOffsetFrames && newDuration == currentClip.DurationFrames) return true;
                if (!AudioClipOperations.TryEdit(_score, section, arrangement, source, currentClip, newStart, newOffset, newDuration,
                        out var updated, out var error))
                {
                    SetAudioStatus(error);
                    return false;
                }
                var index = source.FindIndex(item => item.Id == currentClip.Id);
                if (index < 0 || updated is null) return false;
                source[index] = updated;
                currentClip = updated;
                NotifyScoreEdited();
                if (preview is not null && GodotObject.IsInstanceValid(preview))
                {
                    if (reloadWaveform) preview.LoadClip(asset.CachePath, currentClip.SourceOffsetFrames, currentClip.DurationFrames, asset.SampleRate);
                    UpdateBinding();
                    if (timing.TryGetElapsedSeconds(currentClip.Start, out var position)) timeline.SetClipStart(currentClip.Id, position);
                    timeline.SetClipDuration(currentClip.Id, currentClip.DurationFrames / (double)asset.SampleRate);
                }
                return true;
            }

            start.ValueChanged += value =>
            {
                var next = new ScoreTime((long)Math.Round(value * 4_000_000), 4_000_000);
                if (!CommitEdit(next, currentClip.SourceOffsetFrames, currentClip.DurationFrames, false))
                    start.SetValueNoSignal((double)currentClip.Start.Numerator / currentClip.Start.Denominator * 4);
                else if (preview is null) RenderSection();
            };
            offset.ValueChanged += value =>
            {
                var nextOffset = (long)value;
                var nextDuration = Math.Min(preferredDuration, asset.FrameCount - nextOffset);
                if (nextDuration < 1 || !CommitEdit(currentClip.Start, nextOffset, nextDuration, true))
                    offset.SetValueNoSignal(currentClip.SourceOffsetFrames);
                else duration.SetValueNoSignal(currentClip.DurationFrames);
            };
            duration.ValueChanged += value =>
            {
                var nextDuration = (long)value;
                if (!CommitEdit(currentClip.Start, currentClip.SourceOffsetFrames, nextDuration, true))
                    duration.SetValueNoSignal(currentClip.DurationFrames);
                else preferredDuration = nextDuration;
            };
            timeline.ClipMoveCommitted += (id, proposedSeconds) =>
            {
                if (id != currentClip.Id) return;
                if (!timing.TryGetScoreTimeAtSeconds(proposedSeconds, out var position) ||
                    !CommitEdit(position, currentClip.SourceOffsetFrames, currentClip.DurationFrames, false))
                {
                    if (timing.TryGetElapsedSeconds(currentClip.Start, out var prior)) timeline.SetClipStart(id, prior);
                    SetAudioStatus("The clip cannot be moved there; keep it inside the timeline and clear of other clips.");
                    return;
                }
                start.SetValueNoSignal((double)currentClip.Start.Numerator / currentClip.Start.Denominator * 4);
            };
            timeline.ClipTrimCommitted += (id, trimLeft, boundarySeconds) =>
            {
                if (id != currentClip.Id || !ReferenceEquals(_score, capturedScore)) return;
                AudioClip? trimmed;
                string error;
                var valid = trimLeft
                    ? AudioClipOperations.TryTrimLeft(_score, section, arrangement, source, currentClip, boundarySeconds, out trimmed, out error)
                    : AudioClipOperations.TryTrimRight(_score, section, arrangement, source, currentClip, boundarySeconds, out trimmed, out error);
                if (!valid || trimmed is null || !CommitEdit(trimmed.Start, trimmed.SourceOffsetFrames, trimmed.DurationFrames, true))
                {
                    if (!string.IsNullOrEmpty(error)) SetAudioStatus(error);
                    if (timing.TryGetElapsedSeconds(currentClip.Start, out var prior)) timeline.SetClipStart(id, prior);
                    timeline.SetClipDuration(id, currentClip.DurationFrames / (double)asset.SampleRate);
                    return;
                }
                preferredDuration = currentClip.DurationFrames;
                start.SetValueNoSignal((double)currentClip.Start.Numerator / currentClip.Start.Denominator * 4);
                offset.SetValueNoSignal(currentClip.SourceOffsetFrames);
                duration.SetValueNoSignal(currentClip.DurationFrames);
            };
            timeline.CutRequested += (id, secondsWithinClip) =>
            {
                if (id != currentClip.Id || !ReferenceEquals(_score, capturedScore)) return;
                var frame = currentClip.SourceOffsetFrames + (long)Math.Round(secondsWithinClip * asset.SampleRate, MidpointRounding.AwayFromZero);
                if (!AudioClipOperations.TryCut(_score, section, arrangement, source, currentClip, frame,
                        out var left, out var right, out var error))
                { SetAudioStatus(error); return; }
                var index = source.FindIndex(item => item.Id == id);
                if (index < 0 || left is null || right is null) return;
                source[index] = left; source.Insert(index + 1, right);
                NotifyScoreEdited(); RenderSection();
            };
        }
    }

    private void SetAudioTimelineGuides(AudioSectionTimeline timeline, AudioTimelineTiming timing,
        ScoreSection? section, ScoreArrangement? arrangement)
    {
        var measures = new List<double>();
        var tempos = new List<double>();
        var position = ScoreTime.Zero;
        if (timing.GetDurationSeconds() > 0) tempos.Add(0);
        IEnumerable<ScoreSection> sections = arrangement is null
            ? (section is null ? Array.Empty<ScoreSection>() : new[] { section })
            : arrangement.Sections.SelectMany(entry =>
                Enumerable.Repeat(entry.SectionId, entry.Repetitions))
                .Select(id => _score.Sections.FirstOrDefault(item => item.Id == id))
                .Where(item => item is not null).Cast<ScoreSection>();
        foreach (var currentSection in sections)
        {
            if (currentSection.DefaultTempo is not null && timing.TryGetElapsedSeconds(position, out var sectionTempo))
                tempos.Add(sectionTempo);
            foreach (var measure in currentSection.Measures)
            {
                if (timing.TryGetElapsedSeconds(position, out var measureTime)) measures.Add(measureTime);
                var length = ScorePlaybackClock.GetMeasureDuration(_score, currentSection, measure);
                foreach (var tempoEvent in measure.TempoEvents)
                    if (tempoEvent.Offset >= ScoreTime.Zero && tempoEvent.Offset < length &&
                        timing.TryGetElapsedSeconds(position + tempoEvent.Offset, out var tempoTime))
                        tempos.Add(tempoTime);
                position += length;
            }
        }
        timeline.SetGuides(measures, tempos);
    }

    private void AddRecordingPreview(VBoxContainer parent)
    {
        var preview = new AudioWaveformPreview();
        preview.SetRecordingPreview();
        parent.AddChild(preview);
        _recordingPreview = preview;
    }

    /// <summary>Maps the active shared playback clock onto visible audio waveforms.</summary>
    private void UpdateAudioTrackPlaybackCursors()
    {
        _audioPreviewBindings.RemoveAll(binding => !GodotObject.IsInstanceValid(binding.Preview) ||
            !binding.Preview.IsInsideTree());

        var arrangementPlayback = _arrangementAudio is not null && _arrangementAudio.IsScorePlaying;
        var player = arrangementPlayback ? _arrangementAudio : _soundFontAudio.IsScorePlaying ? _soundFontAudio : null;
        var timelineFrame = player?.VisualTimelineFrame;
        var activeScheduledClips = new Dictionary<Guid, ScheduledAudioClip>();
        if (player is not null && timelineFrame is { } activeFrame)
            foreach (var item in player.ScheduledAudioClips)
                if (activeFrame >= item.StartFrame && activeFrame < item.EndFrame)
                    activeScheduledClips[item.ClipId] = item;
        foreach (var binding in _audioPreviewBindings)
        {
            double? progress = null;
            if (activeScheduledClips.TryGetValue(binding.ClipId, out var occurrence) && player is not null && binding.DurationFrames > 0 && timelineFrame is { } frame)
            {
                var elapsedOutputFrames = frame - occurrence.StartFrame;
                var sourceFrame = occurrence.SourceOffsetFrames + elapsedOutputFrames *
                    (binding.AssetSampleRate / (double)Math.Max(1, player.PlaybackSampleRate));
                progress = (sourceFrame - binding.SourceOffsetFrames) / binding.DurationFrames;
                progress = Math.Clamp(progress.Value, 0, 1);
            }
            binding.Preview.SetPlaybackProgress(progress);
            if (progress is { } visibleProgress && binding.Preview.IsVisibleInTree())
                FollowAudioTimelineCursor(binding.Preview, visibleProgress);
        }

        if (_recordingPreview is null || !GodotObject.IsInstanceValid(_recordingPreview) ||
            !_recordingPreview.IsInsideTree() || !_recordingPreview.IsVisibleInTree()) return;
        if (_recordingTrackId is null)
        { _recordingPreview.SetPlaybackProgress(null); return; }

        double? takeProgress = null;
        if (arrangementPlayback && _recordingIntoArrangement && SelectedArrangement is { } activeArrangement &&
            activeArrangement.Id == _recordingArrangementId)
        {
            if (player is not null && timelineFrame is { } frame && player.PlaybackTimelineDurationFrames > 0)
                takeProgress = frame / (double)player.PlaybackTimelineDurationFrames;
        }
        else if (!arrangementPlayback && !_recordingIntoArrangement && _recordingSectionId is { } recordingSection)
        {
            takeProgress = GetRecordingSectionProgress(player, recordingSection);
        }
        _recordingPreview.SetPlaybackProgress(takeProgress);
    }

    private static void FollowAudioTimelineCursor(AudioWaveformPreview preview, double progress)
    {
        for (Node? node = preview.GetParent(); node is not null; node = node.GetParent())
        {
            if (node is not ScrollContainer scroll || scroll.Name != "AudioTimelineScroll") continue;
            var viewport = scroll.GetGlobalRect();
            if (viewport.Size.X <= 0) return;
            var x = preview.GetGlobalRect().Position.X + preview.Size.X * (float)Math.Clamp(progress, 0, 1);
            var margin = Math.Min(24f, viewport.Size.X / 4f);
            var delta = x < viewport.Position.X + margin ? x - viewport.Position.X - margin :
                x > viewport.End.X - margin ? x - viewport.End.X + margin : 0;
            if (Math.Abs(delta) > 1) scroll.ScrollHorizontal += (int)Math.Round(delta);
            return;
        }
    }

    private double? GetRecordingSectionProgress(SoundFontAudioNode? player, Guid sectionId)
    {
        if (_score is null || player?.VisualPosition is not { } playbackPosition) return null;
        var target = _score.Sections.FirstOrDefault(section => section.Id == sectionId);
        var sourceSection = _score.Sections.FirstOrDefault(section => section.Measures.Any(measure => measure.Id == playbackPosition.MeasureId));
        if (target is null || sourceSection?.Id != target.Id || target.Measures.Count == 0) return null;
        var measureIndex = target.Measures.FindIndex(measure => measure.Id == playbackPosition.MeasureId);
        if (measureIndex < 0) return null;

        var sectionStart = _score.ToAbsolutePosition(target, target.Measures[0], ScoreTime.Zero);
        var measureStart = _score.ToAbsolutePosition(target, target.Measures[measureIndex], ScoreTime.Zero);
        var measureDuration = ScorePlaybackClock.GetMeasureDuration(_score, target, target.Measures[measureIndex]);
        var current = measureStart + measureDuration * new ScoreTime(
            (long)Math.Round(Math.Clamp(playbackPosition.Fraction, 0, 1) * 1_000_000), 1_000_000);
        var sectionEnd = sectionStart;
        foreach (var measure in target.Measures) sectionEnd += ScorePlaybackClock.GetMeasureDuration(_score, target, measure);
        if (player is null) return null;
        var currentSeconds = player.GetScoreElapsedSeconds(current);
        var startSeconds = player.GetScoreElapsedSeconds(sectionStart);
        var endSeconds = player.GetScoreElapsedSeconds(sectionEnd);
        if (currentSeconds is null || startSeconds is null || endSeconds is null) return null;
        var elapsed = currentSeconds.Value - startSeconds.Value;
        var duration = endSeconds.Value - startSeconds.Value;
        return duration > 0 ? Math.Clamp(elapsed / duration, 0, 1) : null;
    }

    private void EnsureAudioClipDoesNotOverlap(System.Collections.Generic.List<AudioClip> clips, AudioClip candidate, AudioAsset candidateAsset,
        ScoreSection? section, ScoreArrangement? arrangement, Guid? ignoreId = null)
    {
        if (_score is null) return;
        var candidateRange = GetAudioClipTimelineRange(candidate, candidateAsset, section, arrangement);
        if (candidateRange is null) return; // Out-of-context clips remain preserved and inaudible as in the scheduler.
        foreach (var other in clips.Where(item => item.TrackId == candidate.TrackId && item.Id != ignoreId))
        {
            var otherAsset = _score.AudioAssets.FirstOrDefault(item => item.Id == other.AssetId);
            if (otherAsset is null) continue;
            var otherRange = GetAudioClipTimelineRange(other, otherAsset, section, arrangement);
            if (otherRange is { } range && candidateRange.Value.Start < range.End && range.Start < candidateRange.Value.End)
                throw new InvalidOperationException("Clips on the same track cannot overlap. Move the clip or trim its source duration.");
        }
    }

    private (double Start, double End)? GetAudioClipTimelineRange(AudioClip clip, AudioAsset asset, ScoreSection? section, ScoreArrangement? arrangement)
    {
        if (_score is null) return null;
        var timing = new AudioTimelineTiming(_score, section, arrangement);
        if (!timing.TryGetElapsedSeconds(clip.Start, out var start)) return null;
        var end = Math.Min(timing.GetDurationSeconds(), start + clip.DurationFrames / (double)asset.SampleRate);
        return end > start ? (start, end) : null;
    }

    private void RenderAudioOnlyRuler(ScoreSection section)
    {
        var ruler = _score.Tracks.FirstOrDefault(track => track.Kind == TrackKind.Ruler);
        if (ruler is null) return;
        var row = _audioRulerScene.Instantiate<PanelContainer>();
        row.Name = $"Ruler_{section.Id:N}";
        _tabTracks.AddChild(row);
        row.GetNode<Label>("Layout/Heading").Text = AudioTranslations.FormatSource("Section ruler · {0} · {1} measures", section.Name, section.Measures.Count);
        var controls = row.GetNode<HBoxContainer>("Layout/MeasureControls");
        for (var i = 0; i < section.Measures.Count; i++)
        {
            var measureIndex = i;
            var signature = _score.ResolveTimeSignature(section, section.Measures[i]);
            controls.AddChild(new Label { Text = $"M{i + 1}  {signature.Numerator}/{signature.Denominator}", CustomMinimumSize = new Vector2(84, 0) });
            var meter = new OptionButton();
            foreach (var (num, den) in new[] { (4, 4), (3, 4), (6, 8), (5, 4), (7, 8) })
                meter.AddItem($"{num}/{den}");
            var choice = Array.IndexOf(new[] { (4, 4), (3, 4), (6, 8), (5, 4), (7, 8) }, (signature.Numerator, signature.Denominator));
            meter.Select(Math.Max(0, choice));
            meter.ItemSelected += selected =>
            {
                if (measureIndex >= section.Measures.Count) return;
                var values = new[] { (4, 4), (3, 4), (6, 8), (5, 4), (7, 8) };
                var (n, d) = values[(int)selected];
                section.Measures[measureIndex].TimeSignatureOverride = new TimeSignature(n, d);
                NotifyScoreEdited(); RenderSection();
            };
            controls.AddChild(meter);
        }
        var add = controls.GetNode<Button>("AddMeasure");
        controls.MoveChild(add, controls.GetChildCount() - 1);
        add.Pressed += () => { InitializeMeasureSnapDefaults(_score.AddMeasure(section)); NotifyScoreEdited(); RenderSection(); };
        var remove = controls.GetNode<Button>("RemoveLastMeasure");
        controls.MoveChild(remove, controls.GetChildCount() - 1);
        remove.Disabled = section.Measures.Count <= 1;
        remove.Pressed += () =>
        {
            if (section.Measures.Count <= 1) return;
            _score.RemoveMeasure(section, section.Measures[^1]); NotifyScoreEdited(); RenderSection();
        };
    }
}

#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;

/// <summary>
/// Godot audio output for the SoundFont playback service.
/// The audio generator and synthesizer use the same mix rate.
/// </summary>
public partial class SoundFontAudioNode : Node
{
    private const int RenderBlockFrames = 256;

    private readonly float[] _leftBuffer = new float[RenderBlockFrames];
    private readonly float[] _rightBuffer = new float[RenderBlockFrames];
    private readonly Vector2[] _stereoBuffer = new Vector2[RenderBlockFrames];

    private AudioStreamGenerator _audioStream = null!;
    private AudioStreamPlayer _audioPlayer = null!;
    private AudioStreamGeneratorPlayback? _audioPlayback;
    private SoundFontPlaybackService? _playbackService;
    private ScorePlaybackClock? _scorePlaybackClock;
    private int _sampleRate;
    private bool _scorePaused;
    private int _scoreStartupGeneration;
    private bool _scoreAudioPreparationPending;
    private long _lastAudioUnderrunFrames;
    private bool _loopEnabled = true;
    private bool _metronomeEnabled;
    private bool _countInEnabled;
    private bool _countInEveryLoop;
    private bool _recordingTransport;
    private long _staleQueuedFrames;
    private bool _scoreFinishedGenerating;
    private long _scoreTailFramesRemaining;
    private long _audioBufferCapacityFrames;
    private PlaybackPosition? _completedScorePosition;
    private PlaybackPosition? _startPosition;
    private PlaybackLoopSelection? _loopSelection;
    private PlaybackVersionSelection _versionSelection = PlaybackVersionSelection.Empty;
    private AudioStreamPlayer? _previewAudioPlayer;
    private AudioStreamGeneratorPlayback? _previewAudioPlayback;
    private SoundFontPlaybackService? _previewService;
	private ScorePlaybackClock? _pairPreviewClock;
	private bool _pairPreviewActive;
    private NoteExpression? _previewCurve;
    private NoteExpression[] _previewExpressions = Array.Empty<NoteExpression>();
    private Guid _previewTrackId;
    private int _previewChannel;
    private int _previewPitch;
    private int _previewVelocity;
    private long _previewNoteFrames;
    private long _previewGapFrames;
    private long _previewFrame;
    private double _previewRestartDelay;
    private double _previewTempoWholeNotesPerMinute;
    private int _previewProgram;
    private int _previewPieceGainPitch = -1;
    private double _previewPieceGainDb;
    private string _previewVariation = "";
    private double _previewVolumeDb;
    private bool _previewLoop;
    private bool _previewStopAfterCycle;
    private bool _previewActive;
    private bool _previewDraining;
    private bool _previewSilent;
    private long _previewBufferCapacityFrames;

    [Export(PropertyHint.File, "*.sf2")]
    public string SoundFontPath { get; set; } = "res://Music/SoundFonts/FluidR3_GM.sf2";

    [Export(PropertyHint.Range, "0.05,0.5,0.01")]
    public float AudioBufferLengthSeconds { get; set; } = 0.2f;

    public bool IsSoundFontLoaded => _playbackService is not null;
	public bool IsNoteExpressionPreviewPlaying => _previewActive || _previewDraining || _pairPreviewActive;
    public event Action? NoteExpressionPreviewFinished;
    public event Action? ScorePlaybackReady;
    public event Action<string>? AudioPlaybackWarning;
    public event Action<long>? AudioPlaybackUnderrun;
    public bool IsScoreReady => _scorePlaybackClock is not null && _audioPlayback is not null;
    public long AudioPlaybackUnderrunFrames => Math.Max(_lastAudioUnderrunFrames, _scorePlaybackClock?.AudioUnderrunFrames ?? 0);
    public bool NoteExpressionPreviewLoopEnabled
    {
        get => _previewLoop;
        set { _previewLoop = value; if (_previewActive) _previewStopAfterCycle = !value; }
    }
    public bool IsScorePlaying => _scoreFinishedGenerating || _scoreTailFramesRemaining > 0 || (_scorePlaybackClock?.IsPlaying ?? false);
    public bool MetronomeEnabled
    {
        get => _metronomeEnabled;
        set
        {
            _metronomeEnabled = value;
            if (_scorePlaybackClock is not null) _scorePlaybackClock.MetronomeEnabled = value;
        }
    }
    public bool CountInEnabled
    {
        get => _countInEnabled;
        set { _countInEnabled = value; if (_scorePlaybackClock is not null) _scorePlaybackClock.CountInEveryLoop = !_recordingTransport && value && _countInEveryLoop; }
    }
    public bool CountInEveryLoop
    {
        get => _countInEveryLoop;
        set
        {
            _countInEveryLoop = value;
            if (_scorePlaybackClock is not null) _scorePlaybackClock.CountInEveryLoop = !_recordingTransport && _countInEnabled && value;
        }
    }

    /// <summary>Whether the audible transport is still in its count-in.</summary>
    public bool IsAudibleCountIn
    {
        get
        {
            if (_scorePlaybackClock is null) return false;
            var queued = _audioPlayback is null ? 0 : Math.Max(0, _audioBufferCapacityFrames - _audioPlayback.GetFramesAvailable());
            return _scorePlaybackClock.IsCountInBehind(queued);
        }
    }
    public bool IsScorePaused => _scorePaused && IsScorePlaying;
    /// <summary>Audible score position estimated from generated audio and queued generator frames.</summary>
    public PlaybackPosition? VisualPosition
    {
        get
        {
            if (_scoreTailFramesRemaining > 0 || (_scoreFinishedGenerating && _completedScorePosition.HasValue) || _scorePlaybackClock is null) return _completedScorePosition;
            // A live score refresh can remap the clock while already generated audio
            // still reflects the previous map. Until the queue drains, use the clock
            // position to avoid showing an invalid jump across that discontinuity.
            if (_staleQueuedFrames > 0 || _audioPlayback is null) return _scorePlaybackClock.Position;
            var queuedFrames = Math.Max(0, _audioBufferCapacityFrames - _audioPlayback.GetFramesAvailable());
            return _scorePlaybackClock.GetPositionBehind(queuedFrames);
        }
    }

    /// <summary>Audible frame in this player's projected score or arrangement timeline.</summary>
    public long? VisualTimelineFrame
    {
        get
        {
            if (_scorePlaybackClock is null) return null;
            if (_staleQueuedFrames > 0 || _audioPlayback is null)
                return _scorePlaybackClock.GetTimelineFrameBehind(0);
            var queuedFrames = Math.Max(0, _audioBufferCapacityFrames - _audioPlayback.GetFramesAvailable());
            return _scorePlaybackClock.GetTimelineFrameBehind(queuedFrames);
        }
    }

    /// <summary>Section occurrences in the active timeline, aligned to VisualTimelineFrame.</summary>
    public IReadOnlyList<PlaybackSectionTimelineSpan> VisualSectionTimelineSpans =>
        _scorePlaybackClock?.SectionTimelineSpans ?? Array.Empty<PlaybackSectionTimelineSpan>();

    /// <summary>Audible location in this player's local timeline, in seconds.</summary>
    public double? VisualTimelineSeconds => VisualTimelineFrame is { } frame && _sampleRate > 0
        ? frame / (double)_sampleRate : null;

    /// <summary>Scheduled audio placements for the active score/arrangement projection.</summary>
    public IReadOnlyList<ScheduledAudioClip> ScheduledAudioClips =>
        _scorePlaybackClock?.AudioClips ?? Array.Empty<ScheduledAudioClip>();

    /// <summary>Output frame rate used by the score clock and its audio schedule.</summary>
    public int PlaybackSampleRate => _sampleRate;

    /// <summary>Creates a synth for offline export without sharing live voices or playback state.</summary>
    public SoundFontPlaybackService CreateExportService() => GetPlaybackService().CreateIsolatedInstance();

    /// <summary>Length of the active projected score/arrangement pass, in output frames.</summary>
    public long PlaybackTimelineDurationFrames => _scorePlaybackClock?.TimelineDurationFrames ?? 0;

    /// <summary>Converts a source-score location through the active player's compiled tempo map.</summary>
    public double? GetScoreElapsedSeconds(ScoreTime position) =>
        _scorePlaybackClock?.GetScoreElapsedSeconds(position);

    public bool LoopEnabled
    {
        get => _loopEnabled;
        set
        {
            _loopEnabled = value;
            if (_scorePlaybackClock is not null)
            {
                _scorePlaybackClock.LoopEnabled = value;
            }
        }
    }

    public override void _Ready()
    {
        _sampleRate = checked((int)Math.Round(AudioServer.GetMixRate()));
        _audioStream = new AudioStreamGenerator
        {
            MixRate = _sampleRate,
            BufferLength = Math.Clamp(AudioBufferLengthSeconds, 0.05f, 0.5f)
        };

        _audioPlayer = new AudioStreamPlayer
        {
            Stream = _audioStream,
            Bus = "Master"
        };
        AddChild(_audioPlayer);

        var previewStream = new AudioStreamGenerator { MixRate = _sampleRate, BufferLength = Math.Clamp(AudioBufferLengthSeconds, 0.05f, 0.5f) };
        _previewAudioPlayer = new AudioStreamPlayer { Stream = previewStream, Bus = "Master" };
        AddChild(_previewAudioPlayer);

        if (Godot.FileAccess.FileExists(SoundFontPath))
        {
            try
            {
                LoadSoundFont(SoundFontPath);
            }
            catch (Exception exception)
            {
                GD.PushError($"Could not load SoundFont '{SoundFontPath}': {exception.Message}");
            }
        }
        else
        {
            GD.PushWarning($"SoundFont file not found at '{SoundFontPath}'. Add FluidR3_GM.sf2 there to enable audio playback.");
        }
    }

    /// <summary>
    /// Loads an SF2 from a Godot resource or user path. Loading is synchronous and should
    /// be done during setup rather than while audio is playing.
    /// </summary>
    public void LoadSoundFont(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A SoundFont path is required.", nameof(path));
        }

        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new FileNotFoundException($"Could not open SoundFont '{path}'.", path);
        var soundFontBytes = file.GetBuffer(checked((long)file.GetLength()));
        if (soundFontBytes.Length == 0)
        {
            throw new InvalidDataException($"SoundFont '{path}' is empty.");
        }

        using var soundFontStream = new MemoryStream(soundFontBytes, writable: false);
        var newPlaybackService = new SoundFontPlaybackService(soundFontStream, _sampleRate);
        StopNoteExpressionPreview(false);
        if (_playbackService is not null)
        {
            _scorePlaybackClock?.Stop(_playbackService);
        }

        _audioPlayer.Stop();
        _audioPlayback = null;
        _scorePlaybackClock = null;
        _scorePaused = false;
        _scoreFinishedGenerating = false;
        _scoreTailFramesRemaining = 0;
        _completedScorePosition = null;
        _scoreAudioPreparationPending = false;
        _lastAudioUnderrunFrames = 0;
        _playbackService = newPlaybackService;
        _previewService = null;
        _audioPlayer.Play();
        _audioPlayer.StreamPaused = false;
        _audioPlayback = (AudioStreamGeneratorPlayback)_audioPlayer.GetStreamPlayback();
        _audioBufferCapacityFrames = _audioPlayback.GetFramesAvailable();
    }

    /// <summary>Starts score-wide playback from score position zero.</summary>
    public void PlayScore(ScoreDocument score, Guid? startMeasureId = null, bool recording = false,
        PlaybackVersionSelection? versionSelection = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (versionSelection is not null) _versionSelection = versionSelection;
        var playbackService = GetPlaybackService();
        _recordingTransport = recording;
        var normalizedStart = NormalizeStartMeasure(score, startMeasureId, _loopSelection);
        var newClock = new ScorePlaybackClock(score, _sampleRate, normalizedStart, _loopSelection, _metronomeEnabled,
            recording || _countInEnabled, !recording && _countInEnabled && _countInEveryLoop,
            versionSelection: _versionSelection);
        newClock.LoopEnabled = _loopSelection?.Mode == PlaybackLoopMode.None ? false : _loopEnabled;

        _scorePlaybackClock?.Stop(playbackService);
        _audioPlayer.Stop();
        _audioPlayback = null;
        _scorePlaybackClock = newClock;
        _staleQueuedFrames = 0;
        _scoreFinishedGenerating = false;
        _scoreTailFramesRemaining = 0;
        _completedScorePosition = null;
        var measures = score.Sections.SelectMany(section => section.Measures).ToArray();
        var scopedMode = _loopSelection?.Mode is PlaybackLoopMode.Measure or PlaybackLoopMode.Sections or PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop;
        var startMeasure = normalizedStart.HasValue
            ? measures.FirstOrDefault(measure => measure.Id == normalizedStart.Value)
            : scopedMode ? null : measures.FirstOrDefault();
        _startPosition = startMeasure is null ? null : new PlaybackPosition(startMeasure.Id, 0.0);
        _scorePaused = false;
        var generation = ++_scoreStartupGeneration;
        if (newClock.AudioClips.Count == 0)
        {
            CompleteScoreStartup(generation, string.Empty, startGenerator: true);
        }
        else
        {
            _scoreAudioPreparationPending = true;
            _ = PrepareAndStartScoreAsync(newClock, generation, startGenerator: true);
        }
    }

    /// <summary>Changes the active loop scope, restarting at its beginning and discarding queued audio.</summary>
    public void SetLoopSelection(ScoreDocument score, PlaybackLoopSelection selection)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(selection);
        var wasActive = IsScorePlaying;
        var wasPaused = IsScorePaused;
        _loopSelection = selection;
        if (!wasActive) return;

        var startMeasureId = NormalizeStartMeasure(score, null, selection);
        var playbackService = GetPlaybackService();
        _scoreStartupGeneration++;
        _scorePlaybackClock?.Stop(playbackService);
        _audioPlayer.Stop();
        _audioPlayback = null;
        _scorePlaybackClock = null;
        _scoreFinishedGenerating = false;
        _scoreTailFramesRemaining = 0;
        _staleQueuedFrames = 0;
        _completedScorePosition = null;
        PlayScore(score, startMeasureId);
        if (wasPaused) PauseScore();
    }

    /// <summary>Applies score edits to future playback without restarting the transport.</summary>
    public void RefreshScore(ScoreDocument score, PlaybackVersionSelection? versionSelection = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (versionSelection is not null) _versionSelection = versionSelection;
        if (_scorePlaybackClock is null || !_scorePlaybackClock.IsPlaying)
        {
            return;
        }

        _scorePlaybackClock.RefreshScore(score, _versionSelection);
        var refreshedStart = NormalizeStartMeasure(score, _startPosition?.MeasureId, _loopSelection);
        _startPosition = refreshedStart.HasValue ? new PlaybackPosition(refreshedStart.Value, 0.0) : null;
        _staleQueuedFrames = _audioPlayback is null ? 0 : Math.Max(0, _audioBufferCapacityFrames - _audioPlayback.GetFramesAvailable());
        var generation = ++_scoreStartupGeneration;
        if (_scorePlaybackClock.AudioClips.Count > 0)
        {
            _scoreAudioPreparationPending = true;
            _ = PrepareAndStartScoreAsync(_scorePlaybackClock, generation, startGenerator: _audioPlayback is null);
        }
        else
        {
            _scoreAudioPreparationPending = false;
            if (_audioPlayback is null)
            {
                CompleteScoreStartup(generation, string.Empty, startGenerator: true);
            }
        }
    }

    /// <summary>Pauses the audio stream without advancing the score clock or synth voices.</summary>
    public void PauseScore()
    {
        if (!IsScorePlaying || _audioPlayer is null)
        {
            return;
        }

        _scorePaused = true;
        _audioPlayer.StreamPaused = true;
    }

    /// <summary>Resumes a score that was paused, keeping its queued audio and event position.</summary>
    public void ResumeScore()
    {
        if (!IsScorePaused || _audioPlayer is null)
        {
            return;
        }

        _scorePaused = false;
        _audioPlayer.StreamPaused = false;
    }

    public void StopScore()
    {
        if (_scorePlaybackClock is null || _playbackService is null)
        {
            if (_completedScorePosition.HasValue) _completedScorePosition = _startPosition;
            return;
        }

        _scorePlaybackClock.Stop(_playbackService);
        _scoreStartupGeneration++;
        _scoreAudioPreparationPending = false;
        _audioPlayer.Stop();
        _audioPlayback = null;
        _scorePaused = false;
        _staleQueuedFrames = 0;
        _scoreFinishedGenerating = false;
        _scoreTailFramesRemaining = 0;
        _completedScorePosition = _startPosition;
        if (_audioPlayer is not null)
        {
            _audioPlayer.StreamPaused = false;
        }
    }

    private async Task PrepareAndStartScoreAsync(ScorePlaybackClock clock, int generation, bool startGenerator)
    {
        var warning = string.Empty;
        try
        {
            // File pages load on the mixer's worker. _Process stays idle until this
            // initial range is ready, so no callback waits on disk access.
            await clock.PrepareAudioAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            warning = $"Audio clip prebuffer failed; affected clips may be silent: {error.Message}";
        }
        CallDeferred(nameof(CompleteScoreStartup), generation, warning, startGenerator);
    }

    public void CompleteScoreStartup(int generation, string warning, bool startGenerator)
    {
        if (generation != _scoreStartupGeneration || _scorePlaybackClock is null || !_scorePlaybackClock.IsPlaying)
            return;

        _scoreAudioPreparationPending = false;

        if (!string.IsNullOrEmpty(warning))
        {
            GD.PushWarning(warning);
            AudioPlaybackWarning?.Invoke(warning);
        }

        if (startGenerator)
        {
            _audioPlayer.Play();
            _audioPlayer.StreamPaused = _scorePaused;
            _audioPlayback = (AudioStreamGeneratorPlayback)_audioPlayer.GetStreamPlayback();
            _audioBufferCapacityFrames = _audioPlayback.GetFramesAvailable();
            ScorePlaybackReady?.Invoke();
        }
    }

    public void NoteOn(int channel, int midiPitch, int velocity) => GetPlaybackService().NoteOn(channel, midiPitch, velocity);

    public void NoteOff(int channel, int midiPitch) => GetPlaybackService().NoteOff(channel, midiPitch);

    /// <summary>Plays a draft note on an independent synth and audio stream, leaving score playback untouched.</summary>
    public void PreviewNoteExpression(ScoreTrack track, int midiPitch, int velocity, ScoreTime duration,
        TempoResolution tempo, NoteExpression draftCurve, IReadOnlyList<NoteExpression>? noteExpressions = null, bool loop = true, string soundVariation = "")
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(draftCurve);
        draftCurve.Validate();
        if (midiPitch is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(midiPitch));
        var pieceGainPitch = -1;
        if (track.Kind == TrackKind.Percussion && track.HasConfiguredPercussionKit)
        {
            pieceGainPitch = midiPitch;
            midiPitch = SoundVariations.ResolvePercussionPitch(midiPitch, soundVariation);
        }
        var previewProgram = track.Kind == TrackKind.Percussion ? track.DefaultMidiProgram : SoundVariations.ResolveProgram(track.DefaultMidiProgram, soundVariation);
        if (velocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(velocity));
        if (!duration.IsInitialized || duration <= ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (tempo.WholeNotesPerMinute <= 0) throw new ArgumentOutOfRangeException(nameof(tempo));
        var service = _previewService ??= GetPlaybackService().CreateIsolatedInstance();
        if (_previewAudioPlayer is null) throw new InvalidOperationException("The audio node must be inside the scene tree before previewing.");

        var effects = (noteExpressions ?? Array.Empty<NoteExpression>()).Where(effect => effect.Kind != draftCurve.Kind)
            .Select(effect => effect.Clone()).Append(draftCurve.Clone()).ToArray();
        foreach (var effect in effects) effect.Validate();
        var frameDurations = NoteExpressionPreviewTiming.GetFrameDurations(duration, tempo, _sampleRate);
        if (_previewActive && _previewTrackId == track.Id && _previewPitch == midiPitch &&
            _previewNoteFrames == frameDurations.NoteFrames && _previewGapFrames == frameDurations.BeatGapFrames &&
            _previewChannel == (track.Kind == TrackKind.Percussion ? 9 : 0) &&
            _previewProgram == previewProgram && _previewVariation == soundVariation && Math.Abs(_previewVolumeDb - track.VolumeDb) < 0.000001 &&
            Math.Abs(_previewTempoWholeNotesPerMinute - (double)tempo.WholeNotesPerMinute) < 0.000001)
        {
            _previewVelocity = ApplyPreviewVelocity(velocity, effects);
            _previewSilent = _previewVelocity == 0;
            _previewExpressions = effects;
            _previewCurve = draftCurve.Clone();
            _previewLoop = loop;
            _previewStopAfterCycle = !loop;
            _previewRestartDelay = 0.12;
            return;
        }
        StopNoteExpressionPreview(false);
        _previewTrackId = track.Id;
        _previewChannel = track.Kind == TrackKind.Percussion ? 9 : 0;
        _previewPitch = midiPitch;
        _previewVelocity = ApplyPreviewVelocity(velocity, effects);
        _previewSilent = _previewVelocity == 0;
        _previewExpressions = effects;
        _previewCurve = draftCurve.Clone();
        _previewLoop = loop;
        _previewStopAfterCycle = !loop;
        _previewFrame = 0;
        _previewNoteFrames = frameDurations.NoteFrames;
        _previewGapFrames = frameDurations.BeatGapFrames;
        _previewTempoWholeNotesPerMinute = (double)tempo.WholeNotesPerMinute;
        _previewProgram = previewProgram;
        _previewVariation = soundVariation;
        _previewPieceGainPitch = pieceGainPitch;
        _previewPieceGainDb = pieceGainPitch >= 0 ? track.PercussionPieces.FirstOrDefault(piece => piece.MidiPitch == pieceGainPitch)?.GainDb ?? 0 : 0;
        _previewVolumeDb = track.VolumeDb;
        service.ProgramChange(track.Id, _previewChannel, track.DefaultMidiProgram);
        service.SetTrackVolume(track.Id, track.VolumeDb);
        if (_previewChannel == 9)
        {
            var piece = track.PercussionPieces.FirstOrDefault(item => item.MidiPitch == pieceGainPitch);
            if (piece is not null) service.SetDrumPieceGain(track.Id, pieceGainPitch, piece.GainDb);
        }
        var stream = (AudioStreamGenerator)_previewAudioPlayer.Stream;
        stream.MixRate = _sampleRate;
        _previewAudioPlayer.Stop();
        _previewAudioPlayer.Play();
        _previewAudioPlayer.StreamPaused = false;
        _previewAudioPlayback = (AudioStreamGeneratorPlayback)_previewAudioPlayer.GetStreamPlayback();
        _previewBufferCapacityFrames = _previewAudioPlayback.GetFramesAvailable();
        _previewActive = true;
        _previewDraining = false;
        StartPreviewCycle();
    }

    /// <summary>Auditions an authored hammer-on or pull-off pair through an isolated score clock and synth.</summary>
    public void PreviewStringArticulationPair(ScoreTrack sourceTrack, TabEvent predecessor, TabEvent destination,
        ScoreTime onsetGap, TempoResolution tempo)
    {
        ArgumentNullException.ThrowIfNull(sourceTrack);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(destination);
        if (_previewAudioPlayer is null) throw new InvalidOperationException("The audio node must be inside the scene tree before previewing.");

        StopNoteExpressionPreview(false);
        var previewScore = StringArticulationPreview.BuildScore(sourceTrack, predecessor, destination, onsetGap, tempo);
        var section = previewScore.Sections[0];

        _previewService ??= GetPlaybackService().CreateIsolatedInstance();
        var stream = (AudioStreamGenerator)_previewAudioPlayer.Stream;
        stream.MixRate = _sampleRate;
        _previewAudioPlayer.Stop();
        _previewAudioPlayer.Play();
        _previewAudioPlayer.StreamPaused = false;
        _previewAudioPlayback = (AudioStreamGeneratorPlayback)_previewAudioPlayer.GetStreamPlayback();
        _previewBufferCapacityFrames = _previewAudioPlayback.GetFramesAvailable();
        _pairPreviewClock = new ScorePlaybackClock(previewScore, _sampleRate, section.Measures[0].Id);
        _pairPreviewActive = true;
        _previewActive = false;
        _previewDraining = false;
        _previewRestartDelay = 0;
    }

    public void StopNoteExpressionPreview() => StopNoteExpressionPreview(true);

    private void StopNoteExpressionPreview(bool notify)
    {
        if (_pairPreviewClock is not null && _previewService is not null)
            _pairPreviewClock.Stop(_previewService);
        _pairPreviewClock = null;
        _pairPreviewActive = false;
        if (_previewService is not null) _previewService.AllNotesOff(_previewChannel);
        _previewRestartDelay = 0;
        _previewActive = false;
        _previewDraining = false;
        _previewAudioPlayer?.Stop();
        _previewAudioPlayback = null;
        if (notify) NoteExpressionPreviewFinished?.Invoke();
    }

    private void StartPreviewCycle()
    {
        var service = _previewService!;
        if (!_previewSilent)
        {
            if (_previewVariation.Length > 0 || _previewPieceGainPitch >= 0)
            {
                service.NoteOnExpressive(_previewTrackId, _previewChannel, _previewPitch, _previewVelocity, PreviewNoteId,
                    _previewChannel == 9 ? -1 : _previewProgram, _previewPieceGainDb, _previewPieceGainPitch);
            }
            else service.NoteOnExpressive(_previewTrackId, _previewChannel, _previewPitch, _previewVelocity, PreviewNoteId);
            ApplyPreviewExpression();
        }
    }

    private static readonly Guid PreviewNoteId = new("c4f6ca8f-8ee6-456a-86ca-92d57ee715b3");

    private void ApplyPreviewExpression()
    {
        if (_previewCurve is null) return;
        var x = Math.Clamp(_previewFrame / (double)_previewNoteFrames, 0d, 1d);
        var pitch = _previewExpressions.Where(effect => effect.Kind is ExpressionKind.Pitch or ExpressionKind.Vibrato).Sum(effect => effect.Sample(x));
        var tremolo = _previewExpressions.Where(effect => effect.Kind == ExpressionKind.Tremolo).Aggregate(1d, (value, effect) => value * effect.Sample(x));
        _previewService?.SetNoteExpression(_previewTrackId, PreviewNoteId, pitch, tremolo);
    }

    private static int ApplyPreviewVelocity(int velocity, IReadOnlyList<NoteExpression> effects) =>
        NoteExpressionPreviewTiming.ApplyVelocity(velocity, effects);

    public override void _Process(double delta)
    {
        if (_previewRestartDelay > 0)
        {
            _previewRestartDelay -= delta;
            if (_previewRestartDelay <= 0 && (_previewActive || _previewDraining)) RestartPreviewCycle();
        }
        if (_pairPreviewActive && _previewAudioPlayback is not null && _previewService is not null)
            RenderArticulationPairPreview();
        else if (_previewActive && _previewAudioPlayback is not null && _previewService is not null)
            RenderNoteExpressionPreview();
        if (_previewDraining && _previewRestartDelay <= 0 && _previewAudioPlayback is not null &&
            _previewBufferCapacityFrames - _previewAudioPlayback.GetFramesAvailable() == 0)
        {
            _previewAudioPlayer?.Stop();
            _previewAudioPlayback = null;
            _previewDraining = false;
            NoteExpressionPreviewFinished?.Invoke();
        }
        if (_scoreFinishedGenerating)
        {
            if (_scorePaused || _audioPlayback is null) return;
            if (_audioBufferCapacityFrames - _audioPlayback.GetFramesAvailable() > 0) return;
            _completedScorePosition ??= _scorePlaybackClock?.Position;
            _scorePlaybackClock = null;
            _scoreFinishedGenerating = false;
            return;
        }
        if (_scorePaused || _audioPlayback is null || _playbackService is null)
        {
            return;
        }
        if (_scoreAudioPreparationPending) return;

        var framesAvailable = _audioPlayback.GetFramesAvailable();
        while (framesAvailable > 0)
        {
            var frameCount = Math.Min(framesAvailable, RenderBlockFrames);
            if (_scoreTailFramesRemaining > 0) frameCount = (int)Math.Min(frameCount, _scoreTailFramesRemaining);
            var left = _leftBuffer.AsSpan(0, frameCount);
            var right = _rightBuffer.AsSpan(0, frameCount);
            if (_scoreTailFramesRemaining > 0)
            {
                _playbackService.Render(left, right);
                _scoreTailFramesRemaining -= frameCount;
                if (_scoreTailFramesRemaining == 0) _scoreFinishedGenerating = true;
            }
            else if (_scorePlaybackClock is null)
            {
                _playbackService.Render(left, right);
            }
            else
            {
                left.Clear(); right.Clear();
                _scorePlaybackClock.Render(_playbackService, left, right);
                var underruns = _scorePlaybackClock.AudioUnderrunFrames;
                if (underruns > _lastAudioUnderrunFrames)
                {
                    _lastAudioUnderrunFrames = underruns;
                    AudioPlaybackUnderrun?.Invoke(underruns);
                }
                if (!_scorePlaybackClock.IsPlaying)
                {
                    if (_scorePlaybackClock.HasTimeBasedEffects)
                    {
                        _completedScorePosition = _scorePlaybackClock.Position;
                        _scorePlaybackClock.Stop(_playbackService);
                        _scoreTailFramesRemaining = 2L * _sampleRate;
                    }
                    else _scoreFinishedGenerating = true;
                }
            }

            if (frameCount == RenderBlockFrames)
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    _stereoBuffer[frame] = new Vector2(_leftBuffer[frame], _rightBuffer[frame]);
                }

                if (!_audioPlayback.PushBuffer(_stereoBuffer))
                {
                    break;
                }
            }
            else
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    if (!_audioPlayback.PushFrame(new Vector2(_leftBuffer[frame], _rightBuffer[frame])))
                    {
                        return;
                    }
                }
            }

            _staleQueuedFrames = Math.Max(0, _staleQueuedFrames - frameCount);
            framesAvailable = _audioPlayback.GetFramesAvailable();
            if (_scoreFinishedGenerating) break;
        }
    }

    private void RenderArticulationPairPreview()
    {
        var clock = _pairPreviewClock!;
        var service = _previewService!;
        var playback = _previewAudioPlayback!;
        var available = playback.GetFramesAvailable();
        while (available > 0 && _pairPreviewActive)
        {
            var count = Math.Min(available, RenderBlockFrames);
            Array.Clear(_leftBuffer, 0, count);
            Array.Clear(_rightBuffer, 0, count);
            clock.Render(service, _leftBuffer.AsSpan(0, count), _rightBuffer.AsSpan(0, count));
            for (var index = 0; index < count; index++)
                if (!playback.PushFrame(new Vector2(_leftBuffer[index], _rightBuffer[index]))) return;
            available = playback.GetFramesAvailable();
            if (!clock.IsPlaying)
            {
                _pairPreviewActive = false;
                _pairPreviewClock = null;
                _previewDraining = true;
                break;
            }
        }
    }

    private void RestartPreviewCycle()
    {
        _previewService?.AllNotesOff(_previewChannel);
        _previewAudioPlayer?.Stop();
        _previewFrame = 0;
        _previewAudioPlayer?.Play();
        if (_previewAudioPlayer is not null)
        {
            _previewAudioPlayer.StreamPaused = false;
            _previewAudioPlayback = (AudioStreamGeneratorPlayback)_previewAudioPlayer.GetStreamPlayback();
            _previewBufferCapacityFrames = _previewAudioPlayback.GetFramesAvailable();
        }
        _previewActive = true;
        _previewDraining = false;
        StartPreviewCycle();
    }

    private void RenderNoteExpressionPreview()
    {
        var playback = _previewAudioPlayback!;
        var service = _previewService!;
        var available = playback.GetFramesAvailable();
        while (available > 0 && _previewActive)
        {
            var noteOn = _previewFrame < _previewNoteFrames;
            if (!noteOn && _previewFrame == _previewNoteFrames)
                service.NoteOff(_previewTrackId, _previewChannel, _previewPitch, PreviewNoteId);
            var boundary = noteOn ? _previewNoteFrames : _previewNoteFrames + _previewGapFrames;
            var curveBoundary = noteOn ? GetNextExpressionBoundaryFrame(_previewFrame) : boundary;
            var nextBoundary = Math.Min(boundary, curveBoundary);
            var count = (int)Math.Min(Math.Min(available, RenderBlockFrames), nextBoundary - _previewFrame);
            if (count <= 0) count = 1;
            if (noteOn) ApplyPreviewExpression();
            var left = _leftBuffer.AsSpan(0, count);
            var right = _rightBuffer.AsSpan(0, count);
            service.Render(left, right);
            for (var i = 0; i < count; i++)
                if (!playback.PushFrame(new Vector2(_leftBuffer[i], _rightBuffer[i]))) return;
            _previewFrame += count;
            available = playback.GetFramesAvailable();
            if (_previewFrame >= _previewNoteFrames + _previewGapFrames)
            {
                if (!NoteExpressionPreviewTiming.ShouldRepeat(_previewLoop, _previewStopAfterCycle))
                {
                    _previewActive = false;
                    _previewDraining = true;
                    break;
                }
                _previewFrame = 0;
                StartPreviewCycle();
            }
        }
    }

    private long GetNextExpressionBoundaryFrame(long currentFrame)
    {
        var next = _previewNoteFrames;
        foreach (var effect in _previewExpressions)
        foreach (var point in effect.Points)
        {
            var frame = (long)Math.Ceiling(point.X * _previewNoteFrames);
            if (frame > currentFrame && frame < next) next = frame;
        }
        return next;
    }

    private SoundFontPlaybackService GetPlaybackService() =>
        _playbackService ?? throw new InvalidOperationException("Load a SoundFont before sending note commands.");

    private static Guid? NormalizeStartMeasure(ScoreDocument score, Guid? preferred, PlaybackLoopSelection? selection)
    {
        var ordered = score.Sections.SelectMany(section => section.Measures).ToArray();
        if (selection?.Mode == PlaybackLoopMode.Measure)
            return selection.MeasureId.HasValue && ordered.Any(measure => measure.Id == selection.MeasureId.Value)
                ? selection.MeasureId : null;
        if (selection?.Mode == PlaybackLoopMode.Sections)
        {
            var ids = selection.SectionIds.ToHashSet();
            var inScope = score.Sections.Where(section => ids.Contains(section.Id))
                .SelectMany(section => section.Measures).ToArray();
            if (preferred.HasValue && inScope.Any(measure => measure.Id == preferred.Value)) return preferred;
            return inScope.FirstOrDefault()?.Id;
        }
        if (selection?.Mode is PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop)
        {
            var first = selection.SectionIds.Select(id => score.Sections.FirstOrDefault(section => section.Id == id))
                .Where(section => section is not null).SelectMany(section => section!.Measures).FirstOrDefault();
            return first?.Id;
        }
        return preferred.HasValue && ordered.Any(measure => measure.Id == preferred.Value)
            ? preferred : ordered.FirstOrDefault()?.Id;
    }
}

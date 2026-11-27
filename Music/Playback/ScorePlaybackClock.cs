#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using OpenTab.Music;

namespace OpenTab.Music.Playback;

/// <summary>A section occurrence in the clock's compiled playback timeline.</summary>
public readonly record struct PlaybackSectionTimelineSpan(Guid SectionId, int OccurrenceIndex, long StartFrame, long EndFrame);

/// <summary>
/// Compiles a score into a sample-frame event timeline and advances that timeline as
/// stereo audio is rendered.
/// </summary>
public sealed partial class ScorePlaybackClock
{
    private enum EventKind
    {
        SelectContent,
        BankSelect,
        ProgramChange,
        ScopeBoundary,
        LegatoNoteOn,
        NoteOff,
        PitchBendReset,
        PitchBendSet,
        NoteOn,
        ExpressiveNoteOn,
        NoteExpressionUpdate,
        MetronomeOn,
        MetronomeOff,
        CountInOn,
        CountInOff
    }

    private readonly record struct ScheduledEvent(long Frame, int Order, EventKind Kind, int Channel, int Value, int SecondaryValue, Guid NoteId = default, Guid TrackId = default, int ProgramOverride = -1, double PieceGainDb = 0, int PieceGainPitch = -1, int ContentChoice = -1, Guid ContentVersion = default, int StringNumber = 0, StringArticulation Articulation = StringArticulation.None, double MinimumExpression = 0, double MaximumExpression = 0);
    private readonly record struct MeasureSpan(Guid Id, long Start, long End, ScoreTime StartPosition, ScoreTime EndPosition, long SourceStart);

    private ScheduledEvent[] _events;
    private ScheduledEvent[] _baseEvents = Array.Empty<ScheduledEvent>();
    private ScheduledEvent[] _initialEvents = Array.Empty<ScheduledEvent>();
    private IReadOnlyList<ScheduledAudioClip> _audioClips = Array.Empty<ScheduledAudioClip>();
    private AudioClipMixer? _audioMixer;
    private int[] _usedChannels;
    private Dictionary<Guid, double> _trackVolumes;
    private Dictionary<Guid, double> _trackMasterGains;
    private Dictionary<(Guid TrackId, int Pitch), double> _pieceGains;
    private bool _volumeUpdatePending = true;
    private Dictionary<Guid, TrackEffectsChain> _trackEffects;
    private bool _effectsUpdatePending = true;
    private MeasureSpan[] _measures;
    private readonly HashSet<Guid> _activeNotes = new();
    // The MIDI synthesizer note-off API is pitch based. Keep per-event ownership so
    // one of two same-pitch tab strings cannot silence the other when it ends.
    private readonly Dictionary<Guid, ScheduledEvent> _activeNoteEvents = new();
    private readonly HashSet<Guid> _activeBends = new();
    // Absolute frame positions for note-offs belonging to notes already sounding.
    private readonly List<ScheduledEvent> _carriedEvents = new();
    private readonly int _sampleRate;
    private TempoMap _tempoMap;
    private long _loopDurationFrames;
    private long _baseLoopDurationFrames;
    private long _repeatCountInFrames;
    private ScheduledEvent[] _repeatEvents = Array.Empty<ScheduledEvent>();
    private bool _initialCountInPending;
    private bool _isInitialPass = true;
    private long _previousPassOffset;
    private long _previousPassDuration;
    private long _previousPassCountInFrames;
    private bool _previousPassWasInitial;
    private bool _hasPreviousPass;
    private int _nextEventIndex;
    private long _renderedFrames;
    private long _eventFrameOffset;
    private bool _stopped;
    private readonly PlaybackLoopSelection? _selection;
    private PlaybackSectionTimelineSpan[] _sectionTimelineSpans = Array.Empty<PlaybackSectionTimelineSpan>();
    private PlaybackVersionSelection _versionSelection;
    private bool _metronomeEnabled;
    private readonly bool _countInEnabled;
    private long _countInFrames;
    private bool _countInEveryLoop;

    private long _initialPositionFrame;
    private Guid? _initialMeasureId;

    public ScorePlaybackClock(ScoreDocument score, int sampleRate, Guid? startMeasureId = null,
        PlaybackLoopSelection? selection = null, bool metronomeEnabled = false,
        bool countInEnabled = false, bool countInEveryLoop = false,
        PlaybackVersionSelection? versionSelection = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        _sampleRate = sampleRate;
        _selection = selection;
        _versionSelection = versionSelection ?? PlaybackVersionSelection.Empty;
        _metronomeEnabled = metronomeEnabled;
        _countInEnabled = countInEnabled;
        _countInEveryLoop = countInEveryLoop;
        _trackVolumes = score.Tracks.ToDictionary(track => track.Id, track => track.VolumeDb);
        _trackMasterGains = score.Tracks.ToDictionary(track => track.Id,
            track => track.Kind == TrackKind.Percussion ? score.PercussionMasterGainDb : 0.0);
        _pieceGains = score.Tracks.Where(track => track.Kind == TrackKind.Percussion)
            .SelectMany(track => track.PercussionPieces.Select(piece => (track.Id, piece.MidiPitch, piece.GainDb)))
            .ToDictionary(piece => (piece.Id, piece.MidiPitch), piece => piece.GainDb);
        var tempoMap = score.CreateTempoMap();
        _tempoMap = tempoMap;
        _trackEffects = CompileTrackEffects(score, tempoMap, sampleRate);
        HasTimeBasedEffects = score.Tracks.Any(track => track.Kind is not (TrackKind.Audio or TrackKind.Ruler) &&
            track.Pedals.Any(pedal => pedal.Enabled && pedal.TypeId is "chorus" or "delay"));
        _loopDurationFrames = ToFrame(tempoMap.GetElapsedSeconds(GetScoreEndPosition(score)), sampleRate);
        _baseLoopDurationFrames = _loopDurationFrames;
        var measures = new List<MeasureSpan>();
        var measureStart = ScoreTime.Zero;
        foreach (var section in score.Sections)
        {
            foreach (var measure in section.Measures)
            {
                var end = measureStart + GetMeasureDuration(score, section, measure);
                var sourceStart = ToFrame(tempoMap.GetElapsedSeconds(measureStart), sampleRate);
                measures.Add(new MeasureSpan(measure.Id, sourceStart,
                    ToFrame(tempoMap.GetElapsedSeconds(end), sampleRate), measureStart, end, sourceStart));
                measureStart = end;
            }
        }
        _measures = measures.ToArray();

        var hasSolo = score.Tracks.Exists(track => track.IsSoloed);
        var activeTracks = new List<ScoreTrack>();
        foreach (var track in score.Tracks)
        {
            // Audio clips are rendered by the audio mixer, and ruler tracks only
            // provide section length. Neither is a MIDI instrument or channel.
            if (track.Kind is TrackKind.Audio or TrackKind.Ruler)
            {
                continue;
            }

            ValidateMidiValue(track.DefaultMidiProgram, "MIDI program", nameof(score));
            activeTracks.Add(track);
        }

        var melodicTracks = activeTracks.FindAll(track => track.Kind != TrackKind.Percussion);
        if (melodicTracks.Count > 15)
        {
            throw new ArgumentException("A MIDI score can use at most 15 melodic track channels because channel 10 is reserved for percussion.", nameof(score));
        }

        var channelByTrack = new Dictionary<Guid, int>(activeTracks.Count);
        var usedChannelsList = new List<int>();
        var melodicChannelIndex = 0;
        foreach (var track in activeTracks)
        {
            var channel = track.Kind == TrackKind.Percussion
                ? 9
                : melodicChannelIndex < 9 ? melodicChannelIndex++ : melodicChannelIndex++ + 1;
            channelByTrack.Add(track.Id, channel);
            if (!usedChannelsList.Contains(channel))
            {
                usedChannelsList.Add(channel);
            }
        }

        _usedChannels = usedChannelsList.ToArray();
        var events = new List<ScheduledEvent>();
        var tabOccurrences = new List<(ScoreTrack Track, TabEvent Note, int Channel, ScoreTime Onset, ScoreTime AuthoredEnd, Guid EventId, NoteExpression[] Expressions, int ContentChoice, Guid ContentVersion)>();
        var order = 0;
        foreach (var track in activeTracks)
        {
            if (track.Kind == TrackKind.Percussion)
            {
                // GM percussion uses MIDI channel 10 (zero-based channel 9). In the
                // supported SoundFont convention, select percussion bank via CC0=0.
                events.Add(new ScheduledEvent(0, order++, EventKind.BankSelect, channelByTrack[track.Id], 0, 0, TrackId: track.Id));
            }

            events.Add(new ScheduledEvent(0, order++, EventKind.ProgramChange, channelByTrack[track.Id], track.DefaultMidiProgram, 0, TrackId: track.Id));
        }

        measureStart = ScoreTime.Zero;
        foreach (var section in score.Sections)
        {
            for (var measureIndex = 0; measureIndex < section.Measures.Count; measureIndex++)
            {
                var measure = section.Measures[measureIndex];
                var measureDuration = GetMeasureDuration(score, section, measure);
                var signature = score.ResolveTimeSignature(section, measure);
                for (var beat = 0; beat < signature.Numerator; beat++)
                {
                    var clickPosition = measureStart + new ScoreTime(beat, signature.Denominator);
                    var clickFrame = ToFrame(tempoMap.GetElapsedSeconds(clickPosition), sampleRate);
                    var clickId = Guid.NewGuid();
                    events.Add(new ScheduledEvent(clickFrame, order++, EventKind.MetronomeOn, 9,
                        beat == 0 ? 76 : 77, beat == 0 ? 112 : 82, clickId, Guid.Empty));
                    events.Add(new ScheduledEvent(clickFrame + Math.Max(1, sampleRate / 30), order++, EventKind.MetronomeOff,
                        9, beat == 0 ? 76 : 77, 0, clickId, Guid.Empty));
                }
                foreach (var track in activeTracks)
                {
                    var patternLength = score.GetTrackPatternLength(section, track);
                    if (patternLength == 0 && !measure.TrackMeasureVersions.Any(version => version.TrackId == track.Id))
                    {
                        continue;
                    }

                    var linkedTrack = (track.Kind != TrackKind.Percussion || track.HasConfiguredPercussionKit) && section.LeadTrackId != track.Id;
                    var occurrence = linkedTrack
                        ? score.GetNonPercussionOccurrence(section, track, measureIndex)
                        : default;


                    var sourceMeasureIndex = linkedTrack
                        ? occurrence.SourceMeasureIndex ?? measureIndex
                        : section.LeadTrackId == track.Id ? measureIndex : patternLength > 0 ? measureIndex % patternLength : measureIndex;
                    var sourceMeasure = section.Measures[sourceMeasureIndex];
                    var originalTrackMeasure = linkedTrack
                        ? occurrence.SourceTrackMeasure
                        : sourceMeasure.GetTrackMeasure(track.Id);
                    var isRepeatedOccurrence = linkedTrack
                        ? occurrence.IsRepeat
                        : measureIndex != sourceMeasureIndex;
                    if (!channelByTrack.TryGetValue(track.Id, out var channel))
                    {
                        continue;
                    }

                    if (track.IsMuted || (hasSolo && !track.IsSoloed))
                    {
                        continue;
                    }

                    var choiceIndex = AddSourceContentChoice(score, section, measure, track, measureStart, events, ref order);
                    var candidates = new List<(Guid Id, TrackMeasure? Content)> { (Guid.Empty, originalTrackMeasure) };
                    candidates.AddRange(measure.TrackMeasureVersions.Where(version => version.TrackId == track.Id)
                        .Select(version => (version.Id, (TrackMeasure?)version.Content)));
                    foreach (var candidate in candidates)
                    {
                        var trackMeasure = candidate.Content;
                        if (trackMeasure is null) continue;
                        var candidateEventStart = events.Count;
                        // TAB notes belong to their source pattern measure but resolve at
                        // each destination measure onset. Unlike legacy beats, their ends
                        // may cross bars and sections.
                        if (trackMeasure.UsesTabEvents)
                        {
                            foreach (var authoredNote in trackMeasure.TabEvents)
                            {
                                var note = authoredNote;
                                if (track.Kind == TrackKind.Percussion && track.HasConfiguredPercussionKit)
                                {
                                    if (note.StringNumber < 1 || note.StringNumber > track.PercussionPieces.Count) continue;
                                    var piece = track.PercussionPieces[note.StringNumber - 1];
                                    if (piece.IsMuted) continue;
                                    // Kit pitch belongs to the lane; never trust editable fret/pitch data.
                                    var resolvedPitch = SoundVariations.ResolvePercussionPitch(piece.MidiPitch, authoredNote.SoundVariation);
                                    note = new TabEvent(note.StringNumber, 0, resolvedPitch, note.Velocity, note.Onset, note.Duration)
                                    { Id = note.Id, AllowsBarOverflow = note.AllowsBarOverflow, SoundVariation = authoredNote.SoundVariation };
                                    note.Expressions.AddRange(authoredNote.Expressions);
                                }
                                ValidateMidiValue(note.MidiPitch, "MIDI pitch", nameof(score));
                                ValidateVelocity(note.Velocity, nameof(score));
                                if (note.Onset < ScoreTime.Zero || note.Duration <= ScoreTime.Zero)
                                    continue;
                                if (note.Onset >= measureDuration && !note.AllowsBarOverflow)
                                    continue;
                                var onset = measureStart + note.Onset;
                                var eventId = isRepeatedOccurrence
                                    ? CreateRepeatedEventId(note.Id, measure.Id)
                                    : note.Id;
                                tabOccurrences.Add((track, note, channel, onset, onset + note.Duration, eventId, note.Expressions.ToArray(), choiceIndex, candidate.Id));
                            }
                        }

                        if (!trackMeasure.UsesTabEvents)
                        foreach (var voice in trackMeasure.Voices)
                        {
                            var beatOffset = ScoreTime.Zero;
                            foreach (var beat in voice.Beats)
                            {
                                // Overfull bars are valid while editing. Ignore material past the
                                // bar and clip its last sounding beat at the bar boundary.
                                if (beatOffset >= measureDuration)
                                {
                                    break;
                                }
                                var beatEnd = beatOffset + beat.Duration;
                                var onset = measureStart + beatOffset;
                                var end = measureStart + (beatEnd < measureDuration ? beatEnd : measureDuration);
                                var onsetFrame = ToFrame(tempoMap.GetElapsedSeconds(onset), sampleRate);
                                var endFrame = ToFrame(tempoMap.GetElapsedSeconds(end), sampleRate);
                                var beatEventId = isRepeatedOccurrence
                                    ? CreateRepeatedEventId(beat.Id, measure.Id)
                                    : beat.Id;
                                if (beat.PitchBendSemitones != 0)
                                {
                                    events.Add(new ScheduledEvent(onsetFrame, order++, EventKind.PitchBendSet, channel,
                                        ToPitchBendValue(beat.PitchBendSemitones), 0, beatEventId, track.Id));
                                    events.Add(new ScheduledEvent(endFrame, order++, EventKind.PitchBendReset, channel, 0, 0, beatEventId, track.Id));
                                }

                                foreach (var note in beat.Notes)
                                {
                                    ValidateMidiValue(note.MidiPitch, "MIDI pitch", nameof(score));
                                    ValidateVelocity(note.Velocity, nameof(score));
                                    var noteEventId = isRepeatedOccurrence
                                        ? CreateRepeatedEventId(note.Id, measure.Id)
                                        : note.Id;
                                    events.Add(new ScheduledEvent(onsetFrame, order++, EventKind.NoteOn, channel, note.MidiPitch, note.Velocity, noteEventId, track.Id));
                                    events.Add(new ScheduledEvent(endFrame, order++, EventKind.NoteOff, channel, note.MidiPitch, 0, noteEventId, track.Id));
                                }

                                beatOffset += beat.Duration;
                            }
                        }
                        for (var candidateEventIndex = candidateEventStart; candidateEventIndex < events.Count; candidateEventIndex++)
                            events[candidateEventIndex] = events[candidateEventIndex] with { ContentChoice = choiceIndex, ContentVersion = candidate.Id };
                    }
                }
                measureStart += measureDuration;
            }
        }

        // Resolve authored sustains against the complete playback order so a later
        // same-string onset cuts the earlier note even across repeated cells/sections.
        var scoreEnd = GetScoreEndPosition(score);
        var legatoTargets = tabOccurrences.Where(item => item.Note.IncomingArticulation != StringArticulation.None &&
            item.Track.Kind != TrackKind.Percussion).ToArray();
        foreach (var occurrence in tabOccurrences)
        {
            var nextSameStringOnset = tabOccurrences
                .Where(candidate => !_hasContentChoices && candidate.Track.Id == occurrence.Track.Id
                    && candidate.Note.StringNumber == occurrence.Note.StringNumber
                    && candidate.Onset > occurrence.Onset)
                .Select(candidate => candidate.Onset)
                .DefaultIfEmpty(scoreEnd)
                .Min();
            var end = occurrence.AuthoredEnd < nextSameStringOnset
                ? occurrence.AuthoredEnd : nextSameStringOnset;
            if (end > scoreEnd) end = scoreEnd;
            if (end <= occurrence.Onset) continue;
            var onsetFrame = ToFrame(tempoMap.GetElapsedSeconds(occurrence.Onset), sampleRate);
            var endFrame = ToFrame(tempoMap.GetElapsedSeconds(end), sampleRate);
            var velocity = ApplyVelocityExpression(occurrence.Note.Velocity, occurrence.Expressions);
            if (velocity == 0) continue;
            var automated = occurrence.Expressions.Where(expression => expression.Kind is ExpressionKind.Pitch or ExpressionKind.Vibrato or ExpressionKind.Tremolo).ToArray();
            var variation = occurrence.Note.SoundVariation;
            var programOverride = occurrence.Track.Kind == TrackKind.Percussion ? -1 : SoundVariations.ResolveProgram(occurrence.Track.DefaultMidiProgram, variation);
            var hasVariation = !string.IsNullOrEmpty(variation);
            var incoming = occurrence.Track.Kind != TrackKind.Percussion ? occurrence.Note.IncomingArticulation : StringArticulation.None;
            // Any viable source may need its own voice, including sources in alternative
            // measure versions whose actual selection is decided during playback.
            var canLeadLegato = occurrence.Track.Kind != TrackKind.Percussion && legatoTargets.Any(next =>
                next.Track.Id == occurrence.Track.Id && next.Note.StringNumber == occurrence.Note.StringNumber &&
                next.Note.IncomingArticulation != StringArticulation.None && next.Onset > occurrence.Onset &&
                next.Onset <= occurrence.AuthoredEnd && Math.Abs(next.Note.MidiPitch - occurrence.Note.MidiPitch) <= 24);
            var pieceGainDb = 0.0;
            var pieceGainPitch = -1;
            if (occurrence.Track.Kind == TrackKind.Percussion && occurrence.Track.HasConfiguredPercussionKit
                && occurrence.Note.StringNumber >= 1 && occurrence.Note.StringNumber <= occurrence.Track.PercussionPieces.Count)
            {
                pieceGainDb = occurrence.Track.PercussionPieces[occurrence.Note.StringNumber - 1].GainDb;
                pieceGainPitch = occurrence.Track.PercussionPieces[occurrence.Note.StringNumber - 1].MidiPitch;
                programOverride = -2; // mark an explicit per-hit gain override, including 0 dB
            }
            var occurrenceEventStart = events.Count;
            var onsetKind = incoming != StringArticulation.None ? EventKind.LegatoNoteOn :
                automated.Length > 0 || hasVariation || canLeadLegato ? EventKind.ExpressiveNoteOn : EventKind.NoteOn;
            var pitchEffects = automated.Where(effect => effect.Kind is ExpressionKind.Pitch or ExpressionKind.Vibrato).ToArray();
            var minimumExpression = pitchEffects.Sum(effect => effect.Points.Count == 0 ? 0 : effect.Points.Min(point => point.Y));
            var maximumExpression = pitchEffects.Sum(effect => effect.Points.Count == 0 ? 0 : effect.Points.Max(point => point.Y));
            events.Add(new ScheduledEvent(onsetFrame, order++, onsetKind, occurrence.Channel,
                occurrence.Note.MidiPitch, velocity, occurrence.EventId, occurrence.Track.Id, programOverride, pieceGainDb, pieceGainPitch,
                Articulation: incoming, MinimumExpression: minimumExpression, MaximumExpression: maximumExpression));
            if (automated.Length > 0)
            {
                var durationSeconds = tempoMap.GetElapsedSeconds(end) - tempoMap.GetElapsedSeconds(occurrence.Onset);
                var subdivisions = Math.Clamp((int)Math.Ceiling(durationSeconds / 0.015), 1, 2048);
                var points = automated.SelectMany(expression => expression.Points).Select(point => Math.Clamp(point.X, 0, 1))
                    .Concat(Enumerable.Range(0, subdivisions).Select(step => step / (double)subdivisions))
                    .Distinct().OrderBy(x => x);
                foreach (var x in points)
                {
                    var position = occurrence.Onset + (end - occurrence.Onset) * ToScoreTime(x);
                    var frame = ToFrame(tempoMap.GetElapsedSeconds(position), sampleRate);
                    var pitch = automated.Where(expression => expression.Kind is ExpressionKind.Pitch or ExpressionKind.Vibrato)
                        .Sum(expression => expression.Sample(x));
                    pitch = Math.Clamp(pitch, -24d, 24d);
                    var tremolo = automated.Where(expression => expression.Kind == ExpressionKind.Tremolo)
                        .Aggregate(1.0, (value, expression) => value * expression.Sample(x));
                    events.Add(new ScheduledEvent(frame, order++, EventKind.NoteExpressionUpdate, occurrence.Channel,
                        (int)Math.Round(pitch * 1000, MidpointRounding.AwayFromZero),
                        (int)Math.Round(tremolo * 1000, MidpointRounding.AwayFromZero), occurrence.EventId, occurrence.Track.Id));
                }
            }
            events.Add(new ScheduledEvent(endFrame, order++, EventKind.NoteOff, occurrence.Channel,
                occurrence.Note.MidiPitch, 0, occurrence.EventId, occurrence.Track.Id));
            for (var occurrenceEventIndex = occurrenceEventStart; occurrenceEventIndex < events.Count; occurrenceEventIndex++)
                events[occurrenceEventIndex] = events[occurrenceEventIndex] with
                { ContentChoice = occurrence.ContentChoice, ContentVersion = occurrence.ContentVersion, StringNumber = occurrence.Note.StringNumber };
        }

        events.Sort(static (left, right) =>
        {
            var frame = left.Frame.CompareTo(right.Frame);
            if (frame != 0)
            {
                return frame;
            }

            var priority = left.Kind.CompareTo(right.Kind);
            return priority != 0 ? priority : left.Order.CompareTo(right.Order);
        });
        var startIndex = startMeasureId.HasValue
            ? Array.FindIndex(_measures, measure => measure.Id == startMeasureId.Value)
            : -1;
        _initialPositionFrame = startIndex >= 0 ? _measures[startIndex].Start : 0;
        _initialMeasureId = startIndex >= 0 ? _measures[startIndex].Id :
            _measures.Length > 0 ? _measures[0].Id : null;
        if (_initialPositionFrame > 0)
        {
            foreach (var setup in events.Where(item => item.Frame == 0 &&
                         (item.Kind is EventKind.BankSelect or EventKind.ProgramChange)))
                _carriedEvents.Add(setup with { Frame = _initialPositionFrame });
        }
        events.Sort(static (left, right) =>
        {
            var frame = left.Frame.CompareTo(right.Frame);
            if (frame != 0) return frame;
            var priority = left.Kind.CompareTo(right.Kind);
            return priority != 0 ? priority : left.Order.CompareTo(right.Order);
        });
        _events = events.ToArray();
        if (selection is not null && selection.Mode is PlaybackLoopMode.Measure or PlaybackLoopMode.Sections or PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop)
        {
            ApplyScopedTimeline(score, selection);
            startIndex = startMeasureId.HasValue
                ? Array.FindIndex(_measures, measure => measure.Id == startMeasureId.Value)
                : -1;
            _initialPositionFrame = startIndex >= 0 ? _measures[startIndex].Start : 0;
            _initialMeasureId = startIndex >= 0 ? _measures[startIndex].Id :
                _measures.Length > 0 ? _measures[0].Id : null;
        }
        ConfigureContentChoices(score);
        if (_countInEnabled && _initialMeasureId is { } countInMeasureId)
        {
            var measure = _measures.First(item => item.Id == countInMeasureId);
            var section = score.Sections.First(item => item.Measures.Any(candidate => candidate.Id == countInMeasureId));
            var sourceMeasure = section.Measures.First(item => item.Id == countInMeasureId);
            var signature = score.ResolveTimeSignature(section, sourceMeasure);
            _countInFrames = ToFrame(_tempoMap.GetElapsedSeconds(measure.StartPosition + signature.BarDuration), sampleRate)
                - ToFrame(_tempoMap.GetElapsedSeconds(measure.StartPosition), sampleRate);
            var baseEvents = _events;
            var initialCountEvents = BuildCountInEvents(measure.StartPosition, signature, _initialPositionFrame, order);
            var loopMeasure = _measures[0];
            var loopSection = score.Sections.First(item => item.Measures.Any(candidate => candidate.Id == loopMeasure.Id));
            var loopSourceMeasure = loopSection.Measures.First(item => item.Id == loopMeasure.Id);
            var loopSignature = score.ResolveTimeSignature(loopSection, loopSourceMeasure);
            _repeatCountInFrames = ToFrame(_tempoMap.GetElapsedSeconds(loopMeasure.StartPosition + loopSignature.BarDuration), sampleRate)
                - ToFrame(_tempoMap.GetElapsedSeconds(loopMeasure.StartPosition), sampleRate);
            var repeatCountEvents = BuildCountInEvents(loopMeasure.StartPosition, loopSignature, 0, order);
            _baseEvents = baseEvents;
            _repeatEvents = baseEvents.Select(item => item with
                { Frame = checked(item.Frame + _repeatCountInFrames) })
                .Concat(repeatCountEvents).OrderBy(item => item.Frame).ThenBy(item => item.Kind).ThenBy(item => item.Order).ToArray();
            _events = baseEvents.Select(item => item with
                { Frame = checked(item.Frame + _countInFrames) })
                .Concat(initialCountEvents).OrderBy(item => item.Frame).ThenBy(item => item.Kind).ThenBy(item => item.Order).ToArray();
            _loopDurationFrames = checked(_loopDurationFrames + _countInFrames);
            _initialCountInPending = true;
        }
        _audioClips = AudioClipSchedule.Build(score, sampleRate, selection);
        if (_audioClips.Count > 0)
        {
            _audioMixer = new AudioClipMixer(score.AudioAssets, score.Tracks, sampleRate);
            _audioMixer.Configure(_audioClips);
            _audioMixer.Seek(_initialPositionFrame);
        }
        if (_initialPositionFrame > 0)
        {
            foreach (var setup in _events.Where(item => item.Frame == 0 &&
                         item.Kind is EventKind.BankSelect or EventKind.ProgramChange))
                _carriedEvents.Add(setup with { Frame = _initialPositionFrame });
        }
        _renderedFrames = _initialPositionFrame;
        _initialEvents = _events;
        if (_baseEvents.Length == 0) _baseEvents = _events;
        _nextEventIndex = Array.FindIndex(_events, item => item.Frame >= _initialPositionFrame);
        if (_nextEventIndex < 0) _nextEventIndex = _events.Length;
    }

    public bool MetronomeEnabled
    {
        get => _metronomeEnabled;
        set => _metronomeEnabled = value;
    }

    public bool CountInEveryLoop
    {
        get => _countInEveryLoop;
        set
        {
            if (!_countInEnabled || _countInEveryLoop == value) return;
            _countInEveryLoop = value;
        }
    }

    public bool IsCountInBehind(long framesBehind)
    {
        if (framesBehind < 0) throw new ArgumentOutOfRangeException(nameof(framesBehind));
        if (!_countInEnabled) return false;
        var frame = Math.Max(0, _renderedFrames - framesBehind);
        if (!TryGetPass(frame, out var offset, out var isInitial, out var countInFrames)) return false;
        var local = frame - offset;
        var start = isInitial ? _initialPositionFrame : 0;
        return countInFrames > 0 && local >= start && local < start + countInFrames;
    }

    private bool TryGetPass(long frame, out long offset, out bool isInitial, out long countInFrames)
    {
        if (frame >= _eventFrameOffset)
        {
            offset = _eventFrameOffset;
            isInitial = _isInitialPass;
            countInFrames = _countInFrames;
            return true;
        }
        if (_hasPreviousPass && frame >= _previousPassOffset &&
            frame < _previousPassOffset + _previousPassDuration)
        {
            offset = _previousPassOffset;
            isInitial = _previousPassWasInitial;
            countInFrames = _previousPassCountInFrames;
            return true;
        }
        offset = 0;
        isInitial = false;
        countInFrames = 0;
        return false;
    }

    /// <summary>Audio clip frame ranges projected into this clock's local timeline.</summary>
    public IReadOnlyList<ScheduledAudioClip> AudioClips => _audioClips;

    /// <summary>Number of timeline frames that could not be mixed because source pages were not ready.</summary>
    public long AudioUnderrunFrames => _audioMixer?.UnderrunFrames ?? 0;

    /// <summary>Preloads audio near the current cursor. Call before starting or resuming generation.</summary>
    public Task PrepareAudioAsync(int frameCount = 8192) => _audioMixer is null
        ? Task.CompletedTask
        : _audioMixer.PrepareAsync(Math.Max(0, _renderedFrames - _eventFrameOffset - _countInFrames), frameCount);

    private void ApplyScopedTimeline(ScoreDocument score, PlaybackLoopSelection selection)
    {
        var selectedSections = selection.Mode == PlaybackLoopMode.Sections
            ? selection.SectionIds.ToHashSet()
            : new HashSet<Guid>();
        var spans = new List<MeasureSpan>();
        var sectionTimelineSpans = new List<PlaybackSectionTimelineSpan>();
        var localStart = 0L;
        var sectionStartIndex = 0;
        var sections = selection.Mode is PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop
            ? selection.SectionIds.Select(id => score.Sections.FirstOrDefault(section => section.Id == id)).Where(section => section is not null).Cast<ScoreSection>().ToArray()
            : score.Sections.ToArray();
        for (var occurrenceIndex = 0; occurrenceIndex < sections.Length; occurrenceIndex++)
        {
            var section = sections[occurrenceIndex];
            var occurrenceStart = localStart;
            sectionStartIndex = score.Sections.TakeWhile(item => item.Id != section.Id).Sum(item => item.Measures.Count);
            var sectionEndIndex = sectionStartIndex + section.Measures.Count;
            var includeSection = selectedSections.Contains(section.Id) || selection.Mode is PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop;
            for (var index = sectionStartIndex; index < sectionEndIndex; index++)
            {
                var source = _measures[index];
                var include = selection.Mode == PlaybackLoopMode.Measure
                    ? source.Id == selection.MeasureId
                    : includeSection;
                if (include)
                {
                    var duration = source.End - source.Start;
                    spans.Add(source with { Start = localStart, End = checked(localStart + duration) });
                    localStart = checked(localStart + duration);
                }
            }
            sectionTimelineSpans.Add(new PlaybackSectionTimelineSpan(section.Id, occurrenceIndex, occurrenceStart, localStart));
            sectionStartIndex = sectionEndIndex;
        }

        if (selection.Mode == PlaybackLoopMode.Measure && selection.MeasureId is null)
            spans.Clear();
        var sourceChoices = _contentChoices;
        _contentChoices = new List<ContentChoice>();
        var projectedChoices = new Dictionary<(int Source, int Run), int>();
        var projected = new List<ScheduledEvent>();
        var order = 0;
        var run = 0;
        MeasureSpan? previous = null;
        foreach (var span in spans)
        {
            if (previous is { } preceding && preceding.EndPosition != span.StartPosition) run++;
            var source = _measures.First(item => item.Id == span.Id);
            var delta = span.Start - source.Start;
            foreach (var item in _events)
            {
                if (item.Frame >= source.Start && item.Frame < source.End)
                {
                    var eventId = item.NoteId;
                    if (selection.Mode is PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop && eventId != Guid.Empty)
                    {
                        var runId = new byte[16];
                        BitConverter.TryWriteBytes(runId.AsSpan(), run);
                        eventId = CreateRepeatedEventId(eventId, new Guid(runId));
                    }
                    var choice = item.ContentChoice;
                    if (choice >= 0)
                    {
                        if (!projectedChoices.TryGetValue((choice, run), out var projectedChoice))
                        {
                            projectedChoice = _contentChoices.Count;
                            var copy = sourceChoices[choice].Copy();
                            copy.StartFrame = checked(copy.StartFrame + delta);
                            _contentChoices.Add(copy);
                            projectedChoices.Add((choice, run), projectedChoice);
                        }
                        choice = projectedChoice;
                    }
                    projected.Add(item with { Frame = checked(item.Frame + delta), Order = order++, NoteId = eventId, ContentChoice = choice });
                }
            }
            previous = span;
        }
        if (spans.Count > 0)
        {
            projected.RemoveAll(item => item.Kind is EventKind.BankSelect or EventKind.ProgramChange);
            foreach (var setup in _events.Where(item => item.Frame == 0 &&
                         item.Kind is EventKind.BankSelect or EventKind.ProgramChange))
                projected.Add(setup with { Frame = 0, Order = order++ });
        }
        // Release notes and bends at every compacted jump and at the loop boundary.
        for (var index = 0; index < spans.Count; index++)
        {
            var current = spans[index];
            var next = spans[(index + 1) % spans.Count];
            var isJump = current.EndPosition != next.StartPosition || index == spans.Count - 1;
            if (!isJump) continue;
            var boundary = current.End;
            if (index < spans.Count - 1)
                projected.Add(new ScheduledEvent(boundary, order++, EventKind.ScopeBoundary, 0, 0, 0));
            var activeNotes = projected.Where(item => (item.Kind is EventKind.NoteOn or EventKind.ExpressiveNoteOn or EventKind.LegatoNoteOn or EventKind.MetronomeOn) && item.Frame < boundary)
                .Select(item => item.NoteId).ToHashSet();
            foreach (var noteOn in projected.Where(item => (item.Kind is EventKind.NoteOn or EventKind.ExpressiveNoteOn or EventKind.LegatoNoteOn or EventKind.MetronomeOn) && activeNotes.Contains(item.NoteId)).ToArray())
            {
                var releaseKind = noteOn.Kind == EventKind.MetronomeOn ? EventKind.MetronomeOff : EventKind.NoteOff;
                if (!projected.Any(item => item.Kind == releaseKind && item.NoteId == noteOn.NoteId && item.Frame <= boundary))
                    projected.Add(new ScheduledEvent(boundary, order++, releaseKind, noteOn.Channel, noteOn.Value, 0, noteOn.NoteId, noteOn.TrackId,
                        ContentChoice: noteOn.ContentChoice, ContentVersion: noteOn.ContentVersion, StringNumber: noteOn.StringNumber));
            }
            foreach (var bend in projected.Where(item => item.Kind == EventKind.PitchBendSet && item.Frame < boundary).ToArray())
                if (!projected.Any(item => item.Kind == EventKind.PitchBendReset && item.NoteId == bend.NoteId && item.Frame <= boundary))
                    projected.Add(new ScheduledEvent(boundary, order++, EventKind.PitchBendReset, bend.Channel, 0, 0, bend.NoteId, bend.TrackId,
                        ContentChoice: bend.ContentChoice, ContentVersion: bend.ContentVersion));
        }
        projected.Sort(static (left, right) => left.Frame != right.Frame ? left.Frame.CompareTo(right.Frame) :
            left.Kind != right.Kind ? left.Kind.CompareTo(right.Kind) : left.Order.CompareTo(right.Order));
        _events = projected.ToArray();
        _measures = spans.ToArray();
        _sectionTimelineSpans = sectionTimelineSpans.ToArray();
        _loopDurationFrames = localStart;
        _baseLoopDurationFrames = localStart;
        _initialPositionFrame = 0;
        _initialMeasureId = _measures.Length > 0 ? _measures[0].Id : null;
        _carriedEvents.Clear();
    }

    /// <summary>
    /// Recompiles upcoming events without restarting the audio stream or already sounding
    /// notes. Call on the same thread as Render after an edit to the score.
    /// </summary>
    public void RefreshScore(ScoreDocument score, PlaybackVersionSelection? versionSelection = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (_stopped) return;
        _versionSelection = versionSelection ?? _versionSelection;
        var replacement = new ScorePlaybackClock(score, _sampleRate, startMeasureId: _initialMeasureId, selection: _selection,
            metronomeEnabled: _metronomeEnabled, countInEnabled: _countInEnabled,
            countInEveryLoop: _countInEveryLoop, versionSelection: _versionSelection);
        var wasInitialPass = _isInitialPass;
        var wasInCountIn = IsCountInBehind(0);
        var oldPassStart = _eventFrameOffset;
        var elapsedCountIn = wasInCountIn
            ? Math.Max(0, _renderedFrames - oldPassStart - (wasInitialPass ? _initialPositionFrame : 0))
            : 0;
        var oldPosition = wasInCountIn
            ? wasInitialPass ? _initialPositionFrame : 0
            : Math.Clamp(_renderedFrames - oldPassStart - _countInFrames, 0, _baseLoopDurationFrames);
        var oldMeasureIndex = Array.FindIndex(_measures,
            measure => oldPosition >= measure.Start && oldPosition < measure.End);
        var currentMeasureWasDeleted = oldMeasureIndex >= 0 &&
            !replacement._measures.Any(measure => measure.Id == _measures[oldMeasureIndex].Id);
        var mappedPosition = MapPosition(oldPosition, replacement);
        var oldOffset = _eventFrameOffset;
        var boundaryNotDispatched = currentMeasureWasDeleted || (HasPendingEvents &&
            _events[_nextEventIndex].Frame + oldOffset == _renderedFrames) ||
            replacement._events.Any(item => item.Frame == mappedPosition &&
                (((item.Kind is EventKind.NoteOff or EventKind.MetronomeOff) && _activeNotes.Contains(item.NoteId)) ||
                 (item.Kind == EventKind.PitchBendReset && _activeBends.Contains(item.NoteId))));

        // Retain the originally scheduled ends of notes already dispatched, even if
        // their source beat was removed, muted, or moved by the edit.
        foreach (var pending in _events.Skip(_nextEventIndex))
        {
            if ((((pending.Kind is EventKind.NoteOff or EventKind.MetronomeOff) && _activeNotes.Contains(pending.NoteId)) ||
                 (pending.Kind == EventKind.PitchBendReset && _activeBends.Contains(pending.NoteId)))
                && !_carriedEvents.Any(item => item.Kind == pending.Kind && item.NoteId == pending.NoteId))
            {
                _carriedEvents.Add(pending with { Frame = checked(pending.Frame + oldOffset) });
            }
        }

        PreserveContentProgress(replacement);
        _versionSelection = replacement._versionSelection;
        _baseEvents = replacement._baseEvents;
        _initialEvents = replacement._initialEvents;
        _repeatEvents = replacement._repeatEvents;
        _repeatCountInFrames = replacement._repeatCountInFrames;
        _initialCountInPending = replacement._initialCountInPending;
        _audioClips = replacement._audioClips;
        _audioMixer?.Dispose();
        _audioMixer = replacement._audioMixer;
        replacement._audioMixer = null;
        _trackVolumes = replacement._trackVolumes;
        _trackMasterGains = replacement._trackMasterGains;
        _pieceGains = replacement._pieceGains;
        _volumeUpdatePending = true;
        _trackEffects = replacement._trackEffects;
        HasTimeBasedEffects = replacement.HasTimeBasedEffects;
        _effectsUpdatePending = true;
        _measures = replacement._measures;
        _sectionTimelineSpans = replacement._sectionTimelineSpans;
        _tempoMap = replacement._tempoMap;
        var initialIndex = _initialMeasureId.HasValue
            ? Array.FindIndex(_measures, measure => measure.Id == _initialMeasureId.Value)
            : -1;
        if (initialIndex < 0 && _measures.Length > 0)
        {
            initialIndex = 0;
            _initialMeasureId = _measures[0].Id;
        }
        _initialPositionFrame = initialIndex >= 0 ? _measures[initialIndex].Start : 0;
        _baseLoopDurationFrames = replacement._baseLoopDurationFrames;
        _usedChannels = _usedChannels.Union(replacement._usedChannels).ToArray();
        if (wasInitialPass)
        {
            _events = _initialEvents;
            _countInFrames = replacement._countInFrames;
            _initialCountInPending = replacement._initialCountInPending;
            _loopDurationFrames = replacement._loopDurationFrames;
            var mappedElapsed = wasInCountIn ? elapsedCountIn : 0;
            _eventFrameOffset = wasInCountIn
                ? checked(_renderedFrames - mappedPosition - mappedElapsed)
                : checked(_renderedFrames - mappedPosition - _countInFrames);
        }
        else
        {
            _countInFrames = _countInEveryLoop ? _repeatCountInFrames : 0;
            _events = _countInEveryLoop ? _repeatEvents : _baseEvents;
            _loopDurationFrames = checked(_baseLoopDurationFrames + _countInFrames);
            _initialCountInPending = false;
            _eventFrameOffset = wasInCountIn
                ? checked(_renderedFrames - elapsedCountIn)
                : checked(_renderedFrames - mappedPosition - _countInFrames);
        }
        _nextEventIndex = Array.FindIndex(_events, item =>
            boundaryNotDispatched ? item.Frame + _eventFrameOffset >= _renderedFrames
                : item.Frame + _eventFrameOffset > _renderedFrames);
        if (_nextEventIndex < 0) _nextEventIndex = _events.Length;
        _audioMixer?.Seek(Math.Max(0, mappedPosition));

        // A refreshed scope may end earlier than an already sounding note's old
        // scheduled release. Respect the new boundary by advancing that carried
        // release to the first matching replacement event.
        for (var carriedIndex = 0; carriedIndex < _carriedEvents.Count; carriedIndex++)
        {
            var carried = _carriedEvents[carriedIndex];
            if (((carried.Kind is EventKind.NoteOff or EventKind.MetronomeOff) && !_activeNotes.Contains(carried.NoteId)) ||
                (carried.Kind == EventKind.PitchBendReset && !_activeBends.Contains(carried.NoteId)))
                continue;
            var deadline = _events.Skip(_nextEventIndex)
                .Where(item => item.Kind == carried.Kind && item.NoteId == carried.NoteId)
                .Select(item => checked(item.Frame + _eventFrameOffset))
                .Where(frame => frame >= _renderedFrames)
                .DefaultIfEmpty(carried.Frame)
                .Min();
            if (_selection?.Mode is PlaybackLoopMode.Measure or PlaybackLoopMode.Sections or PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop)
            {
                var boundary = GetNextScopeBoundaryFrame(mappedPosition);
                if (boundary.HasValue)
                    deadline = Math.Min(deadline, checked(boundary.Value + _eventFrameOffset));
            }
            if (deadline < carried.Frame)
                _carriedEvents[carriedIndex] = carried with { Frame = Math.Max(deadline, _renderedFrames) };
        }

        // Initialize any newly added tracks and changed programs at the current cursor.
        foreach (var setup in _events.Where(item => item.Frame == 0 &&
                     (item.Kind == EventKind.BankSelect || item.Kind == EventKind.ProgramChange)))
        {
            _carriedEvents.Add(setup with { Frame = _renderedFrames });
        }
        _carriedEvents.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : a.Kind.CompareTo(b.Kind));
    }

    private long? GetNextScopeBoundaryFrame(long localPosition)
    {
        if (_measures.Length == 0) return null;
        var currentIndex = Array.FindIndex(_measures, measure =>
            localPosition >= measure.Start && localPosition < measure.End);
        if (currentIndex < 0)
            currentIndex = Array.FindIndex(_measures, measure => measure.Start >= localPosition);
        if (currentIndex < 0) return _measures[^1].End;
        for (var index = currentIndex; index < _measures.Length - 1; index++)
        {
            if (_measures[index].EndPosition != _measures[index + 1].StartPosition)
                return _measures[index].End;
        }
        return _measures[^1].End;
    }

    private long MapPosition(long oldPosition, ScorePlaybackClock replacement)
    {
        var oldIndex = Array.FindIndex(_measures, measure => oldPosition >= measure.Start && oldPosition < measure.End);
        if (oldIndex < 0) return Math.Min(oldPosition, replacement._loopDurationFrames);
        var old = _measures[oldIndex];
        var occurrence = _measures.Take(oldIndex).Count(measure => measure.Id == old.Id);
        var newIndex = Array.FindIndex(replacement._measures, measure => measure.Id == old.Id);
        while (newIndex >= 0 && occurrence-- > 0)
            newIndex = Array.FindIndex(replacement._measures, newIndex + 1, measure => measure.Id == old.Id);
        if (newIndex < 0)
        {
            // Deleting the measure under the playhead skips to the next measure that
            // survived the edit. If none survived, continue at the new score end.
            for (var index = oldIndex + 1; index < _measures.Length; index++)
            {
                newIndex = Array.FindIndex(replacement._measures, measure => measure.Id == _measures[index].Id);
                if (newIndex >= 0) return replacement._measures[newIndex].Start;
            }

            return replacement._loopDurationFrames;
        }
        var target = replacement._measures[newIndex];
        // A meter edit changes the bar length, not the amount already played in it.
        return target.Start + Math.Min(oldPosition - old.Start, target.End - target.Start);
    }

    /// <summary>Whether the current score contains an enabled time-based track effect that can ring out.</summary>
    public bool HasTimeBasedEffects { get; private set; }
    public bool HasPendingEvents => _nextEventIndex < _events.Length;
    /// <summary>Length of the clock's currently projected timeline, in output frames.</summary>
    public long TimelineDurationFrames => _loopDurationFrames;

    /// <summary>Section occurrences projected into the active playback timeline.</summary>
    public IReadOnlyList<PlaybackSectionTimelineSpan> SectionTimelineSpans => _sectionTimelineSpans;

    /// <summary>Converts a source-score position through this clock's already compiled tempo map.</summary>
    public double GetScoreElapsedSeconds(ScoreTime position) => _tempoMap.GetElapsedSeconds(position);

    /// <summary>Current score location, including loop wrapping and score refresh remapping.</summary>
    public PlaybackPosition? Position
    {
        get => GetPositionBehind(0);
    }

    /// <summary>Returns the position at a frame count behind generated audio, accounting for startup and loop boundaries.</summary>
    public PlaybackPosition? GetPositionBehind(long framesBehind)
    {
        var scoreFrame = GetTimelineFrameBehind(framesBehind);
        if (scoreFrame is null || _measures.Length == 0) return null;
        var index = Array.FindIndex(_measures, measure => scoreFrame.Value >= measure.Start && scoreFrame.Value < measure.End);
        if (index < 0)
        {
            var last = _measures[^1];
            return scoreFrame.Value >= last.End ? new PlaybackPosition(last.Id, 1.0) : null;
        }
        var current = _measures[index];
        var sourceFrame = checked(ToFrame(_tempoMap.GetElapsedSeconds(current.StartPosition), _sampleRate)
            + scoreFrame.Value - current.Start);
        var fraction = GetMusicalFraction(current, sourceFrame);
        return new PlaybackPosition(current.Id, Math.Clamp(fraction, 0.0, 1.0));
    }

    /// <summary>
    /// Current frame in this clock's projected playback timeline, accounting for
    /// queued audio and loop wrapping. For arrangements this distinguishes repeated
    /// occurrences that share the same authored measure ID.
    /// </summary>
    public long? GetTimelineFrameBehind(long framesBehind)
    {
        if (framesBehind < 0) throw new ArgumentOutOfRangeException(nameof(framesBehind));
        if (_measures.Length == 0) return null;
        var frame = Math.Max(0, _renderedFrames - framesBehind);
        if (!TryGetPass(frame, out var offset, out var isInitial, out var countInFrames))
            return _initialPositionFrame;
        if (IsCountInBehind(framesBehind)) return isInitial ? _initialPositionFrame : 0;
        var localFrame = frame - offset;
        var countInStart = isInitial ? _initialPositionFrame : 0;
        if (countInFrames > 0 && localFrame >= countInStart + countInFrames)
            localFrame -= countInFrames;
        return Math.Clamp(localFrame, 0, _baseLoopDurationFrames);
    }

    private double GetMusicalFraction(MeasureSpan measure, long frame)
    {
        var sourceStart = ToFrame(_tempoMap.GetElapsedSeconds(measure.StartPosition), _sampleRate);
        var sourceEnd = ToFrame(_tempoMap.GetElapsedSeconds(measure.EndPosition), _sampleRate);
        if (sourceEnd <= sourceStart || frame <= sourceStart) return 0.0;
        if (frame >= sourceEnd) return 1.0;
        var low = 0.0;
        var high = 1.0;
        // Invert the tempo map within this bar so the fraction remains musical
        // when tempo changes or ramps occur inside a measure.
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var fraction = (low + high) * 0.5;
            // Keep the rational denominator modest. ScoreTime uses checked Int64
            // cross-products; nanosecond-like denominators overflow for unusual
            // meters after they are combined with cumulative source positions.
            const long FractionScale = 1_000_000;
            var scaled = new ScoreTime((long)Math.Round(fraction * FractionScale), FractionScale);
            var position = measure.StartPosition + (measure.EndPosition - measure.StartPosition) * scaled;
            var candidateFrame = ToFrame(_tempoMap.GetElapsedSeconds(position), _sampleRate);
            if (candidateFrame < frame) low = fraction;
            else high = fraction;
        }
        return (low + high) * 0.5;
    }

    private ScheduledEvent[] BuildCountInEvents(ScoreTime measureStart, TimeSignature signature,
        long frameStart, int order)
    {
        var events = new List<ScheduledEvent>();
        var startSeconds = _tempoMap.GetElapsedSeconds(measureStart);
        for (var beat = 0; beat < signature.Numerator; beat++)
        {
            var beatPosition = measureStart + new ScoreTime(beat, signature.Denominator);
            var frame = checked(frameStart + ToFrame(_tempoMap.GetElapsedSeconds(beatPosition), _sampleRate) -
                ToFrame(startSeconds, _sampleRate));
            var id = Guid.NewGuid();
            var pitch = beat == 0 ? 76 : 77;
            var velocity = beat == 0 ? 112 : 82;
            events.Add(new ScheduledEvent(frame, order++, EventKind.CountInOn, 9, pitch, velocity, id));
            events.Add(new ScheduledEvent(frame + Math.Max(1, _sampleRate / 30), order++, EventKind.CountInOff, 9, pitch, 0, id));
        }
        return events.ToArray();
    }

    /// <summary>Repeats the score after its final measure while enabled.</summary>
    public bool LoopEnabled { get; set; }

    private bool IsLooping => _selection is null ? LoopEnabled : _selection.Mode switch
    {
        PlaybackLoopMode.Score or PlaybackLoopMode.Measure or PlaybackLoopMode.Sections or PlaybackLoopMode.ArrangementLoop => true,
        _ => false
    };

    /// <summary>Whether scheduled score events remain to be played.</summary>
    public bool IsPlaying
    {
        get
        {
            var playing = !_stopped && (HasPendingEvents || _carriedEvents.Count > 0 ||
                (_loopDurationFrames > 0 && (IsLooping || _renderedFrames < _eventFrameOffset + _loopDurationFrames)));
            if (!playing)
            {
                _audioMixer?.Dispose();
                _audioMixer = null;
            }
            return playing;
        }
    }

    /// <summary>Renders the requested frames, stopping at every event boundary.</summary>
    public void Render(SoundFontPlaybackService service, Span<float> left, Span<float> right)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left and right audio buffers must have equal lengths.");
        }

        if (service.SampleRate != _sampleRate)
        {
            throw new ArgumentException("The playback service sample rate must match the clock sample rate.", nameof(service));
        }

        if (_volumeUpdatePending)
        {
            foreach (var (trackId, volume) in _trackVolumes) service.SetTrackVolume(trackId, volume);
            foreach (var (trackId, masterGain) in _trackMasterGains) service.SetPercussionMasterGain(trackId, masterGain);
            foreach (var (key, gain) in _pieceGains) service.SetDrumPieceGain(key.TrackId, key.Pitch, gain);
            _volumeUpdatePending = false;
        }

        if (_effectsUpdatePending)
        {
            service.SetTrackEffects(_trackEffects);
            _effectsUpdatePending = false;
        }

        var offset = 0;
        while (!_stopped && (offset < left.Length || HasEventAtOrBefore(_renderedFrames)))
        {
            DispatchEventsAtOrBefore(service, _renderedFrames);
            AdvanceLoopIfComplete();
            if (offset == left.Length)
            {
                break;
            }

            var frames = left.Length - offset;
            if (TryGetNextEventFrame(out var nextEventFrame))
            {
                var untilEvent = nextEventFrame - _renderedFrames;
                if (untilEvent <= 0)
                {
                    continue;
                }

                frames = (int)Math.Min(frames, untilEvent);
            }

            // A compacted arrangement may jump to another source measure. Split at
            // each boundary so automation always follows the source score position.
            var sourceFrame = 0L;
            if (_trackEffects.Count > 0)
            {
                var inCountIn = IsCountInBehind(0);
                var localFrame = inCountIn ? (_isInitialPass ? _initialPositionFrame : 0)
                    : Math.Max(0, _renderedFrames - _eventFrameOffset - _countInFrames);
                sourceFrame = localFrame;
                foreach (var measure in _measures)
                {
                    if (localFrame < measure.Start || localFrame >= measure.End) continue;
                    sourceFrame = measure.SourceStart + localFrame - measure.Start;
                    if (!inCountIn) frames = (int)Math.Min(frames, measure.End - localFrame);
                    break;
                }
            }
            var leftSegment = left.Slice(offset, frames);
            var rightSegment = right.Slice(offset, frames);
            service.Render(leftSegment, rightSegment, sourceFrame);
            if (!IsCountInBehind(0))
                _audioMixer?.Mix(Math.Max(0, _renderedFrames - _eventFrameOffset - _countInFrames), leftSegment, rightSegment);
            offset += frames;
            _renderedFrames = checked(_renderedFrames + frames);
        }
    }

    /// <summary>Sends all-notes-off to every channel owned by this clock and discards its timeline.</summary>
    public void Stop(SoundFontPlaybackService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.HoldTrackEffectsAutomation();
        foreach (var channel in _usedChannels)
        {
            service.AllNotesOff(channel);
        }

        _audioMixer?.Dispose();
        _audioMixer = null;

        _nextEventIndex = _events.Length;
        _carriedEvents.Clear();
        _activeNotes.Clear();
        _activeNoteEvents.Clear();
        _activeBends.Clear();
        _stopped = true;
        _renderedFrames = _initialPositionFrame;
        _eventFrameOffset = 0;
        _events = _initialEvents;
        _loopDurationFrames = checked(_baseLoopDurationFrames + _countInFrames);
        _initialCountInPending = _countInFrames > 0;
        _isInitialPass = true;
        _hasPreviousPass = false;
        _nextEventIndex = Array.FindIndex(_events, item => item.Frame >= _initialPositionFrame);
        if (_nextEventIndex < 0) _nextEventIndex = _events.Length;
    }

    private bool HasEventAtOrBefore(long frame) =>
        TryGetNextEventFrame(out var eventFrame) && eventFrame <= frame;

    private void DispatchEventsAtOrBefore(SoundFontPlaybackService service, long frame)
    {
        List<ScheduledEvent>? deferredLegatoReleases = null;
        while (_carriedEvents.Count > 0 && _carriedEvents[0].Frame <= frame)
        {
            var carried = _carriedEvents[0];
            _carriedEvents.RemoveAt(0);
            if (carried.Kind is EventKind.NoteOff or EventKind.MetronomeOff)
            {
                // RefreshScore carries existing note-offs separately. Let a same-frame
                // legato destination claim the sounding voice before its old off fires.
                var hasLegatoAtBoundary = carried.Kind == EventKind.NoteOff && carried.StringNumber > 0 &&
                    _events.Skip(_nextEventIndex).TakeWhile(item => item.Frame + _eventFrameOffset <= frame)
                        .Any(item => item.Kind == EventKind.LegatoNoteOn && item.TrackId == carried.TrackId &&
                            item.StringNumber == carried.StringNumber && item.Frame + _eventFrameOffset == carried.Frame);
                if (hasLegatoAtBoundary) (deferredLegatoReleases ??= new()).Add(carried);
                else DispatchNoteOff(service, carried);
            }
            else if (carried.Kind == EventKind.PitchBendReset)
            {
                if (_activeBends.Remove(carried.NoteId)) service.PitchBendValue(carried.TrackId, carried.Channel, 8192);
            }
            else if (carried.Kind == EventKind.BankSelect) service.ControlChange(carried.TrackId, carried.Channel, 0, carried.Value);
            else if (carried.Kind == EventKind.ProgramChange) service.ProgramChange(carried.TrackId, carried.Channel, carried.Value);
        }
        // HasEventAtOrBefore also reports the synthetic loop boundary when the
        // current cycle's event array is exhausted. That boundary is handled by
        // AdvanceLoopIfComplete; only index the array when a real event is pending.
        while (HasPendingEvents && HasEventAtOrBefore(frame))
        {
            var scheduled = _events[_nextEventIndex++];
            if (scheduled.Kind == EventKind.SelectContent)
            {
                SelectContent(scheduled.ContentChoice);
                continue;
            }
            if (scheduled.ContentChoice >= 0 && (!_contentChoices[scheduled.ContentChoice].HasSelection
                || _contentChoices[scheduled.ContentChoice].SelectedVersion != scheduled.ContentVersion)
                && !((scheduled.Kind is EventKind.NoteOff or EventKind.MetronomeOff) && _activeNotes.Contains(scheduled.NoteId))
                && !(scheduled.Kind == EventKind.PitchBendReset && _activeBends.Contains(scheduled.NoteId)))
                continue;
            if (_hasContentChoices && scheduled.StringNumber > 0 && (scheduled.Kind is EventKind.NoteOn or EventKind.ExpressiveNoteOn))
                ReleasePreviousStringNote(service, scheduled);
            switch (scheduled.Kind)
            {
                case EventKind.ScopeBoundary:
                    foreach (var active in _activeNoteEvents.Values.Where(item => item.StringNumber > 0).ToArray())
                        DispatchNoteOff(service, active);
                    break;
                case EventKind.ProgramChange:
                    service.ProgramChange(scheduled.TrackId, scheduled.Channel, scheduled.Value);
                    break;
                case EventKind.BankSelect:
                    service.ControlChange(scheduled.TrackId, scheduled.Channel, 0, scheduled.Value);
                    break;
                case EventKind.PitchBendSet:
                    service.PitchBendValue(scheduled.TrackId, scheduled.Channel, scheduled.Value);
                    _activeBends.Add(scheduled.NoteId);
                    break;
                case EventKind.PitchBendReset:
                    if (!_carriedEvents.Any(item => item.Kind == EventKind.PitchBendReset && item.NoteId == scheduled.NoteId)
                        && _activeBends.Remove(scheduled.NoteId))
                    {
                        service.PitchBendValue(scheduled.TrackId, scheduled.Channel, 8192);
                    }
                    break;
                case EventKind.NoteOff:
                    if (!_carriedEvents.Any(item => item.Kind == EventKind.NoteOff && item.NoteId == scheduled.NoteId))
                        DispatchNoteOff(service, scheduled);
                    break;
                case EventKind.NoteOn:
                    service.NoteOn(scheduled.TrackId, scheduled.Channel, scheduled.Value, scheduled.SecondaryValue, scheduled.NoteId);
                    _activeNotes.Add(scheduled.NoteId);
                    _activeNoteEvents[scheduled.NoteId] = scheduled;
                    break;
                case EventKind.ExpressiveNoteOn:
                    if (scheduled.ProgramOverride != -1)
                        service.NoteOnExpressive(scheduled.TrackId, scheduled.Channel, scheduled.Value, scheduled.SecondaryValue, scheduled.NoteId,
                            scheduled.ProgramOverride >= 0 ? scheduled.ProgramOverride : -1, scheduled.PieceGainDb, scheduled.PieceGainPitch);
                    else service.NoteOnExpressive(scheduled.TrackId, scheduled.Channel, scheduled.Value, scheduled.SecondaryValue, scheduled.NoteId);
                    _activeNotes.Add(scheduled.NoteId);
                    _activeNoteEvents[scheduled.NoteId] = scheduled;
                    break;
                case EventKind.LegatoNoteOn:
                    DispatchLegatoNoteOn(service, scheduled);
                    break;
                case EventKind.NoteExpressionUpdate:
                    service.SetNoteExpression(scheduled.TrackId, scheduled.NoteId, scheduled.Value / 1000.0,
                        scheduled.SecondaryValue / 1000.0);
                    break;
                case EventKind.MetronomeOn:
                    if (_metronomeEnabled)
                    {
                        service.NoteOn(scheduled.TrackId, scheduled.Channel, scheduled.Value, scheduled.SecondaryValue, scheduled.NoteId);
                        _activeNotes.Add(scheduled.NoteId);
                        _activeNoteEvents[scheduled.NoteId] = scheduled;
                    }
                    break;
                case EventKind.MetronomeOff:
                case EventKind.CountInOff:
                    DispatchNoteOff(service, scheduled);
                    break;
                case EventKind.CountInOn:
                    service.NoteOn(scheduled.TrackId, scheduled.Channel, scheduled.Value, scheduled.SecondaryValue, scheduled.NoteId);
                    _activeNotes.Add(scheduled.NoteId);
                    _activeNoteEvents[scheduled.NoteId] = scheduled;
                    break;
            }
        }
        if (deferredLegatoReleases is not null)
            foreach (var release in deferredLegatoReleases) DispatchNoteOff(service, release);
    }

    private void DispatchLegatoNoteOn(SoundFontPlaybackService service, ScheduledEvent next)
    {
        var previous = _activeNoteEvents.Values.FirstOrDefault(item => item.TrackId == next.TrackId &&
            item.StringNumber == next.StringNumber && item.NoteId != next.NoteId &&
            item.Kind is EventKind.ExpressiveNoteOn or EventKind.LegatoNoteOn);
        var ascending = next.Articulation == StringArticulation.HammerOn;
        var directionMatches = previous.NoteId != Guid.Empty &&
            (ascending ? next.Value > previous.Value : next.Value < previous.Value);
        if (directionMatches && previous.ProgramOverride == next.ProgramOverride &&
            service.TryContinueExpressiveNote(next.TrackId, previous.NoteId, next.NoteId, next.Value, next.MinimumExpression, next.MaximumExpression))
        {
            _activeNotes.Remove(previous.NoteId);
            _activeNoteEvents.Remove(previous.NoteId);
        }
        else
        {
            // A missing predecessor, a changed sound or an out-of-range bend is a
            // regular attack. This also handles seeking directly to the destination.
            if (next.StringNumber > 0) ReleasePreviousStringNote(service, next);
            if (next.ProgramOverride != -1)
                service.NoteOnExpressive(next.TrackId, next.Channel, next.Value, next.SecondaryValue, next.NoteId,
                    next.ProgramOverride >= 0 ? next.ProgramOverride : -1, next.PieceGainDb, next.PieceGainPitch);
            else service.NoteOnExpressive(next.TrackId, next.Channel, next.Value, next.SecondaryValue, next.NoteId);
        }
        _activeNotes.Add(next.NoteId);
        _activeNoteEvents[next.NoteId] = next;
    }

    private void DispatchNoteOff(SoundFontPlaybackService service, ScheduledEvent noteOff)
    {
        if (!_activeNotes.Remove(noteOff.NoteId)) return;
        var wasExpressive = _activeNoteEvents.Remove(noteOff.NoteId, out var activeOff) &&
            activeOff.Kind is EventKind.ExpressiveNoteOn or EventKind.LegatoNoteOn;
        if (wasExpressive || !_activeNoteEvents.Values.Any(active => active.Kind == EventKind.NoteOn &&
                active.TrackId == noteOff.TrackId
                && active.Channel == noteOff.Channel && active.Value == noteOff.Value))
        {
            service.NoteOff(noteOff.TrackId, noteOff.Channel, noteOff.Value, noteOff.NoteId);
        }
    }

    private static int ApplyVelocityExpression(int velocity, IReadOnlyList<NoteExpression> expressions)
    {
        var multiplier = expressions.Where(expression => expression.Kind == ExpressionKind.Velocity)
            .Aggregate(1.0, (value, expression) => value * expression.Sample(expression.Position));
        return Math.Clamp((int)Math.Round(velocity * multiplier, MidpointRounding.AwayFromZero), 0, 127);
    }

    private static ScoreTime ToScoreTime(double normalized)
    {
        const long precision = 1_000_000;
        return new ScoreTime((long)Math.Round(Math.Clamp(normalized, 0d, 1d) * precision,
            MidpointRounding.AwayFromZero), precision);
    }

    private bool TryGetNextEventFrame(out long frame)
    {
        var hasFrame = false;
        frame = 0;
        if (HasPendingEvents)
        {
            frame = checked(_events[_nextEventIndex].Frame + _eventFrameOffset);
            hasFrame = true;
        }
        else if (!_stopped && _loopDurationFrames > 0 &&
                 (IsLooping || _renderedFrames < _eventFrameOffset + _loopDurationFrames))
        {
            frame = checked(_eventFrameOffset + _loopDurationFrames);
            hasFrame = true;
        }
        if (_carriedEvents.Count > 0 && (!hasFrame || _carriedEvents[0].Frame < frame))
        {
            frame = _carriedEvents[0].Frame;
            hasFrame = true;
        }
        return hasFrame;
    }

    private void AdvanceLoopIfComplete()
    {
        if (!IsLooping || _stopped || _loopDurationFrames <= 0
            || HasPendingEvents || _renderedFrames < _eventFrameOffset + _loopDurationFrames)
        {
            return;
        }

        _previousPassOffset = _eventFrameOffset;
        _previousPassDuration = _loopDurationFrames;
        _previousPassCountInFrames = _countInFrames;
        _previousPassWasInitial = _isInitialPass;
        _hasPreviousPass = true;

        if (_initialCountInPending)
        {
            _eventFrameOffset = _renderedFrames;
            SetRepeatPassSchedule();
            _initialCountInPending = false;
            _isInitialPass = false;
            _nextEventIndex = 0;
            return;
        }
        _eventFrameOffset = checked(_eventFrameOffset + _loopDurationFrames);
        _isInitialPass = false;
        SetRepeatPassSchedule();
        _nextEventIndex = 0;
    }

    private void SetRepeatPassSchedule()
    {
        if (_countInEveryLoop)
        {
            _events = _repeatEvents;
            _countInFrames = _repeatCountInFrames;
            _loopDurationFrames = checked(_baseLoopDurationFrames + _repeatCountInFrames);
        }
        else
        {
            _events = _baseEvents;
            _countInFrames = 0;
            _loopDurationFrames = _baseLoopDurationFrames;
        }
    }

    private static ScoreTime GetScoreEndPosition(ScoreDocument score)
    {
        var position = ScoreTime.Zero;
        foreach (var section in score.Sections)
        {
            foreach (var measure in section.Measures)
            {
                position += GetMeasureDuration(score, section, measure);
            }
        }

        return position;
    }

    internal static ScoreTime GetMeasureDuration(ScoreDocument score, ScoreSection section, ScoreMeasure measure)
    {
        if (!measure.IsFreeTime && !measure.IsPickup)
        {
            return score.ResolveTimeSignature(section, measure).BarDuration;
        }
        var duration = ScoreTime.Zero;
        foreach (var voice in measure.TrackMeasures.SelectMany(trackMeasure => trackMeasure.Voices))
        {
            var voiceDuration = voice.Beats.Aggregate(ScoreTime.Zero, (total, beat) => total + beat.Duration);
            if (voiceDuration > duration) duration = voiceDuration;
        }
        foreach (var note in measure.TrackMeasures.SelectMany(trackMeasure =>
                     trackMeasure.UsesTabEvents ? trackMeasure.TabEvents : Enumerable.Empty<TabEvent>()))
        {
            if (note.EndPosition > duration) duration = note.EndPosition;
        }
        return duration;
    }

    private static long ToFrame(double seconds, int sampleRate)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "Elapsed score time must be finite and nonnegative.");
        }

        var frame = Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero);
        if (frame > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), "The score event exceeds the supported sample-frame timeline.");
        }

        return (long)frame;
    }

    private static Guid CreateRepeatedEventId(Guid sourceEventId, Guid destinationMeasureId)
    {
        Span<byte> input = stackalloc byte[32];
        sourceEventId.TryWriteBytes(input[..16]);
        destinationMeasureId.TryWriteBytes(input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }

    private static int ToPitchBendValue(decimal semitones)
    {
        // MIDI pitch bend is asymmetric around 8192: the positive side tops out at
        // 16383, while the negative side reaches 0. Preserve both +/-2 semitone endpoints.
        var offset = semitones < 0m ? semitones * 4096m : semitones * (8191m / 2m);
        return Math.Clamp((int)decimal.Round(8192m + offset, MidpointRounding.AwayFromZero), 0, 16383);
    }

    private static void ValidateMidiValue(int value, string name, string parameterName)
    {
        if (value is < 0 or > 127)
        {
            throw new ArgumentException($"{name} must be between 0 and 127.", parameterName);
        }
    }

    private static void ValidateVelocity(int velocity, string parameterName)
    {
        if (velocity is < 1 or > 127)
        {
            throw new ArgumentException("MIDI note-on velocity must be between 1 and 127.", parameterName);
        }
    }
}

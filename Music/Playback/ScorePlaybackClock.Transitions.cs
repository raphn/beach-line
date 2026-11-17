#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music.Playback;

public sealed partial class ScorePlaybackClock
{
    // All candidates are compiled before rendering. Markers select one candidate
    // per destination occurrence, including silent candidates, without recompiling
    // a looping score or expanding the least common multiple of its sequences.
    private sealed class ContentCounter { public int Position; }
    private sealed class ContentChoice
    {
        public Guid MeasureId;
        public Guid TrackId;
        public long StartFrame;
        public Guid[] Sequence = Array.Empty<Guid>();
        public TrackMeasureTransitionRule[] Rules = Array.Empty<TrackMeasureTransitionRule>();
        public Guid? NextSectionId;
        public Guid? WrapSectionId;
        public Guid RequestedVersion;
        public bool UsesArrangementPolicy;
        public Guid SelectedVersion;
        public bool HasSelection;
        public ContentCounter Counter = new();
        public ContentChoice Copy() => new()
        {
            MeasureId = MeasureId, TrackId = TrackId, StartFrame = StartFrame,
            Sequence = Sequence, Rules = Rules, RequestedVersion = RequestedVersion,
            UsesArrangementPolicy = UsesArrangementPolicy
        };
    }
    private List<ContentChoice> _contentChoices = new();
    private Dictionary<(Guid Measure, Guid Track), ContentCounter> _contentCounters = new();
    private bool _hasContentChoices;

    private int AddSourceContentChoice(ScoreDocument score, ScoreSection section, ScoreMeasure measure,
        ScoreTrack track, ScoreTime start, List<ScheduledEvent> events, ref int order)
    {
        if (!measure.TrackMeasureVersions.Any(version => version.TrackId == track.Id)
            && !measure.TransitionRules.Any(rule => rule.TrackId == track.Id)
            && !measure.Alternations.Any(alternation => alternation.TrackId == track.Id)
            && !_versionSelection.TryGetVersionId(measure.Id, track.Id, out _)) return -1;
        _hasContentChoices = true;
        bool Valid(Guid id) => id == Guid.Empty || measure.TrackMeasureVersions.Any(version => version.TrackId == track.Id && version.Id == id);
        var arrangementPolicy = _selection?.Mode is PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop;
        var requestedVersion = _versionSelection.TryGetVersionId(measure.Id, track.Id, out var selectedId)
            && Valid(selectedId) ? selectedId : Guid.Empty;
        var choice = new ContentChoice
        {
            MeasureId = measure.Id, TrackId = track.Id,
            StartFrame = ToFrame(_tempoMap.GetElapsedSeconds(start), _sampleRate),
            RequestedVersion = requestedVersion,
            UsesArrangementPolicy = arrangementPolicy,
            Sequence = arrangementPolicy ? measure.Alternations.FirstOrDefault(item => item.TrackId == track.Id)?.VersionIds.Where(Valid).ToArray() ?? Array.Empty<Guid>() : Array.Empty<Guid>(),
            Rules = arrangementPolicy ? measure.TransitionRules.Where(rule => rule.TrackId == track.Id && Valid(rule.VersionId)
                && score.Sections.Any(candidate => candidate.Id == rule.SectionId)).ToArray()
                : Array.Empty<TrackMeasureTransitionRule>()
        };
        var index = _contentChoices.Count;
        _contentChoices.Add(choice);
        events.Add(new ScheduledEvent(choice.StartFrame, order++, EventKind.SelectContent, 0, 0, 0, ContentChoice: index));
        return index;
    }

    private void ConfigureContentChoices(ScoreDocument score)
    {
        var sections = score.Sections.SelectMany(section => section.Measures.Select(measure => (measure.Id, Section: section.Id))).ToDictionary(item => item.Id, item => item.Section);
        var groups = new int[_measures.Length];
        for (var index = 1; index < _measures.Length; index++)
            groups[index] = groups[index - 1] + (sections[_measures[index].Id] != sections[_measures[index - 1].Id]
                || _measures[index].StartPosition <= _measures[index - 1].StartPosition ? 1 : 0);
        foreach (var choice in _contentChoices)
        {
            var key = (choice.MeasureId, choice.TrackId);
            if (!_contentCounters.TryGetValue(key, out var counter))
                _contentCounters.Add(key, counter = new ContentCounter());
            choice.Counter = counter;
            var index = Array.FindIndex(_measures, span => span.Id == choice.MeasureId && span.Start == choice.StartFrame);
            if (index < 0 || _selection?.Mode == PlaybackLoopMode.Measure) continue;
            var next = index + 1;
            while (next < _measures.Length && groups[next] == groups[index]) next++;
            if (next < _measures.Length) choice.NextSectionId = sections[_measures[next].Id];
            else if (_measures.Length > 0) choice.WrapSectionId = sections[_measures[0].Id];
        }
    }

    private void SelectContent(int index)
    {
        if (index < 0) return;
        var choice = _contentChoices[index];
        choice.HasSelection = true;
        if (!choice.UsesArrangementPolicy)
        {
            choice.SelectedVersion = choice.RequestedVersion;
            return;
        }
        var next = choice.NextSectionId ?? (IsLooping ? choice.WrapSectionId : null);
        foreach (var rule in choice.Rules)
        {
            if (rule.SectionId != next) continue;
            choice.SelectedVersion = rule.VersionId;
            return;
        }
        if (choice.Sequence.Length == 0) { choice.SelectedVersion = Guid.Empty; return; }
        var position = choice.Counter.Position % choice.Sequence.Length;
        choice.SelectedVersion = choice.Sequence[position];
        choice.Counter.Position = position == choice.Sequence.Length - 1 ? 0 : position + 1;
    }

    private void PreserveContentProgress(ScorePlaybackClock replacement)
    {
        foreach (var pair in replacement._contentCounters)
            if (_contentCounters.TryGetValue(pair.Key, out var previous)) pair.Value.Position = previous.Position;
        // Match the occurrence ordinal, not just its measure ID: arrangements can
        // play the same section multiple times with different destination rules.
        var previousChoices = _contentChoices.GroupBy(choice => (choice.MeasureId, choice.TrackId))
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var group in replacement._contentChoices.GroupBy(choice => (choice.MeasureId, choice.TrackId)))
        {
            if (!previousChoices.TryGetValue(group.Key, out var previous))
            {
                // A newly configured slot that already began was playing its
                // resolved original. For ordinary playback, apply the editor's
                // newly selected content to its upcoming onsets now. Future slots
                // remain unselected until their normal marker is dispatched.
                var localFrame = Math.Max(0, _renderedFrames - _eventFrameOffset - _countInFrames);
                foreach (var choice in group)
                {
                    var started = choice.StartFrame >= (_isInitialPass ? _initialPositionFrame : 0)
                        && choice.StartFrame <= localFrame;
                    if (!choice.UsesArrangementPolicy)
                        choice.SelectedVersion = choice.RequestedVersion;
                    choice.HasSelection = started;
                }
                continue;
            }
            var index = 0;
            foreach (var choice in group)
            {
                if (index < previous.Length)
                {
                    if (choice.UsesArrangementPolicy)
                    {
                        choice.SelectedVersion = previous[index].SelectedVersion;
                        choice.HasSelection = previous[index].HasSelection;
                    }
                    else
                    {
                        // Ordinary playback follows the latest editor snapshot even
                        // when this occurrence already began. Existing note-offs are
                        // carried separately by RefreshScore, so only future onsets swap.
                        // Keep unstarted occurrences behind their selection marker.
                        choice.SelectedVersion = choice.RequestedVersion;
                        choice.HasSelection = previous[index].HasSelection;
                    }
                }
                index++;
            }
        }
        _contentChoices = replacement._contentChoices;
        _contentCounters = replacement._contentCounters;
        _hasContentChoices = replacement._hasContentChoices;
    }

    private void ReleasePreviousStringNote(SoundFontPlaybackService service, ScheduledEvent next)
    {
        // A selected candidate's actual next onset owns the string cutoff. An
        // unselected candidate must never shorten an audible note.
        while (true)
        {
            ScheduledEvent? previous = null;
            foreach (var active in _activeNoteEvents.Values)
                if (active.TrackId == next.TrackId && active.StringNumber == next.StringNumber)
                { previous = active; break; }
            if (previous is not { } note) return;
            DispatchNoteOff(service, note with { Kind = EventKind.NoteOff });
        }
    }
}

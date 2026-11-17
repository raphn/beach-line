#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

public enum TabRetuningMode
{
    KeepFingering,
    KeepPitch
}

/// <summary>An atomic, stale-checked conversion of authored tab events to another tuning.</summary>
public sealed class TabRetuningPlan
{
    private sealed record EventState(TabEvent Event, int String, int Fret, int Pitch, int Velocity,
        ScoreTime Onset, ScoreTime Duration, bool AllowsBarOverflow, int TupletActual);
    private sealed record LegacyNoteState(ScoreNote Note, int Pitch, int Velocity);
    private sealed record PartState(TrackMeasure Part, bool UsesTabEvents, EventState[] Events, LegacyNoteState[] LegacyNotes);
    private sealed record Assignment(TabEvent Event, int String, int Fret, int Pitch);

    private readonly ScoreDocument _score;
    private readonly ScoreTrack _track;
    private readonly int[] _oldTuning;
    private readonly int[] _newTuning;
    private readonly int _maxFret;
    private readonly PartState[] _parts;
    private readonly Assignment[] _assignments;

    private TabRetuningPlan(ScoreDocument score, ScoreTrack track, int[] oldTuning, int[] newTuning,
        PartState[] parts, Assignment[] assignments, int maxFret)
    {
        _score = score; _track = track; _oldTuning = oldTuning; _newTuning = newTuning;
        _parts = parts; _assignments = assignments; _maxFret = maxFret;
    }

    public static bool TryCreate(ScoreDocument score, ScoreTrack track, IReadOnlyList<int> tuning,
        TabRetuningMode mode, out TabRetuningPlan? plan, out string error)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(tuning);
        plan = null;
        error = string.Empty;
        if (!score.Tracks.Contains(track)) { error = "The track does not belong to this score."; return false; }
        if (track.Kind is not (TrackKind.Guitar or TrackKind.Bass))
        { error = "Only guitar and bass tracks can be retuned."; return false; }
        if (!Enum.IsDefined(mode)) { error = "The requested retuning mode is unavailable."; return false; }
        var next = tuning.ToArray();
        if (next.Length is < 1 or > 24 || next.Any(pitch => pitch is < 0 or > 127))
        { error = "A tuning must contain 1–24 MIDI pitches from 0 to 127."; return false; }

        var parts = score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content)))
            .Where(part => part.TrackId == track.Id)
            .Select(part => new PartState(part, part.UsesTabEvents, part.TabEvents.Select(note => new EventState(note,
                note.StringNumber, note.Fret, note.MidiPitch, note.Velocity, note.Onset, note.Duration,
                note.AllowsBarOverflow, note.TupletActual)).ToArray(), part.Voices.SelectMany(voice => voice.Beats)
                .SelectMany(beat => beat.Notes).Select(note => new LegacyNoteState(note, note.MidiPitch, note.Velocity)).ToArray())).ToArray();
        if (track.TabTuningMidiPitches.Count == 0 && parts.Any(part => part.Events.Length != 0))
        { error = "This track has tablature events without a configured tuning; repair the score before retuning."; return false; }
        if (mode == TabRetuningMode.KeepFingering && parts.Any(part => !part.Part.UsesTabEvents &&
            part.Part.Voices.SelectMany(voice => voice.Beats).Any(beat => beat.Notes.Count != 0)))
        { error = "Keeping fingering is unavailable while sounding legacy pitch-only notes remain in the track."; return false; }
        var assignments = new List<Assignment>();
        foreach (var partState in parts)
        {
            var events = partState.Events;
            if (mode == TabRetuningMode.KeepFingering)
            {
                foreach (var state in events)
                {
                    if (state.String < 1 || state.String > next.Length || state.Fret < 0 || state.Fret > track.MaxFret || next[state.String - 1] + state.Fret > 127)
                    { error = "Keeping the existing fingering would put an event outside the new strings, fret range, or MIDI range."; return false; }
                    assignments.Add(new Assignment(state.Event, state.String, state.Fret, next[state.String - 1] + state.Fret));
                }
            }
            else
            {
                foreach (var onsetGroup in events.GroupBy(state => state.Onset))
                {
                    var group = onsetGroup.ToArray();
                    var candidates = group.Select(state => Enumerable.Range(1, next.Length)
                        .Where(stringNumber =>
                        {
                            var fret = state.Pitch - next[stringNumber - 1];
                            return fret >= 0 && fret <= track.MaxFret;
                        })
                        .OrderBy(stringNumber => stringNumber == state.String ? 0 : 1)
                        .ThenBy(stringNumber => Math.Abs(stringNumber - state.String))
                        .ThenBy(stringNumber => state.Pitch - next[stringNumber - 1])
                        .ThenBy(stringNumber => stringNumber).ToArray()).ToArray();
                    if (candidates.Any(candidate => candidate.Length == 0))
                    { error = "At least one sounding pitch cannot be played on the new tuning within the current fret range."; return false; }
                    // Augmenting paths find a complete matching even when the first note's preferred string
                    // must be reassigned to make room for a more constrained chord note.
                    var stringOwner = new Dictionary<int, int>();
                    var chosen = new int[group.Length];
                    bool Match(int eventIndex, HashSet<int> visited)
                    {
                        foreach (var stringNumber in candidates[eventIndex])
                        {
                            if (!visited.Add(stringNumber)) continue;
                            if (!stringOwner.TryGetValue(stringNumber, out var previous) || Match(previous, visited))
                            {
                                stringOwner[stringNumber] = eventIndex;
                                chosen[eventIndex] = stringNumber;
                                return true;
                            }
                        }
                        return false;
                    }
                    foreach (var index in Enumerable.Range(0, group.Length).OrderBy(index => candidates[index].Length)
                                 .ThenBy(index => group[index].String).ThenBy(index => group[index].Event.Id))
                    {
                        if (!Match(index, new HashSet<int>()))
                        { error = "The simultaneous notes cannot be assigned to distinct strings on the new tuning."; return false; }
                    }
                    for (var index = 0; index < group.Length; index++)
                    {
                        var state = group[index];
                        var stringNumber = chosen[index];
                        assignments.Add(new Assignment(state.Event, stringNumber,
                            state.Pitch - next[stringNumber - 1], state.Pitch));
                    }
                }
            }
            if (assignments.Where(assignment => events.Any(state => ReferenceEquals(state.Event, assignment.Event)))
                .GroupBy(assignment => (assignment.String, assignment.Event.Onset)).Any(group => group.Count() > 1))
            { error = "The retuning would place simultaneous events on the same string."; return false; }
        }
        plan = new TabRetuningPlan(score, track, track.TabTuningMidiPitches.ToArray(), next, parts, assignments.ToArray(), track.MaxFret);
        return true;
    }

    public void Apply()
    {
        var currentParts = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content)))
            .Where(part => part.TrackId == _track.Id).ToArray();
        if (!_score.Tracks.Contains(_track) || _track.Kind is not (TrackKind.Guitar or TrackKind.Bass) ||
            _track.MaxFret != _maxFret || !_track.TabTuningMidiPitches.SequenceEqual(_oldTuning) ||
            !currentParts.SequenceEqual(_parts.Select(state => state.Part)))
            throw new InvalidOperationException("The score changed after this retuning plan was created; create a new plan.");
        foreach (var partState in _parts)
        {
            if (!partState.Part.TabEvents.SequenceEqual(partState.Events.Select(state => state.Event)))
                throw new InvalidOperationException("The score changed after this retuning plan was created; create a new plan.");
            if (partState.Part.UsesTabEvents != partState.UsesTabEvents)
                throw new InvalidOperationException("The score changed after this retuning plan was created; create a new plan.");
            if (!partState.Part.Voices.SelectMany(voice => voice.Beats).SelectMany(beat => beat.Notes)
                .SequenceEqual(partState.LegacyNotes.Select(state => state.Note)))
                throw new InvalidOperationException("The score changed after this retuning plan was created; create a new plan.");
            foreach (var state in partState.LegacyNotes)
                if (state.Note.MidiPitch != state.Pitch || state.Note.Velocity != state.Velocity)
                    throw new InvalidOperationException("The score changed after this retuning plan was created; create a new plan.");
            foreach (var state in partState.Events)
                if (state.Event.StringNumber != state.String || state.Event.Fret != state.Fret || state.Event.MidiPitch != state.Pitch ||
                    state.Event.Velocity != state.Velocity || state.Event.Onset != state.Onset || state.Event.Duration != state.Duration ||
                    state.Event.AllowsBarOverflow != state.AllowsBarOverflow || state.Event.TupletActual != state.TupletActual)
                    throw new InvalidOperationException("The score changed after this retuning plan was created; create a new plan.");
        }
        foreach (var assignment in _assignments)
        {
            assignment.Event.StringNumber = assignment.String;
            assignment.Event.Fret = assignment.Fret;
            assignment.Event.MidiPitch = assignment.Pitch;
        }
        _track.ApplyRetunedConfiguration(_newTuning);
    }
}

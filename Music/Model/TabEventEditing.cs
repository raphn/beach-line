#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>Validated operations and derived same-string cutoffs for independent tablature events.</summary>
public static class TabEventEditing
{
    public static TabEvent Add(TrackMeasure measure, ScoreTrack track, int stringNumber, int fret,
        ScoreTime onset, ScoreTime duration, int? velocity = null)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(track);
        if (measure.TrackId != track.Id) throw new ArgumentException("The track must own this measure part.", nameof(track));
        if (!measure.UsesTabEvents && measure.Voices.SelectMany(v => v.Beats).Any(b => b.Notes.Count != 0))
            throw new InvalidOperationException("Convert all legacy notes to tablature atomically before adding tab events to this part.");
        ValidateAssignment(track, stringNumber, fret);
        ValidateTimes(onset, duration);
        if (measure.TabEvents.Any(e => e.StringNumber == stringNumber && e.Onset == onset))
            throw new InvalidOperationException("A string cannot have two note attacks at the same onset.");
        var note = new TabEvent(stringNumber, fret, track.TabLaneMidiPitches[stringNumber - 1] + fret,
            velocity ?? TrackDefaults.VelocityFor(track.Kind), onset, duration);
        measure.TabEvents.Add(note);
        measure.UsesTabEvents = true;
        return note;
    }

    /// <summary>Converts every pitch-only note in the part in one operation, retaining beats as legacy data.</summary>
    public static IReadOnlyList<TabEvent> ConvertLegacyNotes(TrackMeasure measure, ScoreTrack track,
        IReadOnlyDictionary<Guid, (int StringNumber, int Fret)> assignments)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(assignments);
        if (measure.TrackId != track.Id) throw new ArgumentException("The track must own this measure part.", nameof(track));
        if (measure.UsesTabEvents) throw new InvalidOperationException("This part already uses tablature events.");
        var pending = new List<TabEvent>();
        foreach (var voice in measure.Voices)
        {
            var onset = ScoreTime.Zero;
            foreach (var beat in voice.Beats)
            {
                foreach (var legacyNote in beat.Notes)
                {
                    if (!assignments.TryGetValue(legacyNote.Id, out var assignment))
                        throw new InvalidOperationException("Every legacy note must receive a string and fret assignment.");
                    ValidateAssignment(track, assignment.StringNumber, assignment.Fret);
                    var pitch = track.TabLaneMidiPitches[assignment.StringNumber - 1] + assignment.Fret;
                    if (pitch != legacyNote.MidiPitch) throw new ArgumentException("An assigned string and fret do not match a legacy note pitch.", nameof(assignments));
                    pending.Add(new TabEvent(assignment.StringNumber, assignment.Fret, pitch, legacyNote.Velocity, onset, beat.Duration) { Id = legacyNote.Id });
                }
                onset += beat.Duration;
            }
        }
        if (assignments.Count != pending.Count) throw new ArgumentException("Assignments must match the legacy notes in this part exactly.", nameof(assignments));
        if (pending.GroupBy(note => (note.StringNumber, note.Onset)).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Legacy notes assigned to the same string at the same onset conflict.");
        measure.TabEvents.AddRange(pending);
        measure.UsesTabEvents = true;
        return pending.AsReadOnly();
    }

    /// <summary>Projects legacy notes into detached fixed-pitch events for display without changing the part.</summary>
    public static IReadOnlyList<TabEvent> GetLegacyPitchNotes(TrackMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        var notes = new List<TabEvent>();
        foreach (var voice in measure.Voices)
        {
            var onset = ScoreTime.Zero;
            foreach (var beat in voice.Beats)
            {
                foreach (var note in beat.Notes)
                    notes.Add(new TabEvent(128 - note.MidiPitch, 0, note.MidiPitch, note.Velocity, onset, beat.Duration) { Id = note.Id });
                onset += beat.Duration;
            }
        }
        return notes.AsReadOnly();
    }

    /// <summary>Enables fixed-pitch editing only when every legacy note is represented without loss.</summary>
    public static IReadOnlyList<TabEvent> ConvertLegacyPitchNotes(TrackMeasure measure, ScoreTrack track, ScoreTime maximumDuration)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(track);
        if (measure.TrackId != track.Id || !track.UsesFullTab)
            throw new ArgumentException("The part must belong to a keyboard or other pitch-grid track.", nameof(track));
        if (measure.UsesTabEvents || measure.TabEvents.Count != 0)
            throw new InvalidOperationException("This part already contains tablature events.");
        if (!maximumDuration.IsInitialized || maximumDuration <= ScoreTime.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        if (measure.Voices.SelectMany(v => v.Beats).Any(b => b.PitchBendSemitones != 0))
            throw new InvalidOperationException("Legacy pitch bends cannot be converted without changing playback.");
        var notes = GetLegacyPitchNotes(measure);
        if (notes.Any(n => n.EndPosition > maximumDuration))
            throw new InvalidOperationException("A legacy note extends beyond the shortest resolved bar.");
        foreach (var lane in notes.GroupBy(n => n.StringNumber))
        {
            var ordered = lane.OrderBy(n => n.Onset).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                if (ordered[i].Onset < ordered[i - 1].EndPosition)
                    throw new InvalidOperationException("Overlapping legacy notes of the same pitch cannot be converted without changing playback.");
        }
        measure.TabEvents.AddRange(notes);
        measure.UsesTabEvents = true;
        return notes;
    }

    public static void Move(TrackMeasure measure, ScoreTrack track, TabEvent note, int stringNumber, ScoreTime onset)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(note);
        EnsureMember(measure, note);
        ValidateAssignment(track, stringNumber, note.Fret);
        if (!onset.IsInitialized || onset < ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(onset));
        EnsureNoOnsetCollision(measure, note, stringNumber, onset);
        note.StringNumber = stringNumber;
        note.MidiPitch = track.TabLaneMidiPitches[stringNumber - 1] + note.Fret;
        note.Onset = onset;
    }

    public static void Resize(TrackMeasure measure, TabEvent note, ScoreTime duration)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(note);
        EnsureMember(measure, note);
        ValidateTimes(note.Onset, duration);
        note.Duration = duration;
    }

    /// <summary>Applies one exact duration delta to each distinct authored event after validating the complete batch.</summary>
    public static bool ResizeSelection(IEnumerable<(TrackMeasure Source, TabEvent Note)> targets, ScoreTime delta)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!delta.IsInitialized) throw new ArgumentOutOfRangeException(nameof(delta));

        var pending = new List<(TrackMeasure Source, TabEvent Note, ScoreTime Duration)>();
        var seen = new HashSet<(TrackMeasure Source, TabEvent Note)>();
        foreach (var target in targets)
        {
            ArgumentNullException.ThrowIfNull(target.Source);
            ArgumentNullException.ThrowIfNull(target.Note);
            if (!seen.Add(target)) continue;
            EnsureMember(target.Source, target.Note);
            var duration = target.Note.Duration + delta;
            ValidateTimes(target.Note.Onset, duration);
            pending.Add((target.Source, target.Note, duration));
        }

        if (delta == ScoreTime.Zero) return false;
        foreach (var item in pending) item.Note.Duration = item.Duration;
        return pending.Count > 0;
    }

    /// <summary>Returns the authored end clipped by the next attack on this string; authored duration is retained.</summary>
    public static ScoreTime GetEffectiveEnd(IEnumerable<TabEvent> events, TabEvent note)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(note);
        var cutoff = events.Where(e => e.Id != note.Id && e.StringNumber == note.StringNumber && e.Onset > note.Onset)
            .Select(e => e.Onset).DefaultIfEmpty(note.EndPosition).Min();
        return cutoff < note.EndPosition ? cutoff : note.EndPosition;
    }

    public static void ValidateAssignment(ScoreTrack track, int stringNumber, int fret)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit)
            throw new InvalidOperationException("Configure percussion pieces before editing percussion tablature events.");
        if (track.TabLaneMidiPitches.Count == 0) throw new InvalidOperationException("The track has no configured tablature tuning.");
        if (stringNumber < 1 || stringNumber > track.TabLaneMidiPitches.Count) throw new ArgumentOutOfRangeException(nameof(stringNumber));
        if ((track.Kind == TrackKind.Percussion || track.UsesFullTab) && fret != 0)
            throw new ArgumentOutOfRangeException(nameof(fret), "Fixed-pitch lanes require fret zero.");
        if (fret < 0 || fret > track.MaxFret) throw new ArgumentOutOfRangeException(nameof(fret));
        if (track.TabLaneMidiPitches[stringNumber - 1] + fret > 127) throw new ArgumentOutOfRangeException(nameof(fret), "The resulting pitch exceeds MIDI range.");
    }

    private static void ValidateTimes(ScoreTime onset, ScoreTime duration)
    {
        if (!onset.IsInitialized || onset < ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(onset));
        if (!duration.IsInitialized || duration <= ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
    }
    private static void EnsureMember(TrackMeasure measure, TabEvent note)
    {
        if (!measure.TabEvents.Contains(note)) throw new ArgumentException("The event must belong to this track measure.", nameof(note));
    }
    private static void EnsureNoOnsetCollision(TrackMeasure measure, TabEvent note, int stringNumber, ScoreTime onset)
    {
        if (measure.TabEvents.Any(e => e.Id != note.Id && e.StringNumber == stringNumber && e.Onset == onset))
            throw new InvalidOperationException("A string cannot have two note attacks at the same onset.");
    }
}

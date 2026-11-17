#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music;

namespace OpenTab.Music.Playback;

/// <summary>Builds an isolated score for auditioning one authored connected string-note pair.</summary>
public static class StringArticulationPreview
{
    public readonly record struct NoteOccurrence(TabEvent Note, ScoreTime Start, ScoreTime End);

    /// <summary>Resolves the later member of a two-note same-string selection when every destination instance is consecutive.</summary>
    public static bool TryGetSelectedPairDestination(IReadOnlyList<NoteOccurrence> occurrences, TabEvent first,
        TabEvent second, StringArticulation articulation, out TabEvent destination)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        destination = null!;
        if (ReferenceEquals(first, second) || articulation is not (StringArticulation.HammerOn or StringArticulation.PullOff)) return false;
        var firstStart = occurrences.Where(item => ReferenceEquals(item.Note, first)).Select(item => (ScoreTime?)item.Start).Min();
        var secondStart = occurrences.Where(item => ReferenceEquals(item.Note, second)).Select(item => (ScoreTime?)item.Start).Min();
        if (firstStart is null || secondStart is null || first.StringNumber != second.StringNumber || firstStart == secondStart) return false;
        var earlier = firstStart < secondStart ? first : second;
        var later = ReferenceEquals(earlier, first) ? second : first;
        bool directionMatches = articulation == StringArticulation.HammerOn
            ? later.MidiPitch > earlier.MidiPitch
            : later.MidiPitch < earlier.MidiPitch;
        if (!directionMatches) return false;
        var laterOccurrences = occurrences.Where(item => ReferenceEquals(item.Note, later)).ToArray();
        if (laterOccurrences.Length == 0) return false;
        foreach (var occurrence in laterOccurrences)
        {
            var predecessor = occurrences.Where(item => item.Note.StringNumber == later.StringNumber && item.Start < occurrence.Start)
                .OrderByDescending(item => item.Start).FirstOrDefault();
            if (predecessor.Note is null || !ReferenceEquals(predecessor.Note, earlier) || predecessor.End < occurrence.Start) return false;
        }
        destination = later;
        return true;
    }

    public static ScoreDocument BuildScore(ScoreTrack sourceTrack, TabEvent predecessor, TabEvent destination,
        ScoreTime onsetGap, TempoResolution tempo)
    {
        ArgumentNullException.ThrowIfNull(sourceTrack);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(destination);
        if (sourceTrack.Kind == TrackKind.Percussion || sourceTrack.TabTuningMidiPitches.Count == 0)
            throw new ArgumentException("Connected string-note preview requires a configured string track.", nameof(sourceTrack));
        if (predecessor.StringNumber > sourceTrack.TabTuningMidiPitches.Count ||
            destination.StringNumber > sourceTrack.TabTuningMidiPitches.Count)
            throw new ArgumentException("A connected note uses a string outside the track tuning.");
        if (destination.IncomingArticulation is not (StringArticulation.HammerOn or StringArticulation.PullOff))
            throw new ArgumentException("The destination note has no authored hammer-on or pull-off.", nameof(destination));
        if (predecessor.StringNumber != destination.StringNumber || onsetGap <= ScoreTime.Zero || predecessor.Duration < onsetGap ||
            (destination.IncomingArticulation == StringArticulation.HammerOn && destination.MidiPitch <= predecessor.MidiPitch) ||
            (destination.IncomingArticulation == StringArticulation.PullOff && destination.MidiPitch >= predecessor.MidiPitch))
            throw new ArgumentException("The selected notes do not form a valid connected pair.");
        if (tempo.WholeNotesPerMinute <= 0)
            throw new ArgumentOutOfRangeException(nameof(tempo), "Preview tempo must be positive.");

        var score = new ScoreDocument("Articulation preview", tempo.ToTempoMark(), new TimeSignature(4, 4));
        var previewTrack = new ScoreTrack(sourceTrack.Name, sourceTrack.Kind, sourceTrack.DefaultMidiProgram)
        {
            VolumeDb = sourceTrack.VolumeDb
        };
        previewTrack.ConfigureTab(sourceTrack.TabTuningMidiPitches, sourceTrack.MaxFret);
        score.AddTrack(previewTrack);
        var section = score.Sections[0];
        var measure = score.AddMeasure(section);
        measure.IsFreeTime = true;
        var part = measure.GetTrackMeasure(previewTrack.Id)!;
        part.Voices.Clear();
        part.UsesTabEvents = true;
        part.TabEvents.Add(CloneNote(predecessor, ScoreTime.Zero, predecessor.Duration, StringArticulation.None));
        part.TabEvents.Add(CloneNote(destination, onsetGap, destination.Duration, destination.IncomingArticulation));
        return score;
    }

    private static TabEvent CloneNote(TabEvent source, ScoreTime onset, ScoreTime duration, StringArticulation articulation)
    {
        var clone = new TabEvent(source.StringNumber, source.Fret, source.MidiPitch, source.Velocity, onset, duration)
        {
            IncomingArticulation = articulation,
            SoundVariation = source.SoundVariation,
            TupletActual = source.TupletActual
        };
        clone.Expressions.AddRange(source.Expressions.Select(expression => expression.Clone()));
        return clone;
    }
}

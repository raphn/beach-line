#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>Edits the authored notes behind a selection, including linked occurrences.</summary>
public static class TabVelocityEditing
{
    /// <summary>Returns the lowest selected intensity without changing stored MIDI velocities.</summary>
    public static int InitialLevel(IEnumerable<(TrackMeasure Source, TabEvent Note)> selection) =>
        Resolve(selection).Min(note => note.Intensity);

    /// <summary>Validates the complete selection before applying an explicit intensity to each source note.</summary>
    public static void Apply(IEnumerable<(TrackMeasure Source, TabEvent Note)> selection, int level)
    {
        var velocity = Beat.MidiVelocityForIntensity(level);
        var notes = Resolve(selection);
        foreach (var note in notes) note.Velocity = velocity;
    }

    private static TabEvent[] Resolve(IEnumerable<(TrackMeasure Source, TabEvent Note)> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var entries = selection.ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Select at least one note.");
        foreach (var (source, note) in entries)
            if (source is null || note is null || !source.TabEvents.Contains(note))
                throw new InvalidOperationException("A selected note no longer belongs to its source measure.");
        if (entries.GroupBy(entry => entry.Note).Any(group => group.Select(entry => entry.Source).Distinct().Count() != 1))
            throw new InvalidOperationException("A selected note has more than one source measure.");
        return entries.Select(entry => entry.Note).Distinct().ToArray();
    }
}

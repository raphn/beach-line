#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>Preview state for a relative velocity gesture over tablature source events.</summary>
public sealed class TabVelocityGesture
{
    private sealed record Target(TrackMeasure Source, TabEvent Note, int OriginalVelocity, int OriginalLevel);
    private readonly Target[] _targets;

    public TabVelocityGesture(IEnumerable<(TrackMeasure Source, TabEvent Note)> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var entries = selection.ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Select at least one note.");
        foreach (var (source, note) in entries)
            if (source is null || note is null || !source.TabEvents.Contains(note))
                throw new InvalidOperationException("A selected note no longer belongs to its source measure.");
        if (entries.GroupBy(entry => entry.Note).Any(group => group.Select(entry => entry.Source).Distinct().Count() != 1))
            throw new InvalidOperationException("A selected note has more than one source measure.");
        _targets = entries.Select(entry => (entry.Source, entry.Note)).Distinct()
            .Select(entry => new Target(entry.Source, entry.Note, entry.Note.Velocity, entry.Note.Intensity)).ToArray();
    }

    /// <summary>Applies a clamped level offset relative to each target's captured starting level.</summary>
    public void ApplyDelta(int delta)
    {
        ValidateTargets();
        foreach (var target in _targets)
        {
            var level = (int)Math.Clamp((long)target.OriginalLevel + delta, 1L, 5L);
            target.Note.Velocity = level == target.OriginalLevel
                ? target.OriginalVelocity
                : Beat.MidiVelocityForIntensity(level);
        }
    }

    /// <summary>Restores exact original MIDI values, after validating the full target set.</summary>
    public void Cancel()
    {
        ValidateTargets();
        foreach (var target in _targets) target.Note.Velocity = target.OriginalVelocity;
    }

    public bool HasChanges => _targets.Any(target => target.Note.Velocity != target.OriginalVelocity);

    private void ValidateTargets()
    {
        foreach (var target in _targets)
            if (!target.Source.TabEvents.Contains(target.Note))
                throw new InvalidOperationException("A selected note no longer belongs to its source measure.");
    }
}

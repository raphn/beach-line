#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

public sealed record TabSelectionMove(TrackMeasure Source, TabEvent Note, int StringNumber, ScoreTime Onset, int? Fret = null);
public sealed record TabSelectionInterval(TabEvent Note, int StringNumber, ScoreTime Start, ScoreTime End);

/// <summary>Validated atomic source-event edits; the presenter supplies resolved repeat intervals.</summary>
public sealed class TabSelectionEditPlan
{
    private readonly ScoreTrack _track;
    private readonly TabSelectionMove[] _moves;
    private readonly (TrackMeasure Source, TabEvent Note)[] _removed;
    private readonly (TrackMeasure Source, TabEvent[] Events, (int Row, int Fret, int Pitch, int Velocity, ScoreTime Onset, ScoreTime Duration, bool Overflow, int TupletActual)[] State)[] _snapshot;
    private readonly int[] _tuning;
    public bool RequiresConfirmation { get; }

    public TabSelectionEditPlan(ScoreTrack track, IEnumerable<TabSelectionMove> moves,
        IEnumerable<(TrackMeasure Source, TabEvent Note)> removed, bool requiresConfirmation)
    {
        _track = track;
        _moves = moves.ToArray();
        _removed = removed.Distinct().ToArray();
        RequiresConfirmation = requiresConfirmation;
        _tuning = track.TabLaneMidiPitches.ToArray();
        _snapshot = _moves.Select(m => m.Source).Concat(_removed.Select(r => r.Source)).Distinct()
            .Select(source => (source, source.TabEvents.ToArray(), source.TabEvents.Select(State).ToArray())).ToArray();
        Validate();
    }

    private static (int, int, int, int, ScoreTime, ScoreTime, bool, int) State(TabEvent n) =>
        (n.StringNumber, n.Fret, n.MidiPitch, n.Velocity, n.Onset, n.Duration, n.AllowsBarOverflow, n.TupletActual);

    private void Validate()
    {
        foreach (var m in _moves)
        {
            if (m.Source.TrackId != _track.Id || !m.Source.TabEvents.Contains(m.Note))
                throw new InvalidOperationException("A selected note no longer belongs to this track.");
            TabEventEditing.ValidateAssignment(_track, m.StringNumber, m.Fret ?? m.Note.Fret);
            if (m.Fret is { } fret && fret > 24)
                throw new InvalidOperationException("The entire selection must stay between frets 0 and 24.");
            if (!m.Onset.IsInitialized || m.Onset < ScoreTime.Zero)
                throw new InvalidOperationException("The entire selection must fit; a note would start before its owning measure.");
        }
        if (_moves.Select(m => m.Note).Distinct().Count() != _moves.Length)
            throw new InvalidOperationException("Each source note must have one move target.");
        foreach (var r in _removed)
            if (r.Source.TrackId != _track.Id || !r.Source.TabEvents.Contains(r.Note))
                throw new InvalidOperationException("A removed note no longer belongs to this track.");
        var removed = _removed.Select(r => r.Note).ToHashSet();
        foreach (var source in _moves.Select(m => m.Source).Distinct())
        {
            var attacks = source.TabEvents.Where(n => !removed.Contains(n) && !_moves.Any(m => ReferenceEquals(m.Note, n)))
                .Select(n => (n.StringNumber, n.Onset)).Concat(_moves.Where(m => ReferenceEquals(m.Source, source)).Select(m => (m.StringNumber, m.Onset))).ToArray();
            if (attacks.Distinct().Count() != attacks.Length)
                throw new InvalidOperationException("The move would create duplicate attacks.");
        }
    }

    public void Apply(bool confirmed = false)
    {
        if (RequiresConfirmation && !confirmed) throw new InvalidOperationException("Confirm this edit before applying it.");
        if (!_tuning.SequenceEqual(_track.TabLaneMidiPitches) || _snapshot.Any(s => !s.Events.SequenceEqual(s.Source.TabEvents) ||
            !s.State.SequenceEqual(s.Source.TabEvents.Select(State))))
            throw new InvalidOperationException("The score changed; select the notes and try again.");
        Validate();
        foreach (var r in _removed) r.Source.TabEvents.Remove(r.Note);
        foreach (var m in _moves)
        {
            m.Note.StringNumber = m.StringNumber;
            m.Note.Onset = m.Onset;
            m.Note.Fret = m.Fret ?? m.Note.Fret;
            m.Note.MidiPitch = _track.TabLaneMidiPitches[m.StringNumber - 1] + m.Note.Fret;
        }
    }

    /// <summary>Finds conflicts across all projected repeats, retaining cutoffs between selected notes.</summary>
    public static IReadOnlyCollection<TabEvent> FindConflicts(IReadOnlyList<TabSelectionInterval> selected,
        IReadOnlyList<TabSelectionInterval> external)
    {
        if (selected.GroupBy(p => (p.StringNumber, p.Start)).Any(g => g.Count() > 1))
            throw new InvalidOperationException("The move would create duplicate attacks in linked occurrences.");
        return external.Where(e => selected.Any(p =>
        {
            var end = selected.Where(other => other.StringNumber == p.StringNumber && other.Start > p.Start)
                .Select(other => other.Start).Append(p.End).Min();
            return p.StringNumber == e.StringNumber && p.Start < e.End && e.Start < end;
        })).Select(e => e.Note).Distinct().ToArray();
    }
}

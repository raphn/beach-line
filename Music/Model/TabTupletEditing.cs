#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

public sealed record TabTupletOccurrence(TrackMeasure Source, TabEvent Note, ScoreTime Start, ScoreTime End);

/// <summary>Atomic tuplet assignment and measure-relative grid snapping for tablature events.</summary>
public static class TabTupletEditing
{
    public static void ApplyTuplet(IEnumerable<(TrackMeasure Source, TabEvent Note)> targets, int actual, int snapDenominator,
        IReadOnlyDictionary<TrackMeasure, ScoreTime>? sourceBarLimits = null,
        IEnumerable<TabTupletOccurrence>? occurrences = null,
        IReadOnlyDictionary<TrackMeasure, int>? sourceSnapDenominators = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var normal = actual switch { 0 => 1, 3 => 2, 5 or 6 => 4, _ => throw new ArgumentOutOfRangeException(nameof(actual)) };
        if (snapDenominator is not (4 or 8 or 16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(snapDenominator));
        var selected = targets.ToArray();
        if (selected.Length == 0) throw new ArgumentException("Select at least one note.", nameof(targets));
        if (selected.Any(target => target.Source is null || target.Note is null || !target.Source.TabEvents.Contains(target.Note)))
            throw new ArgumentException("Every selected note must belong to its source measure.", nameof(targets));
        if (selected.Select(target => target.Note).Distinct().Count() != selected.Length)
            throw new ArgumentException("Selected notes must be distinct.", nameof(targets));

        var pending = selected.Select(target =>
        {
            var denominator = sourceSnapDenominators?.GetValueOrDefault(target.Source, snapDenominator) ?? snapDenominator;
            if (denominator is not (4 or 8 or 16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(sourceSnapDenominators));
            var sourceStep = new ScoreTime(normal, checked((long)denominator * (actual == 0 ? 1 : actual)));
            var scaled = target.Note.Onset / sourceStep;
            var snappedIndex = checked((long)Math.Round((decimal)scaled.Numerator / scaled.Denominator, MidpointRounding.AwayFromZero));
            var onset = sourceStep * new ScoreTime(snappedIndex, 1);
            var oldActual = target.Note.TupletActual == 0 ? 1 : target.Note.TupletActual;
            var baseDuration = target.Note.Duration * new ScoreTime(oldActual, target.Note.TupletNormal);
            var duration = baseDuration * new ScoreTime(normal, actual == 0 ? 1 : actual);
            return (target.Source, target.Note, Onset: onset, Duration: duration);
        }).ToArray();

        if (sourceBarLimits is not null)
            foreach (var item in pending)
                if (!sourceBarLimits.TryGetValue(item.Source, out var limit) || !limit.IsInitialized || limit <= ScoreTime.Zero ||
                    (item.Onset >= limit && !item.Note.AllowsBarOverflow))
                    throw new InvalidOperationException("The tuplet note would start outside a linked destination measure.");

        foreach (var group in pending.GroupBy(item => item.Source))
        {
            var changes = group.ToDictionary(item => item.Note);
            var notes = group.Key.TabEvents;
            for (var i = 0; i < notes.Count; i++)
            for (var j = i + 1; j < notes.Count; j++)
            {
                var left = notes[i]; var right = notes[j];
                if (left.StringNumber != right.StringNumber || (!changes.ContainsKey(left) && !changes.ContainsKey(right))) continue;
                var oldOverlap = left.Onset < right.EndPosition && right.Onset < left.EndPosition;
                var leftOnset = changes.TryGetValue(left, out var newLeft) ? newLeft.Onset : left.Onset;
                var leftDuration = changes.TryGetValue(left, out newLeft) ? newLeft.Duration : left.Duration;
                var rightOnset = changes.TryGetValue(right, out var newRight) ? newRight.Onset : right.Onset;
                var rightDuration = changes.TryGetValue(right, out newRight) ? newRight.Duration : right.Duration;
                var newOverlap = leftOnset < rightOnset + rightDuration && rightOnset < leftOnset + leftDuration;
                if (newOverlap && (!oldOverlap || leftOnset == rightOnset))
                    throw new InvalidOperationException("Tuplet snapping would introduce a same-string overlap.");
            }
        }

        if (occurrences is not null)
        {
            var occurrenceList = occurrences.ToArray();
            var byEvent = pending.ToDictionary(item => item.Note);
            if (occurrenceList.Any(item => item.Source is null || item.Note is null || item.Start < ScoreTime.Zero || item.End <= item.Start ||
                !item.Source.TabEvents.Contains(item.Note)))
                throw new ArgumentException("Resolved occurrences must identify valid live event intervals.", nameof(occurrences));
            for (var i = 0; i < occurrenceList.Length; i++)
            for (var j = i + 1; j < occurrenceList.Length; j++)
            {
                var left = occurrenceList[i]; var right = occurrenceList[j];
                if (left.Note.StringNumber != right.Note.StringNumber ||
                    (!byEvent.ContainsKey(left.Note) && !byEvent.ContainsKey(right.Note))) continue;
                var oldOverlap = left.Start < right.End && right.Start < left.End;
                var leftStart = left.Start; var leftEnd = left.End;
                var rightStart = right.Start; var rightEnd = right.End;
                if (byEvent.TryGetValue(left.Note, out var changedLeft))
                {
                    var barStart = left.Start - left.Note.Onset;
                    leftStart = barStart + changedLeft.Onset;
                    leftEnd = leftStart + changedLeft.Duration;
                }
                if (byEvent.TryGetValue(right.Note, out var changedRight))
                {
                    var barStart = right.Start - right.Note.Onset;
                    rightStart = barStart + changedRight.Onset;
                    rightEnd = rightStart + changedRight.Duration;
                }
                if (leftStart < rightEnd && rightStart < leftEnd && (!oldOverlap || leftStart == rightStart))
                    throw new InvalidOperationException("Tuplet snapping would introduce a same-string overlap in a linked occurrence.");
            }
        }
        foreach (var item in pending)
        {
            item.Note.Onset = item.Onset;
            item.Note.Duration = item.Duration;
            item.Note.TupletActual = actual;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>
/// A tempo control point at an exact score position. Interpolation describes the segment
/// from this point to the next point in the map.
/// </summary>
public sealed record TempoMapPoint
{
    public TempoMapPoint(ScoreTime position, TempoMark value, TempoInterpolation interpolation = TempoInterpolation.Step)
    {
        if (!position.IsInitialized)
        {
            throw new ArgumentException("A tempo-map point needs an initialized score position.", nameof(position));
        }

        if (position < ScoreTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "A tempo-map point cannot precede the score.");
        }

        Position = position;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation), "Unknown tempo interpolation mode.");
        }

        Interpolation = interpolation;
    }

    public ScoreTime Position { get; }
    public TempoMark Value { get; }
    public TempoInterpolation Interpolation { get; }
}

/// <summary>
/// Tempo at a score position, expressed as whole notes per minute and with the active
/// written beat unit retained for notation/display.
/// </summary>
public readonly record struct TempoResolution
{
    internal TempoResolution(decimal wholeNotesPerMinute, RhythmicValue referenceBeatUnit)
    {
        if (wholeNotesPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wholeNotesPerMinute), "Resolved tempo must be greater than zero.");
        }

        WholeNotesPerMinute = wholeNotesPerMinute;
        ReferenceBeatUnit = referenceBeatUnit ?? throw new ArgumentNullException(nameof(referenceBeatUnit));
    }

    /// <summary>Number of whole-note durations per minute; use 60 / this value for seconds per whole note.</summary>
    public decimal WholeNotesPerMinute { get; }

    /// <summary>The written note value used as the tempo mark's beat.</summary>
    public RhythmicValue ReferenceBeatUnit { get; }

    /// <summary>BPM expressed using <see cref="ReferenceBeatUnit"/>.</summary>
    public decimal BeatsPerMinute => WholeNotesPerMinute / GetWholeNoteDuration(ReferenceBeatUnit);

    public TempoMark ToTempoMark() => new(BeatsPerMinute, ReferenceBeatUnit);

    private static decimal GetWholeNoteDuration(RhythmicValue value) =>
        (decimal)value.Duration.Numerator / value.Duration.Denominator;
}

/// <summary>
/// Resolves score-position tempo points. Step points hold until the next point; linear
/// points ramp in whole-notes-per-minute so marks with different beat units remain
/// musically consistent.
/// </summary>
public sealed class TempoMap
{
    private readonly TempoMapPoint[] _points;

    public TempoMap(IEnumerable<TempoMapPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        var sorted = points
            .Select((point, inputOrder) => (Point: point ?? throw new ArgumentException("Tempo points cannot contain null.", nameof(points)), InputOrder: inputOrder))
            .OrderBy(entry => entry.Point.Position)
            .ThenBy(entry => entry.InputOrder)
            .Select(entry => entry.Point)
            .ToArray();

        if (sorted.Length == 0)
        {
            throw new ArgumentException("A tempo map needs at least one point.", nameof(points));
        }

        var coalesced = new List<TempoMapPoint>(sorted.Length);
        foreach (var point in sorted)
        {
            if (coalesced.Count > 0 && coalesced[^1].Position == point.Position)
            {
                // Later points at the same score position intentionally take precedence.
                coalesced[^1] = point;
            }
            else
            {
                coalesced.Add(point);
            }
        }

        _points = coalesced.ToArray();
    }

    public IReadOnlyList<TempoMapPoint> Points => Array.AsReadOnly(_points);

    public TempoResolution ResolveAt(ScoreTime position)
    {
        if (!position.IsInitialized)
        {
            throw new ArgumentException("Tempo resolution needs an initialized score position.", nameof(position));
        }

        if (position < ScoreTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Tempo cannot be resolved before the score starts.");
        }

        var currentIndex = FindLastPointAtOrBefore(position);
        if (currentIndex < 0)
        {
            return FromMark(_points[0].Value);
        }

        var current = _points[currentIndex];
        if (current.Interpolation != TempoInterpolation.Linear || currentIndex == _points.Length - 1)
        {
            return FromMark(current.Value);
        }

        var next = _points[currentIndex + 1];
        var elapsed = ToDecimal(position) - ToDecimal(current.Position);
        var span = ToDecimal(next.Position) - ToDecimal(current.Position);
        if (span <= 0)
        {
            return FromMark(next.Value);
        }

        var amount = elapsed / span;
        var startRate = ToWholeNotesPerMinute(current.Value);
        var endRate = ToWholeNotesPerMinute(next.Value);
        var interpolatedRate = startRate + ((endRate - startRate) * amount);
        return new TempoResolution(interpolatedRate, current.Value.BeatUnit);
    }

    /// <summary>
    /// Converts an exact score position into elapsed seconds from score position zero.
    /// Linear tempo segments are integrated in whole-notes-per-minute, matching
    /// <see cref="ResolveAt(ScoreTime)"/>.
    /// </summary>
    public double GetElapsedSeconds(ScoreTime position)
    {
        if (!position.IsInitialized)
        {
            throw new ArgumentException("Elapsed-time conversion needs an initialized score position.", nameof(position));
        }

        if (position < ScoreTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Elapsed time cannot be resolved before the score starts.");
        }

        if (position == ScoreTime.Zero)
        {
            return 0.0;
        }

        var elapsedSeconds = 0.0;
        var segmentStart = ScoreTime.Zero;
        var currentIndex = FindLastPointAtOrBefore(ScoreTime.Zero);
        var current = currentIndex >= 0 ? _points[currentIndex] : _points[0];

        while (segmentStart < position)
        {
            var nextIndex = currentIndex + 1;
            var hasNextPoint = nextIndex < _points.Length;
            var nextPosition = hasNextPoint ? _points[nextIndex].Position : position;
            var segmentEnd = nextPosition < position ? nextPosition : position;

            if (segmentEnd > segmentStart)
            {
                var startRate = ToDouble(ToWholeNotesPerMinute(current.Value));
                if (currentIndex >= 0 && hasNextPoint && current.Interpolation == TempoInterpolation.Linear)
                {
                    var wholeNoteSpan = ToDouble(nextPosition - current.Position);
                    var elapsedWholeNotes = ToDouble(segmentEnd - current.Position);
                    var endRate = ToDouble(ToWholeNotesPerMinute(_points[nextIndex].Value));
                    var rateDelta = endRate - startRate;
                    var rateAtEnd = startRate + (rateDelta * (elapsedWholeNotes / wholeNoteSpan));

                    if (Math.Abs(rateDelta) < 1e-12)
                    {
                        elapsedSeconds += 60.0 * ToDouble(segmentEnd - segmentStart) / startRate;
                    }
                    else
                    {
                        var slope = rateDelta / wholeNoteSpan;
                        elapsedSeconds += 60.0 / slope * Math.Log(rateAtEnd / startRate);
                    }
                }
                else
                {
                    elapsedSeconds += 60.0 * ToDouble(segmentEnd - segmentStart) / startRate;
                }
            }

            segmentStart = segmentEnd;
            if (hasNextPoint && segmentStart == nextPosition)
            {
                currentIndex = nextIndex;
                current = _points[currentIndex];
            }
            else
            {
                break;
            }
        }

        return elapsedSeconds;
    }

    private int FindLastPointAtOrBefore(ScoreTime position)
    {
        var low = 0;
        var high = _points.Length - 1;
        var result = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (_points[middle].Position <= position)
            {
                result = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return result;
    }

    private static TempoResolution FromMark(TempoMark mark) =>
        new(ToWholeNotesPerMinute(mark), mark.BeatUnit);

    private static decimal ToWholeNotesPerMinute(TempoMark mark) =>
        mark.BeatsPerMinute * GetWholeNoteDuration(mark.BeatUnit);

    private static decimal GetWholeNoteDuration(RhythmicValue value) =>
        (decimal)value.Duration.Numerator / value.Duration.Denominator;

    private static decimal ToDecimal(ScoreTime value) =>
        (decimal)value.Numerator / value.Denominator;

    private static double ToDouble(ScoreTime value) =>
        (double)value.Numerator / value.Denominator;

    private static double ToDouble(decimal value) => (double)value;
}

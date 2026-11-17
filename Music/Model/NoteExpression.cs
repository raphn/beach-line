#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>Meaning of a per-note expression curve.</summary>
public enum ExpressionKind { Pitch, Vibrato, Tremolo, Velocity }

/// <summary>Interpolation applied from an expression point to the next point.</summary>
public enum ExpressionSegmentMode { Linear = 0, Step = 1 }

/// <summary>A point on a curve with normalized time and kind-specific value.</summary>
public sealed record ExpressionPoint(double X, double Y, ExpressionSegmentMode SegmentMode = ExpressionSegmentMode.Linear);

/// <summary>Portable expression definition shared by notes with the same identity.</summary>
public sealed class NoteExpression
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ExpressionKind Kind { get; set; }
    public List<ExpressionPoint> Points { get; set; } = new();
    /// <summary>Lower and upper absolute pitch limits for pitch and vibrato curves.</summary>
    public double MinSemitones { get; set; } = -2d;
    public double MaxSemitones { get; set; } = 2d;
    /// <summary>Position in the selected attack span for velocity curves; normalized to [0,1].</summary>
    public double Position { get; set; }

    /// <summary>Samples the configured segments, holding end values outside the point range.</summary>
    public double Sample(double x)
    {
        Validate();
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        x = Math.Clamp(x, 0d, 1d);
        if (Points.Count == 0) return Kind is ExpressionKind.Tremolo or ExpressionKind.Velocity ? 1d : 0d;
        if (x <= Points[0].X) return Points[0].Y;
        for (var i = 1; i < Points.Count; i++)
        {
            var right = Points[i];
            if (x < right.X)
            {
                var left = Points[i - 1];
                if (left.SegmentMode == ExpressionSegmentMode.Step) return left.Y;
                var t = (x - left.X) / (right.X - left.X);
                return left.Y + (right.Y - left.Y) * t;
            }
        }
        return Points[^1].Y;
    }

    /// <summary>Copies the definition while retaining identity so linked notes remain linked.</summary>
    public NoteExpression Clone() => new() { Id = Id, Kind = Kind, Position = Position, MinSemitones = MinSemitones,
        MaxSemitones = MaxSemitones, Points = Points.ToList() };

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("Expression identity cannot be empty.", nameof(Id));
        if (!Enum.IsDefined(Kind)) throw new ArgumentOutOfRangeException(nameof(Kind));
        if (!double.IsFinite(Position) || Position is < 0d or > 1d) throw new ArgumentOutOfRangeException(nameof(Position));
        if (!double.IsFinite(MinSemitones) || !double.IsFinite(MaxSemitones) || MinSemitones is < -24d or > 24d || MaxSemitones is < -24d or > 24d || MinSemitones >= MaxSemitones)
            throw new ArgumentOutOfRangeException(nameof(MinSemitones), "Pitch limits must be ordered and stay within +/-24 semitones.");
        if (Points is null) throw new ArgumentNullException(nameof(Points));
        var previous = -1d;
        foreach (var point in Points)
        {
            if (point is null) throw new ArgumentException("Expression points cannot be null.", nameof(Points));
            if (!Enum.IsDefined(point.SegmentMode)) throw new ArgumentOutOfRangeException(nameof(Points), "Expression segment mode is invalid.");
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X is < 0d or > 1d || point.X <= previous)
                throw new ArgumentException("Expression points must have finite values and strictly increasing X coordinates in [0,1].", nameof(Points));
            var validY = Kind switch
            {
                ExpressionKind.Pitch or ExpressionKind.Vibrato => point.Y >= MinSemitones && point.Y <= MaxSemitones,
                ExpressionKind.Tremolo => point.Y is >= 0d and <= 1d,
                ExpressionKind.Velocity => point.Y is >= 0d and <= 2d,
                _ => false
            };
            if (!validY) throw new ArgumentOutOfRangeException(nameof(Points), $"A {Kind} value is outside its supported range.");
            previous = point.X;
        }
    }
}

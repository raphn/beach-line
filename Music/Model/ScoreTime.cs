using System;

namespace OpenTab.Music;

/// <summary>
/// Exact musical time measured in whole-note units.
/// </summary>
public readonly record struct ScoreTime : IComparable<ScoreTime>
{
    public static ScoreTime Zero { get; } = new(0, 1);

    public ScoreTime(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "A musical-time denominator cannot be zero.");
        }

        if (denominator < 0)
        {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        var divisor = GreatestCommonDivisor(Math.Abs(numerator), denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    public long Numerator { get; }
    public long Denominator { get; }
    public bool IsInitialized => Denominator > 0;

    public int CompareTo(ScoreTime other)
    {
        var left = checked(Numerator * other.Denominator);
        var right = checked(other.Numerator * Denominator);
        return left.CompareTo(right);
    }

    public static ScoreTime operator +(ScoreTime left, ScoreTime right) =>
        new(
            checked(left.Numerator * right.Denominator + right.Numerator * left.Denominator),
            checked(left.Denominator * right.Denominator));

    public static ScoreTime operator -(ScoreTime left, ScoreTime right) =>
        new(
            checked(left.Numerator * right.Denominator - right.Numerator * left.Denominator),
            checked(left.Denominator * right.Denominator));

    public static ScoreTime operator *(ScoreTime left, ScoreTime right) =>
        new(checked(left.Numerator * right.Numerator), checked(left.Denominator * right.Denominator));

    public static ScoreTime operator /(ScoreTime left, ScoreTime right)
    {
        if (right.Numerator == 0)
        {
            throw new DivideByZeroException("Cannot divide musical time by zero.");
        }

        return new(
            checked(left.Numerator * right.Denominator),
            checked(left.Denominator * right.Numerator));
    }

    public static bool operator <(ScoreTime left, ScoreTime right) => left.CompareTo(right) < 0;
    public static bool operator >(ScoreTime left, ScoreTime right) => left.CompareTo(right) > 0;
    public static bool operator <=(ScoreTime left, ScoreTime right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ScoreTime left, ScoreTime right) => left.CompareTo(right) >= 0;

    public override string ToString() => Denominator == 1 ? Numerator.ToString() : $"{Numerator}/{Denominator}";

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left == 0 ? 1 : left;
    }
}

/// <summary>
/// A written note value, including augmentation dots and one tuplet ratio.
/// A denominator of 4 is a quarter note; the resulting duration is 1/4 of a whole note.
/// </summary>
public sealed record RhythmicValue
{
    public RhythmicValue(int noteDenominator, int dots = 0, int tupletActual = 1, int tupletNormal = 1)
    {
        if (noteDenominator is not (1 or 2 or 4 or 8 or 16 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(noteDenominator), "Supported note values are whole through 64th notes.");
        }

        if (dots is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(dots), "Use zero, one, or two augmentation dots.");
        }

        if (tupletActual < 1 || tupletNormal < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tupletActual), "Tuplet counts must be positive.");
        }

        NoteDenominator = noteDenominator;
        Dots = dots;
        TupletActual = tupletActual;
        TupletNormal = tupletNormal;
    }

    public int NoteDenominator { get; }
    public int Dots { get; }
    public int TupletActual { get; }
    public int TupletNormal { get; }

    public ScoreTime Duration
    {
        get
        {
            var dottedNumerator = (1 << (Dots + 1)) - 1;
            var dottedDenominator = 1 << Dots;
            return new ScoreTime(1, NoteDenominator)
                * new ScoreTime(dottedNumerator, dottedDenominator)
                * new ScoreTime(TupletNormal, TupletActual);
        }
    }

    public override string ToString()
    {
        var dots = new string('.', Dots);
        var tuplet = TupletActual == TupletNormal ? string.Empty : $" ({TupletActual}:{TupletNormal})";
        return $"1/{NoteDenominator}{dots}{tuplet}";
    }
}

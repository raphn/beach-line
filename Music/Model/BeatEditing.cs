#nullable enable
using System;

namespace OpenTab.Music;

/// <summary>
/// Helpers for creating beats while editing incomplete measures.
/// </summary>
public static class BeatEditing
{
    private static readonly int[] StandardNoteDenominators = [1, 2, 4, 8, 16, 32, 64];

    /// <summary>
    /// Creates one silent beat using the largest undotted standard note value that
    /// fits in the remaining duration of a regular measure. For a positive residual
    /// shorter than a 64th note, uses an exact 64th-note tuplet when its ratio fits
    /// in the supported integer range. Returns <see langword="null"/> when the voice
    /// is complete or overfull, the measure is pickup/free-time, or that ratio cannot
    /// be represented. When repeatLastValue is true, copies the last beat's full
    /// state (rhythm, notes, intensity and pitch bend) with fresh identities only
    /// if it fits; empty voices use the normal selection.
    /// </summary>
    public static Beat? CreateIncompleteVoicePlaceholder(
        ScoreMeasure measure,
        Voice voice,
        TimeSignature timeSignature,
        bool repeatLastValue = false,
        int defaultIntensity = Beat.DefaultIntensity)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(voice);
        _ = Beat.MidiVelocityForIntensity(defaultIntensity);

        if (measure.IsPickup || measure.IsFreeTime)
        {
            return null;
        }

        var actualDuration = ScoreTime.Zero;
        foreach (var beat in voice.Beats)
        {
            actualDuration += beat.Duration;
        }

        var gap = timeSignature.BarDuration - actualDuration;
        if (gap <= ScoreTime.Zero)
        {
            return null;
        }

        if (repeatLastValue && voice.Beats.Count > 0)
        {
            var lastBeat = voice.Beats[^1];
            if (lastBeat.Duration > gap)
            {
                return null;
            }

            var copy = new Beat(lastBeat.RhythmicValue)
            {
                Intensity = lastBeat.Intensity,
                PitchBendSemitones = lastBeat.PitchBendSemitones
            };
            foreach (var note in lastBeat.Notes)
            {
                copy.Notes.Add(new ScoreNote(note.MidiPitch, note.Velocity));
            }

            return copy;
        }

        foreach (var denominator in StandardNoteDenominators)
        {
            var value = new RhythmicValue(denominator);
            if (value.Duration <= gap)
            {
                return new Beat(value) { Intensity = defaultIntensity };
            }
        }

        var gapGcd = GreatestCommonDivisor(gap.Numerator, gap.Denominator);
        var numerator = gap.Numerator / gapGcd;
        var gapDenominator = gap.Denominator / gapGcd;
        var baseGcd = GreatestCommonDivisor(64, gapDenominator);
        var normalFactor = 64 / baseGcd;
        var actual = gapDenominator / baseGcd;
        if (numerator <= 0 || numerator > int.MaxValue / normalFactor || actual <= 0 || actual > int.MaxValue)
        {
            return null;
        }

        var normal = numerator * normalFactor;
        return new Beat(new RhythmicValue(64, tupletActual: (int)actual, tupletNormal: (int)normal))
        {
            Intensity = defaultIntensity
        };
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Abs(left);
    }
}

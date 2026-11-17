#nullable enable

using System;
using System.Collections.Generic;
using OpenTab.Music;
using System.Linq;

namespace OpenTab.Music.Playback;

/// <summary>Pure timing rules used by the isolated note-expression audition.</summary>
public static class NoteExpressionPreviewTiming
{
    public static (long NoteFrames, long BeatGapFrames) GetFrameDurations(ScoreTime duration, TempoResolution tempo, int sampleRate)
    {
        if (!duration.IsInitialized || duration <= ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (tempo.WholeNotesPerMinute <= 0) throw new ArgumentOutOfRangeException(nameof(tempo));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var secondsPerWholeNote = 60.0 / (double)tempo.WholeNotesPerMinute;
        var noteFrames = Frames(duration, secondsPerWholeNote, sampleRate);
        var beatFrames = Frames(tempo.ReferenceBeatUnit.Duration, secondsPerWholeNote, sampleRate);
        return (noteFrames, beatFrames);
    }

    public static bool ShouldRepeat(bool loopEnabled, bool stopAfterCycle) => loopEnabled && !stopAfterCycle;

    public static int ApplyVelocity(int velocity, IReadOnlyList<NoteExpression> expressions)
    {
        if (velocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(velocity));
        ArgumentNullException.ThrowIfNull(expressions);
        var multiplier = expressions.Where(effect => effect.Kind == ExpressionKind.Velocity)
            .Aggregate(1d, (value, effect) => value * effect.Sample(effect.Position));
        if (multiplier <= 0) return 0;
        return Math.Clamp((int)Math.Round(velocity * multiplier, MidpointRounding.AwayFromZero), 1, 127);
    }

    private static long Frames(ScoreTime duration, double secondsPerWholeNote, int sampleRate) =>
        Math.Max(1, (long)Math.Round((double)duration.Numerator / duration.Denominator * secondsPerWholeNote * sampleRate,
            MidpointRounding.AwayFromZero));
}

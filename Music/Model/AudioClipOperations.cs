#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music.Playback;

namespace OpenTab.Music;

/// <summary>Non-destructive section clip edits shared by the timeline and regression checks.</summary>
public static class AudioClipOperations
{
    /// <summary>Moves a clip on the musical timeline without changing its source range.</summary>
    public static bool TryMove(ScoreDocument score, ScoreSection? section, ScoreArrangement? arrangement, IReadOnlyList<AudioClip> clips,
        AudioClip clip, ScoreTime newStart, out AudioClip? result, out string error)
    {
        result = null;
        if (!TryPrepare(score, section, arrangement, clips, clip, out var asset, out var timing, out error)) return false;
        if (!newStart.IsInitialized || newStart < ScoreTime.Zero ||
            !timing.TryGetElapsedSeconds(newStart, out _))
            return Fail("The clip start must be inside the selected timeline.", out error);
        var candidate = new AudioClip(clip.Id, clip.AssetId, clip.TrackId, newStart,
            clip.SourceOffsetFrames, clip.DurationFrames);
        if (!HasNoOverlap(score, clips, candidate, asset, timing, clip.Id, out error)) return false;
        result = candidate;
        return true;
    }

    /// <summary>
    /// Splits at an exact absolute source frame. The source file and existing clip remain untouched;
    /// the returned left clip keeps the original ID and the right clip receives a new ID.
    /// </summary>
    public static bool TryCut(ScoreDocument score, ScoreSection? section, ScoreArrangement? arrangement, IReadOnlyList<AudioClip> clips,
        AudioClip clip, long cutSourceFrame, out AudioClip? left, out AudioClip? right, out string error)
    {
        left = right = null;
        if (!TryPrepare(score, section, arrangement, clips, clip, out var asset, out var timing, out error)) return false;
        var sourceEnd = checked(clip.SourceOffsetFrames + clip.DurationFrames);
        if (cutSourceFrame <= clip.SourceOffsetFrames || cutSourceFrame >= sourceEnd)
            return Fail("Cut point must fall strictly inside the clip's source frames.", out error);
        if (!timing.TryGetElapsedSeconds(clip.Start, out var startSeconds))
            return Fail("The clip starts outside the selected timeline.", out error);
        var cutSeconds = startSeconds + (cutSourceFrame - clip.SourceOffsetFrames) / (double)asset.SampleRate;
        if (!(cutSeconds < timing.GetDurationSeconds()))
            return Fail("Cut point must be before the clipped timeline end.", out error);
        if (!timing.TryGetScoreTimeAtSeconds(cutSeconds, out var rightStart, ScoreTimeRounding.Ceiling))
            return Fail("The cut point falls outside the selected timeline.", out error);

        var leftDuration = cutSourceFrame - clip.SourceOffsetFrames;
        var rightDuration = sourceEnd - cutSourceFrame;
        var first = new AudioClip(clip.Id, clip.AssetId, clip.TrackId, clip.Start,
            clip.SourceOffsetFrames, leftDuration);
        var second = new AudioClip(clip.AssetId, clip.TrackId, rightStart, cutSourceFrame, rightDuration);
        if (!HasNoOverlap(score, clips, first, asset, timing, clip.Id, out error) ||
            !HasNoOverlap(score, clips, second, asset, timing, clip.Id, out error)) return false;
        left = first;
        right = second;
        return true;
    }

    /// <summary>Changes musical start and source range while retaining the source asset unchanged.</summary>
    public static bool TryEdit(ScoreDocument score, ScoreSection? section, ScoreArrangement? arrangement, IReadOnlyList<AudioClip> clips,
        AudioClip clip, ScoreTime newStart, long sourceOffsetFrames, long durationFrames,
        out AudioClip? result, out string error)
    {
        result = null;
        if (!TryPrepare(score, section, arrangement, clips, clip, out var asset, out var timing, out error)) return false;
        if (!newStart.IsInitialized || newStart < ScoreTime.Zero || !timing.TryGetElapsedSeconds(newStart, out _))
            return Fail("The clip start must be inside the selected timeline.", out error);
        if (sourceOffsetFrames < 0 || durationFrames < 1 || sourceOffsetFrames > asset.FrameCount ||
            durationFrames > asset.FrameCount - sourceOffsetFrames)
            return Fail("The edited source range must stay inside the original audio asset.", out error);
        var candidate = new AudioClip(clip.Id, clip.AssetId, clip.TrackId, newStart, sourceOffsetFrames, durationFrames);
        if (!HasNoOverlap(score, clips, candidate, asset, timing, clip.Id, out error)) return false;
        result = candidate;
        return true;
    }

    /// <summary>
    /// Moves the left edge to a context-relative elapsed time. The requested time is quantized to the
    /// nearest source sample; the old absolute source end is retained, so the right edge stays fixed.
    /// </summary>
    public static bool TryTrimLeft(ScoreDocument score, ScoreSection? section, ScoreArrangement? arrangement,
        IReadOnlyList<AudioClip> clips, AudioClip clip, double newBoundaryElapsedSeconds,
        out AudioClip? result, out string error)
    {
        result = null;
        if (!TryPrepare(score, section, arrangement, clips, clip, out var asset, out var timing, out error)) return false;
        if (!timing.TryGetElapsedSeconds(clip.Start, out var oldStartSeconds))
            return Fail("The clip starts outside the selected timeline.", out error);
        if (!TryGetFrameDelta(newBoundaryElapsedSeconds - oldStartSeconds, asset.SampleRate, out var frameDelta))
            return Fail("The trim boundary is not a valid timeline position.", out error);

        long sourceOffset;
        try { sourceOffset = checked(clip.SourceOffsetFrames + frameDelta); }
        catch (OverflowException) { return Fail("The trim boundary exceeds the audio source.", out error); }
        var originalSourceEnd = clip.SourceOffsetFrames + clip.DurationFrames;
        if (sourceOffset < 0 || sourceOffset >= originalSourceEnd || sourceOffset >= asset.FrameCount)
            return Fail("The left trim must leave at least one source frame inside the audio asset.", out error);

        var quantizedStartSeconds = oldStartSeconds + frameDelta / (double)asset.SampleRate;
        if (!timing.TryGetScoreTimeAtSeconds(quantizedStartSeconds, out var start, ScoreTimeRounding.Nearest))
            return Fail("The quantized trim boundary falls outside the selected timeline.", out error);
        var candidate = new AudioClip(clip.Id, clip.AssetId, clip.TrackId, start,
            sourceOffset, originalSourceEnd - sourceOffset);
        if (!HasNoOverlap(score, clips, candidate, asset, timing, clip.Id, out error)) return false;
        result = candidate;
        return true;
    }

    /// <summary>
    /// Moves the right edge to a context-relative elapsed time. The start and source offset remain fixed;
    /// source audio is never modified. The timeline context clips playback at its end as usual.
    /// </summary>
    public static bool TryTrimRight(ScoreDocument score, ScoreSection? section, ScoreArrangement? arrangement,
        IReadOnlyList<AudioClip> clips, AudioClip clip, double newBoundaryElapsedSeconds,
        out AudioClip? result, out string error)
    {
        result = null;
        if (!TryPrepare(score, section, arrangement, clips, clip, out var asset, out var timing, out error)) return false;
        if (!timing.TryGetElapsedSeconds(clip.Start, out var startSeconds))
            return Fail("The clip starts outside the selected timeline.", out error);
        if (!double.IsFinite(newBoundaryElapsedSeconds) || newBoundaryElapsedSeconds < startSeconds ||
            newBoundaryElapsedSeconds > timing.GetDurationSeconds())
            return Fail("The right trim boundary must be between the clip start and selected timeline end.", out error);
        if (!TryGetFrameDelta(newBoundaryElapsedSeconds - startSeconds, asset.SampleRate, out var durationFrames) || durationFrames < 1)
            return Fail("The right trim must leave at least one source frame and use a valid timeline boundary.", out error);
        if (durationFrames > asset.FrameCount - clip.SourceOffsetFrames)
            return Fail("The right trim exceeds the original audio source.", out error);

        var candidate = new AudioClip(clip.Id, clip.AssetId, clip.TrackId, clip.Start,
            clip.SourceOffsetFrames, durationFrames);
        if (!HasNoOverlap(score, clips, candidate, asset, timing, clip.Id, out error)) return false;
        result = candidate;
        return true;
    }

    private static bool TryGetFrameDelta(double elapsedSeconds, int sampleRate, out long frames)
    {
        frames = 0;
        if (!double.IsFinite(elapsedSeconds)) return false;
        var value = elapsedSeconds * sampleRate;
        if (!double.IsFinite(value) || value < long.MinValue || value > long.MaxValue) return false;
        frames = checked((long)Math.Round(value, MidpointRounding.AwayFromZero));
        return true;
    }

    private static bool TryPrepare(ScoreDocument score, ScoreSection? section, ScoreArrangement? arrangement, IReadOnlyList<AudioClip> clips,
        AudioClip clip, out AudioAsset asset, out AudioTimelineTiming timing, out string error)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(clip);
        if ((section is null) == (arrangement is null))
        {
            asset = null!;
            timing = null!;
            return Fail("Choose exactly one section or arrangement context.", out error);
        }
        if (section is not null && !score.Sections.Contains(section) ||
            arrangement is not null && !score.Arrangements.Contains(arrangement))
        {
            asset = null!;
            timing = null!;
            return Fail("The selected timeline context does not belong to this score.", out error);
        }
        asset = score.AudioAssets.FirstOrDefault(item => item.Id == clip.AssetId)!;
        timing = new AudioTimelineTiming(score, section, arrangement);
        if (asset is null || clip.SourceOffsetFrames > asset.FrameCount ||
            clip.DurationFrames > asset.FrameCount - clip.SourceOffsetFrames)
            return Fail("The clip source is missing or its source range is invalid.", out error);
        if (!clips.Any(item => item.Id == clip.Id))
            return Fail("The clip is not present in the selected timeline.", out error);
        error = string.Empty;
        return true;
    }

    private static bool HasNoOverlap(ScoreDocument score, IReadOnlyList<AudioClip> clips,
        AudioClip candidate, AudioAsset candidateAsset, AudioTimelineTiming timing, Guid ignoredId, out string error)
    {
        var candidateRange = GetRange(candidate, candidateAsset, timing);
        if (candidateRange is null)
            return Fail("The clip start must be inside the selected timeline.", out error);
        foreach (var other in clips.Where(item => item.TrackId == candidate.TrackId && item.Id != ignoredId))
        {
            var otherAsset = score.AudioAssets.FirstOrDefault(item => item.Id == other.AssetId);
            if (otherAsset is null) continue;
            var otherRange = GetRange(other, otherAsset, timing);
            if (otherRange is { } range && candidateRange.Value.Start < range.End && range.Start < candidateRange.Value.End)
                return Fail("Clips on the same track cannot overlap. Move the clip or trim its source duration.", out error);
        }
        error = string.Empty;
        return true;
    }

    private static (double Start, double End)? GetRange(AudioClip clip, AudioAsset asset, AudioTimelineTiming timing)
    {
        if (!timing.TryGetElapsedSeconds(clip.Start, out var start)) return null;
        var end = Math.Min(timing.GetDurationSeconds(), start + clip.DurationFrames / (double)asset.SampleRate);
        return end > start ? (start, end) : null;
    }

    private static bool Fail(string message, out string error) { error = message; return false; }
}

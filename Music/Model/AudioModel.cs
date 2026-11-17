#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenTab.Music;

/// <summary>Immutable source metadata; audio bytes are held in a managed cache outside score snapshots.</summary>
public sealed class AudioAsset
{
    public AudioAsset(string fileName, string sha256, int sampleRate, int channels, long frameCount, string cachePath)
        : this(Guid.NewGuid(), fileName, sha256, sampleRate, channels, frameCount, cachePath) { }

    public AudioAsset(Guid id, string fileName, string sha256, int sampleRate, int channels, long frameCount, string cachePath)
    {
        if (id == Guid.Empty) throw new ArgumentException("An audio asset needs a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("An audio asset needs a file name.", nameof(fileName));
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("An audio asset needs a SHA-256 checksum.", nameof(sha256));
        if (sampleRate is < 8000 or > 384000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (string.IsNullOrWhiteSpace(cachePath)) throw new ArgumentException("An audio asset needs a managed cache reference.", nameof(cachePath));
        Id = id; FileName = Path.GetFileName(fileName); Sha256 = sha256.ToLowerInvariant(); SampleRate = sampleRate; Channels = channels; FrameCount = frameCount; CachePath = cachePath;
    }

    public Guid Id { get; }
    public string FileName { get; }
    public string Sha256 { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long FrameCount { get; }
    /// <summary>Absolute path in the app-managed cache; metadata snapshots persist the path but never the media bytes.</summary>
    public string CachePath { get; }
}

/// <summary>Nondestructive reference to a frame range in an audio source at a musical timeline position.</summary>
public sealed class AudioClip
{
    public AudioClip(Guid assetId, Guid trackId, ScoreTime start, long sourceOffsetFrames, long durationFrames)
        : this(Guid.NewGuid(), assetId, trackId, start, sourceOffsetFrames, durationFrames) { }

    public AudioClip(Guid id, Guid assetId, Guid trackId, ScoreTime start, long sourceOffsetFrames, long durationFrames)
    {
        if (id == Guid.Empty || assetId == Guid.Empty || trackId == Guid.Empty) throw new ArgumentException("An audio clip needs non-empty identities.");
        if (!start.IsInitialized || start.Numerator < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (sourceOffsetFrames < 0) throw new ArgumentOutOfRangeException(nameof(sourceOffsetFrames));
        if (durationFrames < 1) throw new ArgumentOutOfRangeException(nameof(durationFrames));
        Id = id; AssetId = assetId; TrackId = trackId; Start = start; SourceOffsetFrames = sourceOffsetFrames; DurationFrames = durationFrames;
    }
    public Guid Id { get; }
    public Guid AssetId { get; }
    public Guid TrackId { get; }
    public ScoreTime Start { get; }
    public long SourceOffsetFrames { get; }
    public long DurationFrames { get; }
}

internal static class AudioModelValidation
{
    public static void ValidateAudio(ScoreDocument score)
    {
        if (score.AudioAssets.Any(asset => asset is null) || score.AudioAssets.Select(asset => asset.Id).Distinct().Count() != score.AudioAssets.Count)
            throw new InvalidDataException("Audio assets must have unique identities.");
        var assets = score.AudioAssets.ToDictionary(asset => asset.Id);
        var clipIds = new HashSet<Guid>();
        foreach (var (clips, arrangementId) in score.Sections.Select(section => (section.AudioClips, (Guid?)null))
                     .Concat(score.Arrangements.Select(arrangement => (arrangement.AudioClips, (Guid?)arrangement.Id))))
        {
            foreach (var clip in clips)
            {
                if (!clipIds.Add(clip.Id) || !assets.TryGetValue(clip.AssetId, out var asset) ||
                    clip.SourceOffsetFrames > asset.FrameCount || clip.DurationFrames > asset.FrameCount - clip.SourceOffsetFrames)
                    throw new InvalidDataException("An audio clip has a duplicate identity, missing source, or out-of-range source frames.");
                if (!score.Tracks.Any(track => track.Id == clip.TrackId && track.Kind == TrackKind.Audio))
                    throw new InvalidDataException("An audio clip must belong to an audio track.");
                if (arrangementId is not null && score.Arrangements.All(arrangement => arrangement.Id != arrangementId))
                    throw new InvalidDataException("An arrangement audio clip has no owning arrangement.");
            }
        }
    }
}

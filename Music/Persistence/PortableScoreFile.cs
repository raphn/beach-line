#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using OpenTab.Music;
using BeachLine.Music.Audio;

namespace OpenTab.Music.Persistence;

/// <summary>Streams project media into explicit saves; history and session snapshots remain metadata-only.</summary>
public static class PortableScoreFile
{
    private static string EntryName(Guid id) => $"audio/{id:N}.wav";

    public static void Save(string path, ScoreDocument score)
    {
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var metadata = new MemoryStream(ScoreFile.Serialize(score, portable: true), writable: false);
            using var metadataArchive = new ZipArchive(metadata, ZipArchiveMode.Read);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var source = metadataArchive.GetEntry("project.json")!.Open())
                using (var target = archive.CreateEntry("project.json", CompressionLevel.Optimal).Open()) source.CopyTo(target);
                foreach (var asset in score.AudioAssets)
                {
                    using var source = File.OpenRead(asset.CachePath);
                    var entry = archive.CreateEntry(EntryName(asset.Id), CompressionLevel.NoCompression);
                    using var target = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        hash.AppendData(buffer, 0, count);
                        target.Write(buffer, 0, count);
                    }
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Audio source '{asset.FileName}' is missing or has changed.");
                }
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ScoreDocument Load(string path, string cacheDirectory)
    {
        using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var project = archive.GetEntry("project.json") ?? throw new InvalidDataException("The score archive has no project.json.");
        if (project.Length > 64 * 1024 * 1024) throw new InvalidDataException("The score document is too large.");
        using var metadata = new MemoryStream();
        using (var copy = new ZipArchive(metadata, ZipArchiveMode.Create, true))
        using (var input = project.Open())
        using (var output = copy.CreateEntry("project.json").Open()) input.CopyTo(output);
        var score = ScoreFile.Deserialize(metadata.ToArray());
        if (score.AudioAssets.Count == 0) return score;
        Directory.CreateDirectory(cacheDirectory);
        for (var i = 0; i < score.AudioAssets.Count; i++)
        {
            var asset = score.AudioAssets[i];
            var entry = archive.GetEntry(EntryName(asset.Id)) ?? throw new InvalidDataException($"Audio source '{asset.FileName}' is missing from the project.");
            // Standard RIFF WAV files cannot exceed 4 GiB, and importing must not trust a ZIP entry path.
            if (entry.Length > uint.MaxValue + 8L) throw new InvalidDataException("The embedded WAV is too large.");
            var temporary = Path.Combine(cacheDirectory, $".{Guid.NewGuid():N}.tmp");
            try
            {
                using (var source = entry.Open())
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var buffer = new byte[65536];
                    int count;
                    while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                    { hash.AppendData(buffer, 0, count); target.Write(buffer, 0, count); }
                    target.Flush(true);
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Audio source '{asset.FileName}' failed its checksum.");
                }
                var (rate, channels, frames) = WavAssetStore.ReadFormat(temporary);
                if (rate != asset.SampleRate || channels != asset.Channels || frames != asset.FrameCount)
                    throw new InvalidDataException($"Audio source '{asset.FileName}' has inconsistent metadata.");
                var destination = Path.Combine(cacheDirectory, asset.Sha256 + ".wav");
                File.Move(temporary, destination, true);
                score.AudioAssets[i] = new AudioAsset(asset.Id, asset.FileName, asset.Sha256, rate, channels, frames, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return score;
    }
}

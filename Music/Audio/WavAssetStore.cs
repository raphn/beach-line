#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OpenTab.Music;

namespace BeachLine.Music.Audio;

/// <summary>Imports immutable 16-bit PCM WAV media into a caller-owned working cache.</summary>
public static class WavAssetStore
{
    public static AudioAsset Import(string sourcePath, string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        var temp = Path.Combine(cacheDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(true);
            }
            var (rate, channels, frames) = ReadFormat(temp);
            string checksum;
            using (var stream = File.OpenRead(temp)) checksum = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var destination = Path.Combine(cacheDirectory, checksum + ".wav");
            if (File.Exists(destination))
            {
                using var existing = File.OpenRead(destination);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(existing)), checksum, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The cached audio file does not match its checksum.");
                File.Delete(temp);
            }
            else File.Move(temp, destination);
            return new AudioAsset(Path.GetFileName(sourcePath), checksum, rate, channels, frames, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    /// <summary>Returns PCM format metadata, rejecting malformed and unsupported input.</summary>
    public static (int SampleRate, int Channels, long Frames) ReadFormat(string path)
    {
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input, Encoding.ASCII, leaveOpen: true);
        if (input.Length < 12 || new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("This is not a RIFF WAV file.");
        var riffSize = reader.ReadUInt32();
        var end = (long)riffSize + 8;
        if (end > input.Length || end < 12 || new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("The WAV header is incomplete.");
        int rate = 0, channels = 0, alignment = 0;
        long dataSize = -1;
        while (input.Position + 8 <= end)
        {
            var id = new string(reader.ReadChars(4));
            var length = reader.ReadUInt32();
            var next = checked(input.Position + length + (length & 1));
            if (next > end) throw new InvalidDataException("A WAV chunk is incomplete.");
            if (id == "fmt ")
            {
                if (length < 16) throw new InvalidDataException("The WAV format is incomplete.");
                var format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); alignment = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                if (format != 1 || bits != 16 || channels is < 1 or > 2 || rate is < 8000 or > 384000 || alignment != channels * 2)
                    throw new InvalidDataException("Only mono or stereo 16-bit PCM WAV files are currently supported.");
            }
            else if (id == "data") dataSize = length;
            input.Position = next;
        }
        if (rate == 0 || dataSize <= 0 || dataSize % alignment != 0) throw new InvalidDataException("The WAV has no valid audio data.");
        return (rate, channels, dataSize / alignment);
    }
}

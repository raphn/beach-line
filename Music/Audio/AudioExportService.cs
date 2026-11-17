#nullable enable

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTab.Music;
using OpenTab.Music.Playback;

namespace BeachLine.Music.Audio;

public enum AudioExportFormat { Wav, Flac, OggVorbis, Mp3 }

public sealed record AudioExportOptions
{
    public int? SampleRate { get; init; }
    public int BufferFrames { get; init; } = 8192;
    public double TailSeconds { get; init; } = 2.0;
    public string? FfmpegPath { get; init; }
    public double MaximumDurationSeconds { get; init; } = 12 * 60 * 60;
}

public sealed record AudioExportResult(string Path, AudioExportFormat Format, int SampleRate, long Frames, long TimelineFrames);

/// <summary>Offline mixdown using the same score clock, synth and audio clip mixer as playback.</summary>
public static class AudioExportService
{
    private const int MaximumBufferFrames = 65536;

    /// <summary>
    /// Renders one finite pass. The caller supplies a service isolated from live playback;
    /// its SoundFont sample rate is used unless options request the same explicit rate.
    /// </summary>
    public static Task<AudioExportResult> ExportAsync(ScoreDocument score, SoundFontPlaybackService isolatedService,
        PlaybackLoopSelection? selection, PlaybackVersionSelection? versions, string targetPath,
        AudioExportFormat format = AudioExportFormat.Wav, AudioExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isolatedService);
        return ExportCoreAsync(score, isolatedService, selection, versions, targetPath, format, options, cancellationToken);
    }

    /// <summary>Loads a SoundFont on the worker and renders one finite pass. The caller retains ownership of the stream.</summary>
    public static Task<AudioExportResult> ExportAsync(ScoreDocument score, Stream soundFontStream,
        PlaybackLoopSelection? selection, PlaybackVersionSelection? versions, string targetPath,
        AudioExportFormat format = AudioExportFormat.Wav, AudioExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(soundFontStream);
        return ExportCoreAsync(score, soundFontStream, selection, versions, targetPath, format, options, cancellationToken);
    }

    private static async Task<AudioExportResult> ExportCoreAsync(ScoreDocument score, object synthSource,
        PlaybackLoopSelection? selection, PlaybackVersionSelection? versions, string targetPath,
        AudioExportFormat format, AudioExportOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(score);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        options ??= new AudioExportOptions();
        ValidateOptions(options);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioModelValidation.ValidateAudio(score);
            SoundFontPlaybackService service;
            if (synthSource is SoundFontPlaybackService supplied)
            {
                var rate = options.SampleRate ?? supplied.SampleRate;
                if (rate != supplied.SampleRate)
                    throw new ArgumentException("The requested sample rate must match the supplied SoundFont service.", nameof(options));
                service = supplied;
            }
            else
            {
                var stream = (Stream)synthSource;
                service = new SoundFontPlaybackService(stream, options.SampleRate ?? 48000);
            }

            var sampleRate = service.SampleRate;
            var fullPath = Path.GetFullPath(targetPath);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(fullPath)}");
            ScorePlaybackClock? clock = null;
            try
            {
                clock = new ScorePlaybackClock(score, sampleRate, selection: selection,
                    versionSelection: versions ?? PlaybackVersionSelection.Empty);
                var timelineFrames = clock.TimelineDurationFrames;
                if (timelineFrames <= 0) throw new InvalidOperationException("The selected score scope contains no playable time.");
                var tailFrames = checked((long)Math.Round(options.TailSeconds * sampleRate));
                var totalFrames = checked(timelineFrames + tailFrames);
                if (totalFrames > options.MaximumDurationSeconds * sampleRate)
                    throw new InvalidOperationException("The selected export exceeds the configured maximum duration.");
                if (format == AudioExportFormat.Wav && totalFrames * 4L > uint.MaxValue - 36L)
                    throw new InvalidOperationException("PCM WAV exports are limited to 4 GiB; choose a compressed format or shorter scope.");

                if (format == AudioExportFormat.Wav)
                    await RenderWavAsync(clock, service, temporary, totalFrames, timelineFrames, sampleRate, options.BufferFrames, cancellationToken).ConfigureAwait(false);
                else
                    await RenderEncodedAsync(clock, service, temporary, totalFrames, timelineFrames, sampleRate, format, options, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, fullPath, true);
                return new AudioExportResult(fullPath, format, sampleRate, totalFrames, timelineFrames);
            }
            finally
            {
                try { clock?.Stop(service); } catch { }
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RenderWavAsync(ScorePlaybackClock clock, SoundFontPlaybackService service,
        string path, long totalFrames, long timelineFrames, int rate, int bufferFrames, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        WriteWavHeader(output, rate, totalFrames);
        var left = new float[bufferFrames]; var right = new float[bufferFrames];
        var bytes = new byte[bufferFrames * 4];
        long position = 0;
        var stopped = false;
        try
        {
            while (position < totalFrames)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(bufferFrames, totalFrames - position);
                Array.Clear(left); Array.Clear(right);
                var rendered = 0;
                if (position < timelineFrames)
                {
                    rendered = (int)Math.Min(count, timelineFrames - position);
                    await clock.PrepareAudioAsync(rendered).WaitAsync(token).ConfigureAwait(false);
                    clock.Render(service, left.AsSpan(0, rendered), right.AsSpan(0, rendered));
                    if (clock.AudioUnderrunFrames != 0)
                        throw new InvalidDataException($"Audio clips could not be fully prepared ({clock.AudioUnderrunFrames} frames underrun).");
                    if (position + rendered == timelineFrames) { clock.Stop(service); stopped = true; }
                }
                if (rendered < count && stopped)
                    service.Render(left.AsSpan(rendered, count - rendered), right.AsSpan(rendered, count - rendered));
                EncodePcm16(left, right, count, bytes);
                await output.WriteAsync(bytes.AsMemory(0, count * 4), token).ConfigureAwait(false);
                position += count;
            }
        }
        finally { if (!stopped) clock.Stop(service); }
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task RenderEncodedAsync(ScorePlaybackClock clock, SoundFontPlaybackService service,
        string path, long totalFrames, long timelineFrames, int rate, AudioExportFormat format,
        AudioExportOptions options, CancellationToken token)
    {
        var ffmpeg = ResolveFfmpeg(options.FfmpegPath);
        var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardError = true, RedirectStandardOutput = false, CreateNoWindow = true };
        foreach (var argument in EncoderArguments(path, format, rate)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start FFmpeg encoder '{ffmpeg}'. Install FFmpeg or choose WAV export.", exception);
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            var left = new float[options.BufferFrames]; var right = new float[options.BufferFrames];
            var bytes = new byte[options.BufferFrames * 8];
            long position = 0;
            var stopped = false;
            while (position < totalFrames)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(options.BufferFrames, totalFrames - position);
                Array.Clear(left); Array.Clear(right);
                var rendered = 0;
                if (position < timelineFrames)
                {
                    rendered = (int)Math.Min(count, timelineFrames - position);
                    await clock.PrepareAudioAsync(rendered).WaitAsync(token).ConfigureAwait(false);
                    clock.Render(service, left.AsSpan(0, rendered), right.AsSpan(0, rendered));
                    if (clock.AudioUnderrunFrames != 0)
                        throw new InvalidDataException($"Audio clips could not be fully prepared ({clock.AudioUnderrunFrames} frames underrun).");
                    if (position + rendered == timelineFrames) { clock.Stop(service); stopped = true; }
                }
                if (rendered < count && stopped)
                    service.Render(left.AsSpan(rendered, count - rendered), right.AsSpan(rendered, count - rendered));
                EncodeFloat32(left, right, count, bytes);
                await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, count * 8), token).ConfigureAwait(false);
                position += count;
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"FFmpeg encoding failed (exit {process.ExitCode}): {stderr.Trim()}");
            if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidDataException("FFmpeg produced an empty audio file.");
            stopped = true;
        }
        catch
        {
            try { clock.Stop(service); } catch { }
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await stderrTask.ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private static string[] EncoderArguments(string path, AudioExportFormat format, int sampleRate)
    {
        var result = new System.Collections.Generic.List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "f32le", "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", "pipe:0", "-vn" };
        switch (format)
        {
            case AudioExportFormat.Flac: result.AddRange(["-c:a", "flac", "-sample_fmt", "s16"]); break;
            case AudioExportFormat.OggVorbis: result.AddRange(["-c:a", "libvorbis", "-q:a", "5"]); break;
            case AudioExportFormat.Mp3: result.AddRange(["-c:a", "libmp3lame", "-b:a", "192k"]); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
        result.Add(path);
        return result.ToArray();
    }

    private static string ResolveFfmpeg(string? requested)
    {
        var candidate = string.IsNullOrWhiteSpace(requested) ? Environment.GetEnvironmentVariable("BEACHLINE_FFMPEG") : requested;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            if (!string.IsNullOrWhiteSpace(requested)) throw new FileNotFoundException($"The configured FFmpeg executable was not found: {requested}");
        }
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe", "ffmpeg" } : new[] { "ffmpeg" };
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var path = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(path)) return path;
            }
        throw new FileNotFoundException("FFmpeg was not found. Install FFmpeg, set BEACHLINE_FFMPEG, or choose WAV export.");
    }

    private static void ValidateOptions(AudioExportOptions options)
    {
        if (options.SampleRate is { } rate && rate is < 16000 or > 192000) throw new ArgumentOutOfRangeException(nameof(options.SampleRate));
        if (options.BufferFrames is < 256 or > MaximumBufferFrames) throw new ArgumentOutOfRangeException(nameof(options.BufferFrames));
        if (!double.IsFinite(options.TailSeconds) || options.TailSeconds is < 0 or > 30) throw new ArgumentOutOfRangeException(nameof(options.TailSeconds));
        if (!double.IsFinite(options.MaximumDurationSeconds) || options.MaximumDurationSeconds is <= 0 or > 48 * 60 * 60) throw new ArgumentOutOfRangeException(nameof(options.MaximumDurationSeconds));
    }

    private static void WriteWavHeader(Stream output, int rate, long frames)
    {
        var dataBytes = checked((uint)(frames * 4));
        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36u + dataBytes); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
        writer.Write((uint)rate); writer.Write((uint)(rate * 4)); writer.Write((ushort)4); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(dataBytes);
    }

    private static void EncodePcm16(float[] left, float[] right, int frames, byte[] output)
    {
        for (var i = 0; i < frames; i++)
        {
            var l = (short)Math.Round(Math.Clamp(left[i], -1f, 1f) * (left[i] < 0 ? 32768 : 32767));
            var r = (short)Math.Round(Math.Clamp(right[i], -1f, 1f) * (right[i] < 0 ? 32768 : 32767));
            var offset = i * 4; output[offset] = (byte)l; output[offset + 1] = (byte)(l >> 8); output[offset + 2] = (byte)r; output[offset + 3] = (byte)(r >> 8);
        }
    }

    private static void EncodeFloat32(float[] left, float[] right, int frames, byte[] output)
    {
        for (var i = 0; i < frames; i++)
        {
            var offset = i * 8;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(left[i]));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset + 4, 4), BitConverter.SingleToInt32Bits(right[i]));
        }
    }
}

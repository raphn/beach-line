#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;

/// <summary>Small waveform preview that reads a bounded peak envelope off the UI thread.</summary>
public partial class AudioWaveformPreview : Control
{
    private const int MaximumBuckets = 1200;
    private const int BatchSize = 48;
    private readonly ConcurrentQueue<PreviewUpdate> _updates = new();
    private readonly List<float> _peaks = new(MaximumBuckets);
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    private bool _finished;
    private string _message = "Loading waveform…";
    private float _peak = 1f;
    private float? _playbackProgress;
    private bool _recordingPreview;

    /// <summary>Horizontal padding used for waveform and playback-cursor drawing.</summary>
    public float WaveformHorizontalInset { get; set; } = 6f;

    private sealed record PreviewUpdate(int Generation, float[] Values, bool Complete, string? Error);

    public AudioWaveformPreview()
    {
        CustomMinimumSize = new Vector2(0, 58);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
        SetProcess(true);
    }

    public void LoadClip(string path, long sourceOffsetFrames, long durationFrames, int sampleRate)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        var generation = ++_generation;
        _peaks.Clear();
        _peak = 1f;
        _finished = false;
        var offsetSeconds = sampleRate > 0 ? sourceOffsetFrames / (double)sampleRate : 0;
        var durationSeconds = sampleRate > 0 ? durationFrames / (double)sampleRate : 0;
        _message = $"Source {offsetSeconds:0.###}s ({sourceOffsetFrames:N0} frames) · {durationSeconds:0.###}s ({durationFrames:N0} frames) · loading waveform…";
        QueueRedraw();

        _ = Task.Run(() => ReadWaveform(path, sourceOffsetFrames, durationFrames, token, generation), token);
    }

    public void SetUnavailable(string message)
    {
        _loadCancellation?.Cancel();
        ++_generation;
        _peaks.Clear();
        _finished = true;
        _message = message;
        QueueRedraw();
    }

    /// <summary>Updates the playback marker without restarting waveform loading.</summary>
    public void SetPlaybackProgress(double? progress)
    {
        var value = progress.HasValue && double.IsFinite(progress.Value)
            ? (float)Math.Clamp(progress.Value, 0.0, 1.0) : (float?)null;
        if (_playbackProgress == value) return;
        _playbackProgress = value;
        QueueRedraw();
    }

    /// <summary>Shows a lightweight live-take lane while the shared score transport records.</summary>
    public void SetRecordingPreview()
    {
        _loadCancellation?.Cancel();
        ++_generation;
        _peaks.Clear();
        _finished = true;
        _recordingPreview = true;
        _message = "Recording · follows the active playback transport";
        QueueRedraw();
    }

    public override void _ExitTree()
    {
        ++_generation;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        while (_updates.TryDequeue(out _)) { }
    }

    public override void _Process(double delta)
    {
        var changed = false;
        while (_updates.TryDequeue(out var update))
        {
            if (update.Generation != _generation) continue;
            foreach (var value in update.Values)
            {
                _peaks.Add(value);
                _peak = Math.Max(_peak, value);
            }
            if (update.Error is not null) _message = update.Error;
            else if (update.Complete) _message = _peaks.Count == 0 ? "No waveform data." : _message.Replace(" · loading waveform…", "");
            if (update.Complete) _finished = true;
            changed = true;
        }
        if (changed) QueueRedraw();
    }

    public override void _Draw()
    {
        var bounds = new Rect2(Vector2.Zero, Size);
        DrawRect(bounds, new Color(0.055f, 0.07f, 0.09f, 0.92f));
        var center = Size.Y * 0.60f;
        DrawLine(new Vector2(0, center), new Vector2(Size.X, center), new Color(0.24f, 0.29f, 0.33f), 1f);
        if (_peaks.Count > 0)
        {
            var inset = Math.Clamp(WaveformHorizontalInset, 0f, Size.X * 0.5f);
            var usableWidth = Math.Max(1f, Size.X - inset * 2);
            var step = usableWidth / _peaks.Count;
            for (var i = 0; i < _peaks.Count; i++)
            {
                var amplitude = _peaks[i] / _peak;
                var height = Math.Max(1.2f, amplitude * Math.Max(4f, Size.Y * 0.37f));
                var x = inset + i * step;
                DrawLine(new Vector2(x, center - height), new Vector2(x, center + height), new Color(0.34f, 0.82f, 0.72f), Math.Max(1f, step * 0.72f));
            }
        }
        if (_playbackProgress is { } progress)
        {
            var inset = Math.Clamp(WaveformHorizontalInset, 0f, Size.X * 0.5f);
            var x = inset + Math.Clamp(progress, 0f, 1f) * Math.Max(1f, Size.X - inset * 2);
            DrawLine(new Vector2(x, 18), new Vector2(x, Size.Y - 3),
                _recordingPreview ? new Color(1f, 0.38f, 0.3f, 0.98f) : new Color(1f, 0.72f, 0.32f, 0.98f), 2.5f, true);
        }
        var font = ThemeDB.FallbackFont;
        DrawString(font, new Vector2(7, 13), _message, HorizontalAlignment.Left, -1, 10, new Color(0.82f, 0.87f, 0.89f));
        if (!_finished && _peaks.Count == 0 && !_message.Contains("loading waveform", StringComparison.Ordinal))
            DrawString(font, new Vector2(7, 31), "Preview unavailable", HorizontalAlignment.Left, -1, 10, new Color(0.95f, 0.55f, 0.45f));
    }

    private void ReadWaveform(string path, long offsetFrames, long durationFrames, CancellationToken token, int generation)
    {
        try
        {
            if (offsetFrames < 0 || durationFrames < 1) throw new InvalidDataException("The clip has an invalid source range.");
            using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            var (dataOffset, dataBytes, channels) = FindPcmData(reader, stream.Length);
            var totalFrames = dataBytes / (channels * 2L);
            if (offsetFrames >= totalFrames || durationFrames > totalFrames - offsetFrames)
                throw new InvalidDataException("The clip source range is outside the WAV file.");

            var bucketCount = (int)Math.Min(MaximumBuckets, durationFrames);
            var completed = new List<float>(BatchSize);
            stream.Position = checked(dataOffset + offsetFrames * channels * 2L);
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                token.ThrowIfCancellationRequested();
                var first = (long)bucket * durationFrames / bucketCount;
                var end = (long)(bucket + 1) * durationFrames / bucketCount;
                var max = 0f;
                for (var frame = first; frame < end; frame++)
                {
                    if ((frame & 4095) == 0) token.ThrowIfCancellationRequested();
                    for (var channel = 0; channel < channels; channel++)
                    {
                        var sample = reader.ReadInt16();
                        max = Math.Max(max, Math.Abs(sample / 32768f));
                    }
                }
                completed.Add(max);
                if (completed.Count >= BatchSize || bucket == bucketCount - 1)
                {
                    _updates.Enqueue(new PreviewUpdate(generation, completed.ToArray(), false, null));
                    completed.Clear();
                }
            }
            _updates.Enqueue(new PreviewUpdate(generation, Array.Empty<float>(), true, null));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        {
            _updates.Enqueue(new PreviewUpdate(generation, Array.Empty<float>(), true, $"Waveform unavailable: {error.Message}"));
        }
    }

    private static (long DataOffset, long DataBytes, int Channels) FindPcmData(BinaryReader reader, long length)
    {
        var stream = reader.BaseStream;
        if (length < 12 || new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF WAV file.");
        var riffEnd = checked((long)reader.ReadUInt32() + 8);
        if (riffEnd > length || riffEnd < 12 || new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Incomplete WAV header.");
        var channels = 0;
        var alignment = 0;
        long dataOffset = -1, dataBytes = 0;
        while (stream.Position + 8 <= riffEnd)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            var start = stream.Position;
            var next = checked(start + size + (size & 1));
            if (next > riffEnd) throw new InvalidDataException("Incomplete WAV chunk.");
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Incomplete WAV format chunk.");
                var format = reader.ReadUInt16(); channels = reader.ReadUInt16();
                reader.ReadUInt32(); reader.ReadUInt32(); alignment = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                if (format != 1 || bits != 16 || channels is < 1 or > 2 || alignment != channels * 2)
                    throw new InvalidDataException("Only mono or stereo 16-bit PCM WAV is supported.");
            }
            else if (id == "data") { dataOffset = start; dataBytes = size; }
            stream.Position = next;
        }
        if (channels == 0 || dataOffset < 0 || dataBytes < alignment || dataBytes % alignment != 0)
            throw new InvalidDataException("WAV has no valid PCM data.");
        return (dataOffset, dataBytes, channels);
    }
}

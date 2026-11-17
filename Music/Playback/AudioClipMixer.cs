#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTab.Music;

namespace OpenTab.Music.Playback;

/// <summary>
/// Streams PCM16 WAV pages on a worker thread and mixes scheduled audio clips into
/// stereo float buffers. The render method never opens or reads a file.
/// </summary>
public sealed class AudioClipMixer : IDisposable
{
    private readonly record struct PageKey(Guid AssetId, long PageIndex);
    private sealed class Page
    {
        public required float[] Samples { get; init; }
        public required int Frames { get; init; }
        public long LastUse;
    }

    private readonly Dictionary<Guid, AudioAsset> _assets;
    private readonly Dictionary<Guid, (bool Muted, double Gain)> _trackGains = new();
    private readonly object _cacheLock = new();
    private readonly ConcurrentDictionary<PageKey, Page> _pages = new();
    private readonly HashSet<PageKey> _queued = new();
    private readonly HashSet<PageKey> _failed = new();
    private readonly Queue<PageKey> _requests = new();
    private readonly AutoResetEvent _requestSignal = new(false);
    private readonly Thread _worker;
    private readonly int _timelineSampleRate;
    private readonly int _pageFrames;
    private readonly int _maxPages;
    private readonly int _lookAheadFrames;
    private ScheduledAudioClip[] _clips = Array.Empty<ScheduledAudioClip>();
    private long _useCounter;
    private long _underrunFrames;
    private bool _disposed;

    /// <summary>Number of timeline frames for which an active clip had missing source data.</summary>
    public long UnderrunFrames => Interlocked.Read(ref _underrunFrames);

    public AudioClipMixer(IReadOnlyCollection<AudioAsset> assets, IReadOnlyCollection<ScoreTrack> tracks,
        int timelineSampleRate, int pageFrames = 16384, int maxPages = 8)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(tracks);
        if (timelineSampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(timelineSampleRate));
        if (pageFrames < 256) throw new ArgumentOutOfRangeException(nameof(pageFrames));
        if (maxPages < 2) throw new ArgumentOutOfRangeException(nameof(maxPages));
        _timelineSampleRate = timelineSampleRate;
        _pageFrames = pageFrames;
        _assets = assets.ToDictionary(asset => asset.Id);
        // Reserve at least one resident page per possible simultaneous source, while
        // keeping total decoded memory bounded even for scores with many imported files.
        _maxPages = Math.Min(64, Math.Max(maxPages, _assets.Count));
        var highestSourceRate = _assets.Count == 0 ? timelineSampleRate : _assets.Values.Max(asset => asset.SampleRate);
        var cacheHorizon = (long)pageFrames * (maxPages - 1) * timelineSampleRate / highestSourceRate;
        _lookAheadFrames = (int)Math.Max(0, Math.Min(timelineSampleRate / 4L, cacheHorizon));

        // Solo is score-wide: a soloed MIDI track silences audio tracks as well.
        var hasSolo = tracks.Any(track => track.IsSoloed);
        foreach (var track in tracks.Where(track => track.Kind == TrackKind.Audio))
        {
            var muted = track.IsMuted || (hasSolo && !track.IsSoloed);
            var gain = muted ? 0d : Math.Pow(10d, Math.Clamp(track.VolumeDb, -120d, 24d) / 20d);
            _trackGains[track.Id] = (muted, gain);
        }

        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "BeachLine audio page reader" };
        _worker.Start();
    }

    /// <summary>Replaces the current immutable playback-pass schedule.</summary>
    public void Configure(IReadOnlyList<ScheduledAudioClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ThrowIfDisposed();
        _clips = clips.Where(clip => clip.EndFrame > clip.StartFrame &&
                _assets.ContainsKey(clip.AssetId) && _trackGains.TryGetValue(clip.TrackId, out var gain) && !gain.Muted)
            .OrderBy(clip => clip.StartFrame).ToArray();
    }

    /// <summary>
    /// Marks a new playback position and queues pages needed there. Call before playback
    /// resumes; use <see cref="PrepareAsync"/> to wait until its initial audio is resident.
    /// </summary>
    public void Seek(long timelineFrame)
    {
        if (timelineFrame < 0) throw new ArgumentOutOfRangeException(nameof(timelineFrame));
        ThrowIfDisposed();
        QueuePagesForRange(timelineFrame, 1);
    }

    /// <summary>Loads all pages needed for a short upcoming range without blocking the render thread.</summary>
    public async Task PrepareAsync(long startFrame, int frameCount, CancellationToken cancellationToken = default)
    {
        if (startFrame < 0) throw new ArgumentOutOfRangeException(nameof(startFrame));
        if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount));
        ThrowIfDisposed();
        var keys = GetPageKeys(startFrame, frameCount);
        if (keys.Count > _maxPages)
            throw new ArgumentOutOfRangeException(nameof(frameCount), "Prebuffer range requires more pages than the configured cache can hold.");
        foreach (var key in keys) QueuePage(key);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            var complete = true;
            lock (_cacheLock)
            {
                foreach (var key in keys)
                {
                    if (_failed.Contains(key))
                        throw new InvalidDataException($"Audio cache page could not be read for asset {_assets[key.AssetId].FileName}.");
                    if (!_pages.ContainsKey(key) && !_failed.Contains(key)) { complete = false; break; }
                }
            }
            if (complete) return;
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds one stereo mix segment to the supplied buffers.</summary>
    public void Mix(long timelineStartFrame, Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Stereo buffers must have equal lengths.");
        if (timelineStartFrame < 0) throw new ArgumentOutOfRangeException(nameof(timelineStartFrame));
        ThrowIfDisposed();
        var timelineEnd = checked(timelineStartFrame + left.Length);
        var lookAhead = Math.Max(left.Length, _lookAheadFrames);
        PrefetchRange(timelineStartFrame, checked(left.Length + lookAhead));

        foreach (var clip in _clips)
        {
            if (clip.StartFrame >= timelineEnd) break;
            if (clip.EndFrame <= timelineStartFrame || !_assets.TryGetValue(clip.AssetId, out var asset) ||
                !_trackGains.TryGetValue(clip.TrackId, out var track) || track.Muted) continue;

            var first = (int)Math.Max(0L, clip.StartFrame - timelineStartFrame);
            var last = (int)Math.Min(left.Length, clip.EndFrame - timelineStartFrame);
            var missedFrames = 0;
            for (var outputFrame = first; outputFrame < last; outputFrame++)
            {
                var timelineFrame = timelineStartFrame + outputFrame;
                var sourceFrame = clip.SourceOffsetFrames + (timelineFrame - clip.StartFrame) *
                    (double)asset.SampleRate / _timelineSampleRate;
                var sourceIndex = (long)Math.Floor(sourceFrame);
                var fraction = (float)(sourceFrame - sourceIndex);
                if (sourceIndex < 0 || sourceIndex >= asset.FrameCount ||
                    !TryReadSourceFrame(asset, sourceIndex, out var l0, out var r0) ||
                    !TryReadSourceFrame(asset, Math.Min(sourceIndex + 1, asset.FrameCount - 1), out var l1, out var r1))
                {
                    missedFrames++;
                    continue;
                }
                left[outputFrame] += (l0 + (l1 - l0) * fraction) * (float)track.Gain;
                right[outputFrame] += (r0 + (r1 - r0) * fraction) * (float)track.Gain;
            }
            if (missedFrames > 0) Interlocked.Add(ref _underrunFrames, missedFrames);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requestSignal.Set();
        if (Thread.CurrentThread != _worker) _worker.Join();
        _requestSignal.Dispose();
        lock (_cacheLock)
        {
            _pages.Clear();
            _queued.Clear();
            _failed.Clear();
            _requests.Clear();
        }
    }

    private void QueuePagesForRange(long timelineStartFrame, int frameCount)
    {
        if (frameCount <= 0) return;
        foreach (var key in GetPageKeys(timelineStartFrame, frameCount)) QueuePage(key);
    }

    // Called by Mix to request current and near-future pages, including clips that
    // begin shortly after this segment. It avoids a temporary collection and performs
    // no file access; only deduplicated queue operations are synchronized.
    private void PrefetchRange(long timelineStartFrame, int frameCount)
    {
        if (frameCount <= 0) return;
        var timelineEnd = checked(timelineStartFrame + frameCount);
        foreach (var clip in _clips)
        {
            var start = Math.Max(timelineStartFrame, clip.StartFrame);
            var end = Math.Min(timelineEnd, clip.EndFrame);
            if (end <= start || !_assets.TryGetValue(clip.AssetId, out var asset) ||
                !_trackGains.TryGetValue(clip.TrackId, out var track) || track.Muted) continue;
            var firstSource = clip.SourceOffsetFrames + (start - clip.StartFrame) * (double)asset.SampleRate / _timelineSampleRate;
            var lastSource = clip.SourceOffsetFrames + (end - clip.StartFrame) * (double)asset.SampleRate / _timelineSampleRate;
            var firstPage = Math.Max(0L, (long)Math.Floor(firstSource) / _pageFrames);
            var lastPage = Math.Max(firstPage, (long)Math.Floor(lastSource) / _pageFrames);
            var finalPage = (asset.FrameCount - 1) / _pageFrames;
            for (var page = firstPage; page <= lastPage && page <= finalPage; page++)
                QueuePage(new PageKey(asset.Id, page));
        }
    }

    private HashSet<PageKey> GetPageKeys(long timelineStartFrame, int frameCount)
    {
        var keys = new HashSet<PageKey>();
        var timelineEnd = checked(timelineStartFrame + frameCount);
        foreach (var clip in _clips)
        {
            var start = Math.Max(timelineStartFrame, clip.StartFrame);
            var end = Math.Min(timelineEnd, clip.EndFrame);
            if (end <= start || !_assets.TryGetValue(clip.AssetId, out var asset) ||
                !_trackGains.TryGetValue(clip.TrackId, out var track) || track.Muted) continue;
            var firstSource = clip.SourceOffsetFrames + (start - clip.StartFrame) * (double)asset.SampleRate / _timelineSampleRate;
            var lastSource = clip.SourceOffsetFrames + (end - clip.StartFrame) * (double)asset.SampleRate / _timelineSampleRate;
            var firstPage = Math.Max(0L, (long)Math.Floor(firstSource) / _pageFrames);
            var lastPage = Math.Max(firstPage, (long)Math.Floor(lastSource) / _pageFrames);
            // A linear interpolation at a page boundary needs the following page too.
            for (var page = firstPage; page <= lastPage && page <= (asset.FrameCount - 1) / _pageFrames; page++)
                keys.Add(new PageKey(asset.Id, page));
        }
        return keys;
    }

    private void QueuePage(PageKey key)
    {
        lock (_cacheLock)
        {
            if (_disposed || _pages.ContainsKey(key) || _queued.Contains(key) || _failed.Contains(key)) return;
            if (_requests.Count >= _maxPages * 4) return;
            _queued.Add(key);
            _requests.Enqueue(key);
        }
        _requestSignal.Set();
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            PageKey key;
            lock (_cacheLock)
            {
                if (_requests.Count == 0) key = default;
                else key = _requests.Dequeue();
            }
            if (key.AssetId == Guid.Empty)
            {
                _requestSignal.WaitOne(100);
                continue;
            }
            try
            {
                if (!_assets.TryGetValue(key.AssetId, out var asset)) continue;
                var page = ReadWavePage(asset, key.PageIndex);
                lock (_cacheLock)
                {
                    _queued.Remove(key);
                    if (_disposed) continue;
                    _pages[key] = page;
                    EvictPages(key);
                }
            }
            catch
            {
                lock (_cacheLock) { _queued.Remove(key); _failed.Add(key); }
            }
        }
    }

    private Page ReadWavePage(AudioAsset asset, long pageIndex)
    {
        using var stream = new FileStream(asset.CachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream);
        if (ReadFourCc(reader) != "RIFF") throw new InvalidDataException("Audio cache is not a RIFF WAV file.");
        _ = reader.ReadUInt32();
        if (ReadFourCc(reader) != "WAVE") throw new InvalidDataException("Audio cache is not a WAV file.");
        short format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        long dataOffset = -1, dataBytes = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = ReadFourCc(reader);
            var size = reader.ReadUInt32();
            var chunkStart = stream.Position;
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("WAV format chunk is truncated.");
                format = reader.ReadInt16(); channels = reader.ReadInt16(); sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32(); _ = reader.ReadInt16(); bits = reader.ReadInt16();
            }
            else if (id == "data") { dataOffset = chunkStart; dataBytes = size; }
            var next = checked(chunkStart + size + (size & 1));
            if (next > stream.Length) throw new InvalidDataException("WAV chunk exceeds file length.");
            stream.Position = next;
            if (dataOffset >= 0 && sampleRate > 0 && channels > 0) break;
        }
        if (format != 1 || bits != 16 || channels != asset.Channels || sampleRate != asset.SampleRate || dataOffset < 0)
            throw new InvalidDataException("Audio cache must be PCM16 WAV matching its asset metadata.");
        var bytesPerFrame = channels * 2;
        var expectedDataBytes = checked(asset.FrameCount * bytesPerFrame);
        if (dataBytes < expectedDataBytes) throw new InvalidDataException("WAV data is shorter than its asset metadata.");
        var startFrame = checked(pageIndex * _pageFrames);
        if (startFrame >= asset.FrameCount) throw new InvalidDataException("Requested audio page is outside the asset.");
        var frames = (int)Math.Min(_pageFrames, asset.FrameCount - startFrame);
        var samples = new float[checked(frames * channels)];
        stream.Position = checked(dataOffset + startFrame * bytesPerFrame);
        var raw = reader.ReadBytes(checked(frames * bytesPerFrame));
        if (raw.Length != frames * bytesPerFrame) throw new EndOfStreamException();
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(raw[i * 2] | raw[i * 2 + 1] << 8) / 32768f;
        return new Page { Samples = samples, Frames = frames };
    }

    private bool TryReadSourceFrame(AudioAsset asset, long sourceFrame, out float left, out float right)
    {
        var key = new PageKey(asset.Id, sourceFrame / _pageFrames);
        if (!_pages.TryGetValue(key, out var page))
        {
            left = right = 0;
            return false;
        }
        Interlocked.Exchange(ref page.LastUse, Interlocked.Increment(ref _useCounter));
        var offset = checked((int)(sourceFrame % _pageFrames) * asset.Channels);
        if (offset >= page.Samples.Length) { left = right = 0; return false; }
        left = page.Samples[offset];
        right = asset.Channels == 1 ? left : page.Samples[offset + 1];
        return true;
    }

    private void EvictPages(PageKey keep)
    {
        while (_pages.Count > _maxPages)
        {
            var oldest = _pages.Where(pair => pair.Key != keep)
                .OrderBy(pair => Interlocked.Read(ref pair.Value.LastUse)).FirstOrDefault();
            if (oldest.Value is null) break;
            _pages.TryRemove(oldest.Key, out _);
        }
    }

    private static string ReadFourCc(BinaryReader reader) => new(reader.ReadChars(4));
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(AudioClipMixer)); }
}

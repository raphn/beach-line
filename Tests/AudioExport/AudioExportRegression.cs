using BeachLine.Music.Audio;
using OpenTab.Music;
using OpenTab.Music.Playback;
using System.Diagnostics;

var appRoot = FindAppRoot(AppContext.BaseDirectory);
var soundFont = Path.Combine(appRoot, "Music", "SoundFonts", "FluidR3_GS.sf2");
if (!File.Exists(soundFont)) throw new FileNotFoundException("Could not locate the project SoundFont.", soundFont);
var testDirectory = Path.GetFullPath(Path.Combine(appRoot, "Tests", "AudioExport"));
var output = Path.GetFullPath(Path.Combine(testDirectory, $"export-{Guid.NewGuid():N}.wav"));
var scratch = Path.GetFullPath(Path.Combine(testDirectory, $"scratch-{Guid.NewGuid():N}"));
EnsureWithin(testDirectory, output);
EnsureWithin(testDirectory, scratch);
try
{
    var score = new ScoreDocument("Export test", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var track = score.AddTrack(new ScoreTrack("Lead", TrackKind.Guitar));
    var sectionA = score.Sections[0];
    var measureA = score.AddMeasure(sectionA);
    var note = new TabEvent(1, 0, 69, 110, ScoreTime.Zero, new ScoreTime(1, 2));
    measureA.GetTrackMeasure(track.Id)!.UsesTabEvents = true;
    measureA.GetTrackMeasure(track.Id)!.TabEvents.Add(note);
    var sectionB = score.AddSection("B", "Second", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var measureB = sectionB.Measures[0];
    measureB.GetTrackMeasure(track.Id)!.UsesTabEvents = true;
    measureB.GetTrackMeasure(track.Id)!.TabEvents.Add(new TabEvent(1, 0, 72, 110, ScoreTime.Zero, new ScoreTime(1, 2)));
    var arrangement = ArrangementEditing.Add(score);
    ArrangementEditing.Append(score, arrangement, sectionA.Id);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, [sectionA.Id], arrangement.Id);

    using var bank = File.OpenRead(soundFont);
    var result = await AudioExportService.ExportAsync(score, bank, selection, PlaybackVersionSelection.Empty,
        output, AudioExportFormat.Wav, new AudioExportOptions { SampleRate = 48000, TailSeconds = 0, BufferFrames = 4096 });
    if (!bank.CanRead) throw new Exception("The stream overload unexpectedly disposed the caller-owned SoundFont stream.");
    if (result.TimelineFrames != 48000 * 2) throw new Exception($"Unexpected selected arrangement duration: {result.TimelineFrames} frames.");
    var bytes = await File.ReadAllBytesAsync(output);
    if (bytes.Length != 44 + result.Frames * 4 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE" || BitConverter.ToInt32(bytes, 40) != bytes.Length - 44)
        throw new Exception("WAV header or data length was invalid.");
    if (!bytes.AsSpan(44).ToArray().Any(value => value != 0)) throw new Exception("Rendered WAV contained no nonzero samples.");
    Console.WriteLine("Audio export regression passed: selected arrangement rendered to non-silent PCM16 WAV.");

    // A pedal must use the same processing path for live blocks and offline mixdown.
    foreach (var effect in new[] { "overdrive", "chorus", "delay" })
    {
        track.Pedals.Add(new TrackPedal { TypeId = effect, Drive = .8f, Tone = .6f, Level = .5f });
        using (var wetBank = File.OpenRead(soundFont))
            await AudioExportService.ExportAsync(score, wetBank, selection, PlaybackVersionSelection.Empty,
                output, AudioExportFormat.Wav, new AudioExportOptions { SampleRate = 48000, TailSeconds = 0, BufferFrames = 4096 });
        var wetBytes = await File.ReadAllBytesAsync(output);
        if (wetBytes.AsSpan(44).SequenceEqual(bytes.AsSpan(44))) throw new Exception($"{effect} did not change exported audio.");
        using (var liveBank = File.OpenRead(soundFont))
        {
            var liveService = new SoundFontPlaybackService(liveBank, 48000);
            var liveClock = new ScorePlaybackClock(score, 48000, selection: selection);
            var liveLeft = new float[4096]; var liveRight = new float[4096];
            liveClock.Render(liveService, liveLeft, liveRight);
            for (var i = 0; i < liveLeft.Length; i++)
            {
                short Encode(float sample) => (short)Math.Round(Math.Clamp(sample, -1f, 1f) * (sample < 0 ? 32768 : 32767));
                if (BitConverter.ToInt16(wetBytes, 44 + i * 4) != Encode(liveLeft[i]) ||
                    BitConverter.ToInt16(wetBytes, 46 + i * 4) != Encode(liveRight[i]))
                    throw new Exception($"Live and exported {effect} differ at frame {i}.");
            }
        }
        track.Pedals.Clear();
    }
    Console.WriteLine("Pedal export regression passed: overdrive, chorus, and delay change WAV audio and match live rendering.");

    if (TryGetFfmpeg() is { } ffmpeg)
    {
        foreach (var (format, extension) in new[] { (AudioExportFormat.Flac, ".flac"), (AudioExportFormat.OggVorbis, ".ogg"), (AudioExportFormat.Mp3, ".mp3") })
        {
            var encodedPath = Path.ChangeExtension(output, extension);
            using var compressedBank = File.OpenRead(soundFont);
            await AudioExportService.ExportAsync(score, compressedBank, selection, PlaybackVersionSelection.Empty,
                encodedPath, format, new AudioExportOptions { SampleRate = 48000, TailSeconds = 0 });
            if (!File.Exists(encodedPath) || new FileInfo(encodedPath).Length < 128) throw new Exception($"{format} export was empty.");
            var decoder = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
                ArgumentList = { "-v", "error", "-i", encodedPath, "-f", "null", "-" }
            } };
            decoder.Start();
            var decoderError = await decoder.StandardError.ReadToEndAsync();
            await decoder.WaitForExitAsync();
            if (decoder.ExitCode != 0) throw new Exception($"{format} output did not decode: {decoderError}");
            File.Delete(encodedPath);
        }
        Console.WriteLine("FFmpeg regression passed: FLAC, Ogg Vorbis and MP3 encoders produced decodable output.");
    }
    else Console.WriteLine("FFmpeg regression skipped: executable not installed.");

    Directory.CreateDirectory(scratch);
    var sourceWav = Path.Combine(scratch, "clip.wav");
    WriteConstantWav(sourceWav, 48000, 4800, 9000);
    var audioScore = new ScoreDocument("Clip export", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var audioTrack = audioScore.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var audioSection = audioScore.Sections[0];
    var asset = WavAssetStore.Import(sourceWav, Path.Combine(scratch, "cache"));
    audioScore.AudioAssets.Add(asset);
    audioSection.AudioClips.Add(new AudioClip(asset.Id, audioTrack.Id, ScoreTime.Zero, 0, 4800));
    var clipResult = await AudioExportService.ExportAsync(audioScore, File.OpenRead(soundFont), null, PlaybackVersionSelection.Empty,
        output, AudioExportFormat.Wav, new AudioExportOptions { SampleRate = 48000, TailSeconds = 0, BufferFrames = 4096 });
    var clipBytes = await File.ReadAllBytesAsync(output);
    if (clipResult.TimelineFrames != 96000 || !clipBytes.AsSpan(44, 4800 * 4).ToArray().Any(value => value != 0))
        throw new Exception("The prepared imported audio clip did not appear in the exported mix.");
    Console.WriteLine("Audio clip regression passed: prepared imported WAV media appeared in offline mixdown.");

    var failurePath = Path.ChangeExtension(output, ".flac");
    try
    {
        using var failureBank = File.OpenRead(soundFont);
        await AudioExportService.ExportAsync(score, failureBank, selection, PlaybackVersionSelection.Empty,
            failurePath, AudioExportFormat.Flac, new AudioExportOptions { FfmpegPath = Path.Combine(scratch, "missing-ffmpeg.exe"), TailSeconds = 0 });
        throw new Exception("Missing explicitly configured FFmpeg unexpectedly succeeded.");
    }
    catch (FileNotFoundException) { }
    if (File.Exists(failurePath) || Directory.GetFiles(Path.GetDirectoryName(failurePath)!, $".{Path.GetFileNameWithoutExtension(failurePath)}.*.tmp.flac").Length != 0)
        throw new Exception("Failed export left a partial destination or temporary file.");
    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        try
        {
            using var canceledBank = File.OpenRead(soundFont);
            await AudioExportService.ExportAsync(score, canceledBank, selection, PlaybackVersionSelection.Empty,
                failurePath, AudioExportFormat.Wav, cancellationToken: canceled.Token);
            throw new Exception("Canceled export unexpectedly succeeded.");
        }
        catch (OperationCanceledException) { }
    }
    if (File.Exists(failurePath)) throw new Exception("Canceled export left a destination file.");
    Console.WriteLine("Failure/cancellation regression passed: no partial destinations were left behind.");
}
finally
{
    if (File.Exists(output)) File.Delete(output);
    if (Directory.Exists(scratch))
    {
        EnsureWithin(testDirectory, scratch);
        Directory.Delete(scratch, recursive: true);
    }
}

static void WriteConstantWav(string path, int rate, int frames, short value)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8); writer.Write((uint)(36 + frames * 2)); writer.Write("WAVE"u8); writer.Write("fmt "u8);
    writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((uint)rate); writer.Write((uint)(rate * 2));
    writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write((uint)(frames * 2));
    for (var i = 0; i < frames; i++) writer.Write(value);
}

static string FindAppRoot(string startDirectory)
{
    for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "project.godot"))) return directory.FullName;
        var nestedAppRoot = Path.Combine(directory.FullName, "App");
        if (File.Exists(Path.Combine(nestedAppRoot, "project.godot"))) return nestedAppRoot;
    }

    throw new DirectoryNotFoundException("Could not locate the Godot project root.");
}

static string? TryGetFfmpeg()
{
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var executable = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        if (File.Exists(executable)) return executable;
    }
    return null;
}

static void EnsureWithin(string parent, string candidate)
{
    var relative = Path.GetRelativePath(Path.GetFullPath(parent), Path.GetFullPath(candidate));
    if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        throw new InvalidOperationException($"Test output path is outside its intended directory: {candidate}");
}

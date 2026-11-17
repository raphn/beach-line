using OpenTab.Music;
using OpenTab.Music.Playback;

const int sampleRate = 48000;
const int frameCount = 700;
const int pageFrames = 256;
var tempPath = Path.Combine(Path.GetTempPath(), $"beachline-mixer-{Guid.NewGuid():N}.wav");
try
{
    WriteRampWave(tempPath, sampleRate, frameCount);
    var asset = new AudioAsset(Guid.NewGuid(), "ramp.wav", new string('a', 64), sampleRate, 1, frameCount, tempPath);
    var track = new ScoreTrack("Audio", TrackKind.Audio);
    using var mixer = new AudioClipMixer(new[] { asset }, new[] { track }, sampleRate,
        pageFrames: pageFrames, maxPages: 4);
    mixer.Configure(new[] { new ScheduledAudioClip(Guid.NewGuid(), asset.Id, track.Id, 0, frameCount, 0) });

    // This range spans three physical pages. Preparation must not return until each
    // page is resident, and page seams must preserve the source ramp without gaps.
    await mixer.PrepareAsync(0, frameCount);
    var left = new float[frameCount];
    var right = new float[frameCount];
    mixer.Mix(0, left, right);
    for (var i = 0; i < frameCount; i++)
    {
        var expected = (short)(i * 20) / 32768f;
        if (Math.Abs(left[i] - expected) > 0.00001f || Math.Abs(right[i] - expected) > 0.00001f)
            throw new InvalidOperationException($"Unexpected mixed sample at frame {i}: {left[i]} / {right[i]}, expected {expected}.");
    }
    if (mixer.UnderrunFrames != 0) throw new InvalidOperationException($"Mixer reported {mixer.UnderrunFrames} underrun frames.");

    // Verify the clock owns the mixer correctly and combines its output with the
    // synthesizer's render path. An audio-only score should reproduce the cached
    // source exactly after the clock's startup prebuffer has completed.
    var score = new ScoreDocument("Clock audio integration", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var audioTrack = score.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var section = score.Sections[0];
    score.AddMeasure(section);
    var clockAsset = new AudioAsset(Guid.NewGuid(), "clock-ramp.wav", new string('b', 64), sampleRate, 1, frameCount, tempPath);
    score.AudioAssets.Add(clockAsset);
    section.AudioClips.Add(new AudioClip(clockAsset.Id, audioTrack.Id, ScoreTime.Zero, 0, frameCount));
    var clock = new ScorePlaybackClock(score, sampleRate);
    await clock.PrepareAudioAsync(frameCount);
    using var soundFont = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var synth = new SoundFontPlaybackService(soundFont, sampleRate);
    var clockLeft = new float[frameCount];
    var clockRight = new float[frameCount];
    clock.Render(synth, clockLeft, clockRight);
    for (var i = 0; i < frameCount; i++)
    {
        var expected = (short)(i * 20) / 32768f;
        if (Math.Abs(clockLeft[i] - expected) > 0.00001f || Math.Abs(clockRight[i] - expected) > 0.00001f)
            throw new InvalidOperationException($"ScorePlaybackClock did not mix source audio at frame {i}: {clockLeft[i]} / {clockRight[i]}, expected {expected}.");
    }
    if (clock.AudioUnderrunFrames != 0) throw new InvalidOperationException($"Clock audio path reported {clock.AudioUnderrunFrames} underrun frames.");
    clock.Stop(synth);
    Console.WriteLine("AudioClipMixer and ScorePlaybackClock integration regressions passed.");
}
finally
{
    if (File.Exists(tempPath)) File.Delete(tempPath);
}

static void WriteRampWave(string path, int sampleRate, int frames)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write("RIFF"u8);
    writer.Write(checked((uint)(36 + frames * 2)));
    writer.Write("WAVE"u8);
    writer.Write("fmt "u8);
    writer.Write(16u);
    writer.Write((ushort)1);
    writer.Write((ushort)1);
    writer.Write(sampleRate);
    writer.Write(sampleRate * 2);
    writer.Write((ushort)2);
    writer.Write((ushort)16);
    writer.Write("data"u8);
    writer.Write(checked((uint)(frames * 2)));
    for (var i = 0; i < frames; i++) writer.Write((short)(i * 20));
}

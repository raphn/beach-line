using OpenTab.Music;
using OpenTab.Music.Playback;

internal static class ChorusDelayRegression
{
    private static AudioAutomationCurve Constant(float value) => new(value, []);

    public static void Run()
    {
        ReadDelayWrapRounding();

        // 1 kHz gives an exact 40-frame minimum delay for an impulse.
        var effect = new DelayTrackEffect(1000, Constant(0), Constant(.5f), Constant(1));
        var left = new float[140]; var right = new float[140];
        left[0] = 1; right[0] = .5f;
        effect.Process(left.AsSpan(0, 60), right.AsSpan(0, 60), 0);
        effect.Process(left.AsSpan(60), right.AsSpan(60), 60, holdAutomation: true);
        Check(Math.Abs(left[40] - 1) < .0001f && Math.Abs(left[80] - .425f) < .0001f,
            "Delay must preserve the first echo and feedback across render blocks and the export tail.");
        Check(Math.Abs(right[40] - .5f) < .0001f && Math.Abs(right[80] - .2125f) < .0001f,
            "Delay must preserve independent stereo channels.");

        var clean = new[] { 1f, -.5f, .2f, 0f };
        var dryL = (float[])clean.Clone(); var dryR = (float[])clean.Clone();
        new DelayTrackEffect(1000, Constant(0), Constant(.5f), Constant(0)).Process(dryL, dryR, 0);
        Check(dryL.SequenceEqual(clean) && dryR.SequenceEqual(clean), "Zero delay mix must leave the input unchanged.");

        var chorus = new ChorusTrackEffect(1000, Constant(.35f), Constant(0), Constant(1));
        var chorusL = new float[40]; var chorusR = new float[40];
        chorusL[0] = chorusR[0] = 1;
        chorus.Process(chorusL, chorusR, 0);
        Check(Math.Abs(chorusL[12] - 1) < .001f && Math.Abs(chorusR[12] - 1) < .001f,
            "Chorus must produce its delayed signal with independently owned state.");
        var fresh = new ChorusTrackEffect(1000, Constant(.35f), Constant(.5f), Constant(1));
        var silenceL = new float[40]; var silenceR = new float[40];
        fresh.Process(silenceL, silenceR, 0);
        Check(silenceL.All(value => value == 0) && silenceR.All(value => value == 0), "A new chorus instance must start without another track's history.");

        var score = new ScoreDocument("Pedal timing", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar, 27));
        score.AddMeasure(score.Sections[0]);
        track.Pedals.Add(new TrackPedal { TypeId = "delay" });
        var clock = new ScorePlaybackClock(score, 48000);
        Check(clock.HasTimeBasedEffects, "Enabled delay should request a playback tail.");
        track.Pedals[0].Enabled = false;
        clock.RefreshScore(score);
        Check(!clock.HasTimeBasedEffects, "A bypassed delay should not request a playback tail after refresh.");
        Console.WriteLine("PASS chorus and delay impulses, state isolation, bypass mix and tail detection");
    }

    private static void ReadDelayWrapRounding()
    {
        // A 48 kHz chorus buffer: subtracting one float ULP from the write
        // position and then wrapping in float rounds up to buffer.Length.
        const int length = 1204;
        var buffer = new float[length];
        buffer[^1] = 3f;
        buffer[0] = 7f;
        var oldPosition = 576f - MathF.BitIncrement(576f);
        oldPosition += length;
        Check(oldPosition == length, "The regression inputs must reproduce the original rounded-out-of-range index.");
        var reader = typeof(ChorusTrackEffect).GetMethod("ReadDelay", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<float[], int, float, float>>();
        var value = reader(buffer, 576, MathF.BitIncrement(576f));
        Check(float.IsFinite(value) && Math.Abs(value - 7f) < .001f,
            "Fractional delay wrapping near the buffer boundary must stay in range despite float integer precision loss.");
        foreach (var sampleRate in new[] { 44100, 48000, 96000 })
        {
            var chorus = new ChorusTrackEffect(sampleRate, Constant(.35f), Constant(1f), Constant(.5f));
            var delay = new DelayTrackEffect(sampleRate, Constant(.53f), Constant(.8f), Constant(.5f));
            var left = new float[257]; var right = new float[257];
            for (var frame = 0; frame < sampleRate * 4; frame += left.Length)
            {
                Array.Clear(left); Array.Clear(right);
                chorus.Process(left, right, frame);
                delay.Process(left, right, frame);
            }
        }
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
    }
}

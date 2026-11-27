using System.Reflection;
using OpenTab.Music;
using OpenTab.Music.Playback;

internal static class PedalEffectsRegression
{
    private sealed class Capture : ITrackAudioEffect
    {
        public readonly List<long> Starts = new();
        public void Process(Span<float> left, Span<float> right, long sourceFrame, bool holdAutomation = false) => Starts.Add(sourceFrame);
    }

    public static void Run()
    {
        var samples = new[] { 0f, .1f, -.2f, .5f };
        var left = (float[])samples.Clone(); var right = (float[])samples.Clone();
        new TrackEffectsChain(Array.Empty<ITrackAudioEffect>()).Process(left, right, 0);
        Check(left.SequenceEqual(samples) && right.SequenceEqual(samples), "Empty/bypassed chain must preserve samples exactly.");
        var curve = new AudioAutomationCurve(.5f, [new(100, .1f, false), new(200, .9f, true), new(300, .2f, false)]);
        Check(Math.Abs(curve.ValueAt(150) - .5f) < .00001 && Math.Abs(curve.ValueAt(250) - .9f) < .00001 && Math.Abs(curve.ValueAt(100) - .1f) < .00001, "Automation interpolation or reverse seek failed.");
        var pedal = MakeEffect();
        pedal.Process(left, right, 0);
        Check(!left.SequenceEqual(samples) && left.SequenceEqual(right) && left.All(float.IsFinite), "Overdrive should change the signal and preserve identical stereo input.");
        PerTrackIsolation();
        ArrangementPositions();
        CompiledTempoAndStep();
    }

    private static OverdriveTrackEffect MakeEffect() => new(48000,
        new AudioAutomationCurve(.8f, []), new AudioAutomationCurve(.6f, []), new AudioAutomationCurve(.5f, []));

    private static SoundFontPlaybackService NewService()
    {
        using var soundfont = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
        return new SoundFontPlaybackService(soundfont, 48000);
    }

    private static void PerTrackIsolation()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var combined = NewService(); var onlyA = combined.CreateIsolatedInstance(); var onlyB = combined.CreateIsolatedInstance();
        foreach (var synth in new[] { combined, onlyA })
        {
            synth.SetTrackEffects(new Dictionary<Guid, TrackEffectsChain> { [a] = new([MakeEffect()]) });
            synth.ProgramChange(a, 0, 27); synth.NoteOn(a, 0, 60, 110, Guid.NewGuid());
        }
        foreach (var synth in new[] { combined, onlyB }) { synth.ProgramChange(b, 1, 0); synth.NoteOn(b, 1, 72, 100, Guid.NewGuid()); }
        var mixL = new float[4096]; var mixR = new float[4096];
        var aL = new float[4096]; var aR = new float[4096];
        var bL = new float[4096]; var bR = new float[4096];
        combined.Render(mixL, mixR, 0); onlyA.Render(aL, aR, 0); onlyB.Render(bL, bR, 0);
        Check(bL.Any(x => Math.Abs(x) > .000001f), "Isolation reference track must be audible.");
        Check(Enumerable.Range(0, mixL.Length).All(i => Math.Abs(mixL[i] - aL[i] - bL[i]) < .00001f), "Overdrive on one track changed another track's samples.");
    }

    private static void ArrangementPositions()
    {
        var score = new ScoreDocument("Pedal position", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar, 27));
        var first = score.Sections[0]; score.AddMeasure(first);
        var second = score.AddSection("Second", "", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
        var clock = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, [second.Id, first.Id, second.Id]));
        var capture = new Capture();
        typeof(ScorePlaybackClock).GetField("_trackEffects", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(clock,
            new Dictionary<Guid, TrackEffectsChain> { [track.Id] = new([capture]) });
        var service = NewService();
        clock.Render(service, new float[288000], new float[288000]);
        Check(capture.Starts.Count >= 3 && capture.Starts[0] == 96000 && capture.Starts.Contains(0) && capture.Starts.Count(x => x == 96000) >= 2,
            $"Pedal automation did not follow source positions through arrangement jumps and repetitions: {string.Join(',', capture.Starts)}.");
    }

    private static void CompiledTempoAndStep()
    {
        var score = new ScoreDocument("Pedal tempo", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar, 27));
        var measure = score.AddMeasure(score.Sections[0]);
        measure.TempoEvents.Add(new TempoEvent(ScoreTime.Zero, new TempoMark(120m, new RhythmicValue(4)), TempoInterpolation.Linear));
        measure.TempoEvents.Add(new TempoEvent(new ScoreTime(1, 2), new TempoMark(60m, new RhythmicValue(4)), TempoInterpolation.Step));
        var pedal = new TrackPedal();
        var drive = new PedalAutomationLane { ParameterId = "drive" };
        drive.Points.Add(new PedalAutomationPoint(0, 0));
        drive.Points.Add(new PedalAutomationPoint(4, 1));
        var tone = new PedalAutomationLane { ParameterId = "tone" };
        tone.Points.Add(new PedalAutomationPoint(0, .2f, ExpressionSegmentMode.Step));
        tone.Points.Add(new PedalAutomationPoint(2, .8f));
        var level = new PedalAutomationLane { ParameterId = "level" };
        level.Points.Add(new PedalAutomationPoint(1e300, .7f)); // valid, but far beyond this score
        pedal.Automation.AddRange([drive, tone, level]); track.Pedals.Add(pedal);
        var clock = new ScorePlaybackClock(score, 48000);
        var chains = (Dictionary<Guid, TrackEffectsChain>)typeof(ScorePlaybackClock).GetField("_trackEffects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(clock)!;
        var chain = chains[track.Id];
        var effects = (ITrackAudioEffect[])typeof(TrackEffectsChain).GetField("_effects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(chain)!;
        var processor = (OverdriveTrackEffect)effects[0];
        var driveCurve = (AudioAutomationCurve)typeof(OverdriveTrackEffect).GetField("_drive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(processor)!;
        var toneCurve = (AudioAutomationCurve)typeof(OverdriveTrackEffect).GetField("_tone", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(processor)!;
        var levelCurve = (AudioAutomationCurve)typeof(OverdriveTrackEffect).GetField("_level", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(processor)!;
        var tempo = score.CreateTempoMap();
        long Frame(ScoreTime position) => (long)Math.Round(tempo.GetElapsedSeconds(position) * 48000, MidpointRounding.AwayFromZero);
        Check(Math.Abs(driveCurve.ValueAt(Frame(new ScoreTime(1, 4))) - .25f) < .001f &&
              Math.Abs(driveCurve.ValueAt(Frame(new ScoreTime(1, 2))) - .5f) < .001f &&
              Math.Abs(driveCurve.ValueAt(Frame(new ScoreTime(3, 4))) - .75f) < .001f,
              "Compiled drive automation does not follow musical position across a tempo ramp/change.");
        Check(Math.Abs(toneCurve.ValueAt(Frame(new ScoreTime(1, 4))) - .2f) < .001f &&
              Math.Abs(toneCurve.ValueAt(Frame(new ScoreTime(1, 2))) - .8f) < .001f,
              "Compiled step automation did not switch at its musical keyframe.");
        Check(Math.Abs(levelCurve.ValueAt(0) - .7f) < .001f, "Out-of-score keyframe did not hold its first value safely.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

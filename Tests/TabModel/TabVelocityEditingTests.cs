using OpenTab.Music;

internal static class TabVelocityEditingTests
{
    public static void Run()
    {
        var source = new TrackMeasure(Guid.NewGuid());
        var other = new TrackMeasure(Guid.NewGuid());
        var first = new TabEvent(1, 0, 60, 81, ScoreTime.Zero, new ScoreTime(1, 4));
        var second = new TabEvent(2, 0, 60, 125, ScoreTime.Zero, new ScoreTime(1, 4));
        source.TabEvents.Add(first);
        other.TabEvents.Add(second);
        var selection = new[] { (source, first), (other, second), (source, first) };
        Check(TabVelocityEditing.InitialLevel(selection) == 3 && first.Velocity == 81, "initial level must be the lowest without quantizing MIDI");
        TabVelocityEditing.Apply(selection, 3);
        Check(first.Velocity == Beat.MidiVelocityForIntensity(3) && second.Velocity == first.Velocity, "explicit level must apply to all source notes");
        second.Velocity = 125;
        other.TabEvents.Remove(second);
        Reject(() => TabVelocityEditing.Apply(selection, 1));
        Check(first.Velocity == Beat.MidiVelocityForIntensity(3) && second.Velocity == 125, "stale selection must fail atomically");
        Reject(() => TabVelocityEditing.Apply(new[] { (source, first) }, 0));
        Check(first.Velocity == Beat.MidiVelocityForIntensity(3), "invalid level mutated a note");
        RelativeGesturePreview();
        Console.WriteLine("PASS tab batch velocity selection and atomic validation");
    }

    private static void RelativeGesturePreview()
    {
        var measure = new TrackMeasure(Guid.NewGuid());
        var soft = new TabEvent(1, 0, 60, 80, ScoreTime.Zero, new ScoreTime(1, 4));
        var strong = new TabEvent(2, 0, 62, 125, ScoreTime.Zero, new ScoreTime(1, 4));
        measure.TabEvents.AddRange(new[] { soft, strong });
        var gesture = new TabVelocityGesture(new[] { (measure, soft), (measure, strong), (measure, soft) });
        gesture.ApplyDelta(1);
        Check(soft.Velocity == Beat.MidiVelocityForIntensity(4) && strong.Velocity == 125 && gesture.HasChanges,
            "relative gesture must preserve raw velocity when a clamped level stays unchanged");
        gesture.ApplyDelta(0);
        Check(soft.Velocity == 80 && strong.Velocity == 125 && !gesture.HasChanges,
            "zero delta must restore exact original MIDI values");
        gesture.ApplyDelta(99);
        Check(soft.Velocity == 127 && strong.Velocity == 125, "relative levels must clamp at five while retaining raw MIDI at an unchanged level");
        gesture.Cancel();
        Check(soft.Velocity == 80 && strong.Velocity == 125 && !gesture.HasChanges,
            "cancel must restore captured MIDI values");

        gesture.ApplyDelta(1);
        measure.TabEvents.Remove(strong);
        Reject(() => gesture.ApplyDelta(2));
        Check(soft.Velocity == Beat.MidiVelocityForIntensity(4), "stale gesture target must reject atomically");
        Reject(gesture.Cancel);
        Check(soft.Velocity == Beat.MidiVelocityForIntensity(4), "stale cancel must reject atomically");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected rejection.");
    }
}

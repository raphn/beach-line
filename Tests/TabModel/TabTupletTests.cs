using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class TabTupletTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Tuplets", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        track.ConfigureTab(new[] { 40, 45, 50, 55, 59, 64 }, 24);
        var section = score.Sections[0];
        score.AddMeasure(section);
        var part = section.Measures[0].GetTrackMeasure(track.Id)!;
        var note = new TabEvent(1, 0, 40, 90, new ScoreTime(1, 5), new ScoreTime(1, 8));
        part.TabEvents.Add(note);
        TabTupletEditing.ApplyTuplet(new[] { (part, note) }, 3, 8);
        Assert(note.TupletActual == 3 && note.TupletNormal == 2, "triplet ratio should be stored as 3:2");
        Assert(note.Onset == new ScoreTime(1, 6) && note.Duration == new ScoreTime(1, 12), "triplet onset and duration should use a 1/12 whole-note grid");
        TabTupletEditing.ApplyTuplet(new[] { (part, note) }, 5, 8);
        Assert(note.Onset == new ScoreTime(1, 5) && note.Duration == new ScoreTime(1, 10), "quintuplet should resnap onset and scale the preserved straight base duration");
        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredNote = restored.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.TabEvents.Single();
        Assert(restoredNote.TupletActual == 5 && restoredNote.Duration == note.Duration && restoredNote.Onset == note.Onset,
            "tuplet metadata and exact timing should survive persistence");
        var linked = new NoteExpression { Kind = ExpressionKind.Velocity, Position = .75,
            Points = new() { new ExpressionPoint(0, .5), new ExpressionPoint(1, 1.5) } };
        note.Expressions.Add(linked);
        var linkedClone = linked.Clone();
        linkedClone.Position = .25;
        var second = new TabEvent(2, 0, 45, 90, ScoreTime.Zero, new ScoreTime(1, 8));
        second.Expressions.Add(linkedClone);
        part.TabEvents.Add(second);
        var expressionRestored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredExpressions = expressionRestored.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.TabEvents
            .SelectMany(tabEvent => tabEvent.Expressions).ToArray();
        Assert(restoredExpressions.Length == 2 && restoredExpressions[0].Id == restoredExpressions[1].Id &&
            restoredExpressions[0].Position == .75 && restoredExpressions[1].Position == .25 &&
            restoredExpressions.All(curve => curve.Sample(.5) == 1),
            "linked expressions, per-note positions, and sampled values should survive persistence");
        Console.WriteLine("PASS tablature tuplet snapping and persistence");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

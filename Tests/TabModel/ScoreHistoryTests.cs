using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class ScoreHistoryTests
{
    public static void Run()
    {
        var score = new ScoreDocument("History", new TempoMark(100, new RhythmicValue(4)), new TimeSignature(4, 4));
        var history = new ScoreHistory(capacity: 2);
        var initial = ScoreFile.Serialize(score);
        history.Reset(initial);
        history.Record(ScoreFile.Serialize(score));
        Assert(!history.CanUndo, "A no-op snapshot must not create an undo step.");

        score.Title = "One";
        var one = ScoreFile.Serialize(score);
        history.Record(one);
        Assert(history.TryUndo(out var restored) && ScoreFile.Deserialize(restored).Title == "History",
            "Undo must restore the initial state.");
        Assert(history.TryRedo(out restored) && ScoreFile.Deserialize(restored).Title == "One",
            "Redo must restore the edited state.");

        Assert(history.TryUndo(out _), "The edit must remain undoable after redo.");
        score.Title = "Branch";
        history.Record(ScoreFile.Serialize(score));
        Assert(!history.CanRedo, "Recording after undo must discard the redo branch.");
        Assert(history.TryUndo(out restored) && ScoreFile.Deserialize(restored).Title == "History",
            "Undo after branching must restore the branch base.");

        score.Title = "Two";
        history.Record(ScoreFile.Serialize(score));
        score.Title = "Three";
        history.Record(ScoreFile.Serialize(score));
        score.Title = "Four";
        history.Record(ScoreFile.Serialize(score));
        Assert(history.TryUndo(out restored) && ScoreFile.Deserialize(restored).Title == "Three",
            "History must retain the configured number of prior states.");
        Console.WriteLine("PASS score history undo, redo, no-op, branching and capacity");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class ArrangementTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Arrangement", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var a = score.Sections[0]; score.AddMeasure(a);
        var b = score.AddSection("B", "B");
        var first = ArrangementEditing.Add(score);
        var second = ArrangementEditing.Add(score);
        var third = ArrangementEditing.Add(score);
        Check(first.Name == "Arrangement" && second.Name == "Arrangement2" && third.Name == "Arrangement3", "Names were not unique.");
        ArrangementEditing.Append(score, first, b.Id);
        ArrangementEditing.Append(score, first, b.Id);
        ArrangementEditing.Append(score, first, a.Id);
        Check(first.Sections.Count == 2 && first.Sections[0].Repetitions == 2, "Consecutive appended sections did not merge.");
        ArrangementEditing.Rename(score, first, "Alternate");
        try { ArrangementEditing.Rename(score, first, "arrangement2"); throw new Exception("Duplicate name accepted."); }
        catch (InvalidOperationException) { }
        var loaded = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        Check(loaded.Arrangements.Count == 3 && loaded.Arrangements[0].Name == "Alternate" &&
            loaded.Arrangements[0].Sections.Select(e => (e.SectionId, e.Repetitions)).SequenceEqual(new[] { (b.Id, 2), (a.Id, 1) }),
            "Arrangement ordering, name, or repetitions did not round-trip.");
        score.RemoveSection(b);
        Check(first.Sections.Count == 1 && first.Sections[0].SectionId == a.Id, "Deleted section remained in the arrangement.");
        Console.WriteLine("PASS arrangement names, sequence, persistence, and section deletion");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

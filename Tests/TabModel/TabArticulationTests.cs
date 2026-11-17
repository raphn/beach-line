using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class TabArticulationTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Articulation", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var guitar = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        guitar.ConfigureTab(new[] { 64, 59, 55, 50, 45, 40 });
        var section = score.Sections[0];
        score.AddMeasure(section);
        score.AddMeasure(section);
        var part = section.Measures[0].GetTrackMeasure(guitar.Id)!;
        var first = new TabEvent(1, 0, 64, 90, ScoreTime.Zero, new ScoreTime(1, 8));
        var second = new TabEvent(1, 2, 66, 90, new ScoreTime(1, 8), new ScoreTime(1, 8))
        {
            IncomingArticulation = StringArticulation.HammerOn
        };
        second.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch,
            Points = { new ExpressionPoint(0, 0, ExpressionSegmentMode.Step), new ExpressionPoint(1, 1) } });
        part.UsesTabEvents = true;
        part.TabEvents.Add(first);
        part.TabEvents.Add(second);

        var encoded = ScoreFile.Serialize(score);
        var restored = ScoreFile.Deserialize(encoded);
        var restoredPart = restored.Sections[0].Measures[0].GetTrackMeasure(guitar.Id)!;
        var restoredSecond = restoredPart.TabEvents.Single(note => note.StringNumber == 1 && note.Onset == second.Onset);
        Assert(restoredSecond.IncomingArticulation == StringArticulation.HammerOn && restoredSecond.Id == second.Id,
            "Articulation or event identity did not round-trip through score persistence.");
        Assert(restoredSecond.Expressions.Single().Points[0].SegmentMode == ExpressionSegmentMode.Step,
            "Expression segment mode did not round-trip through score persistence.");

        var duplicate = score.DuplicateMeasure(section, 0, before: false);
        var duplicatedNote = duplicate.GetTrackMeasure(guitar.Id)!.TabEvents.Single(note => note.StringNumber == 1 && note.Onset == second.Onset);
        Assert(duplicatedNote.IncomingArticulation == StringArticulation.HammerOn && duplicatedNote.Id != second.Id,
            "Measure clone did not retain articulation with an independent event identity.");

        var clipboard = TabClipboard.Copy(guitar, part, new[] { second }, 1, second.Onset);
        var destination = section.Measures[2].GetTrackMeasure(guitar.Id)!;
        var pasted = clipboard.PlanPaste(guitar, destination, 1, ScoreTime.Zero).Apply().Single();
        Assert(pasted.IncomingArticulation == StringArticulation.HammerOn && pasted.Id != second.Id,
            "Clipboard copy did not retain articulation with a fresh event identity.");

        var history = new ScoreHistory();
        history.Reset(encoded);
        second.IncomingArticulation = StringArticulation.PullOff;
        history.Record(ScoreFile.Serialize(score));
        Assert(history.TryUndo(out var undo) && ScoreFile.Deserialize(undo).Sections[0].Measures[0]
            .GetTrackMeasure(guitar.Id)!.TabEvents.Single(note => note.Id == second.Id).IncomingArticulation == StringArticulation.HammerOn,
            "Undo snapshot did not restore the earlier articulation.");
        Assert(history.TryRedo(out var redo) && ScoreFile.Deserialize(redo).Sections[0].Measures[0]
            .GetTrackMeasure(guitar.Id)!.TabEvents.Single(note => note.Id == second.Id).IncomingArticulation == StringArticulation.PullOff,
            "Redo snapshot did not restore the later articulation.");

        var legacy = RewriteAsVersionEleven(encoded);
        var legacyRestored = ScoreFile.Deserialize(legacy).Sections[0].Measures[0].GetTrackMeasure(guitar.Id)!;
        Assert(legacyRestored.TabEvents.Single(note => note.Id == second.Id).IncomingArticulation == StringArticulation.None,
            "Version 11 scores must default incoming articulation to None.");
        Assert(legacyRestored.TabEvents.Single(note => note.Id == second.Id).Expressions.Single().Points[0].SegmentMode == ExpressionSegmentMode.Linear,
            "Version 11 expression points must default to linear segments.");
        Console.WriteLine("PASS tab articulation persistence, clipboard, history and legacy defaults");
    }

    private static byte[] RewriteAsVersionEleven(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, false);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        using var entry = archive.GetEntry("project.json")!.Open();
        var root = JsonNode.Parse(entry)!;
        root["Version"] = 11;
        foreach (var measure in root["Sections"]![0]!["Measures"]!.AsArray())
            foreach (var part in measure!["Parts"]!.AsArray())
                foreach (var note in part!["TabEvents"]!.AsArray())
                {
                    note!.AsObject().Remove("IncomingArticulation");
                    foreach (var expression in note["Expressions"]!.AsArray())
                        foreach (var point in expression!["Points"]!.AsArray())
                            point!.AsObject().Remove("SegmentMode");
                }
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var targetEntry = target.CreateEntry("project.json").Open())
            JsonSerializer.Serialize(targetEntry, root);
        return output.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class MeasureSnapTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Snap test", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Kit", TrackKind.Percussion));
        track.TabSnapDenominator = 8;
        var section = score.Sections[0];
        score.AddMeasure(section);
        var measure = section.Measures[0];
        var part = measure.GetTrackMeasure(track.Id)!;
        part.SnapDenominator = 8;
        var version = score.AddTrackMeasureVersion(section, measure, track, "Alternate", part);
        version.Content.SnapDenominator = 32;

        var duplicate = score.DuplicateMeasure(section, 0, before: false);
        Assert(duplicate.GetTrackMeasure(track.Id)!.SnapDenominator == 8, "Measure duplication lost the authored snap.");
        Assert(duplicate.TrackMeasureVersions.Single().Content.SnapDenominator == 32, "Measure duplication lost version snap.");

        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredMeasure = restored.Sections[0].Measures[0];
        Assert(restoredMeasure.GetTrackMeasure(track.Id)!.SnapDenominator == 8, "Measure snap did not round-trip through score persistence.");
        Assert(restoredMeasure.TrackMeasureVersions.Single().Content.SnapDenominator == 32, "Version snap did not round-trip through score persistence.");
        Assert(restored.Tracks.Single().TabSnapDenominator == 8, "Track snap default did not round-trip through score persistence.");

        var legacy = ScoreFile.Deserialize(AsVersion12WithoutSnap(ScoreFile.Serialize(score)));
        Assert(legacy.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.SnapDenominator == 0,
            "Older score content should leave snap unset so the editor can use its track default.");
        Assert(legacy.Sections[0].Measures[0].TrackMeasureVersions.Single().Content.SnapDenominator == 0,
            "Older versions should leave snap unset so the editor can use its track default.");
        var preTrackDefault = ScoreFile.Deserialize(AsVersion13WithoutTrackSnap(ScoreFile.Serialize(score)));
        Assert(preTrackDefault.Tracks.Single().TabSnapDenominator is null,
            "Scores predating track-owned defaults should retain an unset value for workspace migration.");

        try { part.SnapDenominator = 12; throw new InvalidOperationException("Unsupported snap denominator was accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS measure and version snap persistence, copy and legacy fallback");
    }

    private static byte[] AsVersion12WithoutSnap(byte[] source)
    {
        using var input = new ZipArchive(new MemoryStream(source), ZipArchiveMode.Read);
        using var reader = new StreamReader(input.GetEntry("project.json")!.Open());
        var root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        root["Version"] = 12;
        foreach (var section in root["Sections"]!.AsArray())
        foreach (var measure in section!["Measures"]!.AsArray())
        {
            foreach (var part in measure!["Parts"]!.AsArray()) part!.AsObject().Remove("SnapDenominator");
            foreach (var version in measure["Versions"]?.AsArray() ?? new JsonArray())
                version!["Content"]!.AsObject().Remove("SnapDenominator");
        }
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("project.json").Open()))
            writer.Write(root.ToJsonString());
        return output.ToArray();
    }

    private static byte[] AsVersion13WithoutTrackSnap(byte[] source)
    {
        using var input = new ZipArchive(new MemoryStream(source), ZipArchiveMode.Read);
        using var reader = new StreamReader(input.GetEntry("project.json")!.Open());
        var root = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        root["Version"] = 13;
        foreach (var track in root["Tracks"]!.AsArray()) track!.AsObject().Remove("TabSnapDenominator");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry("project.json").Open()))
            writer.Write(root.ToJsonString());
        return output.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

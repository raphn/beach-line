using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class SoundVariationTests
{
    public static void Run()
    {
        CatalogMappings();
        CopyAndPersistence();
        LegacyFilesDefaultToOriginal();
    }

    private static void CatalogMappings()
    {
        Assert(SoundVariations.GetOptions(25).Count == 2 && SoundVariations.ResolveProgram(25, "palm_mute") == 28 &&
            SoundVariations.ResolveProgram(25, "harmonics") == 31, "guitar variation mapping is incorrect");
        Assert(SoundVariations.ResolveProgram(48, "tremolo") == 44 && SoundVariations.ResolveProgram(49, "pizzicato") == 45,
            "string variation mapping is incorrect");
        Assert(SoundVariations.ResolveProgram(56, "muted_trumpet") == 59, "muted trumpet mapping is incorrect");
        Assert(SoundVariations.ResolvePercussionPitch(42, "open") == 46 &&
            SoundVariations.ResolvePercussionPitch(78, "muted") == 78 &&
            SoundVariations.ResolvePercussionPitch(80, "open") == 81, "percussion family mapping is incorrect");
        Assert(SoundVariations.ResolveProgram(1, "palm_mute") == 1 &&
            SoundVariations.ResolvePercussionPitch(36, "open") == 36, "unsupported variations changed a sound");
    }

    private static void CopyAndPersistence()
    {
        var track = new ScoreTrack("Guitar", TrackKind.Guitar, 25);
        track.ConfigureTab(new[] { 64, 59, 55 });
        var score = new ScoreDocument("Variations", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        score.AddTrack(track);
        var part = score.AddMeasure(score.Sections[0]).GetTrackMeasure(track.Id)!;
        var note = TabEventEditing.Add(part, track, 1, 3, ScoreTime.Zero, new ScoreTime(1, 4));
        note.SoundVariation = "harmonics";
        var clip = TabClipboard.Copy(track, part, new[] { note }, 1, ScoreTime.Zero);
        var pasted = clip.PlanPaste(track, new TrackMeasure(track.Id), 1, new ScoreTime(1, 2)).Apply().Single();
        Assert(pasted.SoundVariation == "harmonics", "clipboard paste lost the selected variation");
        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        Assert(restored.Sections[0].Measures[0].TrackMeasures.Single().TabEvents.Single().SoundVariation == "harmonics",
            "score round trip lost the selected variation");
    }

    private static void LegacyFilesDefaultToOriginal()
    {
        var track = new ScoreTrack("Guitar", TrackKind.Guitar, 25);
        track.ConfigureTab(new[] { 64, 59, 55 });
        var score = new ScoreDocument("Legacy variation", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        score.AddTrack(track);
        var part = score.AddMeasure(score.Sections[0]).GetTrackMeasure(track.Id)!;
        var note = TabEventEditing.Add(part, track, 1, 0, ScoreTime.Zero, new ScoreTime(1, 4));
        note.SoundVariation = "palm_mute";
        var bytes = ScoreFile.Serialize(score);
        using var input = new MemoryStream(bytes);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        using var reader = new StreamReader(archive.GetEntry("project.json")!.Open());
        var json = JsonNode.Parse(reader.ReadToEnd())!;
        json["Version"] = 7;
        foreach (var eventNode in json["Sections"]![0]!["Measures"]![0]!["Parts"]![0]!["TabEvents"]!.AsArray())
            eventNode!.AsObject().Remove("SoundVariation");
        using var output = new MemoryStream();
        using (var legacy = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(legacy.CreateEntry("project.json").Open())) writer.Write(json.ToJsonString());
        var restored = ScoreFile.Deserialize(output.ToArray());
        Assert(restored.Sections[0].Measures[0].TrackMeasures.Single().TabEvents.Single().SoundVariation == "",
            "pre-variation score did not retain the default original sound");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

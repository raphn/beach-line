using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class TrackPedalTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Pedals", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Lead", TrackKind.Guitar, 27));
        var cleanTrack = score.AddTrack(new ScoreTrack("Other", TrackKind.Keyboard));
        var pedal = new TrackPedal { Drive = .7f, Tone = .2f, Level = .6f, Enabled = false };
        var lane = new PedalAutomationLane { ParameterId = "drive" };
        lane.Points.Add(new PedalAutomationPoint(1, .2f));
        lane.Points.Add(new PedalAutomationPoint(3, .8f, ExpressionSegmentMode.Step));
        lane.Points.Add(new PedalAutomationPoint(5, .4f));
        pedal.Automation.Add(lane);
        track.Pedals.Add(pedal);
        var second = new TrackPedal { TypeId = "future-effect", Enabled = true };
        second.SetParameter("future-parameter", .73f);
        second.ExtensionData["FutureMetadata"] = JsonSerializer.SerializeToElement(new { label = "retained" });
        track.Pedals.Add(second);
        var chorus = new TrackPedal { TypeId = "chorus" };
        Check(Math.Abs(chorus.GetParameter("rate") - .35f) < .0001f, "chorus default rate");
        chorus.SetParameter("rate", .64f);
        chorus.SetParameter("depth", .25f);
        var chorusAutomation = new PedalAutomationLane { ParameterId = "mix" };
        chorusAutomation.Points.Add(new PedalAutomationPoint(2, .6f));
        chorusAutomation.ExtensionData["FutureLane"] = JsonSerializer.SerializeToElement(42);
        chorus.Automation.Add(chorusAutomation);
        var delay = new TrackPedal { TypeId = "delay" };
        delay.SetParameter("time", .8f);
        delay.SetParameter("feedback", .4f);
        delay.SetParameter("mix", .2f);
        track.Pedals.Add(chorus);
        track.Pedals.Add(delay);

        Check(Math.Abs(lane.Sample(0, .5f) - .2f) < .0001f, "first point should hold before its position");
        Check(Math.Abs(lane.Sample(2, .5f) - .5f) < .0001f, "linear automation interpolation");
        Check(Math.Abs(lane.Sample(4, .5f) - .8f) < .0001f, "step automation interpolation");
        Check(Math.Abs(lane.Sample(6, .5f) - .4f) < .0001f, "last point should hold after its position");
        Check(new PedalAutomationLane { ParameterId = "tone" }.Sample(0, .35f) == .35f, "empty automation uses fallback");

        var bytes = ScoreFile.Serialize(score);
        var loaded = ScoreFile.Deserialize(bytes);
        var restored = loaded.Tracks[0].Pedals;
        Check(restored.Count == 4 && restored[0].Id == pedal.Id && restored[1].TypeId == "future-effect", "effect identity, ordering and type persist");
        Check(!restored[0].Enabled && restored[0].Drive == .7f && restored[0].Tone == .2f && restored[0].Level == .6f, "pedal settings persist");
        Check(restored[0].Automation[0].Points.SequenceEqual(lane.Points), "automation positions, values and interpolation persist");
        Check(restored[1].GetParameter("future-parameter") == .73f, "unknown effect parameter must be preserved");
        Check(restored[1].ExtensionData["FutureMetadata"].GetProperty("label").GetString() == "retained", "unknown pedal metadata must be preserved");
        Check(restored[2].TypeId == "chorus" && restored[2].GetParameter("rate") == .64f && restored[2].GetParameter("depth") == .25f && restored[2].Automation[0].Points.SequenceEqual(chorusAutomation.Points), "chorus parameters and automation round-trip");
        Check(restored[2].Automation[0].ExtensionData["FutureLane"].GetInt32() == 42, "unknown lane metadata must be preserved");
        Check(restored[3].TypeId == "delay" && restored[3].GetParameter("time") == .8f && restored[3].GetParameter("feedback") == .4f && restored[3].GetParameter("mix") == .2f, "delay parameters round-trip");
        var legacy15 = ScoreFile.Deserialize(AsVersion15(bytes));
        Check(legacy15.Tracks[0].Pedals[0].Drive == .7f && legacy15.Tracks[0].Pedals[0].Automation[0].Points.SequenceEqual(lane.Points), "version 15 overdrive and automation preserved");
        Check(legacy15.Tracks[0].Pedals[2].GetParameter("rate") == .35f, "missing parameters use defaults");
        Check(loaded.Tracks[1].Pedals.Count == 0 && cleanTrack.Pedals.Count == 0, "effects stay isolated to their track");
        Check(ScoreFile.Deserialize(AsVersion14(bytes)).Tracks.All(t => t.Pedals.Count == 0), "older projects load without pedals");

        lane.Points.Add(new PedalAutomationPoint(5, .5f));
        try { ScoreFile.Serialize(score); throw new InvalidOperationException("invalid duplicate time accepted"); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS track pedal automation, validation and project compatibility");
    }

    private static byte[] AsVersion14(byte[] source)
    {
        using var input = new ZipArchive(new MemoryStream(source), ZipArchiveMode.Read);
        using var entry = input.GetEntry("project.json")!.Open();
        var json = JsonNode.Parse(entry)!;
        json["Version"] = 14;
        foreach (var track in json["Tracks"]!.AsArray()) track!.AsObject().Remove("Pedals");
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var target = output.CreateEntry("project.json").Open()) JsonSerializer.Serialize(target, json);
        return buffer.ToArray();
    }

    private static byte[] AsVersion15(byte[] source)
    {
        using var input = new ZipArchive(new MemoryStream(source), ZipArchiveMode.Read);
        using var entry = input.GetEntry("project.json")!.Open();
        var json = JsonNode.Parse(entry)!;
        json["Version"] = 15;
        foreach (var track in json["Tracks"]!.AsArray())
            foreach (var pedal in track!["Pedals"]!.AsArray()) pedal!.AsObject().Remove("Parameters");
        using var buffer = new MemoryStream();
        using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        using (var target = output.CreateEntry("project.json").Open()) JsonSerializer.Serialize(target, json);
        return buffer.ToArray();
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Pedal regression: " + description);
    }
}

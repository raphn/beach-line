using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Persistence;

// Retains the existing tab model, legacy archive, and keyboard decoder checks.
internal static class LegacyTabTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Tab model", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Bass", TrackKind.Bass));
        track.ConfigureTab(new[] { 28, 33, 38, 43 });
        var measure = score.AddMeasure(score.Sections[0]);
        var part = measure.GetTrackMeasure(track.Id)!;
        var first = TabEventEditing.Add(part, track, 1, 0, ScoreTime.Zero, new ScoreTime(3, 4));
        var other = TabEventEditing.Add(part, track, 2, 5, ScoreTime.Zero, new ScoreTime(1, 4));
        Check(first.Velocity == Beat.MidiVelocityForIntensity(4) && first.Intensity == 4, "New bass notes must default to velocity level 4.");
        first.Velocity = 96;
        Check(first.Intensity == 4 && first.Velocity == 96, "Reading intensity changed an existing MIDI velocity.");
        other.Velocity = 81;
        Check(other.Intensity == 3 && other.Velocity == 81, "Legacy velocity was quantized on read.");
        for (var level = 1; level <= 5; level++)
        {
            first.Intensity = level;
            Check(first.Intensity == level && first.Velocity == Beat.MidiVelocityForIntensity(level), "Intensity mapping failed.");
        }
        try { first.Intensity = 0; throw new Exception("Invalid intensity accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Check(first.Intensity == 5, "Invalid intensity changed the note.");
        TabEventEditing.Add(part, track, 1, 2, new ScoreTime(1, 4), new ScoreTime(1, 2));
        Check(TabEventEditing.GetEffectiveEnd(part.TabEvents, first) == new ScoreTime(1, 4), "Same-string cutoff was not derived.");
        Check(TabEventEditing.GetEffectiveEnd(part.TabEvents, other) == new ScoreTime(1, 4), "Other-string duration changed.");
        Check(first.Duration == new ScoreTime(3, 4), "Cutoff modified authored duration.");
        try { TabEventEditing.Add(part, track, 1, 4, new ScoreTime(1, 4), new ScoreTime(1, 8)); throw new Exception("Same-onset attack was accepted."); }
        catch (InvalidOperationException) { }
        var explicitVelocity = TabEventEditing.Add(part, track, 3, 0, new ScoreTime(1, 2), new ScoreTime(1, 8), 37);
        Check(explicitVelocity.Velocity == 37, "an explicit MIDI velocity must override the track default");
        var guitar = new ScoreTrack("Guitar", TrackKind.Guitar);
        guitar.ConfigureTab(new[] { 64, 59 });
        var guitarNote = TabEventEditing.Add(new TrackMeasure(guitar.Id), guitar, 1, 0, ScoreTime.Zero, new ScoreTime(1, 4));
        Check(guitarNote.Intensity == 3 && guitarNote.Velocity == Beat.MidiVelocityForIntensity(3), "new guitar notes must default to intensity 3");

        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredTrack = restored.Tracks.Single();
        track.InstrumentImageId = "acoustic-guitar";
        track.DisplayColorRgba = 0x12ab34ff;
        restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        restoredTrack = restored.Tracks.Single();
        Check(restoredTrack.InstrumentImageId == "acoustic-guitar" && restoredTrack.DisplayColorRgba == 0x12ab34ff,
            "Track image identifier or color did not round-trip.");
        var restoredPart = restored.Sections[0].Measures[0].GetTrackMeasure(restoredTrack.Id)!;
        Check(restoredPart.UsesTabEvents && restoredPart.TabEvents.Count == 4, "Tab events did not round-trip.");
        Check(restoredPart.TabEvents[0].Intensity == 5 && restoredPart.TabEvents[1].Velocity == 81 && restoredPart.TabEvents[1].Intensity == 3,
            "Edited intensity or legacy MIDI velocity did not round-trip.");
        Check(restoredPart.TabEvents[0].Id == first.Id && restoredPart.TabEvents[2].Onset == new ScoreTime(1, 4), "Tab identity or timing changed.");
        Check(restoredTrack.TabTuningMidiPitches.SequenceEqual(new[] { 28, 33, 38, 43 }), "Custom tuning changed.");
        try { restoredTrack.ConfigureTab(new[] { 29, 34, 39, 44 }); throw new Exception("Tuning change was accepted."); }
        catch (InvalidOperationException) { }

        var legacy = new ScoreDocument("Legacy", new TempoMark(100, new RhythmicValue(4)), new TimeSignature(4, 4));
        var legacyTrack = legacy.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        var legacyMeasure = legacy.AddMeasure(legacy.Sections[0]);
        legacyMeasure.GetTrackMeasure(legacyTrack.Id)!.Voices[0].Beats[0].AddNote(new ScoreNote(64, 90));
        using var input = new MemoryStream(ScoreFile.Serialize(legacy));
        using var inZip = new ZipArchive(input, ZipArchiveMode.Read);
        using var jsonStream = new MemoryStream();
        using (var entry = inZip.GetEntry("project.json")!.Open()) entry.CopyTo(jsonStream);
        var json = JsonNode.Parse(jsonStream.ToArray())!.AsObject();
        var legacyV3 = (JsonObject)json.DeepClone();
        foreach (var item in legacyV3["Tracks"]!.AsArray())
        {
            item!.AsObject().Remove("InstrumentImageId");
            item.AsObject().Remove("DisplayColorRgba");
        }
        using var legacyV3Archive = new MemoryStream();
        using (var zip = new ZipArchive(legacyV3Archive, ZipArchiveMode.Create, true))
        using (var output = zip.CreateEntry("project.json").Open())
            System.Text.Json.JsonSerializer.Serialize(output, legacyV3);
        var restoredLegacyV3 = ScoreFile.Deserialize(legacyV3Archive.ToArray());
        Check(restoredLegacyV3.Tracks[0].InstrumentImageId is null && restoredLegacyV3.Tracks[0].DisplayColorRgba == 0xffffffff,
            "A version 3 file without appearance fields did not receive safe defaults.");

        json["Version"] = 1;
        foreach (var item in json["Tracks"]!.AsArray()) { item!.AsObject().Remove("TabTuning"); item.AsObject().Remove("MaxFret"); item.AsObject().Remove("InstrumentImageId"); item.AsObject().Remove("DisplayColorRgba"); }
        foreach (var section in json["Sections"]!.AsArray())
        foreach (var bar in section!["Measures"]!.AsArray())
        foreach (var data in bar!["Parts"]!.AsArray()) { data!.AsObject().Remove("UsesTabEvents"); data.AsObject().Remove("TabEvents"); data.AsObject().Remove("RepeatCount"); }
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
        using (var output = zip.CreateEntry("project.json").Open())
            System.Text.Json.JsonSerializer.Serialize(output, json);
        var restoredLegacy = ScoreFile.Deserialize(archive.ToArray());
        Check(restoredLegacy.Tracks[0].InstrumentImageId is null && restoredLegacy.Tracks[0].DisplayColorRgba == 0xffffffff,
            "Legacy tracks did not default to kind image and opaque white color.");
        var legacyPart = restoredLegacy.Sections[0].Measures[0].GetTrackMeasure(restoredLegacy.Tracks[0].Id)!;
        Check(!legacyPart.UsesTabEvents && legacyPart.TabEvents.Count == 0 && legacyPart.Voices[0].Beats[0].Notes.Single().MidiPitch == 64,
            "Version 1 pitch-only notes were not preserved.");

        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var digit = typeof(MeasureTabView).GetMethod("TryGetDigit", flags)!;
        foreach (var (key, expected) in new[] { (Godot.Key.Key0, 0), (Godot.Key.Key9, 9), (Godot.Key.Kp0, 0), (Godot.Key.Kp3, 3), (Godot.Key.Kp9, 9) })
        {
            object?[] args = [key, 0];
            Check((bool)digit.Invoke(null, args)! && (int)args[1]! == expected, $"Digit decoding failed for {key}.");
        }
        object?[] nonDigit = [Godot.Key.Left, 0];
        Check(!(bool)digit.Invoke(null, nonDigit)!, "An arrow was interpreted as a fret digit.");
        var direction = typeof(MeasureTabView).GetMethod("TryGetNavigation", flags)!;
        foreach (var (key, horizontal, vertical) in new[] { (Godot.Key.Left, -1, 0), (Godot.Key.Right, 1, 0), (Godot.Key.Up, 0, -1), (Godot.Key.Down, 0, 1) })
        {
            object?[] args = [key, 0, 0];
            Check((bool)direction.Invoke(null, args)! && (int)args[1]! == horizontal && (int)args[2]! == vertical,
                $"Navigation decoding failed for {key}.");
        }
        var size = new Godot.Vector2(640, 240);
        var measureDuration = new MeasureTabView.ExactTime(1, 1);
        Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(32, 39), size, 6, 34, 16, measureDuration, out var row, out var onset)
            && row == 0 && onset == new MeasureTabView.ExactTime(0, 1), "First string/grid position mapped incorrectly.");
        Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(60, 39), size, 6, 34, 16, measureDuration, out row, out onset)
            && onset == new MeasureTabView.ExactTime(0, 1), "Pointer advanced before crossing the subdivision boundary.");
        Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(70, 39), size, 6, 34, 16, measureDuration, out row, out onset)
            && onset == new MeasureTabView.ExactTime(1, 16), "Pointer did not advance after crossing the subdivision boundary.");
        Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(144.5f, 209), size, 6, 34, 16, measureDuration, out row, out onset)
            && row == 5 && onset == new MeasureTabView.ExactTime(3, 16), "Pointer did not snap to the correct string and division.");
        Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(631, 39), size, 6, 34, 16, measureDuration, out row, out onset)
            && onset == new MeasureTabView.ExactTime(15, 16), "End-of-measure pointer did not stay inside the measure.");
        Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(40, 70), size, 6, 48, 16, measureDuration, out row, out onset)
            && row == 1 && onset == new MeasureTabView.ExactTime(0, 1), "Nondefault row geometry did not map to the second string.");
        Check(!MeasureTabView.TryMapPointerToCell(new Godot.Vector2(40, 310), size, 6, 48, 16, measureDuration, out _, out _),
            "Nondefault row geometry accepted a pointer below the playable strings.");
        Check(!MeasureTabView.TryMapPointerToCell(new Godot.Vector2(632, 39), size, 6, 34, 16, measureDuration, out _, out _)
            && !MeasureTabView.TryMapPointerToCell(new Godot.Vector2(40, 227), size, 6, 34, 16, measureDuration, out _, out _),
            "Pointer outside the playable area was accepted.");
        foreach (var (themeHeight, headerHeight, expected) in new[] { (42f, 38f, 42f), (30f, 55.2f, 56f), (80f, 40f, 80f), (42f, 230f, 230f) })
        {
            var sharedHeight = MeasureTabView.ResolveSharedRowHeight(themeHeight, headerHeight);
            Check(sharedHeight == expected, "Percussion headers and tab rows did not share the required height.");
            const float laneY = 120, rowStart = 22, headersY = 50;
            var spacer = MeasureTabView.ResolveHeaderSpacerHeight(laneY, rowStart, headersY);
            for (int i = 0; i < 6; i++)
            {
                var headerCenter = headersY + spacer + (i + .5f) * sharedHeight;
                var localY = headerCenter - laneY;
                Check(MeasureTabView.TryMapPointerToCell(new Godot.Vector2(40, localY), new Godot.Vector2(640, 2000), 6, sharedHeight, 16, measureDuration, out var lane, out _) && lane == i,
                    "A percussion header center did not map to its corresponding tab row.");
            }
        }
        Check(Math.Abs(ExpressionCurveGraph.SnapMusicalTime(.31, 1d / 4, 16) - .25) < 1e-9,
            "Curve snap did not map a sixteenth-note grid into a quarter note.");
        Check(Math.Abs(ExpressionCurveGraph.SnapMusicalTime(.65, 1d / 4, 12) - 2d / 3) < 1e-9,
            "Curve snap did not map triplet subdivisions into the note duration.");
        Console.WriteLine("PASS existing tab model, v1 archive and keyboard decoder checks");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

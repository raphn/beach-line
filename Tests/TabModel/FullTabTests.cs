using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class FullTabTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection."); }

    public static void Run()
    {
        var score = new ScoreDocument("Pitch grid", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Keys", TrackKind.Keyboard));
        track.ConfigureTab(new[] { 60, 48 });
        Check(track.UsesFullTab && track.TabLaneMidiPitches.Count == 128 && track.TabLaneMidiPitches[0] == 127 && track.TabLaneMidiPitches[127] == 0, "Pitch lanes must cover MIDI in descending order.");
        Check(track.TabTuningMidiPitches.SequenceEqual(new[] { 60, 48 }), "Pitch lanes changed stored tuning.");
        var bar = score.AddMeasure(score.Sections[0]);
        var part = bar.GetTrackMeasure(track.Id)!;
        var beat = part.Voices[0].Beats[0];
        var legacy = new ScoreNote(60, 81);
        beat.Notes.Add(legacy);
        var projected = TabEventEditing.GetLegacyPitchNotes(part).Single();
        Check(projected.Id == legacy.Id && projected.StringNumber == 68 && projected.Velocity == 81 && !part.UsesTabEvents, "Projection must retain identity and leave source untouched.");
        beat.PitchBendSemitones = 1;
        Reject(() => TabEventEditing.ConvertLegacyPitchNotes(part, track, new ScoreTime(1, 1)));
        Check(!part.UsesTabEvents && part.TabEvents.Count == 0, "Failed conversion must be atomic.");
        beat.PitchBendSemitones = 0;
        Reject(() => TabEventEditing.ConvertLegacyPitchNotes(part, track, new ScoreTime(1, 8)));
        var overlapping = new Voice();
        overlapping.Beats.Add(new Beat(new RhythmicValue(4)));
        overlapping.Beats[0].Notes.Add(new ScoreNote(60, 90));
        part.Voices.Add(overlapping);
        Reject(() => TabEventEditing.ConvertLegacyPitchNotes(part, track, new ScoreTime(1, 1)));
        part.Voices.Remove(overlapping);
        TabEventEditing.ConvertLegacyPitchNotes(part, track, new ScoreTime(1, 1));
        Check(part.Voices[0].Beats[0].Notes.Single() == legacy && part.TabEvents.Single().Id == legacy.Id, "Conversion must retain voices and identity.");
        score.AddTrackMeasureVersion(score.Sections[0], bar, track, "Variation", part);
        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredBar = restored.Sections[0].Measures[0];
        Check(restoredBar.GetTrackMeasure(track.Id)!.TabEvents.Single().MidiPitch == 60 && restoredBar.TrackMeasureVersions.Single().Content.TabEvents.Single().StringNumber == 68, "Pitch-grid main and version events must round-trip.");
        var clipboard = TabClipboard.Copy(track, part, part.TabEvents, 68, ScoreTime.Zero);
        var destination = new TrackMeasure(track.Id);
        Check(clipboard.PlanPaste(track, destination, 68, ScoreTime.Zero).Apply().Single().MidiPitch == 60, "Pitch-grid clipboard changed pitch.");
        var guitar = new ScoreTrack("Guitar", TrackKind.Guitar);
        guitar.ConfigureTab(new[] { 60 });
        Reject(() => clipboard.PlanPaste(guitar, new TrackMeasure(guitar.Id), 1, ScoreTime.Zero));
        var directDuration = new ScoreTime(1, 8);
        var directOnset = new ScoreTime(3, 8);
        var directPart = new TrackMeasure(track.Id);
        var direct = TabClipboard.CreateSingleNote(track, 68, 0, directDuration);
        var directEntry = direct.PlanPaste(track, directPart, 15, directOnset).Apply().Single();
        Check(directEntry.StringNumber == 68 && directEntry.MidiPitch == 60 && directEntry.Fret == 0 &&
            directEntry.Onset == directOnset && directEntry.Duration == directDuration,
            "Direct Full Tab entry must preserve the selected pitch lane and exact timing.");
        var chordDuration = new ScoreTime(5, 16);
        var chordOnset = new ScoreTime(3, 8);
        var chord = TabClipboard.CreateFullTabChord(track, new[] { 60, 127, 0, 64 }, chordDuration);
        var chordPart = new TrackMeasure(track.Id);
        var chordPlan = chord.PlanPaste(track, chordPart, 91, chordOnset);
        Check(chordPlan.Entries.Select(e => e.MidiPitch).Order().SequenceEqual(new[] { 0, 60, 64, 127 }) &&
            chordPlan.Entries.All(e => e.Fret == 0 && e.Onset == chordOnset && e.Duration == chordDuration),
            "Full Tab chord must preserve pitches and exact clicked timing.");
        Check(chordPlan.Apply().Select(e => e.MidiPitch).Order().SequenceEqual(new[] { 0, 60, 64, 127 }), "Full Tab chord paste changed pitches.");
        Reject(() => chord.PlanPaste(guitar, new TrackMeasure(guitar.Id), 1, ScoreTime.Zero));
        RejectArgument(() => TabClipboard.CreateFullTabChord(guitar, new[] { 60, 64 }, chordDuration));
        RejectArgument(() => TabClipboard.CreateFullTabChord(track, new[] { 60, 60 }, chordDuration));
        RejectArgument(() => TabClipboard.CreateFullTabChord(track, new[] { 60 }, chordDuration));
        RejectArgument(() => TabClipboard.CreateFullTabChord(track, new[] { -1, 128 }, chordDuration));
        RejectArgument(() => TabClipboard.CreateFullTabChord(track, new[] { 60, 64 }, ScoreTime.Zero));
        Console.WriteLine("PASS full tab grid, conversion, archive and clipboard");
    }

    private static void RejectArgument(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected argument rejection."); }
}

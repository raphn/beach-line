using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class TabRetuningTests
{
    public static void Run()
    {
        KeepFingeringAndRoundTrip();
        KeepPitchUsesCompleteChordMatching();
        ReorderedStringsPreserveChosenSemantics();
        RejectsImpossibleAndStalePlansAtomically();
        NamedVersionsParticipateAndAreStaleChecked();
        LegacyNotesAndUnconfiguredTracks();
        Console.WriteLine("PASS atomic track retuning modes, matching, stale plans, and round-trip");
    }

    private static void NamedVersionsParticipateAndAreStaleChecked()
    {
        var score = NewScore(new[] { 60, 55 });
        var track = score.Tracks[0];
        var section = score.Sections[0];
        var measure = section.Measures[0];
        var original = measure.GetTrackMeasure(track.Id)!;
        var baseEvent = TabEventEditing.Add(original, track, 1, 2, ScoreTime.Zero, new ScoreTime(1, 4));
        var version = score.AddTrackMeasureVersion(section, measure, track, "Alternate", original);
        var versionEvent = version.Content.TabEvents.Single();
        Check(TabRetuningPlan.TryCreate(score, track, new[] { 62, 57 }, TabRetuningMode.KeepFingering, out var plan, out var error), error);
        plan!.Apply();
        Check(baseEvent.MidiPitch == 64 && versionEvent.MidiPitch == 64 &&
              track.TabTuningMidiPitches.SequenceEqual(new[] { 62, 57 }),
            "Retuning did not update events in both original and named content.");

        Check(TabRetuningPlan.TryCreate(score, track, new[] { 61, 56 }, TabRetuningMode.KeepFingering, out var stale, out error), error);
        versionEvent.Velocity++;
        var currentTuning = track.TabTuningMidiPitches.ToArray();
        try { stale!.Apply(); throw new InvalidOperationException("A changed named version was retuned from a stale plan."); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("changed after", StringComparison.Ordinal)) { }
        Check(track.TabTuningMidiPitches.SequenceEqual(currentTuning) && baseEvent.MidiPitch == 64,
            "A stale named-version plan partially changed the score.");
    }

    private static void ReorderedStringsPreserveChosenSemantics()
    {
        foreach (var mode in new[] { TabRetuningMode.KeepFingering, TabRetuningMode.KeepPitch })
        {
            var score = NewScore(new[] { 64, 59, 55 });
            var track = score.Tracks[0];
            var part = score.Sections[0].Measures[0].GetTrackMeasure(track.Id)!;
            var note = TabEventEditing.Add(part, track, 1, 2, ScoreTime.Zero, new ScoreTime(1, 4));
            Check(TabRetuningPlan.TryCreate(score, track, new[] { 59, 64, 55 }, mode, out var plan, out var error), error);
            plan!.Apply();
            Check(mode == TabRetuningMode.KeepFingering
                ? note.StringNumber == 1 && note.Fret == 2 && note.MidiPitch == 61
                : note.MidiPitch == 66 && track.TabTuningMidiPitches[note.StringNumber - 1] + note.Fret == 66,
                "String reorder did not preserve the chosen property.");
        }
    }

    private static void KeepFingeringAndRoundTrip()
    {
        var score = NewScore(new[] { 60, 55 });
        var firstPart = score.Sections[0].Measures[0].GetTrackMeasure(score.Tracks[0].Id)!;
        var first = TabEventEditing.Add(firstPart, score.Tracks[0], 1, 3, new ScoreTime(1, 4), new ScoreTime(1, 2), 87);
        first.AllowsBarOverflow = true;
        var secondSection = score.AddSection("B", "Second");
        score.AddMeasure(secondSection);
        var secondPart = secondSection.Measures[0].GetTrackMeasure(score.Tracks[0].Id)!;
        var second = TabEventEditing.Add(secondPart, score.Tracks[0], 2, 4, ScoreTime.Zero, new ScoreTime(1, 8), 101);
        var firstId = first.Id;
        var secondId = second.Id;
        Check(TabRetuningPlan.TryCreate(score, score.Tracks[0], new[] { 62, 57, 52 }, TabRetuningMode.KeepFingering, out var plan, out _), "valid fingering plan rejected");
        plan!.Apply();
        Check(first.StringNumber == 1 && first.Fret == 3 && first.MidiPitch == 65 && first.Velocity == 87 &&
              first.Onset == new ScoreTime(1, 4) && first.Duration == new ScoreTime(1, 2) && first.AllowsBarOverflow && first.Id == firstId,
            "keep-fingering changed authored event metadata");
        Check(second.StringNumber == 2 && second.Fret == 4 && second.MidiPitch == 61 && second.Id == secondId,
            "retuning did not traverse a second section");
        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        Check(restored.Tracks[0].TabTuningMidiPitches.SequenceEqual(new[] { 62, 57, 52 }) &&
              restored.Sections[1].Measures[0].GetTrackMeasure(restored.Tracks[0].Id)!.TabEvents.Single().MidiPitch == 61,
            "retuned score did not round-trip");
        Check(score.Tracks[0].MaxFret == 24, "retuning changed max fret");
    }

    private static void KeepPitchUsesCompleteChordMatching()
    {
        var score = NewScore(new[] { 58, 59 });
        var track = score.Tracks[0];
        var part = score.Sections[0].Measures[0].GetTrackMeasure(track.Id)!;
        var constrained = TabEventEditing.Add(part, track, 2, 1, ScoreTime.Zero, new ScoreTime(1, 4)); // pitch 60: only new string 1
        var flexible = TabEventEditing.Add(part, track, 1, 4, ScoreTime.Zero, new ScoreTime(1, 4)); // pitch 62: strings 1 or 2
        var legacyBeat = part.Voices[0].Beats[0];
        legacyBeat.Notes.Add(new ScoreNote(70, 100));
        var constrainedId = constrained.Id;
        Check(TabRetuningPlan.TryCreate(score, track, new[] { 60, 62 }, TabRetuningMode.KeepPitch, out var plan, out string error), error);
        plan!.Apply();
        Check(constrained.StringNumber == 1 && constrained.Fret == 0 && constrained.MidiPitch == 60,
            "matching did not assign the constrained event");
        Check(flexible.StringNumber == 2 && flexible.Fret == 0 && flexible.MidiPitch == 62,
            "matching greedily consumed the constrained event's only string");
        Check(constrained.Id == constrainedId && legacyBeat.Notes.Single().MidiPitch == 70,
            "keep-pitch changed identity or legacy pitch-only data");
    }

    private static void RejectsImpossibleAndStalePlansAtomically()
    {
        var score = NewScore(new[] { 60, 55 });
        var track = score.Tracks[0];
        var part = score.Sections[0].Measures[0].GetTrackMeasure(track.Id)!;
        var note = TabEventEditing.Add(part, track, 2, 3, ScoreTime.Zero, new ScoreTime(1, 4));
        var originalTuning = track.TabTuningMidiPitches.ToArray();
        Check(!TabRetuningPlan.TryCreate(score, track, new[] { 62 }, TabRetuningMode.KeepFingering, out _, out _),
            "string removal with an out-of-range line was accepted");
        Check(!TabRetuningPlan.TryCreate(score, track, new[] { 120 }, TabRetuningMode.KeepPitch, out _, out _),
            "unplayable pitch was accepted");
        Check(track.TabTuningMidiPitches.SequenceEqual(originalTuning) && note.StringNumber == 2 && note.Fret == 3 && note.MidiPitch == 58,
            "failed plan mutated the score");
        Check(TabRetuningPlan.TryCreate(score, track, new[] { 61, 56 }, TabRetuningMode.KeepPitch, out var stale, out _),
            "valid stale-plan fixture was rejected");
        note.Velocity++;
        try { stale!.Apply(); throw new InvalidOperationException("stale plan was applied"); }
        catch (InvalidOperationException exception) when (exception.Message.Contains("changed after", StringComparison.Ordinal)) { }
        Check(track.TabTuningMidiPitches.SequenceEqual(originalTuning) && note.MidiPitch == 58,
            "stale plan partially mutated the score");
    }

    private static void LegacyNotesAndUnconfiguredTracks()
    {
        var score = new ScoreDocument("Legacy", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        score.AddMeasure(score.Sections[0]);
        var part = score.Sections[0].Measures[0].GetTrackMeasure(track.Id)!;
        var legacy = new ScoreNote(64, 90);
        part.Voices[0].Beats[0].Notes.Add(legacy);
        Check(!TabRetuningPlan.TryCreate(score, track, new[] { 60, 55 }, TabRetuningMode.KeepFingering, out _, out string error) &&
              error.Contains("legacy pitch-only", StringComparison.Ordinal), "keep-fingering accepted sounding legacy notes");
        Check(TabRetuningPlan.TryCreate(score, track, new[] { 60, 55 }, TabRetuningMode.KeepPitch, out var plan, out error), error);
        plan!.Apply();
        Check(track.TabTuningMidiPitches.SequenceEqual(new[] { 60, 55 }) && legacy.MidiPitch == 64,
            "initial tuning change altered legacy pitch-only data");
    }

    private static ScoreDocument NewScore(int[] tuning)
    {
        var score = new ScoreDocument("Retuning", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        track.ConfigureTab(tuning);
        score.AddMeasure(score.Sections[0]);
        return score;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

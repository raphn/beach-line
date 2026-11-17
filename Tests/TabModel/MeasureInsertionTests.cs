using System;
using System.Linq;
using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class MeasureInsertionTests
{
    public static void Run()
    {
        InsertBeforeUsesPrecedingSignatureAndPreservesTarget();
        InsertionUpdatesOnlyPatternsContainingTheInsertionPoint();
        DuplicateCopiesEveryTrackWithIndependentIdentities();
        InvalidIndexesDoNotMutateTheScore();
    }

    private static void InsertBeforeUsesPrecedingSignatureAndPreservesTarget()
    {
        var (score, section, lead, guitar) = CreateScore();
        var source = section.Measures[1];
        source.TimeSignatureOverride = new TimeSignature(3, 4);
        source.KeySignatureOverride = new KeySignature(2);
        var inserted = score.InsertMeasureBefore(section, 1);

        Assert(ReferenceEquals(section.Measures[1], inserted) && ReferenceEquals(section.Measures[2], source), "insert-before position changed");
        Assert(score.ResolveTimeSignature(section, inserted) == new TimeSignature(4, 4), "new measure inherited the target's meter override");
        Assert(inserted.GetTrackMeasure(lead.Id)!.Voices[0].Beats.Count == 4, "empty lead part did not use the inherited meter");
        Assert(source.TimeSignatureOverride == new TimeSignature(3, 4) && source.KeySignatureOverride == new KeySignature(2), "target overrides moved or changed");
        Assert(score.ResolveTimeSignature(section, source) == new TimeSignature(3, 4), "target's effective meter changed after insertion");
        Assert(inserted.GetTrackMeasure(guitar.Id) is not null, "new shared measure omitted a track part");

        var (firstScore, firstSection, _, _) = CreateScore();
        firstSection.DefaultTimeSignature = new TimeSignature(5, 8);
        var first = firstScore.InsertMeasureBefore(firstSection, 0);
        Assert(firstScore.ResolveTimeSignature(firstSection, first) == new TimeSignature(5, 8), "insertion before first measure ignored section meter");

        firstSection.Measures[^1].TimeSignatureOverride = new TimeSignature(7, 8);
        var nextSection = firstScore.AddSection("B", "Next");
        var inherited = firstScore.InsertMeasureBefore(nextSection, 0);
        Assert(firstScore.ResolveTimeSignature(nextSection, inherited) == new TimeSignature(7, 8), "section-start insertion ignored the previous section's effective meter");
    }

    private static void InsertionUpdatesOnlyPatternsContainingTheInsertionPoint()
    {
        var (score, section, _, guitar) = CreateScore();
        var source0 = section.Measures[0].GetTrackMeasure(guitar.Id)!;
        var source1 = section.Measures[1].GetTrackMeasure(guitar.Id)!;
        var destination = section.Measures[2].GetTrackMeasure(guitar.Id)!;
        var inserted = score.InsertMeasureBefore(section, 1);
        Assert(score.GetTrackPatternLength(section, guitar) == 3, "insertion within a source prefix did not grow the pattern");
        Assert(ReferenceEquals(section.Measures[0].GetTrackMeasure(guitar.Id), source0) &&
            ReferenceEquals(section.Measures[2].GetTrackMeasure(guitar.Id), source1) &&
            ReferenceEquals(section.Measures[3].GetTrackMeasure(guitar.Id), destination), "insertion replaced or reordered existing parts");
        Assert(inserted.GetTrackMeasure(guitar.Id)!.Voices[0].Beats.All(beat => beat.Notes.Count == 0), "new source slot was not empty");

        var patternBeforeBoundary = score.GetTrackPatternLength(section, guitar);
        score.InsertMeasureAfter(section, 2); // insertion index 3 is at the pattern boundary
        Assert(score.GetTrackPatternLength(section, guitar) == patternBeforeBoundary, "insertion at pattern boundary changed source length");
        var patternAfterBoundary = score.GetTrackPatternLength(section, guitar);
        score.InsertMeasureBefore(section, patternAfterBoundary);
        Assert(score.GetTrackPatternLength(section, guitar) == patternAfterBoundary, "insertion at pattern boundary grew source length");

        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        Assert(restored.GetTrackPatternLength(restored.Sections[0], restored.Tracks.Single(track => track.Id == guitar.Id)) == patternAfterBoundary,
            "pattern length did not persist after insertions");
    }

    private static void DuplicateCopiesEveryTrackWithIndependentIdentities()
    {
        var (score, section, lead, guitar) = CreateScore();
        guitar.ConfigureTab(new[] { 64, 59, 55, 50, 45, 40 });
        var source = section.Measures[0];
        source.TimeSignatureOverride = new TimeSignature(3, 4);
        source.KeySignatureOverride = new KeySignature(-2, KeyMode.Minor);
        source.IsPickup = true;
        source.IsFreeTime = true;
        source.BarlineStyle = "double";
        source.TempoEvents.Add(new TempoEvent(new ScoreTime(1, 4), new TempoMark(88, new RhythmicValue(4)), TempoInterpolation.Linear));

        var leadPart = source.GetTrackMeasure(lead.Id)!;
        leadPart.Voices[0].Number = 2;
        leadPart.Voices[0].Beats[0].Intensity = 4;
        leadPart.Voices[0].Beats[0].PitchBendSemitones = 1.25m;
        leadPart.Voices[0].Beats[0].Notes.Add(new ScoreNote(65, 100));
        var guitarPart = source.GetTrackMeasure(guitar.Id)!;
        guitarPart.UsesTabEvents = true;
        score.SetTrackMeasureRepeatCount(section, guitar, 0, 1);
        var tabNote = new TabEvent(1, 3, 67, 99, new ScoreTime(1, 8), new ScoreTime(3, 8))
        {
            AllowsBarOverflow = true,
            TupletActual = 3,
            SoundVariation = "pick"
        };
        tabNote.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch, Points = { new ExpressionPoint(0, 0), new ExpressionPoint(1, 1) } });
        guitarPart.TabEvents.Add(tabNote);
        var linkedNote = new TabEvent(2, 4, 63, 90, new ScoreTime(1, 2), new ScoreTime(1, 8));
        linkedNote.Expressions.Add(tabNote.Expressions[0].Clone());
        guitarPart.TabEvents.Add(linkedNote);

        var duplicate = score.DuplicateMeasure(section, 0, before: true);
        Assert(ReferenceEquals(section.Measures[0], duplicate) && ReferenceEquals(section.Measures[1], source), "duplicate-before position changed");
        Assert(duplicate.Id != source.Id && duplicate.TimeSignatureOverride == source.TimeSignatureOverride &&
            duplicate.KeySignatureOverride == source.KeySignatureOverride && duplicate.IsPickup == source.IsPickup &&
            duplicate.IsFreeTime == source.IsFreeTime && duplicate.BarlineStyle == source.BarlineStyle, "measure metadata was not copied");
        Assert(duplicate.TempoEvents.Single().Id != source.TempoEvents.Single().Id &&
            duplicate.TempoEvents.Single().Offset == source.TempoEvents.Single().Offset &&
            duplicate.TempoEvents.Single().Interpolation == source.TempoEvents.Single().Interpolation, "tempo event copy was incomplete or reused identity");

        var copiedLead = duplicate.GetTrackMeasure(lead.Id)!;
        var originalLead = source.GetTrackMeasure(lead.Id)!;
        var copiedBeat = copiedLead.Voices[0].Beats[0];
        var originalBeat = originalLead.Voices[0].Beats[0];
        Assert(copiedLead.RepeatCount == 0 && copiedLead.Voices[0].Id != originalLead.Voices[0].Id &&
            copiedBeat.Id != originalBeat.Id && copiedBeat.Notes.Single().Id != originalBeat.Notes.Single().Id,
            "legacy part identities were reused or repeat count copied");
        Assert(copiedLead.Voices[0].Number == 2 && copiedBeat.Intensity == 4 && copiedBeat.PitchBendSemitones == 1.25m &&
            copiedBeat.Notes.Single().MidiPitch == 65, "legacy beat content was lost");

        var copiedGuitar = duplicate.GetTrackMeasure(guitar.Id)!;
        var copiedTab = copiedGuitar.TabEvents.Single(note => note.StringNumber == 1);
        Assert(copiedGuitar.UsesTabEvents && copiedGuitar.RepeatCount == 0 && copiedTab.Id != tabNote.Id &&
            copiedTab.StringNumber == 1 && copiedTab.Fret == 3 && copiedTab.MidiPitch == 67 && copiedTab.Velocity == 99 &&
            copiedTab.Onset == tabNote.Onset && copiedTab.Duration == tabNote.Duration && copiedTab.AllowsBarOverflow &&
            copiedTab.TupletActual == 3 && copiedTab.SoundVariation == "pick", "tablature event content was not fully copied");
        var copiedLinkedTab = copiedGuitar.TabEvents.Single(note => note.StringNumber == 2);
        Assert(copiedTab.Expressions.Single().Id != tabNote.Expressions.Single().Id &&
            copiedTab.Expressions.Single().Id == copiedLinkedTab.Expressions.Single().Id &&
            copiedTab.Expressions.Single().Points.SequenceEqual(tabNote.Expressions.Single().Points), "expression links were not copied independently from the source");
        copiedTab.Fret = 5;
        copiedTab.MidiPitch = 69;
        copiedBeat.Notes.Single().MidiPitch = 70;
        Assert(tabNote.Fret == 3 && originalBeat.Notes.Single().MidiPitch == 65, "editing the duplicate changed its source");
        Assert(score.GetTrackPatternLength(section, guitar) == 3, "duplicate inside nonlead source prefix did not grow the pattern");

        var afterCopy = score.DuplicateMeasure(section, 3, before: false);
        Assert(ReferenceEquals(section.Measures[4], afterCopy) && score.GetTrackPatternLength(section, guitar) == 3,
            "duplicate-after at pattern boundary changed placement or pattern length");
        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredDuplicate = restored.Sections[0].Measures[0];
        Assert(restoredDuplicate.GetTrackMeasure(guitar.Id)!.TabEvents.Single(note => note.StringNumber == 1).Fret == 5 &&
            restoredDuplicate.TempoEvents.Single().Interpolation == TempoInterpolation.Linear,
            "duplicated content did not persist");
    }

    private static void InvalidIndexesDoNotMutateTheScore()
    {
        var (score, section, _, _) = CreateScore();
        var count = section.Measures.Count;
        ExpectArgumentOutOfRange(() => score.InsertMeasureBefore(section, -1));
        ExpectArgumentOutOfRange(() => score.InsertMeasureBefore(section, count));
        ExpectArgumentOutOfRange(() => score.DuplicateMeasure(section, -1, true));
        ExpectArgumentOutOfRange(() => score.DuplicateMeasure(section, count, false));
        ExpectArgument(() => score.InsertMeasureBefore(new ScoreSection("x", "x"), 0));
        Assert(section.Measures.Count == count, "invalid insertion changed the score");
    }

    private static (ScoreDocument Score, ScoreSection Section, ScoreTrack Lead, ScoreTrack Guitar) CreateScore()
    {
        var score = new ScoreDocument("Measure edits", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var lead = score.AddTrack(new ScoreTrack("Lead", TrackKind.Keyboard));
        var guitar = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        var section = score.Sections[0];
        score.AddMeasure(section);
        score.AddMeasure(section);
        score.AddMeasure(section);
        score.SetTrackPatternLength(section, guitar, 2);
        return (score, section, lead, guitar);
    }

    private static void ExpectArgumentOutOfRange(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Expected an out-of-range argument exception.");
    }

    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected an argument exception.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

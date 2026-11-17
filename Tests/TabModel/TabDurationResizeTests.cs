using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class TabDurationResizeTests
{
    public static void Run()
    {
        ResizesDifferentDurationsByOneExactDelta();
        RejectsInvalidBatchAtomically();
        DeduplicatesLinkedOccurrences();
        HistoryUndoRedoRoundTripsResize();
        Console.WriteLine("PASS atomic batch duration resize, deduplication and history round-trip");
    }

    private static void ResizesDifferentDurationsByOneExactDelta()
    {
        var (part, track) = NewPart();
        var shortNote = TabEventEditing.Add(part, track, 1, 1, ScoreTime.Zero, new ScoreTime(1, 8));
        var longNote = TabEventEditing.Add(part, track, 2, 2, ScoreTime.Zero, new ScoreTime(1, 2));
        Check(TabEventEditing.ResizeSelection(new[] { (part, shortNote), (part, longNote) }, new ScoreTime(1, 16)), "Changed batch should report a change.");
        Check(shortNote.Duration == new ScoreTime(3, 16) && longNote.Duration == new ScoreTime(9, 16),
            "Batch resize must add the same exact delta to differing authored durations.");
    }

    private static void RejectsInvalidBatchAtomically()
    {
        var (part, track) = NewPart();
        var first = TabEventEditing.Add(part, track, 1, 1, ScoreTime.Zero, new ScoreTime(1, 4));
        var second = TabEventEditing.Add(part, track, 2, 2, ScoreTime.Zero, new ScoreTime(1, 8));
        var detached = new TabEvent(1, 0, 60, 90, ScoreTime.Zero, new ScoreTime(1, 4));
        try
        {
            TabEventEditing.ResizeSelection(new[] { (part, first), (part, second), (part, detached) }, new ScoreTime(-1, 8));
            throw new InvalidOperationException("A batch containing a nonmember event must be rejected.");
        }
        catch (ArgumentException) { }
        Check(first.Duration == new ScoreTime(1, 4) && second.Duration == new ScoreTime(1, 8),
            "Rejected batch must leave every event unchanged.");

        try
        {
            TabEventEditing.ResizeSelection(new[] { (part, first), (part, second) }, new ScoreTime(-1, 8));
            throw new InvalidOperationException("A batch producing a zero duration must be rejected.");
        }
        catch (ArgumentOutOfRangeException) { }
        Check(first.Duration == new ScoreTime(1, 4) && second.Duration == new ScoreTime(1, 8),
            "Invalid resulting duration must leave the whole batch unchanged.");
    }

    private static void DeduplicatesLinkedOccurrences()
    {
        var (part, track) = NewPart();
        var note = TabEventEditing.Add(part, track, 1, 1, ScoreTime.Zero, new ScoreTime(1, 4));
        TabEventEditing.ResizeSelection(new[] { (part, note), (part, note), (part, note) }, new ScoreTime(1, 8));
        Check(note.Duration == new ScoreTime(3, 8), "Repeated linked occurrences must resize their shared source event once.");
    }

    private static void HistoryUndoRedoRoundTripsResize()
    {
        var score = new ScoreDocument("Resize", new TempoMark(100, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        track.ConfigureTab(new[] { 60, 55 });
        score.AddMeasure(score.Sections[0]);
        var part = score.Sections[0].Measures[0].GetTrackMeasure(track.Id)!;
        var first = TabEventEditing.Add(part, track, 1, 1, ScoreTime.Zero, new ScoreTime(1, 4));
        var second = TabEventEditing.Add(part, track, 2, 2, ScoreTime.Zero, new ScoreTime(1, 2));
        var firstId = first.Id;
        var secondId = second.Id;
        var history = new ScoreHistory();
        history.Reset(ScoreFile.Serialize(score));

        Check(TabEventEditing.ResizeSelection(new[] { (part, first), (part, second) }, new ScoreTime(1, 8)), "Changed resize should report a change.");
        history.Record(ScoreFile.Serialize(score));
        Check(history.TryUndo(out var state), "Resize should create an undo state.");
        var undone = ScoreFile.Deserialize(state);
        Check(Durations(undone, track.Id).SequenceEqual(new[] { new ScoreTime(1, 4), new ScoreTime(1, 2) }),
            "Undo must restore both original durations after serialization.");
        Check(history.TryRedo(out state), "Resize should be redoable.");
        var redone = ScoreFile.Deserialize(state);
        var restoredNotes = redone.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.TabEvents;
        Check(Durations(redone, track.Id).SequenceEqual(new[] { new ScoreTime(3, 8), new ScoreTime(5, 8) }) &&
              restoredNotes[0].Id == firstId && restoredNotes[1].Id == secondId,
            "Redo must restore the resized durations and event identities after serialization.");
    }

    private static ScoreTime[] Durations(ScoreDocument score, Guid trackId) =>
        score.Sections[0].Measures[0].GetTrackMeasure(trackId)!.TabEvents.Select(note => note.Duration).ToArray();

    private static (TrackMeasure Part, ScoreTrack Track) NewPart()
    {
        var score = new ScoreDocument("Resize", new TempoMark(100, new RhythmicValue(4)), new TimeSignature(4, 4));
        var track = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        track.ConfigureTab(new[] { 60, 55 });
        score.AddMeasure(score.Sections[0]);
        return (score.Sections[0].Measures[0].GetTrackMeasure(track.Id)!, track);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

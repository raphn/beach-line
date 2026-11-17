using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class ClipboardTests
{
    public static void Run()
    {
        OccurrenceCopy();
        CopyAnchorUsesEarliestTopLine();
        MultiNotePastePreservesLines();
        PasteConfirmationAndTuning();
        ExternalConflictsAreAtomic();
        LegacyTargetIsRejected();
        MissingStringsAndOverflow();
        ExpressionsKeepLinkedIdentityAndDeepCopyOnPaste();
        ChordCreationAndHistory();
    }

    private static (ScoreTrack Track, TrackMeasure A, TrackMeasure B) Parts(int strings = 3)
    {
        var track = new ScoreTrack("Guitar", TrackKind.Guitar);
        track.ConfigureTab(new[] { 64, 59, 55 }.Take(strings));
        return (track, new TrackMeasure(track.Id), new TrackMeasure(track.Id));
    }

    private static void ExpressionsKeepLinkedIdentityAndDeepCopyOnPaste()
    {
        var (track, source, _) = Parts();
        var note = TabEventEditing.Add(source, track, 1, 3, ScoreTime.Zero, new ScoreTime(1, 4));
        var expression = new NoteExpression { Kind = ExpressionKind.Pitch,
            Points = new() { new ExpressionPoint(0, 0), new ExpressionPoint(1, 1) } };
        note.Expressions.Add(expression);
        var clip = TabClipboard.Copy(track, source, new[] { note }, 1, ScoreTime.Zero);
        var destination = new TrackMeasure(track.Id);
        var pasted = clip.PlanPaste(track, destination, 1, new ScoreTime(1, 2)).Apply().Single();
        var pastedExpression = pasted.Expressions.Single();
        Check(pastedExpression.Id == expression.Id && !ReferenceEquals(pastedExpression, expression), "paste did not retain curve identity with an independent definition object");
        Check(!ReferenceEquals(pastedExpression.Points, expression.Points) && pastedExpression.Sample(.5) == .5,
            "paste did not deep-copy and preserve the expression curve");
        pastedExpression.Points[1] = new ExpressionPoint(1, 2);
        Check(expression.Sample(.5) == .5, "editing the pasted definition changed the source definition");
        Console.WriteLine("PASS expression clipboard identity and deep copy");
    }

    private static void ChordCreationAndHistory()
    {
        var (track, _, target) = Parts();
        var exactDuration = new ScoreTime(5, 16);
        var chord = TabClipboard.CreateChord(track, new[] { (3, 7), (1, 4), (2, 9) }, exactDuration);
        // The target anchor is the chord's minimum string, independent of input/click order.
        foreach (var assignments in new[] { new[] { (3, 7), (1, 4), (2, 9) }, new[] { (2, 9), (3, 7), (1, 4) } })
        {
            var plan = TabClipboard.CreateChord(track, assignments, exactDuration)
                .PlanPaste(track, new TrackMeasure(track.Id), assignments.Min(n => n.Item1), new ScoreTime(1, 8));
            Check(plan.Entries.Select(e => e.StringNumber).Order().SequenceEqual(new[] { 1, 2, 3 }),
                "chord string positions changed with input/click order");
            Check(plan.Entries.All(e => e.Onset == new ScoreTime(1, 8) && e.Duration == exactDuration &&
                e.Velocity == Beat.DefaultMidiVelocity && e.TupletActual == 0), "chord timing/default note metadata is incorrect");
        }
        var bass = new ScoreTrack("Bass", TrackKind.Bass);
        bass.ConfigureTab(new[] { 40, 35, 30 });
        var bassChord = TabClipboard.CreateChord(bass, new[] { (1, 2), (2, 4) }, exactDuration);
        Check(bassChord.PlanPaste(bass, new TrackMeasure(bass.Id), 1, ScoreTime.Zero).Entries.All(e => e.Velocity == Beat.MidiVelocityForIntensity(4)),
            "new bass chords should use intensity 4");
        var unison = TabClipboard.CreateChord(track, new[] { (1, 0), (2, 5) }, new ScoreTime(1, 4));
        Check(unison.PlanPaste(track, new TrackMeasure(track.Id), 1, ScoreTime.Zero).Entries.Select(e => e.MidiPitch).Distinct().Count() == 1,
            "a unison on separate strings was collapsed");
        Reject(() => TabClipboard.CreateChord(track, new[] { (1, 1), (1, 2) }, new ScoreTime(1, 4)));
        Reject(() => TabClipboard.CreateChord(track, new[] { (1, 1) }, new ScoreTime(1, 4)));
        Reject(() => TabClipboard.CreateChord(track, new[] { (1, 1), (4, 2) }, new ScoreTime(1, 4)));
        Reject(() => TabClipboard.CreateChord(track, new[] { (1, 70), (2, 2) }, new ScoreTime(1, 4)));
        Reject(() => TabClipboard.CreateChord(track, new[] { (1, 1), (2, 2) }, ScoreTime.Zero));

        var score = new ScoreDocument("Chord history", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        score.AddTrack(track);
        var scorePart = score.AddMeasure(score.Sections[0]).GetTrackMeasure(track.Id)!;
        var history = new ScoreHistory();
        history.Reset(ScoreFile.Serialize(score));
        var occupied = TabEventEditing.Add(scorePart, track, 1, 0, new ScoreTime(1, 8), new ScoreTime(1, 8));
        var conflictPlan = chord.PlanPaste(track, scorePart, 1, new ScoreTime(1, 8));
        Check(conflictPlan.RequiresConfirmation && conflictPlan.ConflictingEventIds.Contains(occupied.Id), "chord overwrite did not require confirmation");
        Reject(() => conflictPlan.Apply());
        Check(scorePart.TabEvents.Count == 1, "rejected chord confirmation mutated the score");
        var beforeApply = ScoreFile.Serialize(score);
        conflictPlan.Apply(true);
        history.Reset(beforeApply);
        history.Record(ScoreFile.Serialize(score));
        Check(scorePart.TabEvents.Count == 3, "confirmed chord was not applied");
        Check(history.TryUndo(out var restored) && ScoreFile.Deserialize(restored).Sections[0].Measures[0]
            .GetTrackMeasure(track.Id)!.TabEvents.Single().Id == occupied.Id, "undo did not restore the replaced event");
        Check(history.TryRedo(out restored) && ScoreFile.Deserialize(restored).Sections[0].Measures[0]
            .GetTrackMeasure(track.Id)!.TabEvents.Count == 3, "redo did not restore the chord");

        var legacyTarget = new TrackMeasure(track.Id);
        legacyTarget.Voices.Add(new Voice());
        legacyTarget.Voices[0].Beats.Add(new Beat(new RhythmicValue(4)));
        legacyTarget.Voices[0].Beats[0].AddNote(new ScoreNote(64, 80));
        try { chord.PlanPaste(track, legacyTarget, 1, ScoreTime.Zero); throw new Exception("legacy chord target was accepted"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Convert legacy notes", StringComparison.Ordinal)) { }
        Console.WriteLine("PASS chord clipboard anchoring, validation, confirmation, and history");
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return; }
        throw new Exception("An invalid chord was accepted");
    }

    private static void OccurrenceCopy()
    {
        var (track, a, b) = Parts();
        var early = TabEventEditing.Add(a, track, 2, 5, new ScoreTime(3, 4), new ScoreTime(1, 2), 91);
        var current = TabEventEditing.Add(b, track, 1, 2, new ScoreTime(1, 8), new ScoreTime(1, 4), 88);
        var clipboard = TabClipboard.Copy(track, new[]
        {
            new TabCopySource(a, early, new ScoreTime(7, 4)),
            new TabCopySource(b, current, new ScoreTime(17, 8)),
            new TabCopySource(a, early, new ScoreTime(11, 4))
        }, 2, new ScoreTime(2, 1));
        var plan = clipboard.PlanPaste(track, new TrackMeasure(track.Id), 2, new ScoreTime(1, 4));
        Check(plan.Entries.Count == 3, "copy did not preserve each visible occurrence");
        Check(plan.Entries[0].Onset == new ScoreTime(1, 4) && plan.Entries[0].StringNumber == 2 && plan.Entries[0].Duration == new ScoreTime(1, 2), "multi-note paste changed the original line or timing");
        Check(plan.Entries[1].Onset == new ScoreTime(5, 8) && plan.Entries[1].StringNumber == 1, "multi-note paste changed the original line or retained timing");
        Check(plan.Entries[2].Onset == new ScoreTime(5, 4), "repeated source event occurrence was collapsed");
        Console.WriteLine("PASS occurrence-based clipboard copy");
    }

    private static void PasteConfirmationAndTuning()
    {
        var (track, source, target) = Parts();
        var original = TabEventEditing.Add(source, track, 1, 3, ScoreTime.Zero, new ScoreTime(1, 2), 100);
        var clipboard = TabClipboard.Copy(track, source, new[] { original }, 1, ScoreTime.Zero);
        var occupied = TabEventEditing.Add(target, track, 1, 7, ScoreTime.Zero, new ScoreTime(1, 4));
        var plan = clipboard.PlanPaste(track, target, 1, ScoreTime.Zero, new ScoreTime(1, 8), new ScoreTime(1, 1));
        Check(plan.Entries.Single().Onset == new ScoreTime(1, 4) && !plan.RequiresConfirmation, "single-note search did not advance past the occupied duration");
        var tuningTarget = new TrackMeasure(track.Id);
        var tuningPlan = clipboard.PlanPaste(track, tuningTarget, 2, ScoreTime.Zero);
        Check(tuningPlan.Entries.Single().MidiPitch == 62, "destination tuning did not determine pitch");
        var adjacent = clipboard.PlanPaste(track, target, 1, new ScoreTime(1, 4), visibleBarDuration: new ScoreTime(1, 1));
        Check(adjacent.Entries.Single().Onset == new ScoreTime(1, 4), "half-open adjacent note intervals were treated as overlapping");

        var second = TabEventEditing.Add(source, track, 2, 4, ScoreTime.Zero, new ScoreTime(1, 4));
        var multi = TabClipboard.Copy(track, source, new[] { original, second }, 1, ScoreTime.Zero);
        var multiTarget = new TrackMeasure(track.Id);
        var conflict = TabEventEditing.Add(multiTarget, track, 1, 9, ScoreTime.Zero, new ScoreTime(1, 8));
        var multiPlan = multi.PlanPaste(track, multiTarget, 1, ScoreTime.Zero);
        Check(multiPlan.RequiresConfirmation && multiPlan.ConflictingEventIds.Contains(conflict.Id), "multi-note replacement was not surfaced for confirmation");
        try { multiPlan.Apply(); throw new InvalidOperationException("unconfirmed replacement was applied"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("requires confirmation", StringComparison.Ordinal)) { }
        Check(multiTarget.TabEvents.Contains(conflict) && target.TabEvents.Contains(occupied), "canceling a paste changed destination data");
        multiPlan.Apply(true);
        Check(!multiTarget.TabEvents.Contains(conflict) && target.TabEvents.Contains(occupied), "confirmed multi-note replacement removed the wrong notes");
        Console.WriteLine("PASS paste confirmation, collision search, and destination tuning");
    }

    private static void CopyAnchorUsesEarliestTopLine()
    {
        var (track, source, _) = Parts();
        var middle = TabEventEditing.Add(source, track, 2, 4, new ScoreTime(1, 4), new ScoreTime(1, 8));
        var lower = TabEventEditing.Add(source, track, 3, 7, new ScoreTime(1, 4), new ScoreTime(1, 8));
        var top = TabEventEditing.Add(source, track, 1, 5, new ScoreTime(1, 2), new ScoreTime(1, 8));
        // The earliest onset anchors time, while each selected note keeps its source line.
        var clipboard = TabClipboard.Copy(track, new[]
        {
            new TabCopySource(source, lower, lower.Onset),
            new TabCopySource(source, middle, middle.Onset),
            new TabCopySource(source, top, top.Onset)
        }, 3, lower.Onset);
        var plan = clipboard.PlanPaste(track, new TrackMeasure(track.Id), 1, ScoreTime.Zero);
        Check(plan.Entries[0].StringNumber == 3 && plan.Entries[0].Onset == ScoreTime.Zero,
            "The earliest onset was not used as the horizontal paste anchor.");
        Check(plan.Entries[1].StringNumber == 2 && plan.Entries[1].Onset == ScoreTime.Zero,
            "A tied earliest onset did not retain its relative string position.");
        Check(plan.Entries[2].StringNumber == 1 && plan.Entries[2].Onset == new ScoreTime(1, 4),
            "The topmost selected line was not preserved.");
        Console.WriteLine("PASS deterministic earliest-onset copy anchor");
    }

    private static void MultiNotePastePreservesLines()
    {
        var (sourceTrack, source, _) = Parts();
        var lineOne = TabEventEditing.Add(source, sourceTrack, 1, 3, ScoreTime.Zero, new ScoreTime(1, 8));
        var lineThree = TabEventEditing.Add(source, sourceTrack, 3, 5, new ScoreTime(1, 4), new ScoreTime(1, 8));
        var clipboard = TabClipboard.Copy(sourceTrack, source, new[] { lineOne, lineThree }, 1, ScoreTime.Zero);

        var destinationTrack = new ScoreTrack("Destination guitar", TrackKind.Guitar);
        destinationTrack.ConfigureTab(new[] { 70, 62, 50 });
        var destination = new TrackMeasure(destinationTrack.Id);
        var multi = clipboard.PlanPaste(destinationTrack, destination, 2, new ScoreTime(1, 2));
        Check(multi.Entries.Select(entry => entry.StringNumber).Order().SequenceEqual(new[] { 1, 3 }),
            "multi-note paste followed the hovered line instead of preserving source lines");
        Check(multi.Entries.Single(entry => entry.StringNumber == 1).MidiPitch == 73 &&
            multi.Entries.Single(entry => entry.StringNumber == 3).MidiPitch == 55 &&
            multi.Entries.Single(entry => entry.StringNumber == 3).Onset == new ScoreTime(3, 4),
            "destination tuning or horizontal offsets were not applied to preserved lines");

        var shortTrack = new ScoreTrack("Short guitar", TrackKind.Guitar);
        shortTrack.ConfigureTab(new[] { 65, 60 });
        var shortTarget = new TrackMeasure(shortTrack.Id);
        var missingLine = clipboard.PlanPaste(shortTrack, shortTarget, 2, ScoreTime.Zero);
        Check(missingLine.RequiresConfirmation && missingLine.DroppedClipIndices.Count == 1 &&
            missingLine.Entries.Single().StringNumber == 1,
            "a source line absent from the destination was not reported as dropped");

        var drums = new ScoreTrack("Drums", TrackKind.Percussion);
        drums.AddPercussionPiece("kick", "Kick", 36);
        drums.AddPercussionPiece("snare", "Snare", 38);
        drums.AddPercussionPiece("hat", "Hat", 42);
        var drumSource = new TrackMeasure(drums.Id);
        var kick = TabEventEditing.Add(drumSource, drums, 1, 0, ScoreTime.Zero, new ScoreTime(1, 8));
        var hat = TabEventEditing.Add(drumSource, drums, 3, 0, new ScoreTime(1, 4), new ScoreTime(1, 8));
        var drumClip = TabClipboard.Copy(drums, drumSource, new[] { kick, hat }, 1, ScoreTime.Zero);
        var drumTarget = new TrackMeasure(drums.Id);
        var drumPlan = drumClip.PlanPaste(drums, drumTarget, 2, new ScoreTime(1, 2));
        Check(drumPlan.Entries.Select(entry => entry.StringNumber).Order().SequenceEqual(new[] { 1, 3 }) &&
            drumPlan.Entries.Single(entry => entry.StringNumber == 3).MidiPitch == 42,
            "percussion paste did not preserve source lane identity and fixed pitch");

        var singleClip = TabClipboard.Copy(sourceTrack, source, new[] { lineThree }, 3, lineThree.Onset);
        var singlePlan = singleClip.PlanPaste(destinationTrack, new TrackMeasure(destinationTrack.Id), 2, ScoreTime.Zero);
        Check(singlePlan.Entries.Single().StringNumber == 2,
            "single-note paste no longer follows the hovered line");
        Console.WriteLine("PASS multi-note source lines, destination tuning, missing lines, percussion, and single-note targeting");
    }

    private static void ExternalConflictsAreAtomic()
    {
        var (track, source, target) = Parts();
        var note = TabEventEditing.Add(source, track, 1, 2, ScoreTime.Zero, new ScoreTime(1, 4));
        var second = TabEventEditing.Add(source, track, 2, 3, new ScoreTime(1, 4), new ScoreTime(1, 4));
        var clipboard = TabClipboard.Copy(track, source, new[] { note, second }, 1, ScoreTime.Zero);
        var external = new TrackMeasure(track.Id);
        var existing = TabEventEditing.Add(external, track, 1, 6, ScoreTime.Zero, new ScoreTime(1, 2));
        var plan = clipboard.PlanPaste(track, target, 1, ScoreTime.Zero, otherOccupied: new[] { new TabPasteOccupied(external, existing, new ScoreTime(-1, 8), new ScoreTime(1, 4)) });
        Check(plan.RequiresConfirmation && plan.ConflictingEventIds.Contains(existing.Id), "resolved cross-measure conflict was not reported");
        existing.Onset = new ScoreTime(3, 4);
        try { plan.Apply(true); throw new InvalidOperationException("stale external plan was applied"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("timeline conflict changed", StringComparison.Ordinal)) { }
        Check(external.TabEvents.Contains(existing) && target.TabEvents.Count == 0, "stale external conflict partially mutated measures");

        var single = TabClipboard.Copy(track, source, new[] { note }, 1, ScoreTime.Zero);
        var continuation = new TrackMeasure(track.Id);
        var crossing = TabEventEditing.Add(continuation, track, 1, 7, ScoreTime.Zero, new ScoreTime(1, 2));
        var continued = single.PlanPaste(track, target, 1, new ScoreTime(1, 8), new ScoreTime(1, 8), new ScoreTime(1, 1),
            new[] { new TabPasteOccupied(continuation, crossing, new ScoreTime(-1, 8), new ScoreTime(1, 2)) });
        Check(continued.Entries.Single().Onset == new ScoreTime(3, 8), "single-note search did not skip a prior-measure continuation interval");
        Console.WriteLine("PASS external conflict preflight, continuation overlap, and atomicity");
    }

    private static void LegacyTargetIsRejected()
    {
        var (track, source, target) = Parts();
        var note = TabEventEditing.Add(source, track, 1, 1, ScoreTime.Zero, new ScoreTime(1, 4));
        var clipboard = TabClipboard.Copy(track, source, new[] { note }, 1, ScoreTime.Zero);
        target.Voices.Add(new Voice());
        target.Voices[0].Beats.Add(new Beat(new RhythmicValue(4)));
        target.Voices[0].Beats[0].AddNote(new ScoreNote(64, 80));
        try { clipboard.PlanPaste(track, target, 1, ScoreTime.Zero); throw new InvalidOperationException("legacy notes were silently hidden"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Convert legacy notes", StringComparison.Ordinal)) { }
        Check(target.TabEvents.Count == 0 && !target.UsesTabEvents, "legacy target changed during rejection");
        Console.WriteLine("PASS legacy target guard");
    }

    private static void MissingStringsAndOverflow()
    {
        var (track, source, target) = Parts();
        var anchor = TabEventEditing.Add(source, track, 1, 3, ScoreTime.Zero, new ScoreTime(1, 4));
        var upper = TabEventEditing.Add(source, track, 3, 5, new ScoreTime(1, 2), new ScoreTime(1, 4));
        var clipboard = TabClipboard.Copy(track, source, new[] { anchor, upper }, 1, ScoreTime.Zero);
        var shortTrack = new ScoreTrack("Short guitar", TrackKind.Guitar);
        shortTrack.ConfigureTab(new[] { 64, 59 });
        var shortTarget = new TrackMeasure(shortTrack.Id);
        var plan = clipboard.PlanPaste(shortTrack, shortTarget, 2, new ScoreTime(3, 4), visibleBarDuration: new ScoreTime(1, 1));
        Check(plan.RequiresConfirmation && plan.DroppedClipIndices.Count == 1 && plan.PasteCount == 1,
            "An unavailable string was not reported before pasting.");
        try { plan.Apply(); throw new InvalidOperationException("A missing-string paste was applied without confirmation."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("requires confirmation", StringComparison.Ordinal)) { }
        Check(shortTarget.TabEvents.Count == 0, "Canceling the missing-string paste changed the destination.");
        var inserted = plan.Apply(true);
        Check(inserted.Count == 1 && inserted[0].Fret == 3 && inserted[0].StringNumber == 1 && !inserted[0].AllowsBarOverflow,
            "The remaining note did not retain its original string line and selected timing.");

        var overflowTarget = new TrackMeasure(track.Id);
        var overflowPlan = clipboard.PlanPaste(track, overflowTarget, 1, new ScoreTime(3, 4), visibleBarDuration: new ScoreTime(1, 1));
        var overflowNotes = overflowPlan.Apply();
        Check(overflowNotes.Count == 2 && overflowNotes.Single(note => note.Fret == 5).Onset == new ScoreTime(5, 4) &&
            overflowNotes.Single(note => note.Fret == 5).AllowsBarOverflow,
            "A copied group did not retain its overflow onset in the destination measure.");
        var score = new ScoreDocument("Clipboard", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        score.AddTrack(track);
        var scorePart = score.AddMeasure(score.Sections[0]).GetTrackMeasure(track.Id)!;
        scorePart.UsesTabEvents = true;
        scorePart.TabEvents.AddRange(overflowNotes);
        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredNotes = restored.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.TabEvents;
        Check(restoredNotes.Single(note => note.Fret == 5).AllowsBarOverflow &&
            restoredNotes.Single(note => note.Fret == 5).Onset == new ScoreTime(5, 4),
            "Saving and loading lost the explicit overflow and its owner-relative onset.");
        Console.WriteLine("PASS missing-string confirmation and owned overflow");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

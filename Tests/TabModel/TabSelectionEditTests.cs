using OpenTab.Music;

internal static class TabSelectionEditTests
{
    public static void Run()
    {
        AtomicMoveAndDelete();
        ConfirmationAndStalePlans();
        ProjectedRepeatConflicts();
        GroupFretAndStringLimits();
        Console.WriteLine("PASS atomic selection move/delete, confirmation, stale rejection, and linked interval cutoffs");
    }

    private static void GroupFretAndStringLimits()
    {
        var (track, part, first, second) = Create();
        var fretUp = new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, first.StringNumber, first.Onset, first.Fret + 1),
            new TabSelectionMove(part, second, second.StringNumber, second.Onset, second.Fret + 1)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false);
        fretUp.Apply();
        Check(first.Fret == 5 && second.Fret == 8 && first.MidiPitch == 69 && second.MidiPitch == 67,
            "group fret shift did not preserve offsets or recalculate pitches");
        new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, first.StringNumber, first.Onset, 0),
            new TabSelectionMove(part, second, second.StringNumber, second.Onset, 24)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false).Apply();
        Check(first.Fret == 0 && second.Fret == 24, "endpoint frets were not accepted");
        Reject(() => new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, first.StringNumber, first.Onset, 1),
            new TabSelectionMove(part, second, second.StringNumber, second.Onset, 25)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false));
        Reject(() => new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, first.StringNumber, first.Onset, -1),
            new TabSelectionMove(part, second, second.StringNumber, second.Onset, 23)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false));
        Reject(() => new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, 0, first.Onset),
            new TabSelectionMove(part, second, 1, second.Onset)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false));
        Reject(() => new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, 3, first.Onset),
            new TabSelectionMove(part, second, 4, second.Onset)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false));
        Check(first.Fret == 0 && second.Fret == 24 && first.StringNumber == 1 && second.StringNumber == 2,
            "out-of-range group edits partially changed the selection");
        new TabSelectionEditPlan(track, new[]
        {
            new TabSelectionMove(part, first, 2, first.Onset),
            new TabSelectionMove(part, second, 3, second.Onset)
        }, Array.Empty<(TrackMeasure, TabEvent)>(), false).Apply();
        Check(first.StringNumber == 2 && second.StringNumber == 3 && first.MidiPitch == 59 && second.MidiPitch == 79,
            "group string shift did not recalculate pitches for the destination tuning");
    }

    private static (ScoreTrack Track, TrackMeasure Part, TabEvent First, TabEvent Second) Create()
    {
        var track = new ScoreTrack("Guitar", TrackKind.Guitar);
        track.ConfigureTab(new[] { 64, 59, 55 });
        var part = new TrackMeasure(track.Id);
        var first = TabEventEditing.Add(part, track, 1, 4, ScoreTime.Zero, new ScoreTime(1, 2), 81);
        var second = TabEventEditing.Add(part, track, 2, 7, new ScoreTime(1, 4), new ScoreTime(1, 8), 93);
        return (track, part, first, second);
    }

    private static void AtomicMoveAndDelete()
    {
        var (track, part, first, second) = Create();
        var delta = new ScoreTime(1, 8);
        var targets = new[] { new TabSelectionMove(part, first, 2, first.Onset + delta), new TabSelectionMove(part, second, 3, second.Onset + delta) };
        var plan = new TabSelectionEditPlan(track, targets, Array.Empty<(TrackMeasure, TabEvent)>(), false);
        Check(first.Onset == ScoreTime.Zero && second.StringNumber == 2, "planning mutated the score");
        plan.Apply();
        Check(first.Onset == delta && second.Onset == new ScoreTime(3, 8) && first.StringNumber == 2 && second.StringNumber == 3, "move did not preserve a uniform offset");
        Check(first.Duration == new ScoreTime(1, 2) && first.Velocity == 81 && first.Fret == 4 && first.MidiPitch == 63, "move lost authored properties or tuning");
        var noOp = new TabSelectionEditPlan(track, new[] { new TabSelectionMove(part, first, first.StringNumber, first.Onset) }, Array.Empty<(TrackMeasure, TabEvent)>(), false);
        noOp.Apply();
        Check(part.TabEvents.Count == 2, "no-op lost notes");
        new TabSelectionEditPlan(track, Array.Empty<TabSelectionMove>(), new[] { (part, first), (part, second) }, false).Apply();
        Check(part.TabEvents.Count == 0, "group deletion was incomplete");
    }

    private static void ConfirmationAndStalePlans()
    {
        var (track, part, first, second) = Create();
        var plan = new TabSelectionEditPlan(track, new[] { new TabSelectionMove(part, first, 2, second.Onset) }, new[] { (part, second) }, true);
        Reject(() => plan.Apply());
        Check(part.TabEvents.Count == 2 && first.Onset == ScoreTime.Zero, "unconfirmed replacement mutated notes");
        plan.Apply(true);
        Check(part.TabEvents.Count == 1 && ReferenceEquals(part.TabEvents[0], first) && first.Onset == new ScoreTime(1, 4), "confirmed replacement failed");
        var stale = new TabSelectionEditPlan(track, new[] { new TabSelectionMove(part, first, 3, new ScoreTime(1, 2)) }, Array.Empty<(TrackMeasure, TabEvent)>(), true);
        first.Fret++;
        Reject(() => stale.Apply(true));
        Check(first.StringNumber == 2 && first.Onset == new ScoreTime(1, 4), "stale plan partially moved notes");
        Reject(() => new TabSelectionEditPlan(track, new[] { new TabSelectionMove(part, first, 1, ScoreTime.Zero), new TabSelectionMove(part, first, 4, ScoreTime.Zero) }, Array.Empty<(TrackMeasure, TabEvent)>(), false));
        Check(first.StringNumber == 2, "invalid group partially mutated notes");
    }

    private static void ProjectedRepeatConflicts()
    {
        var (_, _, first, second) = Create();
        var external = new TabEvent(1, 0, 64, 96, ScoreTime.Zero, new ScoreTime(1, 8));
        var selected = new[]
        {
            new TabSelectionInterval(first, 1, ScoreTime.Zero, new ScoreTime(1, 1)),
            new TabSelectionInterval(second, 1, new ScoreTime(1, 4), new ScoreTime(3, 8)),
            new TabSelectionInterval(first, 1, new ScoreTime(1, 1), new ScoreTime(2, 1)),
            new TabSelectionInterval(second, 1, new ScoreTime(5, 4), new ScoreTime(11, 8))
        };
        Check(TabSelectionEditPlan.FindConflicts(selected, new[] { new TabSelectionInterval(external, 1, new ScoreTime(1, 2), new ScoreTime(5, 8)) }).Count == 0,
            "raw duration ignored the cutoff from the next selected attack");
        Check(TabSelectionEditPlan.FindConflicts(selected, new[] { new TabSelectionInterval(external, 1, new ScoreTime(9, 8), new ScoreTime(5, 4)) }).Single() == external,
            "conflict at a linked repeat was missed");
        Reject(() => TabSelectionEditPlan.FindConflicts(selected.Concat(new[] { selected[0] }).ToArray(), Array.Empty<TabSelectionInterval>()));
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return; }
        throw new Exception("An invalid selection edit was accepted");
    }
}

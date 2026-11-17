using OpenTab.Music;
using OpenTab.Music.Persistence;

internal static class TransitionModelTests
{
    public static void Run()
    {
        var score = new ScoreDocument("Transitions", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
        var guitar = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
        var drums = score.AddTrack(new ScoreTrack("Drums", TrackKind.Percussion));
        var sectionA = score.Sections[0];
        score.AddMeasure(sectionA);
        var sectionB = score.AddSection("B", "Next");
        var destination = sectionA.Measures[0];
        var guitarOriginal = destination.GetTrackMeasure(guitar.Id)!;
        var drumOriginal = destination.GetTrackMeasure(drums.Id)!;
        var guitarVersion = score.AddTrackMeasureVersion(sectionA, destination, guitar, "Fill", guitarOriginal);
        var drumVersion = score.AddTrackMeasureVersion(sectionA, destination, drums, "Crash", drumOriginal);

        score.SetTrackMeasureTransitionRule(sectionA, destination, guitar, sectionB.Id, guitarVersion.Id);
        score.SetTrackMeasureTransitionRule(sectionA, destination, drums, sectionB.Id, drumVersion.Id);
        score.SetTrackMeasureAlternation(sectionA, destination, guitar, new[] { Guid.Empty, guitarVersion.Id });
        score.SetTrackMeasureAlternation(sectionA, destination, drums, new[] { drumVersion.Id, Guid.Empty });
        Assert(destination.TransitionRules.Count == 2 && destination.Alternations.Count == 2,
            "Rules and sequences for different tracks must coexist on the same slot.");

        var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
        var restoredSlot = restored.Sections[0].Measures[0];
        var restoredGuitar = restored.Tracks.Single(track => track.Id == guitar.Id);
        var restoredDrums = restored.Tracks.Single(track => track.Id == drums.Id);
        Assert(restoredSlot.TrackMeasureVersions.Count == 2, "Named versions did not round-trip.");
        Assert(restoredSlot.TransitionRules.Count == 2 && restoredSlot.Alternations.Count == 2,
            "Per-track transition rules or alternations did not round-trip.");
        Assert(restoredSlot.Alternations.Single(item => item.TrackId == guitar.Id).VersionIds.SequenceEqual(new[] { Guid.Empty, guitarVersion.Id }),
            "The original-content sentinel or order was not preserved.");

        var copied = restored.DuplicateMeasure(restored.Sections[0], 0, before: false);
        Assert(copied.TrackMeasureVersions.Count == 2 && copied.TrackMeasureVersions.All(item => item.Id != guitarVersion.Id && item.Id != drumVersion.Id),
            "Duplicating a slot must assign fresh version identities.");
        var copiedGuitarVersion = copied.TrackMeasureVersions.Single(item => item.TrackId == guitar.Id);
        Assert(copied.Alternations.Single(item => item.TrackId == guitar.Id).VersionIds[1] == copiedGuitarVersion.Id,
            "Duplicating a slot must remap alternation references.");
        Assert(copied.TransitionRules.Single(rule => rule.TrackId == guitar.Id).VersionId == copiedGuitarVersion.Id,
            "Duplicating a slot must remap rule references.");

        Assert(restored.GetTrackMeasureVersion(restored.Sections[0], restoredSlot, restoredGuitar, Guid.Empty).TrackId == guitar.Id,
            "Original content selection did not resolve to the destination track.");
        Assert(restored.RemoveTrackMeasureVersion(restored.Sections[0], restoredSlot, restoredGuitar, guitarVersion.Id), "Version deletion failed.");
        Assert(restoredSlot.TransitionRules.All(rule => rule.VersionId != guitarVersion.Id) &&
            restoredSlot.Alternations.Single(item => item.TrackId == guitar.Id).VersionIds.SequenceEqual(new[] { Guid.Empty }),
            "Deleting a version must clean its rules and sequence entries.");
        restored.RemoveTrack(restoredDrums);
        Assert(restoredSlot.TrackMeasureVersions.All(item => item.TrackId != drums.Id) &&
            restoredSlot.TransitionRules.All(rule => rule.TrackId != drums.Id) && restoredSlot.Alternations.All(item => item.TrackId != drums.Id),
            "Track deletion must clean destination transition data.");
        restored.RemoveMeasure(restored.Sections[0], restored.Sections[0].Measures[0]);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

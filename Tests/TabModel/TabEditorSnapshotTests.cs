using System.Text.Json;
using System.Reflection;
using OpenTab.Music.Playback;

internal static class TabEditorSnapshotTests
{
    public static void Run()
    {
        SnapshotJsonRoundTrip();
        WorkspaceJsonRoundTrip();
        RepeatModePersistence();
        Console.WriteLine("PASS editor snapshot and workspace JSON round-trips");
    }

    private static void RepeatModePersistence()
    {
        var presenter = typeof(DrumScorePresenter);
        var workspaceType = presenter.GetNestedType("EditorWorkspaceState", BindingFlags.NonPublic)!;
        var normalize = presenter.GetMethod("RestorePlaybackRepeatMode", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var mode in new[] { PlaybackLoopMode.None, PlaybackLoopMode.Score, PlaybackLoopMode.Measure, PlaybackLoopMode.Sections })
        {
            var workspace = Activator.CreateInstance(workspaceType, nonPublic: true)!;
            Set(workspaceType, workspace, "PlaybackRepeatMode", (int?)mode);
            var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(workspace, workspaceType), workspaceType)!;
            var saved = Get<int?>(workspaceType, restored, "PlaybackRepeatMode");
            Assert((PlaybackLoopMode)normalize.Invoke(null, new object?[] { saved })! == mode,
                $"Repeat mode {mode} did not survive workspace JSON.");
        }
        var oldWorkspace = JsonSerializer.Deserialize("{\"Version\":1}", workspaceType)!;
        var missing = Get<int?>(workspaceType, oldWorkspace, "PlaybackRepeatMode");
        Assert((PlaybackLoopMode)normalize.Invoke(null, new object?[] { missing })! == PlaybackLoopMode.Score,
            "Older workspaces must retain the default repeat mode.");
        Assert((PlaybackLoopMode)normalize.Invoke(null, new object?[] { 999 })! == PlaybackLoopMode.Score,
            "Invalid saved repeat modes must use the default.");
    }

    private static void WorkspaceJsonRoundTrip()
    {
        var presenterType = typeof(DrumScorePresenter);
        var workspaceType = presenterType.GetNestedType("EditorWorkspaceState", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Private editor workspace state type was not found.");
        var workspace = Activator.CreateInstance(workspaceType, nonPublic: true)
            ?? throw new InvalidOperationException("Could not create the editor workspace state.");
        var sectionId = Guid.NewGuid();
        var otherSectionId = Guid.NewGuid();
        var measureId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var arrangementId = Guid.NewGuid();
        Set(workspaceType, workspace, "SectionId", sectionId);
        Set(workspaceType, workspace, "VisibleSectionIds", new List<Guid> { sectionId, otherSectionId });
        Set(workspaceType, workspace, "SectionRepeatSources", new Dictionary<Guid, Dictionary<Guid, Guid>>
        {
            [sectionId] = new() { [trackId] = measureId }
        });
        Set(workspaceType, workspace, "MeasureId", measureId);
        Set(workspaceType, workspace, "TrackId", trackId);
        Set(workspaceType, workspace, "ArrangementId", arrangementId);
        Set(workspaceType, workspace, "ArrangementLoop", false);
        Set(workspaceType, workspace, "ArrangementShortcuts", true);
        Set(workspaceType, workspace, "TabTrackId", trackId);
        Set(workspaceType, workspace, "TabMeasureId", measureId);
        Set(workspaceType, workspace, "Tab", new MeasureTabView.EditorSnapshot(
            ["visible-note"], "visible-note", "visible-note", 2,
            new MeasureTabView.ExactTime(5, 32), "visible-note", "7"));
        Set(workspaceType, workspace, "Folded", new Dictionary<Guid, bool> { [trackId] = true });
        Set(workspaceType, workspace, "Boards", new Dictionary<Guid, DrumScorePresenter.BoardState>
        {
            [trackId] = new DrumScorePresenter.BoardState
            {
                KeyboardVisible = true, KeyboardX = 123, KeyboardY = 234,
                FretboardVisible = false, FretboardX = 345, FretboardY = 456
            }
        });

        var json = JsonSerializer.Serialize(workspace, workspaceType);
        var restored = JsonSerializer.Deserialize(json, workspaceType)
            ?? throw new InvalidOperationException("The editor workspace deserialized as null.");
        Assert(Get<Guid?>(workspaceType, restored, "SectionId") == sectionId, "Section ID did not survive workspace JSON.");
        Assert(Get<List<Guid>>(workspaceType, restored, "VisibleSectionIds").SequenceEqual(new[] { sectionId, otherSectionId }),
            "Visible section IDs did not survive workspace JSON.");
        Assert(Get<Dictionary<Guid, Dictionary<Guid, Guid>>>(workspaceType, restored, "SectionRepeatSources")[sectionId][trackId] == measureId,
            "Section-specific repeat source did not survive workspace JSON.");
        Assert(Get<Guid?>(workspaceType, restored, "MeasureId") == measureId, "Measure ID did not survive workspace JSON.");
        Assert(Get<Guid?>(workspaceType, restored, "ArrangementId") == arrangementId &&
            !Get<bool>(workspaceType, restored, "ArrangementLoop") && Get<bool>(workspaceType, restored, "ArrangementShortcuts"),
            "Arrangement selection or transport preferences did not survive workspace JSON.");
        Assert(Get<Dictionary<Guid, bool>>(workspaceType, restored, "Folded")[trackId], "Fold state did not survive workspace JSON.");

        var tab = Get<MeasureTabView.EditorSnapshot>(workspaceType, restored, "Tab");
        Assert(tab.ActiveOnset == new MeasureTabView.ExactTime(5, 32) && tab.FretDigits == "7",
            "Tab snapshot did not survive workspace JSON.");
        var boards = Get<Dictionary<Guid, DrumScorePresenter.BoardState>>(workspaceType, restored, "Boards");
        var board = boards[trackId];
        Assert(board.KeyboardVisible && board.KeyboardX == 123 && board.KeyboardY == 234 &&
            !board.FretboardVisible && board.FretboardX == 345 && board.FretboardY == 456,
            "Floating board visibility or positions did not survive workspace JSON.");
    }

    private static void Set<T>(Type type, object target, string name, T value) =>
        type.GetProperty(name)!.SetValue(target, value);

    private static T Get<T>(Type type, object target, string name) =>
        (T)type.GetProperty(name)!.GetValue(target)!;

    private static void SnapshotJsonRoundTrip()
    {
        var snapshot = new MeasureTabView.EditorSnapshot(
            ["note-a", "note-b"], "note-b", "note-a", 3,
            new MeasureTabView.ExactTime(7, 24), "note-b", "12");

        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<MeasureTabView.EditorSnapshot>(json)
            ?? throw new InvalidOperationException("The editor snapshot deserialized as null.");

        Assert(restored.SelectedEventIds.SequenceEqual(snapshot.SelectedEventIds), "Selected note IDs did not round-trip.");
        Assert(restored.SelectedEventId == snapshot.SelectedEventId && restored.SelectionAnchorId == snapshot.SelectionAnchorId,
            "Primary selection or anchor did not round-trip.");
        Assert(restored.ActiveStringNumber == snapshot.ActiveStringNumber && restored.ActiveOnset == snapshot.ActiveOnset,
            "Active string or exact onset did not round-trip.");
        Assert(restored.EditingNoteId == snapshot.EditingNoteId && restored.FretDigits == snapshot.FretDigits,
            "Uncommitted fret editing state did not round-trip.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

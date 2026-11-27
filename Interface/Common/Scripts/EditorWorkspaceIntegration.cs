#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using OpenTab.Music.Playback;

public partial class DrumScorePresenter
{
    private sealed class EditorWorkspaceState
    {
        public int Version { get; set; } = 1;
        public Guid? SectionId { get; set; }
        public List<Guid>? VisibleSectionIds { get; set; }
        public Guid? MeasureId { get; set; }
        public Guid? TrackId { get; set; }
        public DrumFocusState? DrumFocus { get; set; }
        public Guid? TabMeasureId { get; set; }
        public Guid? TabTrackId { get; set; }
        public MeasureTabView.EditorSnapshot? Tab { get; set; }
        public Dictionary<Guid, bool> Folded { get; set; } = new();
        public int? TrackHeight { get; set; }
        public Dictionary<Guid, int> DisplayModes { get; set; } = new();
        public Dictionary<Guid, Guid> RepeatSources { get; set; } = new();
        public Dictionary<Guid, Dictionary<Guid, Guid>>? SectionRepeatSources { get; set; }
        public Dictionary<Guid, BoardState> Boards { get; set; } = new();
        public int Snap { get; set; } = 16;
        public Dictionary<Guid, int>? TrackSnaps { get; set; }
        public Dictionary<Guid, int>? TrackTabScroll { get; set; }
        public Dictionary<Guid, int>? FullTabViewportHeights { get; set; }
        public Dictionary<Guid, int>? FullTabPitchScroll { get; set; }
        public int? PlaybackRepeatMode { get; set; }
        public Guid? ArrangementId { get; set; }
        public bool ArrangementLoop { get; set; } = true;
        public bool ArrangementShortcuts { get; set; }
        public bool? PercussionFolded { get; set; }
        public int ScrollHorizontal { get; set; }
        public int ScrollVertical { get; set; }
    }

    private sealed class DrumFocusState
    {
        public Guid TrackId { get; set; }
        public Guid MeasureId { get; set; }
        public Guid BeatId { get; set; }
        public string ControlPath { get; set; } = string.Empty;
    }

    private DrumFocusState? _drumFocus;
    private bool _restoreDrumFocus;
    private readonly List<(Guid Track, Guid Measure, Guid Beat, Control Cell)> _drumFocusTargets = new();
    private MeasureTabView? _activeEditorView;
    private Guid? _editorTabTrackId, _editorTabMeasureId;
    private MeasureTabView.EditorSnapshot? _editorTabSnapshot;
    private bool _restoreEditorFocus;
    private bool _tabRestorePending;
    private FoldableContainer _percussionFoldable = null!;
    private bool _defaultPercussionFolded;
    private bool _workspaceDirty;
    private double _workspaceDelay;
    private int? _restoreScrollX, _restoreScrollY;
    private int _lastScrollX, _lastScrollY;

    private void InitializeWorkspacePersistence()
    {

    }

    private void MarkEditorStateDirty() { _workspaceDirty = true; _workspaceDelay = 0.5; }

    private string WorkspaceStatePath(string? scorePath)
    {
        var folder = ProjectSettings.GlobalizePath("user://editor_workspaces");
        if (string.IsNullOrWhiteSpace(scorePath)) return Path.Combine(folder, "session.json");
        var normalized = Path.GetFullPath(scorePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(folder, key + ".json");
    }

    private void SetActiveEditorTrack(Guid? trackId)
    {
        if (_selectedNonPercussionTrackId == trackId) return;
        if (_editorTabTrackId != trackId)
        {
            if (_activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView)) _activeEditorView.ShowFocusWhenUnfocused = false;
            _activeEditorView = null; _tabRestorePending = false; _editorTabSnapshot = null;
            _editorTabTrackId = null; _editorTabMeasureId = null;
        }
        _selectedNonPercussionTrackId = trackId;
        UpdateTrackSelectionAppearance();
        ActivateFloatingBoards(trackId);
        RequestBoardHighlightsRefresh();
        MarkEditorStateDirty();
    }

    private void CaptureActiveTab()
    {
        if (!_tabRestorePending && _activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView))
            _editorTabSnapshot = _activeEditorView.CaptureEditorSnapshot();
    }

    private void BeforeEditorRebuild()
    {
        if (!_tabRestorePending && _pendingTabCursor is null && _activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView))
        {
            _restoreEditorFocus = _activeEditorView.HasFocus();
            CaptureActiveTab();
        }
        if (_activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView)) _activeEditorView.ShowFocusWhenUnfocused = false;
        _activeEditorView = null;
        _tabRestorePending = false;
        RequestBoardHighlightsRefresh();
    }

    private void ActivateTabView(Guid trackId, Guid measureId, MeasureTabView view)
    {
        if (_activeEditorView is not null && _activeEditorView != view && GodotObject.IsInstanceValid(_activeEditorView))
            _activeEditorView.ShowFocusWhenUnfocused = false;
        SetActiveEditorTrack(trackId);
        _drumFocus = null;
        _editingMeasureId = measureId;
        var owningSection = _score.Sections.FirstOrDefault(section => section.Measures.Any(measure => measure.Id == measureId));
        if (owningSection is not null) SetFocusedSection(owningSection);
        _editorTabTrackId = trackId;
        _editorTabMeasureId = measureId;
        _activeEditorView = view;
        view.ShowFocusWhenUnfocused = true;
        RequestBoardHighlightsRefresh();
        CaptureActiveTab();
        MarkEditorStateDirty();
    }

    private void BindEditorView(Guid trackId, Guid measureId, MeasureTabView view, ScrollContainer scroll)
    {
        view.EditorStateChanged += () =>
        {
            if (_activeEditorView != view || !GodotObject.IsInstanceValid(view)) return;
            CaptureActiveTab();
            RequestBoardHighlightsRefresh();
            MarkEditorStateDirty();
        };
        if (_pendingTabCursor is not null || _editorTabSnapshot is null || _editorTabTrackId != trackId || _editorTabMeasureId != measureId) return;
        _activeEditorView = view;
        view.ShowFocusWhenUnfocused = true;
        RequestBoardHighlightsRefresh();
        _tabRestorePending = true;
        var snapshot = _editorTabSnapshot;
        var focus = _restoreEditorFocus;
        _restoreEditorFocus = false;
        Callable.From(() =>
        {
            if (_activeEditorView != view || !GodotObject.IsInstanceValid(view) || !view.IsInsideTree()) return;
            _tabRestorePending = false;
            view.RestoreEditorSnapshot(snapshot, focus && view.IsVisibleInTree());
            RequestBoardHighlightsRefresh();
            if (focus && view.IsVisibleInTree() && GodotObject.IsInstanceValid(scroll)) EnsureTabEditorVisible(scroll, view);
        }).CallDeferred();
    }

    private EditorWorkspaceState CaptureWorkspace()
    {
        CaptureActiveTab();
        var scroll = GetNodeOrNull<ScrollContainer>("%ScrollContainer");
        return new EditorWorkspaceState
        {
            SectionId = _score.Sections.Count == 0 ? null : GetSelectedSection().Id,
            VisibleSectionIds = GetVisibleSections().Select(section => section.Id).ToList(),
            MeasureId = _editingMeasureId, TrackId = _selectedNonPercussionTrackId,
            TabTrackId = _editorTabTrackId, TabMeasureId = _editorTabMeasureId, Tab = _editorTabSnapshot, DrumFocus = _drumFocus,
            Folded = new(_trackFoldedStates), TrackHeight = (int)_trackHeight, DisplayModes = new(_trackDisplayModes),
            RepeatSources = _score.Sections.Count == 0 ? new() : _sectionRepeatSourceIndices
                .Where(pair => pair.Key.SectionId == GetSelectedSection().Id && pair.Value >= 0 && pair.Value < GetSelectedSection().Measures.Count)
                .ToDictionary(pair => pair.Key.TrackId, pair => GetSelectedSection().Measures[pair.Value].Id),
            SectionRepeatSources = CaptureSectionRepeatSources(),
            Boards = CaptureFloatingBoardStates(), Snap = _tabSnapDenominator, TrackSnaps = new(_tabSnapDenominators), TrackTabScroll = new(_trackTabScroll), PlaybackRepeatMode = (int)_playbackLoopMode, PercussionFolded = null,
            ArrangementId = _selectedArrangementId, ArrangementLoop = _arrangementLoop, ArrangementShortcuts = _arrangementShortcuts,
            ScrollHorizontal = scroll?.ScrollHorizontal ?? _lastScrollX, ScrollVertical = scroll?.ScrollVertical ?? _lastScrollY,
            FullTabViewportHeights = new(_fullTabViewportHeights), FullTabPitchScroll = new(_fullTabPitchScroll)
        };
    }

    private Dictionary<Guid, Dictionary<Guid, Guid>> CaptureSectionRepeatSources()
    {
        var result = new Dictionary<Guid, Dictionary<Guid, Guid>>();
        foreach (var (key, index) in _sectionRepeatSourceIndices)
        {
            var section = _score.Sections.FirstOrDefault(item => item.Id == key.SectionId);
            if (section is null || index < 0 || index >= section.Measures.Count || !_score.Tracks.Any(track => track.Id == key.TrackId)) continue;
            if (!result.TryGetValue(key.SectionId, out var tracks)) result[key.SectionId] = tracks = new();
            tracks[key.TrackId] = section.Measures[index].Id;
        }
        return result;
    }

    private void SaveEditorWorkspace()
    {
        if (_score is null || !IsInsideTree()) return;
        try
        {
            var json = JsonSerializer.Serialize(CaptureWorkspace());
            void Write(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
            }
            Write(WorkspaceStatePath(null));
            if (!string.IsNullOrWhiteSpace(_currentFilePath)) Write(WorkspaceStatePath(_currentFilePath));
            _workspaceDirty = false;
        }
        catch (Exception error) { GD.PushWarning($"Could not save editor workspace: {error.Message}"); _workspaceDirty = false; }
    }

    private void RestoreEditorWorkspace(string? path, bool loadSavedState = true)
    {
        _trackHeight = TrackHeight.Standard;
        _expandedInFoldedPreset.Clear();
        _fullTabViewportHeights.Clear();
        _fullTabPitchScroll.Clear();
        _playbackLoopMode = PlaybackLoopMode.Score;
        _selectedArrangementId = _score.Arrangements.FirstOrDefault()?.Id;
        _arrangementLoop = true; _arrangementShortcuts = false;
        if (_activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView)) _activeEditorView.ShowFocusWhenUnfocused = false;
        _activeEditorView = null; _tabRestorePending = false; _editorTabSnapshot = null; _editorTabTrackId = null; _editorTabMeasureId = null; _drumFocus = null; _restoreDrumFocus = false;
        RequestBoardHighlightsRefresh();
        _selectedNonPercussionTrackId = null; _editingMeasureId = null; _selectedSectionIndex = 0;
        _visibleSectionIds.Clear();
        _trackFoldedStates.Clear(); _trackDisplayModes.Clear(); _selectedRepeatSourceIndices.Clear(); _sectionRepeatSourceIndices.Clear();
        _tabSnapDenominator = 16; _tabSnapDenominators.Clear(); _trackTabScroll.Clear(); _restoreEditorFocus = false; _restoreScrollX = null; _restoreScrollY = null;
        ActivateFloatingBoards(null);
        RestoreFloatingBoardStates(new());
        if (!loadSavedState) { SeedTrackSnapDefaults(null); ActivateFloatingBoards(null); _workspaceDirty = false; return; }
        try
        {
            var statePath = WorkspaceStatePath(path);
            if (!File.Exists(statePath)) { SeedTrackSnapDefaults(null); ActivateFloatingBoards(null); return; }
            var state = JsonSerializer.Deserialize<EditorWorkspaceState>(File.ReadAllText(statePath));
            if (state is null || state.Version != 1) { SeedTrackSnapDefaults(null); ActivateFloatingBoards(null); return; }
            if (state.TrackHeight is >= (int)TrackHeight.Compact and <= (int)TrackHeight.FoldedTall)
                _trackHeight = (TrackHeight)state.TrackHeight.Value;
            _playbackLoopMode = RestorePlaybackRepeatMode(state.PlaybackRepeatMode);
            _selectedArrangementId = _score.Arrangements.Any(a => a.Id == state.ArrangementId) ? state.ArrangementId : _score.Arrangements.FirstOrDefault()?.Id;
            _arrangementLoop = state.ArrangementLoop; _arrangementShortcuts = state.ArrangementShortcuts;
            var sectionIndex = _score.Sections.ToList().FindIndex(s => s.Id == state.SectionId);
            var validSections = _score.Sections.Select(s => s.Id).ToHashSet();
            foreach (var id in state.VisibleSectionIds ?? new())
                if (validSections.Contains(id)) _visibleSectionIds.Add(id);
            if (_visibleSectionIds.Count > 0 &&
                (sectionIndex < 0 || !_visibleSectionIds.Contains(_score.Sections[sectionIndex].Id)))
                sectionIndex = _score.Sections.ToList().FindIndex(s => _visibleSectionIds.Contains(s.Id));
            _selectedSectionIndex = Math.Max(0, sectionIndex);
            var section = _score.Sections.ElementAtOrDefault(_selectedSectionIndex);
            if (section is not null) _visibleSectionIds.Add(section.Id);
            _editingMeasureId = section?.Measures.FirstOrDefault(m => m.Id == state.MeasureId)?.Id;
            var trackIds = _score.Tracks.Select(t => t.Id).ToHashSet();
            foreach (var (id, value) in state.Folded ?? new()) if (trackIds.Contains(id)) _trackFoldedStates[id] = value;
            foreach (var (id, value) in state.DisplayModes ?? new()) if (trackIds.Contains(id) && value is 0 or 1) _trackDisplayModes[id] = value;
            foreach (var (id, measureId) in state.RepeatSources ?? new())
            {
                var index = section?.Measures.ToList().FindIndex(measure => measure.Id == measureId) ?? -1;
                if (trackIds.Contains(id) && index >= 0)
                {
                    _selectedRepeatSourceIndices[id] = index;
                    _sectionRepeatSourceIndices[(section!.Id, id)] = index;
                }
            }
            foreach (var (sectionId, tracks) in state.SectionRepeatSources ?? new())
            {
                var owner = _score.Sections.FirstOrDefault(item => item.Id == sectionId);
                if (owner is null || tracks is null) continue;
                foreach (var (repeatTrackId, measureId) in tracks)
                {
                    var index = owner.Measures.ToList().FindIndex(measure => measure.Id == measureId);
                    if (trackIds.Contains(repeatTrackId) && index >= 0) _sectionRepeatSourceIndices[(sectionId, repeatTrackId)] = index;
                }
            }
            SeedTrackSnapDefaults(state);
            foreach (var (scrollTrackId, position) in state.TrackTabScroll ?? new())
                if (trackIds.Contains(scrollTrackId)) _trackTabScroll[scrollTrackId] = Math.Max(0, position);
            var fullTabIds = _score.Tracks.Where(track => track.UsesFullTab).Select(track => track.Id).ToHashSet();
            foreach (var (id, height) in state.FullTabViewportHeights ?? new())
                if (fullTabIds.Contains(id) && height >= 0 && height < FullTabViewportHeights.Length) _fullTabViewportHeights[id] = height;
            foreach (var (id, position) in state.FullTabPitchScroll ?? new())
                if (fullTabIds.Contains(id)) _fullTabPitchScroll[id] = Math.Max(0, position);
            // Older workspace snapshots stored a single snap value. Keep that value
            // as the fallback for tracks that have no per-track entry.
            RestoreFloatingBoardStates(state.Boards ?? new());
            if (state.TrackId is { } trackId && trackIds.Contains(trackId)) _selectedNonPercussionTrackId = trackId;
            ActivateFloatingBoards(_selectedNonPercussionTrackId);
            if (state.TabTrackId is { } tabTrack && trackIds.Contains(tabTrack) && section?.Measures.Any(m => m.Id == state.TabMeasureId) == true)
            {
                _editorTabTrackId = tabTrack; _editorTabMeasureId = state.TabMeasureId; _editorTabSnapshot = state.Tab;
                _restoreEditorFocus = state.Tab is not null;
            }
            if (state.DrumFocus is { } drum && trackIds.Contains(drum.TrackId) && section?.Measures.Any(m => m.Id == drum.MeasureId) == true && drum.BeatId != Guid.Empty)
            { _drumFocus = drum; _restoreDrumFocus = true; }
            _restoreScrollX = Math.Max(0, state.ScrollHorizontal); _restoreScrollY = Math.Max(0, state.ScrollVertical);
        }
        catch (Exception error) { GD.PushWarning($"Could not restore editor workspace: {error.Message}"); SeedTrackSnapDefaults(null); ActivateFloatingBoards(null); }
        _workspaceDirty = false;
    }

    private void SeedTrackSnapDefaults(EditorWorkspaceState? state)
    {
        if (state?.Snap is 4 or 8 or 16 or 32 or 64) _tabSnapDenominator = state.Snap;
        foreach (var track in _score.Tracks)
        {
            if (track.TabSnapDenominator is not (4 or 8 or 16 or 32 or 64))
            {
                var migrated = state?.TrackSnaps?.GetValueOrDefault(track.Id);
                track.TabSnapDenominator = migrated is 4 or 8 or 16 or 32 or 64
                    ? migrated : _tabSnapDenominator;
            }
            _tabSnapDenominators[track.Id] = track.TabSnapDenominator!.Value;
        }
    }

    private static PlaybackLoopMode RestorePlaybackRepeatMode(int? saved) => saved switch
    {
        (int)PlaybackLoopMode.None => PlaybackLoopMode.None,
        (int)PlaybackLoopMode.Score => PlaybackLoopMode.Score,
        (int)PlaybackLoopMode.Measure => PlaybackLoopMode.Measure,
        (int)PlaybackLoopMode.Sections => PlaybackLoopMode.Sections,
        _ => PlaybackLoopMode.Score
    };

    private void CaptureDrumFocus()
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered is null || hovered.FocusMode == FocusModeEnum.None) return;
        foreach (var (track, measure, beat, cell) in _drumFocusTargets)
        {
            if (!GodotObject.IsInstanceValid(cell) || !(cell == hovered || cell.IsAncestorOf(hovered))) continue;
            _drumFocus = new DrumFocusState { TrackId = track, MeasureId = measure, BeatId = beat, ControlPath = cell.GetPathTo(hovered).ToString() };
            if (_activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView)) _activeEditorView.ShowFocusWhenUnfocused = false;
            _activeEditorView = null; _editorTabSnapshot = null; _editorTabTrackId = null; _editorTabMeasureId = null;
            MarkEditorStateDirty();
            return;
        }
    }

    private void RegisterDrumFocusTarget(Guid track, Guid measure, Guid beat, Control cell)
    {
        _drumFocusTargets.Add((track, measure, beat, cell));
        if (!_restoreDrumFocus || _drumFocus is not { } saved || saved.TrackId != track || saved.MeasureId != measure || saved.BeatId != beat) return;
        _restoreDrumFocus = false;
        if (string.IsNullOrWhiteSpace(saved.ControlPath)) return;
        var target = cell.GetNodeOrNull<Control>(saved.ControlPath);
        if (target is null) return;
        Callable.From(() => { if (GodotObject.IsInstanceValid(target) && target.IsVisibleInTree() && target.FocusMode != FocusModeEnum.None) target.GrabFocus(); }).CallDeferred();
    }
    private void RestoreWorkspaceScroll()
    {
        if (_restoreScrollX is not { } x || _restoreScrollY is not { } y) return;
        _restoreScrollX = null; _restoreScrollY = null;
        var scroll = GetNode<ScrollContainer>("%ScrollContainer");
        Callable.From(() => { if (GodotObject.IsInstanceValid(scroll)) { scroll.ScrollHorizontal = x; scroll.ScrollVertical = y; } }).CallDeferred();
    }
}

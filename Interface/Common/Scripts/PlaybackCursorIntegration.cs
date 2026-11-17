#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public partial class DrumScorePresenter
{
    private readonly List<(Guid MeasureId, Control Control, bool Drum)> _cursorTargets = new();
    private readonly List<(Guid MeasureId, MeasureTabView View)> _cursorTabViews = new();
    private readonly List<(Guid TrackId, Guid SourceMeasureId, MeasureTabView View, ScrollContainer Scroll)> _playbackFollowTargets = new();
    private bool _playbackSectionRevealQueued;
    private Guid? _editingMeasureId;
    private Guid? _playbackStartMeasureId;
    private PlaybackCursorOverlay _cursorOverlay = null!;

    private void InitializePlaybackCursor()
    {
        _cursorOverlay = new PlaybackCursorOverlay { Name = "PlaybackCursorOverlay", MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        AddChild(_cursorOverlay);
        _cursorOverlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    private void RegisterCursorTarget(Guid measureId, Control control, bool drum = false) =>
        _cursorTargets.Add((measureId, control, drum));

    /// <summary>Registers a source tab cell that playback may reveal for its track.</summary>
    private void RegisterPlaybackFollowTarget(Guid trackId, Guid sourceMeasureId, MeasureTabView view, ScrollContainer scroll) =>
        _playbackFollowTargets.Add((trackId, sourceMeasureId, view, scroll));

    /// <summary>Clears source cells before the tab workspace is rebuilt.</summary>
    private void ClearPlaybackFollowTargets()
    {
        _playbackFollowTargets.Clear();
    }

    private void SelectEditingMeasureAt(Vector2 position)
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        foreach (var (id, control, _) in _cursorTargets)
        {
            if (GodotObject.IsInstanceValid(control) && control.IsVisibleInTree() && hovered is not null && (hovered == control || control.IsAncestorOf(hovered)) && VisibleCursorRect(control).HasPoint(position))
            {
                if (_editingMeasureId != id) { _activeEditorView = null; _tabRestorePending = false; _editorTabSnapshot = null; _editorTabTrackId = null; _editorTabMeasureId = null; }
                _editingMeasureId = id;
                var owningSection = _score.Sections.FirstOrDefault(section => section.Measures.Any(measure => measure.Id == id));
                if (owningSection is not null) SetFocusedSection(owningSection);
                MarkEditorStateDirty();
                return;
            }
        }
    }

    private static Rect2 VisibleCursorRect(Control control)
    {
        var rect = control.GetGlobalRect();
        for (Node? parent = control.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) rect = rect.Intersection(scroll.GetGlobalRect());
        return rect;
    }

    private void UpdatePlaybackCursor()
    {
        if (_cursorOverlay is null || _score is null) return;
        var arrangementPlaying = _arrangementAudio is not null && _arrangementAudio.IsScorePlaying;
        var playbackAudio = arrangementPlaying ? _arrangementAudio! : _soundFontAudio;
        var playing = playbackAudio.IsScorePlaying;
        var start = playing ? _playbackStartMeasureId : _editingMeasureId;
        var position = playing ? playbackAudio.VisualPosition : null;
        var lines = new List<PlaybackCursorOverlay.CursorLine>();
        foreach (var (id, control, drum) in _cursorTargets)
        {
            if (!drum || !GodotObject.IsInstanceValid(control) || !control.IsVisibleInTree()) continue;
            var rect = control.GetGlobalRect();
            var clip = VisibleCursorRect(control);
            if (!clip.HasArea()) continue;
            var offset = _cursorOverlay.GlobalPosition;
            if (id == start && rect.Position.X >= clip.Position.X && rect.Position.X <= clip.End.X) lines.Add(new(rect.Position.X - offset.X, clip.Position.Y - offset.Y, clip.End.Y - offset.Y, true));
            if (position is { } current && id == current.MeasureId)
                {
                var x = rect.Position.X + rect.Size.X * (float)current.Fraction;
                if (x >= clip.Position.X && x <= clip.End.X) lines.Add(new(x - offset.X, clip.Position.Y - offset.Y, clip.End.Y - offset.Y, false));
            }
        }
        _cursorOverlay.SetLines(lines);
        foreach (var (id, view) in _cursorTabViews)
            if (GodotObject.IsInstanceValid(view))
                view.SetPlaybackCursor(id == start, position is { } current && current.MeasureId == id ? current.Fraction : null);
        FollowPlaybackSource(position, playing);
        UpdateAudioTrackPlaybackCursors();
    }

    private void FollowPlaybackSource(OpenTab.Music.Playback.PlaybackPosition? position, bool playing)
    {
        if (!playing || position is not { } current || _score is null) return;

        var section = _score.Sections.FirstOrDefault(item => item.Measures.Any(measure => measure.Id == current.MeasureId));
        if (section is null) return;
        if (!_visibleSectionIds.Contains(section.Id))
        {
            if (!_playbackSectionRevealQueued)
            {
                _playbackSectionRevealQueued = true;
                Callable.From(() =>
                {
                    _playbackSectionRevealQueued = false;
                    var activeAudio = _arrangementAudio is not null && _arrangementAudio.IsScorePlaying
                        ? _arrangementAudio : _soundFontAudio;
                    var now = activeAudio.VisualPosition;
                    if (!activeAudio.IsScorePlaying || now is null ||
                        !_score.Sections.Contains(section) || !section.Measures.Any(measure => measure.Id == now.Value.MeasureId) ||
                        !_visibleSectionIds.Add(section.Id)) return;
                    MarkEditorStateDirty();
                    RenderSection();
                }).CallDeferred();
            }
            return;
        }
        var destinationIndex = section.Measures.FindIndex(measure => measure.Id == current.MeasureId);
        if (destinationIndex < 0) return;

        foreach (var group in _playbackFollowTargets.GroupBy(item => item.TrackId))
        {
            var track = _score.Tracks.FirstOrDefault(item => item.Id == group.Key);
            if (track is null) continue;
            var sourceId = current.MeasureId;
            if (section.LeadTrackId != track.Id)
            {
                var occurrence = _score.GetNonPercussionOccurrence(section, track, destinationIndex);
                if (occurrence.SourceMeasureIndex is not { } sourceIndex || sourceIndex < 0 || sourceIndex >= section.Measures.Count) continue;
                sourceId = section.Measures[sourceIndex].Id;
            }

            var target = group.FirstOrDefault(item => item.SourceMeasureId == sourceId);
            if (target.View is null || !GodotObject.IsInstanceValid(target.View) || !GodotObject.IsInstanceValid(target.Scroll) ||
                !target.View.IsVisibleInTree() || !target.Scroll.IsVisibleInTree()) continue;
            if (sourceId != current.MeasureId)
                target.View.SetPlaybackCursor(sourceId == _playbackStartMeasureId, current.Fraction);

            var viewport = target.Scroll.GetGlobalRect();
            var measure = target.View.GetGlobalRect();
            if (viewport.Size.X <= 0 || measure.Size.X <= 0) continue;
            float delta = 0;
            if (measure.Size.X <= viewport.Size.X)
            {
                if (measure.Position.X < viewport.Position.X) delta = measure.Position.X - viewport.Position.X;
                else if (measure.End.X > viewport.End.X) delta = measure.End.X - viewport.End.X;
            }
            else
            {
                // When a measure is wider than the viewport, follow its playback point.
                float x = measure.Position.X + measure.Size.X * (float)Math.Clamp(current.Fraction, 0, 1);
                float margin = Math.Min(24, viewport.Size.X / 4);
                if (x < viewport.Position.X + margin) delta = x - viewport.Position.X - margin;
                else if (x > viewport.End.X - margin) delta = x - viewport.End.X + margin;
            }
            if (Math.Abs(delta) > 1) target.Scroll.ScrollHorizontal += (int)Math.Round(delta);
        }
    }
}

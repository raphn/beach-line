#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private readonly Dictionary<Guid, FoldableContainer> _trackRows = new();
    private readonly PackedScene _trackReorderControlsScene = GD.Load<PackedScene>("res://Interface/Tracks/Scenes/TrackReorderTitleControls.tscn");
    private StyleBox? _activeTrackPanelStyle;
    private StyleBox? _activeTrackTitleStyle;
    private static readonly string[] TrackTitleStyleNames =
        ["title_panel", "title_hover_panel", "title_collapsed_panel", "title_collapsed_hover_panel"];
    private static readonly string[] CollapsedTrackTitleStyleNames =
        ["title_collapsed_panel", "title_collapsed_hover_panel"];

    private void RegisterTrackRow(Guid trackId, FoldableContainer row)
    {
        _trackRows[trackId] = row;
        var orderedTracks = _score.Tracks.Where(track => track.Kind != TrackKind.Ruler).ToList();
        var controls = _trackReorderControlsScene.Instantiate<TrackReorderTitleControls>();
        controls.Configure(trackId, orderedTracks.FindIndex(track => track.Id == trackId), orderedTracks.Count);
        controls.MoveRequested += (id, direction) => MoveTrackBy(id, direction);
        controls.DropRequested += MoveTrackByDrop;
        row.AddTitleBarControl(controls);
        RefreshTrackTitles();
        var track = _score.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track is not null)
        {
            var icon = new TextureRect
            {
                Texture = GD.Load<Texture2D>(TrackSettingsDialog.ResolveImagePath(track)),
                CustomMinimumSize = new Vector2(24, 24),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Visible = row.Folded
            };
            row.AddTitleBarControl(icon);
            row.FoldingChanged += folded => icon.Visible = folded;
        }
        var authoredVerticalSizeFlags = row.SizeFlagsVertical;
        void ApplyFoldedRowLayout(bool folded)
        {
            if (!GodotObject.IsInstanceValid(row)) return;
            var targetFlags = folded
                ? authoredVerticalSizeFlags & ~Control.SizeFlags.Expand
                : authoredVerticalSizeFlags;
            if (row.SizeFlagsVertical != targetFlags)
            {
                row.SizeFlagsVertical = targetFlags;
                if (row.GetParent() is Container parent && GodotObject.IsInstanceValid(parent))
                    parent.QueueSort();
            }
            RefreshCollapsedTitleFill(row);
        }

        row.FoldingChanged += ApplyFoldedRowLayout;
        row.FoldingChanged += _ => QueueResponsiveTabWidthUpdate();
        row.Resized += () => RefreshCollapsedTitleFill(row);

        ApplyFoldedRowLayout(row.Folded);
        RefreshCollapsedTitleFill(row);
        UpdateTrackSelectionAppearance();
    }

    private void MoveTrackBy(string trackId, int direction)
    {
        if (!Guid.TryParseExact(trackId, "N", out var id)) return;
        var movable = _score.Tracks.Where(track => track.Kind != TrackKind.Ruler).ToList();
        var index = movable.FindIndex(track => track.Id == id);
        if (index < 0) return;
        MoveTrackToIndex(movable[index], index + direction);
    }

    private void MoveTrackByDrop(string sourceId, string targetId, bool before)
    {
        if (!Guid.TryParseExact(sourceId, "N", out var sourceGuid) || !Guid.TryParseExact(targetId, "N", out var targetGuid)) return;
        var movable = _score.Tracks.Where(track => track.Kind != TrackKind.Ruler).ToList();
        var source = movable.FirstOrDefault(track => track.Id == sourceGuid);
        var sourceIndex = source is null ? -1 : movable.IndexOf(source);
        var targetIndex = movable.FindIndex(track => track.Id == targetGuid);
        if (source is null || targetIndex < 0) return;
        if (sourceIndex < targetIndex) targetIndex--;
        MoveTrackToIndex(source, targetIndex + (before ? 0 : 1));
    }

    private void MoveTrackToIndex(ScoreTrack track, int destinationIndex)
    {
        var count = _score.Tracks.Count(candidate => candidate.Kind != TrackKind.Ruler);
        destinationIndex = Math.Clamp(destinationIndex, 0, count - 1);
        if (!_score.MoveTrack(track, destinationIndex)) return;
        NotifyScoreEdited();
        RenderSection();
    }

    private void RefreshTrackTitles()
    {
        if (_score is null || _score.Sections.Count == 0) return;
        var section = _score.Sections[Math.Clamp(_selectedSectionIndex, 0, _score.Sections.Count - 1)];
        foreach (var (id, row) in _trackRows)
        {
            if (!GodotObject.IsInstanceValid(row)) continue;
            var track = _score.Tracks.FirstOrDefault(candidate => candidate.Id == id);
            if (track is null) continue;
            row.Title = TrackTitleSummary.Format(_score, section, track);
            row.TooltipText = row.Title;
        }
    }
    private void ApplyTrackDisplayColor(Guid trackId, uint rgba)
    {
        var track = _score.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track is null) return;
        track.DisplayColorRgba = rgba;
        if (!_trackRows.TryGetValue(trackId, out var row) || !GodotObject.IsInstanceValid(row)) return;
        var color = new Color(rgba);
        foreach (var view in FindControls<MeasureTabView>(row)) view.SetTrackDisplayColor(rgba);
        if (row.FindChild("AudioTrackToolbar", true, false) is PanelContainer toolbar)
            toolbar.SelfModulate = color;
        UpdateTrackSelectionAppearance();
    }

    private void SelectTrackAtPointer(Vector2 position)
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        if (hovered is null) return;
        foreach (var (id, row) in _trackRows)
        {
            if (!GodotObject.IsInstanceValid(row) || !row.IsVisibleInTree()) continue;
            if ((hovered == row || row.IsAncestorOf(hovered)) && VisibleCursorRect(row).HasPoint(position))
            {
                SetActiveEditorTrack(id);
                return;
            }
        }
    }

    private void UpdateTrackSelectionAppearance()
    {
        _activeTrackPanelStyle ??= GD.Load<StyleBox>("res://Interface/Themes/Panels/track_active.tres");
        _activeTrackTitleStyle ??= GD.Load<StyleBox>("res://Interface/Themes/Panels/track_title_active.tres");
        foreach (var (id, row) in _trackRows)
        {
            if (!GodotObject.IsInstanceValid(row)) continue;
            var panel = row.GetNode<PanelContainer>("TrackContainer");
            var track = _score.Tracks.FirstOrDefault(candidate => candidate.Id == id);
            panel.SelfModulate = Colors.White;
            // Resolve the unmodified title styles each time before applying the
            // active and collapsed-color overrides; otherwise repeated refreshes
            // multiply a previously tinted style again.
            foreach (var style in TrackTitleStyleNames) row.RemoveThemeStyleboxOverride(style);
            if (_selectedNonPercussionTrackId == id)
            {
                if (_activeTrackPanelStyle is not null) panel.AddThemeStyleboxOverride("panel", _activeTrackPanelStyle);
                if (_activeTrackTitleStyle is not null)
                    foreach (var style in TrackTitleStyleNames) row.AddThemeStyleboxOverride(style, _activeTrackTitleStyle);
            }
            else
            {
                panel.RemoveThemeStyleboxOverride("panel");
            }
            if (track is not null)
            {
                foreach (var styleName in new[] { "title_collapsed_panel", "title_collapsed_hover_panel" })
                {
                    if (row.GetThemeStylebox(styleName) is not StyleBoxFlat source) continue;
                    var colored = (StyleBoxFlat)source.Duplicate();
                    colored.BgColor *= new Color(track.DisplayColorRgba);
                    row.AddThemeStyleboxOverride(styleName, colored);
                }
            }
            RefreshCollapsedTitleFill(row);
        }
    }

    private static void RefreshCollapsedTitleFill(FoldableContainer row)
    {
        if (!GodotObject.IsInstanceValid(row)) return;
        var extraHeight = row.Folded
            ? Math.Max(0, row.Size.Y - row.GetMinimumSize().Y)
            : 0;

        foreach (var styleName in CollapsedTrackTitleStyleNames)
        {
            if (row.GetThemeStylebox(styleName) is not StyleBoxFlat source) continue;
            var expanded = (StyleBoxFlat)source.Duplicate();
            expanded.ExpandMarginBottom = extraHeight;
            row.AddThemeStyleboxOverride(styleName, expanded);
        }
    }

    private bool TryExpandCollapsedTrackAt(Vector2 position)
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        foreach (var (trackId, row) in _trackRows)
        {
            if (!GodotObject.IsInstanceValid(row) || !row.IsVisibleInTree() || !row.Folded ||
                !VisibleCursorRect(row).HasPoint(position)) continue;
            // The native FoldableContainer only hit-tests its title. A parent of
            // the row can receive the remaining area; unrelated overlays cannot.
            if (hovered is not null && hovered != row && !hovered.IsAncestorOf(row)) continue;
            if (position.Y - row.GlobalPosition.Y < row.GetMinimumSize().Y) continue;
            row.Expand();
            SetActiveEditorTrack(trackId);
            return true;
        }
        return false;
    }
}

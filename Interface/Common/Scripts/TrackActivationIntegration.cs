#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private sealed record TrackRow(FoldableContainer Control, bool DefaultFolded, Vector2 MinimumSize, FoldedTrackPreview? Preview, bool IndependentHeight);
    private readonly Dictionary<Guid, TrackRow> _trackRows = new();
    private readonly HashSet<Guid> _expandedInFoldedPreset = new();
    private bool _applyingTrackFoldPreset;
    private readonly PackedScene _trackReorderControlsScene = GD.Load<PackedScene>("res://Interface/Tracks/Scenes/TrackReorderTitleControls.tscn");
    private readonly PackedScene _foldedTrackPreviewScene = GD.Load<PackedScene>("res://Interface/Tracks/Scenes/FoldedTrackPreview.tscn");
    private const float FoldedPreviewHeight = 30f;
    private const float FoldedPreviewInset = 2f;
    private bool _foldedTrackPreviewRefreshQueued;
    private StyleBox? _activeTrackPanelStyle;
    private StyleBox? _activeTrackTitleStyle;
    private static readonly string[] TrackTitleStyleNames =
        ["title_panel", "title_hover_panel", "title_collapsed_panel", "title_collapsed_hover_panel"];
    private static readonly string[] CollapsedTrackTitleStyleNames =
        ["title_collapsed_panel", "title_collapsed_hover_panel"];

    private void RegisterTrackRow(Guid trackId, FoldableContainer row)
    {
        bool defaultFolded = row.Folded;
        var track = _score.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        bool independentHeight = track?.UsesFullTab == true;
        row.Folded = ResolveTrackFolded(trackId, defaultFolded, independentHeight);
        var orderedTracks = _score.Tracks.Where(track => track.Kind != TrackKind.Ruler).ToList();
        var controls = _trackReorderControlsScene.Instantiate<TrackReorderTitleControls>();
        controls.Configure(trackId, orderedTracks.FindIndex(track => track.Id == trackId), orderedTracks.Count);
        controls.MoveRequested += (id, direction) => MoveTrackBy(id, direction);
        controls.DropRequested += MoveTrackByDrop;
        row.AddTitleBarControl(controls);
        FoldedTrackPreview? preview = null;
        if (track is not null && track.Kind != TrackKind.Audio)
        {
            preview = _foldedTrackPreviewScene.Instantiate<FoldedTrackPreview>();
            // A Node2D follows the row's transform without becoming foldable content.
            row.AddChild(preview);
        }
        var entry = new TrackRow(row, defaultFolded, row.CustomMinimumSize, preview, independentHeight);
        _trackRows[trackId] = entry;
        RefreshTrackTitles();
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
            if (preview is not null) preview.Visible = folded;
            var targetFlags = folded
                ? authoredVerticalSizeFlags & ~Control.SizeFlags.Expand
                : authoredVerticalSizeFlags;
            if (row.SizeFlagsVertical != targetFlags)
            {
                row.SizeFlagsVertical = targetFlags;
                if (row.GetParent() is Container parent && GodotObject.IsInstanceValid(parent))
                    parent.QueueSort();
            }
            FitFoldedTrack(entry);
            if (preview is not null && folded) QueueFoldedTrackPreviewRefresh();
        }

        row.FoldingChanged += ApplyFoldedRowLayout;
        row.FoldingChanged += folded =>
        {
            QueueResponsiveTabWidthUpdate();
            if (_applyingTrackFoldPreset) return;
            if (UsesFoldedTrackHeight && !independentHeight)
            {
                if (folded) _expandedInFoldedPreset.Remove(trackId);
                else _expandedInFoldedPreset.Add(trackId);
                return;
            }
            _trackFoldedStates[trackId] = folded;
            MarkEditorStateDirty();
        };
        row.Resized += () => FitFoldedTrack(entry);
        row.MinimumSizeChanged += () => FitFoldedTrack(entry);

        ApplyFoldedRowLayout(row.Folded);
        UpdateTrackSelectionAppearance();
    }

    private bool ResolveTrackFolded(Guid trackId, bool defaultFolded, bool independentHeight) =>
        UsesFoldedTrackHeight && !independentHeight
            ? !_expandedInFoldedPreset.Contains(trackId)
            : _trackFoldedStates.GetValueOrDefault(trackId, defaultFolded);

    private void ApplyTrackFoldPreset()
    {
        _expandedInFoldedPreset.Clear();
        _applyingTrackFoldPreset = true;
        try
        {
            // Folding can commit a focused rename, which rebuilds the rows.
            foreach (var (id, entry) in _trackRows.ToArray())
            {
                var row = entry.Control;
                if (entry.IndependentHeight || !GodotObject.IsInstanceValid(row) || !row.IsInsideTree()) continue;
                bool folded = ResolveTrackFolded(id, entry.DefaultFolded, entry.IndependentHeight);
                if (row.Folded != folded) row.Folded = folded;
                else FitFoldedTrack(entry);
            }
        }
        finally
        {
            _applyingTrackFoldPreset = false;
        }
    }

    private void FitFoldedTrack(TrackRow entry)
    {
        var row = entry.Control;
        if (!GodotObject.IsInstanceValid(row)) return;
        var extraHeight = (entry.IndependentHeight ? TrackHeight.FoldedCompact : _trackHeight) switch
        {
            TrackHeight.FoldedStandard => 16f,
            TrackHeight.FoldedTall => 36f,
            _ => 0f
        };
        var minimum = entry.MinimumSize;
        if (row.Folded)
        {
            var titleHeight = row.GetMinimumSize().Y;
            if (entry.Preview is { } preview)
            {
                var previewHeight = FoldedPreviewHeight + extraHeight;
                minimum.Y = Math.Max(minimum.Y, titleHeight + previewHeight + FoldedPreviewInset * 2);
                preview.SetBounds(new Rect2(FoldedPreviewInset, titleHeight + FoldedPreviewInset,
                    Math.Max(0, row.Size.X - FoldedPreviewInset * 2), previewHeight));
            }
            else
            {
                minimum.Y = Math.Max(minimum.Y, titleHeight) + extraHeight;
            }
        }
        if (row.CustomMinimumSize != minimum) row.CustomMinimumSize = minimum;
        if (entry.Preview is null) RefreshCollapsedTitleFill(row);
    }

    private void QueueFoldedTrackPreviewRefresh()
    {
        if (_foldedTrackPreviewRefreshQueued || !IsInsideTree()) return;
        _foldedTrackPreviewRefreshQueued = true;
        Callable.From(RefreshFoldedTrackPreviews).CallDeferred();
    }

    private void RefreshFoldedTrackPreviews()
    {
        _foldedTrackPreviewRefreshQueued = false;
        if (!IsInsideTree()) return;
        var foldedTracks = _score.Tracks
            .Where(track => track.Kind is not (TrackKind.Audio or TrackKind.Ruler) &&
                _trackRows.TryGetValue(track.Id, out var entry) &&
                GodotObject.IsInstanceValid(entry.Control) && entry.Control.Folded)
            .ToArray();
        if (foldedTracks.Length == 0) return;

        var visibleSections = GetVisibleSections().ToHashSet();
        var elapsed = 0.0;
        var visibleBars = new List<(TabBar Bar, double Start)>();
        var visibleSpans = new List<(ScoreTime Start, ScoreTime End, double PreviewStart)>();
        var bars = BuildTabBars();
        foreach (var bar in bars)
        {
            if (!visibleSections.Contains(bar.Section)) continue;
            visibleBars.Add((bar, elapsed));
            if (bar.Duration <= ScoreTime.Zero) continue;
            var end = bar.Start + bar.Duration;
            if (visibleSpans.Count > 0 && visibleSpans[^1].End == bar.Start)
            {
                var previous = visibleSpans[^1];
                visibleSpans[^1] = (previous.Start, end, previous.PreviewStart);
            }
            else
            {
                visibleSpans.Add((bar.Start, end, elapsed));
            }
            elapsed += PreviewTime(bar.Duration);
        }

        foreach (var track in foldedTracks)
        {
            var notes = new List<FoldedTrackPreview.Note>();
            foreach (var lane in BuildTabOccurrences(track, bars).GroupBy(occurrence => occurrence.Note.StringNumber))
            {
                var ordered = lane.OrderByDescending(occurrence => occurrence.Start).ToArray();
                ScoreTime? latestStart = null;
                ScoreTime? nextStart = null;
                foreach (var occurrence in ordered)
                {
                    if (latestStart != occurrence.Start)
                    {
                        nextStart = latestStart;
                        latestStart = occurrence.Start;
                    }
                    // Same-string retriggers truncate a sustain in the tab editor.
                    var end = nextStart is { } next && next < occurrence.End ? next : occurrence.End;
                    foreach (var span in visibleSpans)
                    {
                        if (occurrence.Start >= span.End || end <= span.Start) continue;
                        var start = occurrence.Start < span.Start ? span.Start : occurrence.Start;
                        var clippedEnd = end < span.End ? end : span.End;
                        notes.Add(new FoldedTrackPreview.Note(span.PreviewStart + PreviewTime(start - span.Start),
                            span.PreviewStart + PreviewTime(clippedEnd - span.Start), occurrence.Note.MidiPitch));
                    }
                }
            }

            foreach (var (bar, start) in visibleBars)
            {
                var occurrence = ResolveTrackOccurrence(bar.Section, track, bar);
                var part = GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, occurrence);
                if (part is null || part.UsesTabEvents) continue;

                foreach (var voice in part.Voices)
                {
                    var onset = ScoreTime.Zero;
                    foreach (var beat in voice.Beats)
                    {
                        if (onset >= bar.Duration) break;
                        var end = onset + beat.Duration;
                        var clippedEnd = end < bar.Duration ? end : bar.Duration;
                        foreach (var note in beat.Notes)
                            notes.Add(new FoldedTrackPreview.Note(start + PreviewTime(onset),
                                start + PreviewTime(clippedEnd), note.MidiPitch));
                        onset = end;
                    }
                }
            }
            _trackRows[track.Id].Preview?.SetNotes(notes, elapsed);
        }
    }

    private static double PreviewTime(ScoreTime time) => (double)time.Numerator / time.Denominator;

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
        foreach (var (id, entry) in _trackRows)
        {
            var row = entry.Control;
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
        if (!_trackRows.TryGetValue(trackId, out var entry) || !GodotObject.IsInstanceValid(entry.Control)) return;
        var row = entry.Control;
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
        foreach (var (id, entry) in _trackRows)
        {
            var row = entry.Control;
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
        foreach (var (id, entry) in _trackRows)
        {
            var row = entry.Control;
            if (!GodotObject.IsInstanceValid(row)) continue;
            var panel = row.GetNodeOrNull<PanelContainer>("VBoxContainer/TrackContainer") ?? row.GetNode<PanelContainer>("BoxContainer/TrackContainer");
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
                entry.Preview?.SetColor(new Color(track.DisplayColorRgba));
                foreach (var styleName in CollapsedTrackTitleStyleNames)
                {
                    if (row.GetThemeStylebox(styleName) is not StyleBoxFlat source) continue;
                    var colored = (StyleBoxFlat)source.Duplicate();
                    colored.BgColor *= new Color(track.DisplayColorRgba);
                    row.AddThemeStyleboxOverride(styleName, colored);
                }
                if (track.Kind == TrackKind.Audio) RefreshCollapsedTitleFill(row);
            }
        }
    }

    private static void RefreshCollapsedTitleFill(FoldableContainer row)
    {
        var extraHeight = row.Folded ? Math.Max(0, row.Size.Y - row.GetMinimumSize().Y) : 0;
        foreach (var styleName in CollapsedTrackTitleStyleNames)
        {
            if (row.GetThemeStylebox(styleName) is not StyleBoxFlat source || source.ExpandMarginBottom == extraHeight) continue;
            var expanded = (StyleBoxFlat)source.Duplicate();
            expanded.ExpandMarginBottom = extraHeight;
            row.AddThemeStyleboxOverride(styleName, expanded);
        }
    }

    private bool TryExpandCollapsedTrackAt(Vector2 position)
    {
        var hovered = GetViewport().GuiGetHoveredControl();
        foreach (var (trackId, entry) in _trackRows)
        {
            var row = entry.Control;
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

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private sealed record FullTabViewport(FoldableContainer Row, ScrollContainer Scroll, OptionButton Height);
    private readonly Dictionary<Guid, FullTabViewport> _fullTabViewports = new();
    private readonly Dictionary<Guid, int> _fullTabViewportHeights = new();
    private readonly Dictionary<Guid, int> _fullTabPitchScroll = new();
    private static readonly int[] FullTabViewportHeights = [192, 310, 428];

    private void BindFullTabViewport(Guid trackId, FoldableContainer row, ScrollContainer scroll)
    {
        var choice = row.GetNode<OptionButton>("VBoxContainer/CompactTrackControls/Controls/FullTabHeight");
        choice.Show();
        var viewport = new FullTabViewport(row, scroll, choice);
        _fullTabViewports[trackId] = viewport;
        int height = _fullTabViewportHeights.GetValueOrDefault(trackId, 1);
        choice.Select(height);
        SetMinimumHeight(scroll, FullTabViewportHeights[height]);
        scroll.VerticalScrollMode = ScrollContainer.ScrollMode.Auto;
        choice.ItemSelected += index => ApplyFullTabHeight(trackId, viewport, (int)index);

        var sections = scroll.GetNode<HBoxContainer>("Sections");
        BindPitchMeasureHeaders(scroll, sections);
        var views = FindControls<MeasureTabView>(sections).Where(view => view.ViewData?.IsFullTab == true).ToArray();
        if (views.Length == 0) return;

        var range = scroll.GetVScrollBar();
        int? saved = _fullTabPitchScroll.TryGetValue(trackId, out var offset) ? offset : null;
        bool initialized = false;
        bool restoreQueued = false;
        void QueueRestorePitchScroll()
        {
            if (initialized || restoreQueued || !scroll.IsVisibleInTree()) return;
            restoreQueued = true;
            Callable.From(() =>
            {
                restoreQueued = false;
                if (!GodotObject.IsInstanceValid(scroll) || !scroll.IsVisibleInTree() || range.Page <= 0 || range.MaxValue <= range.Page) return;
                int[] pitches = views.SelectMany(view => view.ViewData!.Notes).Select(note => note.StringNumber).Order().ToArray();
                var view = views[0];
                int lane = pitches.Length == 0 ? view.ViewData!.OpenStringMidiPitches.Count - 60 : pitches[pitches.Length / 2];
                float center = FullTabLaneCenter(scroll, view, lane);
                scroll.ScrollVertical = saved ?? Math.Max(0, Mathf.RoundToInt(center - (float)range.Page / 2));
                initialized = true;
            }).CallDeferred();
        }
        range.Changed += QueueRestorePitchScroll;
        scroll.VisibilityChanged += QueueRestorePitchScroll;
        range.ValueChanged += value =>
        {
            if (!initialized || !scroll.IsVisibleInTree()) return;
            _fullTabPitchScroll[trackId] = (int)value;
            MarkEditorStateDirty();
        };
        QueueRestorePitchScroll();
    }

    private void ApplyFullTabHeight(Guid trackId, FullTabViewport viewport, int height)
    {
        if (height == _fullTabViewportHeights.GetValueOrDefault(trackId, 1)) return;
        _fullTabViewportHeights[trackId] = height;
        viewport.Height.Select(height);
        SetMinimumHeight(viewport.Scroll, FullTabViewportHeights[height]);
        MarkEditorStateDirty();
    }

    private bool TryStepFullTabHeight(Control hovered, int step)
    {
        foreach (var (trackId, viewport) in _fullTabViewports)
        {
            if (viewport.Row != hovered && !viewport.Row.IsAncestorOf(hovered)) continue;
            if (viewport.Height.IsVisibleInTree())
                ApplyFullTabHeight(trackId, viewport, Math.Clamp(viewport.Height.Selected + step, 0, FullTabViewportHeights.Length - 1));
            return true;
        }
        return false;
    }

    // Keep each live measure header aligned with its column as the grid scrolls.
    private static void BindPitchMeasureHeaders(ScrollContainer scroll, HBoxContainer sections)
    {
        var strip = scroll.GetNode<Control>("../PitchMeasureHeaders");
        var cells = new List<(Control Column, Control Header)>();
        foreach (var section in sections.GetChildren().OfType<Control>())
        {
            var measures = section.GetNode<HBoxContainer>("VBoxContainer/Measures");
            foreach (var column in measures.GetChildren().OfType<Control>())
            {
                if (column.GetNodeOrNull<Control>("MeasureHeader") is not { } header) continue;
                header.Owner = null;
                header.Reparent(strip, false);
                cells.Add((column, header));
                if (column.GetNodeOrNull<Control>("Content") is { } content)
                    column.CustomMinimumSize = new Vector2(column.CustomMinimumSize.X, content.CustomMinimumSize.Y);
                column.SetMeta("full_tab_header_width", header.GetCombinedMinimumSize().X);
            }
        }
        if (cells.Count == 0) return;
        strip.Show();
        bool queued = false;
        void LayoutHeaders()
        {
            queued = false;
            if (!GodotObject.IsInstanceValid(strip) || !strip.IsInsideTree()) return;
            var height = cells.Max(cell => cell.Header.GetCombinedMinimumSize().Y);
            SetMinimumHeight(strip, height);
            var left = strip.GlobalPosition.X;
            foreach (var (column, header) in cells)
            {
                header.Position = new Vector2(column.GlobalPosition.X - left, 0);
                header.Size = new Vector2(column.Size.X, height);
            }
        }
        void QueueLayout()
        {
            if (queued) return;
            queued = true;
            Callable.From(LayoutHeaders).CallDeferred();
        }
        foreach (var (column, header) in cells)
        {
            column.Resized += QueueLayout;
            header.MinimumSizeChanged += () =>
            {
                column.SetMeta("full_tab_header_width", header.GetCombinedMinimumSize().X);
                QueueLayout();
            };
        }
        scroll.GetHScrollBar().ValueChanged += _ => QueueLayout();
        scroll.Resized += QueueLayout;
        sections.Resized += QueueLayout;
        strip.VisibilityChanged += QueueLayout;
        QueueLayout();
    }

    private static float FullTabLaneCenter(ScrollContainer scroll, MeasureTabView view, int lane) =>
        view.GlobalPosition.Y - scroll.GlobalPosition.Y + scroll.ScrollVertical + view.RowStart + (lane - 0.5f) * view.RowHeight;

    private static void EnsureTabEditorVisible(ScrollContainer scroll, MeasureTabView view, int lane = 0)
    {
        int pitchScroll = scroll.ScrollVertical;
        scroll.EnsureControlVisible(view);
        if (view.ViewData?.IsFullTab != true) return;
        scroll.ScrollVertical = pitchScroll;
        if (lane <= 0) return;
        float center = FullTabLaneCenter(scroll, view, lane);
        float page = (float)scroll.GetVScrollBar().Page;
        if (center - view.RowHeight / 2 < pitchScroll || center + view.RowHeight / 2 > pitchScroll + page)
            scroll.ScrollVertical = Mathf.RoundToInt(center - page / 2);
    }

    private void BindLegacyFullTabEditing(Control column, ScoreTrack track, TrackMeasure source, List<TabBar> bars)
    {
        var enable = column.GetNode<Button>("MeasureHeader/HeaderContent/EnablePitchEditing");
        enable.Show();
        enable.Pressed += () =>
        {
            try
            {
                var shortestBar = bars.Where(bar =>
                    ReferenceEquals(source, GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, ResolveTrackOccurrence(bar.Section, track, bar))))
                    .Select(bar => bar.Duration).DefaultIfEmpty(ScoreTime.Zero).Min();
                TabEventEditing.ConvertLegacyPitchNotes(source, track, shortestBar);
                NotifyScoreEdited();
                RenderSection();
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
            {
                SetTabStatus(error.Message);
            }
        };
    }
}

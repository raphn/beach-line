#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public partial class DrumScorePresenter
{
    private readonly PackedScene _trackTabPagesScene = GD.Load<PackedScene>("res://Interface/Tab/Scenes/TrackTabPages.tscn");
    // Positions survive editor rebuilds without modifying the score.
    private readonly Dictionary<Guid, int> _trackTabScroll = new();

    private ScrollContainer CreateTrackTabPages(Guid trackId, HBoxContainer host, out VBoxContainer headers, out HBoxContainer sections)
    {
        var savedPosition = _trackTabScroll.GetValueOrDefault(trackId);
        bool navigatingToMeasure = _pendingTabCursor?.TrackId == trackId;
        var panel = _trackTabPagesScene.Instantiate<VBoxContainer>();
        host.AddChild(panel);
        headers = panel.GetNode<VBoxContainer>("Headers");
        var scroll = panel.GetNode<ScrollContainer>("Scroll");
        sections = scroll.GetNode<HBoxContainer>("Sections");
        var sectionHost = sections;
        scroll.Resized += () => SizeTrackTabMeasures(scroll, sectionHost, GetTabSnapDenominator(trackId));
        var bar = scroll.GetHScrollBar();
        bar.ValueChanged += value => { _trackTabScroll[trackId] = (int)value; MarkEditorStateDirty(); };
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(scroll)) return;
            SizeTrackTabMeasures(scroll, sectionHost, GetTabSnapDenominator(trackId));
            Callable.From(() => { if (GodotObject.IsInstanceValid(scroll) && !navigatingToMeasure) scroll.ScrollHorizontal = savedPosition; }).CallDeferred();
        }).CallDeferred();
        return scroll;
    }

    private void BindTrackTabHeader(Guid trackId, Control controls, ScrollContainer scroll, bool multiSection = false)
    {
        controls.Visible = true;
        var snap = controls.GetNode<OptionButton>("SnapSelector");
        foreach (var value in new[] { 4, 8, 16, 32, 64 }) snap.AddItem($"1/{value}", value);
        for (int i = 0; i < snap.ItemCount; i++) if (snap.GetItemId(i) == GetTabSnapDenominator(trackId)) snap.Select(i);
        snap.TooltipText = Tr("Default snap for newly created measures on this track.");
        snap.ItemSelected += index =>
        {
            MaterializeTrackSnapDefaults(trackId);
            var selectedSnap = snap.GetItemId((int)index);
            var track = _score.Tracks.FirstOrDefault(item => item.Id == trackId);
            if (track is not null) track.TabSnapDenominator = selectedSnap;
            _tabSnapDenominators[trackId] = selectedSnap;
            SetActiveEditorTrack(trackId);
            MarkEditorStateDirty();
            NotifyScoreEdited();
            RenderSection();
        };
        var navigation = controls.GetNode<HBoxContainer>("PageNavigation");
        var previous = navigation.GetNode<Button>("Previous");
        var next = navigation.GetNode<Button>("Next");
        var label = navigation.GetNode<Label>("PageLabel");
        previous.TooltipText = Tr("Scroll to the previous measures in the selected sections.");
        next.TooltipText = Tr("Scroll to the next measures in the selected sections.");
        label.CustomMinimumSize = new Vector2(96, label.CustomMinimumSize.Y);
        controls.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        navigation.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var bar = scroll.GetHScrollBar();
        void UpdateNavigation()
        {
            if (!GodotObject.IsInstanceValid(controls)) return;
            var max = Math.Max(0, bar.MaxValue - bar.Page);
            navigation.Visible = multiSection || max > 1;
            previous.Disabled = bar.Value <= 0;
            next.Disabled = bar.Value >= max - 1;
            label.Text = GetVisibleMeasureRange(scroll);
            label.TooltipText = label.Text;
        }
        bar.Changed += UpdateNavigation;
        bar.ValueChanged += _ => QueueNavigationUpdate();
        void QueueNavigationUpdate() => Callable.From(UpdateNavigation).CallDeferred();
        scroll.Resized += QueueNavigationUpdate;
        var sectionHost = scroll.GetNode<HBoxContainer>("Sections");
        sectionHost.Resized += QueueNavigationUpdate;
        previous.Pressed += () => scroll.ScrollHorizontal = Math.Max(0, scroll.ScrollHorizontal - (int)scroll.Size.X);
        next.Pressed += () => scroll.ScrollHorizontal += (int)scroll.Size.X;
        UpdateNavigation();
        QueueNavigationUpdate();
    }

    private static string GetVisibleMeasureRange(ScrollContainer scroll)
    {
        var sections = scroll.GetNodeOrNull<HBoxContainer>("Sections");
        if (sections is null) return TranslationServer.Translate("Measures");
        var viewport = scroll.GetGlobalRect();
        var ranges = new List<string>();
        foreach (var section in sections.GetChildren().OfType<Control>())
        {
            var measures = section.GetNodeOrNull<HBoxContainer>("VBoxContainer/Measures");
            if (measures is null) continue;
            var localIndex = 0;
            var first = int.MaxValue;
            var last = 0;
            foreach (var column in measures.GetChildren().OfType<Control>())
            {
                var count = column.HasMeta("section_remainder_count") ? Math.Max(1, column.GetMeta("section_remainder_count").AsInt32()) : 1;
                var rect = column.GetGlobalRect();
                var slice = TabPaginationGeometry.VisibleSlice(rect.Position.X, rect.Size.X, count, viewport.Position.X, viewport.End.X);
                if (slice is { } visible)
                {
                    first = Math.Min(first, localIndex + visible.First + 1);
                    last = Math.Max(last, localIndex + visible.Last + 1);
                }
                localIndex += count;
            }
            if (last > 0)
            {
                var marker = section.GetNodeOrNull<Label>("VBoxContainer/SectionMarker");
                var sectionName = marker?.Text is { Length: > 0 } text ? text : section.Name.ToString();
                var range = first == last
                    ? string.Format(TranslationServer.Translate("Measure {0}"), first)
                    : string.Format(TranslationServer.Translate("Measures {0}–{1}"), first, last);
                ranges.Add(string.Format(TranslationServer.Translate("{0} · {1}"), sectionName, range));
            }
        }
        return ranges.Count == 0 ? "—" : string.Join(" | ", ranges);
    }

    private void SizeTrackTabMeasures(ScrollContainer scroll, HBoxContainer sections, int snap)
    {
        var measures = sections.GetChildren().OfType<Control>()
            .Select(section => section.GetNodeOrNull<HBoxContainer>("VBoxContainer/Measures"))
            .Where(group => group is not null).ToArray();
        int count = measures.Sum(group => group!.GetChildren().OfType<Control>()
            .Sum(column => column.HasMeta("section_remainder_count") ? Math.Max(1, column.GetMeta("section_remainder_count").AsInt32()) : 1));
        float fit = count > 0 ? Math.Max(0, scroll.Size.X - 16 - count * 4) / count : 0;
        foreach (var group in measures)
        {
            foreach (var column in group!.GetChildren().OfType<Control>())
            {
                var view = column.GetNodeOrNull<MeasureTabView>("Content/MeasureTabView");
                double duration = view?.ViewData?.MeasureDuration.Value ?? 1;
                var minimumCellWidth = view?.MinimumCellWidth ?? GetTabThemeConstant("minimum_cell_width", 24f, 1f, 500f);
                int measureSnap = view is null ? snap : view.SnapDenominator;
                float comfortable = 40 + (float)(duration * measureSnap * minimumCellWidth);
                int remainder = column.HasMeta("section_remainder_count") ? Math.Max(1, column.GetMeta("section_remainder_count").AsInt32()) : 1;
                float headerWidth = column.HasMeta("full_tab_header_width") ? column.GetMeta("full_tab_header_width").AsSingle() : 0;
                float width = Math.Max(headerWidth, Math.Max(fit, Math.Max(TabMeasureMinimumWidth, comfortable)) * remainder);
                column.CustomMinimumSize = new Vector2(width, column.CustomMinimumSize.Y);
                if (view is not null) view.CustomMinimumSize = new Vector2(width, view.CustomMinimumSize.Y);
            }
        }
    }
}

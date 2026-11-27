#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

/// <summary>Keeps the banner strip in score order and reconnects rebuilt editor rows.</summary>
public partial class TrackBannerManager : PanelContainer
{
    [Export] private BoxContainer _banners = null!;
    [Export] private PackedScene _bannerScene = null!;
    [Export] private Button _selectAll = null!;
    [Export] private Button _selectNone = null!;

    private readonly Dictionary<Guid, TrackBinding> _bindings = new();

    public override void _Ready()
    {
        _selectAll.Pressed += SelectAll;
        _selectNone.Pressed += SelectNone;
    }

    public override void _ExitTree()
    {
        _selectAll.Pressed -= SelectAll;
        _selectNone.Pressed -= SelectNone;
        foreach (var binding in _bindings.Values) binding.Dispose();
        _bindings.Clear();
    }

    public void SyncRows(IReadOnlyList<(ScoreTrack Track, Control Row)> rows)
    {
        var trackIds = rows.Select(entry => entry.Track.Id).ToHashSet();
        foreach (var id in _bindings.Keys.Where(id => !trackIds.Contains(id)).ToArray())
        {
            var binding = _bindings[id];
            _bindings.Remove(id);
            binding.Dispose();
            _banners.RemoveChild(binding.Banner);
            binding.Banner.QueueFree();
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var (track, row) = rows[index];
            if (!_bindings.TryGetValue(track.Id, out var binding))
            {
                var banner = _bannerScene.Instantiate<TrackBanner>();
                _banners.AddChild(banner);
                binding = new TrackBinding(banner, row);
                _bindings.Add(track.Id, binding);
            }
            else
            {
                binding.ReplaceRow(row);
            }
            _banners.MoveChild(binding.Banner, index);
            RefreshTrack(track);
        }
    }

    public void RefreshTrack(ScoreTrack track)
    {
        if (!_bindings.TryGetValue(track.Id, out var binding)) return;
        binding.Banner.SetTrackName(track.Name);
        var imagePath = TrackBannerArt.ResolvePath(track);
        if (binding.ImagePath == imagePath) return;
        binding.Banner.SetImage(GD.Load<Texture2D>(imagePath));
        binding.ImagePath = imagePath;
    }

    private void SelectAll() => SetAllVisible(true);
    private void SelectNone() => SetAllVisible(false);

    private void SetAllVisible(bool visible)
    {
        foreach (var id in _bindings.Keys.ToArray())
            if (_bindings.TryGetValue(id, out var binding)) binding.SetVisible(visible);
    }

    private sealed class TrackBinding : IDisposable
    {
        public TrackBanner Banner { get; }
        public string? ImagePath { get; set; }
        private Control _row;
        private bool _visible;

        public TrackBinding(TrackBanner banner, Control row)
        {
            Banner = banner;
            _row = row;
            Banner.SelectionChanged += SetVisible;
            _row.VisibilityChanged += OnRowVisibilityChanged;
            OnRowVisibilityChanged();
        }

        public void ReplaceRow(Control row)
        {
            if (GodotObject.IsInstanceValid(_row)) _row.VisibilityChanged -= OnRowVisibilityChanged;
            _row = row;
            _row.Visible = _visible;
            _row.VisibilityChanged += OnRowVisibilityChanged;
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            Banner.SetSelected(visible);
            if (GodotObject.IsInstanceValid(_row)) _row.Visible = visible;
        }

        private void OnRowVisibilityChanged()
        {
            _visible = _row.Visible;
            Banner.SetSelected(_visible);
        }

        public void Dispose()
        {
            Banner.SelectionChanged -= SetVisible;
            if (GodotObject.IsInstanceValid(_row)) _row.VisibilityChanged -= OnRowVisibilityChanged;
        }
    }
}

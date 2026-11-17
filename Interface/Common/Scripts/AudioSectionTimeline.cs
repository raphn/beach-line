#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

/// <summary>Scrollable-content timeline surface for audio clips. It previews gestures and delegates edits to its owner.</summary>
public partial class AudioSectionTimeline : Control
{
    private static AudioSectionTimeline? s_cursorOwner;
    private sealed class ClipEntry
    {
        public required Guid Id;
        public required double Start;
        public required double Duration;
        public required Control HitArea;
        public required AudioWaveformPreview Preview;
    }

    private readonly List<ClipEntry> _clips = new();
    private readonly List<double> _measureGuides = new();
    private readonly List<double> _tempoGuides = new();
    private readonly Texture2D? _scissorsCursor = LoadScissorsCursor();
    private readonly AudioSectionTimelineOverlay _overlay;
    private double _pixelsPerSecond = 80;
    private double _timelineDurationSeconds;
    private bool _guidesVisible = true;
    private bool _scissorsEnabled;
    private ClipEntry? _hovered;
    private ClipEntry? _dragging;
    private ClipEntry? _trimming;
    private bool _trimLeft;
    private double _trimStartSeconds;
    private double _trimDurationSeconds;
    private double _previewTrimBoundarySeconds;
    private double _trimOriginMouseX;
    private double _dragStartSeconds;
    private double _dragOriginMouseX;
    private double _previewStartSeconds;
    private Vector2 _mousePosition;

    /// <summary>Fired while dragging, with the proposed timeline start in seconds.</summary>
    public event Action<Guid, double>? ClipMovePreviewRequested;
    /// <summary>Fired once on release; the owner should validate and commit this move as one undoable edit.</summary>
    public event Action<Guid, double>? ClipMoveCommitted;
    /// <summary>Fired when Escape cancels an active move.</summary>
    public event Action<Guid>? ClipMoveCanceled;
    /// <summary>Fired while trimming. leftEdge=true selects the start boundary; boundarySeconds is timeline time.</summary>
    public event Action<Guid, bool, double>? ClipTrimPreviewRequested;
    /// <summary>Fired once when a trim is released.</summary>
    public event Action<Guid, bool, double>? ClipTrimCommitted;
    /// <summary>Fired when Escape cancels an active trim.</summary>
    public event Action<Guid, bool>? ClipTrimCanceled;
    /// <summary>Fired when scissors mode cuts at a clip-local time in seconds.</summary>
    public event Action<Guid, double>? CutRequested;

    public AudioSectionTimeline()
    {
        ClipContentsMinimumSize = new Vector2(240, 64);
        CustomMinimumSize = new Vector2(240, 64);
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Pass;
        _overlay = new AudioSectionTimelineOverlay { Name = "TimelineOverlay", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_overlay);
        SetProcessInput(true);
    }

    /// <summary>Minimum horizontal content extent; a parent ScrollContainer can scroll this control.</summary>
    public Vector2 ClipContentsMinimumSize
    {
        get => CustomMinimumSize;
        set { CustomMinimumSize = new Vector2(Math.Max(0, value.X), Math.Max(64, value.Y)); Refresh(); }
    }

    /// <summary>Scale in pixels per elapsed second. Waveform playback remains at fixed speed.</summary>
    public void SetZoom(double pixelsPerSecond)
    {
        if (!double.IsFinite(pixelsPerSecond) || pixelsPerSecond <= 0) return;
        _pixelsPerSecond = Math.Clamp(pixelsPerSecond, 8, 1200);
        LayoutClips();
    }

    /// <summary>Sets faint measure and tempo-change marks in elapsed timeline seconds.</summary>
    public void SetGuides(IEnumerable<double> measureSeconds, IEnumerable<double> tempoSeconds)
    {
        _measureGuides.Clear();
        _tempoGuides.Clear();
        _measureGuides.AddRange(measureSeconds.Where(double.IsFinite).Where(x => x >= 0).Distinct().OrderBy(x => x));
        _tempoGuides.AddRange(tempoSeconds.Where(double.IsFinite).Where(x => x >= 0).Distinct().OrderBy(x => x));
        Refresh();
    }

    public void SetGuidesVisible(bool visible) { _guidesVisible = visible; Refresh(); }

    /// <summary>Sets the section end in seconds, used to bound the scrollable surface and clip overhanging children.</summary>
    public void SetTimelineDuration(double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0) return;
        _timelineDurationSeconds = durationSeconds;
        LayoutClips();
    }

    /// <summary>Enables click-to-cut behavior and the scissors cursor while hovering a clip.</summary>
    public void SetScissorsEnabled(bool enabled)
    {
        _scissorsEnabled = enabled;
        UpdateCursor();
        Refresh();
    }

    /// <summary>Adds a waveform preview. The widget takes ownership of the preview node and reparents it here.</summary>
    public void AddClip(Guid id, double startSeconds, double durationSeconds, AudioWaveformPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (id == Guid.Empty || !double.IsFinite(startSeconds) || !double.IsFinite(durationSeconds) || startSeconds < 0 || durationSeconds <= 0)
            throw new ArgumentException("Clip id, start, and duration must describe a valid timeline clip.");
        RemoveClip(id);
        var hitArea = new Control { Name = $"Clip_{id:N}", MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(0, 58) };
        AddChild(hitArea);
        if (preview.GetParent() is Node oldParent) oldParent.RemoveChild(preview);
        preview.WaveformHorizontalInset = 0;
        hitArea.ClipContents = true;
        hitArea.AddChild(preview);
        preview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        hitArea.GuiInput += e => OnClipInput(hitArea, e);
        var entry = new ClipEntry { Id = id, Start = startSeconds, Duration = durationSeconds, HitArea = hitArea, Preview = preview };
        _clips.Add(entry);
        hitArea.MouseEntered += () => { _hovered = entry; UpdateCursor(); Refresh(); };
        hitArea.MouseExited += () => { if (_hovered == entry) _hovered = null; UpdateCursor(); Refresh(); };
        LayoutClips();
    }

    public bool RemoveClip(Guid id)
    {
        var entry = _clips.FirstOrDefault(item => item.Id == id);
        if (entry is null) return false;
        if (_dragging == entry) CancelDrag();
        if (_hovered == entry) _hovered = null;
        _clips.Remove(entry);
        entry.HitArea.QueueFree();
        LayoutClips();
        return true;
    }

    public void ClearClips()
    {
        CancelDrag();
        foreach (var entry in _clips) entry.HitArea.QueueFree();
        _clips.Clear();
        _hovered = null;
        LayoutClips();
    }

    /// <summary>Updates the displayed start after the owner accepts/rejects a move.</summary>
    public bool SetClipStart(Guid id, double startSeconds)
    {
        var entry = _clips.FirstOrDefault(item => item.Id == id);
        if (entry is null || !double.IsFinite(startSeconds) || startSeconds < 0) return false;
        entry.Start = startSeconds;
        LayoutClips();
        return true;
    }

    /// <summary>Changes source duration without removing or reloading the waveform preview.</summary>
    public bool SetClipDuration(Guid id, double durationSeconds)
    {
        var entry = _clips.FirstOrDefault(item => item.Id == id);
        if (entry is null || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return false;
        entry.Duration = durationSeconds;
        LayoutClips();
        return true;
    }

    public override void _Draw()
    {
        // The overlay child draws after the opaque waveform controls.
    }

    internal void DrawOverlay(CanvasItem canvas)
    {
        if (_guidesVisible)
        {
            foreach (var x in _measureGuides.Select(seconds => (float)(seconds * _pixelsPerSecond)))
                canvas.DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), new Color(0.58f, 0.68f, 0.72f, 0.17f), 1f);
            foreach (var x in _tempoGuides.Select(seconds => (float)(seconds * _pixelsPerSecond)))
                canvas.DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), new Color(0.94f, 0.65f, 0.31f, 0.24f), 1f, true);
        }
        var lineClip = _dragging ?? (_scissorsEnabled ? _hovered : null);
        if (lineClip is not null)
        {
            var visibleWidth = Math.Max(1f, lineClip.HitArea.Size.X);
            var localSeconds = Math.Clamp((_mousePosition.X - lineClip.HitArea.Position.X) / visibleWidth * lineClip.Duration, 0, lineClip.Duration);
            var x = _dragging is not null
                ? (float)(_previewStartSeconds * _pixelsPerSecond + localSeconds / lineClip.Duration * visibleWidth)
                : _mousePosition.X;
            canvas.DrawLine(new Vector2(x, 0), new Vector2(x, Size.Y), new Color(1f, 0.78f, 0.35f, 0.9f), 2f);
        }
        var handleClips = _clips.Where(item => _trimming == item || (!_scissorsEnabled && _dragging != item));
        foreach (var handleClip in handleClips)
        {
            var x0 = (float)((_trimming == handleClip && _trimLeft ? _previewTrimBoundarySeconds : handleClip.Start) * _pixelsPerSecond);
            var x1 = (float)((_trimming == handleClip && !_trimLeft ? _previewTrimBoundarySeconds : handleClip.Start + handleClip.Duration) * _pixelsPerSecond);
            if (_timelineDurationSeconds > 0) x1 = Math.Min(x1, (float)(_timelineDurationSeconds * _pixelsPerSecond));
            var leftVisible = x0 >= 0 && x0 <= Size.X;
            var rightVisible = x1 >= 0 && x1 <= Size.X;
            DrawTrimHandle(canvas, x0, leftVisible, _trimming == handleClip && _trimLeft, Size.Y);
            DrawTrimHandle(canvas, x1, rightVisible, _trimming == handleClip && !_trimLeft, Size.Y);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if ((_dragging is not null || _trimming is not null) && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (_trimming is not null) CancelTrim(); else CancelDrag();
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseMotion motion && _trimming is not null && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            UpdateTrim(motion.GlobalPosition.X);
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } && _trimming is not null)
        {
            CommitTrim();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) ResizeChildren();
    }

    public override void _ExitTree() => RestoreCursor(this);

    private void OnClipInput(Control hitArea, InputEvent @event)
    {
        var entry = _clips.FirstOrDefault(item => item.HitArea == hitArea);
        if (entry is null) return;
        if (@event is InputEventMouseMotion motion)
        {
            _mousePosition = GetLocalMousePosition();
            if (_hovered == entry && _trimming is null && _dragging is null) UpdateCursor();
            if (_trimming == entry && Input.IsMouseButtonPressed(MouseButton.Left))
            {
                UpdateTrim(motion.GlobalPosition.X);
                return;
            }
            if (_dragging == entry && Input.IsMouseButtonPressed(MouseButton.Left))
            {
                var proposed = Math.Max(0, _dragStartSeconds + (motion.GlobalPosition.X - _dragOriginMouseX) / _pixelsPerSecond);
                _previewStartSeconds = proposed;
                entry.HitArea.Position = new Vector2((float)(proposed * _pixelsPerSecond), entry.HitArea.Position.Y);
                ClipMovePreviewRequested?.Invoke(entry.Id, proposed);
            }
            Refresh();
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } left)
        {
            _mousePosition = GetLocalMousePosition();
            if (_scissorsEnabled)
            {
                var localX = left.GlobalPosition.X - entry.HitArea.GlobalPosition.X;
                var local = Math.Clamp(localX / _pixelsPerSecond, 0, entry.Duration);
                if (local > 0 && local < entry.Duration) CutRequested?.Invoke(entry.Id, local);
            }
            else
            {
                var localX = left.GlobalPosition.X - entry.HitArea.GlobalPosition.X;
                var handleWidth = Math.Min(HandleHitWidth, entry.HitArea.Size.X / 2);
                var edgeDistance = Math.Min(localX, entry.HitArea.Size.X - localX);
                if (edgeDistance <= handleWidth)
                {
                    _trimming = entry;
                    _trimLeft = localX <= entry.HitArea.Size.X / 2;
                    _trimStartSeconds = entry.Start;
                    _trimDurationSeconds = entry.Duration;
                    _previewTrimBoundarySeconds = _trimLeft ? entry.Start : VisibleEnd(entry);
                    _trimOriginMouseX = left.GlobalPosition.X;
                    UpdateCursor();
                }
                else
                {
                    _dragging = entry;
                    _dragStartSeconds = entry.Start;
                    _previewStartSeconds = entry.Start;
                    _dragOriginMouseX = left.GlobalPosition.X;
                }
            }
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            if (_dragging == entry)
            {
                var moved = Math.Abs(_previewStartSeconds - _dragStartSeconds) > 0.000001;
                _dragging = null;
                if (moved) ClipMoveCommitted?.Invoke(entry.Id, _previewStartSeconds);
                LayoutClips();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void CancelDrag()
    {
        if (_dragging is null) return;
        var entry = _dragging;
        _dragging = null;
        entry.HitArea.Position = new Vector2((float)(entry.Start * _pixelsPerSecond), entry.HitArea.Position.Y);
        ClipMoveCanceled?.Invoke(entry.Id);
        Refresh();
    }

    private const float HandleHitWidth = 10f;

    private void UpdateTrim(double globalX)
    {
        if (_trimming is null) return;
        var entry = _trimming;
        var initialBoundary = _trimLeft ? _trimStartSeconds : Math.Min(_trimStartSeconds + _trimDurationSeconds, _timelineDurationSeconds > 0 ? _timelineDurationSeconds : double.MaxValue);
        var proposed = Math.Max(0, initialBoundary + (globalX - _trimOriginMouseX) / _pixelsPerSecond);
        if (_trimLeft) proposed = Math.Min(proposed, Math.Max(0, Math.Min(_trimStartSeconds + _trimDurationSeconds, _timelineDurationSeconds > 0 ? _timelineDurationSeconds : double.MaxValue) - MinClipDurationSeconds));
        else proposed = Math.Max(proposed, _trimStartSeconds + MinClipDurationSeconds);
        if (_timelineDurationSeconds > 0) proposed = Math.Min(proposed, _timelineDurationSeconds);
        _previewTrimBoundarySeconds = proposed;
        var start = _trimLeft ? proposed : _trimStartSeconds;
        var end = _trimLeft ? Math.Min(_trimStartSeconds + _trimDurationSeconds, _timelineDurationSeconds > 0 ? _timelineDurationSeconds : double.MaxValue) : proposed;
        entry.HitArea.Position = new Vector2((float)(start * _pixelsPerSecond), entry.HitArea.Position.Y);
        entry.HitArea.Size = new Vector2((float)((end - start) * _pixelsPerSecond), entry.HitArea.Size.Y);
        entry.Preview.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
        entry.Preview.Position = new Vector2((float)((_trimStartSeconds - start) * _pixelsPerSecond), 0);
        entry.Preview.Size = new Vector2((float)(_trimDurationSeconds * _pixelsPerSecond), entry.HitArea.Size.Y);
        ClipTrimPreviewRequested?.Invoke(entry.Id, _trimLeft, proposed);
        Refresh();
    }

    private const double MinClipDurationSeconds = 0.000001;

    private void CommitTrim()
    {
        if (_trimming is null) return;
        var entry = _trimming;
        var boundary = _previewTrimBoundarySeconds;
        var left = _trimLeft;
        _trimming = null;
        var initialBoundary = left ? _trimStartSeconds : Math.Min(_trimStartSeconds + _trimDurationSeconds, _timelineDurationSeconds > 0 ? _timelineDurationSeconds : double.MaxValue);
        var changed = Math.Abs(boundary - initialBoundary) > 0.000001;
        if (changed) ClipTrimCommitted?.Invoke(entry.Id, left, boundary);
        LayoutClips();
        UpdateCursor();
    }

    private void CancelTrim()
    {
        if (_trimming is null) return;
        var entry = _trimming;
        var left = _trimLeft;
        _trimming = null;
        entry.Start = _trimStartSeconds;
        entry.Duration = _trimDurationSeconds;
        ClipTrimCanceled?.Invoke(entry.Id, left);
        LayoutClips();
        UpdateCursor();
    }

    private static void DrawTrimHandle(CanvasItem canvas, float x, bool visible, bool active, float height)
    {
        if (!visible) return;
        var color = active ? new Color(0.4f, 1f, 1f, 1f) : new Color(0.2f, 0.92f, 0.94f, 0.95f);
        canvas.DrawRect(new Rect2(x - 3, 4, 6, 14), color);
        canvas.DrawLine(new Vector2(x, 18), new Vector2(x, Math.Max(18, height - 4)), new Color(color, 0.72f), 2f);
    }

    private void LayoutClips()
    {
        foreach (var entry in _clips)
        {
            var x = _dragging == entry ? _previewStartSeconds * _pixelsPerSecond : entry.Start * _pixelsPerSecond;
            entry.HitArea.Position = new Vector2((float)x, 3);
            var visibleDuration = Math.Max(0, VisibleEnd(entry) - entry.Start);
            entry.HitArea.Size = new Vector2((float)(visibleDuration * _pixelsPerSecond), Math.Max(58, Size.Y - 6));
            if (_trimming != entry)
            {
                entry.Preview.SetAnchorsAndOffsetsPreset(LayoutPreset.TopLeft);
                entry.Preview.Position = Vector2.Zero;
                entry.Preview.Size = new Vector2((float)(entry.Duration * _pixelsPerSecond), entry.HitArea.Size.Y);
            }
        }
        var clipEnd = _clips.Count == 0 ? 0 : _clips.Max(item => item.Start + item.Duration);
        var contentWidth = (_timelineDurationSeconds > 0 ? _timelineDurationSeconds : clipEnd) * _pixelsPerSecond;
        ClipContentsMinimumSize = new Vector2((float)Math.Max(_timelineDurationSeconds > 0 ? 1 : 240, contentWidth), Math.Max(64, Size.Y));
        ResizeChildren();
        MoveChild(_overlay, GetChildCount() - 1);
        Refresh();
    }

    private void ResizeChildren()
    {
        if (_overlay is null) return;
        foreach (var entry in _clips)
        {
            entry.HitArea.Size = new Vector2(entry.HitArea.Size.X, Math.Max(58, Size.Y - 6));
            if (_trimming != entry) entry.Preview.Size = new Vector2(entry.Preview.Size.X, entry.HitArea.Size.Y);
        }
        _overlay.Position = Vector2.Zero;
        _overlay.Size = Size;
        Refresh();
    }

    private double VisibleEnd(ClipEntry entry) => Math.Min(entry.Start + entry.Duration, _timelineDurationSeconds > 0 ? _timelineDurationSeconds : double.MaxValue);

    private void Refresh()
    {
        QueueRedraw();
        if (GodotObject.IsInstanceValid(_overlay)) _overlay.QueueRedraw();
    }

    private void UpdateCursor()
    {
        // A hovered Control's cursor shape takes precedence over Input's default.
        // Set it on the clip hit area as well as the global cursor during gestures.
        if (_trimming is not null)
        {
            _trimming.HitArea.MouseDefaultCursorShape = CursorShape.Hsize;
            s_cursorOwner = this;
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
            Input.SetDefaultCursorShape(Input.CursorShape.Hsize);
            return;
        }
        if (_scissorsEnabled && _hovered is not null && _scissorsCursor is not null)
        {
            _hovered.HitArea.MouseDefaultCursorShape = CursorShape.Arrow;
            s_cursorOwner = this;
            Input.SetCustomMouseCursor(_scissorsCursor, Input.CursorShape.Arrow, new Vector2(2, 2));
        }
        else if (_scissorsEnabled && _hovered is not null)
        {
            _hovered.HitArea.MouseDefaultCursorShape = CursorShape.Cross;
            s_cursorOwner = this;
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
            Input.SetDefaultCursorShape(Input.CursorShape.Cross);
        }
        else if (!_scissorsEnabled && _hovered is not null && IsNearHoveredEdge())
        {
            _hovered.HitArea.MouseDefaultCursorShape = CursorShape.Hsize;
            s_cursorOwner = this;
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
            Input.SetDefaultCursorShape(Input.CursorShape.Hsize);
        }
        else if (!_scissorsEnabled && _hovered is not null)
        {
            _hovered.HitArea.MouseDefaultCursorShape = CursorShape.Move;
            s_cursorOwner = this;
            Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
            Input.SetDefaultCursorShape(Input.CursorShape.Move);
        }
        else RestoreCursor(this);
    }

    private bool IsNearHoveredEdge()
    {
        if (_hovered is null) return false;
        var x = GetGlobalMousePosition().X - _hovered.HitArea.GlobalPosition.X;
        var edgeWidth = Math.Min(HandleHitWidth, _hovered.HitArea.Size.X / 2);
        return Math.Min(x, _hovered.HitArea.Size.X - x) <= edgeWidth;
    }

    private static Texture2D? LoadScissorsCursor()
    {
        var image = GD.Load<Texture2D>("res://Interface/Common/Graphs/audio/scissors_lorc.png")?.GetImage();
        if (image is null || image.IsEmpty()) return null;
        image.Resize(32, 32, Image.Interpolation.Lanczos);
        return ImageTexture.CreateFromImage(image);
    }

    private static void RestoreCursor(AudioSectionTimeline requester)
    {
        if (!ReferenceEquals(s_cursorOwner, requester)) return;
        s_cursorOwner = null;
        Input.SetCustomMouseCursor(null, Input.CursorShape.Arrow);
        Input.SetDefaultCursorShape(Input.CursorShape.Arrow);
    }
}

/// <summary>Topmost, mouse-transparent drawing layer for timeline guides and gesture markers.</summary>
public partial class AudioSectionTimelineOverlay : Control
{
    public override void _Draw()
    {
        if (GetParent() is AudioSectionTimeline timeline) timeline.DrawOverlay(this);
    }
}

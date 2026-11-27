#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public partial class FoldedTrackPreview : Node2D
{
    public readonly record struct Note(double Start, double End, int MidiPitch);

    private const float HorizontalInset = 10f;
    private const float VerticalInset = 5f;
    private static readonly Color NoteColor = new(0.95f, 0.98f, 1f, 0.40f);
    private ColorRect _panel = null!;
    private IReadOnlyList<Note> _notes = Array.Empty<Note>();
    private double _duration;
    private int _lowestPitch;
    private int _highestPitch;

    public override void _Ready() => _panel = GetNode<ColorRect>("GlossyPanel");

    public void SetBounds(Rect2 bounds)
    {
        Position = bounds.Position;
        _panel.Size = bounds.Size;
        ((ShaderMaterial)_panel.Material).SetShaderParameter("panel_size", bounds.Size);
        QueueRedraw();
    }

    public void SetColor(Color color) => _panel.Color = new Color(color.R, color.G, color.B, 1f);

    public void SetNotes(IReadOnlyList<Note> notes, double duration)
    {
        _notes = notes;
        _duration = duration;
        _lowestPitch = notes.Count == 0 ? 0 : notes.Min(note => note.MidiPitch);
        _highestPitch = notes.Count == 0 ? 0 : notes.Max(note => note.MidiPitch);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var width = _panel.Size.X - HorizontalInset * 2;
        var height = _panel.Size.Y - VerticalInset * 2;
        if (width <= 0 || height <= 0 || _duration <= 0) return;

        var pitchRange = _highestPitch - _lowestPitch;
        var noteHeight = Math.Min(3f, height / (pitchRange + 1));
        foreach (var note in _notes)
        {
            var start = (float)(Math.Clamp(note.Start / _duration, 0, 1) * width);
            var end = (float)(Math.Clamp(note.End / _duration, 0, 1) * width);
            if (end <= start) continue;
            var pitch = pitchRange == 0 ? 0.5f : (float)(_highestPitch - note.MidiPitch) / pitchRange;
            var x = HorizontalInset + start;
            var y = VerticalInset + pitch * (height - noteHeight);
            var noteWidth = Math.Min(width - start, Math.Max(1f, end - start));
            DrawRect(new Rect2(x, y, noteWidth, noteHeight), NoteColor);
        }
    }
}

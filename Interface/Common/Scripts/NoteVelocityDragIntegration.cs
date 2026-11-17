#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private TabVelocityGesture? _velocityDrag;
    private ScoreDocument? _velocityDragScore;
    private ScoreTrack? _velocityDragTrack;
    private (TrackMeasure Source, TabEvent Note)[] _velocityDragTargets = Array.Empty<(TrackMeasure, TabEvent)>();
    private NoteVelocityDragFeedback? _velocityDragFeedback;
    private int _velocityDragDelta;

    private void BeginVelocityDrag(ScoreTrack track, Dictionary<string, TabOccurrence> mappings,
        IReadOnlyList<MeasureTabView.TabNoteViewData> selection, Vector2 pointer)
    {
        FinishVelocityDrag(cancel: true);
        if (!_score.Tracks.Contains(track) || selection.Count == 0 || selection.Any(note => !mappings.ContainsKey(note.Id))) return;
        var targets = selection.Select(note => (mappings[note.Id].Source, mappings[note.Id].Note)).Distinct().ToArray();
        try { _velocityDrag = new TabVelocityGesture(targets); }
        catch (InvalidOperationException) { return; }
        _velocityDragScore = _score;
        _velocityDragTrack = track;
        _velocityDragTargets = targets;
        _velocityDragDelta = 0;
        if (_velocityDragFeedback is null || !GodotObject.IsInstanceValid(_velocityDragFeedback))
        {
            _velocityDragFeedback = GD.Load<PackedScene>("res://Interface/Tab/Scenes/NoteVelocityDragFeedback.tscn").Instantiate<NoteVelocityDragFeedback>();
            AddChild(_velocityDragFeedback);
        }
        var (minimum, maximum) = CurrentDragLevels();
        _velocityDragFeedback.ShowAt(pointer, minimum, targets.Length, maximum);
    }

    private (int Minimum, int Maximum) CurrentDragLevels()
    {
        var levels = _velocityDragTargets.Select(target => target.Note.Intensity).ToArray();
        return (levels.Min(), levels.Max());
    }

    private void UpdateVelocityDrag(int delta, Vector2 pointer)
    {
        if (_velocityDrag is null) return;
        if (!ReferenceEquals(_score, _velocityDragScore) || _velocityDragTrack is null || !_score.Tracks.Contains(_velocityDragTrack))
        {
            FinishVelocityDrag(cancel: true);
            return;
        }
        if (delta != 0)
        {
            // Keep the full pointer offset even after values saturate, so moving back
            // to the starting position restores the original velocities.
            _velocityDragDelta = (int)Math.Clamp((long)_velocityDragDelta + delta, int.MinValue, int.MaxValue);
            try { _velocityDrag.ApplyDelta(_velocityDragDelta); }
            catch (InvalidOperationException) { FinishVelocityDrag(cancel: true); return; }
            RefreshVelocityDragViews();
            RefreshVelocityDragAudio();
        }
        var (minimum, maximum) = CurrentDragLevels();
        _velocityDragFeedback?.UpdateFeedback(pointer, minimum, _velocityDragTargets.Length, maximum);
    }

    private void FinishVelocityDrag(bool cancel)
    {
        var gesture = _velocityDrag;
        if (gesture is null) return;
        _velocityDrag = null; // Clear before rendering or emitting any callbacks.
        var sameScore = ReferenceEquals(_score, _velocityDragScore);
        var changed = gesture.HasChanges;
        if (cancel || !sameScore)
        {
            try { gesture.Cancel(); }
            catch (InvalidOperationException) { /* A removed target must not cause a partial restore. */ }
            if (sameScore) { RefreshVelocityDragViews(); RefreshVelocityDragAudio(); }
        }
        else if (changed) NotifyScoreEdited();
        _velocityDragFeedback?.Dismiss();
        _velocityDragTargets = Array.Empty<(TrackMeasure, TabEvent)>();
        _velocityDragScore = null;
        _velocityDragTrack = null;
        _velocityDragDelta = 0;
    }

    private void RefreshVelocityDragViews()
    {
        var edited = _velocityDragTargets.Select(target => target.Note).ToHashSet();
        foreach (var (view, context) in _tabClipboardViews)
        {
            if (!GodotObject.IsInstanceValid(view) || view.ViewData is not { } data) continue;
            var levels = context.Occurrences.Where(pair => edited.Contains(pair.Value.Note))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Note.Intensity, StringComparer.Ordinal);
            if (levels.Count == 0) continue;
            view.Bind(data with { Notes = data.Notes.Select(note => levels.TryGetValue(note.Id, out var level)
                ? note with { VelocityLevel = level } : note).ToArray() });
        }
    }

    private void RefreshVelocityDragAudio()
    {
        try { _soundFontAudio.RefreshScore(_score); _arrangementAudio?.RefreshScore(_score); }
        catch (Exception exception) { GD.PushWarning($"Could not refresh playback during velocity editing: {exception.Message}"); }
    }
}

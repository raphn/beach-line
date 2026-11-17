#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private NoteExpressionCurveEditor? _expressionEditor;
    private ScoreDocument? _expressionScore;
    private ScoreTrack? _expressionTrack;
    private TabOccurrence[] _expressionSelection = Array.Empty<TabOccurrence>();

    private void OpenNoteExpressionEditor()
    {
        _soundFontAudio.StopNoteExpressionPreview();
        if (_expressionEditor is not null && GodotObject.IsInstanceValid(_expressionEditor)) _expressionEditor.SetPreviewEnded();
        var track = _velocityTrack;
        var selection = _expressionPendingOccurrences.GroupBy(item => item.Note).Select(group => group.First()).ToArray();
        if (track is null || !_score.Tracks.Contains(track) || selection.Length == 0) return;
        _expressionScore = _score;
        _expressionTrack = track;
        _expressionSelection = selection;
        if (_expressionEditor is null || !GodotObject.IsInstanceValid(_expressionEditor))
        {
            _expressionEditor = GD.Load<PackedScene>("res://Interface/Tab/Scenes/NoteExpressionCurveEditor.tscn").Instantiate<NoteExpressionCurveEditor>();
            AddChild(_expressionEditor);
            _expressionEditor.Applied += ApplyNoteExpression;
            _expressionEditor.Removed += RemoveNoteExpression;
            _expressionEditor.PreviewRequested += PreviewNoteExpression;
            _expressionEditor.PreviewStopped += _soundFontAudio.StopNoteExpressionPreview;
            _expressionEditor.LoopChanged += enabled => _soundFontAudio.NoteExpressionPreviewLoopEnabled = enabled;
            _soundFontAudio.NoteExpressionPreviewFinished += () =>
            {
                if (_expressionEditor is not null && GodotObject.IsInstanceValid(_expressionEditor))
                    _expressionEditor.SetPreviewEnded();
            };
        }
        var definitions = selection.Select(item => (IReadOnlyList<NoteExpression>)item.Note.Expressions.Select(curve => curve.Clone()).ToArray()).ToArray();
        var first = selection.Min(item => item.Start);
        var last = selection.Max(item => item.Start);
        var noteDuration = TimeValue(selection[0].Note.Duration);
        var velocitySpan = TimeValue(last - first);
        _expressionEditor.Initialize(definitions, ExpressionKind.Pitch, noteDuration,
            velocitySpan > 0 ? velocitySpan : noteDuration,
            selection.Any(item => item.Note.Duration != selection[0].Note.Duration));
        _expressionEditor.OpenAt(GetViewport().GetMousePosition());
    }

    private void PreviewNoteExpression(NoteExpression draft, bool loop)
    {
        if (!ExpressionSelectionIsLive() || _expressionTrack is null)
        {
            _soundFontAudio.StopNoteExpressionPreview();
            _expressionEditor?.SetPreviewEnded();
            _expressionEditor?.SetPreviewStatus("The selected note changed; reopen the editor.");
            return;
        }
        try
        {
            var occurrence = _expressionSelection[0];
            var note = occurrence.Note;
            var pitch = _expressionTrack.Kind == TrackKind.Percussion && _expressionTrack.HasConfiguredPercussionKit
                && note.StringNumber >= 1 && note.StringNumber <= _expressionTrack.PercussionPieces.Count
                ? _expressionTrack.PercussionPieces[note.StringNumber - 1].MidiPitch : note.MidiPitch;
            if (draft.Kind == ExpressionKind.Velocity && !note.Expressions.Any(curve => curve.Id == draft.Id))
            {
                var first = _expressionSelection.Min(item => item.Start);
                var last = _expressionSelection.Max(item => item.Start);
                var span = TimeValue(last - first);
                draft.Position = span > 0 ? Math.Clamp(TimeValue(occurrence.Start - first) / span, 0, 1) : 0;
            }
            _soundFontAudio.PreviewNoteExpression(_expressionTrack, pitch, note.Velocity, note.Duration,
                _score.ResolveTempoAt(occurrence.Start), draft, note.Expressions, loop, note.SoundVariation);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _soundFontAudio.StopNoteExpressionPreview();
            _expressionEditor?.SetPreviewEnded();
            _expressionEditor?.SetPreviewStatus($"Preview unavailable: {error.Message}");
        }
    }

    private bool ExpressionSelectionIsLive()
    {
        if (!ReferenceEquals(_score, _expressionScore) || _expressionTrack is null || !_score.Tracks.Contains(_expressionTrack) || _expressionSelection.Length == 0) return false;
        var live = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).ToHashSet();
        return _expressionSelection.All(item => live.Contains(item.Source) && item.Source.TrackId == _expressionTrack.Id && item.Source.TabEvents.Contains(item.Note));
    }

    private void ApplyNoteExpression(NoteExpression curve)
    {
        if (!ExpressionSelectionIsLive()) { SetTabStatus("The selected notes changed; reopen the expression editor."); _expressionEditor?.Hide(); return; }
        try
        {
            curve.Validate();
            var selected = _expressionSelection.Select(item => item.Note).ToArray();
            bool editingShared = selected.All(note => note.Expressions.Any(item => item.Kind == curve.Kind && item.Id == curve.Id));
            // Updating an existing shared curve updates its definition everywhere; each
            // velocity assignment keeps its own position within the shared span.
            if (editingShared)
            {
                foreach (var note in AllScoreTabEvents())
                foreach (var existing in note.Expressions.Where(item => item.Id == curve.Id))
                {
                    existing.Points = curve.Points.ToList();
                    existing.MinSemitones = curve.MinSemitones;
                    existing.MaxSemitones = curve.MaxSemitones;
                }
            }
            else
            {
                var first = _expressionSelection.Min(item => item.Start);
                var last = _expressionSelection.Max(item => item.Start);
                double span = TimeValue(last - first);
                foreach (var occurrence in _expressionSelection)
                {
                    var assignment = curve.Clone();
                    assignment.Position = curve.Kind == ExpressionKind.Velocity && span > 0
                        ? Math.Clamp(TimeValue(occurrence.Start - first) / span, 0, 1) : 0;
                    occurrence.Note.Expressions.RemoveAll(item => item.Kind == curve.Kind);
                    occurrence.Note.Expressions.Add(assignment);
                }
            }
            NotifyScoreEdited();
            RenderSection();
            SetTabStatus("{0} curve applied to {1} note(s).", curve.Kind, selected.Length);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        { SetTabStatus(error.Message); }
    }

    private void RemoveNoteExpression(ExpressionKind kind)
    {
        if (!ExpressionSelectionIsLive()) { SetTabStatus("The selected notes changed; reopen the expression editor."); _expressionEditor?.Hide(); return; }
        var removed = 0;
        foreach (var item in _expressionSelection) removed += item.Note.Expressions.RemoveAll(curve => curve.Kind == kind);
        if (removed == 0) return;
        NotifyScoreEdited();
        RenderSection();
        SetTabStatus("{0} curve removed from {1} note(s).", kind, removed);
    }

    private IEnumerable<TabEvent> AllScoreTabEvents() => _score.Sections.SelectMany(section => section.Measures)
        .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).SelectMany(part => part.TabEvents);

    private static double TimeValue(ScoreTime time) => (double)time.Numerator / time.Denominator;
}

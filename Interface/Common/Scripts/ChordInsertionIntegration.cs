#nullable enable
using System;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private ScoreDocument? _chordScore;
    private ScoreTrack? _chordTrack;
    private int[] _chordTuning = Array.Empty<int>();
    private int _chordMaxFret = -1;

    private static bool IsChordTrack(ScoreTrack? track) =>
        track is not null && track.Kind is TrackKind.Guitar or TrackKind.Bass && track.TabTuningMidiPitches.Count > 0;

    private ScoreTrack? ActiveChordTrack() => _score?.Tracks.FirstOrDefault(track => track.Id == _activeFloatingBoardTrackId);

    // The windows are shared between tracks. A changed score, track, tuning or
    // fret limit invalidates the composition even if the popup remains visible.
    private void RefreshChordBoard()
    {
        var track = ActiveChordTrack();
        if (!IsChordTrack(track)) track = null;
        if (ReferenceEquals(_chordScore, _score) && ReferenceEquals(_chordTrack, track) &&
            (track is null || (_chordMaxFret == track.MaxFret && _chordTuning.SequenceEqual(track.TabTuningMidiPitches)))) return;
        _chordScore = _score;
        _chordTrack = track;
        _chordTuning = track?.TabTuningMidiPitches.ToArray() ?? Array.Empty<int>();
        _chordMaxFret = track?.MaxFret ?? -1;
        _chordBoard.Configure(track);
    }

    private bool HandleChordPlacementClick(MeasureTabView view, ScoreTrack track, Vector2 position)
    {
        if (!_floatingBoardsInitialized || !_chordBoard.IsArmed) return false;
        RefreshChordBoard();
        if (!_chordBoard.IsArmed || !ReferenceEquals(track, _chordTrack)) return true;
        if (view.HasUncommittedFretInput || (_activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView) && _activeEditorView.HasUncommittedFretInput))
        {
            SetTabStatus("Finish or cancel the current fret entry before inserting a chord.");
            return true;
        }
        if (view.ViewData is not { } data || !_tabClipboardViews.TryGetValue(view, out var target) ||
            !MeasureTabView.TryMapPointerToCell(position, view.Size, data.OpenStringMidiPitches.Count, view.RowHeight,
                view.SnapDenominator, data.MeasureDuration, out _, out var time))
        {
            SetTabStatus("Click a position inside this track's tablature to insert the chord.");
            return true;
        }

        try
        {
            var selected = _chordBoard.SelectedNotes.ToArray();
            var duration = new ScoreTime(1, _score.ResolveTimeSignature(target.Bar.Section, target.Bar.Measure).Denominator);
            var clipboard = TabClipboard.CreateChord(track, selected, duration);
            var plan = PlanTabPaste(clipboard, target, selected.Min(note => note.StringNumber),
                new ScoreTime(time.Numerator, time.Denominator), out var linked);
            if (plan.DroppedClipIndices.Count != 0) throw new InvalidOperationException("The entire chord must fit on the track's strings.");
            var score = _score;
            var tuning = track.TabTuningMidiPitches.ToArray();
            var maxFret = track.MaxFret;
            var bars = BuildTabBars();
            var barSnapshot = bars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
                ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure)).ToArray();
            var timeline = BuildTabOccurrences(track, bars).Select(o => (o.Source, o.Note, o.Start, o.End,
                o.Note.StringNumber, o.Note.Fret, o.Note.Duration)).ToArray();

            void Apply(bool confirmed)
            {
                try
                {
                    var currentBars = BuildTabBars();
                    if (!ReferenceEquals(score, _score) || !ReferenceEquals(_chordTrack, track) || !_chordBoard.IsArmed ||
                        !_chordBoard.SelectedNotes.SequenceEqual(selected) || !_score.Tracks.Contains(track) ||
                        maxFret != track.MaxFret || !tuning.SequenceEqual(track.TabTuningMidiPitches) ||
                        !_score.Sections.Contains(target.Bar.Section) || !target.Bar.Section.Measures.Contains(target.Bar.Measure) ||
                        !barSnapshot.SequenceEqual(currentBars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
                            ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure))) ||
                        !timeline.SequenceEqual(BuildTabOccurrences(track, currentBars).Select(o => (o.Source, o.Note, o.Start, o.End,
                            o.Note.StringNumber, o.Note.Fret, o.Note.Duration))))
                        throw new InvalidOperationException("The score or chord changed. Choose the destination again.");
                    plan.Apply(confirmed);
                    _chordBoard.ResetComposition();
                    NotifyScoreEdited();
                    RenderSection();
                    SetTabStatus("Inserted {0} chord notes.", selected.Length);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
                { SetTabStatus(error.Message); }
            }

            if (plan.RequiresConfirmation)
            {
                var dialog = new ConfirmationDialog
                {
                    Title = Tr("Confirm chord insertion"), OkButtonText = Tr("Insert"),
                    DialogText = string.Format(Tr("Insert {0} notes into {1}, measure {2}?\n\nExisting notes to replace: {3}{4}"),
                        selected.Length, track.Name, target.Bar.Index + 1, plan.ConflictingEventIds.Count,
                        linked ? Tr("\n\nLinked repeated occurrences will also change.") : "")
                };
                AddChild(dialog);
                dialog.Confirmed += () => { Apply(true); dialog.QueueFree(); };
                dialog.Canceled += dialog.QueueFree;
                dialog.CloseRequested += dialog.QueueFree;
                dialog.PopupCentered();
            }
            else Apply(false);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
        { SetTabStatus(error.Message); }
        return true;
    }
}

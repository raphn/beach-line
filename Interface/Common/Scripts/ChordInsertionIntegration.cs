#nullable enable
using System;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private ScoreDocument? _chordScore;
    private ScoreTrack? _chordTrack;
    private ScoreTrack? _keyboardChordTrack;
    private int[] _chordTuning = Array.Empty<int>();
    private int _chordMaxFret = -1;

    private static bool IsChordTrack(ScoreTrack? track) =>
        track is not null && track.Kind is TrackKind.Guitar or TrackKind.Bass && track.TabTuningMidiPitches.Count > 0;

    private ScoreTrack? ActiveChordTrack() => _score?.Tracks.FirstOrDefault(track => track.Id == _activeFloatingBoardTrackId);

    // The windows are shared between tracks. A changed score, track, tuning or
    // fret limit invalidates the composition even if the popup remains visible.
    private void RefreshChordBoards()
    {
        var activeTrack = ActiveChordTrack();
        var keyboardTrack = activeTrack?.UsesFullTab == true ? activeTrack : null;
        var scoreChanged = !ReferenceEquals(_chordScore, _score);
        if (scoreChanged || !ReferenceEquals(_keyboardChordTrack, keyboardTrack))
        {
            if (scoreChanged && ReferenceEquals(_keyboardChordTrack, keyboardTrack)) _keyboardPopup.ResetComposition();
            _keyboardChordTrack = keyboardTrack;
            _keyboardPopup.Configure(keyboardTrack);
            if (keyboardTrack is null) _keyboardPopup.Hide();
        }
        var track = IsChordTrack(activeTrack) ? activeTrack : null;
        if (!scoreChanged && ReferenceEquals(_chordTrack, track) &&
            (track is null || (_chordMaxFret == track.MaxFret && _chordTuning.SequenceEqual(track.TabTuningMidiPitches)))) return;
        _chordScore = _score;
        _chordTrack = track;
        _chordTuning = track?.TabTuningMidiPitches.ToArray() ?? Array.Empty<int>();
        _chordMaxFret = track?.MaxFret ?? -1;
        _chordBoard.Configure(track);
    }

    private bool ChordPlacementArmed => _floatingBoardsInitialized && (_chordBoard.IsArmed || _keyboardPopup.IsArmed);

    private void ShowChordPlacementHint()
    {
        if (ChordPlacementArmed && GodotObject.IsInstanceValid(_tabStatus))
            SetTabStatus("Click the desired time in this track's tablature to insert the chord. Escape cancels placement.");
    }

    private void CancelChordPlacement()
    {
        if (_chordBoard.IsArmed) _chordBoard.CancelPlacement();
        if (_keyboardPopup.IsArmed) _keyboardPopup.CancelPlacement();
    }

    private bool HandleChordPlacementClick(MeasureTabView view, ScoreTrack track, Vector2 position)
    {
        if (!ChordPlacementArmed) return false;
        RefreshChordBoards();
        bool keyboard = _keyboardPopup.IsArmed;
        if (keyboard ? !ReferenceEquals(track, _keyboardChordTrack) || view.ViewData?.IsFullTab != true
                     : !_chordBoard.IsArmed || !ReferenceEquals(track, _chordTrack)) return true;
        if (view.HasUncommittedFretInput || (_activeEditorView is not null && GodotObject.IsInstanceValid(_activeEditorView) && _activeEditorView.HasUncommittedFretInput))
        {
            SetTabStatus("Finish or cancel the current fret entry before inserting a chord.");
            return true;
        }
        if (view.ViewData is not { } data || !_tabClipboardViews.TryGetValue(view, out var target) ||
            !MeasureTabView.TryMapPointerToCell(position, view.Size, data.OpenStringMidiPitches.Count, view.RowHeight,
                view.SnapDenominator, data.MeasureDuration, out var row, out var time))
        {
            SetTabStatus(view.ViewData?.IsReadOnly == true
                ? "Enable note editing in this measure before inserting a chord."
                : "Click a position inside this track's tablature to insert the chord.");
            return true;
        }

        try
        {
            var selectedPitches = keyboard ? _keyboardPopup.SelectedPitches.ToArray() : Array.Empty<int>();
            var selectedFrets = keyboard ? Array.Empty<(int StringNumber, int Fret)>() : _chordBoard.SelectedNotes.ToArray();
            var noteCount = keyboard ? selectedPitches.Length : selectedFrets.Length;
            var duration = new ScoreTime(1, _score.ResolveTimeSignature(target.Bar.Section, target.Bar.Measure).Denominator);
            var clipboard = keyboard
                ? TabClipboard.CreateFullTabChord(track, selectedPitches, duration)
                : TabClipboard.CreateChord(track, selectedFrets, duration);
            int anchor = keyboard ? row + 1 : selectedFrets.Min(note => note.StringNumber);
            var plan = PlanTabPaste(clipboard, target, anchor,
                new ScoreTime(time.Numerator, time.Denominator), out var linked);
            if (plan.DroppedClipIndices.Count != 0) throw new InvalidOperationException("The entire chord must fit on the track's note lanes.");
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
                    bool sameComposition = keyboard
                        ? _keyboardPopup.IsArmed && ReferenceEquals(_keyboardChordTrack, track) && _keyboardPopup.SelectedPitches.SequenceEqual(selectedPitches)
                        : _chordBoard.IsArmed && ReferenceEquals(_chordTrack, track) && _chordBoard.SelectedNotes.SequenceEqual(selectedFrets);
                    var bar = target.Bar;
                    if (!ReferenceEquals(score, _score) || !sameComposition || !_score.Tracks.Contains(track) ||
                        maxFret != track.MaxFret || !tuning.SequenceEqual(track.TabTuningMidiPitches) ||
                        !_score.Sections.Contains(bar.Section) || !bar.Section.Measures.Contains(bar.Measure) ||
                        !ReferenceEquals(target.Source, GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, ResolveTrackOccurrence(bar.Section, track, bar))) ||
                        !barSnapshot.SequenceEqual(currentBars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
                            ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure))) ||
                        !timeline.SequenceEqual(BuildTabOccurrences(track, currentBars).Select(o => (o.Source, o.Note, o.Start, o.End,
                            o.Note.StringNumber, o.Note.Fret, o.Note.Duration))))
                        throw new InvalidOperationException("The score or chord changed. Choose the destination again.");
                    plan.Apply(confirmed);
                    if (keyboard) _keyboardPopup.CompletePlacement();
                    else _chordBoard.CompletePlacement();
                    NotifyScoreEdited();
                    RenderSection();
                    SetTabStatus("Inserted {0} chord notes.", noteCount);
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
                        noteCount, track.Name, target.Bar.Index + 1, plan.ConflictingEventIds.Count,
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
    private void WriteBoardNote(bool keyboard, int stringNumber, int fret)
    {
        var view = _activeEditorView;
        var track = ActiveChordTrack();
        if (track is null || (keyboard ? !_keyboardPopup.IsWriteEnabled || !ReferenceEquals(track, _keyboardChordTrack)
                                      : !_chordBoard.IsWriteEnabled || !ReferenceEquals(track, _chordTrack))) return;
        if (view is null || !GodotObject.IsInstanceValid(view) || !view.IsInsideTree() || view.IsQueuedForDeletion() ||
            !_tabClipboardViews.TryGetValue(view, out var target) || !ReferenceEquals(target.Track, track) ||
            view.ViewData?.IsReadOnly != false || !view.TryGetFocusedCell(out var focusedString, out var focusedTime))
        {
            SetTabStatus("Select a writable tablature cell in this track first.");
            return;
        }
        if (view.HasUncommittedFretInput)
        {
            SetTabStatus("Finish or cancel the current fret entry before writing a note.");
            return;
        }
        try
        {
            var onset = new ScoreTime(focusedTime.Numerator, focusedTime.Denominator);
            var duration = new ScoreTime(1, _score.ResolveTimeSignature(target.Bar.Section, target.Bar.Measure).Denominator);
            var clipboard = TabClipboard.CreateSingleNote(track, stringNumber, fret, duration);
            var plan = PlanTabPaste(clipboard, target, stringNumber, onset, out var linked);
            if (plan.DroppedClipIndices.Count != 0) throw new InvalidOperationException("The note does not fit on the track's note lanes.");
            var score = _score;
            var tuning = track.TabTuningMidiPitches.ToArray();
            var maxFret = track.MaxFret;
            var bars = BuildTabBars();
            var barSnapshot = bars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
                ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure)).ToArray();
            var timeline = BuildTabOccurrences(track, bars).Select(o => (o.Source, o.Note, o.Start, o.End,
                o.Note.StringNumber, o.Note.Fret, o.Note.Duration)).ToArray();
            bool autoMove = keyboard ? _keyboardPopup.AutoMoveEnabled : _chordBoard.AutoMoveEnabled;

            void Apply(bool confirmed)
            {
                try
                {
                    var currentBars = BuildTabBars();
                    var bar = target.Bar;
                    if (!ReferenceEquals(score, _score) || !ReferenceEquals(track, ActiveChordTrack()) ||
                        !ReferenceEquals(view, _activeEditorView) || !GodotObject.IsInstanceValid(view) || !view.IsInsideTree() || view.IsQueuedForDeletion() ||
                        !view.TryGetFocusedCell(out var currentString, out var currentTime) || currentString != focusedString ||
                        currentTime.Numerator != focusedTime.Numerator || currentTime.Denominator != focusedTime.Denominator ||
                        view.HasUncommittedFretInput || (keyboard ? !_keyboardPopup.IsWriteEnabled : !_chordBoard.IsWriteEnabled) ||
                        !_score.Tracks.Contains(track) || maxFret != track.MaxFret || !tuning.SequenceEqual(track.TabTuningMidiPitches) ||
                        !_score.Sections.Contains(bar.Section) || !bar.Section.Measures.Contains(bar.Measure) ||
                        !ReferenceEquals(target.Source, GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, ResolveTrackOccurrence(bar.Section, track, bar))) ||
                        !barSnapshot.SequenceEqual(currentBars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
                            ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure))) ||
                        !timeline.SequenceEqual(BuildTabOccurrences(track, currentBars).Select(o => (o.Source, o.Note, o.Start, o.End,
                            o.Note.StringNumber, o.Note.Fret, o.Note.Duration))))
                        throw new InvalidOperationException("The score or focused cell changed. Choose the destination again.");
                    plan.Apply(confirmed);
                    var absolute = bar.Start + onset + (autoMove ? duration : ScoreTime.Zero);
                    var destination = currentBars.Where(b => ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure is not null)
                        .FirstOrDefault(b => absolute >= b.Start && absolute < b.Start + b.Duration);
                    // At the end of the score, retain the current onset rather than leaving the valid grid.
                    var cursor = destination is null
                        ? new PendingTabCursor(track.Id, bar.Section, bar.Index, stringNumber, onset)
                        : new PendingTabCursor(track.Id, destination.Section, destination.Index, stringNumber, absolute - destination.Start);
                    _editingMeasureId = cursor.Section.Measures[cursor.MeasureIndex].Id;
                    _editorTabSnapshot = null;
                    _pendingTabCursor = cursor;
                    MarkEditorStateDirty();
                    EnsureSectionVisible(cursor.Section);
                    NotifyScoreEdited();
                    RenderSection();
                    Callable.From(() =>
                    {
                        if (_activeEditorView is { } active && GodotObject.IsInstanceValid(active) && active.IsInsideTree())
                            active.GetWindow()?.GrabFocus();
                    }).CallDeferred();
                    if (autoMove && destination is null && currentBars.Any(b => absolute >= b.Start && absolute < b.Start + b.Duration))
                        SetTabStatus("Inserted note. Add a source measure to continue writing in the next measure.");
                    else SetTabStatus("Inserted note.");
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
                { SetTabStatus(error.Message); }
            }

            if (plan.RequiresConfirmation)
            {
                var dialog = new ConfirmationDialog
                {
                    Title = Tr("Confirm note insertion"), OkButtonText = Tr("Insert"),
                    DialogText = string.Format(Tr("Insert a note into {0}, measure {1}?\n\nExisting notes to replace: {2}{3}"),
                        track.Name, target.Bar.Index + 1, plan.ConflictingEventIds.Count,
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
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
	private bool ApplyTabSelectionEdit(ScoreTrack track, TabBar bar,
		Dictionary<string, TabOccurrence> mappings, MeasureTabView.TabEditIntent intent)
	{
		var selected = intent.Notes?.ToArray();
		if (selected is null || selected.Length == 0)
		{
			if (!mappings.TryGetValue(intent.EventId, out var single)) return false;
			selected = new[] { new MeasureTabView.TabNoteViewData(intent.EventId, single.Note.StringNumber, single.Note.Fret,
				TabTime(single.Start - bar.Start), TabTime(single.Note.Duration)) };
		}
		var selection = selected.Select(n => mappings.TryGetValue(n.Id, out var occurrence) ? occurrence :
			throw new InvalidOperationException("The selection changed; select the notes again.")).ToArray();
		var notes = selection.Select(o => o.Note).ToHashSet();
		var targets = new List<TabSelectionMove>();
		var bars = BuildTabBars();
		var timeline = BuildTabOccurrences(track, bars);
		if (intent.Action == "move")
		{
			var anchor = selected.FirstOrDefault(n => n.Id == intent.EventId) ??
				throw new InvalidOperationException("The drag anchor is no longer selected.");
			var destination = new ScoreTime(intent.Onset.Numerator, intent.Onset.Denominator);
			if (destination < ScoreTime.Zero || destination >= bar.Duration)
				throw new InvalidOperationException("The move anchor must start inside the measure.");
			var delta = destination - new ScoreTime(anchor.Onset.Numerator, anchor.Onset.Denominator);
			if (delta != ScoreTime.Zero && selection.Any(occurrence => occurrence.Start < bar.Start))
				throw new InvalidOperationException("Move this selection from its starting measure.");
			var stringDelta = intent.StringNumber - anchor.StringNumber;
			foreach (var occurrence in selection)
			{
				var note = occurrence.Note;
				var onset = note.Onset + delta;
				var row = note.StringNumber + stringDelta;
				TabEventEditing.ValidateAssignment(track, row, note.Fret);
				if (onset < ScoreTime.Zero)
					throw new InvalidOperationException("The entire selection must fit; a note would start before its owning measure.");
				if (!note.AllowsBarOverflow && bars.Where(b => ReferenceEquals(ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure, occurrence.Source))
					.Any(b => onset >= b.Duration))
					throw new InvalidOperationException("The entire selection must fit inside its owning measures.");
				if (!targets.Any(t => ReferenceEquals(t.Note, note))) targets.Add(new(occurrence.Source, note, row, onset));
			}
			if (targets.All(t => t.Note.StringNumber == t.StringNumber && t.Note.Onset == t.Onset)) return true;
		}
		else if (intent.Action == "shift_fret")
		{
			var anchor = selected.FirstOrDefault(n => n.Id == intent.EventId) ??
				throw new InvalidOperationException("The selection changed; select the notes again.");
			var delta = intent.Fret - anchor.Fret;
			foreach (var occurrence in selection.DistinctBy(o => o.Note))
			{
				var fret = occurrence.Note.Fret + delta;
				if (fret < 0 || fret > 24 || fret > track.MaxFret)
					throw new InvalidOperationException("The entire selection must stay within the track's fret range, up to fret 24.");
				targets.Add(new TabSelectionMove(occurrence.Source, occurrence.Note, occurrence.Note.StringNumber, occurrence.Note.Onset, fret));
			}
		}
		var projected = timeline.Where(o => notes.Contains(o.Note)).Select(o =>
		{
			var target = targets.FirstOrDefault(t => ReferenceEquals(t.Note, o.Note));
			return (Occurrence: o, Row: target?.StringNumber ?? o.Note.StringNumber,
				Start: target is null ? o.Start : o.Start + target.Onset - o.Note.Onset);
		}).ToArray();
		var externalIntervals = timeline.Where(o => !notes.Contains(o.Note)).Select(candidate =>
		{
			var next = timeline.Where(o => !notes.Contains(o.Note) && o.Note.StringNumber == candidate.Note.StringNumber && o.Start > candidate.Start)
				.Select(o => o.Start).DefaultIfEmpty(candidate.End).Min();
			return new TabSelectionInterval(candidate.Note, candidate.Note.StringNumber, candidate.Start, next < candidate.End ? next : candidate.End);
		}).ToArray();
		var conflicts = intent.Action == "move" ? TabSelectionEditPlan.FindConflicts(projected.Select(p =>
			new TabSelectionInterval(p.Occurrence.Note, p.Row, p.Start, p.Start + p.Occurrence.Note.Duration)).ToArray(), externalIntervals).ToHashSet() : new HashSet<TabEvent>();
		var removals = timeline.Where(o => conflicts.Contains(o.Note) || intent.Action == "delete" && notes.Contains(o.Note))
			.Select(o => (o.Source, o.Note)).Distinct().ToArray();
		var plan = new TabSelectionEditPlan(track, targets, removals, conflicts.Count != 0);
		var snapshot = timeline.Select(o => (o.Source, o.Note, o.Start, o.End, o.Note.StringNumber, o.Note.Fret, o.Note.Velocity, o.Note.AllowsBarOverflow, o.Note.TupletActual)).ToArray();
		var tuning = track.TabTuningMidiPitches.ToArray();
		var barSnapshot = bars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
			ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure)).ToArray();
		void Apply()
		{
			try
			{
				var currentBars = BuildTabBars();
				var current = BuildTabOccurrences(track, currentBars).Select(o => (o.Source, o.Note, o.Start, o.End, o.Note.StringNumber, o.Note.Fret, o.Note.Velocity, o.Note.AllowsBarOverflow, o.Note.TupletActual));
				if (!_score.Tracks.Contains(track) || !snapshot.SequenceEqual(current) || !tuning.SequenceEqual(track.TabTuningMidiPitches) ||
					!barSnapshot.SequenceEqual(currentBars.Select(b => (b.Section, b.Measure, b.Start, b.Duration,
						ResolveTrackOccurrence(b.Section, track, b).SourceTrackMeasure))))
					throw new InvalidOperationException("The score changed; select the notes and try again.");
				plan.Apply(true);
				NotifyScoreEdited(); RenderSection();
			}
			catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException) { SetTabStatus(error.Message); }
		}
		if (plan.RequiresConfirmation)
		{
			var dialog = new ConfirmationDialog { Title = Tr(intent.Action == "move" ? "Confirm move" : "Confirm deletion"), OkButtonText = Tr(intent.Action == "move" ? "Move" : "Delete"),
				DialogText = string.Format(Tr(intent.Action == "move" ? "Move {0} selected note(s)?\nExisting notes to replace: {1}" : "Delete {0} selected note(s)?\nExisting notes to replace: {1}"), notes.Count, conflicts.Count) };
			AddChild(dialog);
			dialog.Confirmed += () => { Apply(); dialog.QueueFree(); };
			dialog.Canceled += dialog.QueueFree;
			dialog.CloseRequested += dialog.QueueFree;
			dialog.PopupCentered();
		}
		else Apply();
		return true;
	}
}

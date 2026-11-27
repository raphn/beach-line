#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
	private sealed record TabClipboardViewContext(ScoreTrack Track, TabBar Bar, TrackMeasure Source,
		Dictionary<string, TabOccurrence> Occurrences);
	private readonly Dictionary<MeasureTabView, TabClipboardViewContext> _tabClipboardViews = new();
	private TabClipboard? _tabClipboard;

	private void RegisterTabClipboardView(MeasureTabView view, ScoreTrack track, TabBar bar,
		TrackMeasure source, Dictionary<string, TabOccurrence> occurrences)
	{
		_tabClipboardViews[view] = new TabClipboardViewContext(track, bar, source, occurrences);
	}

	private bool HandleTabClipboardShortcut(Key key, Control? focusOwner)
	{
		if (key == Key.C)
		{
			if (focusOwner is not MeasureTabView sourceView || !_tabClipboardViews.TryGetValue(sourceView, out var sourceContext))
				return false;
			if (sourceView.HasUncommittedFretInput)
			{
				SetTabStatus("Finish or cancel the current fret entry before copying.");
				return true;
			}
			try
			{
				var anchor = sourceView.SelectionAnchorNote;
				if (anchor is null || !sourceContext.Occurrences.TryGetValue(anchor.Id, out var anchorOccurrence))
					throw new InvalidOperationException("Select one or more notes before copying.");
				var selected = new List<TabCopySource>();
				foreach (var note in sourceView.SelectedNotes)
				{
					if (!sourceContext.Occurrences.TryGetValue(note.Id, out var occurrence))
						throw new InvalidOperationException("The selection changed; select the notes again.");
					selected.Add(new TabCopySource(occurrence.Source, occurrence.Note, occurrence.Start));
				}
				_tabClipboard = TabClipboard.Copy(sourceContext.Track, selected, anchorOccurrence.Note.StringNumber, anchorOccurrence.Start);
				SetTabStatus("Copied {0} note(s). Point at a destination and use the paste shortcut.", selected.Count);
			}
			catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
			{
				SetTabStatus(error.Message);
			}
			return true;
		}

		if (key != Key.V) return false;
		if (GetViewport().GuiGetHoveredControl() is not MeasureTabView targetView ||
			!_tabClipboardViews.TryGetValue(targetView, out var target))
		{
			if (focusOwner is not MeasureTabView) return false;
			SetTabStatus("Point at a tablature measure before pasting.");
			return true;
		}
		if (focusOwner is MeasureTabView { HasUncommittedFretInput: true })
		{
			SetTabStatus("Finish or cancel the current fret entry before pasting.");
			return true;
		}
		if (_tabClipboard is null)
		{
			SetTabStatus("Copy one or more tablature notes first.");
			return true;
		}
		if (!targetView.TryGetHoverCellContext(out var targetString, out var targetOnset))
		{
			SetTabStatus("Point at a string and subdivision inside a measure to paste.");
			return true;
		}

		try
		{
			var onset = new ScoreTime(targetOnset.Numerator, targetOnset.Denominator);
			var plan = PlanTabPaste(_tabClipboard, target, targetString, onset, out var changesLinkedNotes);
			if (plan.RequiresConfirmation)
			{
				ConfirmTabPaste(plan, target, changesLinkedNotes);
				return true;
			}
			ApplyTabPaste(plan, target, false);
		}
		catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
		{
			SetTabStatus(error.Message);
		}
		return true;
	}

	private TabClipboard.PastePlan PlanTabPaste(TabClipboard clipboard, TabClipboardViewContext target,
		int targetString, ScoreTime onset, out bool changesLinkedNotes)
	{
			var bars = BuildTabBars();
			var targetBars = bars.Where(bar => ReferenceEquals(target.Source,
				GetEffectiveTrackMeasure(bar.Section, target.Track, bar.Measure, ResolveTrackOccurrence(bar.Section, target.Track, bar)))).ToArray();
			var occurrences = BuildTabOccurrences(target.Track, bars);
			var occupied = new List<TabPasteOccupied>();
			foreach (var occurrence in occurrences)
			{
				var next = occurrences.Where(other => other.Note.StringNumber == occurrence.Note.StringNumber && other.Start > occurrence.Start)
					.Select(other => other.Start).DefaultIfEmpty(occurrence.End).Min();
				var end = next < occurrence.End ? next : occurrence.End;
				if (end <= occurrence.Start) continue;
				foreach (var targetBar in targetBars)
				{
					var relativeStart = occurrence.Start - targetBar.Start;
					if (end <= targetBar.Start) continue;
					occupied.Add(new TabPasteOccupied(occurrence.Source, occurrence.Note, relativeStart, end - occurrence.Start));
				}
			}
			var plan = clipboard.PlanPaste(target.Track, target.Source, targetString, onset,
				new ScoreTime(1, GetMeasureSnapDenominator(target.Track.Id, target.Source)), target.Bar.Duration, occupied);
			// The same source can play more than once. Two new notes must not create
			// simultaneous attacks on a string in any of those linked occurrences.
			var attacks = new HashSet<(int, ScoreTime)>();
			foreach (var entry in plan.Entries)
			foreach (var targetBar in targetBars)
			{
				if (!attacks.Add((entry.StringNumber, targetBar.Start + entry.Onset)))
				throw new InvalidOperationException(TranslationServer.Translate("The pasted notes would overlap an attack from a linked repeat. Change the repeat count or paste position first."));
			}
			changesLinkedNotes = targetBars.Length > 1 || occurrences
					.Where(occurrence => plan.ConflictingEventIds.Contains(occurrence.Note.Id))
					.GroupBy(occurrence => occurrence.Note.Id).Any(group => group.Count() > 1);
			return plan;
	}

	private void ConfirmTabPaste(TabClipboard.PastePlan plan, TabClipboardViewContext target, bool linked)
	{
		var dialog = new ConfirmationDialog
		{
			Title = TranslationServer.Translate("Confirm paste"),
			OkButtonText = TranslationServer.Translate("Paste"),
			DialogText = string.Format(TranslationServer.Translate("Paste {0} note(s) into {1}, measure {2}?\n\nExisting notes to replace: {3}\nCopied notes that cannot fit: {4}{5}"),
				plan.PasteCount, target.Track.Name, target.Bar.Index + 1, plan.ConflictingEventIds.Count,
				plan.DroppedClipIndices.Count, linked ? TranslationServer.Translate("\n\nSome affected notes belong to linked measures: changes also appear in their repeated occurrences.") : "")
		};
		AddChild(dialog);
		dialog.Confirmed += () => { ApplyTabPaste(plan, target, true); dialog.QueueFree(); };
		dialog.Canceled += dialog.QueueFree;
		dialog.PopupCentered();
	}

	private void ApplyTabPaste(TabClipboard.PastePlan plan, TabClipboardViewContext target, bool confirmed)
	{
		try
		{
			if (!_score.Tracks.Contains(target.Track) || !_score.Sections.Contains(target.Bar.Section) ||
				!target.Bar.Section.Measures.Contains(target.Bar.Measure) ||
				!target.Bar.Section.Measures.Any(measure => ReferenceEquals(measure.GetTrackMeasure(target.Track.Id), target.Source) ||
					measure.TrackMeasureVersions.Any(version => version.TrackId == target.Track.Id && ReferenceEquals(version.Content, target.Source))))
				throw new InvalidOperationException(TranslationServer.Translate("The destination changed. Select it and paste again."));
			var inserted = plan.Apply(confirmed);
			if (inserted.Count == 0) { SetTabStatus("No copied notes fit at this position."); return; }
			// Clipboard contents can outlive a project switch. If another project has
			// since edited a curve with the same identity, keep the pasted group linked
			// to itself instead of creating conflicting definitions in the destination.
			var pasted = inserted.ToHashSet();
			var existing = AllScoreTabEvents().Where(note => !pasted.Contains(note)).SelectMany(note => note.Expressions).ToArray();
			foreach (var group in inserted.SelectMany(note => note.Expressions).GroupBy(curve => curve.Id))
			{
				var first = group.First();
				if (existing.Any(curve => curve.Id == group.Key && (curve.Kind != first.Kind || !curve.Points.SequenceEqual(first.Points) ||
					curve.MinSemitones != first.MinSemitones || curve.MaxSemitones != first.MaxSemitones ||
					(first.Kind != ExpressionKind.Velocity && curve.Position != first.Position))))
				{
					var replacementId = Guid.NewGuid();
					foreach (var curve in group) curve.Id = replacementId;
				}
			}
			NotifyScoreEdited();
			RenderSection();
			SetTabStatus("Pasted {0} note(s).", inserted.Count);
		}
		catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException or OverflowException)
		{
			SetTabStatus(error.Message);
		}
	}
}


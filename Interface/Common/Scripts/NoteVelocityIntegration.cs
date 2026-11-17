#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;

public partial class DrumScorePresenter
{
    private NoteVelocityPopup? _noteVelocityPopup;
    private ScoreTrack? _velocityTrack;
    private (TrackMeasure Source, TabEvent Note)[] _velocityTargets = Array.Empty<(TrackMeasure, TabEvent)>();
    private int? _velocityTupletActual;
    private TabOccurrence[] _expressionPendingOccurrences = Array.Empty<TabOccurrence>();

    private void ShowNoteVelocitySettings(ScoreTrack track, IReadOnlyList<TabOccurrence> occurrences, int selectionCount, Vector2 position)
    {
        if (!_score.Tracks.Contains(track) || occurrences.Count == 0) return;
        var targets = occurrences.Select(occurrence => (occurrence.Source, occurrence.Note)).Distinct().ToArray();
        int initialLevel;
        try { initialLevel = TabVelocityEditing.InitialLevel(targets); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        var tuplets = targets.Select(target => target.Note.TupletActual).Distinct().ToArray();
        if (_noteVelocityPopup is null || !GodotObject.IsInstanceValid(_noteVelocityPopup))
        {
            _noteVelocityPopup = GD.Load<PackedScene>("res://Interface/Tab/Scenes/NoteVelocityPopup.tscn").Instantiate<NoteVelocityPopup>();
            AddChild(_noteVelocityPopup);
            _noteVelocityPopup.LevelChanged += SetNoteVelocityLevel;
            _noteVelocityPopup.TupletChanged += SetNoteTuplet;
            _noteVelocityPopup.EffectsRequested += OpenNoteExpressionEditor;
            _noteVelocityPopup.VariationChanged += SetNoteSoundVariation;
            _noteVelocityPopup.ArticulationChanged += SetIncomingArticulation;
            _noteVelocityPopup.ArticulationPreviewRequested += PreviewIncomingArticulation;
            _noteVelocityPopup.Closed += () => { _soundFontAudio?.StopNoteExpressionPreview(); _velocityTrack = null; _velocityTargets = Array.Empty<(TrackMeasure, TabEvent)>(); _velocityTupletActual = null; };
        }
        _velocityTrack = track;
        _velocityTargets = targets;
        _expressionPendingOccurrences = occurrences.ToArray();
        _velocityTupletActual = tuplets.Length == 1 ? tuplets[0] : null;
        _noteVelocityPopup.Initialize(initialLevel, selectionCount, _velocityTupletActual);
        _noteVelocityPopup.SetArticulationAvailable(track.Kind != TrackKind.Percussion && track.TabTuningMidiPitches.Count > 0);
        _noteVelocityPopup.SetArticulationPreviewAvailable(CanPreviewIncomingArticulation(track, targets));
        var choices = targets.Select(target => VariationOptions(track, target.Note)).ToArray();
        var common = choices[0].Where(option => choices.Skip(1).All(list =>
            list.Any(other => other.Id == option.Id && other.DisplayName == option.DisplayName))).ToArray();
        var variations = targets.Select(target => target.Note.SoundVariation).Distinct().ToArray();
        _noteVelocityPopup.SetVariations(common, variations.Length == 1 ? variations[0] : null);
        _noteVelocityPopup.OpenAt(position);
    }

    private static IReadOnlyList<SoundVariationOption> VariationOptions(ScoreTrack track, TabEvent note)
    {
        if (track.Kind != TrackKind.Percussion)
            return SoundVariations.GetOptions(track.DefaultMidiProgram);
        if (!track.HasConfiguredPercussionKit || note.StringNumber < 1 || note.StringNumber > track.PercussionPieces.Count)
            return Array.Empty<SoundVariationOption>();
        return SoundVariations.GetOptions(track.DefaultMidiProgram, track.PercussionPieces[note.StringNumber - 1].MidiPitch);
    }

    private void SetNoteSoundVariation(string variation)
    {
        var track = _velocityTrack;
        var targets = _velocityTargets;
        var liveParts = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).ToHashSet();
        if (track is null || !_score.Tracks.Contains(track) || targets.Length == 0 ||
            targets.Any(target => target.Source.TrackId != track.Id || !liveParts.Contains(target.Source) ||
                !target.Source.TabEvents.Contains(target.Note) ||
                (variation.Length != 0 && !VariationOptions(track, target.Note).Any(option => option.Id == variation))))
        {
            _noteVelocityPopup?.Hide();
            return;
        }
        if (targets.All(target => target.Note.SoundVariation == variation)) return;
        foreach (var target in targets) target.Note.SoundVariation = variation;
        NotifyScoreEdited();
    }

    private void SetIncomingArticulation(StringArticulation articulation)
    {
        var track = _velocityTrack;
        var targets = _velocityTargets;
        if (track is null || !_score.Tracks.Contains(track) || track.Kind == TrackKind.Percussion ||
            track.TabTuningMidiPitches.Count == 0 || targets.Length == 0)
        {
            _noteVelocityPopup?.ShowTupletError("Incoming articulations require selected notes on a configured string track.");
            return;
        }
		if (articulation != StringArticulation.None && targets.Length == 2 && targets[0].Note.StringNumber == targets[1].Note.StringNumber)
		{
			try
			{
				var timeline = BuildTabOccurrences(track, BuildTabBars())
					.Select(item => new StringArticulationPreview.NoteOccurrence(item.Note, item.Start, item.End)).ToArray();
				if (!StringArticulationPreview.TryGetSelectedPairDestination(timeline, targets[0].Note, targets[1].Note, articulation, out var destination))
				{
					_noteVelocityPopup?.ShowTupletError("Select two consecutive sustained notes on the same string with the correct pitch direction.");
					return;
				}
				targets = targets.Where(target => ReferenceEquals(target.Note, destination)).ToArray();
			}
			catch (ArgumentException error) { _noteVelocityPopup?.ShowTupletError(error.Message); return; }
			catch (InvalidOperationException error) { _noteVelocityPopup?.ShowTupletError(error.Message); return; }
			catch (OverflowException error) { _noteVelocityPopup?.ShowTupletError(error.Message); return; }
		}
        var liveParts = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).ToHashSet();
        if (targets.Any(target => target.Source.TrackId != track.Id || !liveParts.Contains(target.Source) ||
                !target.Source.TabEvents.Contains(target.Note)))
        {
            _noteVelocityPopup?.ShowTupletError("The selected notes changed before the articulation could be applied.");
            return;
        }
        if (articulation != StringArticulation.None)
        {
            try
            {
                var occurrences = BuildTabOccurrences(track, BuildTabBars());
                foreach (var target in targets)
                {
                    var destinationOccurrences = occurrences.Where(item => ReferenceEquals(item.Note, target.Note) && ReferenceEquals(item.Source, target.Source)).ToArray();
                    if (destinationOccurrences.Length == 0)
                        throw new InvalidOperationException("A selected note has no visible destination occurrence.");
                    TabEvent? commonPredecessor = null;
                    foreach (var destination in destinationOccurrences)
                    {
                        var predecessor = occurrences.Where(item => item.Note.StringNumber == destination.Note.StringNumber && item.Start < destination.Start)
                            .OrderByDescending(item => item.Start).FirstOrDefault();
                        if (predecessor is null || predecessor.End < destination.Start)
                            throw new InvalidOperationException("Each selected note needs a sustained immediately preceding note on the same string.");
                        if (commonPredecessor is not null && !ReferenceEquals(commonPredecessor, predecessor.Note))
                            throw new InvalidOperationException("This selection has different preceding notes across repeats or sections; apply the articulation to a smaller selection.");
                        commonPredecessor = predecessor.Note;
                        bool directionMatches = articulation == StringArticulation.HammerOn
                            ? destination.Note.MidiPitch > predecessor.Note.MidiPitch
                            : destination.Note.MidiPitch < predecessor.Note.MidiPitch;
                        if (!directionMatches)
                            throw new InvalidOperationException(articulation == StringArticulation.HammerOn
                                ? "A hammer-on must move to a higher pitch on the same string."
                                : "A pull-off must move to a lower pitch on the same string.");
                    }
                }
            }
            catch (ArgumentException error) { _noteVelocityPopup?.ShowTupletError(error.Message); return; }
            catch (InvalidOperationException error) { _noteVelocityPopup?.ShowTupletError(error.Message); return; }
            catch (OverflowException error) { _noteVelocityPopup?.ShowTupletError(error.Message); return; }
        }
        if (targets.All(target => target.Note.IncomingArticulation == articulation)) return;
        foreach (var target in targets) target.Note.IncomingArticulation = articulation;
        _soundFontAudio?.StopNoteExpressionPreview();
        _noteVelocityPopup?.SetArticulationPreviewAvailable(CanPreviewIncomingArticulation(track, _velocityTargets));
        NotifyScoreEdited();
        RenderSection();
    }

    private bool CanPreviewIncomingArticulation(ScoreTrack track, (TrackMeasure Source, TabEvent Note)[] targets)
    {
        if (track.Kind == TrackKind.Percussion || track.TabTuningMidiPitches.Count == 0 || targets.Length != 1 ||
            targets[0].Note.IncomingArticulation == StringArticulation.None) return false;
        try { ResolveIncomingArticulationPair(track, targets[0], out _, out _, out _); return true; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (OverflowException) { return false; }
    }

    private void PreviewIncomingArticulation()
    {
        var track = _velocityTrack;
        var targets = _velocityTargets;
        if (track is null || !_score.Tracks.Contains(track) || targets.Length != 1 || _soundFontAudio is null)
        {
            _noteVelocityPopup?.ShowTupletError("Select one authored hammer-on or pull-off note to preview its connected pair.");
            return;
        }
        try
        {
            ResolveIncomingArticulationPair(track, targets[0], out var predecessor, out var destination, out var onsetGap);
            _soundFontAudio.PreviewStringArticulationPair(track, predecessor.Note, destination.Note, onsetGap,
                _score.ResolveTempoAt(destination.Start));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _noteVelocityPopup?.ShowTupletError($"Preview unavailable: {error.Message}");
        }
    }

    private void ResolveIncomingArticulationPair(ScoreTrack track, (TrackMeasure Source, TabEvent Note) target,
        out TabOccurrence predecessor, out TabOccurrence destination, out ScoreTime onsetGap)
    {
        if (!_score.Tracks.Contains(track) || track.Kind == TrackKind.Percussion || track.TabTuningMidiPitches.Count == 0 ||
            target.Note.IncomingArticulation == StringArticulation.None)
            throw new InvalidOperationException("The selected note has no previewable incoming articulation.");
        var liveParts = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).ToHashSet();
        if (target.Source.TrackId != track.Id || !liveParts.Contains(target.Source) || !target.Source.TabEvents.Contains(target.Note))
            throw new InvalidOperationException("The selected note changed before the pair could be previewed.");
        var occurrences = BuildTabOccurrences(track, BuildTabBars());
        var destinations = occurrences.Where(item => ReferenceEquals(item.Note, target.Note) && ReferenceEquals(item.Source, target.Source)).ToArray();
        if (destinations.Length == 0) throw new InvalidOperationException("The selected note has no destination occurrence.");
        TabOccurrence? firstPredecessor = null;
        ScoreTime? commonGap = null;
        var initialDestination = destinations[0];
        foreach (var current in destinations)
        {
            var currentPredecessor = occurrences.Where(item => item.Note.StringNumber == current.Note.StringNumber && item.Start < current.Start)
                .OrderByDescending(item => item.Start).FirstOrDefault();
            if (currentPredecessor is null || currentPredecessor.End < current.Start)
                throw new InvalidOperationException("The immediately preceding note is not sustained through the connected note.");
            var currentGap = current.Start - currentPredecessor.Start;
            if (firstPredecessor is { } first && !ReferenceEquals(first.Note, currentPredecessor.Note))
                throw new InvalidOperationException("The selected note has different predecessors across repeats or sections.");
            if (commonGap is { } gap && gap != currentGap)
                throw new InvalidOperationException("The connected note has different timing across repeats or sections.");
            firstPredecessor ??= currentPredecessor;
            commonGap ??= currentGap;
            var directionMatches = target.Note.IncomingArticulation == StringArticulation.HammerOn
                ? current.Note.MidiPitch > currentPredecessor.Note.MidiPitch
                : current.Note.MidiPitch < currentPredecessor.Note.MidiPitch;
            if (!directionMatches) throw new InvalidOperationException("The pitch direction does not match the authored articulation.");
        }
        predecessor = firstPredecessor ?? throw new InvalidOperationException("The selected note has no predecessor.");
        destination = initialDestination;
        onsetGap = commonGap ?? throw new InvalidOperationException("The selected note has no connected timing.");
    }

    private void SetNoteVelocityLevel(int level)
    {
        var track = _velocityTrack;
        var targets = _velocityTargets;
        var liveParts = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).ToHashSet();
        if (track is null || !_score.Tracks.Contains(track) || targets.Length == 0 ||
            targets.Any(target => target.Source.TrackId != track.Id || !liveParts.Contains(target.Source) ||
                !target.Source.TabEvents.Contains(target.Note)))
        {
            _noteVelocityPopup?.SetTupletSelection(_velocityTupletActual);
            _noteVelocityPopup?.ShowTupletError("The selected notes changed before the tuplet could be applied.");
            _noteVelocityPopup?.Hide();
            return;
        }
        try { TabVelocityEditing.Apply(targets, level); }
        catch (ArgumentException) { _noteVelocityPopup?.Hide(); return; }
        catch (InvalidOperationException) { _noteVelocityPopup?.Hide(); return; }
        NotifyScoreEdited();
        // Linked repeats and continuations refer to the same source event.
        var edited = targets.Select(target => target.Note).ToHashSet();
        foreach (var (view, context) in _tabClipboardViews)
        {
            if (!GodotObject.IsInstanceValid(view) || view.ViewData is not { } data) continue;
            var ids = context.Occurrences.Where(pair => edited.Contains(pair.Value.Note))
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            if (ids.Count == 0) continue;
            view.Bind(data with { Notes = data.Notes.Select(visible => ids.Contains(visible.Id)
                ? visible with { VelocityLevel = level } : visible).ToArray() });
        }
    }

    private void SetNoteTuplet(int actual)
    {
        var track = _velocityTrack;
        var targets = _velocityTargets;
        var liveParts = _score.Sections.SelectMany(section => section.Measures)
            .SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).ToHashSet();
        if (track is null || !_score.Tracks.Contains(track) || targets.Length == 0 ||
            targets.Any(target => target.Source.TrackId != track.Id || !liveParts.Contains(target.Source) ||
                !target.Source.TabEvents.Contains(target.Note)))
        {
            _noteVelocityPopup?.SetTupletSelection(_velocityTupletActual);
            _noteVelocityPopup?.ShowTupletError("The selected notes changed before the tuplet could be applied.");
            return;
        }

        try
        {
            var bars = BuildTabBars();
            var sourceBarLimits = new Dictionary<TrackMeasure, ScoreTime>();
            foreach (var source in targets.Select(target => target.Source).Distinct())
            {
                var durations = bars
                    .Where(bar => ReferenceEquals(GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, ResolveTrackOccurrence(bar.Section, track, bar)), source))
                    .Select(bar => bar.Duration)
                    .ToArray();
                if (durations.Length == 0)
                    throw new InvalidOperationException("The selected source measure has no visible destination bar.");
                sourceBarLimits[source] = durations.Aggregate((shortest, duration) => duration < shortest ? duration : shortest);
            }
            var resolvedOccurrences = BuildTabOccurrences(track, bars)
                .Select(occurrence => new TabTupletOccurrence(occurrence.Source, occurrence.Note, occurrence.Start, occurrence.End))
                .ToArray();
            var sourceSnaps = targets.Select(target => target.Source).Distinct()
                .ToDictionary(source => source, source => GetMeasureSnapDenominator(track.Id, source));
            TabTupletEditing.ApplyTuplet(targets, actual, GetTabSnapDenominator(track.Id), sourceBarLimits, resolvedOccurrences, sourceSnaps);
        }
        catch (ArgumentException error) { RestoreTupletSelection(error.Message); return; }
        catch (InvalidOperationException error) { RestoreTupletSelection(error.Message); return; }
        catch (OverflowException error) { RestoreTupletSelection(error.Message); return; }

        _velocityTupletActual = actual;
        NotifyScoreEdited();
        RenderSection();
    }

    private void RestoreTupletSelection(string message)
    {
        _noteVelocityPopup?.SetTupletSelection(_velocityTupletActual);
        _noteVelocityPopup?.ShowTupletError(message);
    }
}

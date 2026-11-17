#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

public readonly record struct TimeSignature
{
    public TimeSignature(int numerator, int denominator)
    {
        if (numerator is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), "The numerator must be between 1 and 32.");
        }

        if (denominator is not (1 or 2 or 4 or 8 or 16 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), "The denominator must be 1, 2, 4, 8, 16, or 32.");
        }

        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }
    public int Denominator { get; }
    public ScoreTime BarDuration => new(Numerator, Denominator);

    public override string ToString() => $"{Numerator}/{Denominator}";
}

public sealed record TempoMark
{
    public TempoMark(decimal beatsPerMinute, RhythmicValue beatUnit)
    {
        if (beatsPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(beatsPerMinute), "Tempo must be greater than zero BPM.");
        }

        BeatsPerMinute = beatsPerMinute;
        BeatUnit = beatUnit ?? throw new ArgumentNullException(nameof(beatUnit));
    }

    public decimal BeatsPerMinute { get; }
    public RhythmicValue BeatUnit { get; }

    /// <summary>Tempo in quarter-note beats per minute, regardless of this mark's reference note.</summary>
    public decimal QuarterNotesPerMinute
    {
        get
        {
            var duration = BeatUnit.Duration;
            return ScaleTempo(BeatsPerMinute, checked(duration.Numerator * 4), duration.Denominator, nameof(BeatsPerMinute));
        }
    }

    /// <summary>Creates a tempo mark with the requested quarter-note rate and the supplied written beat unit.</summary>
    public static TempoMark FromQuarterNotesPerMinute(decimal quarterNotesPerMinute, RhythmicValue beatUnit)
    {
        ArgumentNullException.ThrowIfNull(beatUnit);
        if (quarterNotesPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quarterNotesPerMinute), "Tempo must be greater than zero BPM.");
        }

        var duration = beatUnit.Duration;
        var beatsPerMinute = ScaleTempo(quarterNotesPerMinute, duration.Denominator, checked(duration.Numerator * 4), nameof(quarterNotesPerMinute));
        return new TempoMark(beatsPerMinute, beatUnit);
    }

    private static decimal ScaleTempo(decimal value, long numerator, long denominator, string parameterName)
    {
        var divisor = GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        try
        {
            return numerator <= denominator
                ? value / denominator * numerator
                : value * numerator / denominator;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The converted tempo is outside the supported decimal range.");
        }
    }

    private static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0) (left, right) = (right, left % right);
        return left;
    }
}

public enum TrackKind
{
    Guitar,
    Bass,
    Keyboard,
    Percussion,
    Other,
    Audio,
    /// <summary>Silent timeline ruler used to define section length in audio-only scores.</summary>
    Ruler
}

/// <summary>Track-aware defaults for newly authored note intensity and MIDI velocity.</summary>
public static class TrackDefaults
{
    public static int IntensityFor(TrackKind kind) => kind == TrackKind.Guitar ? 3 : 4;
    public static int VelocityFor(TrackKind kind) => Beat.MidiVelocityForIntensity(IntensityFor(kind));
}

public enum TempoInterpolation
{
    Step,
    Linear
}

public sealed class ScoreDocument
{
    private readonly List<ScoreSection> _sections = new();
    private readonly IReadOnlyList<ScoreSection> _sectionView;
    private double _percussionMasterGainDb;

    public ScoreDocument(string title, TempoMark defaultTempo, TimeSignature defaultTimeSignature)
        : this(title, defaultTempo, defaultTimeSignature, KeySignature.CMajor)
    {
    }

    public ScoreDocument(string title, TempoMark defaultTempo, TimeSignature defaultTimeSignature, KeySignature defaultKeySignature)
    {
        _sectionView = _sections.AsReadOnly();
        Title = title;
        DefaultTempo = defaultTempo ?? throw new ArgumentNullException(nameof(defaultTempo));
        DefaultTimeSignature = defaultTimeSignature;
        DefaultKeySignature = defaultKeySignature;
        _sections.Add(new ScoreSection("A", "New Section"));
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public string Title { get; set; }
    public TempoMark DefaultTempo { get; set; }
    public TimeSignature DefaultTimeSignature { get; set; }
    public KeySignature DefaultKeySignature { get; set; }
    /// <summary>Whether playback includes scheduled metronome clicks.</summary>
    public bool MetronomeEnabled { get; set; }
    /// <summary>Whether playback starts with a one-measure count-in.</summary>
    public bool CountInEnabled { get; set; }
    /// <summary>Whether a count-in is added at each playback loop restart.</summary>
    public bool CountInEveryLoop { get; set; }
    /// <summary>Additional post-synthesis gain applied to percussion tracks only.</summary>
    public double PercussionMasterGainDb
    {
        get => _percussionMasterGainDb;
        set
        {
            if (!double.IsFinite(value) || value is < -80 or > 20)
                throw new ArgumentOutOfRangeException(nameof(value), "Percussion master gain must be between -80 and 20 dB.");
            _percussionMasterGainDb = value;
        }
    }
    public List<ScoreTrack> Tracks { get; } = new();
    /// <summary>Metadata for immutable audio sources. Media bytes live in the managed cache, outside history snapshots.</summary>
    public List<AudioAsset> AudioAssets { get; } = new();
    public List<ScoreArrangement> Arrangements { get; } = new();
    public IReadOnlyList<ScoreSection> Sections => _sectionView;

    public ScoreTrack AddTrack(ScoreTrack track)
        => AddTrackCore(track, createRulerForAudio: true);

    /// <summary>Moves an editable track to a zero-based position among non-ruler tracks.</summary>
    public bool MoveTrack(ScoreTrack track, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind == TrackKind.Ruler || !Tracks.Any(candidate => ReferenceEquals(candidate, track)))
            throw new ArgumentException("The track must be an editable member of this score.", nameof(track));

        var orderedTracks = Tracks.Where(candidate => candidate.Kind != TrackKind.Ruler).ToList();
        if (destinationIndex < 0 || destinationIndex >= orderedTracks.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        var sourceIndex = orderedTracks.IndexOf(track);
        if (sourceIndex == destinationIndex) return false;

        orderedTracks.RemoveAt(sourceIndex);
        orderedTracks.Insert(destinationIndex, track);
        var rulers = Tracks.Where(candidate => candidate.Kind == TrackKind.Ruler).ToArray();
        Tracks.Clear();
        Tracks.AddRange(orderedTracks);
        Tracks.AddRange(rulers);
        return true;
    }

    internal ScoreTrack RestoreTrack(ScoreTrack track)
        => AddTrackCore(track, createRulerForAudio: false);

    private ScoreTrack AddTrackCore(ScoreTrack track, bool createRulerForAudio)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (Tracks.Exists(existing => existing.Id == track.Id))
        {
            throw new InvalidOperationException("A track with this identity already belongs to the score.");
        }

        Tracks.Add(track);
        foreach (var section in Sections)
        {
            if (section.LeadTrackId is null && track.Kind is not TrackKind.Audio)
                section.LeadTrackId = track.Id;
            foreach (var measure in section.Measures)
            {
                var signature = ResolveTimeSignature(section, measure);
                measure.TrackMeasures.Add(CreateDefaultTrackMeasure(track, signature));
            }
        }

        if (createRulerForAudio && track.Kind != TrackKind.Audio && track.Kind != TrackKind.Ruler && Tracks.Any(candidate => candidate.Kind == TrackKind.Ruler))
        {
            // Replacing the ruler as lead keeps the existing section grid intact.
            foreach (var section in Sections)
                if (section.LeadTrackId is null || Tracks.Any(candidate => candidate.Id == section.LeadTrackId && candidate.Kind == TrackKind.Ruler))
                    SetLeadTrack(section, track);
            RemoveRulerTracks();
        }

        if (createRulerForAudio && track.Kind == TrackKind.Audio)
            EnsureRulerForAudioOnlySections();

        return track;
    }

    private void EnsureRulerForAudioOnlySections()
    {
        if (!Tracks.Any(track => track.Kind == TrackKind.Audio) || Tracks.Any(track => track.Kind is not (TrackKind.Audio or TrackKind.Ruler)))
            return;
        var ruler = Tracks.FirstOrDefault(track => track.Kind == TrackKind.Ruler);
        if (ruler is null)
        {
            ruler = new ScoreTrack("Ruler", TrackKind.Ruler);
            Tracks.Add(ruler);
            foreach (var section in Sections)
                foreach (var measure in section.Measures)
                    measure.TrackMeasures.Add(CreateDefaultTrackMeasure(ruler, ResolveTimeSignature(section, measure)));
        }
        foreach (var section in Sections)
        {
            if (section.LeadTrackId is null) section.LeadTrackId = ruler.Id;
            if (section.Measures.Count == 0) AddMeasure(section);
        }

        var movable = Tracks.Where(track => track.Kind != TrackKind.Ruler).ToArray();
        var rulers = Tracks.Where(track => track.Kind == TrackKind.Ruler).ToArray();
        if (!Tracks.Select(track => track.Id).SequenceEqual(movable.Concat(rulers).Select(track => track.Id)))
        {
            Tracks.Clear();
            Tracks.AddRange(movable);
            Tracks.AddRange(rulers);
        }
    }

    private void RemoveRulerTracks()
    {
        foreach (var ruler in Tracks.Where(track => track.Kind == TrackKind.Ruler).ToArray())
        {
            Tracks.Remove(ruler);
            foreach (var section in Sections)
            {
                foreach (var measure in section.Measures) measure.TrackMeasures.RemoveAll(part => part.TrackId == ruler.Id);
                section.TrackPatternLengths.Remove(ruler.Id);
                if (section.LeadTrackId == ruler.Id) section.LeadTrackId = null;
            }
        }
    }

    /// <summary>Removes a track and its authored parts while preserving every shared measure.</summary>
    public void RemoveTrack(ScoreTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (!Tracks.Any(candidate => ReferenceEquals(candidate, track)))
            throw new ArgumentException("The track must belong to this score.", nameof(track));
        if (track.Kind == TrackKind.Ruler)
            throw new InvalidOperationException("The audio-only ruler is managed by the score and cannot be removed directly.");

        Tracks.Remove(track);
        foreach (var section in Sections) section.AudioClips.RemoveAll(clip => clip.TrackId == track.Id);
        foreach (var arrangement in Arrangements) arrangement.AudioClips.RemoveAll(clip => clip.TrackId == track.Id);
        foreach (var section in Sections)
        {
            foreach (var measure in section.Measures)
            {
                measure.TrackMeasures.RemoveAll(part => part.TrackId == track.Id);
                measure.TrackMeasureVersions.RemoveAll(version => version.TrackId == track.Id);
                measure.Alternations.RemoveAll(alternation => alternation.TrackId == track.Id);
                measure.TransitionRules.RemoveAll(rule => rule.TrackId == track.Id);
            }

            section.TrackPatternLengths.Remove(track.Id);
            if (section.LeadTrackId == track.Id)
                section.LeadTrackId = Tracks.FirstOrDefault(candidate => candidate.Kind is not (TrackKind.Audio or TrackKind.Ruler))?.Id;
        }
        if (track.Kind == TrackKind.Audio && !Tracks.Any(candidate => candidate.Kind == TrackKind.Audio))
            RemoveRulerTracks();
        else if (track.Kind != TrackKind.Audio)
            EnsureRulerForAudioOnlySections();
    }

    public ScoreSection AddSection(
        string name,
        string title,
        TempoMark? defaultTempo = null,
        TimeSignature? defaultTimeSignature = null,
        KeySignature? defaultKeySignature = null)
    {
        var section = new ScoreSection(name, title)
        {
            DefaultTempo = defaultTempo,
            DefaultTimeSignature = defaultTimeSignature,
            DefaultKeySignature = defaultKeySignature,
            LeadTrackId = Tracks.FirstOrDefault(track => track.Kind != TrackKind.Audio)?.Id
        };

        _sections.Add(section);
        AddMeasure(section);
        return section;
    }

    /// <summary>Removes a section and returns its next neighbour, previous neighbour, or a new empty default.</summary>
    public ScoreSection RemoveSection(ScoreSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var index = _sections.FindIndex(candidate => ReferenceEquals(candidate, section));
        if (index < 0)
        {
            throw new ArgumentException("The section must belong to this score.", nameof(section));
        }

        _sections.RemoveAt(index);
        foreach (var arrangement in Arrangements) arrangement.Sections.RemoveAll(entry => entry.SectionId == section.Id);
        if (Sections.Count == 0)
        {
            var replacement = new ScoreSection("A", "New Section")
            {
                LeadTrackId = Tracks.FirstOrDefault(track => track.Kind != TrackKind.Audio)?.Id
            };
            _sections.Add(replacement);
            if (Tracks.Any(track => track.Kind == TrackKind.Ruler)) AddMeasure(replacement);
            return replacement;
        }

        return Sections[Math.Min(index, Sections.Count - 1)];
    }

    /// <summary>The lead track spans the section's entire shared measure grid.</summary>
    public void SetLeadTrack(ScoreSection section, ScoreTrack track)
    {
        ValidateSectionAndTrack(section, track);
        if (track.Kind == TrackKind.Audio)
            throw new InvalidOperationException("Audio tracks cannot define section length.");
        if (section.LeadTrackId == track.Id) return;

        if (section.LeadTrackId is { } previousLead && section.Measures.Count > 0)
        {
            section.TrackPatternLengths[previousLead] = section.Measures.Count;
        }
        section.LeadTrackId = track.Id;
    }

    /// <summary>Returns the source pattern length in measures; zero means the section is empty.</summary>
    public int GetTrackPatternLength(ScoreSection section, ScoreTrack track)
    {
        ValidateSectionAndTrack(section, track);
        var count = section.Measures.Count;
        if (count == 0) return 0;
        if (section.LeadTrackId == track.Id) return count;
        return Math.Clamp(section.TrackPatternLengths.GetValueOrDefault(track.Id, 1), 1, count);
    }

    /// <summary>Sets how many of a non-lead track's measures repeat within the section.</summary>
    public void SetTrackPatternLength(ScoreSection section, ScoreTrack track, int measureCount)
    {
        ValidateSectionAndTrack(section, track);
        var sectionLength = section.Measures.Count;
        if (measureCount < (sectionLength == 0 ? 0 : 1) || measureCount > sectionLength)
        {
            throw new ArgumentOutOfRangeException(nameof(measureCount), "A pattern must fit within its section and contain at least one measure when the section is nonempty.");
        }
        if (section.LeadTrackId == track.Id && measureCount != sectionLength)
        {
            throw new ArgumentException("The lead track must span the whole section.", nameof(measureCount));
        }

        if (measureCount == 0) section.TrackPatternLengths.Remove(track.Id);
        else section.TrackPatternLengths[track.Id] = measureCount;
    }

    /// <summary>
    /// Resolves linked occurrences for non-lead tracks with explicit-repeat support.
    /// Unconfigured legacy percussion remains on its historical modulo pattern path.
    /// </summary>
    public TrackMeasureOccurrence GetNonPercussionOccurrence(ScoreSection section, ScoreTrack track, int destinationIndex)
    {
        ValidateSectionAndTrack(section, track);
        if (destinationIndex < 0) throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        if (destinationIndex >= section.Measures.Count || section.LeadTrackId == track.Id ||
            (track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit))
            return TrackMeasureOccurrence.Empty;

        var destination = 0;
        var sourceCount = GetTrackPatternLength(section, track);
        for (var sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            var sourceMeasure = section.Measures[sourceIndex];
            var sourcePart = sourceMeasure.GetTrackMeasure(track.Id);
            if (destination++ == destinationIndex)
                return new TrackMeasureOccurrence(sourceIndex, false, sourcePart);

            var repeatCount = sourcePart?.RepeatCount ?? 0;
            var remainingSourceCount = sourceCount - sourceIndex - 1;
            var availableRepeatSlots = Math.Max(0, section.Measures.Count - destination - remainingSourceCount);
            repeatCount = Math.Min(repeatCount, availableRepeatSlots);
            for (var repeat = 0; repeat < repeatCount; repeat++)
            {
                if (destination++ == destinationIndex)
                    return new TrackMeasureOccurrence(sourceIndex, true, sourcePart);
            }
        }

        return TrackMeasureOccurrence.Empty;
    }

    /// <summary>Sets the number of additional linked occurrences for one authored source measure.</summary>
    public void SetTrackMeasureRepeatCount(ScoreSection section, ScoreTrack track, int sourceMeasureIndex, int repeatCount)
    {
        ValidateSectionAndTrack(section, track);
        if (section.LeadTrackId == track.Id || (track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit))
            throw new InvalidOperationException("Per-measure linked repeats require a non-lead track with linked-repeat support.");
        if (repeatCount < 0) throw new ArgumentOutOfRangeException(nameof(repeatCount));

        var sourceCount = GetTrackPatternLength(section, track);
        if (sourceMeasureIndex < 0 || sourceMeasureIndex >= sourceCount)
            throw new ArgumentOutOfRangeException(nameof(sourceMeasureIndex), "The index must identify an authored source measure.");
        var sourceMeasure = section.Measures[sourceMeasureIndex];
        var sourcePart = sourceMeasure.GetTrackMeasure(track.Id)
            ?? throw new InvalidOperationException("The source measure has no part for this track.");
        var totalRepeats = section.Measures.Take(sourceCount)
            .Select(measure => measure.GetTrackMeasure(track.Id)?.RepeatCount ?? 0)
            .Sum(count => (long)count);
        var nextTotal = totalRepeats - sourcePart.RepeatCount + repeatCount;
        if (repeatCount > sourcePart.RepeatCount && nextTotal > section.Measures.Count - sourceCount)
            throw new InvalidOperationException("There are not enough unfilled destination bars for this repeat count; no authored source bars may be displaced.");

        sourcePart.RepeatCount = repeatCount;
    }

    /// <summary>
    /// Adds the next source measure for a non-lead track with linked-repeat support in an
    /// unused destination slot. Existing hidden part data is never replaced.
    /// </summary>
    public ScoreMeasure AppendTrackMeasureSource(ScoreSection section, ScoreTrack track, int? copyFromSourceIndex = null)
    {
        ValidateSectionAndTrack(section, track);
        if (section.LeadTrackId == track.Id || (track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit))
            throw new InvalidOperationException("Independent source measures require a non-lead track with linked-repeat support.");

        var sourceCount = GetTrackPatternLength(section, track);
        if (sourceCount >= section.Measures.Count)
            throw new InvalidOperationException("There is no unused destination bar available for another source measure.");
        if (GetNonPercussionOccurrence(section, track, section.Measures.Count - 1).SourceTrackMeasure is not null)
            throw new InvalidOperationException("There is no unfilled destination bar available for another source measure.");
        var newSourceIndex = sourceCount;
        if (copyFromSourceIndex is { } copyIndex && (copyIndex < 0 || copyIndex >= sourceCount))
            throw new ArgumentOutOfRangeException(nameof(copyFromSourceIndex), "The copy source must identify an existing authored measure.");

        var destinationMeasure = section.Measures[newSourceIndex];
        var existingPart = destinationMeasure.GetTrackMeasure(track.Id)
            ?? throw new InvalidOperationException("The destination measure has no part for this track.");
        if (!IsUnusedDefaultPart(existingPart, ResolveTimeSignature(section, destinationMeasure), track.Kind))
            throw new InvalidOperationException("The next source slot contains hidden authored data. It was preserved; make this track the lead to review it before adding a new source measure.");

        var newPart = copyFromSourceIndex is { } sourceIndex
            ? CloneTrackMeasure(section.Measures[sourceIndex].GetTrackMeasure(track.Id)!)
            : CreateDefaultTrackMeasure(track, ResolveTimeSignature(section, destinationMeasure));
        destinationMeasure.TrackMeasures[destinationMeasure.TrackMeasures.IndexOf(existingPart)] = newPart;
        section.TrackPatternLengths[track.Id] = newSourceIndex + 1;
        return destinationMeasure;
    }

    private static bool IsUnusedDefaultPart(TrackMeasure part, TimeSignature signature, TrackKind trackKind)
    {
        if (part.UsesTabEvents || part.TabEvents.Count != 0 || part.Voices.Count != 1) return false;
        var voice = part.Voices[0];
        if (voice.Number != 1 || voice.Beats.Count != signature.Numerator) return false;
        var expectedIntensity = TrackDefaults.IntensityFor(trackKind);
        var partIntensity = voice.Beats[0].Intensity;
        if (partIntensity != expectedIntensity && partIntensity != Beat.DefaultIntensity) return false;
        return voice.Beats.All(beat => beat.RhythmicValue == new RhythmicValue(signature.Denominator)
            && beat.Intensity == partIntensity
            && beat.PitchBendSemitones == 0 && beat.Notes.Count == 0);
    }

    private static TrackMeasure CloneTrackMeasure(TrackMeasure source)
    {
        var clone = new TrackMeasure(source.TrackId) { UsesTabEvents = source.UsesTabEvents, RepeatCount = 0, SnapDenominator = source.SnapDenominator };
        foreach (var sourceVoice in source.Voices)
        {
            var voice = new Voice { Number = sourceVoice.Number };
            foreach (var sourceBeat in sourceVoice.Beats)
            {
                var beat = new Beat(sourceBeat.RhythmicValue)
                {
                    Intensity = sourceBeat.Intensity,
                    PitchBendSemitones = sourceBeat.PitchBendSemitones
                };
                foreach (var note in sourceBeat.Notes)
                    beat.Notes.Add(new ScoreNote(note.MidiPitch, note.Velocity));
                voice.Beats.Add(beat);
            }
            clone.Voices.Add(voice);
        }
        foreach (var note in source.TabEvents)
        {
            var clonedNote = new TabEvent(note.StringNumber, note.Fret, note.MidiPitch, note.Velocity, note.Onset, note.Duration)
            {
                AllowsBarOverflow = note.AllowsBarOverflow,
                TupletActual = note.TupletActual,
                SoundVariation = note.SoundVariation,
                IncomingArticulation = note.IncomingArticulation
            };
            clonedNote.Expressions.AddRange(note.Expressions.Select(expression => expression.Clone()));
            clone.TabEvents.Add(clonedNote);
        }
        return clone;
    }

    private void ValidateSectionAndTrack(ScoreSection section, ScoreTrack track)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(track);
        if (!_sections.Exists(candidate => ReferenceEquals(candidate, section)))
            throw new ArgumentException("The section must belong to this score.", nameof(section));
        if (!Tracks.Exists(candidate => ReferenceEquals(candidate, track)))
            throw new ArgumentException("The track must belong to this score.", nameof(track));
    }

    public ScoreMeasure AddMeasure(
        ScoreSection section,
        TimeSignature? timeSignatureOverride = null,
        KeySignature? keySignatureOverride = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!_sections.Exists(existing => ReferenceEquals(existing, section)))
        {
            throw new ArgumentException("The section must belong to this score.", nameof(section));
        }

        var measure = new ScoreMeasure
        {
            TimeSignatureOverride = timeSignatureOverride,
            KeySignatureOverride = keySignatureOverride
        };
        var signature = timeSignatureOverride ?? ResolveTimeSignatureForNewMeasure(section);
        foreach (var track in Tracks)
        {
            measure.TrackMeasures.Add(CreateDefaultTrackMeasure(track, signature));
        }

        section.Measures.Add(measure);
        return measure;
    }

    public ScoreMeasure InsertMeasureAfter(ScoreSection section, int measureIndex)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!_sections.Exists(existing => ReferenceEquals(existing, section)))
        {
            throw new ArgumentException("The section must belong to this score.", nameof(section));
        }

        if (measureIndex < 0 || measureIndex >= section.Measures.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(measureIndex), "The index must identify an existing measure in the section.");
        }

        var insertionIndex = measureIndex + 1;
        var signature = ResolveTimeSignature(section, section.Measures[measureIndex]);
        return InsertEmptyMeasure(section, insertionIndex, signature);
    }

    /// <summary>Inserts an empty shared measure before an existing measure.</summary>
    public ScoreMeasure InsertMeasureBefore(ScoreSection section, int measureIndex)
    {
        ValidateMeasureIndex(section, measureIndex);
        var signature = ResolveTimeSignatureBefore(section, measureIndex);
        return InsertEmptyMeasure(section, measureIndex, signature);
    }

    /// <summary>Duplicates a complete shared measure before or after its source with independent event identities.</summary>
    public ScoreMeasure DuplicateMeasure(ScoreSection section, int measureIndex, bool before)
    {
        ValidateMeasureIndex(section, measureIndex);
        var source = section.Measures[measureIndex];
        var patternLengths = GetNonLeadPatternLengths(section);
        var duplicate = new ScoreMeasure
        {
            TimeSignatureOverride = source.TimeSignatureOverride,
            KeySignatureOverride = source.KeySignatureOverride,
            IsPickup = source.IsPickup,
            IsFreeTime = source.IsFreeTime,
            BarlineStyle = source.BarlineStyle
        };
        foreach (var tempo in source.TempoEvents)
            duplicate.TempoEvents.Add(new TempoEvent(tempo.Offset, tempo.Value, tempo.Interpolation));
        foreach (var part in source.TrackMeasures)
            duplicate.TrackMeasures.Add(CloneTrackMeasure(part));
        var versionIds = new Dictionary<Guid, Guid>();
        foreach (var version in source.TrackMeasureVersions)
        {
            var copied = new TrackMeasureVersion(version.TrackId, version.Name, CloneTrackMeasure(version.Content));
            foreach (var expression in copied.Content.TabEvents.SelectMany(note => note.Expressions)) expression.Id = Guid.NewGuid();
            versionIds.Add(version.Id, copied.Id);
            duplicate.TrackMeasureVersions.Add(copied);
        }
        foreach (var rule in source.TransitionRules)
            duplicate.TransitionRules.Add(new TrackMeasureTransitionRule(rule.TrackId, rule.SectionId, rule.VersionId == Guid.Empty ? Guid.Empty : versionIds[rule.VersionId]));
        foreach (var alternation in source.Alternations)
            duplicate.Alternations.Add(new TrackMeasureAlternation(alternation.TrackId, alternation.VersionIds.Select(id => id == Guid.Empty ? Guid.Empty : versionIds[id]).ToList()));
        var expressionIds = new Dictionary<Guid, Guid>();
        foreach (var expression in duplicate.TrackMeasures.SelectMany(part => part.TabEvents).SelectMany(note => note.Expressions))
        {
            if (!expressionIds.TryGetValue(expression.Id, out var copiedId))
                expressionIds.Add(expression.Id, copiedId = Guid.NewGuid());
            expression.Id = copiedId;
        }

        var insertionIndex = before ? measureIndex : measureIndex + 1;
        section.Measures.Insert(insertionIndex, duplicate);
        GrowPatternForInsertion(section, insertionIndex, patternLengths);
        return duplicate;
    }

    private ScoreMeasure InsertEmptyMeasure(ScoreSection section, int insertionIndex, TimeSignature signature)
    {
        var patternLengths = GetNonLeadPatternLengths(section);
        var measure = new ScoreMeasure();
        foreach (var track in Tracks)
            measure.TrackMeasures.Add(CreateDefaultTrackMeasure(track, signature));
        section.Measures.Insert(insertionIndex, measure);
        GrowPatternForInsertion(section, insertionIndex, patternLengths);
        return measure;
    }

    private void ValidateMeasureIndex(ScoreSection section, int measureIndex)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (!_sections.Exists(candidate => ReferenceEquals(candidate, section)))
            throw new ArgumentException("The section must belong to this score.", nameof(section));
        if (measureIndex < 0 || measureIndex >= section.Measures.Count)
            throw new ArgumentOutOfRangeException(nameof(measureIndex), "The index must identify an existing measure in the section.");
    }

    private Dictionary<Guid, int> GetNonLeadPatternLengths(ScoreSection section) => Tracks
        .Where(track => section.LeadTrackId != track.Id)
        .ToDictionary(track => track.Id, track => GetTrackPatternLength(section, track));

    private static void GrowPatternForInsertion(ScoreSection section, int insertionIndex, IReadOnlyDictionary<Guid, int> patternLengths)
    {
        foreach (var (trackId, patternLength) in patternLengths)
        {
            // Source measures occupy the prefix. Insertions strictly inside that
            // prefix shift a source right, so grow it to retain all prior sources.
            if (insertionIndex < patternLength)
                section.TrackPatternLengths[trackId] = patternLength + 1;
        }
    }

    private TimeSignature ResolveTimeSignatureBefore(ScoreSection section, int insertionIndex)
    {
        if (insertionIndex > 0)
            return ResolveTimeSignature(section, section.Measures[insertionIndex - 1]);
        var current = DefaultTimeSignature;
        foreach (var priorSection in Sections.TakeWhile(candidate => !ReferenceEquals(candidate, section)))
        {
            if (priorSection.DefaultTimeSignature is { } priorDefault) current = priorDefault;
            foreach (var priorMeasure in priorSection.Measures)
                if (priorMeasure.TimeSignatureOverride is { } priorOverride) current = priorOverride;
        }
        return section.DefaultTimeSignature ?? current;
    }

    /// <summary>Removes a shared measure, including its content for every track.</summary>
    public void RemoveMeasure(ScoreSection section, ScoreMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(measure);
        if (!_sections.Exists(existing => ReferenceEquals(existing, section)))
        {
            throw new ArgumentException("The section must belong to this score.", nameof(section));
        }

        if (section.Measures.Count == 1 && section.Measures.Contains(measure) &&
            Tracks.Any(track => track.Kind == TrackKind.Ruler && track.Id == section.LeadTrackId))
            throw new InvalidOperationException("An audio-only section must retain at least one ruler measure.");

        if (!section.Measures.Remove(measure))
        {
            throw new ArgumentException("The measure must belong to the specified section.", nameof(measure));
        }

        foreach (var trackId in section.TrackPatternLengths.Keys.ToArray())
        {
            if (section.Measures.Count == 0) section.TrackPatternLengths.Remove(trackId);
            else section.TrackPatternLengths[trackId] = Math.Min(section.TrackPatternLengths[trackId], section.Measures.Count);
        }
    }

    /// <summary>Adds a named, independent content version owned by one destination slot and track.</summary>
    public TrackMeasureVersion AddTrackMeasureVersion(ScoreSection section, ScoreMeasure measure, ScoreTrack track, string name, TrackMeasure content)
    {
        ValidateSlotTrack(section, measure, track);
        ArgumentNullException.ThrowIfNull(content);
        if (content.TrackId != track.Id) throw new ArgumentException("Version content must belong to the selected track.", nameof(content));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A version needs a name.", nameof(name));
        var version = new TrackMeasureVersion(track.Id, name.Trim(), CloneTrackMeasure(content));
        foreach (var expression in version.Content.TabEvents.SelectMany(note => note.Expressions)) expression.Id = Guid.NewGuid();
        measure.TrackMeasureVersions.Add(version);
        return version;
    }

    public bool RemoveTrackMeasureVersion(ScoreSection section, ScoreMeasure measure, ScoreTrack track, Guid versionId)
    {
        ValidateSlotTrack(section, measure, track);
        var removed = measure.TrackMeasureVersions.RemoveAll(version => version.Id == versionId && version.TrackId == track.Id) != 0;
        if (removed)
        {
            measure.TransitionRules.RemoveAll(rule => rule.TrackId == track.Id && rule.VersionId == versionId);
            foreach (var alternation in measure.Alternations.Where(item => item.TrackId == track.Id)) alternation.VersionIds.RemoveAll(id => id == versionId);
        }
        return removed;
    }

    public void SetTrackMeasureTransitionRule(ScoreSection section, ScoreMeasure measure, ScoreTrack track, Guid nextSectionId, Guid versionId)
    {
        ValidateSlotTrack(section, measure, track);
        if (!Sections.Any(candidate => candidate.Id == nextSectionId)) throw new ArgumentException("The target section must belong to this score.", nameof(nextSectionId));
        ValidateSlotVersion(measure, track.Id, versionId);
        measure.TransitionRules.RemoveAll(rule => rule.TrackId == track.Id && rule.SectionId == nextSectionId);
        measure.TransitionRules.Add(new TrackMeasureTransitionRule(track.Id, nextSectionId, versionId));
    }

    public bool RemoveTrackMeasureTransitionRule(ScoreSection section, ScoreMeasure measure, ScoreTrack track, Guid nextSectionId)
    {
        ValidateSlotTrack(section, measure, track);
        return measure.TransitionRules.RemoveAll(rule => rule.TrackId == track.Id && rule.SectionId == nextSectionId) != 0;
    }

    public void SetTrackMeasureAlternation(ScoreSection section, ScoreMeasure measure, ScoreTrack track, IEnumerable<Guid> orderedVersionIds)
    {
        ValidateSlotTrack(section, measure, track);
        ArgumentNullException.ThrowIfNull(orderedVersionIds);
        var ids = orderedVersionIds.ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new ArgumentException("Alternation entries must be unique.", nameof(orderedVersionIds));
        foreach (var id in ids) ValidateSlotVersion(measure, track.Id, id);
        var alternation = measure.Alternations.FirstOrDefault(item => item.TrackId == track.Id);
        if (alternation is null) measure.Alternations.Add(alternation = new TrackMeasureAlternation(track.Id));
        alternation.VersionIds.Clear();
        alternation.VersionIds.AddRange(ids);
    }

    public void RenameTrackMeasureVersion(ScoreSection section, ScoreMeasure measure, ScoreTrack track, Guid versionId, string name)
    {
        ValidateSlotTrack(section, measure, track);
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A version needs a name.", nameof(name));
        var version = measure.TrackMeasureVersions.SingleOrDefault(item => item.Id == versionId && item.TrackId == track.Id)
            ?? throw new ArgumentException("The version must belong to this destination slot and track.", nameof(versionId));
        version.Name = name.Trim();
    }

    public TrackMeasure GetTrackMeasureVersion(ScoreSection section, ScoreMeasure measure, ScoreTrack track, Guid versionId)
    {
        ValidateSlotTrack(section, measure, track);
        ValidateSlotVersion(measure, track.Id, versionId);
        if (versionId != Guid.Empty) return measure.TrackMeasureVersions.Single(version => version.Id == versionId).Content;
        var destinationIndex = section.Measures.IndexOf(measure);
        if (section.LeadTrackId == track.Id) return measure.GetTrackMeasure(track.Id)!;
        if (track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit)
        {
            var legacyPatternLength = GetTrackPatternLength(section, track);
            return section.Measures[destinationIndex % legacyPatternLength].GetTrackMeasure(track.Id) ?? measure.GetTrackMeasure(track.Id)!;
        }
        var occurrence = GetNonPercussionOccurrence(section, track, destinationIndex);
        return occurrence.SourceTrackMeasure ?? measure.GetTrackMeasure(track.Id)!;
    }

    private void ValidateSlotTrack(ScoreSection section, ScoreMeasure measure, ScoreTrack track)
    {
        ValidateSectionAndTrack(section, track);
        if (!section.Measures.Contains(measure)) throw new ArgumentException("The measure must belong to the specified section.", nameof(measure));
        if (measure.GetTrackMeasure(track.Id) is null) throw new InvalidOperationException("The destination slot has no content for this track.");
    }

    private static void ValidateSlotVersion(ScoreMeasure measure, Guid trackId, Guid versionId)
    {
        if (versionId != Guid.Empty && !measure.TrackMeasureVersions.Any(version => version.Id == versionId && version.TrackId == trackId))
            throw new ArgumentException("The version must belong to this destination slot and track.", nameof(versionId));
    }

    public TimeSignature ResolveTimeSignature(ScoreSection targetSection, ScoreMeasure targetMeasure)
    {
        var current = DefaultTimeSignature;

        foreach (var section in Sections)
        {
            if (section.DefaultTimeSignature is { } sectionSignature)
            {
                current = sectionSignature;
            }

            foreach (var measure in section.Measures)
            {
                if (measure.TimeSignatureOverride is { } measureSignature)
                {
                    current = measureSignature;
                }

                if (ReferenceEquals(section, targetSection) && ReferenceEquals(measure, targetMeasure))
                {
                    return current;
                }
            }
        }

        throw new ArgumentException("The measure must belong to the section in this score.", nameof(targetMeasure));
    }

    public KeySignature ResolveKeySignature(ScoreSection targetSection, ScoreMeasure targetMeasure)
    {
        var current = DefaultKeySignature;

        foreach (var section in Sections)
        {
            if (section.DefaultKeySignature is { } sectionKey)
            {
                current = sectionKey;
            }

            foreach (var measure in section.Measures)
            {
                if (measure.KeySignatureOverride is { } measureKey)
                {
                    current = measureKey;
                }

                if (ReferenceEquals(section, targetSection) && ReferenceEquals(measure, targetMeasure))
                {
                    return current;
                }
            }
        }

        throw new ArgumentException("The measure must belong to the section in this score.", nameof(targetMeasure));
    }

    /// <summary>
    /// Converts a section/measure-local musical offset into the exact whole-note position
    /// from the start of the score.
    /// </summary>
    public ScoreTime ToAbsolutePosition(ScoreSection targetSection, ScoreMeasure targetMeasure, ScoreTime measureOffset)
    {
        ArgumentNullException.ThrowIfNull(targetSection);
        ArgumentNullException.ThrowIfNull(targetMeasure);
        if (!measureOffset.IsInitialized)
        {
            throw new ArgumentException("A measure offset must be an initialized score position.", nameof(measureOffset));
        }

        if (measureOffset < ScoreTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(measureOffset), "A score position cannot precede its measure.");
        }

        var measureStart = ScoreTime.Zero;
        foreach (var section in Sections)
        {
            foreach (var measure in section.Measures)
            {
                if (ReferenceEquals(section, targetSection) && ReferenceEquals(measure, targetMeasure))
                {
                    var duration = GetTimelineMeasureDuration(section, measure);
                    if (measureOffset > duration)
                    {
                        throw new ArgumentOutOfRangeException(nameof(measureOffset), "The position is beyond the end of the measure.");
                    }

                    return measureStart + measureOffset;
                }

                measureStart += GetTimelineMeasureDuration(section, measure);
            }
        }

        throw new ArgumentException("The measure must belong to the section in this score.", nameof(targetMeasure));
    }

    /// <summary>
    /// Builds a tempo map from the score default, section defaults, and measure events.
    /// Measure event offsets are exact rational whole-note positions.
    /// </summary>
    public TempoMap CreateTempoMap()
    {
        var points = new List<TempoMapPoint>
        {
            new(ScoreTime.Zero, DefaultTempo)
        };
        var measureStart = ScoreTime.Zero;

        foreach (var section in Sections)
        {
            if (section.Measures.Count > 0 && section.DefaultTempo is { } sectionTempo)
            {
                points.Add(new TempoMapPoint(measureStart, sectionTempo));
            }

            foreach (var measure in section.Measures)
            {
                var measureDuration = GetTimelineMeasureDuration(section, measure);
                foreach (var tempoEvent in measure.TempoEvents)
                {
                    if (!tempoEvent.Offset.IsInitialized || tempoEvent.Offset < ScoreTime.Zero)
                    {
                        throw new InvalidOperationException("A tempo event needs a nonnegative, initialized measure position.");
                    }

                    if (tempoEvent.Offset > measureDuration)
                    {
                        throw new InvalidOperationException("A tempo event cannot be positioned beyond its measure.");
                    }

                    points.Add(new TempoMapPoint(
                        measureStart + tempoEvent.Offset,
                        tempoEvent.Value,
                        tempoEvent.Interpolation));
                }

                measureStart += measureDuration;
            }
        }

        return new TempoMap(points);
    }

    public TempoResolution ResolveTempoAt(ScoreTime absolutePosition) => CreateTempoMap().ResolveAt(absolutePosition);

    public TempoResolution ResolveTempoAt(ScoreSection section, ScoreMeasure measure, ScoreTime measureOffset) =>
        ResolveTempoAt(ToAbsolutePosition(section, measure, measureOffset));

    public List<VoiceDurationIssue> ValidateMeasure(ScoreSection section, ScoreMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(measure);
        if (!Sections.Any(candidate => ReferenceEquals(candidate, section)) ||
            !section.Measures.Any(candidate => ReferenceEquals(candidate, measure)))
        {
            throw new ArgumentException("The measure must belong to the specified section in this score.", nameof(measure));
        }

        return MeasureValidator.Validate(measure, ResolveTimeSignature(section, measure), Tracks);
    }

    private TimeSignature ResolveTimeSignatureForNewMeasure(ScoreSection targetSection)
    {
        if (targetSection.Measures.Count > 0)
        {
            return ResolveTimeSignature(targetSection, targetSection.Measures[^1]);
        }

        var current = DefaultTimeSignature;
        foreach (var section in Sections)
        {
            if (ReferenceEquals(section, targetSection))
            {
                return section.DefaultTimeSignature ?? current;
            }

            if (section.DefaultTimeSignature is { } sectionSignature)
            {
                current = sectionSignature;
            }

            foreach (var measure in section.Measures)
            {
                if (measure.TimeSignatureOverride is { } measureSignature)
                {
                    current = measureSignature;
                }
            }
        }

        throw new ArgumentException("The section must belong to this score.", nameof(targetSection));
    }

    private ScoreTime GetTimelineMeasureDuration(ScoreSection section, ScoreMeasure measure)
    {
        if (!measure.IsFreeTime && !measure.IsPickup)
        {
            return ResolveTimeSignature(section, measure).BarDuration;
        }

        var maximumVoiceDuration = ScoreTime.Zero;
        foreach (var voice in measure.TrackMeasures.SelectMany(trackMeasure => trackMeasure.Voices))
        {
            var voiceDuration = voice.Beats.Aggregate(ScoreTime.Zero, (total, beat) => total + beat.Duration);
            if (voiceDuration > maximumVoiceDuration)
            {
                maximumVoiceDuration = voiceDuration;
            }
        }

        var maximumTabEnd = measure.TrackMeasures.Where(trackMeasure => trackMeasure.UsesTabEvents).SelectMany(trackMeasure => trackMeasure.TabEvents)
            .Where(note => note.Onset.IsInitialized && note.Duration.IsInitialized && note.Onset >= ScoreTime.Zero && note.Duration > ScoreTime.Zero)
            .Select(note => note.EndPosition).DefaultIfEmpty(ScoreTime.Zero).Max();
        if (maximumTabEnd > maximumVoiceDuration) maximumVoiceDuration = maximumTabEnd;

        return maximumVoiceDuration;
    }

    private static TrackMeasure CreateDefaultTrackMeasure(ScoreTrack track, TimeSignature signature)
    {
        var trackMeasure = new TrackMeasure(track.Id);
        var voice = new Voice { Number = 1 };
        for (var beatIndex = 0; beatIndex < signature.Numerator; beatIndex++)
        {
            voice.Beats.Add(new Beat(new RhythmicValue(signature.Denominator))
            {
                Intensity = TrackDefaults.IntensityFor(track.Kind)
            });
        }

        trackMeasure.Voices.Add(voice);
        return trackMeasure;
    }
}

public sealed class ScoreTrack
{
    private readonly List<int> _tabTuningMidiPitches = new();
    private readonly IReadOnlyList<int> _tabTuningView;
    private readonly List<PercussionPiece> _percussionPieces = new();
    private readonly IReadOnlyList<PercussionPiece> _percussionPiecesView;
    private bool _hasPercussionKitConfiguration;
    public ScoreTrack(string name, TrackKind kind, int defaultMidiProgram = 0)
    {
        _tabTuningView = _tabTuningMidiPitches.AsReadOnly();
        _percussionPiecesView = _percussionPieces.AsReadOnly();
        Name = name;
        Kind = kind;
        DefaultMidiProgram = defaultMidiProgram;
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public string Name { get; set; }
    public TrackKind Kind { get; set; }
    public int DefaultMidiProgram { get; set; }
    /// <summary>Default tablature/percussion editing grid for newly authored measures; null is a legacy score awaiting workspace migration.</summary>
    public int? TabSnapDenominator
    {
        get => _tabSnapDenominator;
        set
        {
            if (value is not (null or 4 or 8 or 16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(value));
            _tabSnapDenominator = value;
        }
    }
    private int? _tabSnapDenominator = 16;
    /// <summary>Stable UI-level identifier for an instrument image; null uses the track kind default.</summary>
    public string? InstrumentImageId { get; set; }
    /// <summary>Track display color as 0xRRGGBBAA, defaulting to opaque white.</summary>
    public uint DisplayColorRgba { get; set; } = 0xffffffff;
    public double VolumeDb { get; set; } = 0.0;
    public bool IsMuted { get; set; }
    public bool IsSoloed { get; set; }
    /// <summary>Open-string MIDI pitches in displayed order (string 1 first). Empty means unconfigured.</summary>
    public IReadOnlyList<int> TabTuningMidiPitches => _tabTuningView;
    /// <summary>Ordered percussion lanes. Each lane's MIDI pitch is also its open-string pitch.</summary>
    public IReadOnlyList<PercussionPiece> PercussionPieces => _percussionPiecesView;
    public bool HasConfiguredPercussionKit => Kind == TrackKind.Percussion && _hasPercussionKitConfiguration;
    public int MaxFret { get; private set; } = 24;

    public void ConfigureTab(IEnumerable<int> openStringMidiPitches, int maxFret = 24)
    {
        ArgumentNullException.ThrowIfNull(openStringMidiPitches);
        var pitches = openStringMidiPitches.ToArray();
        if (pitches.Length is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(openStringMidiPitches), "A tab track must have between 1 and 24 strings.");
        if (pitches.Any(p => p is < 0 or > 127)) throw new ArgumentOutOfRangeException(nameof(openStringMidiPitches), "Open-string pitches must be valid MIDI pitches.");
        if (maxFret is < 0 or > 96) throw new ArgumentOutOfRangeException(nameof(maxFret));
        if (_tabTuningMidiPitches.Count != 0 && (!_tabTuningMidiPitches.SequenceEqual(pitches) || MaxFret != maxFret))
            throw new InvalidOperationException("Changing an established tuning is blocked to protect existing string/fret assignments.");
        _tabTuningMidiPitches.Clear();
        _tabTuningMidiPitches.AddRange(pitches);
        MaxFret = maxFret;
    }

    internal void ApplyRetunedConfiguration(IReadOnlyList<int> pitches)
    {
        ArgumentNullException.ThrowIfNull(pitches);
        if (Kind is not (TrackKind.Guitar or TrackKind.Bass))
            throw new InvalidOperationException("Only guitar and bass tracks support tablature retuning.");
        if (pitches.Count is < 1 or > 24 || pitches.Any(pitch => pitch is < 0 or > 127))
            throw new ArgumentOutOfRangeException(nameof(pitches), "A tuning must contain 1–24 MIDI pitches from 0 to 127.");
        _tabTuningMidiPitches.Clear();
        _tabTuningMidiPitches.AddRange(pitches);
    }

    /// <summary>Configures fixed-pitch percussion lanes in display order.</summary>
    public void ConfigurePercussionPieces(IEnumerable<PercussionPiece> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (Kind != TrackKind.Percussion) throw new InvalidOperationException("Percussion pieces can only be configured on percussion tracks.");
        var ordered = pieces.ToArray();
        if (ordered.Length > 24) throw new ArgumentOutOfRangeException(nameof(pieces), "A percussion track can contain at most 24 pieces.");
        if (ordered.Any(piece => piece is null)) throw new ArgumentException("Percussion pieces cannot contain null entries.", nameof(pieces));
        if (ordered.Select(piece => piece.Id).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Percussion piece IDs must be unique.", nameof(pieces));
        var pitches = ordered.Select(piece => piece.MidiPitch).ToArray();
        if (pitches.Distinct().Count() != pitches.Length)
            throw new ArgumentException("Percussion piece pitches must be unique so event lanes map unambiguously.", nameof(pieces));
        if (_percussionPieces.Count != 0 && (!_percussionPieces.Select(piece => piece.Id).SequenceEqual(ordered.Select(piece => piece.Id)) ||
            !_tabTuningMidiPitches.SequenceEqual(pitches)))
            throw new InvalidOperationException("Changing configured percussion lanes is blocked; add or remove lanes through the safe piece APIs.");
        if (_tabTuningMidiPitches.Count != 0 && !_tabTuningMidiPitches.SequenceEqual(pitches))
            throw new InvalidOperationException("Percussion lane pitches must match the existing ordered pitch configuration.");
        _percussionPieces.Clear();
        _percussionPieces.AddRange(ordered);
        _tabTuningMidiPitches.Clear();
        _tabTuningMidiPitches.AddRange(pitches);
        _hasPercussionKitConfiguration = true;
        MaxFret = 0;
    }

    public PercussionPiece AddPercussionPiece(string id, string name, int midiPitch)
    {
        if (Kind != TrackKind.Percussion) throw new InvalidOperationException("Percussion pieces can only be added to percussion tracks.");
        if (_percussionPieces.Count >= 24) throw new InvalidOperationException("A percussion track can contain at most 24 pieces.");
        if (_percussionPieces.Any(piece => string.Equals(piece.Id, id, StringComparison.Ordinal)))
            throw new ArgumentException("A percussion piece with this ID already exists.", nameof(id));
        if (_percussionPieces.Any(piece => piece.MidiPitch == midiPitch))
            throw new ArgumentException("Percussion piece pitches must be unique so event lanes map unambiguously.", nameof(midiPitch));
        if (_tabTuningMidiPitches.Count != _percussionPieces.Count)
            throw new InvalidOperationException("The existing percussion pitch configuration has no matching lane metadata.");
        var piece = new PercussionPiece(id, name, midiPitch);
        _percussionPieces.Add(piece);
        _tabTuningMidiPitches.Add(midiPitch);
        _hasPercussionKitConfiguration = true;
        MaxFret = 0;
        return piece;
    }

    /// <summary>Removes a lane, deletes its confirmed hits, and compacts later row numbers.</summary>
    public bool RemovePercussionPiece(string id, IEnumerable<TrackMeasure> trackMeasures, bool confirmDeleteHits = false)
    {
        ArgumentNullException.ThrowIfNull(trackMeasures);
        if (Kind != TrackKind.Percussion) throw new InvalidOperationException("Percussion pieces can only be removed from percussion tracks.");
        var index = _percussionPieces.FindIndex(piece => string.Equals(piece.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        var parts = trackMeasures.ToArray();
        if (parts.Any(part => part.TrackId != Id)) throw new ArgumentException("All supplied parts must belong to this track.", nameof(trackMeasures));
        var lane = index + 1;
        if (parts.Any(part => part.TabEvents.Any(note => note.StringNumber == lane)) && !confirmDeleteHits) return false;
        if (parts.SelectMany(part => part.TabEvents).Any(note => note.StringNumber < 1 || note.StringNumber > _percussionPieces.Count ||
            note.Fret != 0 || _tabTuningMidiPitches[note.StringNumber - 1] != note.MidiPitch))
            throw new InvalidOperationException("The track contains percussion events that do not match its current piece lanes.");
        foreach (var part in parts)
        {
            part.TabEvents.RemoveAll(note => note.StringNumber == lane);
            foreach (var note in part.TabEvents)
                if (note.StringNumber > lane) note.StringNumber--;
        }
        _percussionPieces.RemoveAt(index);
        _tabTuningMidiPitches.RemoveAt(index);
        return true;
    }

    internal void RestoreTabConfiguration(IEnumerable<int> pitches, int maxFret)
    {
        if (pitches.Any()) ConfigureTab(pitches, maxFret);
        else if (maxFret is >= 0 and <= 96) MaxFret = maxFret;
        else throw new ArgumentOutOfRangeException(nameof(maxFret));
    }
}

/// <summary>A named fixed-pitch GM percussion lane.</summary>
public sealed class PercussionPiece
{
    private string _name;
    private double _gainDb;
    public PercussionPiece(string id, string name, int midiPitch)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A percussion piece needs a stable ID.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A percussion piece needs a name.", nameof(name));
        if (midiPitch is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(midiPitch));
        Id = id;
        _name = name.Trim();
        MidiPitch = midiPitch;
    }
    public string Id { get; }
    public string Name { get => _name; set => _name = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A percussion piece needs a name.", nameof(value)) : value.Trim(); }
    public int MidiPitch { get; }
    public bool IsMuted { get; set; }
    public double GainDb
    {
        get => _gainDb;
        set => _gainDb = double.IsFinite(value) && value is >= -80 and <= 20 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public sealed class ScoreSection
{
    public ScoreSection(string name, string title)
    {
        Name = name;
        Title = title;
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public string Name { get; set; }
    public string Title { get; set; }
    public TempoMark? DefaultTempo { get; set; }
    public TimeSignature? DefaultTimeSignature { get; set; }
    public KeySignature? DefaultKeySignature { get; set; }
    public Guid? LeadTrackId { get; internal set; }
    internal Dictionary<Guid, int> TrackPatternLengths { get; } = new();
    public List<ScoreMeasure> Measures { get; } = new();
    /// <summary>Section-relative audio placements, repeated with each section occurrence.</summary>
    public List<AudioClip> AudioClips { get; } = new();
}

/// <summary>Maps a destination bar to its authored source bar, or marks it unfilled.</summary>
public readonly record struct TrackMeasureOccurrence(int? SourceMeasureIndex, bool IsRepeat, TrackMeasure? SourceTrackMeasure)
{
    public static TrackMeasureOccurrence Empty { get; } = new(null, false, null);
}

public sealed class ScoreMeasure
{
    public Guid Id { get; internal set; } = Guid.NewGuid();
    public TimeSignature? TimeSignatureOverride { get; set; }
    public KeySignature? KeySignatureOverride { get; set; }
    public bool IsPickup { get; set; }
    public bool IsFreeTime { get; set; }
    public string? BarlineStyle { get; set; }
    public List<TempoEvent> TempoEvents { get; } = new();
    public List<TrackMeasure> TrackMeasures { get; } = new();
    public List<TrackMeasureVersion> TrackMeasureVersions { get; } = new();
    public List<TrackMeasureTransitionRule> TransitionRules { get; } = new();
    public List<TrackMeasureAlternation> Alternations { get; } = new();

    public TrackMeasure? GetTrackMeasure(Guid trackId)
    {
        foreach (var trackMeasure in TrackMeasures)
        {
            if (trackMeasure.TrackId == trackId)
            {
                return trackMeasure;
            }
        }

        return null;
    }
}

/// <summary>Named content owned by one destination measure slot and track.</summary>
public sealed class TrackMeasureVersion
{
    public TrackMeasureVersion(Guid trackId, string name, TrackMeasure content)
    {
        TrackId = trackId;
        Name = name;
        Content = content ?? throw new ArgumentNullException(nameof(content));
        if (content.TrackId != trackId) throw new ArgumentException("Version content must belong to its owner track.", nameof(content));
    }
    public Guid Id { get; internal set; } = Guid.NewGuid();
    public Guid TrackId { get; }
    public string Name { get; set; }
    public TrackMeasure Content { get; }
}

/// <summary>One per-track selection when playback enters the specified next section.</summary>
public sealed record TrackMeasureTransitionRule(Guid TrackId, Guid SectionId, Guid VersionId);

/// <summary>Ordered fallback choices for one track; Guid.Empty means its original slot content.</summary>
public sealed class TrackMeasureAlternation
{
    public TrackMeasureAlternation(Guid trackId) => TrackId = trackId;
    public TrackMeasureAlternation(Guid trackId, List<Guid> versionIds) { TrackId = trackId; VersionIds = versionIds; }
    public Guid TrackId { get; }
    public List<Guid> VersionIds { get; } = new();
}

public sealed class TempoEvent
{
    private ScoreTime _offset;
    private TempoMark _value = null!;
    private TempoInterpolation _interpolation;

    public TempoEvent(ScoreTime offset, TempoMark value, TempoInterpolation interpolation = TempoInterpolation.Step)
    {
        Offset = offset;
        Value = value;
        Interpolation = interpolation;
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public ScoreTime Offset
    {
        get => _offset;
        set
        {
            if (!value.IsInitialized)
            {
                throw new ArgumentException("A tempo event needs an initialized score position.", nameof(value));
            }

            if (value < ScoreTime.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A tempo event cannot start before its measure.");
            }

            _offset = value;
        }
    }

    public TempoMark Value
    {
        get => _value;
        set => _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public TempoInterpolation Interpolation
    {
        get => _interpolation;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _interpolation = value;
        }
    }
}

public sealed class TrackMeasure
{
    private int _repeatCount;
    private int _snapDenominator;

    public TrackMeasure(Guid trackId)
    {
        TrackId = trackId;
    }

    public Guid TrackId { get; }
    /// <summary>Measure-local editing grid; zero means use the track's editor default for legacy content.</summary>
    public int SnapDenominator
    {
        get => _snapDenominator;
        set
        {
            if (value is not (0 or 4 or 8 or 16 or 32 or 64)) throw new ArgumentOutOfRangeException(nameof(value));
            _snapDenominator = value;
        }
    }
    /// <summary>Additional linked destination bars following this authored source bar.</summary>
    public int RepeatCount
    {
        get => _repeatCount;
        internal set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _repeatCount = value;
        }
    }
    public List<Voice> Voices { get; } = new();
    /// <summary>Independent string tablature events; legacy beat notes remain separate and are never auto-assigned.</summary>
    public List<TabEvent> TabEvents { get; } = new();
    /// <summary>When true, tab events are the sounding source and legacy beat notes are retained but not authoritative.</summary>
    public bool UsesTabEvents { get; set; }
}

/// <summary>A single independently timed string/fret note, with exact measure-relative timing.</summary>
public sealed class TabEvent
{
    public TabEvent(int stringNumber, int fret, int midiPitch, int velocity, ScoreTime onset, ScoreTime duration)
    {
        StringNumber = stringNumber;
        Fret = fret;
        MidiPitch = midiPitch;
        Velocity = velocity;
        Onset = onset;
        Duration = duration;
        ValidateBasic();
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public int StringNumber { get; set; }
    public int Fret { get; set; }
    public int MidiPitch { get; set; }
    public int Velocity { get; set; }
    /// <summary>Stable identifier for an optional per-note sound variation; empty preserves the track sound.</summary>
    public string SoundVariation { get; set; } = "";
    /// <summary>Articulation applied by this note from the preceding note on the same string.</summary>
    public StringArticulation IncomingArticulation { get; set; }
    /// <summary>Five-step editing scale; reading it never changes the stored MIDI velocity.</summary>
    public int Intensity
    {
        get
        {
            if (Velocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(Velocity));
            var closest = 1;
            for (var level = 2; level <= 5; level++)
                if (Math.Abs(Velocity - Beat.MidiVelocityForIntensity(level)) <
                    Math.Abs(Velocity - Beat.MidiVelocityForIntensity(closest))) closest = level;
            return closest;
        }
        set => Velocity = Beat.MidiVelocityForIntensity(value);
    }
    public ScoreTime Onset { get; set; }
    public ScoreTime Duration { get; set; }
    /// <summary>Expression curves attached to this source note; curves with matching IDs are linked.</summary>
    public List<NoteExpression> Expressions { get; } = new();
    /// <summary>Tuplet note count: 0 means straight; supported tuplets are 3:2, 5:4, and 6:4.</summary>
    public int TupletActual
    {
        get => _tupletActual;
        set
        {
            if (value is not (0 or 3 or 5 or 6)) throw new ArgumentOutOfRangeException(nameof(value), "Supported tuplets are straight, 3:2, 5:4, and 6:4.");
            _tupletActual = value;
        }
    }
    private int _tupletActual;
    /// <summary>Normal note count paired with <see cref="TupletActual"/>; straight notes return 1.</summary>
    public int TupletNormal => TupletActual switch { 3 => 2, 5 or 6 => 4, _ => 1 };

    /// <summary>Applies a tuplet against one unswung grid unit and updates its exact sounding duration.</summary>
    public void SetTuplet(int actual, ScoreTime baseUnit)
    {
        if (!baseUnit.IsInitialized || baseUnit <= ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(baseUnit));
        var normal = actual switch { 0 => 1, 3 => 2, 5 or 6 => 4, _ => throw new ArgumentOutOfRangeException(nameof(actual)) };
        TupletActual = actual;
        Duration = new ScoreTime(checked(baseUnit.Numerator * normal), checked(baseUnit.Denominator * (actual == 0 ? 1 : actual)));
    }
    /// <summary>Whether this event may attack beyond the destination bar when its pattern repeats.</summary>
    public bool AllowsBarOverflow { get; set; }
    public ScoreTime EndPosition => Onset + Duration;

    internal void ValidateBasic()
    {
        if (StringNumber < 1) throw new ArgumentOutOfRangeException(nameof(StringNumber));
        if (Fret < 0) throw new ArgumentOutOfRangeException(nameof(Fret));
        if (MidiPitch is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(MidiPitch));
        if (Velocity is < 1 or > 127) throw new ArgumentOutOfRangeException(nameof(Velocity));
        if (!Onset.IsInitialized || Onset < ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(Onset));
        if (!Duration.IsInitialized || Duration <= ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(Duration));
    }
}

/// <summary>Legato articulation entering a tablature note.</summary>
public enum StringArticulation
{
    None = 0,
    HammerOn = 1,
    PullOff = 2
}

public sealed class Voice
{
    public Guid Id { get; internal set; } = Guid.NewGuid();
    public int Number { get; set; } = 1;
    public List<Beat> Beats { get; } = new();

    /// <summary>
    /// Gets the exact onset of a beat by summing the durations of preceding beats.
    /// Passing Beats.Count returns the voice's end position.
    /// </summary>
    public ScoreTime GetBeatStartPosition(int beatIndex)
    {
        if (beatIndex < 0 || beatIndex > Beats.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(beatIndex));
        }

        var position = ScoreTime.Zero;
        for (var index = 0; index < beatIndex; index++)
        {
            position += Beats[index].Duration;
        }

        return position;
    }
}

public sealed class Beat
{
	public const int DefaultIntensity = 3;
	public const int DefaultMidiVelocity = (127 * DefaultIntensity + 2) / 5;
	private int _intensity = DefaultIntensity;
    private decimal _pitchBendSemitones;

    public Beat(RhythmicValue rhythmicValue)
    {
        RhythmicValue = rhythmicValue;
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public RhythmicValue RhythmicValue { get; set; }
    public List<ScoreNote> Notes { get; } = new();
    public ScoreTime Duration => RhythmicValue.Duration;

    /// <summary>
    /// Channel-wide pitch bend applied at this beat's onset and reset at its end.
    /// The supported range matches the playback synthesizer's fixed +/-2 semitone range.
    /// Overlapping bends on one MIDI channel are resolved by event order and are not
    /// independently maintained per note.
    /// </summary>
    public decimal PitchBendSemitones
    {
        get => _pitchBendSemitones;
        set
        {
            if (value is < -2m or > 2m)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Pitch bend must be between -2 and 2 semitones.");
            }

            _pitchBendSemitones = value;
        }
    }

	/// <summary>
	/// Beat execution intensity on a five-step scale. The value maps to MIDI note velocity.
	/// </summary>
	public int Intensity
	{
		get => _intensity;
		set
		{
			var velocity = MidiVelocityForIntensity(value);
			_intensity = value;
			foreach (var note in Notes)
			{
				note.Velocity = velocity;
			}
		}
	}

	public void AddNote(ScoreNote note)
	{
		ArgumentNullException.ThrowIfNull(note);
		note.Velocity = MidiVelocityForIntensity(_intensity);
		Notes.Add(note);
	}

	public static int MidiVelocityForIntensity(int intensity)
	{
		if (intensity is < 1 or > 5)
		{
			throw new ArgumentOutOfRangeException(nameof(intensity), "Beat intensity must be between 1 and 5.");
		}

		return (127 * intensity + 2) / 5;
	}
}

public sealed class ScoreNote
{
    public ScoreNote(int midiPitch, int velocity)
    {
        if (midiPitch is < 0 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(midiPitch), "MIDI pitch must be between 0 and 127.");
        }

        if (velocity is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(velocity), "Velocity must be between 1 and 127.");
        }

        MidiPitch = midiPitch;
        Velocity = velocity;
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public int MidiPitch { get; set; }
    public int Velocity { get; set; }
}

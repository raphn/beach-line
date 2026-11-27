#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>An occupied attack elsewhere in the resolved target timeline, expressed in target-measure coordinates.</summary>
public sealed record TabPasteOccupied(TrackMeasure SourceMeasure, TabEvent Event, ScoreTime TargetOnset, ScoreTime? EffectiveDuration = null);
public sealed record TabPasteEntry(int ClipIndex, int StringNumber, int Fret, int MidiPitch, int Velocity, ScoreTime Onset, ScoreTime Duration, int TupletActual = 0, string SoundVariation = "", StringArticulation IncomingArticulation = StringArticulation.None);
public sealed record TabCopySource(TrackMeasure SourceMeasure, TabEvent Event, ScoreTime AbsoluteOnset);

/// <summary>Immutable clipboard payload and atomic paste planner for tablature and fixed-pitch percussion events.</summary>
public sealed class TabClipboard
{
    private sealed record ClipNote(int StringDelta, int? OriginalStringNumber, ScoreTime OnsetDelta, int Fret, int Velocity, ScoreTime Duration, int TupletActual, string SoundVariation, StringArticulation IncomingArticulation, NoteExpression[] Expressions);
    private readonly ClipNote[] _notes;
    private readonly bool _fullTab;
    private readonly bool _exactPlacement;

    private TabClipboard(ClipNote[] notes, bool fullTab = false, bool exactPlacement = false) { _notes = notes; _fullTab = fullTab; _exactPlacement = exactPlacement; }
    public int NoteCount => _notes.Length;

    /// <summary>Creates a simultaneous, exact-duration chord clipboard from string/fret assignments.</summary>
    public static TabClipboard CreateChord(ScoreTrack track,
        IReadOnlyCollection<(int StringNumber, int Fret)> notes, ScoreTime duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(notes);
        if (track.Kind is not (TrackKind.Guitar or TrackKind.Bass))
            throw new ArgumentException("Chords require a guitar or bass tablature track.", nameof(track));
        ValidateTabTrack(track);
        if (!duration.IsInitialized || duration <= ScoreTime.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (notes.Count < 2) throw new ArgumentException("A chord requires at least two notes.", nameof(notes));
        var assignments = notes.ToArray();
        if (assignments.Select(n => n.StringNumber).Distinct().Count() != assignments.Length)
            throw new ArgumentException("A chord can contain only one note per string.", nameof(notes));
        foreach (var (stringNumber, fret) in assignments)
            TabEventEditing.ValidateAssignment(track, stringNumber, fret);
        return CreateChordPayload(track, assignments, duration, false);
    }

    /// <summary>Creates a simultaneous chord on the chromatic pitch grid.</summary>
    public static TabClipboard CreateFullTabChord(ScoreTrack track, IReadOnlyCollection<int> pitches, ScoreTime duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(pitches);
        if (!track.UsesFullTab)
            throw new ArgumentException("Chords require a chromatic pitch grid track.", nameof(track));
        ValidateTabTrack(track);
        if (!duration.IsInitialized || duration <= ScoreTime.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        if (pitches.Count < 2 || pitches.Distinct().Count() != pitches.Count)
            throw new ArgumentException("A chord requires at least two distinct pitches.", nameof(pitches));
        if (pitches.Any(pitch => pitch < 0 || pitch > 127))
            throw new ArgumentOutOfRangeException(nameof(pitches), "Pitches must be between 0 and 127.");
        var assignments = pitches.Select(pitch => (StringNumber: 128 - pitch, Fret: 0)).ToArray();
        foreach (var (stringNumber, fret) in assignments)
            TabEventEditing.ValidateAssignment(track, stringNumber, fret);
        return CreateChordPayload(track, assignments, duration, true);
    }

    /// <summary>Creates a single exact-duration note for direct board entry.</summary>
    public static TabClipboard CreateSingleNote(ScoreTrack track, int stringNumber, int fret, ScoreTime duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        ValidateTabTrack(track);
        if (!duration.IsInitialized || duration <= ScoreTime.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        TabEventEditing.ValidateAssignment(track, stringNumber, fret);
        var velocity = TrackDefaults.VelocityFor(track.Kind);
        return new TabClipboard(new[] { new ClipNote(0, track.UsesFullTab ? stringNumber : null, ScoreTime.Zero, fret, velocity, duration, 0, "", StringArticulation.None, Array.Empty<NoteExpression>()) }, track.UsesFullTab, exactPlacement: true);
    }

    private static TabClipboard CreateChordPayload(ScoreTrack track, (int StringNumber, int Fret)[] assignments,
        ScoreTime duration, bool fullTab)
    {
        var anchor = assignments.Min(n => n.StringNumber);
        var velocity = TrackDefaults.VelocityFor(track.Kind);
        return new TabClipboard(assignments.Select(n => new ClipNote(n.StringNumber - anchor, fullTab ? n.StringNumber : null, ScoreTime.Zero,
            n.Fret, velocity, duration, 0, "", StringArticulation.None, Array.Empty<NoteExpression>())).ToArray(), fullTab);
    }

    /// <summary>Copies selected events with relative timing and source lines. Legacy beat notes are unsupported.</summary>
    public static TabClipboard Copy(ScoreTrack sourceTrack, TrackMeasure sourceMeasure,
        IReadOnlyCollection<TabEvent> selected, int anchorStringNumber, ScoreTime anchorOnset)
    {
        ArgumentNullException.ThrowIfNull(sourceTrack);
        ArgumentNullException.ThrowIfNull(sourceMeasure);
        ArgumentNullException.ThrowIfNull(selected);
        ValidateTabTrack(sourceTrack);
        if (selected.Count == 0) throw new ArgumentException("Select at least one note.", nameof(selected));
        var unique = selected.Distinct().ToArray();
        if (unique.Length != selected.Count) throw new ArgumentException("Selected events must be distinct.", nameof(selected));
        return Copy(sourceTrack, unique.Select(n => new TabCopySource(sourceMeasure, n, n.Onset)).ToArray(), anchorStringNumber, anchorOnset);
    }

    /// <summary>Copies visible occurrences, allowing a source event to appear at multiple absolute timeline positions.</summary>
    public static TabClipboard Copy(ScoreTrack sourceTrack, IReadOnlyList<TabCopySource> selectedOccurrences,
        int anchorStringNumber, ScoreTime anchorAbsoluteOnset)
    {
        ArgumentNullException.ThrowIfNull(sourceTrack);
        ArgumentNullException.ThrowIfNull(selectedOccurrences);
        ValidateTabTrack(sourceTrack);
        if (selectedOccurrences.Count == 0) throw new ArgumentException("Select at least one note occurrence.", nameof(selectedOccurrences));
        if (!anchorAbsoluteOnset.IsInitialized || anchorAbsoluteOnset < ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(anchorAbsoluteOnset));
        TabEventEditing.ValidateAssignment(sourceTrack, anchorStringNumber, 0);
        // The clipboard anchor is the top-left corner of the selection bounds.
        // Keep the legacy anchor parameters in the API, but do not let click/drag position alter copied offsets.
        var validOccurrences = selectedOccurrences
            .Where(occurrence => occurrence is not null && occurrence.Event is not null)
            .ToArray();
        if (validOccurrences.Length == 0) throw new ArgumentException("A selected occurrence cannot be null.", nameof(selectedOccurrences));
        var copyAnchorStringNumber = validOccurrences.Min(occurrence => occurrence.Event.StringNumber);
        var copyAnchorAbsoluteOnset = validOccurrences.Min(occurrence => occurrence.AbsoluteOnset);
        var copies = new ClipNote[selectedOccurrences.Count];
        for (var i = 0; i < selectedOccurrences.Count; i++)
        {
            var occurrence = selectedOccurrences[i] ?? throw new ArgumentException("A selected occurrence cannot be null.", nameof(selectedOccurrences));
            if (occurrence.SourceMeasure is null || occurrence.Event is null || occurrence.SourceMeasure.TrackId != sourceTrack.Id ||
                !occurrence.SourceMeasure.TabEvents.Contains(occurrence.Event))
                throw new ArgumentException("Each occurrence must identify a live event in a source measure owned by the track.", nameof(selectedOccurrences));
            if (!occurrence.AbsoluteOnset.IsInitialized || occurrence.AbsoluteOnset < ScoreTime.Zero)
                throw new ArgumentOutOfRangeException(nameof(selectedOccurrences), "Occurrence onsets must be initialized and nonnegative.");
            TabEventEditing.ValidateAssignment(sourceTrack, occurrence.Event.StringNumber, occurrence.Event.Fret);
            copies[i] = new ClipNote(occurrence.Event.StringNumber - copyAnchorStringNumber,
                selectedOccurrences.Count > 1 ? occurrence.Event.StringNumber : null,
                occurrence.AbsoluteOnset - copyAnchorAbsoluteOnset, occurrence.Event.Fret, occurrence.Event.Velocity, occurrence.Event.Duration,
                occurrence.Event.TupletActual, occurrence.Event.SoundVariation, occurrence.Event.IncomingArticulation, occurrence.Event.Expressions.Select(expression => expression.Clone()).ToArray());
        }
        return new TabClipboard(copies, sourceTrack.UsesFullTab);
    }

    /// <summary>Plans a paste. Copied groups retain source lines; a lone note follows the target line and advances past collisions.</summary>
    public PastePlan PlanPaste(ScoreTrack targetTrack, TrackMeasure targetMeasure, int anchorStringNumber,
        ScoreTime anchorOnset, ScoreTime? collisionStep = null, ScoreTime? visibleBarDuration = null,
        IReadOnlyList<TabPasteOccupied>? otherOccupied = null)
    {
        ArgumentNullException.ThrowIfNull(targetTrack);
        ArgumentNullException.ThrowIfNull(targetMeasure);
        ValidateTabTrack(targetTrack);
        if (_fullTab != targetTrack.UsesFullTab) throw new InvalidOperationException("Cannot paste between a chromatic pitch grid and another tablature grid.");
        if (targetMeasure.TrackId != targetTrack.Id) throw new ArgumentException("The measure part belongs to another track.", nameof(targetMeasure));
        if (!targetMeasure.UsesTabEvents && targetMeasure.Voices.SelectMany(v => v.Beats).Any(b => b.Notes.Count != 0))
            throw new InvalidOperationException("Convert legacy notes in the destination part before pasting tablature events.");
        if (!anchorOnset.IsInitialized || anchorOnset < ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(anchorOnset));
        TabEventEditing.ValidateAssignment(targetTrack, anchorStringNumber, 0);
        var step = collisionStep ?? new ScoreTime(1, 16);
        if (!step.IsInitialized || step <= ScoreTime.Zero) throw new ArgumentOutOfRangeException(nameof(collisionStep));
        if (visibleBarDuration is { } barDuration && (!barDuration.IsInitialized || barDuration <= ScoreTime.Zero))
            throw new ArgumentOutOfRangeException(nameof(visibleBarDuration));
        if (visibleBarDuration is { } anchorLimit && anchorOnset >= anchorLimit)
            throw new ArgumentOutOfRangeException(nameof(anchorOnset), "The paste anchor must be inside the destination measure.");
        otherOccupied ??= Array.Empty<TabPasteOccupied>();
        foreach (var occupied in otherOccupied)
        {
            if (occupied is null || occupied.SourceMeasure is null || occupied.SourceMeasure.TrackId != targetTrack.Id || occupied.Event is null || !occupied.TargetOnset.IsInitialized ||
                (occupied.EffectiveDuration is { } d && (!d.IsInitialized || d <= ScoreTime.Zero)) ||
                !occupied.SourceMeasure.TabEvents.Contains(occupied.Event)) throw new ArgumentException("Every occupied attack must identify a live source event and valid resolved onset.", nameof(otherOccupied));
        }

        var pending = new List<PlannedNote>();
        var dropped = new List<int>();
        for (var i = 0; i < _notes.Length; i++)
        {
            var source = _notes[i];
            var stringNumber = source.OriginalStringNumber ?? anchorStringNumber + source.StringDelta;
            if (stringNumber < 1 || stringNumber > targetTrack.TabLaneMidiPitches.Count)
            {
                dropped.Add(i);
                continue;
            }
            try { TabEventEditing.ValidateAssignment(targetTrack, stringNumber, source.Fret); }
            catch (ArgumentOutOfRangeException) { dropped.Add(i); continue; }
            var onset = anchorOnset + source.OnsetDelta;
            if (onset < ScoreTime.Zero) { dropped.Add(i); continue; }
            pending.Add(new PlannedNote(i, stringNumber, source.Fret, targetTrack.TabLaneMidiPitches[stringNumber - 1] + source.Fret,
                source.Velocity, onset, source.Duration, source.TupletActual, source.SoundVariation, source.IncomingArticulation, source.Expressions));
        }

        if (!_exactPlacement && _notes.Length == 1 && pending.Count == 1)
        {
            var n = pending[0];
            if (visibleBarDuration is { } initialLimit && n.Onset >= initialLimit)
                throw new InvalidOperationException("A single note must start in the destination measure.");
            while (HasIntervalConflict(n.StringNumber, n.Onset, n.Duration, targetMeasure, otherOccupied))
            {
                n = n with { Onset = n.Onset + step };
                if (visibleBarDuration is { } limit && n.Onset >= limit)
                    throw new InvalidOperationException("No unoccupied onset remains in the visible bar for this note.");
            }
            pending[0] = n;
        }
        var collisions = pending.SelectMany(n => targetMeasure.TabEvents
                .Where(e => !otherOccupied.Any(o => ReferenceEquals(o.SourceMeasure, targetMeasure) && ReferenceEquals(o.Event, e)) &&
                    e.StringNumber == n.StringNumber && IntervalsOverlap(n.Onset, n.Duration, e.Onset,
                    TabEventEditing.GetEffectiveEnd(targetMeasure.TabEvents, e) - e.Onset)).Select(e => e.Id))
            .Distinct().ToArray();
        var allConflicts = collisions.Concat(pending.SelectMany(n => otherOccupied.Where(e => e.Event.StringNumber == n.StringNumber &&
            IntervalsOverlap(n.Onset, n.Duration, e.TargetOnset, GetOccupiedDuration(e))).Select(e => e.Event.Id))).Distinct().ToArray();
        return new PastePlan(targetTrack, targetMeasure, pending, dropped, allConflicts, otherOccupied,
            targetMeasure.TabEvents.Select(e => (e.Id, e.StringNumber, e.Onset, e.Duration, e.TupletActual)).ToArray(), targetMeasure.UsesTabEvents, visibleBarDuration);
    }

    private static bool HasIntervalConflict(int stringNumber, ScoreTime onset, ScoreTime duration, TrackMeasure target,
        IReadOnlyList<TabPasteOccupied> external) => target.TabEvents.Any(e => !external.Any(o => ReferenceEquals(o.SourceMeasure, target) && ReferenceEquals(o.Event, e)) && e.StringNumber == stringNumber &&
            IntervalsOverlap(onset, duration, e.Onset, TabEventEditing.GetEffectiveEnd(target.TabEvents, e) - e.Onset)) ||
        external.Any(e => e.Event.StringNumber == stringNumber && IntervalsOverlap(onset, duration, e.TargetOnset, GetOccupiedDuration(e)));

    private static bool IntervalsOverlap(ScoreTime firstStart, ScoreTime firstDuration, ScoreTime secondStart, ScoreTime secondDuration) =>
        firstStart < secondStart + secondDuration && secondStart < firstStart + firstDuration;

    private static ScoreTime GetOccupiedDuration(TabPasteOccupied occupied) => occupied.EffectiveDuration ??
        (TabEventEditing.GetEffectiveEnd(occupied.SourceMeasure.TabEvents, occupied.Event) - occupied.Event.Onset);

    private static void ValidateTabTrack(ScoreTrack track)
    {
        if (track.TabLaneMidiPitches.Count == 0) throw new InvalidOperationException("The track has no configured tablature tuning.");
    }

    internal sealed record PlannedNote(int ClipIndex, int StringNumber, int Fret, int MidiPitch, int Velocity, ScoreTime Onset, ScoreTime Duration, int TupletActual, string SoundVariation, StringArticulation IncomingArticulation, NoteExpression[] Expressions);

    public sealed class PastePlan
    {
        private readonly ScoreTrack _track;
        private readonly TrackMeasure _measure;
        private readonly PlannedNote[] _pending;
        private readonly (Guid Id, int StringNumber, ScoreTime Onset, ScoreTime Duration, int TupletActual)[] _original;
        private readonly TabPasteOccupied[] _external;
        private readonly (TrackMeasure Measure, TabEvent Event, int StringNumber, ScoreTime Onset, ScoreTime Duration, int TupletActual, ScoreTime TargetOnset)[] _externalSnapshot;
        private readonly bool _wasUsingTabs;
        private readonly ScoreTime? _visibleBarDuration;
        internal PastePlan(ScoreTrack track, TrackMeasure measure, IEnumerable<PlannedNote> pending, IEnumerable<int> dropped,
            IEnumerable<Guid> collisions, IReadOnlyList<TabPasteOccupied> external, (Guid, int, ScoreTime, ScoreTime, int)[] original, bool wasUsingTabs, ScoreTime? visibleBarDuration)
        {
            _track = track; _measure = measure; _pending = pending.ToArray(); DroppedClipIndices = dropped.ToArray();
            ConflictingEventIds = collisions.ToArray(); _external = external.ToArray(); _original = original; _wasUsingTabs = wasUsingTabs; _visibleBarDuration = visibleBarDuration;
            _externalSnapshot = _external.Select(o => (o.SourceMeasure, o.Event, o.Event.StringNumber, o.Event.Onset, o.Event.Duration,
                o.Event.TupletActual, o.TargetOnset)).ToArray();
        }
        public IReadOnlyList<int> DroppedClipIndices { get; }
        public IReadOnlyList<Guid> ConflictingEventIds { get; }
        public bool RequiresConfirmation => DroppedClipIndices.Count != 0 || ConflictingEventIds.Count != 0;
        public int PasteCount => _pending.Length;
        public IReadOnlyList<TabPasteEntry> Entries => Array.AsReadOnly(_pending.Select(n => new TabPasteEntry(n.ClipIndex,n.StringNumber,n.Fret,n.MidiPitch,n.Velocity,n.Onset,n.Duration,n.TupletActual,n.SoundVariation,n.IncomingArticulation)).ToArray());

        /// <summary>Applies only after full validation; rejection or stale plans leave the measure untouched.</summary>
        public IReadOnlyList<TabEvent> Apply(bool confirmDestructiveChanges = false)
        {
            if (RequiresConfirmation && !confirmDestructiveChanges)
                throw new InvalidOperationException("This paste drops out-of-range notes or replaces existing attacks and requires confirmation.");
            var current = _measure.TabEvents.Select(e => (e.Id, e.StringNumber, e.Onset, e.Duration, e.TupletActual)).ToArray();
            if (_measure.UsesTabEvents != _wasUsingTabs || !_original.SequenceEqual(current))
                throw new InvalidOperationException("The destination changed after the paste was planned; create a new plan.");
            if (!_measure.UsesTabEvents && _measure.Voices.SelectMany(voice => voice.Beats).Any(beat => beat.Notes.Count != 0))
                throw new InvalidOperationException("The destination now contains legacy notes; create a new plan.");
            foreach (var occupied in _externalSnapshot)
                if (!occupied.Measure.TabEvents.Contains(occupied.Event) || occupied.Event.StringNumber != occupied.StringNumber ||
                    occupied.Event.Onset != occupied.Onset || occupied.Event.Duration != occupied.Duration || occupied.Event.TupletActual != occupied.TupletActual)
                    throw new InvalidOperationException("A timeline conflict changed after the paste was planned; create a new plan.");
            foreach (var n in _pending) TabEventEditing.ValidateAssignment(_track, n.StringNumber, n.Fret);
            var plannedKeys = _pending.Select(n => (n.StringNumber, n.Onset)).ToArray();
            if (plannedKeys.Distinct().Count() != plannedKeys.Length)
                throw new InvalidOperationException("The clipboard contains multiple attacks at the same destination string and onset.");
            var removeIds = new HashSet<Guid>(ConflictingEventIds);
            if (_pending.Any(n => _measure.TabEvents.Any(e => !removeIds.Contains(e.Id) && e.StringNumber == n.StringNumber && e.Onset == n.Onset)))
                throw new InvalidOperationException("The destination now contains a conflicting attack.");
            var created = _pending.Select(n => new TabEvent(n.StringNumber, n.Fret, n.MidiPitch, n.Velocity, n.Onset, n.Duration)
            {
                AllowsBarOverflow = _visibleBarDuration is { } limit && n.Onset >= limit,
                TupletActual = n.TupletActual,
                SoundVariation = n.SoundVariation,
                IncomingArticulation = n.IncomingArticulation
            }).Select((createdNote, index) => { createdNote.Expressions.AddRange(_pending[index].Expressions.Select(expression => expression.Clone())); return createdNote; }).ToArray();
            foreach (var source in _external.Select(o => o.SourceMeasure).Append(_measure).Distinct())
                source.TabEvents.RemoveAll(e => removeIds.Contains(e.Id));
            _measure.TabEvents.AddRange(created);
            if (created.Length != 0) _measure.UsesTabEvents = true;
            return Array.AsReadOnly(created);
        }
    }
}

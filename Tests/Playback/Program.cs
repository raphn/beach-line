using System.Reflection;
using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Playback;
using OpenTab.Music.Persistence;

if (args.Contains("--generate-example"))
{
    var demo = new ScoreDocument("Drum Editing Demo", new TempoMark(100m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var verse = demo.Sections[0]; verse.Title = "Verse";
    demo.AddMeasure(verse);
    demo.AddSection("B", "Fill", new TempoMark(116m, new RhythmicValue(4)), new TimeSignature(6, 8));
    Directory.CreateDirectory("_project/examples");
    ScoreFile.Save("_project/examples/drum_editing_demo.otab", demo);
    Console.WriteLine("Generated example score.");
    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("arrangement fallback versions alternate across real rendered loop passes and preserve refresh", TransitionLoopPlayback),
    ("ordinary playback follows the selected version and ignores arrangement rules", OrdinaryVersionSelection),
    ("transition rules resolve actual repeated sections and do not consume alternation", TransitionArrangementPlayback),
    ("measure loops use the selected version and ignore transitions", TransitionMeasureLoopAndSeek),
    ("changing the selected version refreshes future onsets in the active measure", LiveVersionSelectionRefresh),
    ("a newly created version can take over the active ordinary measure", LiveCreatedVersionSelectionRefresh),
    ("arrangement TAB versions preserve authored sustain until the selected next onset", TransitionTabSustain),
    ("linked destinations honor editor selection and late starts", TransitionLinkedDestination),
    ("count-in and refresh preserve the editor-selected version", TransitionCountIn),
    ("silent alternatives release long TAB sustains at repeated-section jumps", TransitionRepeatedSustainRelease),
    ("non-arrangement scopes ignore contextual transition rules", TransitionNonFinalScope),
    ("seeking past a versioned slot skips its overflowed notes", TransitionSeekSkipsOverflow),
    ("General MIDI instrument catalog has all unique standard names and expected program IDs", GeneralMidiInstrumentCatalog),
    ("audio and ruler tracks do not allocate or schedule MIDI channels", AudioTracksDoNotUseMidi),
    ("audio clip schedule projects section repeats and matching arrangement audio", AudioClipScheduleProjection),
    ("audio timeline timing converts tempo-aware section and repeated arrangement positions", AudioTimelineTimingRegression),
    ("future note and intensity edits replace upcoming events", FutureEdits),
    ("past edits do not replay until the next loop", PastEditsLoop),
    ("measure loop is local, wraps, and reports its source cursor", MeasureLoopScopeAndCursor),
    ("measure loop uses the selected measure's meter duration", MeasureLoopMeterDuration),
    ("measure loop cursor maps through a selected meter override", MeasureLoopMeterCursor),
    ("selected non-consecutive sections play in score order and skip gaps", DiscontiguousSectionLoop),
    ("selected-section loop can start at a later scoped measure", StartInsideSectionLoop),
    ("loop scope boundary releases a sustaining note", LoopBoundaryReleasesNotes),
    ("missing and empty loop scopes stop without falling back to score", EmptyLoopScopes),
    ("measure loop refresh reschedules edited future occurrences", MeasureLoopRefresh),
    ("shortening a measure loop clamps a carried sustain to its new boundary", ShortenedMeasureLoopRelease),
    ("arrangements play ordered repeated sections and loop only when enabled", ArrangementPlayback),
    ("projected timeline frame distinguishes repeated arrangement occurrences and queued positions", ArrangementTimelineFrame),
    ("repeated arrangement sections release sustaining notes at each jump", ArrangementSustainBoundaries),
    ("arrangement playback preserves source section tempos", ArrangementTempo),
    ("active note keeps its original note-off after removal", ActiveNoteRelease),
    ("mute, solo, and intensity velocity remain independent of volume", TrackControls),
    ("overfull beats are clipped at the bar boundary", Overfull),
    ("tab events sustain across measures and cut off at the next same-string onset", TabSustainCutoff),
    ("tab tuplet durations schedule exact playback note on and off frames", TabTupletPlayback),
    ("tab events owned by a measure can start after its barline and cut off at a later onset", TabOverflowOnsetCutoff),
    ("mixed-meter repeats clip ordinary onsets but preserve explicitly pasted overflow", TabMixedMeterOverflow),
    ("tab events cut off at a later section onset", TabSectionCutoff),
    ("tab overflow onset in one section cuts off at a later section onset", TabOverflowSectionCutoff),
    ("tab events repeat at destination measure onsets", TabRepeatOnsets),
    ("same-pitch tab strings retain independent active note ownership", TabSamePitchStrings),
    ("tab intensity schedules the edited MIDI velocity", TabIntensityPlayback),
    ("tab expression curves schedule tempo-aware pitch, tremolo, and attack velocity", TabExpressionSchedule),
    ("hammer-ons and pull-offs continue the same isolated voice", StringLegatoVoiceContinuity),
    ("isolated articulation preview preserves authored pair data and schedules its destination", StringArticulationPreviewScore),
    ("scoped section jumps release a sustained source before an incoming legato destination", StringLegatoSectionJump),
    ("invalid or isolated legato destinations use a normal attack", StringLegatoFallback),
    ("legato survives count-in and selected content versions", StringLegatoCountInAndVersions),
    ("step expressions switch at their authored playback boundary", ExpressionStepPlayback),
    ("isolated note preview uses the resolved reference beat and respects loop changes", NoteExpressionPreviewTimingRules),
    ("note expression pitch limits persist and retain legacy defaults", NoteExpressionRangePersistence),
    ("overlapping expressive notes keep independent synth voices through note-off", TabExpressionVoiceIsolation),
    ("ordinary and expressive same-pitch tab notes release in either order", SamePitchOrdinaryAndExpressiveRelease),
    ("expressive percussion voices retain independent hit slots and piece gain", ExpressivePercussionVoiceIsolation),
    ("live refresh reschedules future expression points", TabExpressionRefresh),
    ("expressive voices stop cleanly and survive measure-loop repeats", ExpressionStopAndLoop),
    ("beat editing chooses the largest fitting incomplete-bar placeholder", BeatEditingLargestFit),
    ("beat editing repeats the last full rhythmic value when requested", BeatEditingRepeatLastValue),
    ("beat editing can fill a gap with successive placeholders", BeatEditingSuccessiveGaps),
    ("beat editing exactly encodes residuals below a 64th note", BeatEditingSub64th),
    ("beat editing skips complete, overfull, pickup, and free-time voices", BeatEditingNoPlaceholderCases),
    ("beat editing placeholder candidates do not mutate or sound", BeatEditingCandidateIsPure),
    ("dotted rhythm updates playback timing and survives saving", DottedBeatTiming),
    ("quarter-note tempo conversion preserves score, section, and event units", TempoQuarterBpmConversionsAndOverridesRoundTrip),
    ("score tempo edits reschedule upcoming playback", TempoEditRefresh),
    ("metronome clicks follow score tempo and arrangement timeline", MetronomeTiming),
    ("count-in schedules audible meter clicks ahead of score and pins cursor", CountInTiming),
    ("count-in loops at score start using its own meter", CountInLoopTiming),
    ("refresh preserves count-in and later no-count loop phase", CountInRefreshPhase),
    ("insertion before current measure preserves its identity and progress", InsertBeforeCurrentMeasure),
    ("starting at a measure skips earlier hits and preserves setup and cursor identity", StartAtMeasure),
    ("cursor fraction follows musical time through an in-measure tempo change", CursorMusicalFraction),
    ("non-looping playback continues through trailing silence", TrailingSilence),
    ("meter change preserves elapsed position within current measure", MeterChange),
    ("measure removal affects all tracks and permits an empty section", RemoveMeasure),
    ("deleting the current measure plays the next measure from its start", DeleteCurrentMeasure),
    ("deleting an earlier measure preserves the current position", DeleteEarlierMeasure),
    ("removing a track refreshes upcoming playback events", RemoveTrackRefresh),
    ("new scores start with an empty section and assign its first lead", DefaultSection),
    ("section lead and pattern lengths remain valid when switching", LeadTrackAndPatternLengths),
    ("shorter drum part repeats to the section boundary", RepeatingPatternStopsAtSection),
    ("two-measure track pattern alternates across the section", TwoMeasurePattern),
    ("non-lead melodic repeats are explicit and leave later bars silent", ExplicitMelodicRepeats),
    ("deleting sections selects a neighbour or creates an empty default", DeleteSection),
    ("live edits refresh future repeated occurrences", RefreshRepeatedPattern),
    ("deleting the current section moves playback to the next section", DeleteCurrentSection),
    ("score archive preserves section deletion and identities", ScoreFileRoundTrip),
    ("configured kit resolves fixed pitches, mute and linked repeats", ConfiguredKitEvents),
    ("per-note sound variations resolve independently for melodic and configured percussion playback", SoundVariationPlayback),
    ("configured kit piece gains are isolated and refresh", ConfiguredKitGain),
    ("each drum piece has four independent hit slots", DrumPolyphony),
    ("closed and pedal hats choke a sounding open hat", HiHatChoke),
    ("track dB gain scales sounding audio without changing velocity", TrackGain),
    ("tracks using the same drum pitch mix independently", TrackGainIsolation),
    ("live score refresh changes gain on a sounding note", TrackGainRefresh),
    ("percussion master gain persists and older archives default to zero", PercussionMasterGainPersistence),
    ("percussion master gain composes with track gain and leaves melodic tracks unchanged", PercussionMasterGainAudio),
    ("live percussion master edits affect sounding notes without compounding", PercussionMasterGainRefresh)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
    }
}

Console.WriteLine($"{tests.Length - failed}/{tests.Length} playback regression checks passed.");
return failed == 0 ? 0 : 1;

static (ScoreDocument Score, ScoreTrack Track, ScoreSection Section, ScoreMeasure Measure, Voice Voice) MakeScore()
{
    var score = new ScoreDocument("Regression", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var track = score.AddTrack(new ScoreTrack("Kit", TrackKind.Percussion));
    var section = score.Sections[0];
    section.Title = "A";
    var measure = score.AddMeasure(section);
    return (score, track, section, measure, measure.GetTrackMeasure(track.Id)!.Voices[0]);
}

static void AudioTracksDoNotUseMidi()
{
    var score = new ScoreDocument("Audio only", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    score.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var clock = new ScorePlaybackClock(score, 48000);
    var usedChannels = (int[])typeof(ScorePlaybackClock)
        .GetField("_usedChannels", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(clock)!;
    Check(usedChannels.Length == 0, "Audio-only playback reserved a MIDI channel.");
    Check(PendingEvents(clock).All(item => item.Kind is not ("ProgramChange" or "BankSelect" or "NoteOn" or "NoteOff" or "ExpressiveNoteOn")),
        "Audio-only playback emitted MIDI setup or note events.");
}

static void AudioClipScheduleProjection()
{
    var score = new ScoreDocument("Audio projection", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var track = score.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var section = score.Sections[0];
    score.AddMeasure(section);
    var asset = new AudioAsset(Guid.NewGuid(), "tone.wav", new string('0', 64), 48000, 1, 200000, "cache-tone");
    score.AudioAssets.Add(asset);
    var sectionClip = new AudioClip(asset.Id, track.Id, ScoreTime.Zero, 1200, 24000);
    section.AudioClips.Add(sectionClip);
    var arrangement = ArrangementEditing.Add(score);
    ArrangementEditing.Append(score, arrangement, section.Id);
    ArrangementEditing.Append(score, arrangement, section.Id);
    var arrangementClip = new AudioClip(asset.Id, track.Id, new ScoreTime(1, 4), 0, 48000);
    arrangement.AudioClips.Add(arrangementClip);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null,
        new[] { section.Id, section.Id }, arrangement.Id);

    var schedule = AudioClipSchedule.Build(score, 48000, selection);
    var sectionPlacements = schedule.Where(item => item.ClipId == sectionClip.Id).OrderBy(item => item.StartFrame).ToArray();
    Check(sectionPlacements.Length == 2, "Section clip did not repeat with both section occurrences.");
    Check(sectionPlacements[0] == new ScheduledAudioClip(sectionClip.Id, asset.Id, track.Id, 0, 24000, 1200),
        "First section clip projection used incorrect timeline or source frames.");
    Check(sectionPlacements[1] == new ScheduledAudioClip(sectionClip.Id, asset.Id, track.Id, 192000, 216000, 1200),
        "Repeated section clip was not shifted by the section occurrence duration.");
    var arrangementPlacement = schedule.Single(item => item.ClipId == arrangementClip.Id);
    Check(arrangementPlacement.StartFrame == 24000 && arrangementPlacement.EndFrame == 72000,
        $"Arrangement audio did not use its arrangement-relative musical start: {arrangementPlacement}.");
    var withoutArrangementId = AudioClipSchedule.Build(score, 48000,
        new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, new[] { section.Id }));
    Check(withoutArrangementId.All(item => item.ClipId != arrangementClip.Id),
        "An unselected arrangement's audio clip leaked into playback.");

    var secondMeasure = section.Measures[1];
    var crossingClip = new AudioClip(asset.Id, track.Id, new ScoreTime(3, 4), 0, 48000);
    section.AudioClips.Add(crossingClip);
    var measureSelection = new PlaybackLoopSelection(PlaybackLoopMode.Measure, secondMeasure.Id, Array.Empty<Guid>());
    var measurePlacement = AudioClipSchedule.Build(score, 48000, measureSelection).Single(item => item.ClipId == crossingClip.Id);
    Check(measurePlacement.StartFrame == 0 && measurePlacement.EndFrame == 24000 && measurePlacement.SourceOffsetFrames == 24000,
        "Measure-scoped playback did not seek a section clip that began before the selected measure.");

    var slow = score.AddSection("B", "Slow", new TempoMark(60m, new RhythmicValue(4)), new TimeSignature(4, 4));
    score.AddMeasure(slow);
    var reordered = ArrangementEditing.Add(score);
    ArrangementEditing.Append(score, reordered, slow.Id);
    ArrangementEditing.Append(score, reordered, section.Id);
    reordered.AudioClips.Add(new AudioClip(asset.Id, track.Id, new ScoreTime(1, 4), 0, 24000));
    var reorderedSelection = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null,
        new[] { slow.Id, section.Id }, reordered.Id);
    var reorderedPlacement = AudioClipSchedule.Build(score, 48000, reorderedSelection)
        .Single(item => item.ClipId == reordered.AudioClips[0].Id);
    Check(reorderedPlacement.StartFrame == 48000,
        "Arrangement-relative start did not use the tempo of its reordered source section.");
}

static void AudioTimelineTimingRegression()
{
    var score = new ScoreDocument("Audio timing", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var first = score.Sections[0];
    score.AddMeasure(first);
    score.AddMeasure(first);
    first.Measures[0].TempoEvents.Add(new TempoEvent(new ScoreTime(1, 2), new TempoMark(60m, new RhythmicValue(4)), TempoInterpolation.Linear));
    var second = score.AddSection("B", "Fast", new TempoMark(240m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var sectionTiming = new AudioTimelineTiming(score, section: second);
    Check(Math.Abs(sectionTiming.GetDurationSeconds() - 1.0) < 1e-8,
        $"Section duration ignored its source offset/section tempo: {sectionTiming.GetDurationSeconds()}.");
    Check(sectionTiming.TryGetScoreTimeAtSeconds(0.25, out var quarter, ScoreTimeRounding.Ceiling),
        "Section seconds did not map to a musical position.");
    Check(quarter == new ScoreTime(1, 4), $"Exact quarter-second boundary was unnecessarily rounded up: {quarter}.");
    Check(sectionTiming.TryGetElapsedSeconds(quarter, out var roundTrip) && roundTrip >= 0.25,
        "Ceiling conversion moved the split point before its source time.");
    Check(!sectionTiming.TryGetScoreTimeAtSeconds(1.0, out _), "The section end was not treated as out of range.");
    Check(!sectionTiming.TryGetScoreTimeAtSeconds(0.99999999, out _, ScoreTimeRounding.Ceiling),
        "Ceiling rounding escaped the valid section range at its end.");

    var firstTiming = new AudioTimelineTiming(score, section: first);
    var firstTempoMap = score.CreateTempoMap();
    foreach (var position in new[] { new ScoreTime(1, 4), new ScoreTime(3, 4), new ScoreTime(5, 4) })
    {
        var expectedSeconds = firstTempoMap.GetElapsedSeconds(position);
        Check(firstTiming.TryGetElapsedSeconds(position, out var elapsed) && Math.Abs(elapsed - expectedSeconds) < 1e-9,
            "Audio timing did not follow an in-section tempo event.");
        Check(firstTiming.TryGetScoreTimeAtSeconds(expectedSeconds, out var restored) &&
            firstTiming.TryGetElapsedSeconds(restored, out var restoredSeconds) && Math.Abs(restoredSeconds - expectedSeconds) < 0.00001,
            "Audio timing did not round-trip across a tempo event.");
    }

    var arrangement = ArrangementEditing.Add(score);
    ArrangementEditing.Append(score, arrangement, second.Id);
    ArrangementEditing.Append(score, arrangement, first.Id);
    ArrangementEditing.Append(score, arrangement, second.Id);
    var arrangementTiming = new AudioTimelineTiming(score, arrangement: arrangement);
    var expected = sectionTiming.GetDurationSeconds() * 2 +
        (score.CreateTempoMap().GetElapsedSeconds(new ScoreTime(2, 1)) - score.CreateTempoMap().GetElapsedSeconds(ScoreTime.Zero));
    Check(Math.Abs(arrangementTiming.GetDurationSeconds() - expected) < 1e-8,
        "Repeated/reordered arrangement duration did not preserve each source section tempo.");
    Check(arrangementTiming.TryGetScoreTimeAtSeconds(1.0, out var afterFirstOccurrence) && afterFirstOccurrence == new ScoreTime(1, 1),
        $"Repeated arrangement boundary mapped incorrectly: {afterFirstOccurrence}.");
}

static void GeneralMidiInstrumentCatalog()
{
    Check(GeneralMidiInstruments.Names.Count == 128, "General MIDI catalog must contain 128 programs.");
    Check(GeneralMidiInstruments.Names.Distinct(StringComparer.Ordinal).Count() == 128,
        "General MIDI program names must be unique.");

    var expected = new Dictionary<int, string>
    {
        [0] = "Acoustic Grand Piano",
        [24] = "Acoustic Guitar (nylon)",
        [25] = "Acoustic Guitar (steel)",
        [32] = "Acoustic Bass",
        [40] = "Violin",
        [56] = "Trumpet",
        [73] = "Flute",
        [127] = "Gunshot"
    };

    foreach (var (program, name) in expected)
        Check(GeneralMidiInstruments.GetName(program) == name,
            $"General MIDI program {program} should be '{name}'.");
}

static ScoreNote AddHit(Voice voice, int beat, int pitch = 36)
{
    var note = new ScoreNote(pitch, 90);
    voice.Beats[beat].AddNote(note);
    return note;
}

static (ScoreDocument Score, ScoreTrack Track, ScoreSection Section, ScoreMeasure First, ScoreMeasure Second) MakeTabScore()
{
    var score = new ScoreDocument("Tab playback", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    score.AddTrack(new ScoreTrack("Lead", TrackKind.Keyboard));
    var tabTrack = score.AddTrack(new ScoreTrack("Tab", TrackKind.Guitar));
    var section = score.Sections[0];
    var first = score.AddMeasure(section);
    var second = score.AddMeasure(section);
    return (score, tabTrack, section, first, second);
}

static TabEvent AddTab(ScoreMeasure measure, ScoreTrack track, int stringNumber, int pitch, ScoreTime onset, ScoreTime duration)
{
    var trackMeasure = measure.GetTrackMeasure(track.Id)!;
    trackMeasure.UsesTabEvents = true;
    var note = new TabEvent(stringNumber, 0, pitch, 90, onset, duration);
    trackMeasure.TabEvents.Add(note);
    return note;
}

static void TabSustainCutoff()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    var early = AddTab(first, track, 1, 64, new ScoreTime(3, 4), new ScoreTime(1, 1));
    AddTab(second, track, 1, 65, new ScoreTime(1, 4), new ScoreTime(1, 4));
    // Legacy beats can coexist for migration but are not played after selecting TAB as source.
    first.GetTrackMeasure(track.Id)!.Voices[0].Beats[0].AddNote(new ScoreNote(50, 90));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Any(e => e.Kind == "NoteOn" && e.Value == 64 && e.Frame == 72000), "TAB onset did not use exact measure-relative time.");
    Check(events.Any(e => e.Kind == "NoteOff" && e.Value == 64 && e.Frame == 120000), "Sustain was not cut at the next same-string onset in the next measure.");
    Check(!events.Any(e => e.Kind == "NoteOn" && e.Value == 50), "Legacy beat notes played alongside authoritative TAB events.");
}

static void TabTupletPlayback()
{
    var (score, track, _, first, _) = MakeTabScore();
    track.ConfigureTab(new[] { 40, 45, 50, 55, 59, 64 }, 24);
    var firstNote = AddTab(first, track, 1, 40, new ScoreTime(1, 6), new ScoreTime(1, 12));
    firstNote.TupletActual = 3;
    var secondNote = AddTab(first, track, 1, 40, new ScoreTime(1, 3), new ScoreTime(1, 12));
    secondNote.TupletActual = 3;
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Any(e => e.Kind == "NoteOn" && e.Value == 40 && e.Frame == 16000), "Triplet onset did not use exact rational timing.");
    Check(events.Any(e => e.Kind == "NoteOff" && e.Value == 40 && e.Frame == 24000), "Triplet duration did not produce its exact note-off.");
    Check(events.Any(e => e.Kind == "NoteOn" && e.Value == 40 && e.Frame == 32000), "Second triplet onset did not use exact rational timing.");
}

static void TabExpressionSchedule()
{
    var (score, track, _, measure, _) = MakeTabScore();
    var note = AddTab(measure, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
    note.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch, Points = new() { new(0, 0), new(1, 1) } });
    note.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Tremolo, Points = new() { new(0, 1), new(1, 0.5) } });
    note.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Velocity, Position = 0.25, Points = new() { new(0, 0.5), new(1, 1) } });
    var silent = AddTab(measure, track, 2, 65, new ScoreTime(1, 2), new ScoreTime(1, 4));
    silent.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Velocity, Points = new() { new(0, 0), new(1, 0) } });
    measure.TempoEvents.Add(new TempoEvent(new ScoreTime(1, 8), new TempoMark(60m, new RhythmicValue(4))));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Any(e => e.Kind == "ExpressiveNoteOn" && e.Value == 64 && e.SecondaryValue == 56),
        "Velocity expression was not sampled at its authored attack position.");
    var updates = events.Where(e => e.Kind == "NoteExpressionUpdate").OrderBy(e => e.Frame).ToArray();
    Check(updates[0].Frame == 0 && updates[^1].Frame == 36000 &&
        updates.Any(e => e.Frame == 12000 && e.Value == 500 && e.SecondaryValue == 750),
        "Expression curves were not sampled along the tempo-aware note timeline.");
    Check(updates[^1].Value == 1000 && updates[^1].SecondaryValue == 500,
        "Pitch or tremolo expression values were not sampled at the curve endpoint.");
    Check(!events.Any(e => e.Value == 65 && e.Kind is "NoteOn" or "ExpressiveNoteOn"),
        "A zero velocity multiplier did not silence its note at attack.");
}

static System.Collections.IDictionary ExpressiveVoices(SoundFontPlaybackService service, Guid trackId) =>
    (System.Collections.IDictionary)typeof(SoundFontPlaybackService)
        .GetField("_expressiveVoices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(TrackService(service, trackId))!;

static void StringLegatoVoiceContinuity()
{
    var (score, track, _, measure, _) = MakeTabScore();
    AddTab(measure, track, 1, 60, ScoreTime.Zero, new ScoreTime(1, 4));
    var hammer = AddTab(measure, track, 1, 64, new ScoreTime(1, 4), new ScoreTime(1, 4));
    hammer.IncomingArticulation = StringArticulation.HammerOn;
    var pull = AddTab(measure, track, 1, 62, new ScoreTime(1, 2), new ScoreTime(1, 4));
    pull.IncomingArticulation = StringArticulation.PullOff;
    var service = NewSynth();
    var clock = new ScorePlaybackClock(score, 48000);
    clock.Render(service, new float[12000], new float[12000]);
    var voices = ExpressiveVoices(service, track.Id);
    Check(voices.Count == 1, "Legato source did not start as an isolated voice.");
    var original = voices.Values.Cast<object>().Single();
    clock.RefreshScore(score); // The carried source note-off must not preempt the connection.
    clock.Render(service, new float[12001], new float[12001]);
    Check(voices.Count == 1 && ReferenceEquals(original, voices.Values.Cast<object>().Single()), "Hammer-on retriggered or lost the sounding voice.");
    var offset = (double)original.GetType().GetProperty("LegatoOffset")!.GetValue(original)!;
    Check(offset == 4, "Hammer-on did not reach the destination pitch.");
    clock.Render(service, new float[24000], new float[24000]);
    Check(voices.Count == 1 && ReferenceEquals(original, voices.Values.Cast<object>().Single()), "Pull-off retriggered or lost the sounding voice.");
    offset = (double)original.GetType().GetProperty("LegatoOffset")!.GetValue(original)!;
    Check(offset == 2, "Pull-off did not reach the destination pitch.");
    clock.Render(service, new float[24000], new float[24000]);
    Check(voices.Count == 0, "The legato voice was not released at the destination end.");
}

static void StringArticulationPreviewScore()
{
    var (score, track, _, sourceMeasure, _) = MakeTabScore();
    track.ConfigureTab(new[] { 64, 59, 55, 50, 45, 40 });
    track.VolumeDb = -4.5;
    var source = AddTab(sourceMeasure, track, 2, 59, ScoreTime.Zero, new ScoreTime(1, 2));
    source.SoundVariation = "palm_mute";
    source.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch, MinSemitones = -12, MaxSemitones = 12,
        Points = new() { new ExpressionPoint(0, 0), new ExpressionPoint(1, 2) } });
    var destination = AddTab(sourceMeasure, track, 2, 64, new ScoreTime(1, 2), new ScoreTime(1, 4));
    destination.IncomingArticulation = StringArticulation.HammerOn;
    destination.SoundVariation = "harmonic";
    destination.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Tremolo,
        Points = new() { new ExpressionPoint(0, 1), new ExpressionPoint(1, 0.5) } });

    var originalTrackId = track.Id;
    var originalMeasureCount = score.Sections[0].Measures.Count;
    var originalSourceExpression = source.Expressions.Single();
    var originalDestinationExpression = destination.Expressions.Single();
    var tempo = score.ResolveTempoAt(ScoreTime.Zero);
    var preview = StringArticulationPreview.BuildScore(track, source, destination, new ScoreTime(1, 2), tempo);
    var previewTrack = preview.Tracks.Single();
    var previewNotes = preview.Sections.Single().Measures.Single().GetTrackMeasure(previewTrack.Id)!.TabEvents;
    var previewSource = previewNotes.Single(note => note.Onset == ScoreTime.Zero);
    var previewDestination = previewNotes.Single(note => note.IncomingArticulation == StringArticulation.HammerOn);
    var events = PendingEvents(new ScorePlaybackClock(preview, 48000));

    Check(preview.Sections.Single().Measures.Single().IsFreeTime, "Preview score did not use a free-time measure.");
    Check(events.Any(item => item.Kind == "LegatoNoteOn" && item.Value == destination.MidiPitch && item.Frame == 48000),
        "The isolated preview clock did not schedule the connected destination at its authored onset.");
    Check(previewTrack.Id != originalTrackId && previewTrack.VolumeDb == track.VolumeDb && previewTrack.TabTuningMidiPitches.SequenceEqual(track.TabTuningMidiPitches),
        "Preview track did not retain the source instrument settings in an isolated identity.");
    Check(preview.ResolveTempoAt(ScoreTime.Zero).WholeNotesPerMinute == tempo.WholeNotesPerMinute,
        "Preview score did not retain the resolved tempo.");
    Check(previewSource.SoundVariation == source.SoundVariation && previewDestination.SoundVariation == destination.SoundVariation,
        "Preview notes did not retain their authored sound variations.");
    Check(!ReferenceEquals(previewSource.Expressions.Single(), originalSourceExpression) &&
        previewSource.Expressions.Single().Points.SequenceEqual(originalSourceExpression.Points) &&
        !ReferenceEquals(previewDestination.Expressions.Single(), originalDestinationExpression) &&
        previewDestination.Expressions.Single().Points.SequenceEqual(originalDestinationExpression.Points),
        "Preview note expressions were not cloned with their authored points.");
    Check(score.Tracks.Contains(track) && track.Id == originalTrackId && score.Sections[0].Measures.Count == originalMeasureCount &&
        sourceMeasure.GetTrackMeasure(track.Id)!.TabEvents.Count == 2 && source.IncomingArticulation == StringArticulation.None &&
        destination.IncomingArticulation == StringArticulation.HammerOn,
        "Building a preview mutated the authored score, track, or notes.");

    var selectedPairTimeline = new[]
    {
        new StringArticulationPreview.NoteOccurrence(source, ScoreTime.Zero, new ScoreTime(1, 2)),
        new StringArticulationPreview.NoteOccurrence(destination, new ScoreTime(1, 2), new ScoreTime(3, 4))
    };
    Check(StringArticulationPreview.TryGetSelectedPairDestination(selectedPairTimeline, destination, source,
        StringArticulation.HammerOn, out var selectedDestination) && ReferenceEquals(selectedDestination, destination),
        "A consecutive selected pair did not resolve to its later destination note.");
    var intervening = new TabEvent(2, 1, 61, 90, new ScoreTime(1, 4), new ScoreTime(1, 8));
    var nonConsecutiveTimeline = selectedPairTimeline.Append(new StringArticulationPreview.NoteOccurrence(
        intervening, new ScoreTime(1, 4), new ScoreTime(3, 8))).ToArray();
    Check(!StringArticulationPreview.TryGetSelectedPairDestination(nonConsecutiveTimeline, source, destination,
        StringArticulation.HammerOn, out _), "A non-consecutive selected pair was accepted as one connected gesture.");
}

static void StringLegatoSectionJump()
{
    var (score, track, sourceSection, sourceMeasure, _) = MakeTabScore();
    track.ConfigureTab(new[] { 64, 59, 55, 50, 45, 40 });
    var skippedSection = score.AddSection("Skipped", "Skipped");
    var destinationSection = score.AddSection("Destination", "Destination");
    var source = AddTab(sourceMeasure, track, 1, 60, ScoreTime.Zero, new ScoreTime(4, 1));
    var destinationMeasure = destinationSection.Measures[0];
    var destination = AddTab(destinationMeasure, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
    destination.IncomingArticulation = StringArticulation.HammerOn;
    var clock = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(
        PlaybackLoopMode.Sections, null, new[] { sourceSection.Id, destinationSection.Id }));
    var synth = NewSynth();
    clock.Render(synth, new float[1], new float[1]);
    var voices = ExpressiveVoices(synth, track.Id);
    Check(voices.Count == 1, "Scoped playback did not start the source sustain before the section jump.");
    var sourceVoice = voices.Values.Cast<object>().Single();

    // The first section contains two measures, so the selected scope resumes in the destination section at frame 192000.
    clock.Render(synth, new float[191999], new float[191999]);
    clock.Render(synth, new float[1], new float[1]);
    var destinationVoice = voices.Values.Cast<object>().Single();
    Check(voices.Count == 1 && !ReferenceEquals(sourceVoice, destinationVoice),
        "The incoming articulation carried the source voice across a scoped section discontinuity.");
    Check((double)destinationVoice.GetType().GetProperty("LegatoOffset")!.GetValue(destinationVoice)! == 0,
        "The destination did not retrigger after the skipped-section jump released its source.");
    Check(skippedSection.Measures.Count == 1, "The intervening section fixture was not constructed.");
}

static void StringLegatoFallback()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    AddTab(first, track, 1, 64, new ScoreTime(3, 4), new ScoreTime(1, 4));
    var destination = AddTab(second, track, 1, 62, ScoreTime.Zero, new ScoreTime(1, 4));
    destination.IncomingArticulation = StringArticulation.HammerOn; // wrong direction
    var service = NewSynth();
    var clock = new ScorePlaybackClock(score, 48000);
    clock.Render(service, new float[80000], new float[80000]);
    var voices = ExpressiveVoices(service, track.Id);
    var original = voices.Values.Cast<object>().Single();
    clock.Render(service, new float[16001], new float[16001]);
    Check(voices.Count == 1 && !ReferenceEquals(original, voices.Values.Cast<object>().Single()), "Wrong-direction connection did not retrigger safely.");
    var seekService = NewSynth();
    var seek = new ScorePlaybackClock(score, 48000, second.Id);
    seek.Render(seekService, new float[1], new float[1]);
    Check(ExpressiveVoices(seekService, track.Id).Count == 1, "Starting at a legato destination failed to attack.");
}

static void StringLegatoCountInAndVersions()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    AddTab(first, track, 1, 60, new ScoreTime(3, 4), new ScoreTime(1, 4));
    var original = AddTab(second, track, 1, 62, ScoreTime.Zero, new ScoreTime(1, 4));
    original.IncomingArticulation = StringArticulation.HammerOn;
    var variant = new TrackMeasure(track.Id) { UsesTabEvents = true };
    variant.TabEvents.Add(new TabEvent(1, 0, 64, 90, ScoreTime.Zero, new ScoreTime(1, 4)) { IncomingArticulation = StringArticulation.HammerOn });
    var version = score.AddTrackMeasureVersion(section, second, track, "Legato", variant);
    score.SetTrackMeasureAlternation(section, second, track, new[] { version.Id, Guid.Empty });
    var synth = NewSynth();
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(second.Id, track.Id)] = version.Id });
    var clock = new ScorePlaybackClock(score, 48000, countInEnabled: true, versionSelection: snapshot);
    clock.Render(synth, new float[168000], new float[168000]); // one-bar count-in, then first note
    var voices = ExpressiveVoices(synth, track.Id);
    Check(voices.Count == 1, "Count-in or version source failed to sound.");
    var firstVoice = voices.Values.Cast<object>().Single();
    clock.Render(synth, new float[24001], new float[24001]);
    Check(voices.Count == 1 && ReferenceEquals(firstVoice, voices.Values.Cast<object>().Single()), "Selected legato version retriggered after count-in.");
    Check((double)firstVoice.GetType().GetProperty("LegatoOffset")!.GetValue(firstVoice)! == 4, "Wrong version destination pitch was used.");
}

static void ExpressionStepPlayback()
{
    var (score, track, _, measure, _) = MakeTabScore();
    var note = AddTab(measure, track, 1, 60, ScoreTime.Zero, new ScoreTime(1, 4));
    note.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch,
        Points = new() { new(0, 0, ExpressionSegmentMode.Step), new(.5, 2), new(1, 2) } });
    var updates = PendingEvents(new ScorePlaybackClock(score, 48000)).Where(e => e.Kind == "NoteExpressionUpdate").ToArray();
    Check(updates.Any(e => e.Frame == 12000 && e.Value == 2000), "Step did not switch at its authored boundary.");
    Check(updates.Where(e => e.Frame < 12000).All(e => e.Value == 0), "Step changed before its authored boundary.");
}

static void NoteExpressionRangePersistence()
{
    var (score, track, _, measure, _) = MakeTabScore();
    track.ConfigureTab(new[] { 64, 59, 55, 50, 45, 40 });
    var note = AddTab(measure, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
    var expression = new NoteExpression { Kind = ExpressionKind.Pitch, MinSemitones = -12, MaxSemitones = 12,
        Points = new() { new(0, 0), new(1, 8) } };
    expression.Validate();
    var clone = expression.Clone();
    Check(clone.MinSemitones == -12 && clone.MaxSemitones == 12 && clone.Points[1].Y == 8,
        "Cloning an expression lost its pitch limits or absolute pitch values.");
    note.Expressions.Add(expression);
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restoredExpression = restored.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.TabEvents[0].Expressions[0];
    Check(restoredExpression.MinSemitones == -12 && restoredExpression.MaxSemitones == 12 && restoredExpression.Points[1].Y == 8,
        "Score persistence lost the configured limits or absolute pitch values.");
    Check(new NoteExpression { Kind = ExpressionKind.Pitch, Points = new() { new(0, 0), new(1, 2) } }.MaxSemitones == 2,
        "Legacy pitch curves no longer default to a two-semitone range.");
    new NoteExpression { Kind = ExpressionKind.Pitch, MinSemitones = 0, MaxSemitones = 12,
        Points = new() { new(0, 0), new(1, 12) } }.Validate();
}

static void TabExpressionVoiceIsolation()
{
    var (score, track, _, measure, _) = MakeTabScore();
    var shortNote = AddTab(measure, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
    shortNote.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch, Points = new() { new(0, 0), new(1, 1) } });
    AddHit(measure.GetTrackMeasure(score.Tracks[0].Id)!.Voices[0], 0, 64);
    var longNote = AddTab(measure, track, 2, 64, ScoreTime.Zero, new ScoreTime(1, 2));
    longNote.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Vibrato, Points = new() { new(0, 0), new(1, -1) } });
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    var clock = new ScorePlaybackClock(score, 48000);
    var left = new float[24000]; var right = new float[24000];
    clock.Render(service, left, right);
    var nested = TrackService(service, track.Id);
    var active = (System.Collections.IDictionary)typeof(SoundFontPlaybackService)
        .GetField("_expressiveVoices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nested)!;
    Check(active.Count == 1, "A note-off silenced or retained the wrong independently expressive same-pitch voice.");
    var expressiveVoice = active.Values.Cast<object>().Single();
    var voiceType = expressiveVoice.GetType();
    var voiceSynth = (MeltySynth.Synthesizer)voiceType.GetProperty("Synth")!.GetValue(expressiveVoice)!;
    var voiceChannel = (int)voiceType.GetProperty("Channel")!.GetValue(expressiveVoice)!;
    var channels = (Array)typeof(MeltySynth.Synthesizer).GetField("channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(voiceSynth)!;
    var channel = channels.GetValue(voiceChannel)!;
    var bendRange = (short)channel.GetType().GetField("pitchBendRange", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(channel)!;
    Check(bendRange == 24 * 128, $"MeltySynth did not honor expressive pitch bend sensitivity RPN 0,0 (range was {bendRange}/128 semitones).");
    Check(left.Any(sample => Math.Abs(sample) > 0.000001f), "Expressive voices rendered silence.");
}

static void SamePitchOrdinaryAndExpressiveRelease()
{
    static (ScoreDocument Score, ScoreTrack Track, ScoreMeasure Measure) Make()
    {
        var (score, track, _, measure, _) = MakeTabScore();
        track.DefaultMidiProgram = 16; // Sustained organ makes a missed ordinary note-off observable.
        var expression = AddTab(measure, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
        expression.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch, Points = new() { new(0, 0), new(1, 1) } });
        AddTab(measure, track, 2, 64, ScoreTime.Zero, new ScoreTime(1, 2));
        return (score, track, measure);
    }

    static int OrdinaryVoiceCount(SoundFontPlaybackService service, Guid trackId)
    {
        var nested = TrackService(service, trackId);
        var synth = (MeltySynth.Synthesizer)typeof(SoundFontPlaybackService)
            .GetField("_synthesizer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nested)!;
        return synth.ActiveVoiceCount;
    }

    // Expressive note ends first; ordinary same-pitch note must remain active.
    var (scoreA, trackA, _) = Make();
    var longOrdinary = scoreA.Sections[0].Measures[0].GetTrackMeasure(trackA.Id)!.TabEvents[1];
    longOrdinary.Onset = ScoreTime.Zero;
    var serviceA = NewSynth(); var clockA = new ScorePlaybackClock(scoreA, 48000);
    var leftA = new float[24000]; var rightA = new float[24000];
    clockA.Render(serviceA, leftA, rightA);
    Check(OrdinaryVoiceCount(serviceA, trackA.Id) > 0, "The ordinary same-pitch note stopped with the expressive note.");

    // Ordinary note ends first; its channel voice must release while the expressive voice continues.
    var (scoreB, trackB, measureB) = Make();
    var eventsB = measureB.GetTrackMeasure(trackB.Id)!.TabEvents;
    var shortOrdinary = eventsB[1];
    var longExpressive = eventsB[0];
    shortOrdinary.Duration = new ScoreTime(1, 4);
    longExpressive.Duration = new ScoreTime(1, 2);
    var serviceB = NewSynth(); var clockB = new ScorePlaybackClock(scoreB, 48000);
    var leftB = new float[24000]; var rightB = new float[24000];
    clockB.Render(serviceB, leftB, rightB);
    Check(OrdinaryVoiceCount(serviceB, trackB.Id) > 0, "The ordinary note had no active release tail after its note-off.");
    var nextLeft = new float[12000]; var nextRight = new float[12000];
    clockB.Render(serviceB, nextLeft, nextRight);
    Check(OrdinaryVoiceCount(serviceB, trackB.Id) == 0, "The ordinary same-pitch voice remained stuck after its note-off.");
    var expressive = (System.Collections.IDictionary)typeof(SoundFontPlaybackService)
        .GetField("_expressiveVoices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(TrackService(serviceB, trackB.Id))!;
    Check(expressive.Count == 1, "The expressive same-pitch note did not remain active through the ordinary note-off.");
}

static void TabExpressionRefresh()
{
    var (score, track, _, measure, _) = MakeTabScore();
    var note = AddTab(measure, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 2));
    var pitch = new NoteExpression { Kind = ExpressionKind.Pitch, Points = new() { new(0, 0), new(1, 0.5) } };
    note.Expressions.Add(pitch);
    var clock = new ScorePlaybackClock(score, 48000);
    var service = NewSynth();
    var left = new float[6000]; var right = new float[6000];
    clock.Render(service, left, right);
    pitch.Points = new() { new(0, 0), new(1, -0.5) };
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteExpressionUpdate" && e.Value == -500 && e.Frame > 6000),
        "Refreshing playback did not replace the upcoming expression curve.");
}

static void ExpressivePercussionVoiceIsolation()
{
    var baseline = NewSynth(); var adjusted = NewSynth();
    var track = Guid.NewGuid();
    baseline.SetTrackVolume(track, 0); adjusted.SetTrackVolume(track, 0);
    baseline.SetDrumPieceGain(track, 36, 0); adjusted.SetDrumPieceGain(track, 36, -6);
    var first = Guid.NewGuid(); var second = Guid.NewGuid();
    baseline.NoteOnExpressive(track, 9, 36, 90, first);
    adjusted.NoteOnExpressive(track, 9, 36, 90, first);
    baseline.NoteOnExpressive(track, 9, 36, 90, second);
    adjusted.NoteOnExpressive(track, 9, 36, 90, second);
    baseline.SetNoteExpression(track, first, 0.5, 1);
    adjusted.SetNoteExpression(track, first, 0.5, 1);
    baseline.SetNoteExpression(track, second, -0.5, 0.5);
    adjusted.SetNoteExpression(track, second, -0.5, 0.5);
    baseline.NoteOff(track, 9, 36, first);
    adjusted.NoteOff(track, 9, 36, first);
    var live = (System.Collections.IDictionary)typeof(SoundFontPlaybackService)
        .GetField("_expressiveVoices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(TrackService(adjusted, track))!;
    Check(live.Count == 1 && live.Contains(second), "Ending one expressive drum hit disturbed the overlapping hit.");
    var left = new float[2048]; var right = new float[2048];
    var otherLeft = new float[2048]; var otherRight = new float[2048];
    baseline.Render(left, right); adjusted.Render(otherLeft, otherRight);
    AssertGain(left, otherLeft, Math.Pow(10, -6.0 / 20), "expressive percussion piece gain");
}

static void ExpressionStopAndLoop()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetLeadTrack(section, track);
    var note = AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 2));
    note.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Pitch, Points = new() { new(0, 0), new(1, 1) } });
    var seekNote = AddTab(second, track, 1, 65, ScoreTime.Zero, new ScoreTime(1, 4));
    seekNote.Expressions.Add(new NoteExpression { Kind = ExpressionKind.Tremolo, Points = new() { new(0, 1), new(1, 0.5) } });
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Measure, first.Id, Array.Empty<Guid>());
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    var seekClock = new ScorePlaybackClock(score, 48000, score.Sections[0].Measures[1].Id);
    var seekEvents = PendingEvents(seekClock);
    Check(seekEvents.Any(e => e.Kind == "ExpressiveNoteOn" && e.Value == 65 && e.Frame == 96000),
        "Starting at a later measure lost its expression or onset position.");
    var service = NewSynth();
    var left = new float[96001]; var right = new float[96001];
    clock.Render(service, left, right);
    Check(left.Any(sample => Math.Abs(sample) > 0.000001f), "The expressive loop produced silence.");
    var nested = TrackService(service, track.Id);
    var active = (System.Collections.IDictionary)typeof(SoundFontPlaybackService)
        .GetField("_expressiveVoices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nested)!;
    Check(active.Count == 1, "Loop wrapping failed to start the repeated expressive occurrence.");
    clock.Stop(service);
    Check(active.Count == 0, "Stopping did not clear expressive voices.");
}

static SoundFontPlaybackService TrackService(SoundFontPlaybackService service, Guid trackId)
{
    var outputs = (System.Collections.IDictionary)typeof(SoundFontPlaybackService)
        .GetField("_trackOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    return (SoundFontPlaybackService)outputs[trackId]!.GetType().GetProperty("Service")!.GetValue(outputs[trackId])!;
}

static void TabOverflowOnsetCutoff()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    AddTab(first, track, 1, 64, new ScoreTime(5, 4), new ScoreTime(1, 1)).AllowsBarOverflow = true;
    AddTab(second, track, 1, 65, new ScoreTime(1, 2), new ScoreTime(1, 4));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Any(e => e.Kind == "NoteOn" && e.Value == 64 && e.Frame == 120000),
        "An event whose onset exceeded its owner measure was not scheduled at its measure-relative position: " + string.Join("; ", events.Where(e => e.Value is 64 or 65)));
    Check(events.Any(e => e.Kind == "NoteOff" && e.Value == 64 && e.Frame == 144000),
        "The overflow event was not cut off at the later same-string onset in the next measure.");
}

static void TabMixedMeterOverflow()
{
    var (score, track, section, first, second) = MakeTabScore();
    second.TimeSignatureOverride = new TimeSignature(2, 4);
    score.AddMeasure(section);
    score.SetTrackPatternLength(section, track, 1);
    score.SetTrackMeasureRepeatCount(section, track, 0, 1);
    var note = AddTab(first, track, 1, 64, new ScoreTime(3, 4), new ScoreTime(1, 8));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Count(e => e.Kind == "NoteOn" && e.Value == 64) == 1,
        "An ordinary onset past a shorter destination measure was not clipped.");
    note.AllowsBarOverflow = true;
    events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Count(e => e.Kind == "NoteOn" && e.Value == 64) == 2,
        "An explicitly pasted overflow onset was not retained across a shorter repeated bar.");
}

static void TabRepeatOnsets()
{
    var (score, track, section, first, _) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 1);
    score.SetTrackMeasureRepeatCount(section, track, 0, 1);
    AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 2));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    var onsets = events.Where(e => e.Kind == "NoteOn" && e.Value == 64).Select(e => e.Frame).ToArray();
    Check(onsets.SequenceEqual(new long[] { 0, 96000 }), "A repeated pattern event did not attack at each destination measure onset.");
    Check(events.Any(e => e.Kind == "NoteOff" && e.Value == 64 && e.Frame == 48000), "First repeated occurrence lost its independent authored duration.");
}

static void ExplicitMelodicRepeats()
{
    var (score, _, _, section) = MakeThreeMeasureSection();
    var melody = score.AddTrack(new ScoreTrack("Second Guitar", TrackKind.Guitar));
    var sourceMeasure = section.Measures[0].GetTrackMeasure(melody.Id)!;
    var sourceNote = AddHit(sourceMeasure.Voices[0], 0, 67);
    score.SetTrackPatternLength(section, melody, 1);

    var noRepeats = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(noRepeats.Where(e => e.Kind == "NoteOn" && e.Value == 67).Select(e => e.Frame).SequenceEqual(new[] { 0L }),
        "A non-lead melodic part repeated automatically without authored repeat counts.");

    score.SetTrackMeasureRepeatCount(section, melody, 0, 1);
    var clock = new ScorePlaybackClock(score, 48000);
    var repeats = PendingEvents(clock);
    Check(repeats.Where(e => e.Kind == "NoteOn" && e.Value == 67).Select(e => e.Frame).SequenceEqual(new[] { 0L, 96000 }),
        "An explicit additional repeat did not play once or left the remaining lead bar filled.");
    var scheduled = (Array)typeof(ScorePlaybackClock).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var repeatIds = scheduled.Cast<object>()
        .Where(e => e.GetType().GetProperty("Kind")!.GetValue(e)!.ToString() == "NoteOn")
        .Where(e => (int)e.GetType().GetProperty("Value")!.GetValue(e)! == 67)
        .Select(e => (Guid)e.GetType().GetProperty("NoteId")!.GetValue(e)!).ToArray();
    Check(repeatIds.Length == 2 && repeatIds[0] == sourceNote.Id && repeatIds[1] != sourceNote.Id && repeatIds[0] != repeatIds[1],
        "An explicit linked occurrence did not receive an independent event identity.");

    var drum = section.Measures[0].GetTrackMeasure(score.Tracks[1].Id)!.Voices[0];
    AddHit(drum, 0, 36);
    var withDrums = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(withDrums.Where(e => e.Kind == "NoteOn" && e.Value == 36).Select(e => e.Frame).SequenceEqual(new[] { 0L, 96000, 192000 }),
        "The explicit melodic repeat change altered automatic percussion repetition.");
}

static void TabSectionCutoff()
{
    var (score, track, section, first, _) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(4, 1));
    var nextSection = score.AddSection("B", "B");
    AddTab(nextSection.Measures[0], track, 1, 65, ScoreTime.Zero, new ScoreTime(1, 4));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Any(e => e.Kind == "NoteOff" && e.Value == 64 && e.Frame == 192000),
        "A later-section same-string onset did not cut the earlier cross-section sustain.");
}

static void TabOverflowSectionCutoff()
{
    var (score, track, section, _, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    AddTab(second, track, 1, 64, new ScoreTime(5, 4), new ScoreTime(2, 1)).AllowsBarOverflow = true;
    var nextSection = score.AddSection("B", "B");
    AddTab(nextSection.Measures[0], track, 1, 65, new ScoreTime(1, 2), new ScoreTime(1, 4));
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    Check(events.Any(e => e.Kind == "NoteOn" && e.Value == 64 && e.Frame == 216000),
        "An overflow onset owned by the final measure of a section was not scheduled: " + string.Join("; ", events.Where(e => e.Value is 64 or 65)));
    Check(events.Any(e => e.Kind == "NoteOff" && e.Value == 64 && e.Frame == 240000),
        "A later-section same-string onset did not cut off the overflow sustain.");
}

static void TabSamePitchStrings()
{
    var (score, track, _, first, _) = MakeTabScore();
    var firstString = AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
    var secondString = AddTab(first, track, 2, 64, ScoreTime.Zero, new ScoreTime(1, 2));
    var clock = new ScorePlaybackClock(score, 48000);
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    var left = new float[24000]; var right = new float[24000];
    clock.Render(service, left, right);
    var active = (System.Collections.IDictionary)typeof(ScorePlaybackClock).GetField("_activeNoteEvents", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(clock)!;
    Check(!active.Contains(firstString.Id) && active.Contains(secondString.Id),
        "Ending the first same-pitch string did not leave the other string's note active.");
}

static void TabIntensityPlayback()
{
    var (score, track, _, first, _) = MakeTabScore();
    var note = AddTab(first, track, 1, 64, new ScoreTime(1, 2), new ScoreTime(1, 4));
    note.Velocity = 81;
    var clock = new ScorePlaybackClock(score, 48000);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 64 && e.SecondaryValue == 81),
        "Legacy tab velocity changed during playback scheduling.");
    note.Intensity = 2;
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 64 && e.SecondaryValue == Beat.MidiVelocityForIntensity(2)),
        "Edited tab intensity was not scheduled as MIDI velocity.");
}

static (ScorePlaybackClock Clock, SoundFontPlaybackService Service) StartAt(ScoreDocument score, long frames)
{
    var clock = new ScorePlaybackClock(score, 48000);
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    var left = new float[frames];
    var right = new float[frames];
    clock.Render(service, left, right);
    return (clock, service);
}

static void FutureEdits()
{
    var (score, _, _, _, voice) = MakeScore();
    var (clock, _) = StartAt(score, 30000);
    var hit = AddHit(voice, 2, 38);
    voice.Beats[2].Intensity = 5;
    clock.RefreshScore(score);
    var pending = PendingEvents(clock);
    Check(pending.Any(e => e.Kind == "NoteOn" && e.Value == 38 && e.SecondaryValue == 127 && e.Frame == 48000),
        "Expected edited hit at 1 second with intensity 5 velocity.");
}

static void TempoEditRefresh()
{
    var (score, _, _, _, voice) = MakeScore();
    AddHit(voice, 2, 38);
    var (clock, _) = StartAt(score, 12000);
    score.DefaultTempo = TempoMark.FromQuarterNotesPerMinute(60m, score.DefaultTempo.BeatUnit);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 38 && e.Frame == 96000),
        "The upcoming hit was not rescheduled for the edited tempo.");
}

static void MetronomeTiming()
{
    var score = new ScoreDocument("Metronome", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var first = score.Sections[0];
    score.AddMeasure(first);
    score.AddMeasure(first);
    var defaultClock = new ScorePlaybackClock(score, 48000);
    Check(!defaultClock.MetronomeEnabled, "Metronome clicks should be disabled by default.");
    var clock = new ScorePlaybackClock(score, 48000, metronomeEnabled: true);
    var clicks = PendingEvents(clock).Where(e => e.Kind == "MetronomeOn").ToArray();
    Check(clicks.Length == 8, "A two-bar 4/4 score should schedule eight metronome clicks.");
    Check(clicks.Select(e => e.Frame).SequenceEqual(new long[] { 0, 24000, 48000, 72000, 96000, 120000, 144000, 168000 }),
        "Metronome click frames did not follow quarter-note tempo.");
    var arrangementClock = new ScorePlaybackClock(score, 48000, selection:
        new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, [first.Id]), metronomeEnabled: true);
    Check(PendingEvents(arrangementClock).Count(e => e.Kind == "MetronomeOn") == 8,
        "Arrangement playback did not retain metronome clicks.");
    score.MetronomeEnabled = true;
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Check(restored.MetronomeEnabled, "The metronome preference did not round-trip in the project file.");
}

static void CountInTiming()
{
    var score = new ScoreDocument("Count in", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var section = score.Sections[0];
    score.AddMeasure(section);
    var second = score.AddMeasure(section);
    var clock = new ScorePlaybackClock(score, 48000, second.Id, countInEnabled: true);
    var events = PendingEvents(clock);
    var clicks = events.Where(item => item.Kind == "CountInOn").ToArray();
    Check(clicks.Length == 4, $"Expected one 4/4 count-in, got {clicks.Length} clicks.");
    Check(clicks.Select(item => item.Frame).SequenceEqual(new long[] { 96000, 120000, 144000, 168000 }),
        $"Count-in clicks did not use the starting measure's tempo and position: {string.Join(',', clicks.Select(item => item.Frame))}.");
    Check(events.Any(item => item.Kind == "MetronomeOn" && item.Frame == 192000),
        "Score playback did not begin immediately after the count-in measure.");
    Check(clock.IsCountInBehind(0), "The clock did not report the initial count-in as audible.");
    Check(clock.GetTimelineFrameBehind(0) == 96000,
        "The playback cursor moved during count-in instead of staying at the starting measure.");
    var (defaultClock, _) = StartAt(score, 1);
    Check(!defaultClock.IsCountInBehind(0), "Count-in was enabled by default.");
}

static void CountInLoopTiming()
{
    var score = new ScoreDocument("Count-in loop", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var section = score.Sections[0];
    score.AddMeasure(section);
    var selected = score.AddMeasure(section);
    selected.TimeSignatureOverride = new TimeSignature(3, 4);
    var clock = new ScorePlaybackClock(score, 48000, selected.Id, countInEnabled: true, countInEveryLoop: false)
    { LoopEnabled = true };
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    var initialLeft = new float[24000];
    var initialRight = new float[initialLeft.Length];
    clock.Render(service, initialLeft, initialRight);
    clock.CountInEveryLoop = true;
    var left = new float[126000];
    var right = new float[left.Length];
    clock.Render(service, left, right);
    Check(clock.IsCountInBehind(0), "The repeated count-in did not start after the first score pass.");
    Check(clock.IsCountInBehind(6000), "Queued audio from the repeated pre-roll was not identified as count-in.");
    Check(clock.GetTimelineFrameBehind(0) == 0,
        "A repeated count-in did not pin the cursor to the loop's first measure.");
    var repeatedClicks = PendingEvents(clock).Where(item => item.Kind == "CountInOn").ToArray();
    Check(repeatedClicks.Length == 3 && repeatedClicks.Select(item => item.Frame).SequenceEqual(
            new long[] { 264000, 288000, 312000 }),
        $"The repeated count-in did not use the first measure's 4/4 meter: {string.Join(',', repeatedClicks.Select(item => item.Frame))}.");

    var refreshClock = new ScorePlaybackClock(score, 48000, selected.Id, countInEnabled: true);
    var refreshService = new SoundFontPlaybackService(File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2"), 48000);
    var elapsedLeft = new float[24000];
    var elapsedRight = new float[elapsedLeft.Length];
    refreshClock.Render(refreshService, elapsedLeft, elapsedRight);
    refreshClock.RefreshScore(score);
    Check(refreshClock.IsCountInBehind(0) && refreshClock.GetTimelineFrameBehind(0) == 96000,
        "Refreshing the score during count-in changed its phase or cursor position.");

    var onceClock = new ScorePlaybackClock(score, 48000, selected.Id, countInEnabled: true) { LoopEnabled = true };
    var onceService = new SoundFontPlaybackService(File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2"), 48000);
    onceClock.Render(onceService, new float[168000], new float[168000]);
    Check(!onceClock.IsCountInBehind(0) && onceClock.GetTimelineFrameBehind(0) == 24000,
        "A loop without repeated count-in must advance immediately from the beginning of the score.");
    score.CountInEnabled = true;
    score.CountInEveryLoop = true;
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Check(restored.CountInEnabled && restored.CountInEveryLoop, "Count-in preferences did not round-trip.");
}

static void CountInRefreshPhase()
{
    var score = new ScoreDocument("Count-in refresh", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var section = score.Sections[0];
    score.AddMeasure(section);
    var selected = score.AddMeasure(section);
    selected.TimeSignatureOverride = new TimeSignature(3, 4);
    using var initialStream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");

    var initialClock = new ScorePlaybackClock(score, 48000, selected.Id, countInEnabled: true);
    var initialService = new SoundFontPlaybackService(initialStream, 48000);
    var duringCountInLeft = new float[24000];
    initialClock.Render(initialService, duringCountInLeft, new float[duringCountInLeft.Length]);
    initialClock.RefreshScore(score);
    Check(initialClock.IsCountInBehind(0) && initialClock.GetTimelineFrameBehind(0) == 96000,
        "Refresh during initial count-in changed its phase or cursor.");

    var afterCountInClock = new ScorePlaybackClock(score, 48000, selected.Id, countInEnabled: true);
    var afterCountInService = new SoundFontPlaybackService(File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2"), 48000);
    var afterCountInLeft = new float[84000];
    afterCountInClock.Render(afterCountInService, afterCountInLeft, new float[afterCountInLeft.Length]);
    Check(afterCountInClock.IsCountInBehind(36000),
        "Queued audio from the initial pre-roll was not identified as count-in.");
    afterCountInClock.RefreshScore(score);
    Check(!afterCountInClock.IsCountInBehind(0) &&
          afterCountInClock.Position is { } position && position.MeasureId == selected.Id && position.Fraction > 0.1,
        "Refresh after the initial pre-roll restarted or rewound the score position.");

    var repeatClock = new ScorePlaybackClock(score, 48000, selected.Id, countInEnabled: true,
        countInEveryLoop: false) { LoopEnabled = true };
    var repeatService = new SoundFontPlaybackService(File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2"), 48000);
    var repeatedLeft = new float[150000];
    repeatClock.Render(repeatService, repeatedLeft, new float[repeatedLeft.Length]);
    Check(!repeatClock.IsCountInBehind(0), "A disabled repeated count-in was audible on the later loop.");
    var cursorBefore = repeatClock.GetTimelineFrameBehind(0);
    repeatClock.RefreshScore(score);
    Check(!repeatClock.IsCountInBehind(0) && repeatClock.GetTimelineFrameBehind(0) == cursorBefore &&
          PendingEvents(repeatClock).All(item => item.Kind is not ("CountInOn" or "CountInOff")),
        "Refresh on a later no-count-in loop restored the initial pre-roll schedule or moved its cursor.");
}

static void PastEditsLoop()
{
    var (score, _, _, _, voice) = MakeScore();
    var note = AddHit(voice, 0, 36);
    var (clock, service) = StartAt(score, 10000);
    clock.LoopEnabled = true;
    note.MidiPitch = 40;
    clock.RefreshScore(score);
    var events = PendingEvents(clock);
    Check(!events.Any(e => e.Kind == "NoteOn" && e.Value == 40 && e.Frame <= 10000), "Past hit was replayed immediately.");
    var left = new float[100000];
    var right = new float[left.Length];
    clock.Render(service, left, right);
    var active = (HashSet<Guid>)typeof(ScorePlaybackClock).GetField("_activeNotes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    Check(active.Contains(note.Id), "Edited hit did not sound on the next loop while playback remained active.");
}

static void MeasureLoopScopeAndCursor()
{
    var (score, lead, _, section) = MakeThreeMeasureSection();
    AddHit(section.Measures[0].GetTrackMeasure(lead.Id)!.Voices[0], 0, 36);
    AddHit(section.Measures[1].GetTrackMeasure(lead.Id)!.Voices[0], 1, 38);
    AddHit(section.Measures[2].GetTrackMeasure(lead.Id)!.Voices[0], 2, 40);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Measure, section.Measures[1].Id, Array.Empty<Guid>());
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    var events = PendingEvents(clock);
    Check(events.Where(e => e.Kind == "NoteOn").Select(e => (e.Frame, e.Value))
            .SequenceEqual(new[] { (24000L, 38) }),
        "Measure loop included events outside its selected measure or retained score-wide event frames: " +
        string.Join(", ", events.Where(e => e.Kind == "NoteOn").Select(e => $"{e.Frame}:{e.Value}")));

    var service = NewSynth();
    clock.Render(service, new float[120000], new float[120000]);
    Check(clock.Position is { MeasureId: var id } position && id == section.Measures[1].Id
        && Math.Abs(position.Fraction - 0.25) < 0.00001,
        "Measure loop cursor did not wrap within the source measure and preserve its musical fraction.");
    Check(clock.IsPlaying, "Measure loop stopped at the selected measure boundary.");
}

static void ArrangementPlayback()
{
    var (score, track, section, _, voice) = MakeScore();
    var second = score.AddSection("B", "B");
    AddHit(voice, 0, 36);
    AddHit(second.Measures[0].GetTrackMeasure(track.Id)!.Voices[0], 0, 38);
    var sequence = new[] { second.Id, section.Id, section.Id };
    var once = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, sequence));
    var hits = PendingEvents(once).Where(e => e.Kind == "NoteOn").Select(e => (e.Frame, e.Value)).ToArray();
    Check(hits.SequenceEqual(new[] { (0L, 38), (96000L, 36), (192000L, 36) }),
        "Arrangement failed to retain ordering or repeat sections: " + string.Join(", ", hits));
    var synth = NewSynth();
    once.Render(synth, new float[288000], new float[288000]);
    Check(!once.IsPlaying, "Non-looping arrangement did not stop at its end.");
    var loop = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementLoop, null, sequence));
    loop.Render(NewSynth(), new float[288001], new float[288001]);
    Check(loop.IsPlaying && loop.Position is { MeasureId: var id } && id == second.Measures[0].Id,
        "Looping arrangement did not return to its first section.");
    var empty = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementLoop, null, Array.Empty<Guid>()));
    Check(!empty.IsPlaying, "An empty arrangement fell back to score playback.");
}

static void ArrangementTimelineFrame()
{
    var (score, _, section, _, _) = MakeScore();
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null,
        new[] { section.Id, section.Id });
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    clock.Render(NewSynth(), new float[120001], new float[120001]);

    Check(clock.Position is { MeasureId: var measureId, Fraction: var fraction } &&
        measureId == section.Measures[0].Id && Math.Abs(fraction - 0.25) < 0.0001,
        "The repeated occurrence did not retain the authored measure cursor.");
    Check(clock.GetTimelineFrameBehind(0) == 120001,
        "The projected frame did not identify the second repeated occurrence.");
    Check(clock.GetTimelineFrameBehind(24000) == 96001,
        "Queued-frame compensation did not resolve to the earlier repeated occurrence.");
    Check(clock.SectionTimelineSpans.Count == 2 &&
        clock.SectionTimelineSpans[0] == new PlaybackSectionTimelineSpan(section.Id, 0, 0, 96000) &&
        clock.SectionTimelineSpans[1] == new PlaybackSectionTimelineSpan(section.Id, 1, 96000, 192000),
        "Repeated section occurrences did not expose their distinct compiled timeline spans.");

    var loop = new ScorePlaybackClock(score, 48000,
        selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementLoop, null, new[] { section.Id }));
    loop.Render(NewSynth(), new float[96001], new float[96001]);
    Check(loop.IsPlaying && loop.GetTimelineFrameBehind(0) == 1,
        "The projected frame did not wrap with its arrangement loop.");
    Check(loop.SectionTimelineSpans.Count == 1 && loop.SectionTimelineSpans[0].StartFrame == 0 &&
        loop.SectionTimelineSpans[0].EndFrame == 96000,
        "The looping arrangement did not retain its per-pass section span.");
}

static void ArrangementSustainBoundaries()
{
    var (score, track, section, first, _) = MakeTabScore();
    score.RemoveMeasure(section, section.Measures[1]);
    AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(2, 1));
    var clock = new ScorePlaybackClock(score, 48000, selection:
        new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, new[] { section.Id, section.Id }));
    var events = PendingEvents(clock).Where(e => e.Value == 64 && (e.Kind == "NoteOn" || e.Kind == "NoteOff")).ToArray();
    Check(events.Any(e => e.Kind == "NoteOff" && e.Frame == 96000) && events.Any(e => e.Kind == "NoteOff" && e.Frame == 192000),
        "A repeated section did not release its sustain at both boundaries.");
    var active = (HashSet<Guid>)typeof(ScorePlaybackClock).GetField("_activeNotes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var synth = NewSynth();
    clock.Render(synth, new float[96001], new float[96001]);
    Check(active.Count == 1, "A repeated section left the earlier note active or lost the new attack.");
    clock.Render(synth, new float[95999], new float[95999]);
    Check(active.Count == 0 && !clock.IsPlaying, "A sustain remained active at the arrangement end.");
}

static void ArrangementTempo()
{
    var (score, track, section, _, voice) = MakeScore();
    var slow = score.AddSection("B", "Slow", new TempoMark(60m, new RhythmicValue(4)));
    AddHit(voice, 0, 36);
    AddHit(slow.Measures[0].GetTrackMeasure(track.Id)!.Voices[0], 0, 38);
    var clock = new ScorePlaybackClock(score, 48000, selection:
        new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, new[] { slow.Id, section.Id }));
    var hits = PendingEvents(clock).Where(e => e.Kind == "NoteOn").Select(e => (e.Frame, e.Value)).ToArray();
    Check(hits.SequenceEqual(new[] { (0L, 38), (192000L, 36) }), "The arranged section did not use its source tempo.");
}

static void MeasureLoopMeterDuration()
{
    var (score, lead, _, section) = MakeThreeMeasureSection();
    var measure = section.Measures[1];
    measure.TimeSignatureOverride = new TimeSignature(3, 4);
    AddHit(measure.GetTrackMeasure(lead.Id)!.Voices[0], 0, 48);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Measure, measure.Id, Array.Empty<Guid>());
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    var loopDuration = (long)typeof(ScorePlaybackClock).GetField("_loopDurationFrames", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    Check(loopDuration == 72000, "The selected 3/4 measure did not define a 72,000-frame loop duration.");
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 48 && e.Frame == 0),
        "The selected meter's measure event did not start at local frame zero.");
}

static void MeasureLoopMeterCursor()
{
    var (score, _, _, section) = MakeThreeMeasureSection();
    var measure = section.Measures[1];
    measure.TimeSignatureOverride = new TimeSignature(3, 4);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Measure, measure.Id, Array.Empty<Guid>());
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    var service = NewSynth();
    clock.Render(service, new float[90000], new float[90000]);
    Check(clock.Position is { MeasureId: var id } position && id == measure.Id
        && Math.Abs(position.Fraction - 0.25) < 0.00001,
        "The selected 3/4 measure cursor did not map its wrapped local frame back to a source measure fraction.");
}

static void DiscontiguousSectionLoop()
{
    var (score, lead, _, first) = MakeThreeMeasureSection();
    var middle = score.AddSection("B", "Skipped");
    score.AddMeasure(middle);
    var last = score.AddSection("C", "Selected");
    score.AddMeasure(last);
    AddHit(first.Measures[0].GetTrackMeasure(lead.Id)!.Voices[0], 0, 36);
    AddHit(middle.Measures[0].GetTrackMeasure(lead.Id)!.Voices[0], 0, 38);
    AddHit(last.Measures[0].GetTrackMeasure(lead.Id)!.Voices[0], 0, 40);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Sections, null,
        new[] { last.Id, first.Id, first.Id, Guid.NewGuid() });
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    var hits = PendingEvents(clock).Where(e => e.Kind == "NoteOn" && e.Value is 36 or 38 or 40)
        .Select(e => (e.Frame, e.Value)).ToArray();
    Check(hits.SequenceEqual(new[] { (0L, 36), (288000L, 40) }),
        "Selected sections were not deduplicated and traversed in score order with unselected sections skipped: " +
        string.Join(", ", hits.Select(hit => $"{hit.Frame}:{hit.Value}")));
    var service = NewSynth();
    var selectedDurationFrames = (first.Measures.Count + last.Measures.Count) * 96000;
    clock.Render(service, new float[selectedDurationFrames + 1], new float[selectedDurationFrames + 1]);
    Check(clock.Position is { MeasureId: var id } position && id == first.Measures[0].Id
        && position.Fraction < 0.001,
        $"Discontiguous section loop did not wrap its cursor to the first selected source measure: {clock.Position}");
}

static void StartInsideSectionLoop()
{
    var (score, lead, _, first) = MakeThreeMeasureSection();
    var selected = score.AddSection("B", "Selected");
    score.AddMeasure(selected);
    AddHit(selected.Measures[0].GetTrackMeasure(lead.Id)!.Voices[0], 0, 48);
    AddHit(selected.Measures[1].GetTrackMeasure(lead.Id)!.Voices[0], 0, 50);
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Sections, null, new[] { selected.Id, first.Id });
    var clock = new ScorePlaybackClock(score, 48000, selected.Measures[1].Id, selection);
    Check(clock.Position is { MeasureId: var id, Fraction: 0.0 } && id == selected.Measures[1].Id,
        "A scoped start measure did not become the initial cursor position.");
    var pending = PendingEvents(clock);
    Check(pending.Any(e => e.Kind == "NoteOn" && e.Value == 50 && e.Frame == 384000)
        && !pending.Any(e => e.Kind == "NoteOn" && e.Value == 48),
        "Starting inside the selected sections retained events before the chosen measure: " +
        string.Join(", ", pending.Where(e => e.Kind == "NoteOn").Select(e => $"{e.Frame}:{e.Value}")));
}

static void LoopBoundaryReleasesNotes()
{
    var (score, track, _, first, _) = MakeTabScore();
    var note = AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(2, 1));
    var selection = new PlaybackLoopSelection(PlaybackLoopMode.Measure, first.Id, Array.Empty<Guid>());
    var clock = new ScorePlaybackClock(score, 48000, selection: selection);
    var service = NewSynth();
    clock.Render(service, new float[96000], new float[96000]);
    var active = (HashSet<Guid>)typeof(ScorePlaybackClock).GetField("_activeNotes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    Check(!active.Contains(note.Id), "A note sustaining across a measure loop boundary remained active after wrap.");
    Check(clock.Position is { MeasureId: var id, Fraction: 0.0 } && id == first.Id,
        "A boundary release prevented the measure loop from wrapping to its selected measure.");
    clock.Render(service, new float[24000], new float[24000]);
    Check(active.Contains(note.Id), "The next measure-loop occurrence did not attack after releasing the prior sustain.");
}

static void EmptyLoopScopes()
{
    var (score, _, _, section) = MakeThreeMeasureSection();
    var missingMeasure = new PlaybackLoopSelection(PlaybackLoopMode.Measure, Guid.NewGuid(), Array.Empty<Guid>());
    var measureClock = new ScorePlaybackClock(score, 48000, selection: missingMeasure);
    Check(!measureClock.IsPlaying && PendingEvents(measureClock).Count == 0,
        "A missing measure target fell back to score playback instead of stopping empty.");

    var noSections = new PlaybackLoopSelection(PlaybackLoopMode.Sections, null, Array.Empty<Guid>());
    var sectionClock = new ScorePlaybackClock(score, 48000, selection: noSections);
    Check(!sectionClock.IsPlaying && PendingEvents(sectionClock).Count == 0,
        "An empty selected-section set fell back to score playback instead of stopping empty.");

    var emptySection = score.AddSection("Empty", "Empty");
    score.RemoveMeasure(emptySection, emptySection.Measures[0]);
    var emptyOnly = new PlaybackLoopSelection(PlaybackLoopMode.Sections, null, new[] { emptySection.Id });
    var emptyClock = new ScorePlaybackClock(score, 48000, selection: emptyOnly);
    Check(!emptyClock.IsPlaying && PendingEvents(emptyClock).Count == 0,
        "A selected section without measures did not produce an empty stopped scope.");
}

static void MeasureLoopRefresh()
{
    var (score, lead, _, section) = MakeThreeMeasureSection();
    var firstVoice = section.Measures[0].GetTrackMeasure(lead.Id)!.Voices[0];
    var note = AddHit(firstVoice, 1, 36);
    AddHit(section.Measures[1].GetTrackMeasure(lead.Id)!.Voices[0], 1, 38);
    var clock = new ScorePlaybackClock(score, 48000,
        selection: new PlaybackLoopSelection(PlaybackLoopMode.Measure, section.Measures[0].Id, Array.Empty<Guid>()));
    var service = NewSynth();
    clock.Render(service, new float[1000], new float[1000]);
    note.MidiPitch = 42;
    clock.RefreshScore(score);
    var pending = PendingEvents(clock);
    Check(pending.Any(e => e.Kind == "NoteOn" && e.Value == 42 && e.Frame == 24000)
        && !pending.Any(e => e.Kind == "NoteOn" && e.Value == 38),
        "Refreshing a measure loop did not apply the edit to the upcoming scoped event or included another measure: " +
        string.Join(", ", pending.Where(e => e.Kind == "NoteOn").Select(e => $"{e.Frame}:{e.Value}")));
}

static void ShortenedMeasureLoopRelease()
{
    var (score, track, _, measure, _) = MakeTabScore();
    var note = AddTab(measure, track, 1, 64, ScoreTime.Zero, new ScoreTime(2, 1));
    var clock = new ScorePlaybackClock(score, 48000,
        selection: new PlaybackLoopSelection(PlaybackLoopMode.Measure, measure.Id, Array.Empty<Guid>()));
    var service = NewSynth();
    clock.Render(service, new float[24000], new float[24000]);
    var active = (HashSet<Guid>)typeof(ScorePlaybackClock).GetField("_activeNotes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    Check(active.Contains(note.Id), "The setup note did not sound before shortening the selected measure.");
    measure.TimeSignatureOverride = new TimeSignature(2, 4);
    clock.RefreshScore(score);
    clock.Render(service, new float[24000], new float[24000]);
    Check(!active.Contains(note.Id), "A note-off carried from the old longer measure crossed the shortened loop boundary.");
}

static void ActiveNoteRelease()
{
    var (score, _, _, _, voice) = MakeScore();
    var note = AddHit(voice, 0, 42);
    var (clock, _) = StartAt(score, 1000);
    voice.Beats[0].Notes.Remove(note);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOff" && e.Value == 42 && e.Frame == 24000),
        "Removed active note lost its original note-off.");
}

static void TrackControls()
{
    var (score, track, _, _, voice) = MakeScore();
    AddHit(voice, 2, 45);
    track.IsMuted = true;
    var muted = new ScorePlaybackClock(score, 48000);
    Check(!PendingEvents(muted).Any(e => e.Kind == "NoteOn"), "Muted track still scheduled a hit.");
    track.IsMuted = false;
    track.IsSoloed = true;
    track.VolumeDb = -6;
    var controlled = new ScorePlaybackClock(score, 48000);
    var on = PendingEvents(controlled).Single(e => e.Kind == "NoteOn");
    Check(on.Value == 45 && on.SecondaryValue == TrackDefaults.VelocityFor(track.Kind),
        "Track volume changed the track's default authored MIDI velocity.");
}

static void Overfull()
{
    var (score, _, _, _, voice) = MakeScore();
    voice.Beats.Add(new Beat(new RhythmicValue(4)));
    AddHit(voice, 4, 50);
    var clock = new ScorePlaybackClock(score, 48000);
    Check(!PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 50), "Overfull tail was scheduled beyond the measure.");
}

static void BeatEditingLargestFit()
{
    var measure = new ScoreMeasure();
    var voice = new Voice();
    var placeholder = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, new TimeSignature(3, 4));
    Check(placeholder is not null, "An empty 3/4 voice did not produce a placeholder.");
    Check(placeholder!.RhythmicValue == new RhythmicValue(2), "The largest note value fitting in 3/4 was not a half note.");
    Check(placeholder.Notes.Count == 0, "The placeholder contained a note.");
}

static void BeatEditingRepeatLastValue()
{
    var measure = new ScoreMeasure();
    var signature = new TimeSignature(4, 4);

    var plainVoice = new Voice();
    plainVoice.Beats.Add(new Beat(new RhythmicValue(4)));
    var source = new Beat(new RhythmicValue(8)) { Intensity = 4, PitchBendSemitones = 1.25m };
    source.Notes.Add(new ScoreNote(42, 73));
    source.Notes.Add(new ScoreNote(46, 91));
    plainVoice.Beats.Add(source);
    var plain = BeatEditing.CreateIncompleteVoicePlaceholder(measure, plainVoice, signature, repeatLastValue: true);
    Check(plain is not null && plain.RhythmicValue == new RhythmicValue(8),
        "Repeat-last did not preserve the last eighth note instead of choosing the larger fitting value.");
    Check(plain!.Notes.Count == 2 && plainVoice.Beats.Count == 2, "Repeat-last did not copy the active state or mutated the voice.");
    Check(plain.Id != source.Id && plain.Intensity == source.Intensity &&
        plain.PitchBendSemitones == source.PitchBendSemitones, "Repeat-last did not copy intensity/bend with a fresh beat identity.");
    Check(plain.Notes.Select(note => (note.MidiPitch, note.Velocity)).SequenceEqual(source.Notes.Select(note => (note.MidiPitch, note.Velocity))) &&
        plain.Notes.Zip(source.Notes).All(pair => pair.First.Id != pair.Second.Id),
        "Repeat-last did not copy note pitches/velocities with fresh note identities.");
    plain.Notes[0].MidiPitch = 60;
    plain.Intensity = 2;
    plain.RhythmicValue = new RhythmicValue(16);
    plain.PitchBendSemitones = -0.5m;
    Check(source.Notes[0].MidiPitch == 42 && source.Intensity == 4 && source.RhythmicValue == new RhythmicValue(8) &&
        source.PitchBendSemitones == 1.25m, "Mutating the repeated beat changed its source.");

    var silentVoice = new Voice();
    var silent = new Beat(new RhythmicValue(8)) { Intensity = 2, PitchBendSemitones = -1m };
    silentVoice.Beats.Add(silent);
    var silentCopy = BeatEditing.CreateIncompleteVoicePlaceholder(measure, silentVoice, signature, repeatLastValue: true);
    Check(silentCopy is not null && silentCopy.Id != silent.Id && silentCopy.Notes.Count == 0 &&
        silentCopy.Intensity == 2 && silentCopy.PitchBendSemitones == -1m,
        "Repeat-last did not preserve silent beat state with a fresh identity.");

    var dottedValue = new RhythmicValue(8, dots: 1);
    var dottedVoice = new Voice();
    dottedVoice.Beats.Add(new Beat(new RhythmicValue(4)));
    dottedVoice.Beats.Add(new Beat(dottedValue));
    var dotted = BeatEditing.CreateIncompleteVoicePlaceholder(measure, dottedVoice, signature, repeatLastValue: true);
    Check(dotted is not null && dotted.RhythmicValue == dottedValue && dotted.Duration == dottedValue.Duration,
        "Repeat-last did not preserve the dotted rhythmic value exactly.");
    dottedVoice.Beats[^1].Intensity = 5;
    dottedVoice.Beats[^1].AddNote(new ScoreNote(50, 1));
    var dottedCopy = BeatEditing.CreateIncompleteVoicePlaceholder(measure, dottedVoice, signature, repeatLastValue: true);
    Check(dottedCopy is not null && dottedCopy.RhythmicValue == dottedValue && dottedCopy.Notes.Single().MidiPitch == 50,
        "Repeat-last did not retain dotted rhythm while copying full beat state.");

    var tupletValue = new RhythmicValue(8, tupletActual: 3, tupletNormal: 2);
    var tupletVoice = new Voice();
    tupletVoice.Beats.Add(new Beat(new RhythmicValue(4)));
    tupletVoice.Beats.Add(new Beat(tupletValue));
    var tuplet = BeatEditing.CreateIncompleteVoicePlaceholder(measure, tupletVoice, signature, repeatLastValue: true);
    Check(tuplet is not null && tuplet.RhythmicValue == tupletValue && tuplet.Duration == tupletValue.Duration,
        "Repeat-last did not preserve the tuplet ratio exactly.");
    tupletVoice.Beats[^1].AddNote(new ScoreNote(52, 1));
    var tupletCopy = BeatEditing.CreateIncompleteVoicePlaceholder(measure, tupletVoice, signature, repeatLastValue: true);
    Check(tupletCopy is not null && tupletCopy.RhythmicValue == tupletValue && tupletCopy.Notes.Single().MidiPitch == 52,
        "Repeat-last did not retain tuplet rhythm while copying full beat state.");

    var emptyFallback = BeatEditing.CreateIncompleteVoicePlaceholder(measure, new Voice(),
        new TimeSignature(3, 4), repeatLastValue: true);
    Check(emptyFallback is not null && emptyFallback.RhythmicValue == new RhythmicValue(2),
        "An empty voice did not use the normal largest-fitting fallback.");

    var noFitVoice = new Voice();
    noFitVoice.Beats.Add(new Beat(new RhythmicValue(2)));
    noFitVoice.Beats.Add(new Beat(new RhythmicValue(4, dots: 1)));
    var countBefore = noFitVoice.Beats.Count;
    var noFit = BeatEditing.CreateIncompleteVoicePlaceholder(measure, noFitVoice, signature, repeatLastValue: true);
    Check(noFit is null && noFitVoice.Beats.Count == countBefore,
        "A last value that exceeded the gap was added or mutated the voice.");
}

static void BeatEditingSuccessiveGaps()
{
    var measure = new ScoreMeasure();
    var voice = new Voice();
    var signature = new TimeSignature(3, 4);
    var first = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, signature);
    Check(first is not null && first.RhythmicValue == new RhythmicValue(2), "Expected a half-note first placeholder.");
    voice.Beats.Add(first!);
    var second = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, signature);
    Check(second is not null && second.RhythmicValue == new RhythmicValue(4), "Expected a quarter-note placeholder for the remaining gap.");
    voice.Beats.Add(second!);
    Check(BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, signature) is null,
        "A complete 3/4 voice still produced a placeholder.");
}

static void BeatEditingSub64th()
{
    var measure = new ScoreMeasure();
    var voice = new Voice();
    voice.Beats.Add(new Beat(new RhythmicValue(64, tupletActual: 4, tupletNormal: 63)));
    var placeholder = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, new TimeSignature(1, 4));
    Check(placeholder is not null, "A representable sub-64th residual did not produce a placeholder.");
    Check(placeholder!.Duration == new ScoreTime(1, 256), "The sub-64th placeholder did not exactly match the residual.");
    Check(voice.Beats.Count == 1, "Creating the candidate modified the voice.");
}

static void BeatEditingNoPlaceholderCases()
{
    var signature = new TimeSignature(3, 4);
    var regular = new ScoreMeasure();
    var complete = new Voice();
    complete.Beats.Add(new Beat(new RhythmicValue(2)));
    complete.Beats.Add(new Beat(new RhythmicValue(4)));
    Check(BeatEditing.CreateIncompleteVoicePlaceholder(regular, complete, signature) is null,
        "A complete voice produced a placeholder.");

    var overfull = new Voice();
    overfull.Beats.Add(new Beat(new RhythmicValue(1)));
    Check(BeatEditing.CreateIncompleteVoicePlaceholder(regular, overfull, signature) is null,
        "An overfull voice produced a placeholder.");

    var pickup = new ScoreMeasure { IsPickup = true };
    var incomplete = new Voice();
    incomplete.Beats.Add(new Beat(new RhythmicValue(2)));
    Check(BeatEditing.CreateIncompleteVoicePlaceholder(pickup, incomplete, signature) is null,
        "A pickup measure produced a placeholder.");

    var freeTime = new ScoreMeasure { IsFreeTime = true };
    Check(BeatEditing.CreateIncompleteVoicePlaceholder(freeTime, incomplete, signature) is null,
        "A free-time measure produced a placeholder.");
}

static void BeatEditingCandidateIsPure()
{
    var score = new ScoreDocument("Editing", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(3, 4));
    var track = score.AddTrack(new ScoreTrack("Kit", TrackKind.Percussion));
    var measure = score.AddMeasure(score.Sections[0]);
    var voice = measure.GetTrackMeasure(track.Id)!.Voices[0];
    voice.Beats.RemoveAt(voice.Beats.Count - 1);
    var originalBeatCount = voice.Beats.Count;
    var originalNoteCount = voice.Beats.Sum(beat => beat.Notes.Count);
    var clockBefore = new ScorePlaybackClock(score, 48000);
    var before = PendingEvents(clockBefore).ToArray();

    var candidate = BeatEditing.CreateIncompleteVoicePlaceholder(measure, voice, score.DefaultTimeSignature);
    Check(candidate is not null && candidate.Notes.Count == 0, "Expected a silent candidate placeholder.");
    var clockAfter = new ScorePlaybackClock(score, 48000);
    var after = PendingEvents(clockAfter).ToArray();
    Check(voice.Beats.Count == originalBeatCount && voice.Beats.Sum(beat => beat.Notes.Count) == originalNoteCount,
        "Requesting a candidate changed score content.");
    Check(before.Length == after.Length && before.SequenceEqual(after), "Requesting a candidate changed scheduled sound events.");
}

static void DottedBeatTiming()
{
    var (score, track, _, _, voice) = MakeScore();
    AddHit(voice, 0, 36);
    AddHit(voice, 1, 38);
    var (clock, _) = StartAt(score, 1000);
    voice.Beats[0].RhythmicValue = new RhythmicValue(4, dots: 1);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 38 && e.Frame == 36000),
        "The beat after a dotted quarter was not scheduled at 3/8 of a whole note.");
    var saved = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restored = saved.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.Voices[0].Beats[0];
    Check(restored.RhythmicValue.Dots == 1 && restored.Duration == new ScoreTime(3, 8),
        "The dotted value was not preserved in the score file.");
}

static void InsertBeforeCurrentMeasure()
{
    var (score, track, section, first, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var second = score.AddMeasure(section);
    var secondVoice = second.GetTrackMeasure(track.Id)!.Voices[0];
    AddHit(secondVoice, 2, 48);
    var (clock, _) = StartAt(score, 120000);
    score.InsertMeasureAfter(section, 0);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 48 && e.Frame == 144000),
        "Current measure position was not remapped to the same measure after insertion.");
    Check(PendingEvents(clock).All(e => !(e.Kind == "NoteOn" && e.Value == 36 && e.Frame < 120000)), "Past first measure was replayed after insertion.");
}

static void StartAtMeasure()
{
    var (score, track, section, first, firstVoice) = MakeScore();
    AddHit(firstVoice, 0, 36);
    var second = score.AddMeasure(section);
    AddHit(second.GetTrackMeasure(track.Id)!.Voices[0], 2, 48);
    var clock = new ScorePlaybackClock(score, 48000, second.Id);
    var position = clock.Position;
    Check(position is { MeasureId: var id, Fraction: 0.0 } && id == second.Id,
        "Playback did not begin at the selected measure's start.");
    var pending = PendingEvents(clock);
    Check(pending.Any(item => item.Kind == "NoteOn" && item.Value == 48 && item.Frame == 144000),
        "The selected measure's note was not retained at its score-wide frame.");
    Check(pending.All(item => !(item.Kind == "NoteOn" && item.Value == 36)),
        "A hit before the selected measure remained pending.");
    Check(pending.Any(item => item.Kind == "ProgramChange" && item.Frame == 96000),
        "Instrument setup was not moved to the selected start boundary.");

    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    clock.Render(service, new float[24000], new float[24000]);
    Check(clock.Position is { MeasureId: var currentId } current && currentId == second.Id
        && Math.Abs(current.Fraction - 0.25) < 0.00001,
        "The cursor did not advance by musical measure fraction.");
    clock.LoopEnabled = true;
    clock.Render(service, new float[72000], new float[72000]);
    Check(clock.Position is { MeasureId: var loopId, Fraction: 0.0 } && loopId == first.Id,
        "The first full-score loop after a selected start did not return to score start.");
    clock.Stop(service);
    Check(clock.Position is { MeasureId: var stoppedId, Fraction: 0.0 } && stoppedId == second.Id,
        "Stopping did not reset the cursor to the selected start measure.");
}

static void CursorMusicalFraction()
{
    var (score, _, _, measure, _) = MakeScore();
    measure.TempoEvents.Add(new TempoEvent(new ScoreTime(1, 4),
        new TempoMark(60m, new RhythmicValue(4))));
    var clock = new ScorePlaybackClock(score, 48000);
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    clock.Render(service, new float[48000], new float[48000]);
    var position = clock.Position;
    Check(position is { MeasureId: var id } current && id == measure.Id
        && Math.Abs(current.Fraction - 0.375) < 0.00001,
        "Cursor fraction followed elapsed seconds instead of score time across an in-measure tempo change.");
}

static void TrailingSilence()
{
    var (score, _, _, _, _) = MakeScore();
    var (clock, _) = StartAt(score, 24000);
    Check(clock.IsPlaying, "An empty measure stopped before its end.");
}

static void MeterChange()
{
    var (score, _, _, measure, voice) = MakeScore();
    AddHit(voice, 2, 48);
    var (clock, _) = StartAt(score, 12000);
    measure.TimeSignatureOverride = new TimeSignature(6, 8);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 48 && e.Frame == 48000),
        "Changing meter moved the current playback position.");
}

static void RemoveMeasure()
{
    var (score, _, section, measure, _) = MakeScore();
    score.AddTrack(new ScoreTrack("Other", TrackKind.Percussion));
    Check(measure.TrackMeasures.Count == 2, "Expected both tracks in the shared measure.");
    score.RemoveMeasure(section, measure);
    Check(section.Measures.Count == 0, "The shared measure was not removed.");
    var replacement = score.AddMeasure(section);
    Check(replacement.TrackMeasures.Count == 2, "A replacement measure lost track alignment.");
    try
    {
        score.RemoveMeasure(section, measure);
        throw new InvalidOperationException("Removing a measure outside the section succeeded.");
    }
    catch (ArgumentException) { }
}

static void DeleteCurrentMeasure()
{
    var (score, track, section, first, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var second = score.AddMeasure(section);
    AddHit(second.GetTrackMeasure(track.Id)!.Voices[0], 0, 48);
    var (clock, _) = StartAt(score, 12000);
    score.RemoveMeasure(section, first);
    clock.RefreshScore(score);
    var pending = PendingEvents(clock);
    Check(pending.Any(e => e.Kind == "NoteOn" && e.Value == 48 && e.Frame == 12000),
        "Playback did not advance to the next measure's first hit.");
    Check(pending.Any(e => e.Kind == "NoteOff" && e.Value == 36 && e.Frame == 24000),
        "Deleting the current measure lost the sounding note's release.");
}

static void DeleteEarlierMeasure()
{
    var (score, track, section, first, _) = MakeScore();
    var second = score.AddMeasure(section);
    AddHit(second.GetTrackMeasure(track.Id)!.Voices[0], 2, 48);
    var (clock, _) = StartAt(score, 120000);
    score.RemoveMeasure(section, first);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 48 && e.Frame == 144000),
        "Deleting an earlier measure moved the playhead within the current measure.");
}

static void RemoveTrackRefresh()
{
    var (score, removed, section, measure, voice) = MakeScore();
    var retained = score.AddTrack(new ScoreTrack("Retained", TrackKind.Keyboard));
    AddHit(voice, 1, 36);
    AddHit(measure.GetTrackMeasure(retained.Id)!.Voices[0], 2, 60);
    var (clock, _) = StartAt(score, 12000);
    score.RemoveTrack(removed);
    clock.RefreshScore(score);
    var events = PendingEvents(clock);
    var scheduled = (Array)typeof(ScorePlaybackClock).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var nextIndex = (int)typeof(ScorePlaybackClock).GetField("_nextEventIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var noteOns = scheduled.Cast<object>().Skip(nextIndex)
        .Where(item => item.GetType().GetProperty("Kind")!.GetValue(item)!.ToString() == "NoteOn").ToArray();
    Check(!noteOns.Any(item => (Guid)item.GetType().GetProperty("TrackId")!.GetValue(item)! == removed.Id) &&
        noteOns.Any(item => (Guid)item.GetType().GetProperty("TrackId")!.GetValue(item)! == retained.Id) &&
        !events.Any(e => e.Kind == "NoteOn" && e.Value == 36) && events.Any(e => e.Kind == "NoteOn" && e.Value == 60),
        "Refreshing playback after track deletion retained removed-track events or lost the surviving track's events.");
    Check(section.LeadTrackId == retained.Id, "Deleting the lead did not update the section lead before playback refresh.");
}

static (ScoreDocument Score, ScoreTrack Lead, ScoreTrack Drums, ScoreSection Section) MakeThreeMeasureSection()
{
    var score = new ScoreDocument("Repeating", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var lead = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
    var drums = score.AddTrack(new ScoreTrack("Drums", TrackKind.Percussion));
    var section = score.Sections[0];
    section.Title = "Verse";
    score.AddMeasure(section);
    score.AddMeasure(section);
    score.AddMeasure(section);
    return (score, lead, drums, section);
}

static void DefaultSection()
{
    var score = new ScoreDocument("New", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    Check(score.Sections.Count == 1 && score.Sections[0].Measures.Count == 0 && score.Sections[0].LeadTrackId is null,
        "A new score did not start with one empty section and no lead track.");
    try
    {
        ((ICollection<ScoreSection>)score.Sections).Clear();
        throw new InvalidOperationException("The read-only section collection allowed clearing the final section.");
    }
    catch (NotSupportedException) { }
    Check(score.Sections.Count == 1, "The last section was removed through the collection view.");
    var track = score.AddTrack(new ScoreTrack("First", TrackKind.Guitar));
    Check(score.Sections[0].LeadTrackId == track.Id && score.GetTrackPatternLength(score.Sections[0], track) == 0,
        "The first track did not become lead of the empty section.");
}

static void LeadTrackAndPatternLengths()
{
    var (score, lead, drums, section) = MakeThreeMeasureSection();
    Check(section.LeadTrackId == lead.Id, "First track was not selected as section lead.");
    Check(score.GetTrackPatternLength(section, lead) == 3 && score.GetTrackPatternLength(section, drums) == 1,
        "Lead length and default non-lead length do not match the section.");
    score.SetTrackPatternLength(section, drums, 2);
    score.SetLeadTrack(section, drums);
    Check(score.GetTrackPatternLength(section, drums) == 3 && score.GetTrackPatternLength(section, lead) == 3,
        "Switching leads did not preserve the prior lead's full part.");
    score.SetLeadTrack(section, lead);
    Check(score.GetTrackPatternLength(section, drums) == 3, "The previous lead's full part was not retained.");
    try
    {
        score.SetTrackPatternLength(section, lead, 1);
        throw new InvalidOperationException("A shorter lead pattern was accepted.");
    }
    catch (ArgumentException) { }
}

static void RepeatingPatternStopsAtSection()
{
    var (score, _, drums, section) = MakeThreeMeasureSection();
    AddHit(section.Measures[0].GetTrackMeasure(drums.Id)!.Voices[0], 0, 36);
    score.AddSection("B", "Next");
    var clock = new ScorePlaybackClock(score, 48000);
    var events = PendingEvents(clock);
    var hits = events.Where(e => e.Kind == "NoteOn" && e.Value == 36).Select(e => e.Frame).ToArray();
    Check(hits.SequenceEqual(new long[] { 0, 96000, 192000 }),
        "The one-measure drum part did not repeat exactly across its three-measure section.");
    var scheduled = (Array)typeof(ScorePlaybackClock).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var eventIds = scheduled.Cast<object>()
        .Where(e => e.GetType().GetProperty("Kind")!.GetValue(e)!.ToString() == "NoteOn")
        .Where(e => (int)e.GetType().GetProperty("Value")!.GetValue(e)! == 36)
        .Select(e => (Guid)e.GetType().GetProperty("NoteId")!.GetValue(e)!).ToArray();
    Check(eventIds.Length == 3 && eventIds.Distinct().Count() == 3,
        "Repeated percussion occurrences did not receive independent event identities.");
}

static void TwoMeasurePattern()
{
    var (score, _, drums, section) = MakeThreeMeasureSection();
    AddHit(section.Measures[0].GetTrackMeasure(drums.Id)!.Voices[0], 0, 36);
    AddHit(section.Measures[1].GetTrackMeasure(drums.Id)!.Voices[0], 0, 38);
    score.SetTrackPatternLength(section, drums, 2);
    var events = PendingEvents(new ScorePlaybackClock(score, 48000));
    var hits = events.Where(e => e.Kind == "NoteOn" && (e.Value == 36 || e.Value == 38))
        .Select(e => (e.Frame, e.Value)).ToArray();
    Check(hits.SequenceEqual(new[] { (0L, 36), (96000L, 38), (192000L, 36) }),
        "The two-measure pattern was not repeated in order.");
}

static void DeleteSection()
{
    var (score, lead, _, first) = MakeThreeMeasureSection();
    var second = score.AddSection("B", "Next");
    Check(ReferenceEquals(score.RemoveSection(first), second), "Removing first section did not select its next neighbour.");
    var replacement = score.RemoveSection(second);
    Check(score.Sections.Count == 1 && ReferenceEquals(score.Sections[0], replacement) && replacement.Measures.Count == 0,
        "Removing the last section did not create one empty default section.");
    Check(replacement.LeadTrackId == lead.Id, "The replacement section did not retain a valid default lead.");
}

static void RefreshRepeatedPattern()
{
    var (score, _, drums, section) = MakeThreeMeasureSection();
    var source = section.Measures[0].GetTrackMeasure(drums.Id)!.Voices[0];
    var note = AddHit(source, 0, 36);
    var (clock, _) = StartAt(score, 1000);
    note.MidiPitch = 38;
    clock.RefreshScore(score);
    var pending = PendingEvents(clock);
    Check(pending.Any(e => e.Kind == "NoteOff" && e.Value == 36 && e.Frame == 24000),
        "Editing the repeating source lost the active note's release.");
    Check(pending.Where(e => e.Kind == "NoteOn" && e.Value == 38).Select(e => e.Frame)
        .SequenceEqual(new long[] { 96000, 192000 }), "Future repeated occurrences did not reflect the live edit.");
}

static void DeleteCurrentSection()
{
    var (score, _, drums, first) = MakeThreeMeasureSection();
    AddHit(first.Measures[0].GetTrackMeasure(drums.Id)!.Voices[0], 0, 36);
    var second = score.AddSection("B", "Next");
    AddHit(second.Measures[0].GetTrackMeasure(drums.Id)!.Voices[0], 0, 38);
    var (clock, _) = StartAt(score, 1000);
    score.RemoveSection(first);
    clock.RefreshScore(score);
    Check(PendingEvents(clock).Any(e => e.Kind == "NoteOn" && e.Value == 38 && e.Frame == 1000),
        "Deleting the playing section did not continue at the next section's first hit.");
}

static IReadOnlyList<EventView> PendingEvents(ScorePlaybackClock clock)
{
    var field = typeof(ScorePlaybackClock).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var array = (Array)field.GetValue(clock)!;
    var nextIndex = (int)typeof(ScorePlaybackClock).GetField("_nextEventIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var offset = (long)typeof(ScorePlaybackClock).GetField("_eventFrameOffset", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var result = new List<EventView>();
    for (var index = nextIndex; index < array.Length; index++)
    {
        var item = array.GetValue(index)!;
        var type = item!.GetType();
        result.Add(new EventView(
            (long)type.GetProperty("Frame")!.GetValue(item)! + offset,
            type.GetProperty("Kind")!.GetValue(item)!.ToString()!,
            (int)type.GetProperty("Value")!.GetValue(item)!,
            (int)type.GetProperty("SecondaryValue")!.GetValue(item)!));
    }
    var carriedField = typeof(ScorePlaybackClock).GetField("_carriedEvents", BindingFlags.Instance | BindingFlags.NonPublic);
    if (carriedField?.GetValue(clock) is System.Collections.IEnumerable carried)
    {
        foreach (var item in carried)
        {
            var type = item!.GetType();
            result.Add(new EventView((long)type.GetProperty("Frame")!.GetValue(item)!, type.GetProperty("Kind")!.GetValue(item)!.ToString()!,
                (int)type.GetProperty("Value")!.GetValue(item)!, (int)type.GetProperty("SecondaryValue")!.GetValue(item)!));
        }
    }
    return result;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ScoreFileRoundTrip()
{
    var (score, track, section, measure, voice) = MakeScore();
    var note = AddHit(voice, 1, 42);
    voice.Beats[1].Intensity = 5;
    var extra = score.AddSection("B", "Delete me");
    score.RemoveSection(extra);
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Check(restored.Sections.Count == 1 && restored.Sections[0].Id == section.Id, "Deleted section returned or section ID changed.");
    Check(restored.Tracks[0].Id == track.Id && restored.Sections[0].Measures[0].Id == measure.Id, "Track or measure identity changed.");
    var restoredBeat = restored.Sections[0].Measures[0].GetTrackMeasure(track.Id)!.Voices[0].Beats[1];
    Check(restoredBeat.Intensity == 5 && restoredBeat.Notes[0].Id == note.Id && restoredBeat.Notes[0].MidiPitch == 42, "Beat or note data changed.");
    Check(restored.Sections[0].LeadTrackId == track.Id, "Section lead changed.");
}

static void TempoQuarterBpmConversionsAndOverridesRoundTrip()
{
    var dottedQuarter = new RhythmicValue(4, dots: 1);
    var scoreTempo = new TempoMark(80m, dottedQuarter);
    Check(scoreTempo.QuarterNotesPerMinute == 120m, "Dotted-quarter BPM was not converted to quarter-note BPM.");
    var converted = TempoMark.FromQuarterNotesPerMinute(120m, dottedQuarter);
    Check(converted.BeatsPerMinute == 80m && converted.BeatUnit == dottedQuarter, "Quarter BPM conversion did not preserve the stored reference unit.");

    var score = new ScoreDocument("Tempo conversion", scoreTempo, new TimeSignature(4, 4));
    var track = score.AddTrack(new ScoreTrack("Drums", TrackKind.Percussion));
    var firstSection = score.Sections[0];
    var firstMeasure = score.AddMeasure(firstSection);
    var eventTempo = new TempoMark(160m, new RhythmicValue(8));
    firstMeasure.TempoEvents.Add(new TempoEvent(new ScoreTime(1, 4), eventTempo));
    var secondSection = score.AddSection("B", "Override", new TempoMark(100m, new RhythmicValue(2)), new TimeSignature(4, 4));
    score.AddMeasure(secondSection);

    var editedDefault = TempoMark.FromQuarterNotesPerMinute(132m, score.DefaultTempo.BeatUnit);
    score.DefaultTempo = editedDefault;
    Check(score.DefaultTempo.BeatUnit == dottedQuarter && score.DefaultTempo.QuarterNotesPerMinute == 132m, "Editing the score tempo changed its stored unit or quarter BPM.");
    Check(secondSection.DefaultTempo == new TempoMark(100m, new RhythmicValue(2)), "Editing the score default changed a section override.");
    Check(firstMeasure.TempoEvents[0].Value == eventTempo, "Editing the score default changed a measure tempo event.");

    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restoredMap = restored.CreateTempoMap();
    Check(restored.DefaultTempo == editedDefault, "The score tempo mark did not round-trip.");
    Check(restored.Sections[1].DefaultTempo == secondSection.DefaultTempo, "The section tempo override did not round-trip.");
    Check(restored.Sections[0].Measures[0].TempoEvents[0].Value == eventTempo, "The measure tempo event did not round-trip.");
    Check(restoredMap.ResolveAt(ScoreTime.Zero).WholeNotesPerMinute * 4m == 132m, "The edited score default did not resolve as quarter BPM.");
    Check(restoredMap.ResolveAt(new ScoreTime(1, 4)).WholeNotesPerMinute * 4m == 80m, "The measure event did not override the score default.");
    Check(restoredMap.ResolveAt(new ScoreTime(1, 1)).WholeNotesPerMinute * 4m == 200m, "The section override did not apply at its boundary.");
    var expectedElapsed = 60.0 / 132.0 + (3.0 * 60.0 / 80.0);
    Check(Math.Abs(restoredMap.GetElapsedSeconds(new ScoreTime(1, 1)) - expectedElapsed) < 1e-9, "Tempo-map elapsed time did not follow the event override.");

    try
    {
        _ = TempoMark.FromQuarterNotesPerMinute(decimal.MaxValue, new RhythmicValue(64));
        throw new InvalidOperationException("An unrepresentable converted tempo was accepted.");
    }
    catch (ArgumentOutOfRangeException) { }
    try
    {
        _ = new TempoMark(decimal.MaxValue, new RhythmicValue(1)).QuarterNotesPerMinute;
        throw new InvalidOperationException("An overflowing quarter-note BPM conversion was accepted.");
    }
    catch (ArgumentOutOfRangeException) { }
}

static void PercussionMasterGainPersistence()
{
    var (score, _, _, _, _) = MakeScore();
    score.PercussionMasterGainDb = -12.5;
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Check(restored.PercussionMasterGainDb == -12.5, "The percussion master gain did not survive archive round-trip.");

    var archive = ScoreFile.Serialize(score);
    using var input = new MemoryStream(archive);
    using var source = new ZipArchive(input, ZipArchiveMode.Read);
    using var jsonReader = new StreamReader(source.GetEntry("project.json")!.Open());
    var document = JsonNode.Parse(jsonReader.ReadToEnd())!;
    document.AsObject().Remove("PercussionMasterGainDb");
    using var legacyBytes = new MemoryStream();
    using (var legacy = new ZipArchive(legacyBytes, ZipArchiveMode.Create, true))
    using (var writer = new StreamWriter(legacy.CreateEntry("project.json").Open()))
        writer.Write(document.ToJsonString());
    var legacyScore = ScoreFile.Deserialize(legacyBytes.ToArray());
    Check(legacyScore.PercussionMasterGainDb == 0, "An archive without the optional gain field did not default to 0 dB.");
    Check(new ScoreFile.Document().PercussionMasterGainDb == 0, "A legacy document DTO did not default to 0 dB.");

    foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -80.01, 20.01 })
    {
        try { score.PercussionMasterGainDb = invalid; throw new InvalidOperationException("Invalid master gain was accepted."); }
        catch (ArgumentOutOfRangeException) { }
    }
}

static void PercussionMasterGainAudio()
{
    var baseline = RenderSingleTrack(TrackKind.Percussion, -6, 0);
    var adjusted = RenderSingleTrack(TrackKind.Percussion, -6, -6);
    AssertGain(baseline, adjusted, Math.Pow(10, -6.0 / 20), "combined -6 dB track and -6 dB percussion master gain");

    var melodicBaseline = RenderSingleTrack(TrackKind.Guitar, -6, 0);
    var melodicWithPercussionMaster = RenderSingleTrack(TrackKind.Guitar, -6, -6);
    AssertGain(melodicBaseline, melodicWithPercussionMaster, 1.0, "percussion master isolation from melodic tracks");

    var twoPercussionBaseline = RenderMultiplePercussionTracks(0);
    var twoPercussionAdjusted = RenderMultiplePercussionTracks(-6);
    AssertGain(twoPercussionBaseline, twoPercussionAdjusted, Math.Pow(10, -6.0 / 20), "equal master gain across multiple percussion tracks");
}

static void PercussionMasterGainRefresh()
{
    var score = MakeSingleTrackScore(TrackKind.Percussion, -6, 0);
    var track = score.Tracks[0];
    var clock = new ScorePlaybackClock(score, 48000);
    var referenceClock = new ScorePlaybackClock(score, 48000);
    var adjusted = NewSynth();
    var reference = NewSynth();
    var left = new float[4096]; var right = new float[4096];
    var refLeft = new float[4096]; var refRight = new float[4096];
    clock.Render(adjusted, left, right);
    referenceClock.Render(reference, refLeft, refRight);

    score.PercussionMasterGainDb = -6;
    clock.RefreshScore(score);
    left = new float[4096]; right = new float[4096];
    refLeft = new float[4096]; refRight = new float[4096];
    clock.Render(adjusted, left, right);
    referenceClock.Render(reference, refLeft, refRight);
    AssertGain(refLeft, left, Math.Pow(10, -6.0 / 20), "live percussion master edit on a sounding hit");

    score.PercussionMasterGainDb = -12;
    clock.RefreshScore(score);
    left = new float[4096]; right = new float[4096];
    refLeft = new float[4096]; refRight = new float[4096];
    clock.Render(adjusted, left, right);
    referenceClock.Render(reference, refLeft, refRight);
    AssertGain(refLeft, left, Math.Pow(10, -12.0 / 20), "repeated master edits without gain compounding");
}

static float[] RenderSingleTrack(TrackKind kind, double trackGainDb, double masterGainDb)
{
    var score = MakeSingleTrackScore(kind, trackGainDb, masterGainDb);
    var clock = new ScorePlaybackClock(score, 48000);
    var service = NewSynth();
    var left = new float[4096]; var right = new float[4096];
    clock.Render(service, left, right);
    return left;
}

static float[] RenderMultiplePercussionTracks(double masterGainDb)
{
    var score = new ScoreDocument("Drum Gain", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4))
    {
        PercussionMasterGainDb = masterGainDb
    };
    var first = score.AddTrack(new ScoreTrack("Kick", TrackKind.Percussion) { VolumeDb = 0 });
    var second = score.AddTrack(new ScoreTrack("Kick Layer", TrackKind.Percussion) { VolumeDb = -6 });
    var measure = score.AddMeasure(score.Sections[0]);
    AddHit(measure.GetTrackMeasure(first.Id)!.Voices[0], 0, 36);
    AddHit(measure.GetTrackMeasure(second.Id)!.Voices[0], 0, 36);
    var clock = new ScorePlaybackClock(score, 48000);
    var service = NewSynth();
    var left = new float[4096]; var right = new float[4096];
    clock.Render(service, left, right);
    return left;
}

static ScoreDocument MakeSingleTrackScore(TrackKind kind, double trackGainDb, double masterGainDb)
{
    var score = new ScoreDocument("Gain", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    score.PercussionMasterGainDb = masterGainDb;
    var track = score.AddTrack(new ScoreTrack("Track", kind) { VolumeDb = trackGainDb });
    var measure = score.AddMeasure(score.Sections[0]);
    AddHit(measure.GetTrackMeasure(track.Id)!.Voices[0], 0, kind == TrackKind.Percussion ? 36 : 60);
    return score;
}

static void DrumPolyphony()
{
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    var service = new SoundFontPlaybackService(stream, 48000);
    for (var i = 0; i < 4; i++) service.NoteOn(9, 36, 100);
    service.NoteOn(9, 38, 100);
    var players = (System.Collections.IDictionary)typeof(SoundFontPlaybackService).GetField("_drumPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    Check(players.Count == 2, "Drum pieces did not receive independent players.");
    var kick = players[36]!;
    var voices = (Array)kick.GetType().GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kick)!;
    Check(voices.Length >= 4, "The drum piece has fewer than four hit slots.");
    var left = new float[2048]; var right = new float[2048];
    service.Render(left, right);
    Check(left.Any(sample => Math.Abs(sample) > 0.000001f) || right.Any(sample => Math.Abs(sample) > 0.000001f), "Drum players produced silence.");
    Check(voices.Cast<MeltySynth.Synthesizer>().All(voice => voice.ActiveVoiceCount > 0), "The four drum hits are not sounding concurrently.");
    for (var i = 0; i < 4; i++) service.NoteOff(9, 36);
    service.AllNotesOff(9);
}

static SoundFontPlaybackService NewSynth()
{
    using var stream = File.OpenRead("Music/SoundFonts/FluidR3_GM.sf2");
    return new SoundFontPlaybackService(stream, 48000);
}

static void TrackGain()
{
    var baseline = NewSynth();
    var adjusted = NewSynth();
    var track = Guid.NewGuid(); var note = Guid.NewGuid();
    baseline.SetTrackVolume(track, 0);
    adjusted.SetTrackVolume(track, -6);
    baseline.NoteOn(track, 9, 36, 76, note);
    adjusted.NoteOn(track, 9, 36, 76, note);
    var left = new float[2048]; var right = new float[2048];
    var otherLeft = new float[2048]; var otherRight = new float[2048];
    baseline.Render(left, right); adjusted.Render(otherLeft, otherRight);
    AssertGain(left, otherLeft, Math.Pow(10, -6.0 / 20), "-6 dB");
    adjusted.SetTrackVolume(track, 6);
    baseline.Render(left, right); adjusted.Render(otherLeft, otherRight);
    AssertGain(left, otherLeft, Math.Pow(10, 6.0 / 20), "+6 dB on an already sounding note");
}

static void TrackGainIsolation()
{
    var reference = NewSynth(); var mixed = NewSynth();
    var a = Guid.NewGuid(); var b = Guid.NewGuid();
    reference.SetTrackVolume(a, 0); mixed.SetTrackVolume(a, 0); mixed.SetTrackVolume(b, -80);
    reference.NoteOn(a, 9, 36, 76, Guid.NewGuid());
    mixed.NoteOn(a, 9, 36, 76, Guid.NewGuid());
    mixed.NoteOn(b, 9, 36, 76, Guid.NewGuid());
    var left = new float[2048]; var right = new float[2048];
    var mixLeft = new float[2048]; var mixRight = new float[2048];
    reference.Render(left, right); mixed.Render(mixLeft, mixRight);
    AssertGain(left, mixLeft, 1.0001, "independent same-pitch track gains");
}

static void TrackGainRefresh()
{
    var (score, track, _, _, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var first = new ScorePlaybackClock(score, 48000);
    var second = new ScorePlaybackClock(score, 48000);
    var baseline = NewSynth(); var adjusted = NewSynth();
    var left = new float[2048]; var right = new float[2048];
    var otherLeft = new float[2048]; var otherRight = new float[2048];
    first.Render(baseline, left, right); second.Render(adjusted, otherLeft, otherRight);
    track.VolumeDb = -6;
    second.RefreshScore(score);
    first.Render(baseline, left, right); second.Render(adjusted, otherLeft, otherRight);
    AssertGain(left, otherLeft, Math.Pow(10, -6.0 / 20), "live score refresh");
}

static void ConfiguredKitEvents()
{
    var (score, _, _, section) = MakeThreeMeasureSection();
    var kit = score.AddTrack(new ScoreTrack("Kit", TrackKind.Percussion));
    kit.ConfigurePercussionPieces(new[] { new PercussionPiece("kick", "Kick", 36), new PercussionPiece("snare", "Snare", 38) { IsMuted = true } });
    var part = section.Measures[0].GetTrackMeasure(kit.Id)!;
    part.UsesTabEvents = true;
    part.TabEvents.Add(new TabEvent(1, 0, 99, 100, ScoreTime.Zero, new ScoreTime(1, 4)));
    part.TabEvents.Add(new TabEvent(2, 0, 98, 100, ScoreTime.Zero, new ScoreTime(1, 4)));
    score.SetTrackPatternLength(section, kit, 1);
    score.SetTrackMeasureRepeatCount(section, kit, 0, 1);
    var events = PendingEvents(new ScorePlaybackClock(score, 48000)).Where(e => e.Kind == "NoteOn").ToArray();
    Check(events.Where(e => e.Value == 36).Select(e => e.Frame).SequenceEqual(new[] { 0L, 96000L }), "Kit fixed pitch or explicit linked repeat is incorrect.");
    Check(!events.Any(e => e.Value is 38 or 98 or 99), "Muted or authored pitch leaked into kit playback.");
}

static void SoundVariationPlayback()
{
    var score = new ScoreDocument("Variations", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var guitar = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar) { DefaultMidiProgram = 25 });
    var measure = score.AddMeasure(score.Sections[0]);
    var part = measure.GetTrackMeasure(guitar.Id)!; part.UsesTabEvents = true;
    part.TabEvents.Add(new TabEvent(1, 0, 60, 90, ScoreTime.Zero, new ScoreTime(1, 4)));
    part.TabEvents.Add(new TabEvent(2, 0, 62, 90, ScoreTime.Zero, new ScoreTime(1, 4)) { SoundVariation = "palm_mute" });
    var clock = new ScorePlaybackClock(score, 48000);
    var scheduled = (Array)typeof(ScorePlaybackClock).GetField("_events", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    var noteOns = scheduled.Cast<object>().Where(item => item.GetType().GetProperty("Kind")!.GetValue(item)!.ToString() == "ExpressiveNoteOn").ToArray();
    Check(noteOns.Length == 1, "A variation note was not scheduled on an isolated expressive voice.");
    Check((int)noteOns[0].GetType().GetProperty("ProgramOverride")!.GetValue(noteOns[0])! == 28, "The selected palm mute did not resolve to program 28.");
    var kitScore = new ScoreDocument("Kit variations", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var kit = kitScore.AddTrack(new ScoreTrack("Hat kit", TrackKind.Percussion));
    kit.ConfigurePercussionPieces(new[] { new PercussionPiece("hat", "Open hat", 46) { GainDb = -6 } });
    var kitMeasure = kitScore.AddMeasure(kitScore.Sections[0]);
    var kitPart = kitMeasure.GetTrackMeasure(kit.Id)!; kitPart.UsesTabEvents = true;
    kitPart.TabEvents.Add(new TabEvent(1, 0, 46, 90, ScoreTime.Zero, new ScoreTime(1, 4)) { SoundVariation = "closed" });
    var hits = PendingEvents(new ScorePlaybackClock(kitScore, 48000)).Where(item => item.Kind is "NoteOn" or "ExpressiveNoteOn").ToArray();
    Check(hits.Length == 1 && hits[0].Value == 42, "Configured open-hat lane did not play its selected closed-hat pitch.");
    var kitSynth = NewSynth(); var kitLeft = new float[2048]; var kitRight = new float[2048];
    new ScorePlaybackClock(kitScore, 48000).Render(kitSynth, kitLeft, kitRight);
    Check(kitLeft.Any(sample => Math.Abs(sample) > 0.000001f) || kitRight.Any(sample => Math.Abs(sample) > 0.000001f),
        "The clock-to-service configured percussion variation path produced silence or rejected its current-program sentinel.");
}

static void HiHatChoke()
{
    var service = NewSynth(); var track = Guid.NewGuid();
    service.NoteOn(track, 9, 46, 100, Guid.NewGuid());
    service.Render(new float[512], new float[512]);
    var players = (System.Collections.IDictionary)typeof(SoundFontPlaybackService).GetField("_trackOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    var output = players[track]!;
    var childService = output.GetType().GetProperty("Service")!.GetValue(output)!;
    var drumPlayers = (System.Collections.IDictionary)childService.GetType().GetField("_drumPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(childService)!;
    var openPlayer = drumPlayers[46]!;
    var voices = (Array)openPlayer.GetType().GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(openPlayer)!;
    Check(voices.Cast<MeltySynth.Synthesizer>().Any(voice => voice.ActiveVoiceCount > 0), "Open hat did not start before choke test.");
    service.NoteOn(track, 9, 42, 100, Guid.NewGuid());
    service.Render(new float[512], new float[512]);
    Check(voices.Cast<MeltySynth.Synthesizer>().All(voice => voice.ActiveVoiceCount == 0), "Closed hat did not stop the open hat voice.");

    // Ordinary open -> expressive closed must choke across both voice systems.
    service = NewSynth(); track = Guid.NewGuid();
    service.NoteOn(track, 9, 46, 100, Guid.NewGuid());
    service.Render(new float[512], new float[512]);
    var output2 = ((System.Collections.IDictionary)typeof(SoundFontPlaybackService).GetField("_trackOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!)[track]!;
    var child2 = output2.GetType().GetProperty("Service")!.GetValue(output2)!;
    var drumPlayers2 = (System.Collections.IDictionary)child2.GetType().GetField("_drumPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child2)!;
    var ordinaryOpenVoices = (Array)drumPlayers2[46]!.GetType().GetField("_voices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(drumPlayers2[46]!)!;
    service.NoteOnExpressive(track, 9, 42, 100, Guid.NewGuid(), -1);
    service.Render(new float[512], new float[512]);
    Check(ordinaryOpenVoices.Cast<MeltySynth.Synthesizer>().All(voice => voice.ActiveVoiceCount == 0), "Expressive closed hat did not stop the ordinary open hat voice.");

    // Expressive open -> ordinary closed must also cross paths.
    service = NewSynth(); track = Guid.NewGuid();
    service.NoteOnExpressive(track, 9, 46, 100, Guid.NewGuid(), -1);
    service.Render(new float[512], new float[512]);
    var output3 = ((System.Collections.IDictionary)typeof(SoundFontPlaybackService).GetField("_trackOutputs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!)[track]!;
    var child3 = output3.GetType().GetProperty("Service")!.GetValue(output3)!;
    var expressiveOpen = (System.Collections.IDictionary)child3.GetType().GetField("_expressiveVoices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(child3)!;
    Check(expressiveOpen.Count == 1, "Expressive open hat did not start before cross-path choke.");
    service.NoteOn(track, 9, 42, 100, Guid.NewGuid());
    service.Render(new float[512], new float[512]);
    Check(expressiveOpen.Count == 0, "Ordinary closed hat did not stop the expressive open hat voice.");
}

static void ConfiguredKitGain()
{
    var score = new ScoreDocument("Kit gain", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var a = score.AddTrack(new ScoreTrack("A", TrackKind.Percussion));
    var b = score.AddTrack(new ScoreTrack("B", TrackKind.Percussion));
    a.ConfigurePercussionPieces(new[] { new PercussionPiece("a", "Kick", 36) });
    b.ConfigurePercussionPieces(new[] { new PercussionPiece("b", "Kick", 36) { GainDb = -80 } });
    var section = score.Sections[0]; var measure = score.AddMeasure(section);
    foreach (var track in new[] { a, b })
    {
        var part = measure.GetTrackMeasure(track.Id)!; part.UsesTabEvents = true;
        part.TabEvents.Add(new TabEvent(1, 0, 36, 100, ScoreTime.Zero, new ScoreTime(1, 1)));
    }
    var reference = new ScorePlaybackClock(score, 48000); var modified = new ScorePlaybackClock(score, 48000);
    var synthA = NewSynth(); var synthB = NewSynth();
    reference.Render(synthA, new float[1000], new float[1000]); modified.Render(synthB, new float[1000], new float[1000]);
    a.PercussionPieces[0].GainDb = -6;
    modified.RefreshScore(score);
    var left = new float[4000]; var right = new float[4000]; var adjusted = new float[4000]; var other = new float[4000];
    reference.Render(synthA, left, right); modified.Render(synthB, adjusted, other);
    var expected = (Math.Pow(10, -6.0 / 20) + 0.0001) / 1.0001;
    AssertGain(left, adjusted, expected, "independent same-pitch kit gain after refresh");
}

static void AssertGain(float[] reference, float[] actual, double expected, string name)
{
    var energy = reference.Sum(sample => (double)sample * sample);
    var error = reference.Zip(actual, (source, value) => Math.Pow(value - source * expected, 2)).Sum();
    Check(energy > 1e-10 && error / energy < 1e-8, $"Incorrect rendered gain for {name}.");
}

static void NoteExpressionPreviewTimingRules()
{
    var score = new ScoreDocument("Preview Timing", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
    var timing = NoteExpressionPreviewTiming.GetFrameDurations(new ScoreTime(1, 8), score.ResolveTempoAt(ScoreTime.Zero), 48000);
    Check(timing.NoteFrames == 12000, $"An eighth note at 120 quarter BPM should last 12000 frames, got {timing.NoteFrames}.");
    Check(timing.BeatGapFrames == 24000, $"The preview gap should use the quarter-note reference beat, got {timing.BeatGapFrames}.");
    Check(NoteExpressionPreviewTiming.ShouldRepeat(true, false), "An enabled preview loop should repeat.");
    Check(!NoteExpressionPreviewTiming.ShouldRepeat(false, false), "A disabled preview loop should finish after its cycle.");
    Check(!NoteExpressionPreviewTiming.ShouldRepeat(true, true), "Disabling looping during playback should finish after the current cycle.");
    var silent = new NoteExpression { Kind = ExpressionKind.Velocity, Points = new() { new ExpressionPoint(0, 0) } };
    var faint = new NoteExpression { Kind = ExpressionKind.Velocity, Points = new() { new ExpressionPoint(0, 0.001) } };
    Check(NoteExpressionPreviewTiming.ApplyVelocity(80, new[] { silent }) == 0, "A zero attack multiplier should silence the preview.");
    Check(NoteExpressionPreviewTiming.ApplyVelocity(80, new[] { faint }) == 1, "A positive attack multiplier should retain the minimum MIDI velocity of one.");
}

static TrackMeasureVersion PlaybackVersion(ScoreDocument score, ScoreSection section, ScoreMeasure measure, ScoreTrack track, int pitch)
{
    var content = new TrackMeasure(track.Id);
    var voice = new Voice();
    content.Voices.Add(voice);
    voice.Beats.Add(new Beat(new RhythmicValue(4)));
    AddHit(voice, 0, pitch);
    return score.AddTrackMeasureVersion(section, measure, track, "Version " + pitch, content);
}

static int[] ActivePlaybackPitches(ScorePlaybackClock clock)
{
    var active = (System.Collections.IDictionary)typeof(ScorePlaybackClock).GetField("_activeNoteEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
    return active.Values.Cast<object>().Select(item => (int)item.GetType().GetProperty("Value")!.GetValue(item)!).OrderBy(value => value).ToArray();
}

static void TransitionLoopPlayback()
{
    var (score, track, section, measure, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var alternate = PlaybackVersion(score, section, measure, track, 38);
    score.SetTrackMeasureAlternation(section, measure, track, new[] { Guid.Empty, alternate.Id });
    var arrangement = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementLoop, null, new[] { section.Id });
    var clock = new ScorePlaybackClock(score, 48000, selection: arrangement) { LoopEnabled = true };
    var synth = NewSynth();
    clock.Render(synth, new float[1], new float[1]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 36 }), "First fallback did not play original only.");
    clock.RefreshScore(score);
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), "Rendered second loop did not advance after refresh.");
    clock.RefreshScore(score);
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 36 }), "Rendered third loop did not wrap fallback.");
    var fresh = new ScorePlaybackClock(score, 48000, selection: arrangement) { LoopEnabled = true };
    fresh.Render(NewSynth(), new float[1], new float[1]);
    Check(ActivePlaybackPitches(fresh).SequenceEqual(new[] { 36 }), "Fresh playback did not reset sequence.");
}

static void OrdinaryVersionSelection()
{
    var (score, track, section, measure, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var selected = PlaybackVersion(score, section, measure, track, 38);
    var ruleVersion = PlaybackVersion(score, section, measure, track, 40);
    score.SetTrackMeasureTransitionRule(section, measure, track, section.Id, ruleVersion.Id);
    score.SetTrackMeasureAlternation(section, measure, track, new[] { ruleVersion.Id, Guid.Empty });
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(measure.Id, track.Id)] = selected.Id });
    var modes = new[]
    {
        new PlaybackLoopSelection(PlaybackLoopMode.Score, null, Array.Empty<Guid>()),
        new PlaybackLoopSelection(PlaybackLoopMode.Sections, null, new[] { section.Id }),
        new PlaybackLoopSelection(PlaybackLoopMode.Measure, measure.Id, Array.Empty<Guid>())
    };
    foreach (var mode in modes)
    {
        var clock = new ScorePlaybackClock(score, 48000, selection: mode, versionSelection: snapshot);
        clock.Render(NewSynth(), new float[1], new float[1]);
        Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), $"{mode.Mode} playback did not honor the selected version.");
    }
    var original = new ScorePlaybackClock(score, 48000);
    original.Render(NewSynth(), new float[1], new float[1]);
    Check(ActivePlaybackPitches(original).SequenceEqual(new[] { 36 }), "Ordinary playback without a selected version did not use original content.");
}

static void TransitionArrangementPlayback()
{
    var (score, track, section, measure, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var second = score.AddSection("C", "C");
    var same = PlaybackVersion(score, section, measure, track, 40);
    var next = PlaybackVersion(score, section, measure, track, 42);
    var fallback = PlaybackVersion(score, section, measure, track, 38);
    score.SetTrackMeasureTransitionRule(section, measure, track, section.Id, same.Id);
    score.SetTrackMeasureTransitionRule(section, measure, track, second.Id, next.Id);
    score.SetTrackMeasureAlternation(section, measure, track, new[] { fallback.Id, Guid.Empty });
    var scope = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, new[] { section.Id, section.Id, second.Id, section.Id });
    var clock = new ScorePlaybackClock(score, 48000, selection: scope);
    var synth = NewSynth();
    clock.Render(synth, new float[1], new float[1]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 40 }), "Repeated A did not resolve next occurrence A.");
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 42 }), "Repeated A before C did not resolve C.");
    clock.Render(synth, new float[192000], new float[192000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), "Once end did not use first fallback after destination overrides.");
    var looping = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementLoop, null, scope.SectionIds));
    var loopSynth = NewSynth();
    looping.Render(loopSynth, new float[288001], new float[288001]);
    Check(ActivePlaybackPitches(looping).SequenceEqual(new[] { 40 }), "Arrangement loop end did not use wrap destination A.");
}

static void TransitionMeasureLoopAndSeek()
{
    var (score, track, section, measure, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var alternate = PlaybackVersion(score, section, measure, track, 38);
    var ruled = PlaybackVersion(score, section, measure, track, 42);
    score.SetTrackMeasureTransitionRule(section, measure, track, section.Id, ruled.Id);
    score.SetTrackMeasureAlternation(section, measure, track, new[] { alternate.Id, Guid.Empty });
    var scope = new PlaybackLoopSelection(PlaybackLoopMode.Measure, measure.Id, Array.Empty<Guid>());
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(measure.Id, track.Id)] = alternate.Id });
    var clock = new ScorePlaybackClock(score, 48000, startMeasureId: measure.Id, selection: scope, versionSelection: snapshot);
    var synth = NewSynth();
    clock.Render(synth, new float[1], new float[1]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), "Measure loop did not honor the editor-selected version.");
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), "Measure loop changed away from the editor-selected version.");
}

static void LiveVersionSelectionRefresh()
{
    var (score, track, section, first, _) = MakeTabScore();
    var original = first.GetTrackMeasure(track.Id)!;
    original.UsesTabEvents = true;
    original.TabEvents.Add(new TabEvent(1, 0, 64, 90, ScoreTime.Zero, new ScoreTime(3, 1)));
    original.TabEvents.Add(new TabEvent(2, 0, 65, 90, new ScoreTime(1, 4), new ScoreTime(1, 4)));
    var content = new TrackMeasure(track.Id) { UsesTabEvents = true };
    content.TabEvents.Add(new TabEvent(1, 0, 66, 90, ScoreTime.Zero, new ScoreTime(3, 1)));
    content.TabEvents.Add(new TabEvent(2, 0, 67, 90, new ScoreTime(1, 4), new ScoreTime(1, 4)));
    var version = score.AddTrackMeasureVersion(section, first, track, "Live", content);
    var clock = new ScorePlaybackClock(score, 48000);
    var synth = NewSynth();
    clock.Render(synth, new float[12001], new float[12001]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "Original note did not remain active before the selection change.");
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(first.Id, track.Id)] = version.Id });
    clock.RefreshScore(score, snapshot);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "Refreshing selection interrupted an already sounding note.");
    clock.Render(synth, new float[12000], new float[12000]);
    Check(ActivePlaybackPitches(clock).Order().SequenceEqual(new[] { 64, 67 }),
        $"The active measure did not use the new selected version for its next onset while preserving the original note end (active: {string.Join(',', ActivePlaybackPitches(clock))}).");
}

static void LiveCreatedVersionSelectionRefresh()
{
    var (score, track, section, first, _) = MakeTabScore();
    var original = first.GetTrackMeasure(track.Id)!;
    original.UsesTabEvents = true;
    original.TabEvents.Add(new TabEvent(1, 0, 64, 90, ScoreTime.Zero, new ScoreTime(3, 1)));
    original.TabEvents.Add(new TabEvent(2, 0, 65, 90, new ScoreTime(1, 4), new ScoreTime(1, 4)));
    var clock = new ScorePlaybackClock(score, 48000);
    var synth = NewSynth();
    clock.Render(synth, new float[12001], new float[12001]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "The original occurrence did not start before version creation.");

    var content = new TrackMeasure(track.Id) { UsesTabEvents = true };
    content.TabEvents.Add(new TabEvent(1, 0, 66, 90, ScoreTime.Zero, new ScoreTime(3, 1)));
    content.TabEvents.Add(new TabEvent(2, 0, 67, 90, new ScoreTime(1, 4), new ScoreTime(1, 4)));
    var version = score.AddTrackMeasureVersion(section, first, track, "Created while playing", content);
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(first.Id, track.Id)] = version.Id });
    clock.RefreshScore(score, snapshot);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "Creating/selecting a version interrupted the already sounding original note.");
    clock.Render(synth, new float[12000], new float[12000]);
    Check(ActivePlaybackPitches(clock).Order().SequenceEqual(new[] { 64, 67 }),
        $"A newly created selected version did not supply the next active-measure onset (active: {string.Join(',', ActivePlaybackPitches(clock))}).");
}

static void TransitionTabSustain()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    AddTab(first, track, 1, 64, new ScoreTime(3, 4), new ScoreTime(1, 1));
    AddTab(second, track, 1, 65, new ScoreTime(1, 4), new ScoreTime(1, 4));
    var content = new TrackMeasure(track.Id) { UsesTabEvents = true };
    content.TabEvents.Add(new TabEvent(1, 0, 66, 90, new ScoreTime(1, 2), new ScoreTime(1, 4)));
    var version = score.AddTrackMeasureVersion(section, second, track, "Late", content);
    score.SetTrackMeasureAlternation(section, second, track, new[] { version.Id, Guid.Empty });
    var arrangement = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementLoop, null, new[] { section.Id });
    var clock = new ScorePlaybackClock(score, 48000, selection: arrangement) { LoopEnabled = true };
    var synth = NewSynth();
    clock.Render(synth, new float[120001], new float[120001]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "Unselected original onset shortened the sustain.");
    clock.RefreshScore(score);
    clock.Render(synth, new float[24000], new float[24000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 66 }), "Selected late onset did not cut the original same-string sustain.");
    clock.Render(synth, new float[24000], new float[24000]);
    Check(ActivePlaybackPitches(clock).Length == 0, "Selected version retained a stuck note after its end.");
    clock.Render(synth, new float[144000], new float[144000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 65 }), "Second loop did not choose original and release the previous same-string sustain.");
}

static void TransitionLinkedDestination()
{
    var (score, track, section, first, second) = MakeTabScore();
    AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(1, 4));
    score.SetTrackMeasureRepeatCount(section, track, 0, 1);
    var unique = PlaybackVersion(score, section, second, track, 66);
    score.SetTrackMeasureAlternation(section, second, track, new[] { unique.Id, Guid.Empty });
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(second.Id, track.Id)] = unique.Id });
    var clock = new ScorePlaybackClock(score, 48000, startMeasureId: second.Id, versionSelection: snapshot);
    var synth = NewSynth();
    clock.Render(synth, new float[1], new float[1]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 66 }), "Late start did not start destination's own fallback at first entry.");
    var normal = new ScorePlaybackClock(score, 48000, versionSelection: snapshot) { LoopEnabled = true };
    var normalSynth = NewSynth();
    normal.Render(normalSynth, new float[1], new float[1]);
    Check(ActivePlaybackPitches(normal).SequenceEqual(new[] { 64 }), "Destination version changed the source slot.");
    normal.Render(normalSynth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(normal).SequenceEqual(new[] { 66 }), "Linked destination did not play its independent version.");
    normal.Render(normalSynth, new float[192000], new float[192000]);
    Check(ActivePlaybackPitches(normal).SequenceEqual(new[] { 66 }), "Linked destination did not keep the editor-selected version.");
}

static void TransitionCountIn()
{
    var (score, track, section, measure, voice) = MakeScore();
    AddHit(voice, 0, 36);
    var version = PlaybackVersion(score, section, measure, track, 38);
    score.SetTrackMeasureAlternation(section, measure, track, new[] { version.Id, Guid.Empty });
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(measure.Id, track.Id)] = version.Id });
    var clock = new ScorePlaybackClock(score, 48000, countInEnabled: true, countInEveryLoop: true,
        versionSelection: snapshot) { LoopEnabled = true };
    var synth = NewSynth();
    clock.Render(synth, new float[96001], new float[96001]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), "Count-in delayed the editor-selected version.");
    clock.RefreshScore(score, snapshot);
    clock.Render(synth, new float[192000], new float[192000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 38 }), "Repeating count-in or refresh lost the editor-selected version.");
}

static void TransitionRepeatedSustainRelease()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.RemoveMeasure(section, second);
    AddTab(first, track, 1, 64, ScoreTime.Zero, new ScoreTime(3, 1));
    var silent = score.AddTrackMeasureVersion(section, first, track, "Silent", new TrackMeasure(track.Id) { UsesTabEvents = true });
    score.SetTrackMeasureAlternation(section, first, track, new[] { Guid.Empty, silent.Id });
    var clock = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null, new[] { section.Id, section.Id, section.Id }));
    var synth = NewSynth();
    clock.Render(synth, new float[1], new float[1]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "Long original sustain did not begin.");
    clock.RefreshScore(score);
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).Length == 0, "Long sustain leaked across the silent repeated occurrence.");
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 64 }), "Silent fallback did not consume one sequence entry.");
    clock.Render(synth, new float[96000], new float[96000]);
    Check(ActivePlaybackPitches(clock).Length == 0 && !clock.IsPlaying, "Long selected sustain remained active after arrangement end.");
}

static void TransitionNonFinalScope()
{
    var (score, track, section, first, voice) = MakeScore();
    AddHit(voice, 0, 36);
    score.AddMeasure(section);
    var skipped = score.AddSection("B", "B");
    var destination = score.AddSection("C", "C");
    var wrong = PlaybackVersion(score, section, first, track, 40);
    var correct = PlaybackVersion(score, section, first, track, 42);
    score.SetTrackMeasureTransitionRule(section, first, track, skipped.Id, wrong.Id);
    score.SetTrackMeasureTransitionRule(section, first, track, destination.Id, correct.Id);
    var clock = new ScorePlaybackClock(score, 48000, selection: new PlaybackLoopSelection(PlaybackLoopMode.Sections, null, new[] { section.Id, destination.Id }));
    clock.Render(NewSynth(), new float[1], new float[1]);
    Check(ActivePlaybackPitches(clock).SequenceEqual(new[] { 36 }), "Section playback applied a contextual transition instead of original content.");
    var ordinary = new ScorePlaybackClock(score, 48000);
    ordinary.Render(NewSynth(), new float[1], new float[1]);
    Check(ActivePlaybackPitches(ordinary).SequenceEqual(new[] { 36 }), "Score playback applied a contextual transition instead of original content.");
}

static void TransitionSeekSkipsOverflow()
{
    var (score, track, section, first, second) = MakeTabScore();
    score.SetTrackPatternLength(section, track, 2);
    var overflow = AddTab(first, track, 1, 64, new ScoreTime(5, 4), new ScoreTime(1, 4));
    overflow.AllowsBarOverflow = true;
    var silent = score.AddTrackMeasureVersion(section, first, track, "Silent", new TrackMeasure(track.Id) { UsesTabEvents = true });
    score.SetTrackMeasureAlternation(section, first, track, new[] { Guid.Empty, silent.Id });
    var snapshot = new PlaybackVersionSelection(new Dictionary<(Guid MeasureId, Guid TrackId), Guid>
        { [(first.Id, track.Id)] = silent.Id });
    var clock = new ScorePlaybackClock(score, 48000, startMeasureId: second.Id, versionSelection: snapshot);
    clock.Render(NewSynth(), new float[24001], new float[24001]);
    Check(ActivePlaybackPitches(clock).Length == 0, "A skipped slot's default original played an overflowed note after seek.");
}

readonly record struct EventView(long Frame, string Kind, int Value, int SecondaryValue);

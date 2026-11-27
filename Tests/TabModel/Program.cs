using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenTab.Music;
using OpenTab.Music.Persistence;

var tests = new (string Name, Action Run)[]
{
    ("explicit repeats resolve in source order", ExplicitRepeatMapping),
    ("section shrink clips repeats without hiding sources and round-trips", ShrinkAndRoundTrip),
    ("lead switching keeps dormant repeat metadata", LeadSwitchRoundTrip),
    ("section lead assignments are independent and persist", IndependentSectionLeadsRoundTrip),
    ("track title summaries reflect focused section and track kind", TrackTitleSummaries),
    ("adding an empty source preserves the shared lead grid", AppendEmptySource),
    ("copy source appends independent events with fresh identities", CopySource),
    ("append guard preserves hidden authored data", HiddenDataGuard),
    ("track removal clears parts and pattern metadata and preserves lead invariants", RemoveTrack),
    ("track removal rejects unrelated tracks without mutation", RejectUnknownTrack),
    ("version two migration ignores modulo counts but retains parts", LegacyMigration),
    ("destination versions, per-track rules, alternation and copy identities round-trip", TransitionModelTests.Run),
    ("measure insertion and duplication preserve shared score data", MeasureInsertionTests.Run),
    ("measure editing snap persists, duplicates and falls back for old scores", MeasureSnapTests.Run),
    ("percussion kits persist lane metadata and fixed-pitch events", PercussionKitRoundTrip),
    ("new score beats use track-specific default intensity", TrackSpecificBeatDefaults),
    ("track order moves editable rows, persists and remains undoable", TrackOrdering),
    ("percussion lane removal confirms hits and compacts event rows", RemovePercussionPiece),
    ("configured percussion tracks use linked repeat APIs", PercussionLinkedRepeats),
    ("legacy percussion remains unconfigured after round trip", LegacyPercussionRemainsLegacy)
    ,("audio assets and placements persist as metadata with audio-only ruler", AudioMetadataRoundTrip),
    ("audio-only ruler lifecycle preserves section grids", AudioRulerLifecycle),
    ("audio imports and portable saves survive missing source files", AudioStorageTests.Run)
};

foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS {name}");
}

LegacyTabTests.Run();
FullTabTests.Run();
TabRetuningTests.Run();
TabSelectionEditTests.Run();
TabTupletTests.Run();
TabVelocityEditingTests.Run();
TrackColorTests.Run();
ClipboardTests.Run();
TabEditorSnapshotTests.Run();
TabPaginationGeometryTests.Run();
TrackSettingsDialogTests.Run();
TrackPedalTests.Run();
ScoreHistoryTests.Run();
TabDurationResizeTests.Run();
NotePitchSelectorTests.Run();
SoundVariationTests.Run();
TabArticulationTests.Run();
NoteExpressionStepTests.Run();
SectionSelectionTests.Run();
ArrangementTests.Run();
RecentProjectsTest();

static void RecentProjectsTest()
{
    var directory = Path.Combine(Directory.GetCurrentDirectory(), ".codex-temp-godot", "recent-" + Guid.NewGuid());
    Directory.CreateDirectory(directory);
    try
    {
        var preferences = Path.Combine(directory, "recent.json");
        var recent = new RecentScorePaths(preferences, message => throw new Exception(message));
        for (var i = 0; i < 12; i++) recent.Record(Path.Combine(directory, $"{i}.otab"));
        var repeated = Path.Combine(directory, "5.otab");
        recent.Record(repeated);
        var restored = new RecentScorePaths(preferences, message => throw new Exception(message));
        if (restored.Paths.Count != 10 || restored.Paths[0] != repeated ||
            restored.Paths.Count(path => path == repeated) != 1 ||
            restored.Paths.Contains(Path.Combine(directory, "0.otab")))
            throw new Exception("Recent projects must persist in most-recent order, deduplicate and retain at most ten paths.");
        Console.WriteLine("PASS recent projects ordering, deduplication, limit and persistence");
    }
    finally { Directory.Delete(directory, true); }
}

static void AudioMetadataRoundTrip()
{
    var score = new ScoreDocument("Audio only", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    var audio = score.AddTrack(new ScoreTrack("Reference", TrackKind.Audio));
    var section = score.Sections[0];
    var ruler = score.Tracks.Single(track => track.Kind == TrackKind.Ruler);
    Assert(section.LeadTrackId == ruler.Id, "audio-only score should use the silent ruler as its lead");
    score.AddMeasure(section);
    var asset = new AudioAsset("reference.wav", new string('a', 64), 48000, 2, 48000, "audio-cache/abc123");
    score.AudioAssets.Add(asset);
    section.AudioClips.Add(new AudioClip(asset.Id, audio.Id, new ScoreTime(1, 4), 240, 12000));
    var arrangement = ArrangementEditing.Add(score);
    arrangement.AudioClips.Add(new AudioClip(asset.Id, audio.Id, new ScoreTime(3, 2), 0, 48000));

    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Assert(restored.Tracks.Any(track => track.Kind == TrackKind.Audio) && restored.Tracks.Any(track => track.Kind == TrackKind.Ruler), "track kinds were not persisted");
    Assert(restored.Sections[0].LeadTrackId == ruler.Id, "ruler lead identity changed on restore");
    Assert(restored.AudioAssets.Single().CachePath == asset.CachePath, "stable cache reference was not persisted");
    Assert(restored.Sections[0].AudioClips.Single().Start == new ScoreTime(1, 4), "section clip timing was not persisted");
    Assert(restored.Arrangements.Single().AudioClips.Single().SourceOffsetFrames == 0, "arrangement clip was not persisted");
    Assert(ScoreFile.Deserialize(ScoreFile.Serialize(restored)).Sections[0].AudioClips.Count == 1, "audio metadata snapshots should round-trip repeatedly");
}

static void AudioRulerLifecycle()
{
    var score = new ScoreDocument("Ruler lifecycle", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    var lead = score.AddTrack(new ScoreTrack("Lead", TrackKind.Keyboard));
    var section = score.Sections[0];
    score.AddMeasure(section);
    score.AddMeasure(section);
    var audio = score.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var originalLength = section.Measures.Count;
    score.RemoveTrack(lead);
    var ruler = score.Tracks.Single(track => track.Kind == TrackKind.Ruler);
    Assert(section.LeadTrackId == ruler.Id && section.Measures.Count == originalLength, "removing the last notation lead should establish a ruler without changing section length");
    Assert(section.Measures.All(measure => measure.GetTrackMeasure(ruler.Id) is not null), "ruler must have structural parts for all existing measures");
    var rulerRemovalRejected = false;
    try { score.RemoveTrack(ruler); }
    catch (InvalidOperationException) { rulerRemovalRejected = true; }
    Assert(rulerRemovalRejected, "users must not directly remove the managed ruler");

    var notation = score.AddTrack(new ScoreTrack("Notation", TrackKind.Guitar));
    Assert(!score.Tracks.Any(track => track.Kind == TrackKind.Ruler), "adding notation should remove the managed ruler");
    Assert(section.LeadTrackId == notation.Id && section.Measures.Count == originalLength, "notation should replace ruler lead while preserving section length");
    score.RemoveTrack(notation);
    Assert(score.Tracks.Any(track => track.Kind == TrackKind.Ruler) && section.LeadTrackId is { } leadId && score.Tracks.Single(track => track.Id == leadId).Kind == TrackKind.Ruler,
        "removing the last notation track should restore the ruler for remaining audio");
    score.RemoveTrack(audio);
    Assert(!score.Tracks.Any(track => track.Kind == TrackKind.Ruler) && section.LeadTrackId is null, "removing the final audio track should remove the ruler");

    var audioOnly = new ScoreDocument("Audio then notation", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    audioOnly.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var audioSection = audioOnly.Sections[0];
    Assert(audioSection.Measures.Count == 1, "an audio-only ruler must create its first measure");
    try { audioOnly.RemoveMeasure(audioSection, audioSection.Measures[0]); throw new Exception("the last ruler measure was removed"); }
    catch (InvalidOperationException) { }
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(audioOnly));
    Assert(restored.Tracks.Count(track => track.Kind == TrackKind.Ruler) == 1 && restored.Sections[0].LeadTrackId == restored.Tracks.Single(track => track.Kind == TrackKind.Ruler).Id,
        "loading audio-only score must preserve its ruler without creating duplicates");
    var replacement = restored.RemoveSection(restored.Sections[0]);
    Assert(replacement.Measures.Count == 1, "replacing the last audio-only section must create a ruler measure");
    var restoredNotation = restored.AddTrack(new ScoreTrack("Notation", TrackKind.Keyboard));
    Assert(!restored.Tracks.Any(track => track.Kind == TrackKind.Ruler) && restored.Sections[0].LeadTrackId == restoredNotation.Id,
        "loaded ruler should be safely replaced when notation is added");
}

static (ScoreDocument Score, ScoreSection Section, ScoreTrack Lead, ScoreTrack Melody, ScoreTrack Drums) CreateScore(int measureCount = 4)
{
    var score = new ScoreDocument("Model regression", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    var lead = score.AddTrack(new ScoreTrack("Lead", TrackKind.Keyboard));
    var melody = score.AddTrack(new ScoreTrack("Melody", TrackKind.Guitar));
    var drums = score.AddTrack(new ScoreTrack("Drums", TrackKind.Percussion));
    var section = score.Sections[0];
    score.AddMeasure(section);
    for (var index = 1; index < measureCount; index++) score.AddMeasure(section);
    score.SetTrackPatternLength(section, melody, Math.Min(2, measureCount));
    return (score, section, lead, melody, drums);
}

static void ExplicitRepeatMapping()
{
    var (score, section, _, melody, _) = CreateScore();
    Assert(section.Measures[0].GetTrackMeasure(melody.Id)!.RepeatCount == 0, "new source repeat count should default to zero");
    var first = section.Measures[0].GetTrackMeasure(melody.Id)!;
    var second = section.Measures[1].GetTrackMeasure(melody.Id)!;
    first.Voices[0].Beats[0].AddNote(new ScoreNote(60, 90));
    second.Voices[0].Beats[0].AddNote(new ScoreNote(62, 90));
    score.SetTrackMeasureRepeatCount(section, melody, 0, 1);

    AssertOccurrence(score.GetNonPercussionOccurrence(section, melody, 0), 0, false, first);
    AssertOccurrence(score.GetNonPercussionOccurrence(section, melody, 1), 0, true, first);
    AssertOccurrence(score.GetNonPercussionOccurrence(section, melody, 2), 1, false, second);
    Assert(score.GetNonPercussionOccurrence(section, melody, 3).SourceTrackMeasure is null, "last destination should be unfilled");
}

static void ShrinkAndRoundTrip()
{
    var (score, section, _, melody, _) = CreateScore();
    score.SetTrackMeasureRepeatCount(section, melody, 0, 2);
    score.RemoveMeasure(section, section.Measures[^1]);
    AssertOccurrence(score.GetNonPercussionOccurrence(section, melody, 0), 0, false, section.Measures[0].GetTrackMeasure(melody.Id)!);
    AssertOccurrence(score.GetNonPercussionOccurrence(section, melody, 1), 0, true, section.Measures[0].GetTrackMeasure(melody.Id)!);
    AssertOccurrence(score.GetNonPercussionOccurrence(section, melody, 2), 1, false, section.Measures[1].GetTrackMeasure(melody.Id)!);
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Assert(restored.Sections[0].Measures[0].GetTrackMeasure(melody.Id)!.RepeatCount == 2, "repeat count was not persisted");
    Assert(restored.GetTrackPatternLength(restored.Sections[0], restored.Tracks[1]) == 2, "source count was not retained");
}

static void LeadSwitchRoundTrip()
{
    var (score, section, _, melody, _) = CreateScore();
    score.SetTrackMeasureRepeatCount(section, melody, 0, 1);
    score.SetLeadTrack(section, melody);
    Assert(score.GetNonPercussionOccurrence(section, melody, 0).SourceTrackMeasure is null, "new lead should not resolve through linked-repeat path");
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restoredMelody = restored.Tracks.Single(track => track.Id == melody.Id);
    Assert(restored.Sections[0].Measures[0].GetTrackMeasure(melody.Id)!.RepeatCount == 1, "dormant lead repeat metadata was lost");
    Assert(restored.GetNonPercussionOccurrence(restored.Sections[0], restoredMelody, 0).SourceTrackMeasure is null, "restored lead should remain on lead path");
}

static void IndependentSectionLeadsRoundTrip()
{
    var (score, firstSection, firstLead, melody, drums) = CreateScore(2);
    var secondSection = score.AddSection("B", "Second");
    score.AddMeasure(secondSection);
    score.SetLeadTrack(secondSection, drums);

    Assert(firstSection.LeadTrackId == firstLead.Id && secondSection.LeadTrackId == drums.Id,
        "assigning the second section's lead changed the first section");

    score.SetLeadTrack(firstSection, melody);
    Assert(firstSection.LeadTrackId == melody.Id && secondSection.LeadTrackId == drums.Id,
        "changing the first section's lead changed the second section");

    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restoredFirst = restored.Sections[0];
    var restoredSecond = restored.Sections[1];
    Assert(restoredFirst.LeadTrackId == melody.Id && restoredSecond.LeadTrackId == drums.Id,
        "independent section lead assignments changed during persistence round trip");

    var restoredFirstLead = restored.Tracks.Single(track => track.Id == firstLead.Id);
    restored.SetLeadTrack(restoredFirst, restoredFirstLead);
    Assert(restoredFirst.LeadTrackId == firstLead.Id && restoredSecond.LeadTrackId == drums.Id,
        "changing a restored section's lead changed its neighbor");
}

static void TrackTitleSummaries()
{
    var (score, first, lead, melody, drums) = CreateScore();
    first.Name = "A";
    var second = score.AddSection("B", "Second");
    score.SetLeadTrack(second, drums);
    Assert(TrackTitleSummary.Format(score, first, lead).Contains("Section A · Lead · 4 measures"), "lead title lost section or measure count");
    Assert(TrackTitleSummary.Format(score, first, melody).Contains("2 source / 4 measures"), "melody source count is incorrect");
    Assert(TrackTitleSummary.Format(score, second, drums).Contains("Section B · Lead · 1 measure"), "percussion lead or singular measure count is incorrect");
    Assert(!TrackTitleSummary.Format(score, second, lead).Contains("· Lead ·"), "title used another section's lead");
    melody.Name = "Renamed";
    Assert(TrackTitleSummary.Format(score, first, melody).StartsWith("Renamed ·"), "track rename is not reflected");
    var audio = score.AddTrack(new ScoreTrack("Recording", TrackKind.Audio));
    var asset = new AudioAsset("reference.wav", new string('a', 64), 48000, 2, 48000, "audio-cache/abc123");
    score.AudioAssets.Add(asset);
    first.AudioClips.Add(new AudioClip(asset.Id, audio.Id, new ScoreTime(0, 1), 0, 100));
    Assert(TrackTitleSummary.Format(score, first, audio).Contains("4 measures · 1 clip"), "audio title must count focused-section clips");
    Assert(TrackTitleSummary.Format(score, second, audio).Contains("1 measure · 0 clips"), "audio title must follow focused section");
}
static void CopySource()
{
    var (score, section, _, melody, _) = CreateScore();
    var original = section.Measures[0].GetTrackMeasure(melody.Id)!;
    var originalBeat = original.Voices[0].Beats[0];
    originalBeat.Intensity = 4;
    originalBeat.PitchBendSemitones = 1.25m;
    originalBeat.AddNote(new ScoreNote(65, 100));
    score.AppendTrackMeasureSource(section, melody, 0);
    var copy = section.Measures[2].GetTrackMeasure(melody.Id)!;
    var copiedBeat = copy.Voices[0].Beats[0];
    Assert(copy.RepeatCount == 0, "copy should start with zero repeats");
    Assert(copy.Voices[0].Id != original.Voices[0].Id && copiedBeat.Id != originalBeat.Id, "voice/beat identities were reused");
    Assert(copiedBeat.Notes[0].Id != originalBeat.Notes[0].Id, "note identity was reused");
    Assert(copiedBeat.Intensity == 4 && copiedBeat.PitchBendSemitones == 1.25m && copiedBeat.Notes[0].MidiPitch == 65,
        "copied beat state was incomplete");

    var (tabScore, tabSection, _, guitar, _) = CreateScore();
    guitar.ConfigureTab(new[] { 64, 59, 55, 50, 45, 40 });
    var tabSource = tabSection.Measures[0].GetTrackMeasure(guitar.Id)!;
    var note = TabEventEditing.Add(tabSource, guitar, 1, 3, new ScoreTime(1, 8), new ScoreTime(3, 8), 99);
    tabScore.AppendTrackMeasureSource(tabSection, guitar, 0);
    var tabCopy = tabSection.Measures[2].GetTrackMeasure(guitar.Id)!;
    var copiedNote = tabCopy.TabEvents.Single();
    Assert(tabCopy.UsesTabEvents && copiedNote.Id != note.Id && copiedNote.StringNumber == 1 && copiedNote.Fret == 3 &&
        copiedNote.MidiPitch == note.MidiPitch && copiedNote.Onset == note.Onset && copiedNote.Duration == note.Duration && copiedNote.Velocity == 99,
        "A copied tablature note lost its state or reused its identity");
    copiedNote.Fret = 5;
    Assert(note.Fret == 3, "Editing a copied note changed its original");
}

static void AppendEmptySource()
{
    var (score, section, lead, melody, drums) = CreateScore();
    score.SetTrackPatternLength(section, melody, 1);
    var ids = section.Measures.Select(measure => measure.Id).ToArray();
    var drumParts = section.Measures.Select(measure => measure.GetTrackMeasure(drums.Id)).ToArray();
    var source = section.Measures[0].GetTrackMeasure(melody.Id)!;
    source.Voices[0].Beats[0].AddNote(new ScoreNote(60, 90));
    score.SetTrackMeasureRepeatCount(section, melody, 0, 1);
    score.AppendTrackMeasureSource(section, melody);
    var added = section.Measures[1].GetTrackMeasure(melody.Id)!;
    Assert(score.GetTrackPatternLength(section, melody) == 2 && added.RepeatCount == 0 &&
        added.Voices.SelectMany(voice => voice.Beats).All(beat => beat.Notes.Count == 0), "The appended source was not empty.");
    Assert(score.GetNonPercussionOccurrence(section, melody, 2).SourceTrackMeasure == added,
        "The appended source was not positioned after the repeat.");
    Assert(section.LeadTrackId == lead.Id && ids.SequenceEqual(section.Measures.Select(measure => measure.Id)) &&
        drumParts.SequenceEqual(section.Measures.Select(measure => measure.GetTrackMeasure(drums.Id))),
        "Appending changed the lead grid or percussion parts.");
}

static void HiddenDataGuard()
{
    var (score, section, _, melody, _) = CreateScore();
    var hidden = section.Measures[2].GetTrackMeasure(melody.Id)!;
    hidden.Voices[0].Beats[0].AddNote(new ScoreNote(77, 88));
    var hiddenId = hidden.Voices[0].Beats[0].Notes[0].Id;
    try
    {
        score.AppendTrackMeasureSource(section, melody);
        throw new InvalidOperationException("Expected hidden authored part guard to reject append.");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains("hidden authored data", StringComparison.Ordinal))
    {
        Assert(hidden.Voices[0].Beats[0].Notes.Single().Id == hiddenId, "hidden data changed during rejected append");
    }
}

static void LegacyMigration()
{
    var (score, section, _, melody, _) = CreateScore();
    score.SetTrackMeasureRepeatCount(section, melody, 0, 1);
    var legacyBytes = RewriteAsVersionTwo(ScoreFile.Serialize(score));
    var restored = ScoreFile.Deserialize(legacyBytes);
    Assert(restored.GetTrackPatternLength(restored.Sections[0], restored.Tracks[1]) == 2, "legacy pattern/source count was not retained");
    Assert(restored.Sections[0].Measures[0].GetTrackMeasure(melody.Id)!.RepeatCount == 0, "v2 migration should default explicit repeats to zero");
    Assert(restored.Sections[0].Measures[0].GetTrackMeasure(melody.Id)!.Voices[0].Beats.Count == 4, "legacy part content was not retained");
}

static void PercussionKitRoundTrip()
{
    var (score, section, _, _, drums) = CreateScore();
    var kick = drums.AddPercussionPiece("kick", "Kick", 36);
    var snare = drums.AddPercussionPiece("snare", "Snare", 38);
    snare.IsMuted = true;
    snare.GainDb = -4.5;
    var part = section.Measures[0].GetTrackMeasure(drums.Id)!;
    var hit = TabEventEditing.Add(part, drums, 2, 0, new ScoreTime(1, 4), new ScoreTime(1, 8), 111);
    Assert(hit.MidiPitch == 38 && hit.Fret == 0, "percussion event did not use its lane fixed pitch");
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restoredDrums = restored.Tracks.Single(track => track.Id == drums.Id);
    var restoredPart = restored.Sections[0].Measures[0].GetTrackMeasure(drums.Id)!;
    Assert(restoredDrums.HasConfiguredPercussionKit && restoredDrums.PercussionPieces.Count == 2 &&
        restoredDrums.PercussionPieces[0].Id == kick.Id && restoredDrums.PercussionPieces[1].Name == "Snare" &&
        restoredDrums.PercussionPieces[1].IsMuted && restoredDrums.PercussionPieces[1].GainDb == -4.5,
        "kit metadata did not round-trip");
    Assert(restoredPart.UsesTabEvents && restoredPart.TabEvents.Single().MidiPitch == 38 &&
        restoredPart.TabEvents.Single().StringNumber == 2, "percussion tab event did not round-trip");

    var clip = TabClipboard.Copy(restoredDrums, restoredPart, new[] { restoredPart.TabEvents.Single() }, 2, new ScoreTime(1, 4));
    var destination = restored.Sections[0].Measures[1].GetTrackMeasure(restoredDrums.Id)!;
    var pasted = clip.PlanPaste(restoredDrums, destination, 1, new ScoreTime(1, 2)).Apply().Single();
    Assert(pasted.Fret == 0 && pasted.MidiPitch == 36 && pasted.StringNumber == 1, "percussion clipboard did not remap to target lane pitch");
    try
    {
        TabEventEditing.Add(destination, restoredDrums, 1, 1, new ScoreTime(0, 1), new ScoreTime(1, 8));
        throw new InvalidOperationException("Expected nonzero percussion fret to be rejected.");
    }
    catch (ArgumentOutOfRangeException) { }
}

static void RemovePercussionPiece()
{
    var (score, section, _, _, drums) = CreateScore();
    drums.AddPercussionPiece("kick", "Kick", 36);
    drums.AddPercussionPiece("snare", "Snare", 38);
    drums.AddPercussionPiece("hat", "Hat", 42);
    var parts = section.Measures.Select(measure => measure.GetTrackMeasure(drums.Id)!).ToArray();
    var first = TabEventEditing.Add(parts[0], drums, 1, 0, new ScoreTime(0, 1), new ScoreTime(1, 8));
    Assert(first.Intensity == 4 && first.Velocity == Beat.MidiVelocityForIntensity(4), "New percussion hits must default to velocity level 4.");
    var removed = TabEventEditing.Add(parts[0], drums, 2, 0, new ScoreTime(1, 4), new ScoreTime(1, 8));
    var shifted = TabEventEditing.Add(parts[1], drums, 3, 0, new ScoreTime(1, 2), new ScoreTime(1, 8));
    var emptyVersionPart = new TrackMeasure(drums.Id) { UsesTabEvents = true };
    var version = score.AddTrackMeasureVersion(section, section.Measures[0], drums, "Fill", emptyVersionPart);
    var versionKick = TabEventEditing.Add(version.Content, drums, 1, 0, ScoreTime.Zero, new ScoreTime(1, 8));
    var versionHit = TabEventEditing.Add(version.Content, drums, 2, 0, new ScoreTime(1, 2), new ScoreTime(1, 8));
    var versionShift = TabEventEditing.Add(version.Content, drums, 3, 0, new ScoreTime(3, 4), new ScoreTime(1, 8));
    var allParts = parts.Append(version.Content).ToArray();
    Assert(!drums.RemovePercussionPiece("snare", allParts), "used lane was removed without confirmation");
    Assert(drums.PercussionPieces.Count == 3 && parts[0].TabEvents.Contains(removed), "rejected deletion mutated the kit or events");
    Assert(drums.RemovePercussionPiece("snare", allParts, confirmDeleteHits: true), "confirmed lane removal failed");
    Assert(drums.PercussionPieces.Select(piece => piece.Id).SequenceEqual(new[] { "kick", "hat" }) &&
        parts[0].TabEvents.SequenceEqual(new[] { first }) && parts[1].TabEvents.Single() == shifted &&
        shifted.StringNumber == 2 && shifted.MidiPitch == 42,
        "lane removal did not delete its hits and compact later event rows while preserving pitch");
    Assert(!version.Content.TabEvents.Contains(versionHit) && version.Content.TabEvents.Count == 2 &&
        version.Content.TabEvents.Contains(versionKick) && versionKick.StringNumber == 1 && versionKick.MidiPitch == 36 &&
        version.Content.TabEvents.Contains(versionShift) && versionShift.StringNumber == 2 && versionShift.MidiPitch == 42,
        "percussion lane deletion did not delete and compact named-version hits");
    Assert(drums.TabTuningMidiPitches[shifted.StringNumber - 1] == shifted.MidiPitch,
        "shifted event no longer matches its original piece pitch");
    Assert(drums.RemovePercussionPiece("hat", allParts, confirmDeleteHits: true), "confirmed last lane removal failed");
    Assert(drums.RemovePercussionPiece("kick", allParts, confirmDeleteHits: true), "confirmed final lane removal failed");
    var emptyKit = ScoreFile.Deserialize(ScoreFile.Serialize(score)).Tracks.Single(track => track.Id == drums.Id);
    Assert(emptyKit.HasConfiguredPercussionKit && emptyKit.PercussionPieces.Count == 0,
        "empty configured kit was not distinguished from legacy percussion after round trip");
}

static void TrackSpecificBeatDefaults()
{
    var score = new ScoreDocument("Track defaults", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    var guitar = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
    var bass = score.AddTrack(new ScoreTrack("Bass", TrackKind.Bass));
    var percussion = score.AddTrack(new ScoreTrack("Drums", TrackKind.Percussion));
    percussion.AddPercussionPiece("kick", "Kick", 36);
    var section = score.Sections[0];
    foreach (var measure in section.Measures)
    {
        Assert(measure.GetTrackMeasure(guitar.Id)!.Voices[0].Beats.All(beat => beat.Intensity == 3), "new guitar beats should default to intensity 3");
        Assert(measure.GetTrackMeasure(bass.Id)!.Voices[0].Beats.All(beat => beat.Intensity == 4), "new bass beats should default to intensity 4");
        Assert(measure.GetTrackMeasure(percussion.Id)!.Voices[0].Beats.All(beat => beat.Intensity == 4), "new percussion beats should default to intensity 4");
    }
    var added = score.AddMeasure(section);
    Assert(added.GetTrackMeasure(guitar.Id)!.Voices[0].Beats.All(beat => beat.Intensity == 3) &&
        added.GetTrackMeasure(bass.Id)!.Voices[0].Beats.All(beat => beat.Intensity == 4) &&
        added.GetTrackMeasure(percussion.Id)!.Voices[0].Beats.All(beat => beat.Intensity == 4),
        "newly added measures should use the track-specific defaults");

    var emptyMeasure = new ScoreMeasure();
    var emptyVoice = new Voice();
    var placeholder = BeatEditing.CreateIncompleteVoicePlaceholder(emptyMeasure, emptyVoice, new TimeSignature(4, 4), defaultIntensity: 4);
    Assert(placeholder?.Intensity == 4, "new incomplete-voice beats should accept the owning track's default intensity");
    emptyVoice.Beats.Add(new Beat(new RhythmicValue(4)) { Intensity = 3 });
    var repeated = BeatEditing.CreateIncompleteVoicePlaceholder(emptyMeasure, emptyVoice, new TimeSignature(4, 4), repeatLastValue: true, defaultIntensity: 4);
    Assert(repeated?.Intensity == 3, "repeat-last placeholder should preserve the copied authored intensity");

    var legacyScore = new ScoreDocument("Legacy silent defaults", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    legacyScore.AddTrack(new ScoreTrack("Lead", TrackKind.Guitar));
    var legacyBass = legacyScore.AddTrack(new ScoreTrack("Bass", TrackKind.Bass));
    var legacySection = legacyScore.Sections[0];
    legacyScore.AddMeasure(legacySection);
    legacyScore.AddMeasure(legacySection);
    legacyScore.SetTrackPatternLength(legacySection, legacyBass, 1);
    foreach (var beat in legacySection.Measures[1].GetTrackMeasure(legacyBass.Id)!.Voices[0].Beats)
        beat.Intensity = Beat.DefaultIntensity;
    legacyScore.AppendTrackMeasureSource(legacySection, legacyBass);
    Assert(legacyScore.GetTrackPatternLength(legacySection, legacyBass) == 2,
        "appending should still accept a legacy silent bass part with the former intensity 3 default");
}

static void TrackOrdering()
{
    var score = new ScoreDocument("Track order", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    var audio = score.AddTrack(new ScoreTrack("Audio", TrackKind.Audio));
    var guitar = score.AddTrack(new ScoreTrack("Guitar", TrackKind.Guitar));
    var drums = score.AddTrack(new ScoreTrack("Drums", TrackKind.Percussion));
    Assert(score.MoveTrack(drums, 0), "moving a track to a different position should report a change");
    Assert(score.Tracks.Select(track => track.Id).SequenceEqual(new[] { drums.Id, audio.Id, guitar.Id }), "mixed editable tracks should reorder in one canonical list");
    Assert(!score.MoveTrack(drums, 0), "moving to the current position should be a no-op");
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Assert(restored.Tracks.Select(track => track.Name).SequenceEqual(new[] { "Drums", "Audio", "Guitar" }), "portable score serialization should preserve track order");

    var history = new ScoreHistory();
    history.Reset(ScoreFile.Serialize(score));
    score.MoveTrack(guitar, 0);
    history.Record(ScoreFile.Serialize(score));
    Assert(history.TryUndo(out var priorState), "track reordering should be captured by score history");
    Assert(ScoreFile.Deserialize(priorState).Tracks.Select(track => track.Name).SequenceEqual(new[] { "Drums", "Audio", "Guitar" }), "undo should restore the previous order");
    Assert(history.TryRedo(out var nextState), "track reordering should support redo");
    Assert(ScoreFile.Deserialize(nextState).Tracks.Select(track => track.Name).SequenceEqual(new[] { "Guitar", "Drums", "Audio" }), "redo should restore the reordered list");

    var audioOnly = new ScoreDocument("Ruler ordering", new TempoMark(120, new RhythmicValue(4)), new TimeSignature(4, 4));
    var firstAudio = audioOnly.AddTrack(new ScoreTrack("Audio 1", TrackKind.Audio));
    var ruler = audioOnly.Tracks.Single(track => track.Kind == TrackKind.Ruler);
    var secondAudio = audioOnly.AddTrack(new ScoreTrack("Audio 2", TrackKind.Audio));
    Assert(audioOnly.Tracks[^1].Kind == TrackKind.Ruler, "adding another audio row must keep the managed ruler pinned last");
    audioOnly.MoveTrack(secondAudio, 0);
    Assert(audioOnly.Tracks.Select(track => track.Id).SequenceEqual(new[] { secondAudio.Id, firstAudio.Id, ruler.Id }), "audio reordering must keep its managed ruler last");
    AssertThrows<ArgumentException>(() => audioOnly.MoveTrack(ruler, 0), "managed ruler tracks must not move");
    AssertThrows<ArgumentOutOfRangeException>(() => score.MoveTrack(guitar, 99), "out-of-range track positions must be rejected");
}

static void PercussionLinkedRepeats()
{
    var (score, section, _, _, drums) = CreateScore();
    Assert(score.GetNonPercussionOccurrence(section, drums, 0).SourceTrackMeasure is null,
        "legacy percussion unexpectedly gained linked occurrence behavior");
    drums.AddPercussionPiece("kick", "Kick", 36);
    score.SetTrackPatternLength(section, drums, 2);
    score.SetTrackMeasureRepeatCount(section, drums, 0, 1);
    var source = section.Measures[0].GetTrackMeasure(drums.Id)!;
    Assert(score.GetNonPercussionOccurrence(section, drums, 0).SourceTrackMeasure == source &&
        score.GetNonPercussionOccurrence(section, drums, 1).IsRepeat &&
        score.GetNonPercussionOccurrence(section, drums, 1).SourceTrackMeasure == source,
        "configured kit did not resolve linked repeat occurrences");
    score.AppendTrackMeasureSource(section, drums);
    Assert(score.GetTrackPatternLength(section, drums) == 3, "configured kit could not append an authored source measure");
}

static void LegacyPercussionRemainsLegacy()
{
    var (score, section, _, _, drums) = CreateScore();
    var originalPart = section.Measures[0].GetTrackMeasure(drums.Id)!;
    originalPart.Voices[0].Beats[0].AddNote(new ScoreNote(36, 100));
    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    var restoredDrums = restored.Tracks.Single(track => track.Id == drums.Id);
    Assert(!restoredDrums.HasConfiguredPercussionKit &&
        restored.Sections[0].Measures[0].GetTrackMeasure(drums.Id)!.Voices[0].Beats[0].Notes.Single().MidiPitch == 36,
        "an unconfigured legacy percussion track was changed during round trip");
}

static void RemoveTrack()
{
    var (score, section, lead, melody, drums) = CreateScore();
    var measureIds = section.Measures.Select(measure => measure.Id).ToArray();
    score.SetTrackMeasureRepeatCount(section, melody, 0, 1);
    score.SetLeadTrack(section, melody);
    var secondSection = score.AddSection("B", "Second");
    score.AddMeasure(secondSection);
    score.SetLeadTrack(secondSection, drums);
    score.SetTrackPatternLength(secondSection, melody, 1);
    score.RemoveTrack(melody);
    Assert(!score.Tracks.Contains(melody) && section.Measures.All(measure => measure.GetTrackMeasure(melody.Id) is null),
        "removed track or one of its measure parts remained");
    Assert(section.LeadTrackId == lead.Id,
        "lead was not reassigned after deleting the lead track");
    Assert(secondSection.LeadTrackId == drums.Id && secondSection.Measures.All(m => m.GetTrackMeasure(melody.Id) is null),
        "removal did not clean a second section with a different lead assignment");
    Assert(measureIds.SequenceEqual(section.Measures.Select(measure => measure.Id)) &&
        section.Measures.All(measure => measure.TrackMeasures.Count == score.Tracks.Count),
        "shared measure grid or remaining parts changed");
    Assert(section.Measures.All(measure => measure.GetTrackMeasure(drums.Id) is not null),
        "remaining percussion parts were removed");

    score.RemoveTrack(lead);
    Assert(section.LeadTrackId == drums.Id, "deleting the lead did not assign the first surviving track");
    score.RemoveTrack(drums);
    Assert(score.Tracks.Count == 0 && section.LeadTrackId is null && secondSection.LeadTrackId is null &&
        section.Measures.Count == measureIds.Length && section.Measures.All(measure => measure.TrackMeasures.Count == 0) &&
        secondSection.Measures.All(measure => measure.TrackMeasures.Count == 0), "last track deletion did not preserve grids and clear leads");

    var restored = ScoreFile.Deserialize(ScoreFile.Serialize(score));
    Assert(restored.Tracks.Count == 0 && restored.Sections[0].LeadTrackId is null &&
        restored.Sections[0].Measures.Count == measureIds.Length && restored.Sections[0].Measures.All(m => m.TrackMeasures.Count == 0) &&
        restored.Sections[1].LeadTrackId is null && restored.Sections[1].Measures.All(m => m.TrackMeasures.Count == 0),
        "empty-track score failed archive round trip");
}

static void RejectUnknownTrack()
{
    var (score, section, _, _, _) = CreateScore();
    var tracksBefore = score.Tracks.ToArray();
    var partsBefore = section.Measures.SelectMany(measure => measure.TrackMeasures).ToArray();
    var leadBefore = section.LeadTrackId;
    try
    {
        score.RemoveTrack(new ScoreTrack("Unrelated", TrackKind.Other));
        throw new InvalidOperationException("Expected unrelated track removal to fail.");
    }
    catch (ArgumentException)
    {
        Assert(tracksBefore.SequenceEqual(score.Tracks) && partsBefore.SequenceEqual(section.Measures.SelectMany(measure => measure.TrackMeasures)) &&
            leadBefore == section.LeadTrackId, "rejected track removal mutated the score");
    }
}

static byte[] RewriteAsVersionTwo(byte[] bytes)
{
    using var source = new MemoryStream(bytes, false);
    using var archive = new ZipArchive(source, ZipArchiveMode.Read);
    using var entryStream = archive.GetEntry("project.json")!.Open();
    var root = JsonNode.Parse(entryStream)!;
    root["Version"] = 2;
    foreach (var measure in root["Sections"]![0]!["Measures"]!.AsArray())
        foreach (var part in measure!["Parts"]!.AsArray())
            part!.AsObject().Remove("RepeatCount");
    using var output = new MemoryStream();
    using (var target = new ZipArchive(output, ZipArchiveMode.Create, true))
    using (var targetEntry = target.CreateEntry("project.json").Open())
        System.Text.Json.JsonSerializer.Serialize(targetEntry, root);
    return output.ToArray();
}

static void AssertOccurrence(TrackMeasureOccurrence occurrence, int sourceIndex, bool isRepeat, TrackMeasure expectedPart)
{
    Assert(occurrence.SourceMeasureIndex == sourceIndex && occurrence.IsRepeat == isRepeat
        && ReferenceEquals(occurrence.SourceTrackMeasure, expectedPart), "occurrence mapping did not match expected source");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}


using OpenTab.Music;
using OpenTab.Music.Playback;
using OpenTab.Music.Persistence;

var score = new ScoreDocument("Clip operations", new TempoMark(120m, new RhythmicValue(4)), new TimeSignature(4, 4));
var section = score.Sections[0];
score.AddMeasure(section);
score.AddMeasure(section);
section.Measures[0].TempoEvents.Add(new TempoEvent(new ScoreTime(1, 2),
    new TempoMark(60m, new RhythmicValue(4)), TempoInterpolation.Step));
var audioA = score.AddTrack(new ScoreTrack("A", TrackKind.Audio));
var audioB = score.AddTrack(new ScoreTrack("B", TrackKind.Audio));
const int sampleRate = 8000;
var asset = new AudioAsset(Guid.NewGuid(), "source.wav", new string('a', 64), sampleRate, 1, 96000, "source-cache");
score.AudioAssets.Add(asset);
var original = new AudioClip(asset.Id, audioA.Id, new ScoreTime(1, 4), 13, 24000);
var clips = new List<AudioClip> { original };
var originalId = original.Id;

// A cut well off the midpoint retains the exact frame boundary and preserves source data/identity.
const long cutFrame = 4117;
Check(AudioClipOperations.TryCut(score, section, null, clips, original, cutFrame,
    out var left, out var right, out var error), error);
Check(left is not null && right is not null, "Cut did not return both clips.");
Check(left!.Id == originalId && left.SourceOffsetFrames == 13 && left.DurationFrames == cutFrame - 13,
    "Left cut range or identity was not preserved.");
Check(right!.Id != originalId && right.SourceOffsetFrames == cutFrame && right.DurationFrames == 24000 - (cutFrame - 13),
    "Right cut did not begin at the exact requested source frame.");
var timing = new AudioTimelineTiming(score, section: section);
Check(timing.TryGetElapsedSeconds(original.Start, out var originalStart), "Original start did not map.");
var expectedCutSeconds = originalStart + (cutFrame - original.SourceOffsetFrames) / (double)sampleRate;
Check(timing.TryGetElapsedSeconds(right.Start, out var actualCutSeconds) && actualCutSeconds >= expectedCutSeconds - 1e-12,
    "Ceiling conversion placed the right clip before the exact cut time.");
Check(actualCutSeconds - expectedCutSeconds < 0.00001,
    "Ceiling conversion placed the right clip too far from the exact cut time.");
Check(clips.Count == 1 && ReferenceEquals(clips[0], original) && original.SourceOffsetFrames == 13 && original.DurationFrames == 24000,
    "Cut mutated the clip list or original clip.");

Check(!AudioClipOperations.TryCut(score, section, null, clips, original, original.SourceOffsetFrames,
    out _, out _, out _), "Cut at the first source edge should fail.");
Check(!AudioClipOperations.TryCut(score, section, null, clips, original, original.SourceOffsetFrames + original.DurationFrames,
    out _, out _, out _), "Cut at the last source edge should fail.");

// A cut whose source time lands exactly at the clipped section end is rejected.
var nearEnd = new AudioClip(asset.Id, audioA.Id, new ScoreTime(7, 4), 0, 16000);
var nearEndClips = new List<AudioClip> { nearEnd };
Check(!AudioClipOperations.TryCut(score, section, null, nearEndClips, nearEnd, 8000,
    out _, out _, out _), "Cut at the clipped section end should fail.");

// Same-track overlap is rejected; a different track remains independently editable.
var neighbor = new AudioClip(asset.Id, audioA.Id, new ScoreTime(3, 4), 30000, 4000);
var overlapClips = new List<AudioClip> { original, neighbor };
Check(!AudioClipOperations.TryMove(score, section, null, overlapClips, original, new ScoreTime(1, 2),
    out _, out _), "Same-track overlapping move should fail.");
var otherTrack = new AudioClip(asset.Id, audioB.Id, new ScoreTime(1, 2), 30000, 4000);
var acrossTrackClips = new List<AudioClip> { original, otherTrack };
Check(AudioClipOperations.TryMove(score, section, null, acrossTrackClips, original, new ScoreTime(1, 2),
    out var moved, out error), error);
Check(moved is not null && moved.Id == original.Id && moved.SourceOffsetFrames == original.SourceOffsetFrames &&
    moved.DurationFrames == original.DurationFrames, "Move changed clip source metadata or identity.");
Check(!AudioClipOperations.TryMove(score, section, null, clips, original, new ScoreTime(2, 1),
    out _, out _), "Move to the section end should fail.");

// Source edits can trim non-destructively and later restore the whole source range.
Check(AudioClipOperations.TryEdit(score, section, null, clips, original, original.Start, 12000, 40000,
    out var trimmed, out error), error);
Check(trimmed is not null && trimmed.SourceOffsetFrames == 12000 && trimmed.DurationFrames == 40000,
    "Valid source trim did not apply.");
var trimmedList = new List<AudioClip> { trimmed! };
Check(AudioClipOperations.TryEdit(score, section, null, trimmedList, trimmed!, ScoreTime.Zero, 0, asset.FrameCount,
    out var restored, out error), error);
Check(restored is not null && restored.SourceOffsetFrames == 0 && restored.DurationFrames == asset.FrameCount,
    "Moving the source offset back did not restore access to the full original asset.");
Check(!AudioClipOperations.TryEdit(score, section, null, clips, original, original.Start, asset.FrameCount - 1, 2,
    out _, out _), "Out-of-range source edit should fail.");

// Edge trims quantize elapsed pointer positions to source samples and never modify the source asset.
Check(timing.TryGetElapsedSeconds(original.Start, out originalStart), "Original clip start did not map for trims.");
var leftTrimBoundary = originalStart + 127.0 / sampleRate;
Check(AudioClipOperations.TryTrimLeft(score, section, null, clips, original, leftTrimBoundary,
    out var leftTrim, out error), error);
Check(leftTrim is not null && leftTrim.Id == original.Id && leftTrim.AssetId == original.AssetId &&
    leftTrim.SourceOffsetFrames == original.SourceOffsetFrames + 127 &&
    leftTrim.DurationFrames == original.DurationFrames - 127,
    "Left trim did not quantize to a source sample and preserve the original source end.");
Check(timing.TryGetElapsedSeconds(leftTrim!.Start, out var leftStart) &&
    Math.Abs(leftStart - leftTrimBoundary) <= 1.0 / sampleRate,
    "Tempo-aware inverse mapping placed the left trim more than one source sample from its requested boundary.");
Check(AudioClipOperations.TryTrimLeft(score, section, null, clips, original,
    originalStart + 17.5 / sampleRate, out var roundedLeft, out error), error);
Check(roundedLeft is not null && roundedLeft.SourceOffsetFrames == original.SourceOffsetFrames + 18,
    "Half-sample trim positions should round away from zero deterministically.");

var leftExpansionBoundary = originalStart - 5.0 / sampleRate;
Check(AudioClipOperations.TryTrimLeft(score, section, null, clips, original, leftExpansionBoundary,
    out var expandedLeft, out error), error);
Check(expandedLeft is not null && expandedLeft.SourceOffsetFrames == original.SourceOffsetFrames - 5 &&
    expandedLeft.DurationFrames == original.DurationFrames + 5,
    "Left expansion did not restore available source frames while retaining the old source end.");
Check(!AudioClipOperations.TryTrimLeft(score, section, null, clips, original,
    originalStart - 14.0 / sampleRate, out _, out _),
    "Left expansion beyond source frame zero should fail.");
Check(!AudioClipOperations.TryTrimLeft(score, section, null, clips, original, double.NaN, out _, out _),
    "A non-finite left trim boundary should fail.");

var rightTrimBoundary = originalStart + 9999.0 / sampleRate;
Check(AudioClipOperations.TryTrimRight(score, section, null, clips, original, rightTrimBoundary,
    out var rightTrim, out error), error);
Check(rightTrim is not null && rightTrim.Id == original.Id && rightTrim.Start == original.Start &&
    rightTrim.SourceOffsetFrames == original.SourceOffsetFrames && rightTrim.DurationFrames == 9999,
    "Right trim changed the clip start/source offset or missed the sample-rounded duration.");
Check(AudioClipOperations.TryTrimRight(score, section, null, clips, original,
    originalStart + 42.5 / sampleRate, out var roundedRight, out error), error);
Check(roundedRight is not null && roundedRight.DurationFrames == 43,
    "Half-sample right trim positions should round away from zero deterministically.");
var rightExpansionBoundary = originalStart + 26000.0 / sampleRate;
Check(AudioClipOperations.TryTrimRight(score, section, null, clips, original, rightExpansionBoundary,
    out var expandedRight, out error), error);
Check(expandedRight is not null && expandedRight.DurationFrames == 26000,
    "Right expansion into available source audio should succeed.");
Check(!AudioClipOperations.TryTrimRight(score, section, null, clips, original,
    originalStart + (asset.FrameCount + 1.0) / sampleRate, out _, out _),
    "Right trim beyond the selected timeline boundary should fail.");
Check(!AudioClipOperations.TryTrimRight(score, section, null, clips, original,
    originalStart + (asset.FrameCount - original.SourceOffsetFrames + 1.0) / sampleRate, out _, out _),
    "An invalid right trim boundary should fail.");
var sourceEdgeClip = new AudioClip(asset.Id, audioB.Id, new ScoreTime(1, 4), 90000, 5000);
Check(!AudioClipOperations.TryTrimRight(score, section, null, new[] { sourceEdgeClip }, sourceEdgeClip,
    originalStart + 7000.0 / sampleRate, out _, out _),
    "Right expansion beyond the source asset should fail even when the boundary is inside the section.");
Check(!AudioClipOperations.TryTrimRight(score, section, null, clips, original, originalStart,
    out _, out _), "A zero-length right trim should fail.");
Check(!AudioClipOperations.TryTrimRight(score, section, null, overlapClips, original,
    originalStart + 20000.0 / sampleRate, out _, out _),
    "A right trim overlapping a same-track neighbor should fail.");
Check(original.SourceOffsetFrames == 13 && original.DurationFrames == 24000 && asset.FrameCount == 96000,
    "A trim mutated the original clip or source asset metadata.");

// Existing arrangement audio placements use the same operation rules and context timing.
var arrangement = ArrangementEditing.Add(score);
ArrangementEditing.Append(score, arrangement, section.Id);
var arranged = new AudioClip(asset.Id, audioA.Id, new ScoreTime(1, 4), 100, 4000);
arrangement.AudioClips.Add(arranged);
Check(AudioClipOperations.TryMove(score, null, arrangement, arrangement.AudioClips, arranged, new ScoreTime(1, 2),
    out var arrangedMove, out error), error);
Check(arrangedMove is not null && arrangedMove.Id == arranged.Id, "Arrangement clip was not editable in its own context.");

// A committed drag and cut participate in the same snapshot history as score edits.
section.AudioClips.Add(original);
var history = new ScoreHistory();
history.Reset(ScoreFile.Serialize(score));
Check(AudioClipOperations.TryMove(score, section, null, section.AudioClips, original, new ScoreTime(1, 2),
    out var historyMove, out error), error);
section.AudioClips[0] = historyMove!;
history.Record(ScoreFile.Serialize(score));
Check(AudioClipOperations.TryCut(score, section, null, section.AudioClips, historyMove!, cutFrame,
    out var historyLeft, out var historyRight, out error), error);
section.AudioClips[0] = historyLeft!;
section.AudioClips.Add(historyRight!);
history.Record(ScoreFile.Serialize(score));
Check(history.TryUndo(out var beforeCut), "The cut was not undoable.");
var restoredBeforeCut = ScoreFile.Deserialize(beforeCut);
Check(restoredBeforeCut.Sections[0].AudioClips.Count == 1 && restoredBeforeCut.Sections[0].AudioClips[0].Start == new ScoreTime(1, 2),
    "Undo did not restore the moved, uncut clip.");
Check(history.TryUndo(out var beforeMove), "The drag was not undoable.");
Check(ScoreFile.Deserialize(beforeMove).Sections[0].AudioClips.Single().Start == original.Start,
    "Undo did not restore the clip's original position.");
Check(history.TryRedo(out var redoneMove) && ScoreFile.Deserialize(redoneMove).Sections[0].AudioClips.Single().Start == new ScoreTime(1, 2),
    "Redo did not restore the drag.");

var preTrimClip = section.AudioClips[0];
Check(timing.TryGetElapsedSeconds(preTrimClip.Start, out var preTrimStart), "Pre-trim history clip start did not map.");
Check(AudioClipOperations.TryTrimLeft(score, section, null, section.AudioClips, preTrimClip,
    preTrimStart + 80.0 / sampleRate, out var historyTrim, out error), error);
section.AudioClips[0] = historyTrim!;
history.Record(ScoreFile.Serialize(score));
Check(history.TryUndo(out var beforeTrim), "The trim was not undoable.");
var restoredBeforeTrim = ScoreFile.Deserialize(beforeTrim).Sections[0].AudioClips.Single();
Check(restoredBeforeTrim.SourceOffsetFrames == original.SourceOffsetFrames &&
    restoredBeforeTrim.DurationFrames == original.DurationFrames && restoredBeforeTrim.Start == preTrimClip.Start,
    "Undo did not restore the clip's pre-trim source and timeline boundaries.");

Console.WriteLine("Audio clip operation regressions passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

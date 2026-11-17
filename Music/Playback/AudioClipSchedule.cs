#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music;

namespace OpenTab.Music.Playback;

/// <summary>A frame-domain projection of a nondestructive audio clip onto a playback pass.</summary>
public readonly record struct ScheduledAudioClip(
    Guid ClipId,
    Guid AssetId,
    Guid TrackId,
    long StartFrame,
    long EndFrame,
    long SourceOffsetFrames);

/// <summary>
/// Projects audio clip placements onto the score clock. This is deliberately separate
/// from decoding and mixing so the render callback never performs file IO.
/// </summary>
public static class AudioClipSchedule
{
    private readonly record struct SectionOccurrence(ScoreSection Section, ScoreTime SectionStart,
        ScoreTime SourceStart, ScoreTime SourceEnd, ScoreTime ArrangementStart,
        long TimelineStartFrame, long TimelineEndFrame);

    public static IReadOnlyList<ScheduledAudioClip> Build(ScoreDocument score, int sampleRate,
        PlaybackLoopSelection? selection = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var tempo = score.CreateTempoMap();
        var sections = selection?.Mode switch
        {
            PlaybackLoopMode.Measure => BuildMeasureScope(score, selection.MeasureId),
            PlaybackLoopMode.Sections => score.Sections.Where(section => selection.SectionIds.Contains(section.Id)).ToArray(),
            PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop => selection.SectionIds
                .Select(id => score.Sections.FirstOrDefault(section => section.Id == id))
                .Where(section => section is not null).Cast<ScoreSection>().ToArray(),
            _ => score.Sections.ToArray()
        };

        var sourceStarts = new Dictionary<Guid, ScoreTime>();
        var sourcePosition = ScoreTime.Zero;
        foreach (var section in score.Sections)
        {
            sourceStarts[section.Id] = sourcePosition;
            foreach (var measure in section.Measures)
                sourcePosition += ScorePlaybackClock.GetMeasureDuration(score, section, measure);
        }

        var occurrences = new List<SectionOccurrence>();
        var timelineFrame = 0L;
        var arrangementPosition = ScoreTime.Zero;
        foreach (var section in sections)
        {
            var sectionStart = sourceStarts[section.Id];
            var sourceStart = sectionStart;
            var sourceEnd = sectionStart;
            foreach (var measure in section.Measures)
                sourceEnd += ScorePlaybackClock.GetMeasureDuration(score, section, measure);
            var sectionEnd = sourceEnd;

            if (selection?.Mode == PlaybackLoopMode.Measure)
            {
                var measure = section.Measures.FirstOrDefault(item => item.Id == selection.MeasureId);
                if (measure is null) continue;
                var measureStart = sourceStart;
                foreach (var prior in section.Measures.TakeWhile(item => item.Id != measure.Id))
                    measureStart += ScorePlaybackClock.GetMeasureDuration(score, section, prior);
                sourceStart = measureStart;
                sourceEnd = measureStart + ScorePlaybackClock.GetMeasureDuration(score, section, measure);
            }

            var duration = ToFrame(tempo.GetElapsedSeconds(sourceEnd), sampleRate) -
                ToFrame(tempo.GetElapsedSeconds(sourceStart), sampleRate);
            occurrences.Add(new SectionOccurrence(section, sectionStart, sourceStart, sourceEnd,
                arrangementPosition, timelineFrame,
                checked(timelineFrame + duration)));
            timelineFrame = checked(timelineFrame + duration);
            arrangementPosition += sectionEnd - sectionStart;
        }

        var assets = score.AudioAssets.ToDictionary(asset => asset.Id);
        var scheduled = new List<ScheduledAudioClip>();
        foreach (var occurrence in occurrences)
        {
            foreach (var clip in occurrence.Section.AudioClips)
            {
                if (!assets.TryGetValue(clip.AssetId, out var asset)) continue;
                var clipStartPosition = occurrence.SectionStart + clip.Start;
                var sourceStartSeconds = tempo.GetElapsedSeconds(clipStartPosition);
                var sourceEndSeconds = sourceStartSeconds + clip.DurationFrames / (double)asset.SampleRate;
                var visibleStartSeconds = Math.Max(sourceStartSeconds, tempo.GetElapsedSeconds(occurrence.SourceStart));
                var visibleEndSeconds = Math.Min(sourceEndSeconds, tempo.GetElapsedSeconds(occurrence.SourceEnd));
                if (visibleEndSeconds <= visibleStartSeconds) continue;

                var start = checked(occurrence.TimelineStartFrame + ToFrame(visibleStartSeconds, sampleRate) -
                    ToFrame(tempo.GetElapsedSeconds(occurrence.SourceStart), sampleRate));
                var end = checked(occurrence.TimelineStartFrame + ToFrame(visibleEndSeconds, sampleRate) -
                    ToFrame(tempo.GetElapsedSeconds(occurrence.SourceStart), sampleRate));
                end = Math.Min(end, occurrence.TimelineEndFrame);
                if (end <= start) continue;
                var skippedSourceFrames = (long)Math.Round((visibleStartSeconds - sourceStartSeconds) * asset.SampleRate);
                scheduled.Add(new ScheduledAudioClip(clip.Id, clip.AssetId, clip.TrackId, start, end,
                    checked(clip.SourceOffsetFrames + skippedSourceFrames)));
            }
        }

        if (selection?.Mode is PlaybackLoopMode.ArrangementOnce or PlaybackLoopMode.ArrangementLoop &&
            selection.ArrangementId is { } arrangementId)
        {
            var arrangement = score.Arrangements.FirstOrDefault(item => item.Id == arrangementId);
            if (arrangement is not null)
            {
                foreach (var clip in arrangement.AudioClips)
                {
                    if (!assets.TryGetValue(clip.AssetId, out var asset)) continue;
                    var occurrence = occurrences.FirstOrDefault(item => clip.Start >= item.ArrangementStart &&
                        clip.Start < item.ArrangementStart + (item.SourceEnd - item.SectionStart));
                    if (occurrence.Section is null) continue;
                    var sourceStart = occurrence.SectionStart + (clip.Start - occurrence.ArrangementStart);
                    var start = checked(occurrence.TimelineStartFrame +
                        ToFrame(tempo.GetElapsedSeconds(sourceStart), sampleRate) -
                        ToFrame(tempo.GetElapsedSeconds(occurrence.SectionStart), sampleRate));
                    if (start >= timelineFrame) continue;
                    var end = Math.Min(timelineFrame, checked(start + ToFrame(clip.DurationFrames / (double)asset.SampleRate, sampleRate)));
                    if (end > start) scheduled.Add(new ScheduledAudioClip(clip.Id, clip.AssetId, clip.TrackId,
                        start, end, clip.SourceOffsetFrames));
                }
            }
        }

        return scheduled.OrderBy(item => item.StartFrame).ThenBy(item => item.TrackId).ToArray();
    }

    private static ScoreSection[] BuildMeasureScope(ScoreDocument score, Guid? measureId) =>
        measureId is { } id
            ? score.Sections.Where(section => section.Measures.Any(measure => measure.Id == id)).ToArray()
            : Array.Empty<ScoreSection>();

    private static long ToFrame(double seconds, int sampleRate) =>
        checked((long)Math.Round(seconds * sampleRate, MidpointRounding.AwayFromZero));
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music;

namespace OpenTab.Music.Playback;

public enum ScoreTimeRounding { Floor, Ceiling, Nearest }

/// <summary>Tempo-aware conversion between timeline seconds and musical positions in one audio editing context.</summary>
public sealed class AudioTimelineTiming
{
    private readonly record struct Occurrence(ScoreTime SourceStart, ScoreTime SourceEnd, ScoreTime TimelineStart);
    private readonly TempoMap _tempo;
    private readonly Occurrence[] _occurrences;
    private readonly ScoreTime _duration;

    /// <param name="section">A single section context, used when no arrangement is supplied.</param>
    /// <param name="arrangement">An ordered arrangement context. Repetitions and reordered sections are preserved.</param>
    public AudioTimelineTiming(ScoreDocument score, ScoreSection? section = null, ScoreArrangement? arrangement = null)
    {
        ArgumentNullException.ThrowIfNull(score);
        if (arrangement is not null && section is not null)
            throw new ArgumentException("Choose either a section or an arrangement context.");
        _tempo = score.CreateTempoMap();
        var starts = new Dictionary<Guid, ScoreTime>();
        var source = ScoreTime.Zero;
        foreach (var item in score.Sections)
        {
            starts[item.Id] = source;
            foreach (var measure in item.Measures)
                source += ScorePlaybackClock.GetMeasureDuration(score, item, measure);
        }

        var result = new List<Occurrence>();
        var timeline = ScoreTime.Zero;
        IEnumerable<ScoreSection> sections;
        if (arrangement is not null)
        {
            var entries = arrangement.Sections;
            foreach (var entry in entries)
            {
                var arranged = score.Sections.FirstOrDefault(item => item.Id == entry.SectionId);
                if (arranged is null) continue;
                var end = GetSectionEnd(score, arranged, starts[arranged.Id]);
                for (var repeat = 0; repeat < Math.Max(0, entry.Repetitions); repeat++)
                {
                    result.Add(new Occurrence(starts[arranged.Id], end, timeline));
                    timeline += end - starts[arranged.Id];
                }
            }
        }
        else
        {
            sections = section is null ? score.Sections : new[] { section };
            var chosen = sections.FirstOrDefault();
            if (chosen is not null)
            {
                var start = starts[chosen.Id];
                var end = GetSectionEnd(score, chosen, start);
                result.Add(new Occurrence(start, end, ScoreTime.Zero));
                timeline = end - start;
            }
        }
        _occurrences = result.ToArray();
        _duration = timeline;
    }

    public double GetDurationSeconds() => _occurrences.Length == 0 ? 0 :
        _occurrences.Sum(item => _tempo.GetElapsedSeconds(item.SourceEnd) - _tempo.GetElapsedSeconds(item.SourceStart));

    /// <summary>Maps context-relative musical time to elapsed seconds; positions at or beyond the end are out of range.</summary>
    public bool TryGetElapsedSeconds(ScoreTime timelinePosition, out double seconds)
    {
        seconds = 0;
        if (!timelinePosition.IsInitialized || timelinePosition < ScoreTime.Zero || timelinePosition >= _duration) return false;
        var occurrence = FindOccurrence(timelinePosition);
        if (occurrence is null) return false;
        var sourcePosition = occurrence.Value.SourceStart + (timelinePosition - occurrence.Value.TimelineStart);
        seconds = GetOccurrenceTimelineStartSeconds(occurrence.Value) +
            _tempo.GetElapsedSeconds(sourcePosition) - _tempo.GetElapsedSeconds(occurrence.Value.SourceStart);
        return true;
    }

    /// <summary>Maps elapsed seconds to context-relative musical time with deterministic 1/1,000,000-whole-note precision.</summary>
    public bool TryGetScoreTimeAtSeconds(double elapsedSeconds, out ScoreTime timelinePosition,
        ScoreTimeRounding rounding = ScoreTimeRounding.Nearest)
    {
        timelinePosition = ScoreTime.Zero;
        var total = GetDurationSeconds();
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 || elapsedSeconds >= total - 1e-12) return false;
        var elapsed = elapsedSeconds;
        foreach (var occurrence in _occurrences)
        {
            var occurrenceDuration = _tempo.GetElapsedSeconds(occurrence.SourceEnd) - _tempo.GetElapsedSeconds(occurrence.SourceStart);
            if (elapsed < occurrenceDuration)
            {
                var lo = 0L;
                var hi = ToUnits(occurrence.SourceEnd - occurrence.SourceStart);
                while (lo < hi)
                {
                    var mid = lo + (hi - lo + 1) / 2;
                    var pos = occurrence.SourceStart + new ScoreTime(mid, Precision);
                    if (_tempo.GetElapsedSeconds(pos) - _tempo.GetElapsedSeconds(occurrence.SourceStart) <= elapsed) lo = mid;
                    else hi = mid - 1;
                }
                var selected = lo;
                var maxUnits = ToUnits(occurrence.SourceEnd - occurrence.SourceStart);
                if (lo < maxUnits && rounding is ScoreTimeRounding.Nearest or ScoreTimeRounding.Ceiling)
                {
                    var lowerPosition = occurrence.SourceStart + new ScoreTime(lo, Precision);
                    var upperPosition = occurrence.SourceStart + new ScoreTime(lo + 1, Precision);
                    var lowerError = elapsed - (_tempo.GetElapsedSeconds(lowerPosition) - _tempo.GetElapsedSeconds(occurrence.SourceStart));
                    var upperError = (_tempo.GetElapsedSeconds(upperPosition) - _tempo.GetElapsedSeconds(occurrence.SourceStart)) - elapsed;
                    const double exactToleranceSeconds = 1e-12;
                    if (rounding == ScoreTimeRounding.Ceiling ? lowerError > exactToleranceSeconds : upperError < lowerError)
                        selected++;
                }
                timelinePosition = occurrence.TimelineStart + new ScoreTime(selected, Precision);
                if (timelinePosition >= _duration)
                {
                    timelinePosition = ScoreTime.Zero;
                    return false;
                }
                return true;
            }
            elapsed -= occurrenceDuration;
        }
        return false;
    }

    private double GetOccurrenceTimelineStartSeconds(Occurrence target)
    {
        double result = 0;
        foreach (var item in _occurrences)
        {
            if (item.Equals(target)) break;
            result += _tempo.GetElapsedSeconds(item.SourceEnd) - _tempo.GetElapsedSeconds(item.SourceStart);
        }
        return result;
    }

    private Occurrence? FindOccurrence(ScoreTime position)
    {
        foreach (var item in _occurrences)
            if (position >= item.TimelineStart && position < item.TimelineStart + (item.SourceEnd - item.SourceStart)) return item;
        return null;
    }

    private static ScoreTime GetSectionEnd(ScoreDocument score, ScoreSection section, ScoreTime start)
    {
        foreach (var measure in section.Measures) start += ScorePlaybackClock.GetMeasureDuration(score, section, measure);
        return start;
    }

    private const long Precision = 1_000_000;
    private static long ToUnits(ScoreTime value) => checked((long)Math.Round((double)value.Numerator / value.Denominator * Precision));
}

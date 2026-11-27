#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using OpenTab.Music;

namespace OpenTab.Music.Playback;

public sealed partial class ScorePlaybackClock
{
    private static Dictionary<Guid, TrackEffectsChain> CompileTrackEffects(ScoreDocument score, TempoMap tempo, int sampleRate)
    {
        var result = new Dictionary<Guid, TrackEffectsChain>();
        var scoreEnd = GetScoreEndPosition(score);
        var horizon = 4.0 * scoreEnd.Numerator / scoreEnd.Denominator;
        foreach (var track in score.Tracks)
        {
            if (track.Kind is TrackKind.Audio or TrackKind.Ruler || track.Pedals.Count == 0) continue;
            var effects = new List<ITrackAudioEffect>();
            foreach (var pedal in track.Pedals)
            {
                pedal.Validate();
                if (!pedal.Enabled) continue;
                AudioAutomationCurve Curve(string id, float fallback) => CompilePedalCurve(pedal, id, fallback, tempo, sampleRate, horizon);
                switch (pedal.TypeId)
                {
                    case "overdrive":
                        effects.Add(new OverdriveTrackEffect(sampleRate, Curve("drive", pedal.Drive), Curve("tone", pedal.Tone), Curve("level", pedal.Level)));
                        break;
                    case "chorus":
                        effects.Add(new ChorusTrackEffect(sampleRate, Curve("rate", pedal.GetParameter("rate")),
                            Curve("depth", pedal.GetParameter("depth")), Curve("mix", pedal.GetParameter("mix"))));
                        break;
                    case "delay":
                        effects.Add(new DelayTrackEffect(sampleRate, Curve("time", pedal.GetParameter("time")),
                            Curve("feedback", pedal.GetParameter("feedback")), Curve("mix", pedal.GetParameter("mix"))));
                        break;
                }
            }
            if (effects.Count > 0) result.Add(track.Id, new TrackEffectsChain(effects.ToArray()));
        }
        return result;
    }

    private static AudioAutomationCurve CompilePedalCurve(TrackPedal pedal, string parameter, float fallback, TempoMap tempo, int sampleRate, double horizon)
    {
        var lane = pedal.Automation.FirstOrDefault(item => item.ParameterId == parameter);
        if (lane is null || lane.Points.Count == 0) return new AudioAutomationCurve(fallback, Array.Empty<AudioAutomationPoint>());
        // Points beyond the score cannot be played, but the next point still
        // determines the interpolated value at the end of the score.
        var relevant = lane.Points.TakeWhile(point => point.QuarterNote <= horizon).ToList();
        if (relevant.Count < lane.Points.Count)
        {
            var next = lane.Points[relevant.Count];
            if (relevant.Count == 0) relevant.Add(new PedalAutomationPoint(0, next.Value, next.SegmentMode));
            else
            {
                var previous = relevant[^1];
                if (previous.QuarterNote < horizon)
                {
                    var amount = previous.SegmentMode == ExpressionSegmentMode.Step ? 0 :
                        (horizon - previous.QuarterNote) / (next.QuarterNote - previous.QuarterNote);
                    relevant.Add(new PedalAutomationPoint(horizon, (float)(previous.Value + (next.Value - previous.Value) * amount)));
                }
            }
        }
        var tempoPoints = tempo.Points;
        var anchors = new List<(double Quarter, float Value, bool Step)>();
        for (var i = 0; i < relevant.Count; i++)
        {
            var current = relevant[i];
            anchors.Add((current.QuarterNote, current.Value, current.SegmentMode == ExpressionSegmentMode.Step));
            if (current.SegmentMode == ExpressionSegmentMode.Step || i + 1 >= relevant.Count) continue;
            var next = relevant[i + 1];
            var boundaries = new SortedSet<double>();
            for (var t = 0; t < tempoPoints.Count; t++)
            {
                var point = tempoPoints[t];
                var quarter = 4.0 * point.Position.Numerator / point.Position.Denominator;
                if (quarter > current.QuarterNote && quarter < next.QuarterNote) boundaries.Add(quarter);
                if (point.Interpolation != TempoInterpolation.Linear || t + 1 >= tempoPoints.Count) continue;
                var following = tempoPoints[t + 1].Position;
                var endQuarter = 4.0 * following.Numerator / following.Denominator;
                var start = Math.Max(current.QuarterNote, quarter);
                var end = Math.Min(next.QuarterNote, endQuarter);
                if (end <= start) continue;
                // Approximate beat-linear automation across a tempo ramp with short
                // source-time segments; bound work for unusually long ramps.
                var subdivisions = Math.Clamp((int)Math.Ceiling(Math.Min((end - start) * 16.0, 4096.0)), 1, 4096);
                for (var n = 1; n < subdivisions; n++) boundaries.Add(start + (end - start) * n / subdivisions);
            }
            foreach (var quarter in boundaries)
            {
                var amount = (quarter - current.QuarterNote) / (next.QuarterNote - current.QuarterNote);
                anchors.Add((quarter, (float)(current.Value + (next.Value - current.Value) * amount), false));
            }
        }
        var points = new List<AudioAutomationPoint>(anchors.Count);
        foreach (var anchor in anchors)
        {
            // Quarter positions are stored as doubles; quantize to a fine musical
            // grid before handing them to the score's exact rational tempo map.
            var ticks = checked((long)Math.Round(anchor.Quarter * 96000.0));
            var frame = ToFrame(tempo.GetElapsedSeconds(new ScoreTime(ticks, 384000)), sampleRate);
            var point = new AudioAutomationPoint(frame, anchor.Value, anchor.Step);
            if (points.Count > 0 && points[^1].Frame == frame) points[^1] = point;
            else points.Add(point);
        }
        return new AudioAutomationCurve(fallback, points.ToArray());
    }
}

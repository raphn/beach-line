using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

public enum VoiceDurationStatus
{
	Complete,
	Incomplete,
	Overfull
}

public sealed record VoiceDurationIssue(
	Guid TrackId,
	int VoiceNumber,
	ScoreTime ActualDuration,
	ScoreTime ExpectedDuration,
	VoiceDurationStatus Status);

public static class MeasureValidator
{
	public static List<VoiceDurationIssue> Validate(
		ScoreMeasure measure,
		TimeSignature timeSignature,
		IReadOnlyList<ScoreTrack> tracks)
	{
		ArgumentNullException.ThrowIfNull(measure);
		ArgumentNullException.ThrowIfNull(tracks);

		var issues = new List<VoiceDurationIssue>();
		foreach (var trackMeasure in measure.TrackMeasures)
		{
			if (!tracks.Any(track => track.Id == trackMeasure.TrackId))
			{
				continue;
			}

			foreach (var voice in trackMeasure.Voices)
			{
				var actual = voice.Beats.Aggregate(ScoreTime.Zero, (total, beat) => total + beat.Duration);
				var comparison = actual.CompareTo(timeSignature.BarDuration);
				if (comparison == 0 || measure.IsFreeTime)
				{
					continue;
				}

				if (measure.IsPickup && actual < timeSignature.BarDuration)
				{
					continue;
				}

				issues.Add(new VoiceDurationIssue(
					trackMeasure.TrackId,
					voice.Number,
					actual,
					timeSignature.BarDuration,
					comparison < 0 ? VoiceDurationStatus.Incomplete : VoiceDurationStatus.Overfull));
			}
		}

		return issues;
	}
}

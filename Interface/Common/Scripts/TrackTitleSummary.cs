#nullable enable
using System.Linq;
using OpenTab.Music;

/// <summary>Formats the focused-section summary shown in a track's foldable title.</summary>
public static class TrackTitleSummary
{
    public static string Format(ScoreDocument score, ScoreSection section, ScoreTrack track)
    {
        var sectionName = string.IsNullOrWhiteSpace(section.Name) ? section.Title : section.Name;
        var context = string.IsNullOrWhiteSpace(sectionName) ? "Section" : $"Section {sectionName}";
        var total = section.Measures.Count;
        string measures = $"{total} {(total == 1 ? "measure" : "measures")}";
        if (track.Kind == TrackKind.Audio)
        {
            var clips = section.AudioClips.Count(clip => clip.TrackId == track.Id);
            return $"{track.Name} · {context} · {measures} · {clips} {(clips == 1 ? "clip" : "clips")}";
        }
        if (section.LeadTrackId == track.Id)
            return $"{track.Name} · {context} · Lead · {measures}";
        var sources = score.GetTrackPatternLength(section, track);
        return $"{track.Name} · {context} · {sources} source / {measures}";
    }
}

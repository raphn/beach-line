#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>A reusable alternative ordering of sections in a score.</summary>
public sealed class ScoreArrangement
{
    public Guid Id { get; internal set; } = Guid.NewGuid();
    public string Name { get; internal set; }
    public List<ArrangementSection> Sections { get; } = new();
    /// <summary>Timeline-relative audio placements belonging only to this arrangement.</summary>
    public List<AudioClip> AudioClips { get; } = new();
    internal ScoreArrangement(string name) => Name = name;
}

public sealed class ArrangementSection
{
    public Guid SectionId { get; set; }
    public int Repetitions { get; set; } = 1;
    public ArrangementSection(Guid sectionId, int repetitions = 1) { SectionId = sectionId; Repetitions = repetitions; }
}

public static class ArrangementEditing
{
    public const int MaxRepetitions = 1024;
    public static ScoreArrangement Add(ScoreDocument score)
    {
        var name = "Arrangement";
        for (var suffix = 2; score.Arrangements.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            name = $"Arrangement{suffix}";
        var arrangement = new ScoreArrangement(name);
        score.Arrangements.Add(arrangement);
        return arrangement;
    }
    public static void Rename(ScoreDocument score, ScoreArrangement arrangement, string name)
    {
        name = name.Trim();
        if (!score.Arrangements.Contains(arrangement) || name.Length == 0 || score.Arrangements.Any(a => a != arrangement && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Arrangement names must be nonempty and unique.");
        arrangement.Name = name;
    }
    public static void Append(ScoreDocument score, ScoreArrangement arrangement, Guid sectionId)
    {
        if (!score.Arrangements.Contains(arrangement) || !score.Sections.Any(s => s.Id == sectionId)) throw new ArgumentException("Unknown section or arrangement.");
        var last = arrangement.Sections.LastOrDefault();
        if (last?.SectionId == sectionId)
        {
            if (last.Repetitions >= MaxRepetitions) throw new InvalidOperationException("Maximum repetitions reached.");
            last.Repetitions++;
        }
        else arrangement.Sections.Add(new ArrangementSection(sectionId));
    }
}

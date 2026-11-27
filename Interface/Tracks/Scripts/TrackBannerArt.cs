#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using OpenTab.Music;

/// <summary>Provides the in-project artwork that may be assigned to a track banner.</summary>
public static class TrackBannerArt
{
    private const string BannerDirectory = "res://Interface/Common/Graphs/banners";
    private const string DefaultArtwork = BannerDirectory + "/hungquach679png-edm-7158021_1920.png";
    private const string DefaultAudioArtwork = BannerDirectory + "/cdubby-sound-7701546_1920.jpg";

    private static readonly Lazy<IReadOnlyList<string>> Catalog = new(LoadCatalog);

    /// <summary>Full resource paths of every supported banner image in the project.</summary>
    public static IReadOnlyList<string> Paths => Catalog.Value;

    public static string DefaultPath(TrackKind kind) =>
        kind == TrackKind.Audio ? DefaultAudioArtwork : DefaultArtwork;

    /// <summary>Returns a persisted path only when it is a current banner asset.</summary>
    public static string ResolvePath(ScoreTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.BannerImagePath is { } path && Paths.Contains(path, StringComparer.Ordinal)
            ? path
            : DefaultPath(track.Kind);
    }

    private static IReadOnlyList<string> LoadCatalog()
    {
        var paths = new List<string>();
        CollectDirectory(BannerDirectory, paths);
        paths.Sort(StringComparer.Ordinal);
        return paths.AsReadOnly();
    }

    private static void CollectDirectory(string directoryPath, ICollection<string> paths)
    {
        foreach (var entry in ResourceLoader.ListDirectory(directoryPath))
        {
            if (entry.EndsWith('/'))
            {
                CollectDirectory($"{directoryPath}/{entry.TrimEnd('/')}", paths);
                continue;
            }
            if (Path.GetExtension(entry) is ".jpg" or ".png") paths.Add($"{directoryPath}/{entry}");
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

/// <summary>Stores recently opened or saved score paths independently of the working session.</summary>
public sealed class RecentScorePaths
{
    private sealed class Preferences { public List<string>? Paths { get; set; } = new(); }
    private const int MaximumCount = 10;
    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly List<string> _paths = new();

    public RecentScorePaths() : this(ProjectSettings.GlobalizePath("user://recent_scores.json")) { }

    public RecentScorePaths(string path, Action<string>? warn = null)
    {
        _path = path;
        _warn = warn ?? (message => GD.PushWarning(message));
        try
        {
            if (File.Exists(_path))
                _paths.AddRange((JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path))?.Paths ?? new())
                    .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(PathComparer).Take(MaximumCount));
        }
        catch (Exception exception) { _warn($"Could not read recent score preferences: {exception.Message}"); _paths.Clear(); }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public IReadOnlyList<string> Paths => _paths;

    public void Record(string path)
    {
        var fullPath = Path.GetFullPath(path);
        _paths.RemoveAll(existing => PathComparer.Equals(existing, fullPath));
        _paths.Insert(0, fullPath);
        if (_paths.Count > MaximumCount) _paths.RemoveRange(MaximumCount, _paths.Count - MaximumCount);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Preferences { Paths = _paths }));
            File.Move(temporary, _path, true);
        }
        catch (Exception exception) { _warn($"Could not save recent score preferences: {exception.Message}"); }
    }
}

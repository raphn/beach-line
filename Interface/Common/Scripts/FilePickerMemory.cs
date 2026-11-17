#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

/// <summary>Persists the shared file-dialog folder lists independently of the open score.</summary>
public sealed class FilePickerMemory
{
    private sealed class Preferences
    {
        public string LastDirectory { get; set; } = "";
        public string[] Favorites { get; set; } = [];
        public string[] Recent { get; set; } = [];
    }

    private readonly string _path;
    private readonly FileDialog _open;
    private readonly FileDialog _save;
    private Preferences _preferences = new();

    public FilePickerMemory(FileDialog open, FileDialog save)
    {
        _open = open;
        _save = save;
        _path = ProjectSettings.GlobalizePath("user://file_picker_preferences.json");
        try
        {
            if (File.Exists(_path))
                _preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_path)) ?? new Preferences();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Could not read file picker preferences: {exception.Message}");
        }

        FileDialog.SetFavoriteList((_preferences.Favorites ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
        FileDialog.SetRecentList((_preferences.Recent ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray());
        Attach(open);
        Attach(save);
    }

    /// <summary>Shares the saved folder and favorites behavior with another file dialog.</summary>
    public void RegisterDialog(FileDialog dialog) => Attach(dialog);

    private void Attach(FileDialog dialog)
    {
        dialog.UseNativeDialog = false;
        dialog.FavoritesEnabled = true;
        dialog.RecentListEnabled = true;
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible)
            {
                RememberDirectory(dialog.CurrentDir);
                Save();
            }
        };
        dialog.FileSelected += path =>
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                dialog.CurrentDir = directory;
                RememberDirectory(directory);
                Save();
            }
        };
    }

    public void Show(FileDialog dialog)
    {
        if (!string.IsNullOrWhiteSpace(_preferences.LastDirectory) && Directory.Exists(_preferences.LastDirectory))
            dialog.CurrentDir = _preferences.LastDirectory;
        dialog.PopupCenteredRatio(0.7f);
    }

    private void RememberDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            _preferences.LastDirectory = Path.GetFullPath(path);
    }

    public void Save()
    {
        if (_open.Visible) RememberDirectory(_open.CurrentDir);
        if (_save.Visible) RememberDirectory(_save.CurrentDir);
        _preferences.Favorites = FileDialog.GetFavoriteList();
        _preferences.Recent = FileDialog.GetRecentList();
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_preferences));
            File.Move(temporary, _path, true);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"Could not save file picker preferences: {exception.Message}");
        }
    }
}

#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using OpenTab.Music.Persistence;
using OpenTab.Music.Playback;
using BeachLine.Music.Audio;

public partial class DrumScorePresenter
{
    private Window _exportOptions = null!;
    private OptionButton _exportScope = null!, _exportFormat = null!;
    private Button _exportAudio = null!, _exportProject = null!, _cancelExport = null!;
    private Label _exportStatus = null!;
    private FileDialog _exportAudioFile = null!, _exportProjectFile = null!;
    private CancellationTokenSource? _exportCancellation;
    private const int ExportMenuId = 5;

    private void BindExportControls()
    {
        _exportOptions = GD.Load<PackedScene>("res://Interface/Export/Scenes/ExportOptions.tscn").Instantiate<Window>();
        AddChild(_exportOptions);
        var content = _exportOptions.GetNode<VBoxContainer>("Margin/Content");
        _exportScope = content.GetNode<OptionButton>("Scope");
        _exportFormat = content.GetNode<OptionButton>("Format");
        _exportAudio = content.GetNode<Button>("AudioButton");
        _exportProject = content.GetNode<Button>("ProjectButton");
        _cancelExport = content.GetNode<Button>("CancelExport");
        _exportStatus = content.GetNode<Label>("Status");
        _exportAudioFile = _exportOptions.GetNode<FileDialog>("AudioFile");
        _exportProjectFile = _exportOptions.GetNode<FileDialog>("ProjectFile");
        _filePickerMemory.RegisterDialog(_exportAudioFile);
        _filePickerMemory.RegisterDialog(_exportProjectFile);
        _exportFormat.AddItem("WAV", 0);
        _exportFormat.AddItem("FLAC", 1);
        _exportFormat.AddItem("Ogg Vorbis", 2);
        _exportFormat.AddItem("MP3", 3);
        _exportOptions.CloseRequested += () => { if (_exportCancellation is not null) _exportCancellation.Cancel(); else _exportOptions.Hide(); };
        _cancelExport.Pressed += () => _exportCancellation?.Cancel();
        _exportAudio.Pressed += ChooseAudioExport;
        _exportProject.Pressed += () =>
        {
            if (IsRecordingForExport()) return;
            _exportProjectFile.CurrentFile = "project.otab";
            _filePickerMemory.Show(_exportProjectFile);
        };
        _exportAudioFile.FileSelected += ExportAudioTo;
        _exportProjectFile.FileSelected += ExportPortableProjectTo;
        _fileMenu.AddSeparator();
        _fileMenu.AddItem(Tr("Export…"), ExportMenuId);
        _fileMenu.IdPressed += id => { if (id == ExportMenuId) ShowExportOptions(); };
    }

    private void RefreshExportLocalization()
    {
        if (_exportOptions is null) return;
        var menuIndex = _fileMenu.GetItemIndex(ExportMenuId);
        if (menuIndex >= 0) _fileMenu.SetItemText(menuIndex, Tr("Export…"));
        _exportOptions.Title = Tr("Export");
        _exportAudioFile.Title = Tr("Export audio");
        _exportProjectFile.Title = Tr("Export portable project");
        var chosen = _exportScope.Selected;
        _exportScope.Clear();
        _exportScope.AddItem(Tr("Whole score"));
        _exportScope.AddItem(Tr("Selected arrangement"));
        _exportScope.Select(Math.Clamp(chosen, 0, 1));
    }

    private void ShowExportOptions()
    {
        RefreshExportLocalization();
        _exportScope.SetItemDisabled(1, SelectedArrangement is null);
        if (SelectedArrangement is null && _exportScope.Selected == 1) _exportScope.Select(0);
        _exportStatus.Text = Tr("Audio export uses the current output sample rate.");
        _exportOptions.PopupCentered();
    }

    private bool IsRecordingForExport()
    {
        if (!_audioRecordingStarting && _audioRecorder?.IsTakeActive != true) return false;
        _exportStatus.Text = Tr("Stop or cancel the recording before exporting.");
        return true;
    }

    private void ChooseAudioExport()
    {
        if (IsRecordingForExport()) return;
        if (_soundFontAudio.IsScorePlaying || _arrangementAudio.IsScorePlaying)
        { _exportStatus.Text = Tr("Stop playback before exporting audio."); return; }
        var extension = ExportExtension();
        _exportAudioFile.Filters = [$"*.{extension} ; {extension.ToUpperInvariant()} audio"];
        _exportAudioFile.CurrentFile = $"mix.{extension}";
        _filePickerMemory.Show(_exportAudioFile);
    }

    private string ExportExtension() => _exportFormat.Selected switch { 1 => "flac", 2 => "ogg", 3 => "mp3", _ => "wav" };

    private void SetExportBusy(bool busy)
    {
        _exportAudio.Disabled = busy; _exportProject.Disabled = busy;
        _exportScope.Disabled = busy; _exportFormat.Disabled = busy;
        _cancelExport.Visible = busy;
    }

    private async void ExportAudioTo(string path)
    {
        if (_exportCancellation is not null || IsRecordingForExport()) return;
        if (_soundFontAudio.IsScorePlaying || _arrangementAudio.IsScorePlaying)
        { _exportStatus.Text = Tr("Stop playback before exporting audio."); return; }
        try
        {
            var snapshot = ScoreFile.Deserialize(ScoreFile.Serialize(_score));
            PlaybackLoopSelection? selection = null;
            if (_exportScope.Selected == 1)
            {
                if (SelectedArrangement is not { } arrangement || !arrangement.Sections.Any(entry => snapshot.Sections.Any(section => section.Id == entry.SectionId && section.Measures.Count > 0)))
                { _exportStatus.Text = Tr("Select an arrangement with at least one playable section."); return; }
                selection = new PlaybackLoopSelection(PlaybackLoopMode.ArrangementOnce, null,
                    arrangement.Sections.SelectMany(entry => Enumerable.Repeat(entry.SectionId, entry.Repetitions)).ToArray(), arrangement.Id);
            }
            var versions = CapturePlaybackVersions();
            var service = _soundFontAudio.CreateExportService();
            var format = _exportFormat.Selected switch { 1 => AudioExportFormat.Flac, 2 => AudioExportFormat.OggVorbis, 3 => AudioExportFormat.Mp3, _ => AudioExportFormat.Wav };
            var extension = "." + ExportExtension();
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
            _exportCancellation = new CancellationTokenSource();
            SetExportBusy(true); _exportStatus.Text = Tr("Exporting…");
            await AudioExportService.ExportAsync(snapshot, service, selection, versions, path, format, cancellationToken: _exportCancellation.Token);
            _exportStatus.Text = Tr("Export complete.");
        }
        catch (OperationCanceledException) { _exportStatus.Text = Tr("Export canceled."); }
        catch (Exception error) { _exportStatus.Text = string.Format(Tr("Could not export: {0}"), error.Message); GD.PushWarning(error.Message); }
        finally { _exportCancellation?.Dispose(); _exportCancellation = null; SetExportBusy(false); }
    }

    private async void ExportPortableProjectTo(string path)
    {
        if (_exportCancellation is not null || IsRecordingForExport()) return;
        try
        {
            if (!path.EndsWith(".otab", StringComparison.OrdinalIgnoreCase)) path += ".otab";
            var snapshot = ScoreFile.Deserialize(ScoreFile.Serialize(_score));
            _exportCancellation = new CancellationTokenSource();
            SetExportBusy(true); _cancelExport.Disabled = true; _exportStatus.Text = Tr("Exporting…");
            await Task.Run(() => PortableScoreFile.Save(path, snapshot));
            _exportStatus.Text = Tr("Export complete.");
        }
        catch (Exception error) { _exportStatus.Text = string.Format(Tr("Could not export: {0}"), error.Message); GD.PushWarning(error.Message); }
        finally { _cancelExport.Disabled = false; _exportCancellation?.Dispose(); _exportCancellation = null; SetExportBusy(false); }
    }
}

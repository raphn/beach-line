#nullable enable

using Godot;
using System;
using System.Linq;
using OpenTab.Music;
using OpenTab.Music.Persistence;

public partial class DrumScorePresenter
{
    private readonly ScoreHistory _scoreHistory = new();
    private PopupMenu? _historyMenu;

    private void ResetScoreHistory()
    {
        _scoreHistory.Reset(ScoreFile.Serialize(_score));
        UpdateScoreHistoryMenu();
    }

    private void InitializeScoreHistoryMenu()
    {
        var menuBar = GetNode<MenuBar>("%MenuBar");
        _historyMenu = new PopupMenu { Name = "Edit" };
        menuBar.AddChild(_historyMenu);
        _historyMenu.AddItem("Undo", 0);
        _historyMenu.AddItem("Redo", 1);
        _historyMenu.IdPressed += id => RestoreScoreHistory(id == 1);
        _historyMenu.AboutToPopup += UpdateScoreHistoryMenu;
        UpdateScoreHistoryMenu();
    }

    private void UpdateScoreHistoryMenu()
    {
        if (_historyMenu is null) return;
        _historyMenu.SetItemDisabled(0, !_scoreHistory.CanUndo);
        _historyMenu.SetItemDisabled(1, !_scoreHistory.CanRedo);
    }

    private void RecordScoreHistory()
    {
        if (_score is not null) _scoreHistory.Record(ScoreFile.Serialize(_score));
        UpdateScoreHistoryMenu();
    }

    /// <summary>Handles Ctrl+Z and Ctrl+Shift+Z when text editing does not own the shortcut.</summary>
    private bool HandleScoreHistoryShortcut(InputEventKey key)
    {
        if (!key.IsActionPressed("undo_score", false, true) && !key.IsActionPressed("redo_score", false, true)) return false;

        var focusOwner = GetViewport().GuiGetFocusOwner();
        if (focusOwner is LineEdit or TextEdit) return false;

        RestoreScoreHistory(key.IsActionPressed("redo_score", false, true));
        return true;
    }

    private void RestoreScoreHistory(bool redo)
    {
        // Restoring a snapshot replaces the document and can remove the row that owns
        // the recording controls while a take is being captured.
        if (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true)
        {
            SetAudioStatus("Stop or cancel the recording before using undo or redo.");
            return;
        }
        var moved = redo ? _scoreHistory.TryRedo(out var state) : _scoreHistory.TryUndo(out state);
        if (!moved) return;

        _score = ScoreFile.Deserialize(state);
        _selectedSectionIndex = System.Math.Clamp(_selectedSectionIndex, 0, _score.Sections.Count - 1);
        if (_selectedArrangementId is Guid arrangementId && !_score.Arrangements.Exists(item => item.Id == arrangementId))
            _selectedArrangementId = _score.Arrangements.Count == 0 ? null : _score.Arrangements[0].Id;
        if (_editingMeasureId is Guid measureId && !_score.Sections.Any(section => section.Measures.Any(measure => measure.Id == measureId)))
            _editingMeasureId = null;
        RebuildDrumDefinitions();
        _hasUnsavedChanges = true;
        _sessionDirty = true;
        _sessionDelay = 0;
        try
        {
            RenderSection();
            _metronomeButton.SetPressedNoSignal(_score.MetronomeEnabled);
            _soundFontAudio.MetronomeEnabled = _score.MetronomeEnabled;
            if (_arrangementAudio is not null) _arrangementAudio.MetronomeEnabled = _score.MetronomeEnabled;
            SyncCountInPlayback();
            _soundFontAudio.RefreshScore(_score, CapturePlaybackVersions());
            _arrangementAudio?.RefreshScore(_score);
        }
        catch (System.Exception exception)
        {
            GD.PushWarning($"Could not refresh the editor after history navigation: {exception.Message}");
        }
        UpdateScoreHistoryMenu();
    }
}


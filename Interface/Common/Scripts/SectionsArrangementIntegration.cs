#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;

public partial class DrumScorePresenter
{
    private Guid? _selectedArrangementId;
    private bool _arrangementLoop = true, _arrangementShortcuts;
    private SoundFontAudioNode _arrangementAudio = null!;
    private PanelContainer _arrangementPanel = null!;
    private StyleBox _arrangementPanelNormal = null!, _arrangementPanelActive = null!;
    private MenuButton _arrangementMenu = null!;
    private LineEdit _arrangementName = null!;
    private Button _arrangementDelete = null!, _arrangementPlay = null!, _arrangementStop = null!, _arrangementLoopButton = null!, _arrangementPrimary = null!;
    private HBoxContainer _arrangementThumbnails = null!;
    private Label _arrangementAudioSummary = null!;
    private Button _importArrangementButton = null!, _recordArrangementButton = null!;
    private OptionButton _arrangementAdd = null!;
    private PackedScene _arrangementThumbnailScene = null!;
    private ConfirmationDialog _arrangementDeleteDialog = null!;
    private Guid? _arrangementPendingDelete;
    private bool _arrangementStartInProgress;

    private ScoreArrangement? SelectedArrangement => _score?.Arrangements.FirstOrDefault(a => a.Id == _selectedArrangementId);

    private void BindArrangements()
    {
        var root = GetNode<PanelContainer>("%SectionsArrangement");
        _arrangementPanel = root;
        _arrangementPanelNormal = root.GetThemeStylebox("panel");
        _arrangementPanelActive = GD.Load<StyleBox>("res://Interface/Themes/Panels/arrangement_player_active.tres");
        const string toolbar = "VBoxContainer/BoxContainer/";
        const string player = toolbar + "PanelContainer/BoxContainer/";
        const string order = "VBoxContainer/HBoxContainer/ScrollContainer/SectionsOrder/BoxContainer/";
        _arrangementMenu = root.GetNode<MenuButton>(toolbar + "SelectArrangementMenuButton");
        _arrangementName = root.GetNode<LineEdit>(toolbar + "ArrangementNameEdit");
        _arrangementDelete = root.GetNode<Button>(toolbar + "DeleteArrangementButton");
        _arrangementPlay = root.GetNode<Button>(player + "PlayArrangementButton");
        _arrangementStop = root.GetNode<Button>(player + "StopArrangementButton");
        _arrangementLoopButton = root.GetNode<Button>(player + "LoopArrangementButton");
        _arrangementPrimary = root.GetNode<Button>(player + "PrimaryPlayerEnabledButton");
        _arrangementThumbnails = root.GetNode<HBoxContainer>(order + "SectionsThumbnails");
        _arrangementAudioSummary = root.GetNode<Label>("VBoxContainer/ArrangementAudioSummary");
        _importArrangementButton = root.GetNode<Button>("VBoxContainer/ArrangementAudioActions/ImportArrangementButton");
        _recordArrangementButton = root.GetNode<Button>("VBoxContainer/ArrangementAudioActions/RecordArrangementButton");
        _arrangementAdd = root.GetNode<OptionButton>(order + "AddSectionOptionButton");
        var deleteTarget = root.GetNode<ArrangementDropTarget>("VBoxContainer/HBoxContainer/DropToDeleteSection");
        _arrangementThumbnailScene = GD.Load<PackedScene>("res://Interface/Sections/Scenes/section_thumbnail.tscn");
        _arrangementAudio = new SoundFontAudioNode { Name = "ArrangementAudio" };
        AddChild(_arrangementAudio);
        BindAudioPlaybackDiagnostics(_arrangementAudio);
        _arrangementDeleteDialog = new ConfirmationDialog { Title = "Delete arrangement?", OkButtonText = "Delete" };
        AddChild(_arrangementDeleteDialog);
        _arrangementDeleteDialog.GetOkButton().ThemeTypeVariation = "AppDestructiveButton";
        _arrangementDeleteDialog.GetCancelButton().ThemeTypeVariation = "AppActionButton";
        _arrangementMenu.GetPopup().IdPressed += id =>
        {
            if (IsArrangementRecordingOrStarting())
            { SetAudioStatus("Finish or cancel the arrangement recording before switching arrangements."); return; }
            if (id == int.MaxValue) { StopArrangement(); var created = ArrangementEditing.Add(_score); _selectedArrangementId = created.Id; ArrangementChanged(); RenderSection(); }
            else if (id >= 0 && id < _score.Arrangements.Count) { StopArrangement(); _selectedArrangementId = _score.Arrangements[(int)id].Id; MarkEditorStateDirty(); RenderSection(); }
        };
        _arrangementName.TextSubmitted += _ => CommitArrangementName();
        _arrangementName.FocusExited += CommitArrangementName;
        _arrangementDelete.Pressed += () =>
        {
            if (SelectedArrangement is not { } selected) return;
            _arrangementPendingDelete = selected.Id;
            _arrangementDeleteDialog.DialogText = string.Format(Tr("Delete arrangement ‘{0}’?"), selected.Name);
            _arrangementDeleteDialog.PopupCentered();
        };
        _arrangementDeleteDialog.Confirmed += () =>
        {
            var selected = _score.Arrangements.FirstOrDefault(a => a.Id == _arrangementPendingDelete);
            _arrangementPendingDelete = null;
            if (selected is null) return;
            if (IsArrangementRecordingOrStarting() && _recordingArrangementId == selected.Id)
            { SetAudioStatus("Finish or cancel the arrangement recording before deleting its arrangement."); return; }
            StopArrangement(); _score.Arrangements.Remove(selected);
            _selectedArrangementId = _score.Arrangements.FirstOrDefault()?.Id;
            ArrangementChanged();
            RenderSection();
        };
        _arrangementPlay.Toggled += pressed =>
        {
            if ((_audioRecordingStarting || _audioRecorder?.IsTakeActive == true) && _recordingIntoArrangement)
            { SetRecordingPaused(!pressed); return; }
            if (pressed) StartArrangement(); else StopArrangement();
        };
        _arrangementStop.Pressed += () =>
        {
            if (_audioRecorder?.IsTakeActive == true && _recordingIntoArrangement) FinishAudioRecording();
            else StopArrangement();
        };
        _arrangementLoopButton.Toggled += pressed =>
        {
            if ((_audioRecordingStarting || _audioRecorder?.IsTakeActive == true) && _recordingIntoArrangement)
            { _arrangementLoopButton.SetPressedNoSignal(_arrangementLoop); return; }
            _arrangementLoop = pressed; MarkEditorStateDirty(); RestartArrangementIfPlaying();
        };
        _arrangementPrimary.Toggled += pressed => { _arrangementShortcuts = pressed; UpdateArrangementShortcutAppearance(); MarkEditorStateDirty(); };
        RefreshTransportShortcutTooltips();
        _arrangementPlay.TooltipText = "Play the arrangement from the beginning.";
        _arrangementStop.TooltipText = "Stop arrangement playback.";
        _arrangementLoopButton.TooltipText = "Repeat the arrangement.";
        _arrangementDelete.TooltipText = "Delete this arrangement.";
        const string audioActionsUnavailable = "Arrangement audio actions will be available when arrangement audio support is more mature.";
        _importArrangementButton.Disabled = true;
        _importArrangementButton.TooltipText = audioActionsUnavailable;
        _recordArrangementButton.Disabled = true;
        _recordArrangementButton.TooltipText = audioActionsUnavailable;
        deleteTarget.TooltipText = "Drop a section here to remove it from the arrangement.";
        _arrangementAdd.ItemSelected += index =>
        {
            if (SelectedArrangement is not { } selected || index <= 0 || index > _score.Sections.Count) return;
            try { ArrangementEditing.Append(_score, selected, _score.Sections[(int)index - 1].Id); ArrangementChanged(); }
            catch (Exception error) { GD.PushWarning(error.Message); }
        };
        deleteTarget.Dropped = index =>
        {
            if (SelectedArrangement is not { } selected || index < 0 || index >= selected.Sections.Count) return;
            selected.Sections.RemoveAt(index); ArrangementChanged();
        };
    }

    private void RefreshTransportShortcutTooltips()
    {
        var shortcut = AppPreferences.FormatBinding("play");
        if (_playPauseButton is not null)
            _playPauseButton.TooltipText = string.Format(Tr("Play or pause from the selected measure. {0} starts or stops playback."), shortcut);
        if (_arrangementPrimary is not null)
            _arrangementPrimary.TooltipText = string.Format(Tr("Route playback shortcuts to the arrangement player. While an arrangement plays, {0} stops all players."), shortcut);
    }

    private void CommitArrangementName()
    {
        if (SelectedArrangement is not { } selected) return;
        try { if (selected.Name != _arrangementName.Text.Trim()) { ArrangementEditing.Rename(_score, selected, _arrangementName.Text); ArrangementChanged(); } }
        catch (InvalidOperationException error) { _arrangementName.TooltipText = error.Message; _arrangementName.Text = selected.Name; }
    }

    private void ArrangementChanged()
    {
        NotifyScoreEdited();
        RestartArrangementIfPlaying();
        RenderArrangements();
    }

    private void RenderArrangements()
    {
        if (_arrangementMenu is null || _score is null) return;
        var selected = SelectedArrangement;
        var popup = _arrangementMenu.GetPopup(); popup.Clear();
        for (var i = 0; i < _score.Arrangements.Count; i++) popup.AddRadioCheckItem(_score.Arrangements[i].Name, i);
        if (_score.Arrangements.Count > 0) popup.AddSeparator();
        popup.AddItem(Tr("New arrangement"), int.MaxValue);
        for (var i = 0; i < _score.Arrangements.Count; i++) popup.SetItemChecked(i, _score.Arrangements[i].Id == _selectedArrangementId);
        _arrangementMenu.Text = selected?.Name ?? Tr("Select");
        if (!_arrangementName.HasFocus()) _arrangementName.Text = selected?.Name ?? "";
        _arrangementName.Editable = selected is not null;
        _arrangementDelete.Disabled = selected is null;
        _arrangementPlay.Disabled = selected is null || !selected.Sections.Any(e => _score.Sections.Any(s => s.Id == e.SectionId && s.Measures.Count > 0));
        _arrangementLoopButton.SetPressedNoSignal(_arrangementLoop);
        _arrangementPrimary.SetPressedNoSignal(_arrangementShortcuts);
        UpdateArrangementShortcutAppearance();
        _arrangementAdd.Clear(); _arrangementAdd.AddItem("+");
        foreach (var section in _score.Sections) _arrangementAdd.AddItem(SectionArrangementSymbol(section));
        _arrangementAdd.Select(0); _arrangementAdd.Disabled = selected is null;
        foreach (Node child in _arrangementThumbnails.GetChildren()) { _arrangementThumbnails.RemoveChild(child); child.QueueFree(); }
        if (selected is null) { _arrangementAudioSummary.Text = Tr("Select an arrangement to view its audio clips."); return; }
        var audioClips = selected.AudioClips.Select(clip =>
        {
            var asset = _score.AudioAssets.FirstOrDefault(item => item.Id == clip.AssetId);
            return asset is null ? Tr("Missing WAV") : asset.FileName;
        }).ToArray();
        _arrangementAudioSummary.Text = audioClips.Length == 0
            ? Tr("No arrangement audio clips. Import and recording are not available yet.")
            : string.Format(Tr("Arrangement audio · {0}"), string.Join(", ", audioClips));
        for (var i = 0; i < selected.Sections.Count; i++)
        {
            var entry = selected.Sections[i];
            var section = _score.Sections.FirstOrDefault(s => s.Id == entry.SectionId);
            if (section is null) continue;
            var thumbnail = _arrangementThumbnailScene.Instantiate<SectionThumbnail>();
            _arrangementThumbnails.AddChild(thumbnail);
            thumbnail.Index = i; thumbnail.Setup(SectionArrangementSymbol(section), entry.Repetitions);
            thumbnail.RepetitionChanged = (index, repetitions) =>
            {
                if (SelectedArrangement is not { } current || index < 0 || index >= current.Sections.Count || current.Sections[index].Repetitions == repetitions) return;
                current.Sections[index].Repetitions = repetitions; ArrangementChanged();
            };
            thumbnail.Reordered = (from, to) =>
            {
                if (SelectedArrangement is not { } current || from < 0 || from >= current.Sections.Count || to < 0 || to > current.Sections.Count) return;
                var item = current.Sections[from]; current.Sections.RemoveAt(from); if (to > from) to--;
                current.Sections.Insert(to, item); ArrangementChanged();
            };
        }
    }

    private void UpdateArrangementShortcutAppearance()
    {
        if (_arrangementPanel is null || _arrangementPrimary is null) return;
        _arrangementPrimary.Text = Tr(_arrangementShortcuts ? "Shortcuts: On" : "Shortcuts: Off");
        _arrangementPanel.AddThemeStyleboxOverride("panel", _arrangementShortcuts ? _arrangementPanelActive : _arrangementPanelNormal);
    }

    private string SectionArrangementSymbol(ScoreSection section)
    {
        var index = _score.Sections.ToList().FindIndex(item => item.Id == section.Id);
        return string.IsNullOrWhiteSpace(section.Name) ? GetSectionFallbackLabel(index) : section.Name.Trim();
    }

    private PlaybackLoopSelection ArrangementSelection(ScoreArrangement selected) => new(
        _arrangementLoop ? PlaybackLoopMode.ArrangementLoop : PlaybackLoopMode.ArrangementOnce, null,
        selected.Sections.SelectMany(e => Enumerable.Repeat(e.SectionId, e.Repetitions)).ToArray(), selected.Id);

    private void RevealArrangementSections(ScoreArrangement selected)
    {
        var referencedIds = selected.Sections.Select(entry => entry.SectionId).ToHashSet();
        var changed = false;
        foreach (var section in _score.Sections)
            if (referencedIds.Contains(section.Id)) changed |= _visibleSectionIds.Add(section.Id);
        if (!changed) return;

        // Keep the current focus and selection; only add the arrangement's sections to the view.
        MarkEditorStateDirty();
        RenderSection();
    }

    private void StartArrangement(bool recording = false)
    {
        if (_arrangementStartInProgress) return;
        if (SelectedArrangement is not { } selected || _arrangementPlay.Disabled) { _arrangementPlay.SetPressedNoSignal(false); return; }
        if (recording)
        {
            if (!_audioRecordingStarting || !_recordingIntoArrangement || _recordingArrangementId != selected.Id) return;
        }
        else if (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true || IsArrangementRecordingOrStarting())
        {
            return;
        }

        _arrangementStartInProgress = true;
        try
        {
            RevealArrangementSections(selected);
            StopPlayback(); _arrangementAudio.StopScore();
            _arrangementAudio.SetLoopSelection(_score, ArrangementSelection(selected));
            _arrangementAudio.PlayScore(_score, recording: recording);
            _arrangementPlay.SetPressedNoSignal(_arrangementAudio.IsScorePlaying);
            _arrangementStop.Disabled = !_arrangementAudio.IsScorePlaying;
            UpdateArrangementEditorLock();
        }
        catch (Exception error) { _arrangementPlay.SetPressedNoSignal(false); UpdateArrangementEditorLock(); GD.PushWarning($"Could not play arrangement: {error.Message}"); }
        finally { _arrangementStartInProgress = false; }
    }

    private void StopArrangement()
    {
        if (_arrangementAudio is null) return;
        _arrangementAudio.StopScore();
        _arrangementPlay?.SetPressedNoSignal(false);
        if (_arrangementStop is not null) _arrangementStop.Disabled = true;
        UpdateArrangementEditorLock();
    }

    private void PauseArrangementTransport()
    {
        if (_arrangementAudio is { IsScorePlaying: true, IsScorePaused: false })
        {
            _arrangementAudio.PauseScore();
            _arrangementPlay?.SetPressedNoSignal(false);
        }
    }

    private void ResumeArrangementTransport()
    {
        if (_arrangementAudio is { IsScorePaused: true })
        {
            _arrangementAudio.ResumeScore();
            _arrangementPlay?.SetPressedNoSignal(_arrangementAudio.IsScorePlaying);
        }
    }

    private void RestartArrangementIfPlaying()
    {
        if (IsArrangementRecordingOrStarting())
        { SetAudioStatus("Arrangement edits are saved, but playback stays on its current sequence until the recording ends."); return; }
        if (_arrangementAudio is null || !_arrangementAudio.IsScorePlaying) return;
        if (SelectedArrangement is null) { StopArrangement(); return; }
        StartArrangement();
    }

    private bool IsArrangementRecordingOrStarting() =>
        _recordingIntoArrangement && (_audioRecordingStarting || _audioRecorder?.IsTakeActive == true);

    private void PollArrangementPlayer()
    {
        if (_arrangementAudio is null || _arrangementPlay is null) return;
        var playing = _arrangementAudio.IsScorePlaying;
        if (!playing) { _arrangementPlay.SetPressedNoSignal(false); _arrangementStop.Disabled = true; }
        UpdateArrangementEditorLock();
        if (SelectedArrangement is { } selected)
        {
            var timelineFrame = playing ? _arrangementAudio.VisualTimelineFrame : null;
            var sectionSpans = playing ? _arrangementAudio.VisualSectionTimelineSpans : Array.Empty<PlaybackSectionTimelineSpan>();
            var occurrenceCursor = 0;
            foreach (var thumbnail in _arrangementThumbnails.GetChildren().OfType<SectionThumbnail>())
            {
                if (thumbnail.Index >= selected.Sections.Count) continue;
                var entry = selected.Sections[thumbnail.Index];
                var section = _score.Sections.FirstOrDefault(s => s.Id == entry.SectionId);
                if (section is not null && thumbnail.GetNode<Label>("SectionSymbol").Text != SectionArrangementSymbol(section))
                { RenderArrangements(); break; }

                if (section is null)
                {
                    thumbnail.SetPlaybackProgress(0.0);
                    continue;
                }

                var occurrenceStart = occurrenceCursor;
                occurrenceCursor += entry.Repetitions;
                var occurrencesMatch = occurrenceStart + entry.Repetitions <= sectionSpans.Count;
                if (occurrencesMatch)
                    for (var repetition = 0; repetition < entry.Repetitions; repetition++)
                        if (sectionSpans[occurrenceStart + repetition].SectionId != entry.SectionId)
                        { occurrencesMatch = false; break; }
                if (!occurrencesMatch)
                {
                    thumbnail.SetPlaybackProgress(0.0);
                    continue;
                }

                var occurrenceSpans = new List<PlaybackSectionTimelineSpan>(entry.Repetitions);
                for (var repetition = 0; repetition < entry.Repetitions; repetition++)
                    occurrenceSpans.Add(sectionSpans[occurrenceStart + repetition]);

                var totalFrames = occurrenceSpans.Sum(span => Math.Max(0L, span.EndFrame - span.StartFrame));
                var elapsedFrames = 0L;
                if (timelineFrame.HasValue)
                {
                    foreach (var span in occurrenceSpans)
                    {
                        var duration = Math.Max(0L, span.EndFrame - span.StartFrame);
                        if (timelineFrame.Value >= span.EndFrame) elapsedFrames += duration;
                        else if (timelineFrame.Value > span.StartFrame)
                        {
                            elapsedFrames += Math.Min(duration, timelineFrame.Value - span.StartFrame);
                            break;
                        }
                        else break;
                    }
                }
                thumbnail.SetPlaybackProgress(totalFrames > 0 ? elapsedFrames * 100.0 / totalFrames : 0.0);
            }
        }
    }
}

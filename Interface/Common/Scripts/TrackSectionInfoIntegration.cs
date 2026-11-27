#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private readonly Dictionary<Guid, (Button Lead, RichTextLabel Section)> _trackSectionInfo = new();

    // A track's lead status belongs to the focused section, even when several sections are visible.
    private void BindTrackSectionInfo(ScoreTrack track, Control host)
    {
        var lead = host.GetNode<Button>("LeadButton");
        var sectionInfo = host.GetNode<RichTextLabel>("SectionInfoHolder");
        _trackSectionInfo[track.Id] = (lead, sectionInfo);
        lead.Pressed += () =>
        {
            if (_score.Sections.Count == 0 || !_score.Tracks.Contains(track)) return;
            var section = GetSelectedSection();
            SetActiveEditorTrack(track.Id);
            _score.SetLeadTrack(section, track);
            NotifyScoreEdited();
            RenderSection();
        };
        RefreshTrackSectionInfo();
    }

    private void RefreshTrackSectionInfo()
    {
        if (_score is null || _score.Sections.Count == 0) return;
        var section = GetSelectedSection();
        RefreshTrackTitles();
        foreach (var (trackId, controls) in _trackSectionInfo)
        {
            if (!GodotObject.IsInstanceValid(controls.Lead) || !GodotObject.IsInstanceValid(controls.Section)) continue;
            var isLead = section.LeadTrackId == trackId;
            controls.Section.Text = $"[i]Section[/i] [b]{section.Name} · {section.Title}[/b]";
            controls.Section.TooltipText = $"{section.Name} · {section.Title}";
            controls.Lead.Text = TranslationServer.Translate(isLead ? "Lead" : "Set lead");
            controls.Lead.SetPressedNoSignal(isLead);
            controls.Lead.Disabled = isLead;
            controls.Lead.ThemeTypeVariation = isLead ? "AppLeadActiveButton" : "AppLeadButton";
            controls.Lead.TooltipText = TranslationServer.Translate(isLead ? "This track determines the focused section length." : "Make this track determine the focused section length.");
        }
    }
}

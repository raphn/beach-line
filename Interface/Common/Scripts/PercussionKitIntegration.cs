#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
    private static void ConfigureDefaultPercussionKit(ScoreTrack track)
    {
        track.AddPercussionPiece(Guid.NewGuid().ToString("N"), "Kick", 36);
        track.AddPercussionPiece(Guid.NewGuid().ToString("N"), "Snare", 38);
        track.AddPercussionPiece(Guid.NewGuid().ToString("N"), "Closed hi-hat", 42);
        track.AddPercussionPiece(Guid.NewGuid().ToString("N"), "Low tom", 45);
        track.AddPercussionPiece(Guid.NewGuid().ToString("N"), "High tom", 50);
        track.AddPercussionPiece(Guid.NewGuid().ToString("N"), "Crash cymbal", 49);
    }

    private void RenderPercussionKitTrack(ScoreTrack track, List<TabBar> bars, IReadOnlyList<ScoreSection> visibleSections)
    {
        var row = GD.Load<PackedScene>("res://Interface/Tab/Scenes/PercussionKitTrack.tscn").Instantiate<FoldableContainer>();
        row.Name = $"Track_{track.Id:N}";
        _tabTracks.AddChild(row);

        row.Folded = _trackFoldedStates.GetValueOrDefault(track.Id, row.Folded);
        RegisterTrackRow(track.Id, row);
        row.FoldingChanged += folded => { _trackFoldedStates[track.Id] = folded; MarkEditorStateDirty(); };
        var controls = row.GetNode<Control>("TrackContainer/VBoxContainer/TrackControls/HBoxContainer");
        var name = controls.GetNode<Label>("HBoxContainer/NameContainer/TrackNameLabel");
        var edit = controls.GetNode<LineEdit>("HBoxContainer/NameContainer/TrackNameEdit");
        name.Text = track.Name; name.MouseFilter = Control.MouseFilterEnum.Stop;
        controls.GetNode<TextureRect>("HBoxContainer/TextureRect").Texture = GD.Load<Texture2D>(TrackSettingsDialog.ResolveImagePath(track));
        name.TooltipText = "Double-click to rename this kit.";
        
        name.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true, DoubleClick: true }) { name.Visible = false; edit.Visible = true; edit.Text = track.Name; edit.GrabFocus(); edit.SelectAll(); } };
        var committing = false;
        void CommitName()
        {
            if (committing) return;
            committing = true;
            var value = edit.Text.Trim();
            if (value.Length > 0 && value != track.Name) { track.Name = value; NotifyScoreEdited(); }
            name.Text = track.Name; RefreshTrackTitles(); edit.Visible = false; name.Visible = true;
            committing = false;
        }
        edit.TextSubmitted += _ => CommitName(); edit.FocusExited += CommitName;
        var mute = controls.GetNode<Button>("HBoxContainer/Config/MuteButton");
        var solo = controls.GetNode<Button>("HBoxContainer/Config/SoloButton");
        mute.ToggleMode = true; mute.ButtonPressed = track.IsMuted;
        solo.ToggleMode = true; solo.ButtonPressed = track.IsSoloed;
        mute.Toggled += value => { track.IsMuted = value; NotifyScoreEdited(); };
        solo.Toggled += value => { track.IsSoloed = value; NotifyScoreEdited(); };
        var volume = controls.GetNode<HSlider>("HBoxContainer/HBoxContainer2/Volume");
        var volumeLabel = controls.GetNode<Label>("HBoxContainer/HBoxContainer2/Label");
        volume.Step = 1; volume.Value = track.VolumeDb; volumeLabel.Text = $"{volume.Value:0.#} dB";
        volume.ValueChanged += value => { track.VolumeDb = value; volumeLabel.Text = $"{value:0.#} dB"; NotifyScoreEdited(); };
        controls.GetNode<Button>("HBoxContainer/Config/TrackSettingsButton").Pressed += () => ShowTrackSettings(track);
        var confirmation = new ConfirmationDialog { Title = "Delete percussion kit?", DialogText = "Delete this kit and its contents from all sections?", OkButtonText = "Delete" };
        row.AddChild(confirmation);
        controls.GetNode<Button>("DeleteButton").Pressed += () => confirmation.PopupCentered();
        confirmation.Confirmed += () => DeleteNonPercussionTrack(track);

        var header = controls.GetNode<Control>("HeaderContainer");
        foreach (var child in header.GetChildren().OfType<Control>())
            if (child.Name != "AddNewSubTrack" && child.Name != "TrackSectionInfo") child.Visible = false;
        BindTrackSectionInfo(track, header.GetNode<Control>("TrackSectionInfo"));
        var add = header.GetNode<Button>("AddNewSubTrack");
        var menu = new PopupMenu(); add.AddChild(menu);
        var presets = new (string Name, int Pitch)[] { ("Kick", 36), ("Snare", 38), ("Closed hi-hat", 42), ("Open hi-hat", 46), ("Low tom", 45), ("High tom", 50), ("Crash cymbal", 49), ("Ride cymbal", 51), ("Clap", 39) };
        for (int i = 0; i < presets.Length; i++) menu.AddItem(presets[i].Name, i);
        add.Pressed += () => { menu.Position = new Vector2I((int)add.GlobalPosition.X, (int)(add.GlobalPosition.Y + add.Size.Y)); menu.Popup(); };
        menu.IdPressed += id =>
        {
            var preset = presets[(int)id];
            try { track.AddPercussionPiece(Guid.NewGuid().ToString("N"), preset.Name, preset.Pitch); NotifyScoreEdited(); RenderSection(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { SetTabStatus(error.Message); }
        };

        var host = row.GetNode<VBoxContainer>("TrackContainer/VBoxContainer/TabSectionContainer/SubTracks");
        var lanes = host.GetNode<HBoxContainer>("KitLanes");
        var headers = lanes.GetNode<VBoxContainer>("SubTrackHeaders");
        var spacer = headers.GetNode<Control>("HeaderSpacer");
        headers.AddThemeConstantOverride("separation", 0);
        headers.SizeFlagsVertical = Control.SizeFlags.Fill | Control.SizeFlags.Expand;
        var pieceHeaders = new List<Control>();
        var headerScene = GD.Load<PackedScene>("res://Interface/Tab/Scenes/percussion_sub_track_header.tscn");
        foreach (var piece in track.PercussionPieces)
        {
            var pieceHeader = headerScene.Instantiate<Control>(); headers.AddChild(pieceHeader);
            pieceHeaders.Add(pieceHeader);
            var pieceName = pieceHeader.GetNode<Label>("BoxContainer/HBoxContainer/NameContainer/TrackNameLabel");
            var pieceEdit = pieceHeader.GetNode<LineEdit>("BoxContainer/HBoxContainer/NameContainer/TrackNameEdit");
            pieceName.Text = piece.Name; pieceName.MouseFilter = Control.MouseFilterEnum.Stop;
            pieceName.TooltipText = string.Format(Tr("MIDI note {0}. Double-click to rename."), piece.MidiPitch);
            var pieceIcon = pieceHeader.GetNode<TextureRect>("BoxContainer/HBoxContainer/PieceTexture");
            // Drum atlas textures use unverified artwork kept in the local project.
            // The licensed generic drum marker keeps public builds complete without
            // trying to load excluded resources or emitting missing-file warnings.
            pieceIcon.Visible = true;
            pieceIcon.Texture = GD.Load<Texture2D>("res://Interface/Common/Graphs/plain-circle_delapouite.png");
            pieceName.GuiInput += input => { if (input is InputEventMouseButton { Pressed: true, DoubleClick: true }) { pieceName.Visible = false; pieceEdit.Visible = true; pieceEdit.Text = piece.Name; pieceEdit.GrabFocus(); pieceEdit.SelectAll(); } };
            var editing = false;
            void CommitPieceName() { if (editing) return; editing = true; var text = pieceEdit.Text.Trim(); if (text.Length > 0 && text != piece.Name) { piece.Name = text; NotifyScoreEdited(); } pieceName.Text = piece.Name; pieceEdit.Visible = false; pieceName.Visible = true; editing = false; }
            pieceEdit.TextSubmitted += _ => CommitPieceName(); pieceEdit.FocusExited += CommitPieceName;
            var pieceMute = pieceHeader.GetNode<Button>("BoxContainer/HBoxContainer/Config/MuteButton");
            pieceMute.ToggleMode = true; pieceMute.ButtonPressed = piece.IsMuted;
            pieceMute.Toggled += value => { piece.IsMuted = value; NotifyScoreEdited(); };
            pieceHeader.GetNode<Button>("BoxContainer/HBoxContainer/Config/SoloButton").Visible = false;
            var gain = pieceHeader.GetNode<HSlider>("BoxContainer/HBoxContainer2/Volume");
            var gainLabel = pieceHeader.GetNode<Label>("BoxContainer/HBoxContainer2/Label");
            gain.Step = 1; gain.Value = piece.GainDb; gainLabel.Text = $"{gain.Value:0.#} dB";
            gain.ValueChanged += value => { piece.GainDb = value; gainLabel.Text = $"{value:0.#} dB"; NotifyScoreEdited(); };
            var info = new AcceptDialog { Title = Tr("Percussion piece"), DialogText = string.Format(Tr("{0}\nMIDI note: {1}"), piece.Name, piece.MidiPitch) };
            pieceHeader.AddChild(info);
            pieceHeader.GetNode<Button>("BoxContainer/HBoxContainer/Config/TrackSettingsButton").Pressed += () => info.PopupCentered();
            var delete = new ConfirmationDialog { Title = Tr("Delete percussion piece?"), DialogText = string.Format(Tr("Delete {0} and all its hits across every section?"), piece.Name), OkButtonText = Tr("Delete") };
            pieceHeader.AddChild(delete);
            pieceHeader.GetNode<Button>("BoxContainer/DeleteButton").Pressed += () => delete.PopupCentered();
            delete.Confirmed += () => { try { if (track.RemovePercussionPiece(piece.Id, _score.Sections.SelectMany(section => section.Measures).SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content))).Where(part => part.TrackId == track.Id), confirmDeleteHits: true)) { NotifyScoreEdited(); RenderSection(); } else SetTabStatus("The percussion piece could not be removed."); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { SetTabStatus(error.Message); } };
        }
        var scroll = CreateTrackTabPages(track.Id, lanes, out var fixedHeaders, out var pageSections);
        BindTrackTabHeader(track.Id, header.GetNode<Control>("TabControls"), scroll, multiSection: visibleSections.Count > 1);
        foreach (var section in visibleSections)
        {
            var template = _nonPercussionTrackScene.Instantiate<FoldableContainer>();
            var sectionContainer = template.GetNode<Control>("TrackContainer/HBoxContainer/TabSectionContainer");
            sectionContainer.GetParent().RemoveChild(sectionContainer); template.Free();
            pageSections.AddChild(sectionContainer);
            RenderNonPercussionSection(track, section, bars, scroll, sectionContainer);
            var sectionHeader = sectionContainer.GetNode<Control>("VBoxContainer/Header");
            var marker = sectionContainer.GetNode<Label>("VBoxContainer/SectionMarker");
            if (visibleSections.Count > 1)
            {
                sectionHeader.Visible = false;
                marker.Visible = true;
            }
            else
            {
                sectionHeader.GetParent().RemoveChild(sectionHeader);
                fixedHeaders.AddChild(sectionHeader);
            }
        }

        bool layoutUpdateQueued = false;
        void QueueKitAlignmentUpdate()
        {
            if (layoutUpdateQueued || !GodotObject.IsInstanceValid(row) || !row.IsInsideTree()) return;
            layoutUpdateQueued = true;
            Callable.From(UpdateKitAlignment).CallDeferred();
        }

        void UpdateKitAlignment()
        {
            layoutUpdateQueued = false;
            if (!GodotObject.IsInstanceValid(row) || !row.IsInsideTree() || !GodotObject.IsInstanceValid(headers) || !GodotObject.IsInstanceValid(spacer)) return;

            var views = FindControls<MeasureTabView>(pageSections)
                .Where(view => GodotObject.IsInstanceValid(view) && view.IsInsideTree() && view.ViewData?.IsPercussion == true)
                .ToArray();
            if (views.Length == 0)
            {
                SetMinimumHeight(spacer, 0);
                return;
            }

            float naturalHeaderMinimumHeight = 0;
            foreach (var pieceHeader in pieceHeaders)
            {
                if (!GodotObject.IsInstanceValid(pieceHeader) || pieceHeader.IsQueuedForDeletion()) continue;
                naturalHeaderMinimumHeight = Math.Max(naturalHeaderMinimumHeight, GetNaturalPieceHeaderMinimumHeight(pieceHeader));
            }
            float effectiveRowHeight = MeasureTabView.ResolveSharedRowHeight(views.Max(view => view.ThemeRowHeight), naturalHeaderMinimumHeight);

            var measureHeaders = views
                .Select(view => view.GetParent()?.GetParent()?.GetNodeOrNull<Control>("MeasureHeader"))
                .Where(measureHeader => measureHeader is not null && GodotObject.IsInstanceValid(measureHeader))
                .Cast<Control>()
                .Distinct()
                .ToArray();
            var commonMeasureHeaderHeight = measureHeaders
                .Select(GetNaturalMeasureHeaderMinimumHeight)
                .DefaultIfEmpty(0f)
                .Max();
            commonMeasureHeaderHeight = MathF.Ceiling(commonMeasureHeaderHeight);
            foreach (var measureHeader in measureHeaders) SetMinimumHeight(measureHeader, commonMeasureHeaderHeight);

            foreach (var view in views) view.SetEffectiveRowHeight(effectiveRowHeight);
            foreach (var pieceHeader in pieceHeaders)
                if (GodotObject.IsInstanceValid(pieceHeader) && !pieceHeader.IsQueuedForDeletion()) SetMinimumHeight(pieceHeader, effectiveRowHeight);

            // Use the actual lane origin from the rendered first measure. Since
            // the header VBox has zero separation, this spacer places row one
            // directly against that origin without compensating constants.
            var firstView = views.OrderBy(view => view.GlobalPosition.Y).First();
            var spacerHeight = MeasureTabView.ResolveHeaderSpacerHeight(firstView.GlobalPosition.Y, firstView.RowStart, headers.GlobalPosition.Y);
            SetMinimumHeight(spacer, spacerHeight);
        }

        headers.Resized += QueueKitAlignmentUpdate;
        foreach (var pieceHeader in pieceHeaders)
        {
            pieceHeader.Resized += QueueKitAlignmentUpdate;
            pieceHeader.MinimumSizeChanged += QueueKitAlignmentUpdate;
            var pieceContent = pieceHeader.GetNodeOrNull<Control>("BoxContainer");
            if (pieceContent is not null) pieceContent.MinimumSizeChanged += QueueKitAlignmentUpdate;
        }
        foreach (var view in FindControls<MeasureTabView>(pageSections))
        {
            view.Resized += QueueKitAlignmentUpdate;
            view.LayoutGeometryChanged += QueueKitAlignmentUpdate;
            var measureHeader = view.GetParent()?.GetParent()?.GetNodeOrNull<Control>("MeasureHeader");
            if (measureHeader is not null)
            {
                measureHeader.Resized += QueueKitAlignmentUpdate;
                measureHeader.MinimumSizeChanged += QueueKitAlignmentUpdate;
                var measureHeaderContent = measureHeader.GetNodeOrNull<Control>("HeaderContent");
                if (measureHeaderContent is not null) measureHeaderContent.MinimumSizeChanged += QueueKitAlignmentUpdate;
            }
        }
        pageSections.Resized += QueueKitAlignmentUpdate;
        fixedHeaders.Resized += QueueKitAlignmentUpdate;
        QueueKitAlignmentUpdate();
    }

    private static IEnumerable<T> FindControls<T>(Node root) where T : Node
    {
        foreach (var child in root.GetChildren())
        {
            if (child is T match) yield return match;
            foreach (var nested in FindControls<T>(child)) yield return nested;
        }
    }

    private static void SetMinimumHeight(Control control, float height)
    {
        if (Math.Abs(control.CustomMinimumSize.Y - height) < 0.25f) return;
        control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, height);
    }

    private static float GetNaturalPieceHeaderMinimumHeight(Control header)
    {
        var content = header.GetNodeOrNull<Control>("BoxContainer");
        var style = header.GetThemeStylebox("panel");
        return (content?.GetCombinedMinimumSize().Y ?? 0f) + (style?.GetMinimumSize().Y ?? 0f);
    }

    private static float GetNaturalMeasureHeaderMinimumHeight(Control header)
    {
        var content = header.GetNodeOrNull<Control>("HeaderContent");
        var style = header.GetThemeStylebox("panel");
        return (content?.GetCombinedMinimumSize().Y ?? 0f) + (style?.GetMinimumSize().Y ?? 0f);
    }
}





#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;
using OpenTab.Music.Playback;

// Reusable non-percussion track controls and tablature measures bound to the shared score.
public partial class DrumScorePresenter
{
	private readonly PackedScene _tabStatusBarScene = GD.Load<PackedScene>("res://Interface/Tab/Scenes/TabStatusBar.tscn");
	private VBoxContainer _tabTracks = null!;
	private PackedScene _tabMeasureScene = null!;
	private PackedScene _nonPercussionTrackScene = null!;
	private readonly PackedScene _measureVersionControlsScene = GD.Load<PackedScene>("res://Interface/Tab/Scenes/MeasureVersionControls.tscn");
	private readonly Dictionary<(Guid MeasureId, Guid TrackId), Guid> _selectedMeasureVersions = new();
	private PlaybackVersionSelection CapturePlaybackVersions() => new(_selectedMeasureVersions);
	private Label _tabStatus = null!;
	private string _tabStatusSource = "";
	private object[] _tabStatusArguments = System.Array.Empty<object>();
	private int _tabSnapDenominator = 16;
	private readonly Dictionary<Guid, int> _tabSnapDenominators = new();

	private int GetTabSnapDenominator(Guid trackId)
	{
		var stored = _score?.Tracks.FirstOrDefault(track => track.Id == trackId)?.TabSnapDenominator;
		return stored is 4 or 8 or 16 or 32 or 64 ? stored.Value : _tabSnapDenominators.GetValueOrDefault(trackId, _tabSnapDenominator);
	}
	private int GetMeasureSnapDenominator(Guid trackId, TrackMeasure content) => content.SnapDenominator is 4 or 8 or 16 or 32 or 64
		? content.SnapDenominator : GetTabSnapDenominator(trackId);
	private void InitializeMeasureSnapDefaults(ScoreMeasure measure)
	{
		foreach (var part in measure.TrackMeasures)
			if (part.SnapDenominator == 0) part.SnapDenominator = GetTabSnapDenominator(part.TrackId);
	}
	private void MaterializeTrackSnapDefaults(Guid trackId)
	{
		var fallback = GetTabSnapDenominator(trackId);
		foreach (var part in _score.Sections.SelectMany(section => section.Measures)
			.SelectMany(measure => measure.TrackMeasures.Concat(measure.TrackMeasureVersions.Select(version => version.Content)))
			.Where(part => part.TrackId == trackId && part.SnapDenominator == 0))
			part.SnapDenominator = fallback;
	}
	private int GetActiveTabSnapDenominator() => _selectedNonPercussionTrackId is { } id ? GetTabSnapDenominator(id) : _tabSnapDenominator;

	// Keep the score readable while allowing several short measures to fit in a
	// normal workspace. If the score is wider than the viewport, the ScrollContainer
	// retains horizontal scrolling below this floor.
	private static float TabMeasureMinimumWidth => GetTabThemeConstant("minimum_measure_width", 180f, 80f, 2000f);
	private static float GetTabThemeConstant(string name, float fallback, float minimum, float maximum)
	{
		var theme = GD.Load<Theme>("res://Interface/Themes/main.tres");
		float value = theme is not null && theme.HasConstant(name, "TabMeasureView")
			? theme.GetConstant(name, "TabMeasureView") : fallback;
		return Math.Clamp(float.IsFinite(value) ? value : fallback, minimum, maximum);
	}
	private Guid? _selectedNonPercussionTrackId;
	private readonly Dictionary<Guid, int> _selectedRepeatSourceIndices = new();
	private readonly Dictionary<(Guid SectionId, Guid TrackId), int> _sectionRepeatSourceIndices = new();
	private PendingTabCursor? _pendingTabCursor;
	private readonly Dictionary<Guid, int> _trackDisplayModes = new();
	private readonly Dictionary<Guid, bool> _trackFoldedStates = new();
	private int _visibleTabMeasureCount;
	private bool _tabWidthUpdateQueued;
	private sealed record TabBar(ScoreSection Section, ScoreMeasure Measure, int Index, ScoreTime Start, ScoreTime Duration);
	private sealed record TabOccurrence(TabEvent Note, TrackMeasure Source, ScoreTime Start, ScoreTime End, bool IsRepeat, int DestinationIndex);
	private sealed record PendingTabCursor(Guid TrackId, ScoreSection Section, int MeasureIndex, int StringNumber, ScoreTime Onset);

	private void BindTabEditor()
	{
		BindAudioTrackEditor();
		GetNode<Button>("%AddMidiTrackButton").Pressed += ShowNewTrackSettings;
		var workspaceScroll = GetNode<ScrollContainer>("%ScrollContainer");
		workspaceScroll.GetHScrollBar().ValueChanged += value => { _lastScrollX = (int)value; MarkEditorStateDirty(); };
		workspaceScroll.GetVScrollBar().ValueChanged += value => { _lastScrollY = (int)value; MarkEditorStateDirty(); };
		workspaceScroll.Resized += QueueResponsiveTabWidthUpdate;
		_tabTracks = GetNode<VBoxContainer>("%Tracks");
		_tabMeasureScene = GD.Load<PackedScene>("res://Interface/Tab/Scenes/MeasureTab.tscn");
		_nonPercussionTrackScene = GD.Load<PackedScene>("res://Interface/Tab/Scenes/NonPercussionTrack.tscn");
	}

	private void RenderTabSection(ScoreSection section)
	{
		_tabClipboardViews.Clear();
		_trackRows.Clear();
		ClearChildren(_tabTracks);
		var tools = _tabStatusBarScene.Instantiate<HFlowContainer>();
		_tabTracks.AddChild(tools);
		_tabStatus = tools.GetNode<Label>("Status");
		RefreshTabStatusTooltip();

		var bars = BuildTabBars();
		var visibleSections = GetVisibleSections().ToList();
		_visibleTabMeasureCount = visibleSections.Sum(visible => visible.Measures.Count);
		foreach (var track in _score.Tracks)
			if (track.Kind == TrackKind.Ruler) continue;
			else if (track.Kind == TrackKind.Audio) RenderAudioTrack(track, section);
			else if (track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit)
				_tabTracks.AddChild(new Label { Text = $"Legacy percussion track '{track.Name}' is preserved but cannot be edited in the kit editor." });
			else if (track.Kind == TrackKind.Percussion) RenderPercussionKitTrack(track, bars, visibleSections);
			else RenderNonPercussionTrack(track, bars, visibleSections);
		RenderAudioOnlyRuler(section);
		QueueResponsiveTabWidthUpdate();
	}

	private void RenderNonPercussionTrack(ScoreTrack track, List<TabBar> bars, IReadOnlyList<ScoreSection> visibleSections)
	{
		var row = _nonPercussionTrackScene.Instantiate<FoldableContainer>();
		row.Name = $"Track_{track.Id:N}";
		_tabTracks.AddChild(row);

		row.Folded = _trackFoldedStates.GetValueOrDefault(track.Id, row.Folded);
		RegisterTrackRow(track.Id, row);
		row.FoldingChanged += folded => { _trackFoldedStates[track.Id] = folded; MarkEditorStateDirty(); };
		BindFloatingBoardButtons(track.Id, row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/ToggleKeyboardButton"), row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/ToggleFretboardButton"));
		var deleteTrack = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/PanelContainer/DeleteButton");
		var nameLabel = row.GetNode<Label>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/NameContainer/TrackNameLabel");
		var nameEdit = row.GetNode<LineEdit>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/NameContainer/TrackNameEdit");
		var icon = row.GetNode<TextureRect>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/PanelContainer/TextureRect");
		var settings = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/TrackSettingsButton");
		var mute = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/MuteButton");
		var solo = row.GetNode<Button>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/Configs/SoloButton");
		var volume = row.GetNode<HSlider>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/VolumeContainer/Volume");
		var volumeLabel = row.GetNode<Label>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/VBoxContainer/VolumeContainer/Label");
		BindTrackSectionInfo(track, row.GetNode<Control>("TrackContainer/HBoxContainer/TrackControls/VBoxContainer/TrackSectionInfo"));

		nameLabel.Text = track.Name;

		nameLabel.MouseFilter = Control.MouseFilterEnum.Stop;
		nameLabel.TooltipText = TranslationServer.Translate("Double-click to rename this track.");
		nameEdit.Text = track.Name;
		nameLabel.Visible = true;
		nameEdit.Visible = false;
		icon.Texture = GD.Load<Texture2D>(TrackSettingsDialog.ResolveImagePath(track));
		mute.ToggleMode = true;
		mute.ButtonPressed = track.IsMuted;
		mute.TooltipText = TranslationServer.Translate("Mute this track");
		solo.ToggleMode = true;
		solo.ButtonPressed = track.IsSoloed;
		solo.TooltipText = TranslationServer.Translate("Solo this track");
		volume.MinValue = -80; volume.MaxValue = 20; volume.Step = 1; volume.Value = track.VolumeDb;
		volume.TooltipText = TranslationServer.Translate("Track volume in decibels.");
		volumeLabel.Text = $"{volume.Value:0.#} dB";

		void SelectTrack()
		{
			SetActiveEditorTrack(track.Id);
		}
		nameLabel.GuiInput += input =>
		{
			if (input is InputEventMouseButton { Pressed: true, DoubleClick: true })
			{
				SelectTrack(); nameLabel.Visible = false; nameEdit.Visible = true; nameEdit.Text = track.Name; nameEdit.GrabFocus(); nameEdit.SelectAll();
			}
		};
		var committingName = false;
		void CommitName()
		{
			if (committingName) return;
			committingName = true;
			var nextName = nameEdit.Text.Trim();
			var changed = nextName.Length > 0 && nextName != track.Name;
			if (changed)
			{
				track.Name = nextName;
				NotifyScoreEdited();
			}
			nameLabel.Text = track.Name;

			nameEdit.Visible = false; nameLabel.Visible = true;
			committingName = false;
			if (changed) RenderSection();
		}
		nameEdit.TextSubmitted += _ => CommitName();
		nameEdit.FocusExited += CommitName;
		var deleteTrackConfirmation = new ConfirmationDialog { Title = TranslationServer.Translate("Delete track?"), OkButtonText = TranslationServer.Translate("Delete") };
		row.AddChild(deleteTrackConfirmation);
		deleteTrackConfirmation.GetOkButton().ThemeTypeVariation = "AppDestructiveButton";
		deleteTrackConfirmation.GetCancelButton().ThemeTypeVariation = "AppActionButton";
		deleteTrack.Pressed += () =>
		{
			SelectTrack();
			deleteTrackConfirmation.DialogText = string.Format(TranslationServer.Translate("Delete track {0} and its contents from all sections?"), $"‘{track.Name}’");
			deleteTrackConfirmation.PopupCentered();
		};
		deleteTrackConfirmation.Confirmed += () => DeleteNonPercussionTrack(track);
		settings.Pressed += () => { SelectTrack(); ShowTrackSettings(track); };
		mute.Toggled += pressed => { SelectTrack(); track.IsMuted = pressed; NotifyScoreEdited(); };
		solo.Toggled += pressed => { SelectTrack(); track.IsSoloed = pressed; NotifyScoreEdited(); };
		volume.ValueChanged += value => { SelectTrack(); track.VolumeDb = value; volumeLabel.Text = $"{value:0.#} dB"; NotifyScoreEdited(); };


		var sectionHost = row.GetNode<HBoxContainer>("TrackContainer/HBoxContainer");
		var sectionTemplate = row.GetNode<Control>("TrackContainer/HBoxContainer/TabSectionContainer");
		sectionHost.RemoveChild(sectionTemplate);
		var measureScroll = CreateTrackTabPages(track.Id, sectionHost, out var fixedHeaders, out var pageSections);
		pageSections.AddChild(sectionTemplate);
		var sectionContainers = new List<Control> { sectionTemplate };
		for (var sectionIndex = 1; sectionIndex < visibleSections.Count; sectionIndex++)
		{
			var clone = (Control)sectionTemplate.Duplicate();
			pageSections.AddChild(clone);
			sectionContainers.Add(clone);
		}
		for (var sectionIndex = 0; sectionIndex < visibleSections.Count; sectionIndex++)
		{
			var sectionContainer = sectionContainers[sectionIndex];
			sectionContainer.Name = $"Section_{visibleSections[sectionIndex].Id:N}";
			RenderNonPercussionSection(track, visibleSections[sectionIndex], bars, measureScroll, sectionContainer);
			var headerPanel = sectionContainer.GetNode<Control>("VBoxContainer/Header");
			var header = headerPanel.GetNode<Control>("HeaderContainer");
			var tabControls = header.GetNode<Control>("TabControls");
			if (visibleSections.Count == 1)
				BindTrackTabHeader(track.Id, tabControls, measureScroll);
			else
			{
				if (sectionIndex == 0)
				{
					tabControls.GetParent().RemoveChild(tabControls);
					var fixedControls = measureScroll.GetNode<HBoxContainer>("../Headers/FixedControls");
					fixedControls.AddChild(tabControls);
					fixedControls.Visible = true;
					BindTrackTabHeader(track.Id, tabControls, measureScroll, multiSection: true);
				}
				headerPanel.Visible = false;
				var marker = sectionContainer.GetNode<Label>("VBoxContainer/SectionMarker");
				marker.Visible = true;
			}
			if (visibleSections.Count == 1)
			{
				headerPanel.GetParent().RemoveChild(headerPanel);
				fixedHeaders.AddChild(headerPanel);
			}
		}
	}

	private void QueueResponsiveTabWidthUpdate()
	{
		if (_tabWidthUpdateQueued || !IsInsideTree()) return;
		_tabWidthUpdateQueued = true;
		Callable.From(ApplyResponsiveTabWidths).CallDeferred();
	}

	private void ApplyResponsiveTabWidths()
	{
		_tabWidthUpdateQueued = false;
	}

	private float GetResponsiveTabMeasureWidth(ScrollContainer scroll, int visibleMeasureCount)
	{
		if (visibleMeasureCount <= 0) return TabMeasureMinimumWidth;
		var structuralOverhead = GetResponsiveTabStructuralOverhead();
		// Container layout rounds individual columns to whole pixels; leave a
		// pixel per measure plus a small edge allowance so rounding does not
		// introduce a horizontal scrollbar when the section otherwise fits.
		var availableWidth = Math.Max(0, scroll.Size.X - structuralOverhead - visibleMeasureCount - 2);
		return Math.Max(TabMeasureMinimumWidth, MathF.Floor(availableWidth / visibleMeasureCount));
	}

	/// <summary>
	/// Measure fixed row chrome from the current layout. The row minimum includes
	/// track controls, panel padding and container gaps; subtracting its measure
	/// columns leaves stable overhead, so repeated resizes do not drift.
	/// </summary>
	private float GetResponsiveTabStructuralOverhead()
	{
		var maxOverhead = 0f;
		foreach (var row in _tabTracks.GetChildren().OfType<FoldableContainer>())
		{
			if (!row.IsVisibleInTree() || row.Folded) continue;
			var host = row.GetNodeOrNull<HBoxContainer>("TrackContainer/HBoxContainer") ??
				row.GetNodeOrNull<HBoxContainer>("TrackContainer/VBoxContainer/TabSectionContainer/SubTracks/KitLanes");
			if (host is null) continue;

			var measureWidth = 0f;
			for (var index = 1; index < host.GetChildCount(); index++)
			{
				if (host.GetChild(index) is not Control sectionContainer) continue;
				var measures = sectionContainer.GetNodeOrNull<HBoxContainer>("VBoxContainer/Measures");
				if (measures is not null) measureWidth += measures.GetCombinedMinimumSize().X;
			}
			if (measureWidth <= 0) continue;
			maxOverhead = Math.Max(maxOverhead, row.GetCombinedMinimumSize().X - measureWidth);
		}
		return Math.Max(0, maxOverhead);
	}

	private void RenderNonPercussionSection(ScoreTrack track, ScoreSection section,
		List<TabBar> bars, ScrollContainer measureScroll, Control sectionContainer)
	{
		var addFirstMeasure = sectionContainer.GetNode<Button>("VBoxContainer/Header/HeaderContainer/AddFirstMeasureButton");
		var patternLength = sectionContainer.GetNode<SpinBox>("VBoxContainer/Header/HeaderContainer/PatternLength");
		var modeSelector = sectionContainer.GetNode<OptionButton>("VBoxContainer/Header/HeaderContainer/ModeSelector");
		var sectionMarker = sectionContainer.GetNode<Label>("VBoxContainer/SectionMarker");
		sectionMarker.Text = SectionArrangementSymbol(section);
		sectionMarker.TooltipText = sectionMarker.Text;
		var measureViews = sectionContainer.GetNode<HBoxContainer>("VBoxContainer/Measures");
		sectionContainer.CustomMinimumSize = new Vector2(0, sectionContainer.CustomMinimumSize.Y);
		var mode = _trackDisplayModes.GetValueOrDefault(track.Id, track.Kind is TrackKind.Guitar or TrackKind.Bass or TrackKind.Percussion ? 0 : 1);
		modeSelector.Select(mode);
		void SelectTrack() => SetActiveEditorTrack(track.Id);
		addFirstMeasure.Visible = section.Measures.Count == 0;
		addFirstMeasure.Pressed += () => { SetFocusedSection(section); InitializeMeasureSnapDefaults(_score.AddMeasure(section)); NotifyScoreEdited(); RenderSection(); };
		var isLead = section.LeadTrackId == track.Id;
		patternLength.Visible = false;
		var selectableSourceCount = isLead ? section.Measures.Count : _score.GetTrackPatternLength(section, track);
		var selectedSourceIndex = Math.Clamp(_sectionRepeatSourceIndices.GetValueOrDefault((section.Id, track.Id), 0), 0, Math.Max(0, selectableSourceCount - 1));
		_sectionRepeatSourceIndices[(section.Id, track.Id)] = selectedSourceIndex;
		if (section.Id == GetSelectedSection().Id) _selectedRepeatSourceIndices[track.Id] = selectedSourceIndex;
		modeSelector.ItemSelected += index =>
		{
			SetFocusedSection(section); SelectTrack(); _trackDisplayModes[track.Id] = (int)index; MarkEditorStateDirty(); RenderSection();
		};

		var sectionBars = bars.Where(b => ReferenceEquals(b.Section, section)).ToList();
		var measureWidth = GetResponsiveTabMeasureWidth(measureScroll, _visibleTabMeasureCount);
		var resolved = sectionBars.Select(bar => (Bar: bar, Occurrence: ResolveTrackOccurrence(section, track, bar))).ToList();
		var populatedCount = isLead ? sectionBars.Count : resolved
			.Select((item, index) => (item, index))
			.Where(entry => GetEffectiveTrackMeasure(section, track, entry.item.Bar.Measure, entry.item.Occurrence) is not null)
			.Select(entry => entry.index + 1).DefaultIfEmpty(0).Max();
		ClearChildren(measureViews);
		foreach (var item in resolved.Take(populatedCount))
		{
			var bar = item.Bar;
			var source = GetEffectiveTrackMeasure(section, track, bar.Measure, item.Occurrence)
				?? bar.Measure.GetTrackMeasure(track.Id);
			var column = _tabMeasureScene.Instantiate<Control>();
			column.Name = $"Measure{bar.Index + 1}";
			column.SizeFlagsHorizontal = Control.SizeFlags.Fill;
			column.CustomMinimumSize = new Vector2(measureWidth, 0);
			measureViews.AddChild(column);
			var header = column.GetNode<Control>("MeasureHeader");
			var versionControls = _measureVersionControlsScene.Instantiate<HBoxContainer>();
			header.GetNode<HBoxContainer>("HeaderContent").AddChild(versionControls);
			BindMeasureVersionControls(versionControls, section, track, bar.Measure, item.Occurrence);
			RegisterCursorTarget(bar.Measure.Id, header, drum: true);
			if (source is not null) BindNonPercussionMeasureHeader(header, section, track, bar.Measure, source, bar.Index);
			BindMeasureRepeatControls(header, section, track, item.Occurrence, isLead);
			var linkedRepeatLabel = header.GetNode<Label>("HeaderContent/LinkedRepeatLabel");
			if (!isLead)
			{
				header.GetNode<Button>("HeaderContent/AddMeasureButton").Visible = false;
				header.GetNode<Button>("HeaderContent/InsertBeforeButton").Visible = false;
				header.GetNode<Button>("HeaderContent/DuplicateBeforeButton").Visible = false;
				header.GetNode<Button>("HeaderContent/DuplicateAfterButton").Visible = false;
				header.GetNode<Button>("HeaderContent/DeleteMeasureButton").Visible = false;
			}
			if (item.Occurrence.IsRepeat && item.Occurrence.SourceMeasureIndex is { } sourceIndex)
			{
				linkedRepeatLabel.Text = $"↻ M{sourceIndex + 1}";
				linkedRepeatLabel.TooltipText = string.Format(TranslationServer.Translate("Linked repeat of source measure {0}. Edits update the source and its other repeats."), sourceIndex + 1);
				linkedRepeatLabel.Modulate = HasThemeColor("linked_repeat_color", "AppIndicators")
					? GetThemeColor("linked_repeat_color", "AppIndicators") : new Color(0.58f, 0.79f, 1f);
				linkedRepeatLabel.Visible = true;
			}
		}
		var displayMode = mode;
		var isTabTrack = track.Kind is TrackKind.Guitar or TrackKind.Bass or TrackKind.Percussion;
		if (displayMode == 1)
		{
			AddTrackPlaceholder(measureViews, resolved, populatedCount, measureWidth, "Standard notation view is not available yet. Score notes remain unchanged.", index => SelectRepeatSource(track, section, index));
		}
		else if (!isTabTrack)
		{
			AddTrackPlaceholder(measureViews, resolved, populatedCount, measureWidth, $"Tablature is unavailable for {track.Kind} tracks. Track data remains unchanged.", index => SelectRepeatSource(track, section, index));
		}
		else if (track.TabTuningMidiPitches.Count == 0)
		{
			AddTrackPlaceholder(measureViews, resolved, populatedCount, measureWidth, "No tuning assigned. Existing pitch-only notes remain unchanged.", index => SelectRepeatSource(track, section, index));
		}
		else
		{
			var occurrences = BuildTabOccurrences(track, bars);
			var pendingCursorViewScheduled = false;
			foreach (var item in resolved.Take(populatedCount))
			{
				var bar = item.Bar;
				var source = GetEffectiveTrackMeasure(section, track, bar.Measure, item.Occurrence);
				if (source is null) continue;
				var visible = new List<MeasureTabView.TabNoteViewData>();
				var mappings = new Dictionary<string, TabOccurrence>();
				foreach (var occurrence in occurrences)
				{
					var next = occurrences.Where(o => o.Note.StringNumber == occurrence.Note.StringNumber && o.Start > occurrence.Start)
						.Select(o => o.Start).DefaultIfEmpty(occurrence.End).Min();
					var effectiveEnd = next < occurrence.End ? next : occurrence.End;
					var barEnd = bar.Start + bar.Duration;
					if (occurrence.Start >= barEnd || effectiveEnd <= bar.Start) continue;
					var start = occurrence.Start < bar.Start ? bar.Start : occurrence.Start;
					var end = effectiveEnd < barEnd ? effectiveEnd : barEnd;
					// A sustain and a later linked repeat can both appear in this bar.
					// Keep their view identities distinct while both still edit the same source note.
					var id = $"{occurrence.Note.Id:N}@{occurrence.Start.Numerator}/{occurrence.Start.Denominator}";
					mappings[id] = occurrence;
					visible.Add(new MeasureTabView.TabNoteViewData(id, occurrence.Note.StringNumber, occurrence.Note.Fret,
						TabTime(start - bar.Start), TabTime(end - start), occurrence.Start < bar.Start, effectiveEnd > barEnd,
						VelocityLevel: occurrence.Note.Intensity, TupletActual: occurrence.Note.TupletActual,
						Expressions: occurrence.Note.Expressions.Select(effect => new MeasureTabView.ExpressionMarker(effect.Id, effect.Kind)).ToArray(),
						IncomingArticulation: occurrence.Note.IncomingArticulation));
				}
				var column = measureViews.GetNode<Control>($"Measure{bar.Index + 1}");
				var view = column.GetNode<MeasureTabView>("Content/MeasureTabView");
				view.SnapDenominator = GetMeasureSnapDenominator(track.Id, source);
				view.CustomMinimumSize = new Vector2(measureWidth, view.CustomMinimumSize.Y);
				RegisterCursorTarget(bar.Measure.Id, view);
				_cursorTabViews.Add((bar.Measure.Id, view));
				var signature = _score.ResolveTimeSignature(section, bar.Measure);
				view.Bind(new MeasureTabView.TabMeasureViewData(track.TabTuningMidiPitches, track.MaxFret,
					TabTime(bar.Duration), visible, signature.Numerator, signature.Denominator, IsRepeat: item.Occurrence.IsRepeat,
					RepeatSourceMeasureNumber: (item.Occurrence.SourceMeasureIndex ?? bar.Index) + 1, IsPercussion: track.Kind == TrackKind.Percussion,
					TrackColorRgba: track.DisplayColorRgba));
				if (!item.Occurrence.IsRepeat && item.Occurrence.SourceMeasureIndex is { } sourceIndex)
					RegisterPlaybackFollowTarget(track.Id, section.Measures[sourceIndex].Id, view, measureScroll);
				var content = column.GetNode<Control>("Content");
				content.CustomMinimumSize = new Vector2(0, view.CustomMinimumSize.Y);
				RegisterTabClipboardView(view, track, bar, source, mappings);
				view.PlacementClickHandler = position => HandleChordPlacementClick(view, track, position);
				view.VelocityGestureStarted += (selection, _) => BeginVelocityDrag(track, mappings, selection, view.GetViewport().GetMousePosition());
				view.VelocityGestureUpdated += (_, _, _, delta) => UpdateVelocityDrag(delta, view.GetViewport().GetMousePosition());
				view.VelocityGestureEnded += (_, _) => FinishVelocityDrag(cancel: false);
				view.VelocityGestureCanceled += _ => FinishVelocityDrag(cancel: true);
				view.NoteContextRequested += (clicked, position) =>
				{
					var selected = view.SelectedNotes;
					if (selected.Count == 0 || selected.Any(note => !mappings.ContainsKey(note.Id))) return;
					var targets = selected.Select(note => mappings[note.Id]).ToArray();
					ShowNoteVelocitySettings(track, targets, selected.Count, position);
				};
				BindEditorView(track.Id, bar.Measure.Id, view, measureScroll);
				view.MeasureSelected += () => { SetFocusedSection(section); ActivateTabView(track.Id, bar.Measure.Id, view); SelectRepeatSource(track, section, item.Occurrence.SourceMeasureIndex); };
				view.EditIntentHandler = intent =>
				{
					SetActiveEditorTrack(track.Id);
					var target = intent.NavigateHorizontal == 0 && intent.NavigateVertical == 0
						? null : GetTabNavigationTarget(track, bar, intent);
					if (!ApplyTabEdit(track, bar, source, mappings, intent)) return false;
					if (intent.NavigateHorizontal == 0 && intent.NavigateVertical == 0) return true;
					if (target is null)
					{
						if (intent.Action != "navigate") { NotifyScoreEdited(); RenderSection(); }
						return true;
					}
					_editingMeasureId = target.Section.Measures[target.MeasureIndex].Id;
					_editorTabSnapshot = null;
					_pendingTabCursor = target;
					MarkEditorStateDirty();
					EnsureSectionVisible(target.Section);
					if (intent.Action != "navigate") NotifyScoreEdited();
					RenderSection();
					return true;
				};
				if (_pendingTabCursor is { } pending && pending.TrackId == track.Id &&
					pending.MeasureIndex == bar.Index && ReferenceEquals(pending.Section, section) && ReferenceEquals(section, bar.Section))
				{
					pendingCursorViewScheduled = true;
					_pendingTabCursor = null;
					Callable.From(() =>
					{
						if (!GodotObject.IsInstanceValid(view) || !GodotObject.IsInstanceValid(measureScroll)) return;
						measureScroll.EnsureControlVisible(view);
						ActivateTabView(track.Id, bar.Measure.Id, view);
						view.BeginCursor(pending.StringNumber, TabTime(pending.Onset));
					}).CallDeferred();
				}
			}
			if (_pendingTabCursor is { } unmatched && unmatched.TrackId == track.Id && ReferenceEquals(unmatched.Section, section) && !pendingCursorViewScheduled)
				_pendingTabCursor = null;
		}
		if (!isLead && section.Measures.Count > 0 && populatedCount < sectionBars.Count)
		{
			foreach (var item in resolved.Skip(populatedCount))
			{
				var slot = new VBoxContainer { CustomMinimumSize = new Vector2(measureWidth, 0), SizeFlagsHorizontal = Control.SizeFlags.Fill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
				slot.AddChild(new Label { Text = $"M{item.Bar.Index + 1} versions", HorizontalAlignment = HorizontalAlignment.Center });
				var controls = _measureVersionControlsScene.Instantiate<HBoxContainer>();
				BindMeasureVersionControls(controls, section, track, item.Bar.Measure, item.Occurrence);
				slot.AddChild(controls);
				measureViews.AddChild(slot);
			}
			var add = new Button
			{
				Text = TranslationServer.Translate("Append source"),
			TooltipText = string.Format(TranslationServer.Translate("Append an empty independent measure. {0}+click to copy the previous measure with independent events."), "Ctrl"),
				CustomMinimumSize = new Vector2(measureWidth, Math.Max(180, measureWidth * 0.30f)),
				SizeFlagsHorizontal = Control.SizeFlags.Fill,
				SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				ThemeTypeVariation = "AppActionButton"
			};
			var copyOnClick = false;
			add.GuiInput += input =>
			{
				if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
					copyOnClick = click.CtrlPressed;
			};
			add.Pressed += () =>
			{
				try
				{
					var lastSourceIndex = resolved.Take(populatedCount).Select(entry => entry.Occurrence.SourceMeasureIndex).LastOrDefault(index => index.HasValue);
					SetFocusedSection(section);
					var appended = _score.AppendTrackMeasureSource(section, track, copyOnClick ? lastSourceIndex : null);
					appended.GetTrackMeasure(track.Id)!.SnapDenominator = GetTabSnapDenominator(track.Id);
					NotifyScoreEdited();
					RenderSection();
				}
				catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
				{
					SetTabStatus(error.Message);
				}
			};
			add.SetMeta("section_remainder_count", sectionBars.Count - populatedCount);
			measureViews.AddChild(add);
		}
		AlignNonPercussionMeasureWidths(measureViews, measureWidth);
	}

	private TrackMeasureOccurrence ResolveTrackOccurrence(ScoreSection section, ScoreTrack track, TabBar bar)
	{
		if (section.LeadTrackId == track.Id)
			return new TrackMeasureOccurrence(bar.Index, false, bar.Measure.GetTrackMeasure(track.Id));
		return _score.GetNonPercussionOccurrence(section, track, bar.Index);
	}

	private TrackMeasure? GetEffectiveTrackMeasure(ScoreSection section, ScoreTrack track, ScoreMeasure measure, TrackMeasureOccurrence occurrence)
	{
		var versionId = _selectedMeasureVersions.GetValueOrDefault((measure.Id, track.Id), Guid.Empty);
		if (versionId != Guid.Empty && measure.TrackMeasureVersions.Any(version => version.TrackId == track.Id && version.Id == versionId))
			return _score.GetTrackMeasureVersion(section, measure, track, versionId);
		if (versionId != Guid.Empty) _selectedMeasureVersions.Remove((measure.Id, track.Id));
		return occurrence.SourceTrackMeasure;
	}

	private void BindMeasureVersionControls(HBoxContainer controls, ScoreSection section, ScoreTrack track, ScoreMeasure measure, TrackMeasureOccurrence occurrence)
	{
		var selector = controls.GetNode<OptionButton>("Selector");
		var key = (measure.Id, track.Id);
		var selectedId = _selectedMeasureVersions.GetValueOrDefault(key, Guid.Empty);
		if (selectedId != Guid.Empty && !measure.TrackMeasureVersions.Any(version => version.TrackId == track.Id && version.Id == selectedId))
			selectedId = Guid.Empty;
		var versions = measure.TrackMeasureVersions.Where(item => item.TrackId == track.Id).ToArray();
		var hasNamedVersions = versions.Length > 0;
		selector.ThemeTypeVariation = hasNamedVersions ? "MeasureVersionOptionButton" : "DrumOptionButton";
		selector.TooltipText = TranslationServer.Translate("Choose the version for ordinary playback; arrangements use their transition and fallback rules.");
		selector.AddItem(occurrence.IsRepeat ? $"Original ↻ M{(occurrence.SourceMeasureIndex ?? 0) + 1}" : "Original");
		selector.SetItemMetadata(0, Guid.Empty.ToString("N"));
		foreach (var version in versions)
		{
			selector.AddItem(version.Name);
			selector.SetItemMetadata(selector.ItemCount - 1, version.Id.ToString("N"));
		}
		var chosen = Enumerable.Range(0, selector.ItemCount).FirstOrDefault(index => Guid.Parse(selector.GetItemMetadata(index).AsString()) == selectedId);
		selector.Select(chosen);
		selector.ItemSelected += index =>
		{
			var id = Guid.Parse(selector.GetItemMetadata((int)index).AsString());
			_selectedMeasureVersions[key] = id;
			SetFocusedSection(section); SetActiveEditorTrack(track.Id);
			try { _soundFontAudio.RefreshScore(_score, CapturePlaybackVersions()); }
			catch (Exception error) { GD.PushWarning($"Could not update playback after selecting a version: {error.Message}"); }
			RenderSection();
		};
		controls.GetNode<Button>("ManageButton").Pressed += () => ShowMeasureVersionManager(section, track, measure, occurrence);
	}

	private void ShowMeasureVersionManager(ScoreSection section, ScoreTrack track, ScoreMeasure measure, TrackMeasureOccurrence occurrence)
	{
		var dialog = new AcceptDialog { Title = $"Measure {section.Measures.IndexOf(measure) + 1} versions", DialogText = TranslationServer.Translate("Create measure versions and configure arrangement transitions and fallback order.") };
		dialog.Confirmed += dialog.QueueFree;
		dialog.CloseRequested += dialog.QueueFree;
		var manager = GD.Load<PackedScene>("res://Interface/Tab/Scenes/MeasureVersionManager.tscn").Instantiate<VBoxContainer>();
		dialog.AddChild(manager); AddChild(dialog);
		var name = manager.GetNode<LineEdit>("Name"); name.Text = $"Version {measure.TrackMeasureVersions.Count + 1}";
		var copy = manager.GetNode<Button>("Actions/Copy");
		copy.Disabled = occurrence.SourceTrackMeasure is null;
		var empty = manager.GetNode<Button>("Actions/Empty");
		var rename = manager.GetNode<Button>("Actions/Rename");
		var remove = manager.GetNode<Button>("Actions/Delete");
		var nextSection = manager.GetNode<OptionButton>("RuleRow/Section");
		var ruleVersion = manager.GetNode<OptionButton>("RuleRow/RuleVersion");
		var alternationChoice = manager.GetNode<OptionButton>("AlternationChoice");
		var alternationList = manager.GetNode<ItemList>("AlternationList");
		foreach (var candidate in _score.Sections) { nextSection.AddItem(candidate.Name); nextSection.SetItemMetadata(nextSection.ItemCount - 1, candidate.Id.ToString("N")); }
		ruleVersion.AddItem("Original"); ruleVersion.SetItemMetadata(0, Guid.Empty.ToString("N"));
		alternationChoice.AddItem("Original"); alternationChoice.SetItemMetadata(0, Guid.Empty.ToString("N"));
		foreach (var version in measure.TrackMeasureVersions.Where(item => item.TrackId == track.Id))
		{
			ruleVersion.AddItem(version.Name); ruleVersion.SetItemMetadata(ruleVersion.ItemCount - 1, version.Id.ToString("N"));
			alternationChoice.AddItem(version.Name); alternationChoice.SetItemMetadata(alternationChoice.ItemCount - 1, version.Id.ToString("N"));
		}
		var existingAlternation = measure.Alternations.FirstOrDefault(item => item.TrackId == track.Id)?.VersionIds ?? new List<Guid>();
		var orderedIds = existingAlternation.ToList();
		void RefreshAlternation() { alternationList.Clear(); foreach (var id in orderedIds) alternationList.AddItem(id == Guid.Empty ? "Original" : measure.TrackMeasureVersions.FirstOrDefault(item => item.Id == id)?.Name ?? "[Missing version]"); }
		RefreshAlternation();
		foreach (var rule in measure.TransitionRules.Where(item => item.TrackId == track.Id))
		{
			var targetName = _score.Sections.FirstOrDefault(candidate => candidate.Id == rule.SectionId)?.Name;
			var row = new HBoxContainer();
			var versionName = rule.VersionId == Guid.Empty ? "Original" : measure.TrackMeasureVersions.FirstOrDefault(item => item.Id == rule.VersionId)?.Name ?? "[Missing version]";
			var unresolved = targetName is null;
			row.AddChild(new Label { Text = unresolved ? $"Unresolved section {rule.SectionId:N} → {versionName}" : $"{targetName} → {versionName}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
			var removeRule = new Button { Text = "Remove" }; row.AddChild(removeRule); manager.GetNode<VBoxContainer>("TransitionRules").AddChild(row);
			removeRule.Pressed += () => { _score.RemoveTrackMeasureTransitionRule(section, measure, track, rule.SectionId); NotifyScoreEdited(); row.QueueFree(); };
		}
		void Create(TrackMeasure content)
		{
			try
			{
				var version = _score.AddTrackMeasureVersion(section, measure, track, name.Text, content);
				_selectedMeasureVersions[(measure.Id, track.Id)] = version.Id;
				NotifyScoreEdited(); RenderSection();
			}
			catch (Exception error) when (error is ArgumentException or InvalidOperationException) { SetTabStatus(error.Message); }
			dialog.QueueFree();
		}
		copy.Pressed += () => { if (occurrence.SourceTrackMeasure is { } original) Create(original); };
		empty.Pressed += () => { var content = new TrackMeasure(track.Id) { UsesTabEvents = true }; Create(content); };
		rename.Pressed += () =>
		{
			var id = _selectedMeasureVersions.GetValueOrDefault((measure.Id, track.Id), Guid.Empty);
			if (id == Guid.Empty) { SetTabStatus("Select a named version first."); return; }
			try { _score.RenameTrackMeasureVersion(section, measure, track, id, name.Text); NotifyScoreEdited(); RenderSection(); }
			catch (Exception error) when (error is ArgumentException or InvalidOperationException) { SetTabStatus(error.Message); }
			dialog.QueueFree();
		};
		remove.Pressed += () =>
		{
			var id = _selectedMeasureVersions.GetValueOrDefault((measure.Id, track.Id), Guid.Empty);
			if (id == Guid.Empty) { SetTabStatus("Select a named version first."); return; }
			_score.RemoveTrackMeasureVersion(section, measure, track, id);
			_selectedMeasureVersions.Remove((measure.Id, track.Id)); NotifyScoreEdited(); RenderSection(); dialog.QueueFree();
		};
		manager.GetNode<Button>("SetRule").Pressed += () =>
		{
			try
			{
				var targetId = Guid.Parse(nextSection.GetItemMetadata(nextSection.Selected).AsString());
				var versionId = Guid.Parse(ruleVersion.GetItemMetadata(ruleVersion.Selected).AsString());
				_score.SetTrackMeasureTransitionRule(section, measure, track, targetId, versionId);
				NotifyScoreEdited(); SetTabStatus("Transition rule saved."); dialog.QueueFree();
			}
			catch (Exception error) when (error is ArgumentException or InvalidOperationException) { SetTabStatus(error.Message); }
		};
		manager.GetNode<Button>("AlternationActions/AddChoice").Pressed += () =>
		{
			var id = Guid.Parse(alternationChoice.GetItemMetadata(alternationChoice.Selected).AsString());
			if (!orderedIds.Contains(id)) orderedIds.Add(id);
			RefreshAlternation();
		};
		manager.GetNode<Button>("AlternationActions/RemoveChoice").Pressed += () => { var index = alternationList.GetSelectedItems().FirstOrDefault(-1); if (index >= 0) { orderedIds.RemoveAt(index); RefreshAlternation(); } };
		manager.GetNode<Button>("AlternationActions/MoveUp").Pressed += () => { var index = alternationList.GetSelectedItems().FirstOrDefault(-1); if (index > 0) { (orderedIds[index - 1], orderedIds[index]) = (orderedIds[index], orderedIds[index - 1]); RefreshAlternation(); alternationList.Select(index - 1); } };
		manager.GetNode<Button>("AlternationActions/MoveDown").Pressed += () => { var index = alternationList.GetSelectedItems().FirstOrDefault(-1); if (index >= 0 && index < orderedIds.Count - 1) { (orderedIds[index + 1], orderedIds[index]) = (orderedIds[index], orderedIds[index + 1]); RefreshAlternation(); alternationList.Select(index + 1); } };
		manager.GetNode<Button>("SaveAlternation").Pressed += () => { try { _score.SetTrackMeasureAlternation(section, measure, track, orderedIds); NotifyScoreEdited(); SetTabStatus("Alternation order saved."); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { SetTabStatus(error.Message); } };
		dialog.PopupCentered();
	}

	private void SelectRepeatSource(ScoreTrack track, ScoreSection section, int? sourceMeasureIndex)
	{
		if (sourceMeasureIndex is not { } sourceIndex || sourceIndex < 0 || sourceIndex >= section.Measures.Count) return;
		SetFocusedSection(section);
		SetActiveEditorTrack(track.Id);
		_sectionRepeatSourceIndices[(section.Id, track.Id)] = sourceIndex;
		if (GetSelectedSection().Id == section.Id) _selectedRepeatSourceIndices[track.Id] = sourceIndex;
		MarkEditorStateDirty();
	}

	private void BindMeasureRepeatControls(Control header, ScoreSection section, ScoreTrack track,
		TrackMeasureOccurrence occurrence, bool isLead)
	{
		var label = header.GetNode<Label>("HeaderContent/RepeatLabel");
		var repeatCount = header.GetNode<SpinBox>("HeaderContent/RepeatCount");
		var sourceIndex = occurrence.SourceMeasureIndex;
		var supportsLinkedRepeats = !(track.Kind == TrackKind.Percussion && !track.HasConfiguredPercussionKit);
		var enabled = !isLead && supportsLinkedRepeats && section.Measures.Count > 0 && sourceIndex is { } index &&
			index >= 0 && index < _score.GetTrackPatternLength(section, track) && section.Measures[index].GetTrackMeasure(track.Id) is not null;
		label.Visible = enabled;
		repeatCount.Visible = enabled;
		if (!enabled || sourceIndex is not { } authoredIndex)
			return;

		var sourcePart = section.Measures[authoredIndex].GetTrackMeasure(track.Id)!;
		label.Text = "Repeats";
		label.TooltipText = occurrence.IsRepeat
			? $"This linked occurrence uses source measure {authoredIndex + 1}; changing the count edits that source."
			: $"Additional linked occurrences of source measure {authoredIndex + 1}.";
		repeatCount.MinValue = 0;
		repeatCount.MaxValue = Math.Max(0, section.Measures.Count);
		repeatCount.Step = 1;
		repeatCount.Editable = true;
		repeatCount.TooltipText = label.TooltipText;
		repeatCount.SetValueNoSignal(sourcePart.RepeatCount);
		repeatCount.ValueChanged += value =>
		{
			try
			{
				SelectRepeatSource(track, section, authoredIndex);
				_score.SetTrackMeasureRepeatCount(section, track, authoredIndex, (int)value);
				NotifyScoreEdited();
				RenderSection();
			}
			catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
			{
				SetTabStatus(error.Message);
				repeatCount.SetValueNoSignal(section.Measures[authoredIndex].GetTrackMeasure(track.Id)?.RepeatCount ?? 0);
			}
		};
	}

	private void AddTrackPlaceholder(HBoxContainer measures, IReadOnlyList<(TabBar Bar, TrackMeasureOccurrence Occurrence)> resolved, int visibleCount, float measureWidth, string message, Action<int?> selectSource)
	{
		if (resolved.Count == 0)
		{
			measures.AddChild(new Label { Text = message, VerticalAlignment = VerticalAlignment.Center, CustomMinimumSize = new Vector2(240, 56) });
			return;
		}
		foreach (var item in resolved.Take(visibleCount))
		{
			var repeatLabel = item.Occurrence.IsRepeat && item.Occurrence.SourceMeasureIndex is { } sourceIndex
				? $"↻ REPEAT · M{sourceIndex + 1}\n" : "";
			var column = measures.GetNode<Control>($"Measure{item.Bar.Index + 1}");
			var editor = column.GetNode<Control>("Content/MeasureTabView");
			var label = column.GetNode<Label>("Content/PlaceholderLabel");
			editor.Visible = false;
			label.Visible = true;
			label.Text = repeatLabel + message;
			label.VerticalAlignment = VerticalAlignment.Center;
			label.ClipText = true;
			label.MouseFilter = Control.MouseFilterEnum.Stop;
			label.TooltipText = item.Occurrence.IsRepeat && item.Occurrence.SourceMeasureIndex is { } linkedSource
				? $"Linked repeat of source measure {linkedSource + 1}. {message}" : message;
			label.GuiInput += input =>
			{
				if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
					selectSource(item.Occurrence.SourceMeasureIndex);
			};
			RegisterCursorTarget(item.Bar.Measure.Id, label, drum: true);
		}
	}

	private void BindNonPercussionMeasureHeader(Control header, ScoreSection section, ScoreTrack track, ScoreMeasure measure, TrackMeasure content, int measureIndex)
	{
		var selector = header.GetNode<OptionButton>("HeaderContent/TimeSignatureSelector");
		var snapSelector = header.GetNode<OptionButton>("HeaderContent/MeasureSnapSelector");
		header.GetNode<HBoxContainer>("HeaderContent").MoveChild(snapSelector, 1);
		for (var denominator = 4; denominator <= 64; denominator *= 2)
			snapSelector.AddItem($"1/{denominator}", denominator);
		var snap = GetMeasureSnapDenominator(track.Id, content);
		for (var index = 0; index < snapSelector.ItemCount; index++)
			if (snapSelector.GetItemId(index) == snap) snapSelector.Select(index);
		snapSelector.TooltipText = Tr("Editing snap for this measure and track.");
		snapSelector.ItemSelected += index =>
		{
			var selectedSnap = snapSelector.GetItemId((int)index);
			if (content.SnapDenominator == selectedSnap) return;
			content.SnapDenominator = selectedSnap;
			NotifyScoreEdited(); RenderSection();
		};
		var validation = header.GetNode<RichTextLabel>("HeaderContent/ValidationInfo");
		var addMeasure = header.GetNode<Button>("HeaderContent/AddMeasureButton");
		var deleteMeasure = header.GetNode<Button>("HeaderContent/DeleteMeasureButton");
		var confirmation = header.GetNode<ConfirmationDialog>("DeleteMeasureConfirmation");
		BindMeasurePlacementActions(header, section, measureIndex);
		selector.Select(FindSignatureChoice(_score.ResolveTimeSignature(section, measure)));
		selector.ItemSelected += index =>
		{
			var signature = TimeSignatureChoices[(int)index];
			if (_score.ResolveTimeSignature(section, measure) == signature) return;
			SetFocusedSection(section);
			measure.TimeSignatureOverride = signature; NotifyScoreEdited(); RenderSection();
		};
		addMeasure.Pressed += () => { SetFocusedSection(section); InitializeMeasureSnapDefaults(_score.InsertMeasureAfter(section, measureIndex)); NotifyScoreEdited(); RenderSection(); };
		confirmation.GetOkButton().ThemeTypeVariation = "BeatRemoveButton";
		confirmation.GetCancelButton().ThemeTypeVariation = "DrumActionButton";
		confirmation.DialogText = string.Format(Tr("Delete measure {0} from section {1}?\nThis removes its content from all tracks."), measureIndex + 1, section.Name);
		deleteMeasure.Pressed += () => confirmation.PopupCentered();
		confirmation.Confirmed += () => { SetFocusedSection(section); _score.RemoveMeasure(section, measure); NotifyScoreEdited(); RenderSection(); };
		RefreshMeasureValidation(section, measure, measureIndex, validation);
	}

	private static void AlignNonPercussionMeasureWidths(HBoxContainer measures, float preferredWidth)
	{
		for (var index = 0; index < measures.GetChildCount(); index++)
		{
			if (measures.GetChild(index) is not Control measure) continue;
			var remainderCount = measure.HasMeta("section_remainder_count")
				? measure.GetMeta("section_remainder_count").AsInt32() : 0;
			var measureHeight = measure.CustomMinimumSize.Y;
			var width = preferredWidth * Math.Max(1, remainderCount);
			if (remainderCount == 0)
			{
				measure.CustomMinimumSize = new Vector2(0, measureHeight);
				var view = measure.GetNodeOrNull<Control>("Content/MeasureTabView");
				if (view is not null) view.CustomMinimumSize = new Vector2(0, view.CustomMinimumSize.Y);
				width = Math.Max(preferredWidth, measure.GetCombinedMinimumSize().X);
			}
			measure.CustomMinimumSize = new Vector2(width, measureHeight);
			var tabView = measure.GetNodeOrNull<Control>("Content/MeasureTabView");
			if (tabView is not null) tabView.CustomMinimumSize = new Vector2(width, tabView.CustomMinimumSize.Y);
		}
	}
	private void DeleteNonPercussionTrack(ScoreTrack track)
	{
		// The score may have changed while the confirmation dialog was open.
		if (!_score.Tracks.Contains(track)) return;
		_score.RemoveTrack(track);
		if (_selectedNonPercussionTrackId == track.Id) SetActiveEditorTrack(null);
		else if (_activeFloatingBoardTrackId == track.Id) ActivateFloatingBoards(null);
		if (_editorTabTrackId == track.Id)
		{
			_activeEditorView = null; _tabRestorePending = false; _editorTabSnapshot = null;
			_editorTabTrackId = null; _editorTabMeasureId = null;
		}
		if (_pendingTabCursor?.TrackId == track.Id) _pendingTabCursor = null;
		_trackFoldedStates.Remove(track.Id);
		foreach (var key in _sectionRepeatSourceIndices.Keys.Where(key => key.TrackId == track.Id).ToArray()) _sectionRepeatSourceIndices.Remove(key);
		_trackDisplayModes.Remove(track.Id);
		_selectedRepeatSourceIndices.Remove(track.Id);
		_floatingBoardStates.Remove(track.Id);
		_floatingBoardButtons.Remove(track.Id);
		NotifyScoreEdited();
		RenderSection();
	}

	private void ShowTrackSettings(ScoreTrack track)
	{
		var dialog = GD.Load<PackedScene>("res://Interface/Tracks/TrackSettingsDialog.tscn").Instantiate<TrackSettingsDialog>();
		AddChild(dialog);
		dialog.Configure(track);
		dialog.EvaluateRetuning = tuning =>
		{
			if (!_score.Tracks.Contains(track)) return (false, false, "This track is no longer in the score.", "This track is no longer in the score.");
			var fingering = TabRetuningPlan.TryCreate(_score, track, tuning, TabRetuningMode.KeepFingering, out _, out var fingeringError);
			var pitch = TabRetuningPlan.TryCreate(_score, track, tuning, TabRetuningMode.KeepPitch, out _, out var pitchError);
			return (fingering, pitch, fingeringError, pitchError);
		};
		dialog.RefreshRetuningOptions();
		dialog.GetOkButton().ThemeTypeVariation = "AppActionButton";
		dialog.GetCancelButton().ThemeTypeVariation = "AppActionButton";
		dialog.Confirmed += () =>
		{
			if (!_score.Tracks.Contains(track)) { dialog.QueueFree(); return; }
			var program = dialog.SelectedProgram;
			var color = dialog.SelectedColorRgba;
			var image = dialog.SelectedImageId;
			var name = dialog.SelectedName;
			var tuningChanged = track.Kind is TrackKind.Guitar or TrackKind.Bass && dialog.TuningChanged;
			if (tuningChanged)
			{
				if (!TabRetuningPlan.TryCreate(_score, track, dialog.SelectedTuning, dialog.SelectedRetuningMode, out var plan, out var error))
				{
					dialog.RefreshRetuningOptions();
					dialog.PopupCentered();
					var message = new AcceptDialog { Title = "Cannot change tuning", DialogText = error };
					dialog.AddChild(message);
					message.Confirmed += message.QueueFree;
					message.CloseRequested += message.QueueFree;
					message.PopupCentered();
					return;
				}
				plan!.Apply();
				if (_editorTabTrackId == track.Id)
				{
					_activeEditorView = null; _tabRestorePending = false; _editorTabSnapshot = null;
					_editorTabTrackId = null; _editorTabMeasureId = null;
				}
				if (_pendingTabCursor?.TrackId == track.Id) _pendingTabCursor = null;
			}
			var imageChanged = image != TrackSettingsDialog.ResolveImageId(track);
			if (tuningChanged || name != track.Name || program != track.DefaultMidiProgram || color != track.DisplayColorRgba || imageChanged)
			{
				track.Name = name;
				track.DefaultMidiProgram = program;
				track.DisplayColorRgba = color;
				if (imageChanged) track.InstrumentImageId = image;
				NotifyScoreEdited();
				RenderSection();
			}
			dialog.QueueFree();
		};
		dialog.Canceled += dialog.QueueFree;
		dialog.CloseRequested += dialog.QueueFree;
		dialog.PopupCentered();
	}

	private void ShowNewTrackSettings()
	{
		var dialog = GD.Load<PackedScene>("res://Interface/Tracks/TrackSettingsDialog.tscn").Instantiate<TrackSettingsDialog>();
		AddChild(dialog);
		dialog.ConfigureNew();
		dialog.GetOkButton().ThemeTypeVariation = "AppActionButton";
		dialog.GetCancelButton().ThemeTypeVariation = "AppActionButton";
		dialog.Confirmed += () =>
		{
			var track = new ScoreTrack(dialog.SelectedName, dialog.SelectedKind, dialog.SelectedProgram)
			{
				DisplayColorRgba = dialog.SelectedColorRgba,
				InstrumentImageId = dialog.SelectedImageId,
				TabSnapDenominator = _tabSnapDenominator
			};
			if (track.Kind is TrackKind.Guitar or TrackKind.Bass) track.ConfigureTab(dialog.SelectedTuning);
			if (track.Kind == TrackKind.Percussion) ConfigureDefaultPercussionKit(track);
			_score.AddTrack(track);
			_tabSnapDenominators[track.Id] = track.TabSnapDenominator!.Value;
			foreach (var measure in _score.Sections.SelectMany(section => section.Measures))
				if (measure.GetTrackMeasure(track.Id) is { } newPart) newPart.SnapDenominator = GetTabSnapDenominator(track.Id);
			if (GetSelectedSection().Measures.Count == 0) InitializeMeasureSnapDefaults(_score.AddMeasure(GetSelectedSection()));
			SetActiveEditorTrack(track.Id);
			_trackDisplayModes[track.Id] = track.Kind is TrackKind.Guitar or TrackKind.Bass or TrackKind.Percussion ? 0 : 1;
			NotifyScoreEdited();
			RenderSection();
			dialog.QueueFree();
		};
		dialog.Canceled += dialog.QueueFree;
		dialog.CloseRequested += dialog.QueueFree;
		dialog.PopupCentered();
	}

	private bool ApplyTabEdit(ScoreTrack track, TabBar bar, TrackMeasure source,
		Dictionary<string, TabOccurrence> mappings, MeasureTabView.TabEditIntent intent)
	{
		try
		{
			if (intent.Action == "navigate") return true;
			if (intent.Action is "move" or "delete" or "shift_fret") return ApplyTabSelectionEdit(track, bar, mappings, intent);
			var onset = new ScoreTime(intent.Onset.Numerator, intent.Onset.Denominator);
			var duration = new ScoreTime(intent.Duration.Numerator, intent.Duration.Denominator);
			if (intent.Action == "resize" && intent.Notes is { Count: > 1 } selected)
			{
				var anchor = selected.FirstOrDefault(note => note.Id == intent.EventId)
					?? throw new InvalidOperationException("The drag anchor is no longer selected.");
				var delta = duration - new ScoreTime(anchor.Duration.Numerator, anchor.Duration.Denominator);
				var targets = selected.Select(note => mappings.TryGetValue(note.Id, out var occurrence)
					? (occurrence.Source, occurrence.Note)
					: throw new InvalidOperationException("The selection changed; select the notes again.")).ToArray();
				if (TabEventEditing.ResizeSelection(targets, delta)) { NotifyScoreEdited(); RenderSection(); }
				return true;
			}
			if (intent.Action == "insert")
			{
				if (onset >= bar.Duration) throw new InvalidOperationException("The note must start inside the measure.");
				if (!source.UsesTabEvents && source.Voices.SelectMany(v => v.Beats).Any(b => b.Notes.Count > 0))
					throw new InvalidOperationException("Existing pitch-only notes are preserved; assigning them to strings is not yet available here.");
				TabEventEditing.Add(source, track, intent.StringNumber, intent.Fret, onset, duration);
			}
			else if (mappings.TryGetValue(intent.EventId, out var occurrence))
			{
				var note = occurrence.Note;
				if (intent.Action == "resize")
				{
					var absoluteEnd = bar.Start + onset + duration;
					TabEventEditing.Resize(occurrence.Source, note, absoluteEnd - occurrence.Start);
				}
				else if (intent.Action == "fret")
				{
					TabEventEditing.ValidateAssignment(track, note.StringNumber, intent.Fret);
					note.Fret = intent.Fret; note.MidiPitch = track.TabTuningMidiPitches[note.StringNumber - 1] + intent.Fret;
				}
				else return false;
			}
			else return false;
			if (intent.NavigateHorizontal != 0 || intent.NavigateVertical != 0) return true;
			NotifyScoreEdited(); RenderSection();
			return true;
		}
		catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
		{
			SetTabStatus(error.Message);
			return false;
		}
	}

	private PendingTabCursor? GetTabNavigationTarget(ScoreTrack track, TabBar bar, MeasureTabView.TabEditIntent intent)
	{
		if (track.TabTuningMidiPitches.Count == 0 || bar.Duration <= ScoreTime.Zero) return null;
		var stringNumber = Math.Clamp(intent.StringNumber + intent.NavigateVertical, 1, track.TabTuningMidiPitches.Count);
		var onset = new ScoreTime(intent.Onset.Numerator, intent.Onset.Denominator);
		var resolved = ResolveTrackOccurrence(bar.Section, track, bar);
		var editingPart = GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, resolved);
		var snapDenominator = editingPart is null ? GetTabSnapDenominator(track.Id) : GetMeasureSnapDenominator(track.Id, editingPart);
		var unit = new ScoreTime(1, snapDenominator);
		var absolute = bar.Start + onset + new ScoreTime(intent.NavigateHorizontal, snapDenominator);
		var scoreBars = BuildTabBars().Where(candidate => ResolveTrackOccurrence(candidate.Section, track, candidate).SourceTrackMeasure is not null)
			.OrderBy(candidate => candidate.Start).ToList();
		foreach (var candidate in scoreBars)
		{
			var end = candidate.Start + candidate.Duration;
			if (absolute >= candidate.Start && absolute < end)
				return new PendingTabCursor(track.Id, candidate.Section, candidate.Index, stringNumber, absolute - candidate.Start);
		}
		var lastOnset = bar.Duration - unit;
		if (lastOnset < ScoreTime.Zero) lastOnset = ScoreTime.Zero;
		if (onset < ScoreTime.Zero) onset = ScoreTime.Zero;
		if (onset > lastOnset) onset = lastOnset;
		return new PendingTabCursor(track.Id, bar.Section, bar.Index, stringNumber, onset);
	}

	private List<TabBar> BuildTabBars()
	{
		var result = new List<TabBar>();
		foreach (var section in _score.Sections)
		for (var i = 0; i < section.Measures.Count; i++)
		{
			var measure = section.Measures[i];
			var start = _score.ToAbsolutePosition(section, measure, ScoreTime.Zero);
			var duration = _score.ResolveTimeSignature(section, measure).BarDuration;
			if (measure.IsPickup || measure.IsFreeTime)
			{
				duration = measure.TrackMeasures.Where(m => !m.UsesTabEvents).SelectMany(m => m.Voices)
					.Select(v => v.Beats.Aggregate(ScoreTime.Zero, (sum, beat) => sum + beat.Duration))
					.Concat(measure.TrackMeasures.Where(m => m.UsesTabEvents).SelectMany(m => m.TabEvents).Select(n => n.EndPosition))
					.DefaultIfEmpty(ScoreTime.Zero).Max();
			}
			result.Add(new TabBar(section, measure, i, start, duration));
		}
		return result;
	}

	private List<TabOccurrence> BuildTabOccurrences(ScoreTrack track, List<TabBar> bars)
	{
		var occurrences = new List<TabOccurrence>();
		foreach (var bar in bars)
		{
			var resolved = ResolveTrackOccurrence(bar.Section, track, bar);
			var source = GetEffectiveTrackMeasure(bar.Section, track, bar.Measure, resolved);
			if (source is null || !source.UsesTabEvents) continue;
			foreach (var note in source.TabEvents.Where(note => note.Onset < bar.Duration || note.AllowsBarOverflow))
				occurrences.Add(new TabOccurrence(note, source, bar.Start + note.Onset, bar.Start + note.EndPosition, resolved.IsRepeat, bar.Index));
		}
		return occurrences;
	}

	private static MeasureTabView.ExactTime TabTime(ScoreTime value) => new(value.Numerator, value.Denominator);

	private static string FormatShortcut(InputEventKey binding)
	{
		var modifiers = new List<string>();
		if (binding.CtrlPressed) modifiers.Add("Ctrl");
		if (binding.AltPressed) modifiers.Add("Alt");
		if (binding.ShiftPressed) modifiers.Add("Shift");
		if (binding.MetaPressed) modifiers.Add("Meta");
		modifiers.Add(binding.Keycode == Key.None ? binding.PhysicalKeycode.ToString() : binding.Keycode.ToString());
		return string.Join("+", modifiers);
	}

	/// <summary>Refreshes the tablature help with the current configurable copy and paste bindings.</summary>
	public void RefreshTabStatusTooltip()
	{
		if (_tabStatus is null || !GodotObject.IsInstanceValid(_tabStatus)) return;
		var copy = FormatShortcut(AppPreferences.GetBinding("copy_notes"));
		var paste = FormatShortcut(AppPreferences.GetBinding("paste_notes"));
		_tabStatus.TooltipText = string.Format(TranslationServer.Translate(
			"Drag empty space to select. Shift-drag adds to selection.\n{0} copies notes; point at a destination and press {1} to paste.\nCtrl-click adds fret 0. Double-click edits a fret.\nCtrl+arrow keys edit or move notes."),
			copy, paste);
	}

	public void SetTabStatus(string sourceMessage, params object[] arguments)
	{
		_tabStatusSource = sourceMessage;
		_tabStatusArguments = arguments;
		if (_tabStatus is not null && GodotObject.IsInstanceValid(_tabStatus))
			_tabStatus.Text = arguments.Length == 0 ? TranslationServer.Translate(sourceMessage) : string.Format(TranslationServer.Translate(sourceMessage), arguments.Select(value => value is Enum ? (object)TranslationServer.Translate(value.ToString() ?? "") : value).ToArray());
	}

	public void RefreshTabStatusLocalization()
	{
		RefreshTabStatusTooltip();
		if (_tabStatusSource.Length > 0) SetTabStatus(_tabStatusSource, _tabStatusArguments);
	}
}

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using OpenTab.Music;

public partial class DrumScorePresenter
{
	private readonly HashSet<Guid> _visibleSectionIds = new();
	private HBoxContainer _sectionNavigationButtons = null!;
	private Button _sectionNavigationAddButton = null!;
	private PopupMenu _sectionNavigationPopup = null!;
	private ConfirmationDialog _sectionNavigationDeleteConfirmation = null!;
	private Guid? _sectionPendingNavigationDeletionId;

	/// <summary>Returns the visible sections in score order, restoring the active section if necessary.</summary>
	private IReadOnlyList<ScoreSection> GetVisibleSections()
	{
		if (_score.Sections.Count == 0)
			return Array.Empty<ScoreSection>();

		_selectedSectionIndex = Math.Clamp(_selectedSectionIndex, 0, _score.Sections.Count - 1);
		var activeSection = _score.Sections[_selectedSectionIndex];
		var scoreOrder = _score.Sections.Select(section => section.Id).ToArray();
		var existingIds = scoreOrder.ToHashSet();
		_visibleSectionIds.RemoveWhere(id => !existingIds.Contains(id));
		if (_visibleSectionIds.Count == 0)
			_visibleSectionIds.Add(activeSection.Id);
		var visibleIdsInScoreOrder = SectionSelectionLogic.OrderVisible(scoreOrder, _visibleSectionIds);
		var visibleIds = visibleIdsInScoreOrder.ToHashSet();
		return _score.Sections.Where(section => visibleIds.Contains(section.Id)).ToArray();
	}

	/// <summary>Adds a section to the view and focuses it without rebuilding the editors.</summary>
	private void EnsureSectionVisible(ScoreSection section)
	{
		if (section is null || !_score.Sections.Any(candidate => candidate.Id == section.Id))
			return;

		_visibleSectionIds.Add(section.Id);
		_selectedSectionIndex = _score.Sections.ToList().FindIndex(candidate => candidate.Id == section.Id);
		RefreshTrackSectionInfo();
	}

	/// <summary>Changes the active section only when it is already visible.</summary>
	private void SetFocusedSection(ScoreSection section)
	{
		if (section is null || !_visibleSectionIds.Contains(section.Id))
			return;

		var index = _score.Sections.ToList().FindIndex(candidate => candidate.Id == section.Id);
		if (index < 0 || index == _selectedSectionIndex) return;
		_selectedSectionIndex = index;
		RefreshTrackSectionInfo();
	}

	/// <summary>Resets the view to the current active section.</summary>
	private void ResetVisibleSections()
	{
		_visibleSectionIds.Clear();
		if (_score.Sections.Count == 0)
			return;

		_selectedSectionIndex = Math.Clamp(_selectedSectionIndex, 0, _score.Sections.Count - 1);
		_visibleSectionIds.Add(_score.Sections[_selectedSectionIndex].Id);
	}

	private void BindSectionNavigation()
	{
		var navigation = GetNode<Control>("%Sections");
		_sectionNavigationButtons = navigation.GetNode<HBoxContainer>("VBoxContainer/HBoxContainer2/ScrollContainer/SectionButtons");
		_sectionNavigationAddButton = navigation.GetNode<Button>("VBoxContainer/HBoxContainer2/AddButton");
		_sectionNavigationAddButton.Pressed += AddSection;
		_sectionNavigationAddButton.Pressed += FrameVisibleSections;

		_sectionNavigationPopup = new PopupMenu { Name = "SectionNavigationPopup" };
		_sectionNavigationPopup.AddItem("Delete section", 1);
		_sectionNavigationPopup.IdPressed += id =>
		{
			if (id == 1)
				OpenSectionDeleteConfirmation();
		};
		AddChild(_sectionNavigationPopup);

		_sectionNavigationDeleteConfirmation = new ConfirmationDialog
		{
			Name = "SectionNavigationDeleteConfirmation",
			Title = "Delete section?",
			OkButtonText = "Delete",
			DialogText = "Delete this section and its contents from all tracks?"
		};
		AddChild(_sectionNavigationDeleteConfirmation);
		_sectionNavigationDeleteConfirmation.GetOkButton().ThemeTypeVariation = "AppDestructiveButton";
		_sectionNavigationDeleteConfirmation.GetCancelButton().ThemeTypeVariation = "AppActionButton";
		_sectionNavigationDeleteConfirmation.Confirmed += DeletePendingNavigationSection;
	}

	private void RenderSectionNavigation()
	{
		if (_sectionNavigationButtons is null || !GodotObject.IsInstanceValid(_sectionNavigationButtons))
			return;

		ClearChildren(_sectionNavigationButtons);

		GetVisibleSections();
		var duplicateNames = _score.Sections
			.Where(section => !string.IsNullOrWhiteSpace(section.Name))
			.GroupBy(section => section.Name.Trim(), StringComparer.OrdinalIgnoreCase)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var section in _score.Sections)
		{
			var sectionId = section.Id;
			var scoreIndex = _score.Sections.ToList().FindIndex(candidate => candidate.Id == sectionId);
			var sectionName = section.Name?.Trim() ?? string.Empty;
			var displayName = string.IsNullOrEmpty(sectionName)
				? GetSectionFallbackLabel(scoreIndex)
				: duplicateNames.Contains(sectionName) ? $"{sectionName} {scoreIndex + 1}" : sectionName;
			var button = new Button
			{
				Name = $"Section_{sectionId:N}",
				CustomMinimumSize = new Vector2(56, 48),
				ToggleMode = true,
				ButtonPressed = _visibleSectionIds.Contains(sectionId),
				ThemeTypeVariation = "AppToggleButton",
				Text = displayName,
				TooltipText = string.Format(Tr("Section {0}: {1} · {2}\nRight-click to delete; Shift-click to show or hide."), scoreIndex + 1, displayName, section.Title)
			};
			var shiftOnPress = false;
			button.Pressed += () => HandleSectionButtonPressed(sectionId, shiftOnPress || Input.IsKeyPressed(Key.Shift));
			button.GuiInput += inputEvent =>
			{
				if (inputEvent is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
					shiftOnPress = click.ShiftPressed;
				else if (inputEvent is InputEventKey { Pressed: true } key)
					shiftOnPress = key.ShiftPressed;
				HandleSectionButtonGuiInput(button, sectionId, inputEvent);
			};
			_sectionNavigationButtons.AddChild(button);
		}
	}

	private static string GetSectionFallbackLabel(int index)
	{
		if (index < 0)
			return "?";

		var label = string.Empty;
		var value = index + 1;
		while (value > 0)
		{
			value--;
			label = (char)('A' + value % 26) + label;
			value /= 26;
		}
		return label;
	}

	private Guid GetFocusedSectionId()
	{
		if (_score.Sections.Count == 0)
			return Guid.Empty;
		_selectedSectionIndex = Math.Clamp(_selectedSectionIndex, 0, _score.Sections.Count - 1);
		return _score.Sections[_selectedSectionIndex].Id;
	}

	private void HandleSectionButtonPressed(Guid sectionId, bool shiftPressed)
	{
		var section = _score.Sections.FirstOrDefault(candidate => candidate.Id == sectionId);
		if (section is null)
			return;

		var wasVisible = _visibleSectionIds.Contains(sectionId);
		if (!SectionSelectionLogic.ApplyClick(_visibleSectionIds, sectionId, shiftPressed))
		{
			RenderSectionNavigation();
			return;
		}

		if (!shiftPressed || !wasVisible)
		{
			SetFocusedSection(section);
		}
		else if (GetFocusedSectionId() == sectionId)
		{
			var nextFocusId = SectionSelectionLogic.OrderVisible(_score.Sections.Select(candidate => candidate.Id), _visibleSectionIds).First();
			SetFocusedSection(_score.Sections.First(candidate => candidate.Id == nextFocusId));
		}

		MarkEditorStateDirty();
		RenderSection();
		FrameVisibleSections();
	}

	private void HandleSectionButtonGuiInput(Button button, Guid sectionId, InputEvent inputEvent)
	{
		if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
			return;

		if (!_score.Sections.Any(section => section.Id == sectionId))
			return;
		_sectionPendingNavigationDeletionId = sectionId;
		_sectionNavigationPopup.Position = DisplayServer.MouseGetPosition();
		_sectionNavigationPopup.Popup();
		button.AcceptEvent();
	}

	private void OpenSectionDeleteConfirmation()
	{
		if (_sectionPendingNavigationDeletionId is not Guid sectionId)
			return;
		var section = _score.Sections.FirstOrDefault(candidate => candidate.Id == sectionId);
		if (section is null)
			return;

		_sectionNavigationDeleteConfirmation.DialogText = string.Format(Tr("Delete section {0} and its contents from all tracks?"), section.Name);
		_sectionNavigationDeleteConfirmation.PopupCentered();
	}

	private void DeletePendingNavigationSection()
	{
		if (_sectionPendingNavigationDeletionId is not Guid deletedId)
			return;
		var deleted = _score.Sections.FirstOrDefault(section => section.Id == deletedId);
		if (deleted is null)
			return;

		var focusedId = GetFocusedSectionId();
		var replacingLastSection = _score.Sections.Count == 1;
		var selectedAfterDeletion = _score.RemoveSection(deleted);
		if (replacingLastSection && selectedAfterDeletion.Measures.Count > 0)
			InitializeMeasureSnapDefaults(selectedAfterDeletion.Measures[0]);
		foreach (var key in _sectionRepeatSourceIndices.Keys.Where(key => key.SectionId == deletedId).ToArray())
			_sectionRepeatSourceIndices.Remove(key);
		_visibleSectionIds.Remove(deletedId);
		_visibleSectionIds.RemoveWhere(id => !_score.Sections.Any(section => section.Id == id));
		var focusedIdAfterDeletion = SectionSelectionLogic.ResolveFocusAfterDeletion(
			_score.Sections.Select(section => section.Id).ToArray(), _visibleSectionIds, focusedId, selectedAfterDeletion.Id);
		var focused = _score.Sections.First(section => section.Id == focusedIdAfterDeletion);
		_selectedSectionIndex = _score.Sections.ToList().FindIndex(section => section.Id == focused.Id);
		_sectionPendingNavigationDeletionId = null;
		MarkEditorStateDirty();
		NotifyScoreEdited();
		RenderSection();
		FrameVisibleSections();
	}

	private void FrameVisibleSections()
	{
		Callable.From(() =>
		{
			if (!IsInsideTree()) return;
			var tabScroll = GetNode<ScrollContainer>("%ScrollContainer");
			tabScroll.ScrollHorizontal = 0;
		}).CallDeferred();
	}

}

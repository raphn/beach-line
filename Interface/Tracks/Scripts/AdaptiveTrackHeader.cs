#nullable enable
using Godot;

/// <summary>Arranges an expanded track's existing controls for the workspace height.</summary>
public partial class AdaptiveTrackHeader : FoldableContainer
{
	public enum Presentation { Compact, Standard, Tall }

	private Control? _compactPanel;
	private Control _thumbnail = null!;
	private Control _name = null!;
	private HBoxContainer _volume = null!;
	private HBoxContainer _actions = null!;
	private Control _sectionInfo = null!;
	private RichTextLabel _sectionLabel = null!;
	private Control _delete = null!;
	private Presentation? _presentation;

	public override void _Ready()
	{
		_compactPanel = GetNodeOrNull<Control>("VBoxContainer/CompactTrackControls");
		if (_compactPanel is null)
		{
			var header = GetNode<Control>("BoxContainer/TrackContainer/VBoxContainer/TrackControls/HBoxContainer");
			var identity = header.GetNode<Control>("HBoxContainer");
			_thumbnail = identity.GetNode<Control>("TextureRect");
			_name = identity.GetNode<Control>("NameContainer");
			_volume = identity.GetNode<HBoxContainer>("HBoxContainer2");
			_actions = identity.GetNode<HBoxContainer>("Config");
			_sectionInfo = header.GetNode<Control>("HeaderContainer/TrackSectionInfo");
			_delete = header.GetNode<Control>("DeleteButton");
		}
		else
		{
			var sidebar = GetNode<Control>("VBoxContainer/TrackContainer/HBoxContainer/TrackControls/VBoxContainer");
			_thumbnail = sidebar.GetNode<Control>("PanelContainer/TextureRect");
			_name = sidebar.GetNode<Control>("VBoxContainer/NameContainer");
			_volume = sidebar.GetNode<HBoxContainer>("VBoxContainer/VolumeContainer");
			_actions = sidebar.GetNode<HBoxContainer>("Configs");
			_sectionInfo = sidebar.GetNode<Control>("TrackSectionInfo");
			_delete = sidebar.GetNode<Control>("PanelContainer/DeleteButton");
		}
		_sectionLabel = _sectionInfo.GetNode<RichTextLabel>("SectionInfoHolder");
	}

	public static void Apply(FoldableContainer row, Presentation presentation)
	{
		if (row is AdaptiveTrackHeader header) header.SetPresentation(presentation);
	}

	private void SetPresentation(Presentation presentation)
	{
		if (_presentation == presentation) return;
		var compact = presentation == Presentation.Compact;
		var tall = presentation == Presentation.Tall;
		var hasSidebar = _compactPanel is not null;

		if (_compactPanel is not null && _compactPanel.Visible != compact)
			ArrangeSidebar(_compactPanel, compact);

		var thumbnailSize = compact ? 24 : tall ? (hasSidebar ? 64 : 56) : 36;
		_thumbnail.CustomMinimumSize = new Vector2(thumbnailSize, thumbnailSize);
		_name.CustomMinimumSize = new Vector2(compact ? 132 : tall ? (hasSidebar ? 224 : 192) : 156, 0);
		_volume.CustomMinimumSize = new Vector2(compact ? 118 : hasSidebar ? 0 : tall ? 256 : 148, 0);
		_volume.AddThemeConstantOverride("separation", compact ? 2 : 4);
		_actions.AddThemeConstantOverride("separation", compact ? 2 : 4);

		// Keep controls at their normal hit size. Only the artwork and gaps shrink.
		// The track title already identifies the section in Compact.
		_sectionLabel.Visible = !compact;
		_sectionLabel.FitContent = tall;
		_sectionLabel.CustomMinimumSize = new Vector2(tall ? 0 : 76, 20);
		if (hasSidebar)
		{
			var artworkPanel = GetNode<PanelContainer>("VBoxContainer/TrackContainer/HBoxContainer/TrackControls/VBoxContainer/PanelContainer");
			artworkPanel.SizeFlagsVertical = tall ? SizeFlags.ExpandFill : SizeFlags.Fill;
		}
		_presentation = presentation;
	}

	private void ArrangeSidebar(Control compactPanel, bool compact)
	{
		var sidebarPanel = GetNode<Control>("VBoxContainer/TrackContainer/HBoxContainer/TrackControls");
		var sidebar = sidebarPanel.GetNode<VBoxContainer>("VBoxContainer");
		var artwork = sidebar.GetNode<PanelContainer>("PanelContainer");
		var details = sidebar.GetNode<VBoxContainer>("VBoxContainer");
		var strip = compactPanel.GetNode<HFlowContainer>("Controls");
		var thumbnailActions = strip.GetNode<HBoxContainer>("ThumbnailActions");
		var separator = _actions.GetNode<Control>("VSeparator");

		// Moving the name editor releases focus. Block its commit signal for the
		// move so changing presentation while typing preserves the pending edit.
		var nameEdit = _name.GetNode<LineEdit>("TrackNameEdit");
		var restoreFocus = nameEdit.HasFocus();
		var signalsBlocked = nameEdit.IsBlockingSignals();
		if (restoreFocus) nameEdit.SetBlockSignals(true);

		_delete.Reparent(compact ? thumbnailActions : artwork, false);
		_thumbnail.Reparent(compact ? thumbnailActions : artwork, false);
		// Restore the sidebar's Delete overlay above the artwork.
		if (!compact) artwork.MoveChild(_thumbnail, 0);
		_name.Reparent(compact ? strip : details, false);
		_volume.Reparent(compact ? strip : details, false);
		_actions.Reparent(compact ? strip : sidebar, false);
		_sectionInfo.Reparent(compact ? strip : sidebar, false);
		_thumbnail.SizeFlagsHorizontal = compact ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill;
		_thumbnail.SizeFlagsVertical = compact ? SizeFlags.ShrinkCenter : SizeFlags.ExpandFill;
		_name.SizeFlagsVertical = compact ? SizeFlags.ShrinkCenter : SizeFlags.Fill;
		_volume.SizeFlagsVertical = compact ? SizeFlags.ShrinkCenter : SizeFlags.Fill;
		_actions.SizeFlagsVertical = compact ? SizeFlags.ShrinkCenter : SizeFlags.Fill;
		_sectionInfo.SizeFlagsVertical = compact ? SizeFlags.ShrinkCenter : SizeFlags.Fill;
		separator.Visible = !compact;
		sidebarPanel.Visible = !compact;
		compactPanel.Visible = compact;

		if (!restoreFocus) return;
		nameEdit.GrabFocus();
		nameEdit.SetBlockSignals(signalsBlocked);
	}
}

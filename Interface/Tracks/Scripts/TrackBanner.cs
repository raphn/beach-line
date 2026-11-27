using System;
using Godot;

/// <summary>A selectable track banner with its artwork supplied by the scene.</summary>
public partial class TrackBanner : PanelContainer
{
	[Export] private TextureRect _artwork;
	[Export] private Control _borders;
	[Export] private Button _selectionButton;

	public event Action<bool> SelectionChanged;

	public override void _Ready()
	{
		_selectionButton.Toggled += OnToggled;
		UpdateAppearance(_selectionButton.ButtonPressed);
	}

	public override void _ExitTree()
	{
		if (GodotObject.IsInstanceValid(_selectionButton))
			_selectionButton.Toggled -= OnToggled;
	}

	public void SetSelected(bool selected)
	{
		_selectionButton.SetPressedNoSignal(selected);
		UpdateAppearance(selected);
	}

	public void SetTrackName(string name)
	{
		_selectionButton.TooltipText = $"{name}\nClick to show or hide this track";
	}

	public void SetImage(Texture2D image)
	{
		_artwork.Texture = image;
	}

	private void OnToggled(bool selected)
	{
		UpdateAppearance(selected);
		SelectionChanged?.Invoke(selected);
	}

	private void UpdateAppearance(bool selected)
	{
		_artwork.SelfModulate = selected ? Colors.White : Colors.DimGray;
		_borders.Visible = selected;
	}
}

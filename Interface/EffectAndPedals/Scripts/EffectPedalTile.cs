using Godot;

public partial class EffectPedalTile : PanelContainer
{
	[Export] public TextureRect Artwork { get; set; }
	[Export] public Label TitleLabel { get; set; }
	[Export] public CheckButton EnabledButton { get; set; }
	[Export] public Button RemoveButton { get; set; }
	[Export] public OptionButton PresetButton { get; set; }
	[Export] public BoxContainer Knobs { get; set; }
}

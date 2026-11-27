using Godot;

public partial class EffectPedalKnob : PanelContainer
{
	[Export] public Label NameLabel { get; set; }
	[Export] public HSlider Slider { get; set; }
	[Export] public Label ValueLabel { get; set; }
	[Export] public Button AutomationButton { get; set; }
}

#nullable enable
using Godot;

/// <summary>Editor-authored presentation and starting values for one pedal type.</summary>
[GlobalClass]
public partial class PedalInfo : Resource
{
    [Export] public string TypeId { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export] public Texture2D? Image { get; set; }
    [Export] public PedalConfiguration DefaultConfiguration { get; set; } = new();
    [Export] public Godot.Collections.Array<PedalConfiguration> BuiltinConfigurations { get; set; } = new();
}

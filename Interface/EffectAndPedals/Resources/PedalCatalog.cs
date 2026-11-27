#nullable enable
using Godot;

/// <summary>Editor-authored catalog of effect pedal types available to a track.</summary>
[GlobalClass]
public partial class PedalCatalog : Resource
{
    [Export]
    public Godot.Collections.Array<PedalInfo> Pedals { get; set; } = new();

    /// <summary>Finds a pedal definition by its stable type identifier.</summary>
    public PedalInfo? Find(string typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId)) return null;
        foreach (var pedal in Pedals)
            if (pedal is not null && pedal.TypeId == typeId)
                return pedal;
        return null;
    }
}

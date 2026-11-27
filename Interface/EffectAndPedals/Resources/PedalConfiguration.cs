#nullable enable
using System;
using Godot;
using OpenTab.Music;

/// <summary>A named set of normalized values for a pedal's controls.</summary>
[GlobalClass]
public partial class PedalConfiguration : Resource
{
    [Export] public string DisplayName { get; set; } = "";
    [Export] public Godot.Collections.Dictionary<string, float> Values { get; set; } = new();

    /// <summary>Applies this configuration without changing the pedal's automation lanes.</summary>
    public void Apply(TrackPedal pedal)
    {
        ArgumentNullException.ThrowIfNull(pedal);
        foreach (var (key, value) in Values)
        {
            if (string.IsNullOrWhiteSpace(key) || !TrackPedal.ValidValue(value))
                throw new ArgumentException("Pedal configuration values must use nonempty parameter IDs and normalized values.", nameof(Values));
        }

        foreach (var (key, value) in Values)
        {
            if (pedal.TypeId == "overdrive")
                switch (key)
                {
                    case "drive": pedal.Drive = value; continue;
                    case "tone": pedal.Tone = value; continue;
                    case "level": pedal.Level = value; continue;
                }
            pedal.SetParameter(key, value);
        }
    }

    /// <summary>Checks whether the pedal's base values match this configuration.</summary>
    public bool Matches(TrackPedal pedal)
    {
        ArgumentNullException.ThrowIfNull(pedal);
        foreach (var (key, value) in Values)
        {
            var actual = pedal.TypeId == "overdrive" ? key switch
            {
                "drive" => pedal.Drive,
                "tone" => pedal.Tone,
                "level" => pedal.Level,
                _ => pedal.GetParameter(key)
            } : pedal.GetParameter(key);
            if (Math.Abs(actual - value) >= .0001f) return false;
        }
        return true;
    }
}

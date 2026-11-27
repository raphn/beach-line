#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenTab.Music;

/// <summary>An ordered, track-owned effect and its parameter automation.</summary>
public sealed class TrackPedal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Stable effect identifier. Unknown effects are retained when a project is opened.</summary>
    public string TypeId { get; set; } = "overdrive";
    public bool Enabled { get; set; } = true;
    /// <summary>Normalized overdrive controls; level 0.5 represents the default output level.</summary>
    public float Drive { get; set; } = 0.4f;
    public float Tone { get; set; } = 0.5f;
    public float Level { get; set; } = 0.5f;
    /// <summary>Additional normalized controls, keyed by stable parameter ID.</summary>
    public Dictionary<string, float> Parameters { get; } = new(StringComparer.Ordinal);
    /// <summary>Unrecognized serialized pedal fields retained for forward-compatible editing.</summary>
    public Dictionary<string, JsonElement> ExtensionData { get; } = new(StringComparer.Ordinal);
    public List<PedalAutomationLane> Automation { get; } = new();

    /// <summary>Returns a stored parameter or the effect's default when it is absent.</summary>
    public float GetParameter(string parameterId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        if (Parameters.TryGetValue(parameterId, out var value)) return value;
        return (TypeId, parameterId) switch
        {
            ("chorus", "rate") => .35f,
            ("chorus", "depth") => .45f,
            ("chorus", "mix") => .35f,
            ("delay", "time") => .35f,
            ("delay", "feedback") => .3f,
            ("delay", "mix") => .3f,
            _ => .5f
        };
    }

    public void SetParameter(string parameterId, float value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterId);
        if (!ValidValue(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Parameters[parameterId] = value;
    }

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("A pedal must have an identity.");
        if (string.IsNullOrWhiteSpace(TypeId)) throw new ArgumentException("A pedal must have a type.");
        if (!ValidValue(Drive) || !ValidValue(Tone) || !ValidValue(Level))
            throw new ArgumentException("Pedal values must be finite and in the range 0–1.");
        foreach (var (parameter, value) in Parameters)
            if (string.IsNullOrWhiteSpace(parameter) || !ValidValue(value))
                throw new ArgumentException("Pedal parameter IDs must be nonempty and values must be finite and in the range 0–1.");
        var parameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in Automation)
        {
            if (lane is null || !parameters.Add(lane.ParameterId)) throw new ArgumentException("Pedal automation lanes must be unique and non-null.");
            lane.Validate();
        }
    }

    internal static bool ValidValue(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;
}

/// <summary>Source-score quarter-note position, normalized value and interpolation to the next point.</summary>
public sealed record PedalAutomationPoint(double QuarterNote, float Value, ExpressionSegmentMode SegmentMode = ExpressionSegmentMode.Linear)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

public sealed class PedalAutomationLane
{
    public string ParameterId { get; set; } = "";
    public List<PedalAutomationPoint> Points { get; } = new();
    public Dictionary<string, JsonElement> ExtensionData { get; } = new(StringComparer.Ordinal);

    /// <summary>Evaluates a lane; a lane without points leaves its parameter at its fallback value.</summary>
    public float Sample(double quarterNote, float fallback)
    {
        if (!double.IsFinite(quarterNote)) throw new ArgumentOutOfRangeException(nameof(quarterNote));
        Validate();
        if (Points.Count == 0) return fallback;
        if (quarterNote <= Points[0].QuarterNote) return Points[0].Value;
        for (var i = 1; i < Points.Count; i++)
        {
            var right = Points[i];
            if (quarterNote >= right.QuarterNote) continue;
            var left = Points[i - 1];
            if (left.SegmentMode == ExpressionSegmentMode.Step) return left.Value;
            var fraction = (quarterNote - left.QuarterNote) / (right.QuarterNote - left.QuarterNote);
            return (float)(left.Value + (right.Value - left.Value) * fraction);
        }
        return Points[^1].Value;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ParameterId)) throw new ArgumentException("An automation lane must name its parameter.");
        var previous = -1d;
        foreach (var point in Points)
        {
            if (point is null || !double.IsFinite(point.QuarterNote) || point.QuarterNote < 0d || point.QuarterNote <= previous ||
                !TrackPedal.ValidValue(point.Value) || !Enum.IsDefined(point.SegmentMode))
                throw new ArgumentException("Automation points must have increasing nonnegative positions, normalized values, and valid interpolation.");
            previous = point.QuarterNote;
        }
    }
}

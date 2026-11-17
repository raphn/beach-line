using System;

namespace OpenTab.Music.Playback;

/// <summary>A playback location within a score measure.</summary>
public readonly record struct PlaybackPosition(Guid MeasureId, double Fraction);

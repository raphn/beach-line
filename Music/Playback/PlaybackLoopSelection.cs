#nullable enable

using System;
using System.Collections.Generic;

namespace OpenTab.Music.Playback;

/// <summary>The playback scope used by the score transport.</summary>
public enum PlaybackLoopMode
{
    None,
    Score,
    Measure,
    Sections,
    ArrangementOnce,
    ArrangementLoop
}

/// <summary>Identifies a score, measure, or ordered set of sections to play.</summary>
public sealed record PlaybackLoopSelection(
    PlaybackLoopMode Mode,
    Guid? MeasureId,
    IReadOnlyList<Guid> SectionIds,
    Guid? ArrangementId = null);

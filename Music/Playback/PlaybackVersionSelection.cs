#nullable enable

using System;
using System.Collections.Generic;

namespace OpenTab.Music.Playback;

/// <summary>
/// Runtime-only snapshot of the editor's per-measure, per-track version choices.
/// Missing entries select the original content. This is intentionally not score data.
/// </summary>
public sealed class PlaybackVersionSelection
{
    private readonly IReadOnlyDictionary<(Guid MeasureId, Guid TrackId), Guid> _versions;

    public static PlaybackVersionSelection Empty { get; } = new(
        new Dictionary<(Guid MeasureId, Guid TrackId), Guid>());

    public PlaybackVersionSelection(IReadOnlyDictionary<(Guid MeasureId, Guid TrackId), Guid> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        _versions = new Dictionary<(Guid MeasureId, Guid TrackId), Guid>(versions);
    }

    public bool TryGetVersionId(Guid measureId, Guid trackId, out Guid versionId) =>
        _versions.TryGetValue((measureId, trackId), out versionId);
}

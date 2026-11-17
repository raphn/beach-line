#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTab.Music;

/// <summary>Bounded undo and redo storage for serialized score states.</summary>
/// <remarks>
/// The editor owns when a score edit is committed and how a restored state is rendered.
/// This class only maintains immutable snapshots, so it can be used by score models and UI.
/// </remarks>
public sealed class ScoreHistory
{
    private readonly int _capacity;
    private readonly List<byte[]> _undo = new();
    private readonly List<byte[]> _redo = new();
    private byte[]? _current;

    public ScoreHistory(int capacity = 100)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Starts a new history epoch, such as after opening or creating a score.</summary>
    public void Reset(ReadOnlySpan<byte> state)
    {
        _undo.Clear();
        _redo.Clear();
        _current = state.ToArray();
    }

    /// <summary>Records the post-edit state; the previous state becomes undoable.</summary>
    public void Record(ReadOnlySpan<byte> state)
    {
        var next = state.ToArray();
        if (_current is null)
        {
            _current = next;
            return;
        }
        if (_current.SequenceEqual(next)) return;
        _undo.Add(_current);
        if (_undo.Count > _capacity) _undo.RemoveAt(0);
        _current = next;
        _redo.Clear();
    }

    public bool TryUndo(out byte[] state) => TryMove(_undo, _redo, out state);
    public bool TryRedo(out byte[] state) => TryMove(_redo, _undo, out state);

    private bool TryMove(List<byte[]> source, List<byte[]> destination, out byte[] state)
    {
        if (_current is null || source.Count == 0)
        {
            state = Array.Empty<byte>();
            return false;
        }

        destination.Add(_current);
        _current = source[^1];
        source.RemoveAt(source.Count - 1);
        state = (byte[])_current.Clone();
        return true;
    }
}

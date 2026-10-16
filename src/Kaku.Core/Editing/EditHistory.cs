namespace Kaku.Core.Editing;

/// <summary>State to go back to: the chapter source and where the caret was.</summary>
public sealed record EditSnapshot(string Source, TextLocation Caret, int CodeOffset, bool CodeMode);

/// <summary>
/// One undo history per file, shared by text and code mode (it stores whole-source snapshots).
/// Consecutive edits with the same group key (typing in one paragraph) within a short time form one step.
/// </summary>
public sealed class EditHistory(int limit = 400)
{
    private static readonly TimeSpan GroupWindow = TimeSpan.FromSeconds(1.5);
    private readonly LinkedList<EditSnapshot> _undo = new();
    private readonly Stack<EditSnapshot> _redo = new();
    private string? _lastKey;
    private DateTime _lastTime;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Call before applying an edit, with the state before it. A null key never groups.</summary>
    public void Record(EditSnapshot before, string? groupKey, DateTime? now = null)
    {
        var t = now ?? DateTime.UtcNow;
        bool grouped = groupKey != null && groupKey == _lastKey && t - _lastTime < GroupWindow && _undo.Count > 0;
        _lastKey = groupKey;
        _lastTime = t;
        _redo.Clear();
        if (grouped) return;
        _undo.AddLast(before);
        if (_undo.Count > limit) _undo.RemoveFirst();
    }

    /// <summary>Ends the current group so the next edit starts a new undo step.</summary>
    public void Break() => _lastKey = null;

    /// <summary>Forgets everything (the chapter was rebuilt by a structural change).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastKey = null;
    }

    public EditSnapshot? Undo(EditSnapshot current)
    {
        if (_undo.Last is not { } last) return null;
        _undo.RemoveLast();
        _redo.Push(current);
        _lastKey = null;
        return last.Value;
    }

    public EditSnapshot? Redo(EditSnapshot current)
    {
        if (_redo.Count == 0) return null;
        var next = _redo.Pop();
        _undo.AddLast(current);
        _lastKey = null;
        return next;
    }
}

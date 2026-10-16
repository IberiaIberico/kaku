namespace Kaku.Core.Navigation;

/// <summary>Back/forward through the places you jumped from (Alt+← / Alt+→), like a browser or VS Code.</summary>
public sealed class NavigationHistory(int limit = 100)
{
    private readonly LinkedList<TextLocation> _back = new();
    private readonly Stack<TextLocation> _forward = new();

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Call before a jump with the location being left. Nearby entries are merged.</summary>
    public void Record(TextLocation from)
    {
        if (_back.Last is { } last && Near(last.Value, from)) _back.RemoveLast();
        _back.AddLast(from);
        if (_back.Count > limit) _back.RemoveFirst();
        _forward.Clear();
    }

    public TextLocation? Back(TextLocation current)
    {
        // skip an entry that is where we already are
        while (_back.Last is { } last && Near(last.Value, current) && _back.Count > 1) _back.RemoveLast();
        if (_back.Last is not { } target) return null;
        _back.RemoveLast();
        _forward.Push(current);
        return target.Value;
    }

    public TextLocation? Forward(TextLocation current)
    {
        if (_forward.Count == 0) return null;
        var target = _forward.Pop();
        _back.AddLast(current);
        return target;
    }

    public void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    private static bool Near(TextLocation a, TextLocation b) => a.Chapter == b.Chapter && Math.Abs(a.Block - b.Block) <= 2;
}

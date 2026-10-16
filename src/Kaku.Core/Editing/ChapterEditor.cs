using System.Xml.Linq;
using Kaku.Core.Epub;
using Kaku.Core.Text;

namespace Kaku.Core.Editing;

/// <summary>Replace visible characters [Start, End) of a block with <see cref="Insert"/>.</summary>
public readonly record struct TextChange(int Start, int End, string Insert);

/// <summary>
/// Markup-safe editing of a chapter in text mode. Every operation changes the DOM of the affected
/// block(s), serializes only those elements and splices them into the source, so the rest of the
/// file stays byte-identical. After each operation the chapter is reloaded and positions refreshed.
/// </summary>
public sealed class ChapterEditor(Book book, Chapter chapter)
{
    public Chapter Chapter => chapter;

    // ------------------------------------------------------------------ text edits

    /// <summary>Applies text changes (in the block's visible-text coordinates) to one block.</summary>
    public bool ApplyTextChanges(int blockIndex, IReadOnlyList<TextChange> changes)
    {
        if (!TryBlock(blockIndex, out var block) || changes.Count == 0) return false;
        var map = block.Map;
        // right to left, so earlier offsets stay valid
        foreach (var ch in changes.OrderByDescending(c => c.Start))
        {
            if (ch.Start < 0 || ch.End > map.Length || ch.Start > ch.End) return false;
            for (int i = ch.Start; i < ch.End; i++)
                if (map[i].Placeholder != null) return false; // images/breaks are not editable as text
        }
        foreach (var ch in changes.OrderByDescending(c => c.Start))
            ApplyOne(block, ch);
        return Commit(block.Element!);
    }

    /// <summary>
    /// Text changes in many blocks with a single splice and reload (replace all). Anonymous blocks and blocks
    /// whose changes touch placeholders are not applied; their indexes are returned so the caller can retry
    /// them one by one or report them as skipped.
    /// </summary>
    public List<int> ApplyTextChangesBatch(IReadOnlyDictionary<int, List<TextChange>> perBlock)
    {
        var rejected = new List<int>();
        var edited = new List<XElement>();
        foreach (var (bi, changes) in perBlock)
        {
            if (!TryBlock(bi, out var block) || block.IsAnonymous || changes.Count == 0
                || changes.Any(c => c.Start < 0 || c.End > block.Map.Length || c.Start > c.End
                                    || Enumerable.Range(c.Start, c.End - c.Start).Any(i => block.Map[i].Placeholder != null))
                || chapter.RangeOf(block.Element!) is not { SelfClosing: false })
            {
                rejected.Add(bi);
                continue;
            }
            foreach (var ch in changes.OrderByDescending(c => c.Start)) ApplyOne(block, ch);
            edited.Add(block.Element!);
        }
        if (edited.Count == 0) return rejected;

        // leaf blocks never overlap: splice from the end so earlier offsets stay valid
        var src = chapter.SourceText;
        var sb = new System.Text.StringBuilder(src);
        foreach (var e in edited.OrderByDescending(e => chapter.RangeOf(e)!.Value.ContentStart))
        {
            var r = chapter.RangeOf(e)!.Value;
            sb.Remove(r.ContentStart, r.ContentEnd - r.ContentStart).Insert(r.ContentStart, XmlFragmentWriter.Inner(e));
        }
        chapter.Reload(sb.ToString());
        if (chapter.ParseError != null)
        {
            chapter.Reload(src);
            return perBlock.Keys.ToList();
        }
        Refresh();
        return rejected;
    }

    private static void ApplyOne(ContentBlock block, TextChange ch)
    {
        var map = block.Map;

        // where the inserted text goes, decided before deleting anything
        (XText Node, int Offset)? anchor = null;
        XNode? afterNode = null, beforeNode = null;
        if (ch.End > ch.Start && map[ch.Start].Node is { } firstDeleted)
            anchor = (firstDeleted, map[ch.Start].Start);
        else if (ch.Start > 0 && map[ch.Start - 1].Node is { } prev)
            anchor = (prev, map[ch.Start - 1].End);
        else if (ch.Start < map.Length && map[ch.Start].Node is { } next)
            anchor = (next, map[ch.Start].Start);
        else if (ch.Start > 0)
            afterNode = map[ch.Start - 1].Placeholder;
        else if (ch.Start < map.Length)
            beforeNode = map[ch.Start].Placeholder;

        // delete: per text node, one contiguous range (collapsed whitespace included)
        var cuts = new Dictionary<XText, (int Start, int End)>();
        void Cut(XText n, int s, int e) => cuts[n] = cuts.TryGetValue(n, out var r) ? (Math.Min(r.Start, s), Math.Max(r.End, e)) : (s, e);
        for (int i = ch.Start; i < ch.End; i++)
        {
            var src = map[i];
            if (src.Node != null) Cut(src.Node, src.Start, src.End);
            if (src.Extra != null) foreach (var (n, s, e) in src.Extra) Cut(n, s, e);
        }
        int anchorShift = 0;
        foreach (var (node, (s, e)) in cuts)
        {
            if (anchor is { } a && ReferenceEquals(a.Node, node) && s < a.Offset) anchorShift = Math.Min(e, a.Offset) - s;
            node.Value = node.Value.Remove(s, e - s);
        }

        if (ch.Insert.Length == 0) return;
        if (anchor is { } at)
        {
            int off = Math.Clamp(at.Offset - anchorShift, 0, at.Node.Value.Length);
            at.Node.Value = at.Node.Value.Insert(off, ch.Insert);
        }
        else if (afterNode != null) afterNode.AddAfterSelf(new XText(ch.Insert));
        else if (beforeNode != null) beforeNode.AddBeforeSelf(new XText(ch.Insert));
        else block.Element!.Add(new XText(ch.Insert)); // empty block
    }

    // ------------------------------------------------------------------ structure

    /// <summary>Splits a block at a visible offset (Enter). The new block gets the same tag and class.</summary>
    public bool SplitBlock(int blockIndex, int offset)
    {
        if (!TryBlock(blockIndex, out var block) || block.IsAnonymous || block.Element == null) return false;
        var e = block.Element;
        var map = block.Map;
        offset = Math.Clamp(offset, 0, map.Length);

        // DOM position of the split: (parent, index of first node that moves) after splitting a text node
        XNode? firstMoved;
        if (offset < map.Length && map[offset].Node is { } n)
            firstMoved = SplitText(n, map[offset].Start);
        else if (offset < map.Length && map[offset].Placeholder is { } ph)
            firstMoved = ph;
        else if (offset > 0 && map[offset - 1].Node is { } p)
            firstMoved = SplitText(p, map[offset - 1].End);
        else if (offset > 0 && map[offset - 1].Placeholder is { } pph)
            firstMoved = pph.NextNode ?? AppendEmptyAfter(pph);
        else
            firstMoved = e.FirstNode; // offset 0: everything moves

        var isHeading = XmlLoader.Local(e) is ['h', >= '1' and <= '6'];
        var newBlock = new XElement(isHeading && offset >= map.Length ? e.Name.Namespace + "p" : e.Name,
            CopyAttributes(e, keepClass: !(isHeading && offset >= map.Length)));

        // climb from the split point to the block, cloning each inline ancestor
        XNode? moving = firstMoved;
        XElement? parent = moving?.Parent;
        while (parent != null && parent != e)
        {
            var clone = new XElement(parent.Name, CopyAttributes(parent, keepClass: true));
            MoveFollowing(moving!, clone);
            parent.AddAfterSelf(clone);
            moving = clone;
            parent = clone.Parent;
        }
        if (moving != null) MoveFollowing(moving, newBlock);

        var range = chapter.RangeOf(e);
        if (range is not { } r || r.SelfClosing) return false;
        var whitespace = LeadingWhitespace(chapter.SourceText, r.OuterStart);
        var text = XmlFragmentWriter.Inner(e) + XmlFragmentWriter.EndTag(e) + whitespace + XmlFragmentWriter.Outer(newBlock);
        return Splice(r.ContentStart, r.OuterEnd, text);
    }

    /// <summary>
    /// Inserts a new paragraph containing an image after block <paramref name="blockIndex"/> (−1: at the start of the body).
    /// <paramref name="imagePath"/> is a zip path; the src is written relative to the chapter.
    /// </summary>
    public bool InsertImageParagraph(int blockIndex, string imagePath, string alt = "")
    {
        if (chapter.ParseError != null || chapter.Document?.Root is not { } root) return false;
        var ns = root.Name.Namespace;
        var src = EpubPath.Relative(chapter.Path, imagePath);
        var p = new XElement(ns + "p", new XAttribute("class", "kaku-imagen"), new XElement(ns + "img", new XAttribute("src", src), new XAttribute("alt", alt)));
        int at;
        string indent;
        if (blockIndex >= 0 && blockIndex < chapter.Blocks.Count && chapter.Blocks[blockIndex] is { IsAnonymous: false, Element: { } e }
            && chapter.RangeOf(e) is { } r)
        {
            e.AddAfterSelf(p); // attached, so it serializes with the document's prefixes
            at = r.OuterEnd;
            indent = LeadingWhitespace(chapter.SourceText, r.OuterStart);
        }
        else
        {
            var body = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "body");
            if (body == null || chapter.RangeOf(body) is not { SelfClosing: false } br) return false;
            body.AddFirst(p);
            at = br.ContentStart;
            indent = "\n";
        }
        var text = indent + XmlFragmentWriter.Outer(p);
        p.Remove();
        return Splice(at, at, text);
    }

    /// <summary>Inserts an inline element (note marker, …) at a visible-text offset of a block.</summary>
    public bool InsertInlineElement(int blockIndex, int offset, XElement element)
    {
        if (!TryBlock(blockIndex, out var block)) return false;
        var map = block.Map;
        offset = Math.Clamp(offset, 0, map.Length);
        if (offset < map.Length && map[offset].Node is { } n)
            SplitText(n, map[offset].Start).AddBeforeSelf(element);
        else if (offset < map.Length && map[offset].Placeholder is { } ph)
            ph.AddBeforeSelf(element);
        else if (offset > 0 && map[offset - 1].Node is { } p)
            SplitText(p, map[offset - 1].End).AddBeforeSelf(element);
        else if (offset > 0 && map[offset - 1].Placeholder is { } pph)
            pph.AddAfterSelf(element);
        else
            block.Element!.Add(element);
        return Commit(block.Element!);
    }

    /// <summary>True if the block can be merged into the previous one (Backspace at its start).</summary>
    public bool CanMergeWithPrevious(int blockIndex) => MergePair(blockIndex) != null;

    /// <summary>Appends the block's content to the previous block and removes it.</summary>
    public bool MergeWithPrevious(int blockIndex)
    {
        if (MergePair(blockIndex) is not var (prev, cur)) return false;
        var rp = chapter.RangeOf(prev)!.Value;
        var rc = chapter.RangeOf(cur)!.Value;
        var moved = string.Concat(cur.Nodes().Select(XmlFragmentWriter.Outer));
        return Splice(rp.ContentEnd, rc.OuterEnd, moved + XmlFragmentWriter.EndTag(prev));
    }

    private (XElement Prev, XElement Cur)? MergePair(int blockIndex)
    {
        if (blockIndex <= 0 || !TryBlock(blockIndex, out var cur) || !TryBlock(blockIndex - 1, out var prev)) return null;
        if (cur.IsAnonymous || prev.IsAnonymous || cur.Element == null || prev.Element == null) return null;
        // only real siblings: anything between them (other than whitespace) would be lost
        var between = prev.Element.NodesAfterSelf().TakeWhile(n => n != cur.Element).ToList();
        if (cur.Element.Parent != prev.Element.Parent || between.Any(n => n is not XText t || t.Value.Trim().Length > 0)) return null;
        var rp = chapter.RangeOf(prev.Element);
        var rc = chapter.RangeOf(cur.Element);
        if (rp is not { SelfClosing: false } || rc is not { SelfClosing: false }) return null;
        return (prev.Element, cur.Element);
    }

    /// <summary>Deletes visible text from (startBlock, startOffset) to (endBlock, endOffset), merging the blocks.</summary>
    public bool DeleteRange(int startBlock, int startOffset, int endBlock, int endOffset)
    {
        if (endBlock == startBlock)
            return ApplyTextChanges(startBlock, [new TextChange(startOffset, endOffset, "")]);
        if (endBlock < startBlock) return false;
        for (int k = startBlock + 1; k <= endBlock; k++)
            if (!CanMergeWithPrevious(k)) return false;
        // emptied blocks must survive the reload (an emptied <div> would vanish and shift the indexes)
        for (int k = startBlock; k <= endBlock; k++)
            if (!TextExtractor.KeepEmpty.Contains(chapter.Blocks[k].Tag)) return false;

        // text of the last block before endOffset, middle blocks entirely, tail of the first block
        if (!DeleteText(endBlock, 0, endOffset)) return false;
        for (int k = endBlock - 1; k > startBlock; k--)
            if (!DeleteText(k, 0, chapter.Blocks[k].Text.Length)) return false;
        if (!DeleteText(startBlock, startOffset, chapter.Blocks[startBlock].Text.Length)) return false;
        for (int k = endBlock; k > startBlock; k--)
            if (!MergeWithPrevious(k)) return false;
        return true;
    }

    /// <summary>Deletes the text characters of a range, leaving images and breaks in place.</summary>
    private bool DeleteText(int blockIndex, int start, int end)
    {
        var map = chapter.Blocks[blockIndex].Map;
        var changes = new List<TextChange>();
        int i = start;
        while (i < end)
        {
            while (i < end && map[i].Placeholder != null) i++;
            int s = i;
            while (i < end && map[i].Placeholder == null) i++;
            if (i > s) changes.Add(new TextChange(s, i, ""));
        }
        return changes.Count == 0 || ApplyTextChanges(blockIndex, changes);
    }

    // ------------------------------------------------------------------ formatting

    /// <summary>
    /// Bold/italic for a range inside one block. If the whole range is already formatted, the formatting
    /// element(s) around it are removed; otherwise each text piece of the range is wrapped.
    /// </summary>
    public bool ToggleFormat(int blockIndex, int start, int end, SpanKind kind)
    {
        if (kind is not (SpanKind.Bold or SpanKind.Italic) || !TryBlock(blockIndex, out var block) || end <= start) return false;
        var names = kind == SpanKind.Bold ? new[] { "b", "strong" } : new[] { "i", "em" };
        var map = block.Map;
        var chars = Enumerable.Range(start, Math.Min(end, map.Length) - start).Where(i => map[i].Node != null).ToList();
        if (chars.Count == 0) return false;

        XElement? Formatting(XNode n) => n.Ancestors().TakeWhile(a => a != block.Element)
            .FirstOrDefault(a => names.Contains(XmlLoader.Local(a)));

        var existing = chars.Select(i => Formatting(map[i].Node!)).ToList();
        if (existing.All(x => x != null))
        {
            foreach (var f in existing.Distinct().ToList())
            {
                f!.AddBeforeSelf(f.Nodes());
                f.Remove();
            }
        }
        else
        {
            var tag = block.Element!.Name.Namespace + (kind == SpanKind.Bold ? "b" : "i");
            // contiguous pieces per text node, wrapped right to left
            var pieces = chars.GroupBy(i => map[i].Node!)
                .Select(g => (Node: g.Key, Start: map[g.Min()].Start, End: map[g.Max()].End))
                .OrderByDescending(p => p.Start).ToList();
            foreach (var (node, s, e) in pieces)
            {
                var value = node.Value;
                var wrapped = new XElement(tag, value[s..e]);
                node.AddAfterSelf(wrapped);
                if (e < value.Length) wrapped.AddAfterSelf(new XText(value[e..]));
                if (s > 0) node.Value = value[..s];
                else node.Remove();
            }
        }
        return Commit(block.Element!);
    }

    // ------------------------------------------------------------------ code mode

    /// <summary>Replaces the whole source (code mode, undo). Returns the parse error, or null if well-formed.</summary>
    public string? SetSource(string source)
    {
        chapter.Reload(source);
        Refresh();
        return chapter.ParseError;
    }

    // ------------------------------------------------------------------ helpers

    private bool TryBlock(int index, out ContentBlock block)
    {
        block = null!;
        if (chapter.ParseError != null || index < 0 || index >= chapter.Blocks.Count) return false;
        block = chapter.Blocks[index];
        return block.Element != null;
    }

    /// <summary>Serializes the content of an edited element and splices it into the source.</summary>
    private bool Commit(XElement element)
    {
        if (chapter.RangeOf(element) is not { } r) return false;
        if (r.SelfClosing)
            return Splice(r.OuterStart, r.OuterEnd, XmlFragmentWriter.Outer(element));
        return Splice(r.ContentStart, r.ContentEnd, XmlFragmentWriter.Inner(element));
    }

    private bool Splice(int start, int end, string replacement)
    {
        var src = chapter.SourceText;
        chapter.Reload(string.Concat(src.AsSpan(0, start), replacement, src.AsSpan(end)));
        if (chapter.ParseError != null)
        {
            chapter.Reload(src); // never leave the chapter broken by a text-mode edit
            return false;
        }
        Refresh();
        return true;
    }

    private void Refresh() => book.RefreshPositions();

    private static XText SplitText(XText node, int offset)
    {
        var value = node.Value;
        var right = new XText(value[offset..]);
        node.Value = value[..offset];
        node.AddAfterSelf(right);
        return right;
    }

    private static XText AppendEmptyAfter(XNode n)
    {
        var t = new XText("");
        n.AddAfterSelf(t);
        return t;
    }

    private static void MoveFollowing(XNode first, XElement target)
    {
        var nodes = new List<XNode> { first };
        nodes.AddRange(first.NodesAfterSelf());
        foreach (var n in nodes) n.Remove();
        target.Add(nodes);
    }

    /// <summary>Attributes for a split-off copy: no id (must stay unique), no namespace declarations.</summary>
    private static IEnumerable<XAttribute> CopyAttributes(XElement e, bool keepClass) =>
        e.Attributes()
            .Where(a => !a.IsNamespaceDeclaration && a.Name.LocalName != "id" && a.Name.LocalName != TextBlockAttribute && (keepClass || a.Name.LocalName != "class"))
            .Select(a => new XAttribute(a));

    private const string TextBlockAttribute = "data-kaku-b";

    /// <summary>Whitespace before the element on its line, reused between split blocks.</summary>
    private static string LeadingWhitespace(string src, int elementStart)
    {
        int i = elementStart;
        while (i > 0 && src[i - 1] is ' ' or '\t') i--;
        return i > 0 && src[i - 1] == '\n' ? "\n" + src[i..elementStart] : "\n";
    }
}

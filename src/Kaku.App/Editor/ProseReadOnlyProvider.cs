using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace Kaku.App.Editor;

/// <summary>
/// Text mode protection: placeholders (images, line breaks) cannot be typed into or deleted, and line
/// breaks between blocks can only be removed by Kaku itself (merge), so one line is always one block.
/// </summary>
public sealed class ProseReadOnlyProvider(TextDocument document, TextSegmentCollection<TextSegment> chips) : IReadOnlySectionProvider
{
    public bool CanInsert(int offset) =>
        !chips.FindSegmentsContaining(offset).Any(s => s.StartOffset < offset && offset < s.EndOffset);

    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        int start = segment.Offset, end = segment.EndOffset;
        var blocked = new List<(int Start, int End)>();
        foreach (var c in chips.FindOverlappingSegments(start, segment.Length)) blocked.Add((c.StartOffset, c.EndOffset));
        for (int i = start; i < end; i++)
            if (document.GetCharAt(i) is '\n' or '\r') blocked.Add((i, i + 1));
        blocked.Sort();

        int pos = start;
        foreach (var (bs, be) in blocked)
        {
            if (bs > pos) yield return new TextSegment { StartOffset = pos, EndOffset = Math.Min(bs, end) };
            pos = Math.Max(pos, be);
            if (pos >= end) yield break;
        }
        if (pos < end) yield return new TextSegment { StartOffset = pos, EndOffset = end };
    }
}

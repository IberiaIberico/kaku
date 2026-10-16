using CommunityToolkit.Mvvm.ComponentModel;
using Kaku.App.Localization;
using Kaku.Core;
using Kaku.Core.Search;

namespace Kaku.App.ViewModels;

/// <summary>One row of the search results, with the replacement preview.</summary>
public sealed partial class SearchHitItem : ObservableObject
{
    public SearchHitItem(Book book, SearchHit hit)
    {
        Hit = hit;
        if (hit.Chapter >= 0)
        {
            var c = book.Chapters[hit.Chapter];
            GroupName = T.F("Search_GroupChapter", hit.Chapter + 1, c.Title);
            PositionText = hit.Block >= 0
                ? T.F("Search_PositionText", hit.Block + 1, T.N1(book.PercentAt(new TextLocation(hit.Chapter, hit.Block, hit.Start))))
                : T.F("Search_PositionLine", hit.Line);
        }
        else
        {
            GroupName = hit.Path;
            PositionText = T.F("Search_PositionLine", hit.Line);
        }
    }

    public SearchHit Hit { get; }
    public string GroupName { get; }
    public string PositionText { get; }
    public string Before => Hit.Before;
    public string Match => Hit.Match;
    public string After => Hit.After;

    [ObservableProperty]
    private bool _include = true;

    /// <summary>What the match becomes (null when not replacing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReplacement))]
    private string? _replacement;

    public bool HasReplacement => Replacement != null;
}

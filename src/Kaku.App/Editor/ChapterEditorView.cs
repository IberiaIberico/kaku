using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using Kaku.App.Localization;
using Kaku.App.Services;
using Kaku.Core;
using Kaku.Core.Editing;
using Kaku.Core.Epub;
using Kaku.Core.Search;
using Kaku.Core.Text;
using TextLocation = Kaku.Core.TextLocation;
using TextChange = Kaku.Core.Editing.TextChange;
using EditingCommands = System.Windows.Documents.EditingCommands;

namespace Kaku.App.Editor;

public enum EditKind
{
    /// <summary>Text inside one block changed (block count unchanged).</summary>
    Text,
    /// <summary>Blocks were split/merged/deleted: indexes changed.</summary>
    Structure,
    /// <summary>The whole source was replaced (code mode, undo).</summary>
    Source,
}

/// <summary>
/// A chapter tab: text mode (clean prose, one line per block) and code mode (raw XHTML), switchable
/// with Ctrl+E keeping the position. Both modes edit the same chapter and share one undo history.
/// </summary>
public sealed class ChapterEditorView : DockPanel
{
    private readonly Book _book;
    private readonly ChapterEditor _editor;
    private readonly TextSegmentCollection<TextSegment> _chips;
    private readonly Grid _editors = new();
    private readonly Border _errorBar;
    private readonly TextBlock _errorText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToolTip _hoverTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private readonly DispatcherTimer _codeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private TextEditor? _code;
    private bool _suppress;
    private bool _codePending;
    private int _caretLine = 1;
    private bool _inBatch;
    private bool _batchRecorded;

    private List<SearchHit> _searchHits = new();
    private readonly SpellingRenderer _spelling;
    private HashSet<int> _bookmarkLines = new();

    public ChapterEditorView(Book book, Chapter chapter, EditHistory history)
    {
        _book = book;
        Chapter = chapter;
        History = history;
        _editor = new ChapterEditor(book, chapter);

        var goToError = new Button { Content = T.Get("Editor_GoToError"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        goToError.Click += (_, _) => GoToParseError();
        var bar = new DockPanel();
        DockPanel.SetDock(goToError, Dock.Right);
        bar.Children.Add(goToError);
        bar.Children.Add(_errorText);
        _errorBar = new Border { Background = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)), Padding = new Thickness(10, 5, 10, 5), Child = bar, Visibility = Visibility.Collapsed };
        SetDock(_errorBar, Dock.Top);
        Children.Add(_errorBar);
        Children.Add(_editors);

        Prose = EditorFactory.CreateProse();
        Prose.IsReadOnly = false;
        Prose.Options.EnableTextDragDrop = false; // a drag could move text across blocks
        Prose.Document.UndoStack.SizeLimit = 0;   // Kaku's own history (EditHistory) is used
        _chips = new TextSegmentCollection<TextSegment>(Prose.Document);
        Prose.TextArea.ReadOnlySectionProvider = new ProseReadOnlyProvider(Prose.Document, _chips);
        Prose.TextArea.TextView.LineTransformers.Add(new TextModeColorizer(chapter));
        Prose.TextArea.TextView.BackgroundRenderers.Add(new BookmarkRenderer(() => _bookmarkLines));
        Prose.TextArea.TextView.BackgroundRenderers.Add(new SearchHighlightRenderer(ProseHighlights));
        _spelling = new SpellingRenderer(WordTextOfLine);
        Prose.TextArea.TextView.BackgroundRenderers.Add(_spelling);
        Prose.TextArea.ContextMenu = new ContextMenu();
        Prose.TextArea.ContextMenuOpening += OnProseContextMenu;
        SetProseText();

        Prose.Document.Changed += OnProseChanged;
        Prose.TextArea.Caret.PositionChanged += (_, _) => OnProseCaretMoved();
        Prose.TextArea.TextEntering += OnProseTextEntering;
        CommandManager.AddPreviewExecutedHandler(Prose.TextArea, OnProsePreviewExecuted);
        Prose.MouseHover += OnProseMouseHover;
        Prose.TextArea.PreviewMouseDoubleClick += (_, e) =>
        {
            var pos = Prose.GetPositionFromPoint(e.GetPosition(Prose));
            if (pos == null || pos.Value.Line - 1 >= Chapter.Blocks.Count) return;
            int col = pos.Value.Column - 1;
            var span = Chapter.Blocks[pos.Value.Line - 1].Spans.FirstOrDefault(sp => sp.Kind == SpanKind.NoteRef && col >= sp.Start && col <= sp.End);
            if (span != default && span.Data != null)
            {
                NoteMarkerActivated?.Invoke(span.Data);
                e.Handled = true;
            }
        };
        Prose.MouseHoverStopped += (_, _) => _hoverTip.IsOpen = false;
        _editors.Children.Add(Prose);

        _codeTimer.Tick += (_, _) => FlushCode();
        UpdateErrorBar();
    }

    public Chapter Chapter { get; }
    public TextEditor Prose { get; }
    public bool IsCodeMode { get; private set; }
    public EditHistory History { get; }

    public event EventHandler? CaretMoved;

    /// <summary>Text of the note a marker points to (href → text), for the hover tooltip.</summary>
    public Func<string, string?>? NoteText { get; set; }

    /// <summary>A note marker was double-clicked (href).</summary>
    public event Action<string>? NoteMarkerActivated;

    /// <summary>Images, line breaks and note markers: shown as protected chips in text mode.</summary>
    private static bool IsChip(InlineSpan s) => s.IsPlaceholder || s.Kind == SpanKind.NoteRef;

    /// <summary>Where a note marker goes: the end of the selection, or the caret.</summary>
    public (TextLocation Location, string Selected)? NoteInsertionPoint()
    {
        FlushCode();
        if (IsCodeMode && !SetCodeMode(false)) return null;
        SyncLineToModel(Prose.TextArea.Caret.Line - 1);
        var sel = Prose.TextArea.Selection;
        if (!sel.IsEmpty && !IsMultiLineSelection(Prose))
        {
            var end = sel.EndPosition.Location > sel.StartPosition.Location ? sel.EndPosition.Location : sel.StartPosition.Location;
            return (new TextLocation(Chapter.Index, end.Line - 1, ModelOffset(end.Line, end.Column - 1)), Prose.SelectedText.Trim());
        }
        // the marker sticks to the word: "palabra¹ y", not "palabra ¹y"
        var loc = CurrentLocation;
        var text = Chapter.Blocks.Count > loc.Block ? Chapter.Blocks[loc.Block].Text : "";
        int off = Math.Min(loc.Offset, text.Length);
        while (off > 0 && text[off - 1] == ' ') off--;
        return (loc with { Offset = off }, WordBeforeCaret());
    }

    private string WordBeforeCaret()
    {
        var line = Prose.Document.GetLineByNumber(Prose.TextArea.Caret.Line);
        var text = Prose.Document.GetText(line)[..(Prose.TextArea.Caret.Column - 1)].TrimEnd();
        int i = text.Length;
        while (i > 0 && char.IsLetterOrDigit(text[i - 1])) i--;
        return text[i..];
    }

    /// <summary>The chapter changed. The int is the edited block for <see cref="EditKind.Text"/>.</summary>
    public event Action<ChapterEditorView, EditKind, int>? Edited;

    private TextEditor ActiveEditor => IsCodeMode ? _code! : Prose;

    private TextEditor Code
    {
        get
        {
            if (_code == null)
            {
                _code = EditorFactory.CreateCode(Chapter.Path);
                _code.IsReadOnly = false;
                _code.Document.UndoStack.SizeLimit = 0;
                _code.Text = Chapter.SourceText;
                _code.TextArea.Caret.PositionChanged += (_, _) => RaiseCaretMoved();
                _code.Document.Changed += OnCodeChanged;
                _code.TextArea.TextView.BackgroundRenderers.Add(new SearchHighlightRenderer(CodeHighlights));
                CommandManager.AddPreviewExecutedHandler(_code.TextArea, OnCodePreviewExecuted);
                _code.Visibility = Visibility.Collapsed;
                _editors.Children.Add(_code);
            }
            return _code;
        }
    }

    private void RaiseCaretMoved() => CaretMoved?.Invoke(this, EventArgs.Empty);

    // ================================================================== positions

    /// <summary>Location of the caret in the text model (code mode maps the caret to the enclosing block).</summary>
    public TextLocation CurrentLocation
    {
        get
        {
            if (IsCodeMode)
                return new TextLocation(Chapter.Index, Chapter.BlockAtOffset(_code!.CaretOffset), 0);
            var caret = Prose.TextArea.Caret;
            int block = Math.Clamp(caret.Line - 1, 0, Math.Max(0, Chapter.Blocks.Count - 1));
            return new TextLocation(Chapter.Index, block, ModelOffset(caret.Line, caret.Column - 1));
        }
    }

    public void ToggleMode() => SetCodeMode(!IsCodeMode);

    public bool SetCodeMode(bool code)
    {
        if (code == IsCodeMode) return true;
        if (!code)
        {
            FlushCode();
            if (Chapter.ParseError != null)
            {
                SystemSounds.Beep.Play();
                GoToParseError();
                return false; // text mode needs well-formed XHTML
            }
        }
        var loc = CurrentLocation;
        if (code)
        {
            SyncAllProseLines();
            var ed = Code;
            if (ed.Text != Chapter.SourceText) SetCodeText(Chapter.SourceText);
        }
        else
        {
            SetProseText();
        }
        IsCodeMode = code;
        Prose.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
        if (_code != null) _code.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        GoTo(loc);
        ActiveEditor.Focus();
        RaiseCaretMoved();
        return true;
    }

    /// <summary>Puts the caret at the location and scrolls it into view (centred).</summary>
    public void GoTo(TextLocation loc)
    {
        if (Chapter.Blocks.Count == 0) return;
        int bi = Math.Clamp(loc.Block, 0, Chapter.Blocks.Count - 1);
        var ed = ActiveEditor;
        int offset;
        if (IsCodeMode)
        {
            offset = Math.Clamp(Chapter.Blocks[bi].SourceOffset, 0, ed.Document.TextLength);
        }
        else
        {
            var dl = ed.Document.GetLineByNumber(Math.Min(bi + 1, ed.Document.LineCount));
            offset = dl.Offset + Math.Clamp(loc.Offset, 0, dl.Length);
        }
        ed.TextArea.Caret.Offset = offset;
        ed.TextArea.ClearSelection();
        CenterCaret(ed);
    }

    private static void CenterCaret(TextEditor ed)
    {
        var pos = ed.Document.GetLocation(ed.CaretOffset);
        ed.Dispatcher.BeginInvoke(() =>
        {
            ed.ScrollTo(pos.Line, pos.Column);
            var y = ed.TextArea.TextView.GetVisualPosition(new TextViewPosition(pos), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle).Y;
            ed.ScrollToVerticalOffset(Math.Max(0, y - ed.ViewportHeight / 2));
        }, DispatcherPriority.Loaded);
    }

    public void FocusEditor() => ActiveEditor.Focus();

    /// <summary>XHTML inside a block element, for updating the preview in place (null: reload instead).</summary>
    public string? BlockInnerXhtml(int block) =>
        block >= 0 && block < Chapter.Blocks.Count && Chapter.Blocks[block] is { IsAnonymous: false, Element: { } e }
            ? XmlFragmentWriter.Inner(e) : null;

    // ================================================================== spelling

    public bool SpellingEnabled
    {
        get => _spelling.Enabled;
        set
        {
            _spelling.Enabled = value;
            Prose.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
        }
    }

    public void RedrawSpelling() => Prose.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);

    /// <summary>Text checked for a line: the block's word text, or the editor text while it is being typed.</summary>
    private string? WordTextOfLine(int lineNumber)
    {
        int bi = lineNumber - 1;
        if (bi < 0 || lineNumber > Prose.Document.LineCount) return null;
        var editor = Prose.Document.GetText(Prose.Document.GetLineByNumber(lineNumber));
        if (bi < Chapter.Blocks.Count && Chapter.Blocks[bi].Text == editor) return Chapter.Blocks[bi].WordText;
        return editor;
    }

    /// <summary>Set by the window: "Insertar nota…" in the context menu.</summary>
    public Action? InsertNoteRequested { get; set; }

    private void OnProseContextMenu(object sender, ContextMenuEventArgs e)
    {
        var menu = Prose.TextArea.ContextMenu!;
        menu.Items.Clear();
        var pos = Prose.GetPositionFromPoint(Mouse.GetPosition(Prose));
        if (pos != null && SpellingEnabled && _spelling.For(pos.Value.Line) is { } errors && SpellService.Checker is { } checker)
        {
            int col = pos.Value.Column - 1;
            var err = errors.FirstOrDefault(x => col >= x.Start && col <= x.Start + x.Length);
            if (err != default)
            {
                var line = Prose.Document.GetLineByNumber(pos.Value.Line);
                var suggestions = checker.Suggest(err.Word);
                foreach (var sug in suggestions)
                {
                    var mi = new MenuItem { Header = sug.Replace("_", "__"), FontWeight = FontWeights.SemiBold };
                    mi.Click += (_, _) =>
                    {
                        Prose.Document.Replace(line.Offset + err.Start, err.Length, sug);
                        Prose.Focus();
                    };
                    menu.Items.Add(mi);
                }
                if (suggestions.Count == 0) menu.Items.Add(new MenuItem { Header = T.Get("Spell_NoSuggestions"), IsEnabled = false });
                var add = new MenuItem { Header = T.F("Spell_Add", err.Word.Replace("_", "__")) };
                add.Click += (_, _) => SpellService.AddToDictionary(err.Word);
                var ignore = new MenuItem { Header = T.Get("Spell_Ignore") };
                ignore.Click += (_, _) => SpellService.Ignore(err.Word);
                menu.Items.Add(new Separator());
                menu.Items.Add(add);
                menu.Items.Add(ignore);
                menu.Items.Add(new Separator());
            }
        }
        void Cmd(string key, ICommand cmd, string gesture)
        {
            menu.Items.Add(new MenuItem { Header = T.Get(key), Command = cmd, CommandTarget = Prose.TextArea, InputGestureText = gesture });
        }
        Cmd("Menu_Cut", ApplicationCommands.Cut, "Ctrl+X");
        Cmd("Menu_Copy", ApplicationCommands.Copy, "Ctrl+C");
        Cmd("Menu_Paste", ApplicationCommands.Paste, "Ctrl+V");
        if (InsertNoteRequested != null)
        {
            menu.Items.Add(new Separator());
            var note = new MenuItem { Header = T.Get("Menu_InsertNote"), InputGestureText = "Ctrl+Alt+N" };
            note.Click += (_, _) => InsertNoteRequested();
            menu.Items.Add(note);
        }
    }

    // ================================================================== search, bookmarks, selection

    /// <summary>Matches to highlight in this chapter (text-scope hits use blocks, code-scope hits use offsets).</summary>
    public void SetSearchHits(IEnumerable<SearchHit> hits)
    {
        _searchHits = hits.Where(h => h.Chapter == Chapter.Index && h.Revision == Chapter.Revision).ToList();
        Prose.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
        _code?.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Selection);
    }

    private IEnumerable<(int, int)> ProseHighlights()
    {
        var doc = Prose.Document;
        foreach (var h in _searchHits)
        {
            if (h.Block < 0 || h.Block >= doc.LineCount) continue;
            var line = doc.GetLineByNumber(h.Block + 1);
            if (h.Start + h.Length <= line.Length) yield return (line.Offset + h.Start, h.Length);
        }
    }

    private IEnumerable<(int, int)> CodeHighlights()
    {
        int len = _code?.Document.TextLength ?? 0;
        foreach (var h in _searchHits)
            if (h.Block < 0 && h.Start + h.Length <= len) yield return (h.Start, h.Length);
    }

    /// <summary>Paragraphs (0-based block indexes) marked with a bookmark.</summary>
    public void SetBookmarks(IEnumerable<int> blocks)
    {
        _bookmarkLines = blocks.Select(b => b + 1).ToHashSet();
        Prose.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
    }

    /// <summary>Selects a search match (switching to code mode for source matches) and scrolls to it.</summary>
    public void SelectHit(SearchHit hit)
    {
        if (hit.Block < 0)
        {
            if (!IsCodeMode) SetCodeMode(true);
            var len = _code!.Document.TextLength;
            if (hit.Start + hit.Length > len) return;
            _code.Select(hit.Start, hit.Length);
            CenterCaret(_code);
            return;
        }
        if (IsCodeMode && !SetCodeMode(false)) return;
        SelectText(hit.Block, hit.Start, hit.Block, hit.Start + hit.Length);
    }

    /// <summary>Selects visible text from (block a, offset) to (block b, offset).</summary>
    public void SelectText(int blockA, int startA, int blockB, int endB)
    {
        if (IsCodeMode && !SetCodeMode(false)) return;
        var doc = Prose.Document;
        if (blockA >= doc.LineCount || blockB >= doc.LineCount) return;
        var la = doc.GetLineByNumber(blockA + 1);
        var lb = doc.GetLineByNumber(blockB + 1);
        int s = la.Offset + Math.Min(startA, la.Length);
        int e = lb.Offset + Math.Min(endB, lb.Length);
        Prose.Select(s, Math.Max(0, e - s));
        Prose.TextArea.Caret.Offset = s;
        CenterCaret(Prose);
        Prose.TextArea.Caret.Offset = e;
    }

    /// <summary>Opens code mode at a 1-based source line (links and problems point there).</summary>
    public void GoToSourceLine(int line)
    {
        if (!IsCodeMode) SetCodeMode(true);
        if (!IsCodeMode) return;
        var doc = _code!.Document;
        _code.TextArea.Caret.Offset = doc.GetLineByNumber(Math.Clamp(line, 1, doc.LineCount)).Offset;
        CenterCaret(_code);
        _code.Focus();
    }

    /// <summary>Inserts an image as a new paragraph after the paragraph with the caret.</summary>
    public bool InsertImageParagraph(string imagePath)
    {
        FlushCode();
        if (IsCodeMode && !SetCodeMode(false)) return false;
        SyncLineToModel(Prose.TextArea.Caret.Line - 1);
        var loc = CurrentLocation;
        Record(null);
        if (!_editor.InsertImageParagraph(Chapter.Blocks.Count == 0 ? -1 : loc.Block, imagePath)) return false;
        RebuildProse(new TextLocation(Chapter.Index, Math.Min(loc.Block + 1, Chapter.Blocks.Count - 1), 0));
        Edited?.Invoke(this, EditKind.Structure, loc.Block + 1);
        Prose.Focus();
        return true;
    }

    /// <summary>The chapter was changed from outside (replace all, recovery): show its current state.</summary>
    public void ReloadFromModel()
    {
        _codePending = false;
        _codeTimer.Stop();
        if (_code != null) SetCodeText(Chapter.SourceText);
        if (!IsCodeMode || Chapter.ParseError == null) RebuildProse(CurrentLocation);
        UpdateErrorBar();
        RaiseCaretMoved();
    }

    // ================================================================== text mode: model ⇄ editor

    /// <summary>Rebuilds the whole text-mode document from the chapter (one line per block).</summary>
    private void SetProseText()
    {
        _suppress = true;
        int caret = Prose.CaretOffset; // replacing the text moves the caret to the end
        try
        {
            Prose.Document.Text = string.Join("\n", Chapter.Blocks.Select(b => b.Text));
            Prose.CaretOffset = Math.Min(caret, Prose.Document.TextLength);
            _chips.Clear();
            for (int i = 0; i < Chapter.Blocks.Count; i++) AddChips(i);
        }
        finally
        {
            _suppress = false;
        }
        Prose.TextArea.TextView.Redraw();
    }

    private void AddChips(int block)
    {
        var line = Prose.Document.GetLineByNumber(block + 1);
        foreach (var s in Chapter.Blocks[block].Spans.Where(IsChip))
            if (s.End <= line.Length)
                _chips.Add(new TextSegment { StartOffset = line.Offset + s.Start, Length = s.Length });
    }

    /// <summary>Replaces one editor line with the model's text for that block.</summary>
    private void ResyncLine(int block)
    {
        if (block < 0 || block >= Chapter.Blocks.Count || block >= Prose.Document.LineCount)
        {
            RebuildProse(CurrentLocation);
            return;
        }
        var line = Prose.Document.GetLineByNumber(block + 1);
        var text = Chapter.Blocks[block].Text;
        _suppress = true;
        try
        {
            foreach (var c in _chips.FindOverlappingSegments(line.Offset, line.Length).ToList()) _chips.Remove(c);
            if (Prose.Document.GetText(line) != text) Prose.Document.Replace(line.Offset, line.Length, text);
            AddChips(block);
        }
        finally
        {
            _suppress = false;
        }
        Prose.TextArea.TextView.Redraw(line);
    }

    /// <summary>Full rebuild after structural changes, keeping the scroll position.</summary>
    private void RebuildProse(TextLocation caret)
    {
        double scroll = Prose.VerticalOffset;
        SetProseText();
        int bi = Math.Clamp(caret.Block, 0, Math.Max(0, Chapter.Blocks.Count - 1));
        var line = Prose.Document.GetLineByNumber(Math.Min(bi + 1, Prose.Document.LineCount));
        Prose.TextArea.Caret.Offset = line.Offset + Math.Clamp(caret.Offset, 0, line.Length);
        Prose.TextArea.ClearSelection();
        Prose.ScrollToVerticalOffset(scroll);
        Prose.TextArea.Caret.BringCaretToView();
    }

    private void OnProseChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_suppress) return;
        if (e.InsertedText.Text.Contains('\n') || e.RemovedText.Text.Contains('\n') || Prose.Document.LineCount != Chapter.Blocks.Count)
        {
            // should not happen (newlines are protected), but never let editor and model drift apart
            RebuildProse(CurrentLocation);
            return;
        }
        SyncLineToModel(Prose.Document.GetLineByOffset(e.Offset).LineNumber - 1);
    }

    /// <summary>Applies the difference between an editor line and its block to the chapter.</summary>
    private void SyncLineToModel(int bi)
    {
        if (bi < 0 || bi >= Chapter.Blocks.Count) return;
        var block = Chapter.Blocks[bi];
        var line = Prose.Document.GetLineByNumber(bi + 1);
        var editorText = Prose.Document.GetText(line);
        if (editorText == block.Text) return;

        // placeholders are the same, in the same order, on both sides: diff the text between them
        var chips = _chips.FindOverlappingSegments(line.Offset, line.Length).OrderBy(c => c.StartOffset).ToList();
        var holders = block.Spans.Where(IsChip).OrderBy(s => s.Start).ToList();
        if (chips.Count != holders.Count)
        {
            ResyncLine(bi);
            return;
        }
        var changes = new List<TextChange>();
        int ePos = 0, mPos = 0;
        for (int k = 0; k <= holders.Count; k++)
        {
            int eEnd = k < chips.Count ? chips[k].StartOffset - line.Offset : editorText.Length;
            int mEnd = k < holders.Count ? holders[k].Start : block.Text.Length;
            var eSeg = editorText[ePos..eEnd];
            var mSeg = block.Text[mPos..mEnd];
            if (eSeg != mSeg)
            {
                int p = 0;
                while (p < eSeg.Length && p < mSeg.Length && eSeg[p] == mSeg[p]) p++;
                int s = 0;
                while (s < eSeg.Length - p && s < mSeg.Length - p && eSeg[^(s + 1)] == mSeg[^(s + 1)]) s++;
                changes.Add(new TextChange(mPos + p, mPos + mSeg.Length - s, eSeg[p..(eSeg.Length - s)]));
            }
            if (k < holders.Count)
            {
                ePos = chips[k].EndOffset - line.Offset;
                mPos = holders[k].End;
            }
        }
        if (changes.Count == 0) return;

        Record($"t{bi}");
        if (!_editor.ApplyTextChanges(bi, changes))
        {
            ResyncLine(bi);
            SystemSounds.Beep.Play();
            return;
        }
        // the model collapses whitespace like a browser; keep the typed spaces until the caret leaves the line
        if (Normalize(editorText) != Normalize(Chapter.Blocks[bi].Text) || Chapter.Blocks.Count != Prose.Document.LineCount)
        {
            if (Chapter.Blocks.Count != Prose.Document.LineCount) RebuildProse(CurrentLocation);
            else ResyncLine(bi);
        }
        else
        {
            Prose.TextArea.TextView.Redraw(line);
        }
        Edited?.Invoke(this, EditKind.Text, bi);
    }

    private void OnProseCaretMoved()
    {
        int line = Prose.TextArea.Caret.Line;
        if (line != _caretLine)
        {
            int left = _caretLine - 1;
            _caretLine = line;
            History.Break();
            // tidy the line we left (spaces the model collapsed)
            if (left >= 0 && left < Chapter.Blocks.Count && left < Prose.Document.LineCount
                && Prose.Document.GetText(Prose.Document.GetLineByNumber(left + 1)) != Chapter.Blocks[left].Text)
                Dispatcher.BeginInvoke(() => ResyncLine(left), DispatcherPriority.Background);
        }
        RaiseCaretMoved();
    }

    private void SyncAllProseLines()
    {
        for (int i = 0; i < Chapter.Blocks.Count && i < Prose.Document.LineCount; i++)
            if (Prose.Document.GetText(Prose.Document.GetLineByNumber(i + 1)) != Chapter.Blocks[i].Text)
                ResyncLine(i);
    }

    private static string Normalize(string s) => string.Join(' ', s.Split([' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Maps an editor column to an offset in the block's model text (they differ only in whitespace).</summary>
    private int ModelOffset(int lineNumber, int column)
    {
        int bi = lineNumber - 1;
        if (bi < 0 || bi >= Chapter.Blocks.Count || lineNumber > Prose.Document.LineCount) return 0;
        var model = Chapter.Blocks[bi].Text;
        var editor = Prose.Document.GetText(Prose.Document.GetLineByNumber(lineNumber));
        if (editor == model) return Math.Clamp(column, 0, model.Length);
        int i = 0, j = 0;
        while (i < column && i < editor.Length && j < model.Length)
        {
            if (editor[i] == model[j]) { i++; j++; }
            else if (editor[i] == ' ') i++;
            else if (model[j] == ' ') j++;
            else { i++; j++; }
        }
        return Math.Clamp(j, 0, model.Length);
    }

    // ================================================================== text mode: commands

    private void OnProseTextEntering(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Contains('\n') || e.Text.Contains('\r'))
        {
            e.Handled = true;
            Split();
            return;
        }
        if (IsMultiLineSelection(Prose)) DeleteSelection(); // then the typed text goes in at the caret
    }

    private void OnProsePreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var caret = Prose.TextArea.Caret;
        bool noSelection = Prose.TextArea.Selection.IsEmpty;
        int lineLength = Prose.Document.GetLineByNumber(caret.Line).Length;
        bool atStart = noSelection && caret.Column == 1;
        bool atEnd = noSelection && caret.Column - 1 >= lineLength;

        if (e.Command == EditingCommands.EnterParagraphBreak || e.Command == EditingCommands.EnterLineBreak)
            Handle(e, Split);
        else if ((e.Command == EditingCommands.Backspace || e.Command == EditingCommands.DeletePreviousWord) && atStart && caret.Line > 1)
            Handle(e, () => Merge(caret.Line - 1));
        else if ((e.Command == EditingCommands.Delete || e.Command == EditingCommands.DeleteNextWord) && atEnd && caret.Line < Prose.Document.LineCount)
            Handle(e, () => Merge(caret.Line));
        else if ((e.Command == EditingCommands.Backspace || e.Command == EditingCommands.Delete) && IsMultiLineSelection(Prose))
            Handle(e, DeleteSelection);
        else if (e.Command == ApplicationCommands.Cut && IsMultiLineSelection(Prose))
            Handle(e, () =>
            {
                Clipboard.SetText(Prose.SelectedText);
                DeleteSelection();
            });
        else if (e.Command == ApplicationCommands.Paste)
            Handle(e, () => Paste(Clipboard.ContainsText() ? Clipboard.GetText() : ""));
        else if (e.Command == ApplicationCommands.Undo)
            Handle(e, Undo);
        else if (e.Command == ApplicationCommands.Redo)
            Handle(e, Redo);
    }

    private static void Handle(ExecutedRoutedEventArgs e, Action action)
    {
        e.Handled = true;
        action();
    }

    private static bool IsMultiLineSelection(TextEditor ed) =>
        !ed.TextArea.Selection.IsEmpty && ed.TextArea.Selection.StartPosition.Line != ed.TextArea.Selection.EndPosition.Line;

    /// <summary>Enter: splits the block at the caret.</summary>
    private void Split()
    {
        if (!Prose.TextArea.Selection.IsEmpty)
        {
            if (IsMultiLineSelection(Prose)) DeleteSelection();
            else Prose.TextArea.Selection.ReplaceSelectionWithText("");
        }
        var loc = CurrentLocation;
        Record(null);
        if (!_editor.SplitBlock(loc.Block, loc.Offset))
        {
            SystemSounds.Beep.Play();
            return;
        }
        RebuildProse(new TextLocation(Chapter.Index, loc.Block + 1, 0));
        Edited?.Invoke(this, EditKind.Structure, loc.Block + 1);
    }

    /// <summary>Joins block <paramref name="block"/> (0-based) into the previous one.</summary>
    private void Merge(int block)
    {
        if (!_editor.CanMergeWithPrevious(block))
        {
            SystemSounds.Beep.Play();
            return;
        }
        int prevLength = Chapter.Blocks[block - 1].Text.Length;
        Record(null);
        if (!_editor.MergeWithPrevious(block)) return;
        RebuildProse(new TextLocation(Chapter.Index, block - 1, prevLength));
        Edited?.Invoke(this, EditKind.Structure, block - 1);
    }

    private void DeleteSelection()
    {
        var sel = Prose.TextArea.Selection;
        if (sel.IsEmpty) return;
        var a = sel.StartPosition.Location;
        var b = sel.EndPosition.Location;
        if (a > b) (a, b) = (b, a);
        int sa = ModelOffset(a.Line, a.Column - 1), sb = ModelOffset(b.Line, b.Column - 1);
        Record(null);
        if (!_editor.DeleteRange(a.Line - 1, sa, b.Line - 1, sb))
        {
            SystemSounds.Beep.Play();
            RebuildProse(CurrentLocation);
            return;
        }
        RebuildProse(new TextLocation(Chapter.Index, a.Line - 1, sa));
        Edited?.Invoke(this, EditKind.Structure, a.Line - 1);
    }

    /// <summary>Pastes plain text; each line of the clipboard becomes a paragraph. One undo step.</summary>
    private void Paste(string text)
    {
        if (text.Length == 0) return;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        BeginBatch();
        try
        {
            if (IsMultiLineSelection(Prose)) DeleteSelection();
            Prose.TextArea.Selection.ReplaceSelectionWithText(lines[0]);
            for (int i = 1; i < lines.Length; i++)
            {
                Split();
                Prose.Document.Insert(Prose.CaretOffset, lines[i]);
            }
        }
        finally
        {
            EndBatch();
        }
    }

    /// <summary>Ctrl+B / Ctrl+I on the selection (within one paragraph).</summary>
    /// <summary>Types text at the caret (replacing the selection), in whichever mode is active. False if not possible here.</summary>
    public bool InsertText(string text)
    {
        var ed = ActiveEditor;
        if (!IsCodeMode)
        {
            if (IsMultiLineSelection(Prose)) DeleteSelection();
            if (!Prose.TextArea.ReadOnlySectionProvider.CanInsert(Prose.CaretOffset)) return false;
        }
        if (ed.TextArea.Selection.IsEmpty) ed.Document.Insert(ed.CaretOffset, text);
        else ed.TextArea.Selection.ReplaceSelectionWithText(text);
        ed.Focus();
        return true;
    }

    public void ToggleFormat(SpanKind kind)
    {
        if (IsCodeMode) return;
        var sel = Prose.TextArea.Selection;
        if (sel.IsEmpty || IsMultiLineSelection(Prose))
        {
            SystemSounds.Beep.Play();
            return;
        }
        var a = sel.StartPosition.Location;
        var b = sel.EndPosition.Location;
        if (a > b) (a, b) = (b, a);
        SyncLineToModel(a.Line - 1);
        int s = ModelOffset(a.Line, a.Column - 1), e = ModelOffset(b.Line, b.Column - 1);
        Record(null);
        if (!_editor.ToggleFormat(a.Line - 1, s, e, kind))
        {
            SystemSounds.Beep.Play();
            return;
        }
        ResyncLine(a.Line - 1);
        Edited?.Invoke(this, EditKind.Text, a.Line - 1);
    }

    // ================================================================== code mode

    private void OnCodeChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_suppress) return;
        if (!_codePending)
        {
            Record("code");
            _codePending = true;
        }
        _codeTimer.Stop();
        _codeTimer.Start();
    }

    /// <summary>Pushes code-mode text into the chapter (debounced while typing).</summary>
    public void FlushCode()
    {
        _codeTimer.Stop();
        if (!_codePending || _code == null) return;
        _codePending = false;
        _editor.SetSource(_code.Text);
        UpdateErrorBar();
        Edited?.Invoke(this, EditKind.Source, -1);
    }

    private void SetCodeText(string text)
    {
        _suppress = true;
        try
        {
            int caret = _code!.CaretOffset;
            _code.Document.Text = text;
            _code.CaretOffset = Math.Min(caret, text.Length);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void OnCodePreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Undo) Handle(e, Undo);
        else if (e.Command == ApplicationCommands.Redo) Handle(e, Redo);
    }

    private void UpdateErrorBar()
    {
        if (Chapter.ParseError is { } err)
        {
            _errorText.Text = T.F("Editor_XmlError", Chapter.ParseErrorLine, err);
            _errorBar.Visibility = Visibility.Visible;
        }
        else
        {
            _errorBar.Visibility = Visibility.Collapsed;
        }
    }

    private void GoToParseError()
    {
        if (Chapter.ParseError == null) return;
        if (!IsCodeMode)
        {
            IsCodeMode = true;
            Prose.Visibility = Visibility.Collapsed;
            Code.Visibility = Visibility.Visible;
        }
        int line = Math.Clamp(Chapter.ParseErrorLine, 1, _code!.Document.LineCount);
        _code.TextArea.Caret.Offset = _code.Document.GetLineByNumber(line).Offset;
        CenterCaret(_code);
        _code.Focus();
        RaiseCaretMoved();
    }

    // ================================================================== history

    private EditSnapshot Snapshot() =>
        new(Chapter.SourceText, IsCodeMode ? CurrentLocation : new TextLocation(Chapter.Index, Prose.TextArea.Caret.Line - 1, Prose.TextArea.Caret.Column - 1),
            _code?.CaretOffset ?? 0, IsCodeMode);

    private void Record(string? key)
    {
        if (_inBatch)
        {
            if (_batchRecorded) return;
            _batchRecorded = true;
            History.Record(Snapshot(), null);
            return;
        }
        History.Record(Snapshot(), key);
    }

    private void BeginBatch()
    {
        _inBatch = true;
        _batchRecorded = false;
    }

    private void EndBatch() => _inBatch = false;

    public void Undo() => Restore(History.Undo);

    public void Redo() => Restore(History.Redo);

    private void Restore(Func<EditSnapshot, EditSnapshot?> step)
    {
        FlushCode();
        if (!IsCodeMode) SyncLineToModel(Prose.TextArea.Caret.Line - 1);
        if (step(Snapshot()) is not { } snap)
        {
            SystemSounds.Beep.Play();
            return;
        }
        _editor.SetSource(snap.Source);
        UpdateErrorBar();
        if (IsCodeMode || Chapter.ParseError != null)
        {
            if (!IsCodeMode)
            {
                IsCodeMode = true;
                Prose.Visibility = Visibility.Collapsed;
                Code.Visibility = Visibility.Visible;
            }
            SetCodeText(snap.Source);
            _code!.CaretOffset = Math.Min(snap.CodeOffset, _code.Document.TextLength);
            _code.TextArea.Caret.BringCaretToView();
        }
        else
        {
            RebuildProse(snap.Caret);
        }
        Edited?.Invoke(this, EditKind.Source, -1);
        RaiseCaretMoved();
    }

    // ================================================================== hover info (ruby reading, image, link)

    private void OnProseMouseHover(object sender, MouseEventArgs e)
    {
        var pos = Prose.GetPositionFromPoint(e.GetPosition(Prose));
        if (pos == null) return;
        int bi = pos.Value.Line - 1;
        if (bi < 0 || bi >= Chapter.Blocks.Count) return;
        int col = pos.Value.Column - 1;
        var span = Chapter.Blocks[bi].Spans.LastOrDefault(s => col >= s.Start && col < s.End && s.Kind is SpanKind.Ruby or SpanKind.Image or SpanKind.Link or SpanKind.NoteRef);
        if (span == default || span.Data == null) return;

        object content = span.Kind switch
        {
            SpanKind.Ruby => T.F("Hover_Ruby", span.Data),
            SpanKind.Image => (object?)ImagePreview(span.Data) ?? span.Data,
            SpanKind.NoteRef => NoteText?.Invoke(span.Data) is { } note ? T.F("Hover_Note", note) : T.F("Hover_NoteRef", span.Data),
            _ => T.F("Hover_Link", span.Data),
        };
        _hoverTip.Content = content;
        _hoverTip.PlacementTarget = Prose;
        _hoverTip.IsOpen = true;
        e.Handled = true;
    }

    private FrameworkElement? ImagePreview(string path)
    {
        var data = _book.Package.Read(path);
        if (data == null) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 240;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            var panel = new StackPanel();
            panel.Children.Add(new Image { Source = bmp, MaxHeight = 240, MaxWidth = 320 });
            panel.Children.Add(new TextBlock { Text = path, Margin = new Thickness(0, 4, 0, 0) });
            return panel;
        }
        catch (Exception)
        {
            return null; // unsupported format (e.g. SVG)
        }
    }
}

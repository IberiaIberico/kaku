# Kaku — EPUB Editor

**Status:** Draft v0.7 (under discussion)
**Platform:** Windows 11, .NET 10 (SDK 10.0.103 installed), single EXE
**Purpose:** A fast EPUB editor that feels like an advanced text editor (VS Code / Notepad++ /
Sublime style). Kaku works directly on the **final EPUB**: you open it, move through it quickly, fix
text, manage chapters and images, add annotations, and save. It does not depend on the translation
pipeline. Comparing with the original Japanese book is an optional extra.

Items marked **[OPEN]** need a decision. Items marked **[PROPOSED]** are my default unless you say otherwise.

---

## 1. Typical use cases

1. **Fix what I saw on the e-reader.** While reading on an external device I notice a typo or bad
   sentence → in Kaku I search a few words of it (or jump to chapter / % position), land on it
   instantly, fix it, save.
2. **Review a chapter.** Read a chapter in a comfortable view, edit inline, add notes.
3. **Restructure the book.** Delete pages (ads, publisher pages, empty pages), add pages
   (translator's note, glossary of characters), reorder chapters, fix the TOC.
4. **Manage images.** See all images, replace/delete/add them, change the cover.
5. **Annotate.** Attach notes with extra information to words or passages (see §4.6).
6. **Check against the original** (optional). Open the Japanese EPUB alongside and see the
   matching paragraph.

---

## 2. Goals and non-goals

### Goals
- Direct editing of any EPUB 2/3 file, saving a valid EPUB.
- Text-editor speed and ergonomics: tabs, keyboard shortcuts, instant search, go-to, bookmarks.
- Book-level operations: pages, images, TOC, metadata.
- Annotations.

### Non-goals (v1) [PROPOSED]
- No DRM handling.
- No visual page-layout designer (CSS is edited as text, not with a designer).
- No dependency on `translate_epub.py`, caches or glossaries.
- Only one book open at a time.
- Japanese reference and LLM assistance deferred to later stages.

---

## 3. Main window layout [PROPOSED]

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Menu / toolbar                                       [ Command palette Ctrl+P ] │
├─────────────────────────┬────────────────────────────────────┬─────────────────┤
│[Capítulos]|Archivos|Marc│ Tabs: [Cap. 3 ×] [Cap. 4 ×] [css ×] │ Preview         │
│ filter… ________________│ ┌────────────────────────────────┐ │ (rendered page, │
│ ▣ Portada        (img)  │ │ editor                         │ │  synced scroll) │
│   Cap. 1  0,0% · 3.120 w│ │                                │ │                 │
│   «Aquella mañana…»  ●2 │ │                                │ │   ─ or ─        │
│   Cap. 2  3,2% · 4.870 w│ │                                │ │ Japanese        │
│ ▶ Cap. 3 45,1% · 6.010 w│ │                                │ │ original        │
│   ▕████░░░░░░▏ ✎ ●5 ⚠1  │ │                                │ │ (later stage)   │
│     ├ 3.1 El andén      │ └────────────────────────────────┘ │                 │
│     └ 3.2 La carta      ├────────────────────────────────────┤                 │
│   Cap. 4 51,3% · 5.400 w│ Bottom: Search | Notes | Problems  │                 │
├─────────────────────────┴────────────────────────────────────┴─────────────────┤
│ Status: Cap. 3 · párr. 112/340 · palabra 45.210/98.730 · 45,8 % · modificado     │
└──────────────────────────────────────────────────────────────────────────────┘
  ●n = notes   ⚠n = problems   ✎ = unsaved changes   ▕██░░▏ = cursor position in chapter
```

- Left: **Chapter browser** (default tab, see F16), **Book explorer** (all files grouped by type), bookmarks.
- Center: tabbed editors.
- Right: live preview (the Japanese original as an alternative view comes in a later stage).
- Bottom: search results, annotations list, problems.
- All panels dockable / hideable; layout remembered.

---

## 4. Functional requirements

### 4.1 Opening and saving
- F1. Open `.epub` (drag & drop, recent files, command line `kaku.exe book.epub`).
- F2. Save / Save as. Writes a valid EPUB: `mimetype` first and uncompressed, OPF manifest/spine
  updated automatically.
- F3. Autosave of a working copy + crash recovery. The original is untouched until you save.
- F4. Automatic backup of the previous version when saving (`book.epub.bak` or a backups folder) [PROPOSED].
- F5. **One book at a time** per window **[DECIDED]**. Opening another book closes the current one
  (asking to save). The Japanese original as a read-only reference comes in a later stage (§4.8).

### 4.2 Text editing — editing modes **[DECIDED: both modes]**
Each chapter is an XHTML file and can be edited in two modes, switchable per tab with **Ctrl+E**.
The cursor/selection stays on the same text when switching.

**Text mode** (default for chapters [PROPOSED])
- M1. Clean prose: one block (`p`, `h1–h6`, `li`, `blockquote`…) per paragraph, tags hidden.
- M2. Inline markup shown as formatting, not tags: bold, italics, small caps, links, ruby
  (furigana above the base text), spans with a class (subtle underline + class name on hover).
- M3. Non-text elements shown as inline placeholders/chips: images (thumbnail), line breaks,
  note references, page breaks, tables (opens in code mode on double-click).
- M4. Editing is markup-safe: typing, deleting, pasting and Enter (split paragraph) / Backspace at
  start (merge paragraphs) produce valid XHTML; attributes and classes of the block are preserved.
- M5. Formatting shortcuts: Ctrl+B / Ctrl+I, clear formatting, change block type (paragraph ↔ heading),
  apply a CSS class from the book's stylesheets.
- M6. Paste from outside is pasted as plain text by default (Ctrl+Shift+V keeps formatting) [PROPOSED].

**Code mode** (default for CSS, OPF, NCX, nav; available for chapters)
- M7. Raw XHTML/CSS/XML with syntax highlighting, tag auto-closing, matching tag highlight, folding.
- M8. Live well-formedness check: an XML error is underlined and listed in Problems; switching back
  to text mode is blocked until the file is well-formed (with a "jump to error" button).
- M9. Pretty-print / reformat file command.

**Shared**
- M10. One undo history per file, covering edits in both modes.
- M11. Search, bookmarks, notes and problems work in both modes and map to the same positions.

Editor features (both modes):
- F6. Undo/redo (unlimited, per file), multi-cursor, column selection, line/paragraph move, duplicate.
- F7. Spell checking in Spanish (Hunspell `es_ES` dictionary), with "add to dictionary".
- F8. Typographic helpers: insert « » — … ¿ ¡, convert straight quotes, fix double spaces.
- F9. Font, size, line spacing, light/dark theme configurable for comfortable reading.
- F10. Word wrap, minimap/scroll overview with markers (search hits, notes, problems, edits).

### 4.3 Navigation (must be fast)
- F11. **Command palette** (Ctrl+P): type a chapter name, a heading, a bookmark, a file → jump.
- F12. **Find a passage from the e-reader** (main way to locate text, Ctrl+Shift+P [PROPOSED]):
  paste or type a few words → fuzzy search across the whole book → jump to the best match.
  - Tolerant to accents, case, quote styles (« » “ ” "), dashes, ellipsis, extra spaces and line breaks,
    and to a word or two that differ (text copied from the device or typed from memory).
  - Searches visible text only (tags, ruby readings and attributes ignored); a passage may span
    inline markup or two paragraphs.
  - Results ranked by similarity; each one shows chapter, % and word position, plus context, so you
    can pick the right one when the phrase appears several times. Enter jumps to the best match.
- F13. **Position information** shown everywhere a location appears (status bar, search results,
  notes panel, problems, bookmarks):
  - **% of the book** (one decimal) and **word position** `palabra 45.210 / 98.730`;
  - chapter and paragraph number within the chapter.
  - Counting rule: words of the visible text in spine (reading) order, all spine documents included,
    note texts excluded [PROPOSED]. Word = run of letters/digits separated by spaces or punctuation.
  - Positions update live while editing.
  - **Go to** (Ctrl+G) accepts any of them: `45%`, `45.8%`, `w45210` (word), chapter name, or chapter + paragraph.
  - Note: the % shown by the e-reader is computed by the device and may differ slightly from
    Kaku's; Kaku's go-to-% is meant to land you close, then the pasted-words search pinpoints.
- F14. Chapter outline (headings), bookmarks (Ctrl+F2 toggle, F2 next), back/forward history (Alt+←/→).
- F15. Preview ↔ editor sync: click in the preview to jump to that paragraph in the editor, and the preview follows the cursor.
- F16. **Chapter browser** (side panel, always available; replaces a continuous-scroll mode — books
  are edited chapter by chapter) **[DECIDED]**:
  - One row per spine document in reading order, showing: title (from TOC/first heading, or file
    name), first words of the text, start position (% and word), length in words, and badges for
    notes, problems, bookmarks and unsaved changes.
  - The current chapter is highlighted and a position bar inside its row shows where the cursor is.
  - Expand a chapter to see its headings and its notes/bookmarks → click to jump there.
  - Single click opens the chapter in the current tab (preview tab, like VS Code), double click
    pins it in its own tab.
  - Keyboard: Ctrl+PgDn / Ctrl+PgUp = next / previous chapter; type-to-filter by title or text.
  - Hover shows a small rendered preview of the chapter start.
  - Right-click: rename, duplicate, delete, split, merge with next, add page before/after;
    drag & drop to reorder (same operations as §4.5).
  - Non-text pages (cover, image-only pages) shown with a thumbnail.

### 4.4 Search and replace
- F17. Find in current file (Ctrl+F) and **in the whole book** (Ctrl+Shift+F), results listed with context, grouped by chapter.
- F18. Options: case, whole word, accent-insensitive, regex; search in text only or in code (tags/attributes).
- F19. Replace one / replace all, with a preview of every change before applying; fully undoable.
- F20. Saved searches (e.g. "Japanese characters left", "straight quotes").

### 4.5 Book structure
- F21. Add page: blank, from template (translator's note, character list, …), or import an XHTML/HTML/TXT/Markdown file.
- F22. Delete page(s), duplicate, rename.
- F23. Reorder pages by drag & drop (updates spine).
- F24. Split a chapter at the cursor / merge chapters.
- F25. TOC editor: generate from headings, edit titles/levels, reorder; updates `nav.xhtml` and `toc.ncx`.
- F26. Metadata editor: title, author, language, series, description, cover.
- F27. Images: gallery view, preview, replace, add, delete (warns if referenced), insert into text, set as cover, see where each one is used.
- F28. CSS and fonts: edit stylesheets as text, add/remove fonts.
- F29. Clean-up tools: find unused files, broken links, missing images; fix them.

### 4.6 Annotations: notes inside the book **[DECIDED]**
Annotations are **real notes inside the EPUB**, which the reader sees on the e-reader: a small
marker in the text that opens the note (a popup on readers that support it, otherwise a link that
jumps to the note and back). Typical use: explaining Japanese terms, honorifics, food, places,
cultural references, wordplay. There are no private notes stored outside the book.

**Markup [PROPOSED]**: standard EPUB 3 notes, the format most widely supported for popups:
```html
<!-- in the text -->
<a epub:type="noteref" href="notas.xhtml#n12" id="ref12" class="noteref">12</a>
<!-- in the notes location -->
<aside epub:type="footnote" id="n12" class="nota">
  <p><a href="cap03.xhtml#ref12">12.</a> <i>Senpai</i>: compañero de cursos superiores…</p>
</aside>
```
Kaku adds the `epub` namespace and a small default CSS for markers and notes if the book lacks them.

Requirements:
- F30. **Insert note** (shortcut, e.g. Ctrl+Alt+N) at the cursor or after the selected word; a small
  editor opens to type the note (bold, italics, links allowed).
- F31. In text mode the marker is a chip; hovering it shows the note, clicking edits it.
- F32. **Automatic numbering**: inserting, deleting or moving a note renumbers all markers and notes
  of the book (or per chapter, see F34). Marker style configurable: `1, 2, 3`, `*`, `[1]`.
- F33. **Back links** from each note to its marker are created and kept correct automatically.
- F34. **Placement** configurable per book (default for new notes: **b**, a "Notas" page at the end [PROPOSED];
  books that already have notes keep their existing scheme):
  - a. end of each chapter (footnotes), numbering restarts per chapter;
  - b. a single "Notas" page at the end of the book (endnotes), continuous numbering.
  A command converts the whole book between a and b.
- F35. **Notes panel**: list of all notes (number, chapter, marked word, text), search/filter, jump
  to marker, edit, delete, reorder.
- F36. **Detect existing notes** when opening a book (EPUB 3 `noteref`, or common patterns such as
  superscript links to a notes page) and manage them the same way; offer to convert old-style notes
  to the standard markup.
- F37. Notes are found by the book-wide search and checked by the Problems panel (marker without
  note, note without marker, broken link).
- F38. **Reusable notes** [PROPOSED]: when the same term appears again (e.g. *senpai*), offer to
  link the new marker to the existing note instead of writing it twice.

> Compatibility note: popup behaviour depends on the device and format (e.g. Kobo renders
> `.kepub.epub` and `.epub` with different engines). M5 includes testing the generated markup on
> your actual device.

### 4.7 Checks (Problems panel)
- F39. EPUB validity (structure, manifest, broken links, missing files). Optionally run `epubcheck` if Java is installed.
- F40. Text checks: Japanese characters left, mixed quote styles, double spaces, empty paragraphs, spelling errors.
- F41. Each problem clickable → jumps to the position.

### 4.8 Original-language reference **[IN PROGRESS — branch `feature/referencia-japonesa`]**

**Decision: the reference is the original EPUB, not the translation cache JSON.** The cache keys are
SHA-1 hashes of the Japanese paragraphs, so the Japanese text cannot be recovered from the JSON; the
original EPUB is needed in any case. Measured on the test books, translations made with `translate_epub.py`
keep the original file paths and the paragraph order: Grasshopper 14/14 and Materiales Inflamables 22/22
files match paragraph for paragraph; Unsocial Distance 15/18 (the 3 others were edited in Kaku: split
paragraphs, notes page). The cache can be added later as an optional extra (F42c).

- F42. **Link a reference EPUB** to the book: Libro → «Original japonés…» → choose the file.
  - F42a. Kaku suggests candidates [PROPOSED]: EPUBs in the folders of recently linked books and in a
    configurable folder (e.g. `kobo_downloads`), ranked by how well their structure matches (same file paths,
    similar paragraph counts).
  - F42b. The link is stored per book outside the EPUB [PROPOSED]: `%APPDATA%\Kaku\references.json`
    (book path → reference path), so the translated EPUB stays clean. Follows «Guardar como».
  - F42c. *Later:* optional translation cache (`*.cache.json`) to show the machine draft next to the current
    Spanish text (lookup by SHA-1 of the Japanese paragraph).
- F43. **Show the Japanese paragraph** that matches the paragraph under the cursor.
  - F43a. **Alignment**, per file (same path in both books, else by spine position): sequence alignment
    of the two paragraph lists (like a diff), tolerant to inserted, deleted, split and merged paragraphs.
    Cues: paragraph length ratio, empty paragraphs and images at the same place, digits, quotes/dialogue
    marks, headings. Notes and pages added by Kaku have no counterpart.
  - F43b. **Display** **[OPEN]**: a tab «Original» next to «Vista previa» in the right panel, showing the
    Japanese chapter with furigana and the matching paragraph highlighted and scrolled into view; or a
    paragraph-by-paragraph view (Japanese above/next to each Spanish paragraph).
  - F43c. **Sync** both ways: the cursor in the editor moves the highlight in the original; clicking a
    Japanese paragraph moves the editor to its Spanish counterpart.
  - F43d. **Manual re-alignment**: «Este párrafo corresponde a…» to pin a pair when the automatic alignment
    is wrong; pins are stored with the link (F42b) and re-anchor the rest of the chapter.
  - F43e. Read-only: the Japanese book is never modified.
  - F43f. The Japanese is shown in its own layout (vertical writing) or forced horizontal **[OPEN]**.
- F44. Re-translate / suggest a translation for a paragraph with an LLM (LM Studio). **[LATER STAGE — not in v1]**

---

## 5. Non-functional requirements
- N1. Single self-contained EXE (`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`).
- N2. Speed: open a full novel (~3,000 paragraphs, ~40 files) in < 2 s; whole-book search in < 200 ms; no lag while typing.
- N3. Never corrupt a book: atomic writes, backup on save, crash recovery.
- N4. Fully keyboard-driven; all shortcuts configurable.
- N5. **UI language: Spanish** **[DECIDED]** (menus, dialogs, messages, number format `45.210`, `45,8 %`).
  All strings kept in `.resx` resource files so another language could be added later; code and
  comments in English.

---

## 6. Technology [PROPOSED]
- **WPF** (.NET 10) for the window, docking, explorer, panels.
- **AvalonEdit** for the editor: the mature text-editor control behind SharpDevelop/ILSpy — fast with
  large files, syntax highlighting, folding, custom rendering (for highlights, notes, hidden tags).
- **WebView2** (Edge engine, built into Windows 11) for the preview: renders XHTML/CSS/ruby/vertical
  text like a real reader.
- **AvalonDock** for dockable panels/tabs.
- **WeCantSpell.Hunspell** for spell checking.
- EPUB handling in our own code: `System.IO.Compression` + `XDocument` (no heavy library needed).

---

## 7. Proposed structure
```
kaku/
  SPEC.md
  src/
    Kaku.App/      WPF app: windows, panels, editor integration (MVVM, CommunityToolkit.Mvvm)
    Kaku.Core/     EPUB model (container, OPF, spine, nav/NCX), read/write, search, checks, annotations
  tests/
    Kaku.Core.Tests/   xUnit: round-trip every book in kobo_downloads/ and out/ (open → save → identical/valid)
```

---

## 8. Milestones [PROPOSED]
1. **M1 – Open, read, navigate**: open EPUB, explorer, editor tabs (read-only), preview, go-to, command palette. **[DONE 2026-10-15]**
   - Build: `dotnet publish src/Kaku.App -c Release -o publish` → `publish/Kaku.exe` (~70 MB, self-contained, runs without .NET installed).
   - Tests: `dotnet test` — round-trips every EPUB in `kobo_downloads/` and `out/` byte-for-byte; books open in 20–120 ms.
   - Deviations from §6: panels are a fixed 3-column layout with splitters (no AvalonDock yet); the chapter hover
     shows the opening text, not a rendered thumbnail. Chapters missing from the TOC inherit the previous title
     with a number ("Grasshopper (2)").
   - Chapter browser is a tree: continuation files and nested TOC entries fold under their main chapter
     (arrow or ←/→); the group of the current chapter opens automatically; filtering shows a flat list.
2. **M2 – Edit & save**: text editing, undo, save valid EPUB, backup, autosave/recovery. **[DONE 2026-10-15]**
   - Text mode: typing, Enter = split paragraph, Backspace/Delete at the edges = merge, multi-paragraph
     selection delete, plain-text paste (each line → paragraph), Ctrl+B / Ctrl+I. Images and line breaks
     are protected chips. Edits change only the touched elements; the rest of the file stays byte-identical.
   - Code mode: editable XHTML; broken XML shows a red bar ("Ir al error") and blocks going back to text mode.
   - One undo history per chapter covering both modes (Ctrl+Z / Ctrl+Y); typing in one paragraph is one step.
   - Save (Ctrl+S) / Save as (Ctrl+Mayús+S): atomic and verified; previous file copied first to
     `%LOCALAPPDATA%\Kaku\Backups\<libro>\` (last 10 kept; Archivo → Abrir carpeta de copias de seguridad).
     Warns if the file changed on disk or a chapter has invalid XHTML.
   - Crash recovery: unsaved edits written every 20 s to `%LOCALAPPDATA%\Kaku\Recovery`; offered on next start.
   - Live preview updates the edited paragraph in place while typing (full reload after split/merge).
   - CSS and other text files are editable in code mode; OPF/NCX/nav stay read-only until M4.
   - Shortcut change: Ctrl+B is now bold; the side panel toggle moved to **Ctrl+Alt+B**.
   - Not yet: clear formatting, change block type, apply CSS class (§4.2 M5), pretty-print (M9),
     Shift+Enter as `<br/>` (it splits like Enter). XML error messages come from .NET in English.
   - Cost: ~21 ms per keystroke in the largest test chapter (248 KB, 1,093 paragraphs).
3. **M3 – Search**: book-wide search/replace, fuzzy "find passage", bookmarks, history. **[DONE 2026-10-15]**
   - Search panel at the bottom (Ctrl+F chapter, Ctrl+Mayús+F book, Ctrl+H replace, F3/Mayús+F3, Ctrl+J show/hide):
     case, whole word, accent-insensitive, regex; scope Texto (visible text, markup-safe) or Código (XHTML + CSS).
     Results grouped by chapter with context, paragraph and %; matches highlighted in the editor.
   - Replace: preview of every change (struck match + replacement, regex groups expanded), per-result checkbox,
     replace one / replace checked, one-click "Deshacer reemplazo" plus Ctrl+Z per chapter. Code replacements
     that would break the XHTML are skipped and reported. 451 replacements in a novel take ~0.1 s.
   - Saved searches (★): built-in checks (Japanese left, quote styles, hyphen dialogue, "...", space before
     punctuation, repeated word, model tokens) plus your own.
   - Find passage (Ctrl+Mayús+P): paste text from the e-reader (clipboard pre-filled); fuzzy word alignment
     across paragraphs and markup, typo-tolerant; tested 150/150 garbled snippets found on the 6 Spanish books.
   - Bookmarks (Ctrl+F2 toggle, F2/Mayús+F2 next/prev, "Marcadores" tab, ⚑ badge in the chapter browser),
     stored in `%APPDATA%\Kakuookmarks.json`, follow split/merge and "Guardar como".
   - Back/forward (Alt+← / Alt+→, mouse back/forward buttons) over jumps: go-to, palette, search, passage,
     bookmarks, chapter changes.
   - All shortcuts also work while the preview has focus.
4. **M4 – Structure**: add/delete/reorder pages, images, TOC, metadata. **[DONE 2026-10-15]**
   - Libro menu + chapter-browser context menu: add page (blank / translator's note / characters template,
     styled like its neighbour, optional TOC entry), import TXT/Markdown/HTML as a page, duplicate, rename
     (all links, manifest and TOC follow), delete (warns about links that will break), move up/down
     (Alt+↑/↓) and drag & drop — a chapter always moves together with its parts —, split at the cursor
     (ids and links follow the moved text), merge with the next chapter (clashing ids renamed, links redirected).
   - Image gallery (tab): thumbnails, size, where used; add, replace (a different format renames the file and
     its links), delete, set as cover, insert into the text as a new paragraph after the cursor (Ctrl+Alt+I).
   - Archivos tab context menu: rename, replace, delete, where used, add files (images, fonts, CSS).
   - TOC editor (Ctrl+Alt+T): edit titles, move, indent; generate from chapters and headings (adds ids
     where needed); sort by reading order; writes nav.xhtml and/or toc.ncx (creates an NCX if neither exists).
   - Metadata: title, authors (refines kept), language, publisher, series (EPUB 3 collection + calibre), description, cover.
   - Book check: broken links and fragments, missing files, files outside the manifest, unused files; one-click fixes.
   - Structural undo/redo: Libro menu, or Ctrl+Z / Ctrl+Y while the chapter browser, Archivos tree or gallery
     has focus (Ctrl+Alt+Z is taken by the NVIDIA overlay on the user's PC).
   - "Modified" now also covers structure; crash recovery keeps a full copy of the unsaved book.
   - Tested on all 14 real books: split + merge + rename leave no new broken links and the word count unchanged.
5. **M5 – Annotations**. **[DONE 2026-10-15]**
   - Insert note (Ctrl+Alt+N, Editar menu): after the word at the cursor or the selection; note text with
     **negrita** / *cursiva*. Markers are EPUB 3 noterefs (`a.kaku-noteref`), notes are `aside epub:type="footnote"`
     with id `kaku-nota-N` and a back-link to the marker; every change renumbers the whole book.
   - Placement (F34): default a «Notas» page after the main text (created and removed automatically, added to
     the índice), or at the end of each chapter (numbering restarts per chapter). Marker style 1 / [1] / *.
     Stored in the OPF as `<meta name="kaku:notes-…">`. A small stylesheet `kaku-notas.css` is added.
   - Notes panel (Ctrl+Mayús+N, bottom tab next to the search): number, chapter, word, text; filter,
     double-click to the marker, edit, delete, go to the note text, renumber, placement and style.
   - In text mode a marker is a protected chip; hovering shows the note, double-clicking edits it.
   - Reuse (F38): the note dialog offers existing notes, suggesting the one that already mentions the word.
   - Existing notes in other formats (EPUB 3 noterefs, superscript links) are detected and can be converted.
   - Book check reports markers without a note and notes without a marker.
   - Note numbers are not counted as words. All note operations go through the structural undo.
   - None of the 14 test books has publisher notes, so conversion is tested on synthetic books only.
6. **M6 – Checks & spelling**: Problems panel, EPUB validation, Spanish spell check. **[DONE 2026-10-15]** — version 1 complete.
   - Problemas panel (Ctrl+Mayús+M, bottom tab): chapter or whole book, filter by type with counts, severity
     icons, double-click jumps and selects, re-checks automatically after edits (runs in the background).
   - Text checks: Japanese left untranslated, model tokens, repeated word, ¿?/¡! without pair, quotes that differ
     from the book's dominant style (curly/straight/Japanese), hyphen instead of dialogue dash, "...", space
     before punctuation, missing space after punctuation, ideographic/double spaces (incl. Japanese indentation
     left before images), 3+ empty paragraphs. Most have an automatic fix: one, selected, or all of a type
     (one undo step per chapter). Double spaces between words are not checked: XHTML collapses them, the reader never sees them.
   - Spelling: Hunspell es_ES (LibreOffice dictionary, GPL/LGPL/MPL, bundled with its license). Red wavy
     underline while typing (Editar → Revisar ortografía al escribir), right-click for suggestions / add to the
     personal dictionary (%APPDATA%\Kaku\diccionario-personal.txt) / ignore. Skips: names the book uses in
     mid-sentence, romanized Japanese with macrons (Kōtō), acronyms, numbers, and a built-in list of Japanese
     terms and loanwords translations keep (kun, sensei, sake, smartphone…). On the test books spelling flags drop
     from ~750 to ~170–460 per book, mostly genuine (leftover particles "na", "wa", English "the", rare words).
   - EPUB validation (whole-book scope): the Revisar checks plus required metadata, nav, spine, duplicate ids,
     media types, risky file names, images without alt, and a warning if dc:language is still "ja".
   - epubcheck (Libro → Validar con epubcheck…): optional; needs Java (not installed on the user's PC) and
     epubcheck.jar, chosen once. Runs on a copy of the current state and adds its messages to the panel.

**Later stages (after v1):** Japanese original as reference (§4.8, in progress on `feature/referencia-japonesa`), LLM assistance (F44).

---

## 9. Decisions log
| # | Topic | Decision |
|---|---|---|
| 1 | Editing mode | Both: text mode + code mode, Ctrl+E to switch (§4.2) |
| 2 | Annotations | Notes inside the book (EPUB 3 footnotes / popups), no private notes (§4.6) |
| 3 | Locating text from the e-reader | Paste-words fuzzy search + % and word position everywhere (F12, F13) |
| 4 | Reading flow | Chapter by chapter, with chapter browser side panel (F16) |
| 5 | Books open | One at a time (F5) |
| 6 | Japanese original reference | Later stage (§4.8) |
| 7 | LLM assistance | Later stage (F44) |
| 8 | UI language | Spanish (N5) |

## 10. Open questions
None blocking. Remaining **[PROPOSED]** items (technology §6, milestones §8, shortcuts, note
placement default) are accepted unless changed.

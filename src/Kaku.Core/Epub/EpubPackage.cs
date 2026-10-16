using System.IO.Compression;

namespace Kaku.Core.Epub;

/// <summary>One file inside the EPUB zip, kept fully in memory.</summary>
public sealed class EpubEntry
{
    public EpubEntry(string path, byte[] data, bool compressed, DateTimeOffset lastWrite)
    {
        Path = path;
        Data = data;
        Compressed = compressed;
        LastWrite = lastWrite;
    }

    public string Path { get; }
    public byte[] Data { get; set; }
    public bool Compressed { get; set; }
    public DateTimeOffset LastWrite { get; set; }
}

/// <summary>Raw EPUB container: the list of zip entries, read and written losslessly.</summary>
public sealed class EpubPackage
{
    public const string MimetypePath = "mimetype";
    public const string ContainerPath = "META-INF/container.xml";

    private readonly List<EpubEntry> _entries = new();
    private readonly Dictionary<string, EpubEntry> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EpubEntry> _byPathIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EpubEntry> Entries => _entries;

    public static EpubPackage Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Load(fs);
    }

    public static EpubPackage Load(Stream stream)
    {
        var pkg = new EpubPackage();
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/')) continue; // directory entry
            using var s = e.Open();
            using var ms = new MemoryStream((int)Math.Max(0, Math.Min(e.Length, int.MaxValue)));
            s.CopyTo(ms);
            pkg.Add(new EpubEntry(e.FullName, ms.ToArray(), e.CompressedLength < e.Length, e.LastWriteTime));
        }
        return pkg;
    }

    public void Add(EpubEntry entry)
    {
        _entries.Add(entry);
        _byPath[entry.Path] = entry;
        _byPathIgnoreCase.TryAdd(entry.Path, entry);
    }

    public bool Remove(string path)
    {
        var e = Find(path);
        if (e == null) return false;
        _entries.Remove(e);
        Reindex();
        return true;
    }

    /// <summary>Renames an entry, keeping its position in the zip.</summary>
    public void Rename(string from, string to)
    {
        var e = Find(from) ?? throw new FileNotFoundException(from);
        int i = _entries.IndexOf(e);
        _entries[i] = new EpubEntry(to, e.Data, e.Compressed, e.LastWrite);
        Reindex();
    }

    /// <summary>Adds a new file, or replaces the content of an existing one.</summary>
    public EpubEntry Put(string path, byte[] data, bool? compressed = null)
    {
        if (Find(path) is { } existing)
        {
            existing.Data = data;
            return existing;
        }
        var ext = EpubPath.Extension(path);
        var entry = new EpubEntry(path, data, compressed ?? ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".woff" or ".woff2"), DateTimeOffset.Now);
        Add(entry);
        return entry;
    }

    public bool Exists(string path) => Find(path) != null;

    /// <summary>Shallow copy of the entry list (byte arrays are shared; edits always assign new arrays).</summary>
    public List<EpubEntry> Snapshot() => _entries.Select(e => new EpubEntry(e.Path, e.Data, e.Compressed, e.LastWrite)).ToList();

    public void Restore(List<EpubEntry> snapshot)
    {
        _entries.Clear();
        _entries.AddRange(snapshot.Select(e => new EpubEntry(e.Path, e.Data, e.Compressed, e.LastWrite)));
        Reindex();
    }

    private void Reindex()
    {
        _byPath.Clear();
        _byPathIgnoreCase.Clear();
        foreach (var e in _entries)
        {
            _byPath[e.Path] = e;
            _byPathIgnoreCase.TryAdd(e.Path, e);
        }
    }

    /// <summary>Finds an entry by its path; falls back to a case-insensitive match (some books get the case wrong).</summary>
    public EpubEntry? Find(string path) =>
        _byPath.TryGetValue(path, out var e) ? e : _byPathIgnoreCase.GetValueOrDefault(path);

    public byte[]? Read(string path) => Find(path)?.Data;

    /// <summary>Writes a valid EPUB: <c>mimetype</c> first and stored, the rest in original order. Atomic (temp file + move).</summary>
    public void Save(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var tmp = full + ".kaku-tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite))
            {
                Save(fs);
            }
            Verify(tmp);
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Re-reads a written file and checks every entry is there with the right size.</summary>
    private void Verify(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count == 0 || zip.Entries[0].FullName != MimetypePath)
            throw new IOException("El EPUB escrito no es válido (mimetype).");
        var sizes = zip.Entries.ToDictionary(e => e.FullName, e => e.Length, StringComparer.Ordinal);
        foreach (var e in _entries)
            if (!sizes.TryGetValue(e.Path, out var len) || len != e.Data.Length)
                throw new IOException($"El EPUB escrito no es válido ({e.Path}).");
    }

    public void Save(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var mimetype = Find(MimetypePath);
        var mimeData = mimetype?.Data ?? "application/epub+zip"u8.ToArray();
        WriteEntry(zip, MimetypePath, mimeData, CompressionLevel.NoCompression, mimetype?.LastWrite);
        foreach (var e in _entries)
        {
            if (ReferenceEquals(e, mimetype)) continue;
            WriteEntry(zip, e.Path, e.Data, e.Compressed ? CompressionLevel.Optimal : CompressionLevel.NoCompression, e.LastWrite);
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data, CompressionLevel level, DateTimeOffset? lastWrite)
    {
        var entry = zip.CreateEntry(name, level);
        if (lastWrite is { } lw && lw.Year >= 1980) entry.LastWriteTime = lw;
        using var s = entry.Open();
        s.Write(data);
    }
}

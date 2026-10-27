namespace DashDeck.Core.Dbc;

/// <summary>
/// The person's own CAN databases, opened by file name when a catalog entry refers to one (ADR-0074).
/// </summary>
/// <remarks>
/// One folder — <c>%LOCALAPPDATA%\DashDeck\dbc\</c> on every machine that runs the dash, the ID
/// hunter or the ID matcher — and file names only, so a reference cannot reach anywhere else. Each
/// file is read once and kept. <b>Nothing here throws:</b> a missing or unreadable file is a reason,
/// and the signal that wanted it loads as a placeholder.
/// </remarks>
public sealed class DbcLibrary
{
    private readonly string? _folder;
    private readonly Dictionary<string, (DbcDocument? Document, string? Problem)> _opened = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    private DbcLibrary(string? folder) => _folder = folder;

    /// <summary>No databases at all: every reference says so.</summary>
    public static DbcLibrary None => new(null);

    /// <summary>The databases in a folder, which need not exist.</summary>
    public static DbcLibrary InFolder(string? folder) => new(folder);

    /// <summary>Databases already in hand, by file name — for tests and tools.</summary>
    public static DbcLibrary Of(IReadOnlyDictionary<string, DbcDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var library = new DbcLibrary(null);
        foreach (var (name, document) in documents)
        {
            library._opened[name] = (document, null);
        }

        return library;
    }

    /// <summary>The folder it reads, or null.</summary>
    public string? Folder => _folder;

    /// <summary>True when a name is a file name alone — no folder, nothing that climbs out of one.</summary>
    public static bool IsPlainName(string? file) =>
        !string.IsNullOrWhiteSpace(file)
        && file.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\', ':']) < 0
        && file.Trim('.').Length > 0;

    /// <summary>A database by file name, or why there is none.</summary>
    public (DbcDocument? Document, string? Problem) Open(string file)
    {
        lock (_lock)
        {
            if (_opened.TryGetValue(file, out var known))
            {
                return known;
            }

            var opened = Read(file);
            _opened[file] = opened;
            return opened;
        }
    }

    private (DbcDocument?, string?) Read(string file)
    {
        if (!IsPlainName(file))
        {
            return (null, $"'{file}' is not a file name");
        }

        if (_folder is null)
        {
            return (null, $"there is no database folder to find {file} in");
        }

        var path = Path.Combine(_folder, file);
        if (!File.Exists(path))
        {
            return (null, $"{file} is not in {_folder}");
        }

        try
        {
            return (DbcReader.Load(path).Document, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"{file} would not open: {ex.Message}");
        }
    }
}

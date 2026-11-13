using System.Text.Json;

namespace DataRepo.Bundle;

/// <summary>A folder catalog's <c>latest.json</c>: which versioned catalog file is current (D48 step 1).</summary>
/// <remarks>A folder build writes a NEW file, <c>catalog-&lt;catalog id&gt;.duckdb</c>, and then rewrites this small pointer.
/// So no file a reader may hold is ever replaced, and the swap a reader sees is the pointer's, which is atomic where a
/// 60 GB catalog's never was. A reader opens the folder (or the pointer) and gets the file it names. The pointer is
/// written by the catalog builder; this is the reading side, which every reader (MCP server, site, query) shares.</remarks>
public static class CatalogPointer
{
    public const string FileName = "latest.json";

    /// <summary>The versioned catalog file of catalog <paramref name="catalogId"/>.</summary>
    public static string FileOf(string catalogId) => $"catalog-{catalogId}.duckdb";

    /// <summary>Whether <paramref name="path"/> is a folder catalog or its pointer, rather than one <c>.duckdb</c> file.</summary>
    public static bool IsPointer(string path) =>
        Directory.Exists(path) || string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog file a reader should open: a <c>.duckdb</c> file is itself; a folder, or its
    /// <c>latest.json</c>, is the file the pointer names.</summary>
    /// <exception cref="CatalogException">The pointer is missing or unreadable, or names a file that is not there.</exception>
    public static string Resolve(string path)
    {
        if (!IsPointer(path)) return path;
        var pointer = Directory.Exists(path) ? Path.Combine(path, FileName) : path;
        if (!File.Exists(pointer))
            throw new CatalogException($"{path} is a catalog folder with no {FileName}: nothing has been built into it yet");
        string? name;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pointer));
            name = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("catalog", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new CatalogException($"{pointer} cannot be read: {e.Message}");
        }
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\'))
            throw new CatalogException($"{pointer} names no catalog file in its folder");
        var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pointer))!, name);
        if (!File.Exists(file))
            throw new CatalogException($"{pointer} names {name}, which is not in the folder");
        return file;
    }
}

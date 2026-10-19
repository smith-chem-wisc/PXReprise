using DuckDB.NET.Data;

namespace DataRepo.Catalog;

/// <summary>Whether a DuckDB file is as long as its own header says (aging 107).</summary>
/// <remarks>DuckDB 1.5 writes three 4 KiB headers and then fixed-size blocks, and its header counts every block, free
/// ones included. So a whole file is exactly <c>12288 + total_blocks * block_size</c> bytes (measured 2026-10-19 on a
/// fresh file and on aging's 62 GiB catalog). A shorter file has lost its tail. DuckDB still opens it and answers from
/// what is left, and a query fails only when it reaches a missing block, with "Reached the end of the file". That is
/// what happened to aging's catalog <c>15fdb093d43221ac</c>: whole at 08:58, 4.3 GB short by 12:30, with a length 4 KiB
/// off DuckDB's block grid, so nothing in DuckDB had cut it.</remarks>
public static class CatalogFile
{
    /// <summary>The three file headers before block 0.</summary>
    public const long HeaderBytes = 3 * 4096;

    /// <summary>The length <paramref name="con"/>'s database says its file has, and the file's actual length.</summary>
    public static (long Expected, long Actual) Lengths(DuckDBConnection con, string path)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT total_blocks, block_size FROM pragma_database_size() WHERE database_name = current_database()";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException($"DuckDB reported no size for {path}");
        var blocks = Convert.ToInt64(reader.GetValue(0));
        var blockSize = Convert.ToInt64(reader.GetValue(1));
        return (HeaderBytes + blocks * blockSize, new FileInfo(path).Length);
    }

    /// <summary>Why <paramref name="path"/> cannot be trusted whole, or null when it is as long as DuckDB says.</summary>
    public static string? Shortfall(DuckDBConnection con, string path)
    {
        var (expected, actual) = Lengths(con, path);
        if (actual >= expected) return null;
        return $"{path} is {actual:N0} bytes, {expected - actual:N0} short of the {expected:N0} its own header describes: "
            + "its tail is missing, so any query that reaches it fails with \"Reached the end of the file\"";
    }
}

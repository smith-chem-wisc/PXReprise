using DataRepo.SchemaGen;

// Usage, from the repository root or anywhere inside it:
//   dotnet run --project dotnet/src/DataRepo.SchemaGen            write Tables.g.cs
//   dotnet run --project dotnet/src/DataRepo.SchemaGen -- --check  exit 1 if it is stale
var root = FindRoot(Directory.GetCurrentDirectory())
    ?? FindRoot(AppContext.BaseDirectory)
    ?? throw new DirectoryNotFoundException("no schema/datarepo.yaml above the working directory");
var target = Path.Combine(root, "dotnet", "src", "DataRepo.Bundle", "Generated", "Tables.g.cs");
var text = SchemaGenerator.Render(
    Path.Combine(root, "schema", "datarepo.yaml"), Path.Combine(root, "schema", "study"));

if (args.Contains("--check"))
{
    // Line endings are not content: git may check the file out with CRLF on Windows.
    var current = File.Exists(target) ? File.ReadAllText(target).Replace("\r\n", "\n") : "";
    if (current != text)
    {
        Console.Error.WriteLine($"{Path.GetRelativePath(root, target)} is out of date; run dotnet run --project dotnet/src/DataRepo.SchemaGen");
        return 1;
    }
    Console.WriteLine("tables up to date");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(target)!);
File.WriteAllText(target, text, new System.Text.UTF8Encoding(false));
Console.WriteLine($"wrote {Path.GetRelativePath(root, target)}");
return 0;

static string? FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml")))
            return dir.FullName;
    return null;
}

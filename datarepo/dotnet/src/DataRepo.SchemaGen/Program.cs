using DataRepo.SchemaGen;

// Usage, from the repository root or anywhere inside it:
//   dotnet run --project dotnet/src/DataRepo.SchemaGen            write Tables.g.cs
//   dotnet run --project dotnet/src/DataRepo.SchemaGen -- --check  exit 1 if it is stale
var root = FindRoot(Directory.GetCurrentDirectory())
    ?? FindRoot(AppContext.BaseDirectory)
    ?? throw new DirectoryNotFoundException("no schema/datarepo.yaml above the working directory");
var schemaPath = Path.Combine(root, "schema", "datarepo.yaml");
var studyDir = Path.Combine(root, "schema", "study");
var generated = Path.Combine(root, "dotnet", "src", "DataRepo.Bundle", "Generated");
var outputs = new (string Target, string Text)[]
{
    (Path.Combine(generated, "Tables.g.cs"), SchemaGenerator.Render(schemaPath, studyDir)),
    (Path.Combine(generated, "SchemaDocs.g.cs"), SchemaGenerator.RenderDocs(schemaPath, studyDir)),
};

if (args.Contains("--check"))
{
    var stale = 0;
    foreach (var (target, text) in outputs)
    {
        // Line endings are not content: git may check the file out with CRLF on Windows.
        var current = File.Exists(target) ? File.ReadAllText(target).Replace("\r\n", "\n") : "";
        if (current == text) continue;
        Console.Error.WriteLine($"{Path.GetRelativePath(root, target)} is out of date; run dotnet run --project dotnet/src/DataRepo.SchemaGen");
        stale++;
    }
    if (stale > 0) return 1;
    Console.WriteLine("tables up to date");
    return 0;
}

Directory.CreateDirectory(generated);
foreach (var (target, text) in outputs)
{
    File.WriteAllText(target, text, new System.Text.UTF8Encoding(false));
    Console.WriteLine($"wrote {Path.GetRelativePath(root, target)}");
}
return 0;

static string? FindRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "schema", "datarepo.yaml")))
            return dir.FullName;
    return null;
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PXReprise.Bundle;
using PXReprise.Host;
using PXReprise.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace PXReprise.Data.Tests;

/// <summary>The hosted server (<c>datarepo-host</c>, <c>PXReprise.Host</c>): MCP over Streamable HTTP, as part B of the
/// hosting plan (D56) proposes. Ported from dataRepo's <c>feat/ai-contributions</c> branch, without the session record
/// and the save tool, and with a bearer token, because the first host is private.</summary>
[NonParallelizable]
public class HostTests
{
    private const string Token = "test-token-0123456789abcdef";

    private static string Fixtures => Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures");

    private string _scratch = "";
    private string _catalog = "";

    /// <summary>A new copy of the fixture catalog for each test: DuckDB refuses a second database instance on a file
    /// that another instance in this process holds, and a stopped host may release it late.</summary>
    [SetUp]
    public void Catalog()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "datarepo-host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        _catalog = Path.Combine(_scratch, "catalog.duckdb");
        using var gz = new GZipStream(File.OpenRead(Path.Combine(Fixtures, "site", "catalog.duckdb.gz")), CompressionMode.Decompress);
        using var file = File.Create(_catalog);
        gz.CopyTo(file);
    }

    [TearDown]
    public void RemoveScratch()
    {
        try
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        }
        catch (IOException)
        {
            // Windows may hold the DuckDB file a moment after the host stops; the temp folder is cleaned later.
        }
    }

    private static async Task<McpClient> Connect(string address, string? token = null)
    {
        var options = new HttpClientTransportOptions { Endpoint = new Uri(address + "/mcp") };
        if (token is not null) options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token };
        return await McpClient.CreateAsync(new HttpClientTransport(options),
            new McpClientOptions { ClientInfo = new Implementation { Name = "host-test", Version = "1.0" } });
    }

    private static async Task<HttpStatusCode> Post(string address, string? origin = null, string? token = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, address + "/mcp")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await http.SendAsync(request)).StatusCode;
    }

    [Test]
    public async Task AnAgentGetsTheSameToolsAndAnswersOverHttpAsOverStdio()
    {
        await using var app = HostedServer.Build(new HostSettings(_catalog, Token: Token));
        await app.StartAsync();
        await using (var client = await Connect(HostedServer.Address(app), Token))
        {
            Assert.That((await client.ListToolsAsync()).Select(t => t.Name), Is.EqualTo(Mcp.Mcp.ToolSpecs.Select(s => s.Name)));
            var answer = await client.CallToolAsync("datarepo_sql", new Dictionary<string, object?> { ["query"] = "SELECT 1 AS x" });
            Assert.That(answer.IsError, Is.Not.True);
            Assert.That(answer.StructuredContent!.Value.GetProperty("row_count").GetInt32(), Is.EqualTo(1));
            Assert.That(answer.StructuredContent!.Value.TryGetProperty("provenance", out _), Is.True, "every answer carries its provenance");

            var refused = await client.CallToolAsync("datarepo_sql", new Dictionary<string, object?> { ["query"] = "DROP TABLE datasets" });
            Assert.That(refused.StructuredContent!.Value.GetProperty("error").GetString(), Is.EqualTo("QueryRefused"),
                "the sandbox is the same over HTTP");
        }
        await app.StopAsync();
    }

    [Test]
    public async Task ARequestWithoutTheTokenIsRefused()
    {
        await using var app = HostedServer.Build(new HostSettings(_catalog, Token: Token));
        await app.StartAsync();
        var address = HostedServer.Address(app);
        Assert.That(await Post(address), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await Post(address, token: "wrong"), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await Post(address, token: Token), Is.Not.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(async () => await Connect(address), Throws.Exception, "an MCP client with no token cannot connect");
        await app.StopAsync();
    }

    [Test]
    public void AHostOnAnAddressOtherThanLoopbackNeedsAToken()
    {
        Assert.That(() => HostedServer.Build(new HostSettings(_catalog, Urls: "http://0.0.0.0:0")),
            Throws.TypeOf<DataRepoException>().With.Message.Contains("DATAREPO_HOST_TOKEN"));
        Assert.That(() => HostedServer.Build(new HostSettings(_catalog, Token: "  ")),
            Throws.TypeOf<DataRepoException>().With.Message.Contains("empty"));
    }

    [TestCase("http://127.0.0.1:8080", true)]
    [TestCase("http://localhost:8080", true)]
    [TestCase("http://[::1]:8080", true)]
    [TestCase("http://127.0.0.1:8080;http://[::1]:8080", true)]
    [TestCase("http://0.0.0.0:8080", false)]
    [TestCase("http://*:8080", false)]
    [TestCase("http://+:8080", false)]
    [TestCase("http://10.0.0.5:8080", false)]
    [TestCase("http://127.0.0.1:8080;http://0.0.0.0:8080", false)]
    public void LoopbackIsRecognised(string urls, bool loopback) =>
        Assert.That(HostedServer.OnlyLoopback(urls), Is.EqualTo(loopback));

    [Test]
    public async Task TheHealthCheckNamesTheCatalogServedAndNeedsNoToken()
    {
        string expected;
        using (var server = new CatalogServer(CopyOf(_catalog)))
            expected = PyValues.Str(server.Identity.CatalogId);
        await using var app = HostedServer.Build(new HostSettings(_catalog, Token: Token));
        await app.StartAsync();
        using var http = new HttpClient();
        var response = await http.GetAsync(HostedServer.Address(app) + "/healthz");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(doc.RootElement.GetProperty("catalog_id").GetString(), Is.EqualTo(expected));
        await app.StopAsync();
    }

    [Test]
    public async Task AClientSendingTooMuchIsToldToSlowDown()
    {
        await using var app = HostedServer.Build(new HostSettings(_catalog, PermitPerMinute: 3));
        await app.StartAsync();
        var address = HostedServer.Address(app);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add(await Post(address));
        Assert.That(codes.Skip(3), Is.All.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(codes.Take(3), Has.None.EqualTo(HttpStatusCode.TooManyRequests));
        await app.StopAsync();
    }

    /// <summary>The MCP spec: a server MUST validate the Origin header of every HTTP connection (DNS rebinding). An agent
    /// sends none; a web page's browser always does, and only the origins the operator names are served.</summary>
    [Test]
    public async Task AWebPageFromAnOriginNotNamedIsRefused()
    {
        await using var app = HostedServer.Build(new HostSettings(_catalog, AllowedOrigins: ["https://catalog.example.org"]));
        await app.StartAsync();
        var address = HostedServer.Address(app);
        Assert.That(await Post(address, origin: "https://evil.example.com"), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await Post(address, origin: "https://catalog.example.org"), Is.Not.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await Post(address), Is.Not.EqualTo(HttpStatusCode.Forbidden), "an agent sends no Origin");
        await app.StopAsync();
    }

    // ---------------------------------------------------------------- the command line (HostCommandLine, Program.cs)

    [TestCase("--help")]
    [TestCase("-h")]
    public async Task HelpPrintsTheOptionsAndTheTokenVariableAndExitsZero(string flag)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Assert.That(await HostCommandLine.RunAsync([flag], Token, stdout, stderr), Is.Zero);
        Assert.Multiple(() =>
        {
            Assert.That(stdout.ToString(), Does.StartWith(HostCommandLine.Usage).And.Contain("--allowed-origins A,B")
                .And.Contain(HostCommandLine.TokenVariable));
            Assert.That(stderr.ToString(), Is.Empty);
        });
    }

    [Test]
    public async Task NoCatalogIsAUsageError()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Assert.That(await HostCommandLine.RunAsync(["--urls", "http://127.0.0.1:0"], Token, stdout, stderr), Is.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(stderr.ToString(), Does.StartWith(HostCommandLine.Usage).And.Contain("--catalog is required"));
            Assert.That(stdout.ToString(), Is.Empty);
        });
    }

    [TestCase("0")]
    [TestCase("-5")]
    [TestCase("x")]
    [TestCase("1.5")]
    public void ARateThatIsNotAPositiveWholeNumberIsAUsageError(string rate)
    {
        var (settings, exitCode, message) = HostCommandLine.Parse(["--catalog", "c", "--rate", rate], Token);
        Assert.Multiple(() =>
        {
            Assert.That(settings, Is.Null);
            Assert.That(exitCode, Is.EqualTo(2));
            Assert.That(message, Does.Contain($"--rate must be a positive whole number, not {rate}"));
        });
    }

    [Test]
    public void TheDefaultsAreLoopbackPort8080ARateOf120AndTheNameDatarepo()
    {
        var (settings, exitCode, message) = HostCommandLine.Parse(["--catalog", "c"], null);
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(message, Is.Null);
            Assert.That(settings, Is.EqualTo(new HostSettings("c", Token: null, PermitPerMinute: 120,
                Urls: "http://127.0.0.1:8080", Name: "datarepo", AllowedOrigins: null)));
        });
    }

    [Test]
    public void EveryOptionReachesTheSettingsAndTheOriginsAreTrimmed()
    {
        var (settings, _, _) = HostCommandLine.Parse(["--catalog", "c", "--urls", "http://0.0.0.0:9000", "--rate", "30",
            "--name", "aging", "--allowed-origins", " https://a.example.org , ,https://b.example.org"], Token);
        Assert.That(settings, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(settings!.Urls, Is.EqualTo("http://0.0.0.0:9000"));
            Assert.That(settings.PermitPerMinute, Is.EqualTo(30));
            Assert.That(settings.Name, Is.EqualTo("aging"));
            Assert.That(settings.Token, Is.EqualTo(Token));
            Assert.That(settings.AllowedOrigins, Is.EqualTo(new[] { "https://a.example.org", "https://b.example.org" }));
        });
    }

    /// <summary>The token decides whether a host on an address other than loopback starts at all, so an empty variable
    /// (set, with no value) must read as no token rather than as an empty one.</summary>
    [TestCase(null)]
    [TestCase("")]
    public void AnEmptyOrMissingTokenVariableIsNoToken(string? token) =>
        Assert.That(HostCommandLine.Parse(["--catalog", "c"], token).Settings!.Token, Is.Null);

    [Test]
    public async Task TheHostServesUntilStoppedAndThenExitsZero()
    {
        var (stdout, stderr) = (new StringWriter(), new LockedWriter());
        using var stop = new CancellationTokenSource();
        var run = HostCommandLine.RunAsync(["--catalog", _catalog, "--urls", "http://127.0.0.1:0"], Token, stdout, stderr, stop.Token);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!stderr.ToString().Contains("serving") && !run.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(60))
            await Task.Delay(50);
        Assert.That(stderr.ToString(), Does.Contain($"datarepo-host: serving {_catalog} at http://127.0.0.1:")
            .And.Contain("/mcp (bearer token required)"));
        stop.Cancel();
        Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(60)), Is.Zero);
    }

    [Test]
    public async Task AHostThatRefusesToStartExitsOneAndSaysWhy()
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        Assert.That(await HostCommandLine.RunAsync(["--catalog", _catalog, "--urls", "http://0.0.0.0:0"], null, stdout, stderr),
            Is.EqualTo(1));
        Assert.That(stderr.ToString(), Does.StartWith("datarepo-host: ").And.Contain("is not a loopback address, and no token is set"));
    }

    /// <summary>A writer the running host writes to on its own thread while the test reads it.</summary>
    private sealed class LockedWriter : StringWriter
    {
        private readonly object _gate = new();
        public override void Write(char value) { lock (_gate) base.Write(value); }
        public override void Write(string? value) { lock (_gate) base.Write(value); }
        public override Task WriteLineAsync(string? value) { lock (_gate) base.WriteLine(value); return Task.CompletedTask; }
        public override string ToString() { lock (_gate) return base.ToString(); }
    }

    /// <summary>A copy the host under test does not hold, to read the catalog id from.</summary>
    private string CopyOf(string catalog)
    {
        var copy = Path.Combine(_scratch, $"copy-{Guid.NewGuid():N}.duckdb");
        File.Copy(catalog, copy);
        return copy;
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using PXReprise.Bundle;
using PXReprise.Catalog;
using PXReprise.Mcp;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace PXReprise.Host;

/// <summary>How one hosted server copy runs.</summary>
/// <param name="Catalog">The catalog folder (or one catalog file) to serve.</param>
/// <param name="Token">The bearer token every MCP request must carry (<c>Authorization: Bearer &lt;token&gt;</c>). Null
/// serves without one, which is allowed only on a loopback address.</param>
/// <param name="PermitPerMinute">Requests one client address may make a minute before it is told to slow down (HTTP 429).</param>
/// <param name="Urls">Where to listen; port 0 picks a free one.</param>
/// <param name="Name">The server name a client sees at <c>initialize</c>.</param>
/// <param name="AllowedOrigins">The web origins a browser may call from. An agent sends no Origin.</param>
public sealed record HostSettings(
    string Catalog,
    string? Token = null,
    int PermitPerMinute = 120,
    string Urls = "http://127.0.0.1:0",
    string Name = "datarepo",
    IReadOnlyList<string>? AllowedOrigins = null);

/// <summary>The hosted MCP server: the same three tools as <c>datarepo mcp</c>, over Streamable HTTP at <c>/mcp</c>
/// (PXReprise's hosting plan, part B; D56). Ported from dataRepo's <c>feat/ai-contributions</c> branch without the
/// session record and the save tool, which come later.</summary>
/// <remarks>
/// <para>One copy serves one catalog through one read-only sandbox, one call at a time (<see cref="Mcp.Mcp.Configure"/>).
/// It grows by running more copies, which share nothing.</para>
/// <para>Three checks run before a request reaches the catalog, in this sequence: the Origin (the MCP spec: a server MUST
/// validate it, against DNS rebinding), the rate limit for each client address, and the bearer token.
/// <c>/healthz</c> needs no token, so a load balancer can call it; it names only the catalog served.</para>
/// </remarks>
public static class HostedServer
{
    /// <summary>The application, built and not started.</summary>
    /// <exception cref="CatalogException">There is no catalog at <see cref="HostSettings.Catalog"/>.</exception>
    /// <exception cref="DataRepoException">The settings listen on an address other than loopback with no token, or the
    /// token is empty.</exception>
    public static WebApplication Build(HostSettings settings)
    {
        if (settings.Token is not null && string.IsNullOrWhiteSpace(settings.Token))
            throw new DataRepoException("the token is empty: give a long random token, or none on a loopback address");
        if (settings.Token is null && !OnlyLoopback(settings.Urls))
            throw new DataRepoException(
                $"{settings.Urls} is not a loopback address, and no token is set: set DATAREPO_HOST_TOKEN, or listen on "
                + "http://127.0.0.1:<port> and reach it through a tunnel");

        var catalog = new CatalogServer(settings.Catalog);
        try
        {
            var catalogId = PyValues.Str(catalog.Identity.CatalogId);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(settings.Urls);
            builder.Services
                .AddMcpServer(options => Mcp.Mcp.Configure(options, catalog, settings.Name))
                .WithHttpTransport(http => http.Stateless = false);
            builder.Services.AddRateLimiter(limiter =>
            {
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
                        new FixedWindowRateLimiterOptions { PermitLimit = settings.PermitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });

            var app = builder.Build();
            var allowed = new HashSet<string>((settings.AllowedOrigins ?? []).Select(o => o.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
            app.Use(async (context, next) =>
            {
                var origin = context.Request.Headers.Origin.ToString();
                if (origin.Length > 0 && !allowed.Contains(origin.TrimEnd('/')))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsync($"origin {origin} is not served here").ConfigureAwait(false);
                    return;
                }
                await next(context).ConfigureAwait(false);
            });
            app.UseRateLimiter();
            if (settings.Token is { } token)
            {
                var expected = Encoding.UTF8.GetBytes("Bearer " + token);
                app.Use(async (context, next) =>
                {
                    if (!context.Request.Path.Equals("/healthz", StringComparison.Ordinal)
                        && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), expected))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await context.Response.WriteAsync("a bearer token is required").ConfigureAwait(false);
                        return;
                    }
                    await next(context).ConfigureAwait(false);
                });
            }
            app.MapGet("/healthz", () => Results.Json(new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["catalog_id"] = catalogId,
                ["served_by"] = $"datarepo {CatalogBuilder.PackageVersion}",
            }));
            app.MapMcp("/mcp");
            app.Lifetime.ApplicationStopped.Register(catalog.Dispose);
            return app;
        }
        catch
        {
            catalog.Dispose();
            throw;
        }
    }

    /// <summary>The address a started application listens on (with its real port when it asked for port 0).</summary>
    public static string Address(WebApplication app) =>
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');

    /// <summary>True when every URL in <paramref name="urls"/> (separated by <c>;</c>) listens only on a loopback address.
    /// <c>0.0.0.0</c>, <c>*</c>, <c>+</c> and any other host are not loopback.</summary>
    public static bool OnlyLoopback(string urls) =>
        urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(url =>
        {
            var host = url.Contains("://", StringComparison.Ordinal) ? url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] : url;
            host = host.Split('/')[0];
            if (host.StartsWith('['))
                host = host[1..host.IndexOf(']')];
            else if (host.LastIndexOf(':') is var colon and > 0)
                host = host[..colon];
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
        });
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PXReprise.Cli;

/// <summary>
/// The caller asked for something malformed: a missing argument, an unknown verb, an invalid question or profile file.
/// Exit code 2, error type "usage" (the mzLib bridge's convention).
/// </summary>
public class UsageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Writes the one JSON envelope a command produces on stdout, in the mzLib bridge's shape:
/// <c>{"ok":true,"data":...}</c> or <c>{"ok":false,"error":{"type":...,"message":...}}</c>. Diagnostics go to stderr.
/// </summary>
public static class Envelope
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        WriteIndented = true,
        // The output is read by people and scripts, never embedded in HTML: keep '+', quotes and non-ASCII readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static int Ok(TextWriter stdout, object? data)
    {
        stdout.WriteLine(JsonSerializer.Serialize(new { ok = true, data }, Json));
        return ExitOk;
    }

    public static int Fail(TextWriter stdout, Exception e)
    {
        var (type, code) = Classify(e);
        var inner = Unwrap(e);
        stdout.WriteLine(JsonSerializer.Serialize(new { ok = false, error = new { type, message = inner.Message } }, Json));
        return code;
    }

    /// <summary>
    /// Maps an exception to the envelope's error type and exit code. Unavailability (408/429/5xx, timeouts, transport
    /// failures) is <c>ServiceUnavailable</c>, so a caller can retry it; everything else keeps its own type name, so a
    /// contract break is never mistaken for an outage (mzLib's failure taxonomy).
    /// </summary>
    public static (string Type, int ExitCode) Classify(Exception e)
    {
        var inner = Unwrap(e);
        if (inner is UsageException) return ("usage", ExitUsage);
        if (IsUnavailable(inner)) return ("ServiceUnavailable", ExitFailure);
        return (inner.GetType().Name, ExitFailure);
    }

    public static bool IsUnavailable(Exception e) => e switch
    {
        HttpRequestException h when h.StatusCode is { } s => IsAvailabilityStatus(s),
        HttpRequestException h => h.InnerException is not null || h.Message.Contains("failed with status 5")
                                  || h.Message.Contains("failed with status 408") || h.Message.Contains("failed with status 429"),
        TaskCanceledException or TimeoutException or SocketException => true,
        // A body cut off mid-transfer ("The response ended prematurely ... (ResponseEnded)") is an IOException, not an
        // HttpRequestException: mzLib's download lets it through unwrapped. The bridge mapped it to ServiceUnavailable,
        // so aging's fetch.py retried it; missing it here made every truncated download fail its deposit at once.
        HttpIOException => true,
        _ => false,
    };

    /// <summary>408, 429 and 5xx: the same predicate as mzLib's ProteinDbRetriever and ExternalServiceTestHelper.</summary>
    public static bool IsAvailabilityStatus(HttpStatusCode status) => (int)status is 408 or 429 || (int)status >= 500;

    private static Exception Unwrap(Exception e) =>
        e is AggregateException { InnerExceptions.Count: 1 } a ? Unwrap(a.InnerExceptions[0]) : e;
}

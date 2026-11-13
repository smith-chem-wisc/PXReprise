namespace DataRepo.Bundle;

/// <summary>The sandbox would not run the statement (D14): several at once, or the wrong kind.</summary>
/// <remarks>The C# counterpart of the Python <c>QueryRefused</c>. Refused before execution, so the message can
/// say what to send instead. An agent that gets a reason can fix its query; an agent that gets a permission
/// error from deep inside DuckDB guesses.</remarks>
public class QueryRefusedException(string message) : DataRepoException(message);

/// <summary>The query was still running at the deadline and the watchdog interrupted it (D14).</summary>
/// <remarks>The C# counterpart of the Python <c>QueryTimeout</c>.</remarks>
public class QueryTimeoutException(string message) : DataRepoException(message);

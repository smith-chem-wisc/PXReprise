namespace DataRepo.Bundle;

/// <summary>An engine run, or reading an engine artefact, was refused.</summary>
/// <remarks>The C# counterpart of the Python <c>RunnerError</c>.</remarks>
public class RunnerException(string message) : DataRepoException(message);

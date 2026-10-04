namespace DataRepo.Bundle;

/// <summary>
/// An ingest or bundle write was refused: the inputs cannot be stored truthfully as given.
/// </summary>
/// <remarks>The C# counterpart of the Python <c>IngestError</c>. It is a refusal, never a warning:
/// something that is true about the data becomes a Finding row instead.</remarks>
public class IngestException(string message) : Exception(message);

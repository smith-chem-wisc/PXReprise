namespace DataRepo.Bundle;

/// <summary>Base class for every error dataRepo raises on purpose, each one a thing an operator can act on.</summary>
public class DataRepoException(string message) : Exception(message);

/// <summary>
/// An ingest or bundle write was refused: the inputs are present and readable but do not add up.
/// </summary>
/// <remarks>The C# counterpart of the Python <c>IngestError</c>. It is a refusal, never a warning:
/// something that is true about the data becomes a Finding row instead.</remarks>
public class IngestException(string message) : DataRepoException(message);

/// <summary>The instance manifest is missing, malformed, or does not name the dataset asked for.</summary>
public class ManifestException(string message) : DataRepoException(message);

/// <summary>The manifest marks the dataset <c>exclude</c> or <c>hold</c>.</summary>
/// <remarks>Refusing is the point: the producer has already judged the run unfit (a TMT dataset searched
/// as label-free, say), and loading it would put invalid quant in the repository under a status that
/// says not to.</remarks>
public class DatasetExcludedException(string message) : DataRepoException(message);

/// <summary>The run's <c>provenance.json</c> uses a schema or namespace this ingester cannot read safely.</summary>
public class UnsupportedProvenanceException(string message) : DataRepoException(message);

/// <summary>mzLib could not read a producer file it is responsible for. There is no in-house fallback.</summary>
public class ReaderUnavailableException(string message) : DataRepoException(message);

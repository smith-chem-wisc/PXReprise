namespace DataRepo.Bundle;

/// <summary>The bundles a catalog was asked to load are missing, ambiguous, or do not hold together; or a
/// catalog, or a site written from one, is not what it was expected to be.</summary>
/// <remarks>The C# counterpart of the Python <c>CatalogError</c>.</remarks>
public class CatalogException(string message) : DataRepoException(message);

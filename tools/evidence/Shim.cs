// The compiled-in drafter uses mzLib's internal SdrfReserved, which lives in SdrfBuilder.cs (not compiled in). Mirrored here
// with the same values as mzLib 1.0.596 Readers/Sdrf/SdrfBuilder.cs, as sdrf's results/benchmark/Shim.cs does.
namespace Readers
{
    internal static class SdrfReserved
    {
        public const string NotAvailable = "not available";
        public const string NotApplicable = "not applicable";
        public const string Anonymized = "anonymized";
        public const string Pooled = "pooled";
    }
}

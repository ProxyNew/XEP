using System.IO;
using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Per-source authorisation answers that depend only on (proof, list), so every
    // target of one source shares them: boundary fits for context paths. It caches the
    // existing decisions; it does not decide anything itself.
    private sealed class SourceAuthorisationIndex
    {
        private readonly Dictionary<FormKey, bool>[] boundaryFits;

        public SourceAuthorisationIndex(int proofs)
            => boundaryFits = Enumerable.Range(0, proofs).Select(_ => new Dictionary<FormKey, bool>()).ToArray();

        public Dictionary<FormKey, bool> BoundaryFits(int proof) => boundaryFits[proof];
    }

    // Comparison mode (XEP_COMPARE_AUTHORISATION=1 or --compare-authorisation): every indexed
    // answer is also computed without the index; any difference stops planning with both answers.
    private static bool CompareAuthorisation;
    private static int authorisationComparisons;

    static PatcherEngine()
    {
        if (Environment.GetEnvironmentVariable("XEP_COMPARE_AUTHORISATION") == "1") EnableAuthorisationComparison();
    }

    internal static void EnableAuthorisationComparison()
    {
        if (CompareAuthorisation) return;
        CompareAuthorisation = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Console.WriteLine($"AUTHORISATION-COMPARE matched={authorisationComparisons}");
    }

    private static void RequireSameSourceContexts(FormKey source, FormKey target, string proof,
        IReadOnlyList<FormKey[]> indexedPaths, IReadOnlyList<LootRecipientContext> indexedRecipients, IReadOnlyList<string> indexedEvidence,
        IReadOnlyList<FormKey[]> paths, IReadOnlyList<LootRecipientContext> recipients, IReadOnlyList<string> evidence)
    {
        static string Paths(IEnumerable<FormKey[]> items) => string.Join(";", items.Select(p => string.Join('>', p)));
        static string Recipients(IEnumerable<LootRecipientContext> items)
            => string.Join(";", items.Select(r => string.Join('>', r.Path) + "|" + r.Edge.Identity));
        Interlocked.Increment(ref authorisationComparisons);
        if (Paths(indexedPaths) != Paths(paths) || Recipients(indexedRecipients) != Recipients(recipients)
            || !indexedEvidence.SequenceEqual(evidence, StringComparer.Ordinal))
            throw new InvalidDataException($"Authorisation index disagrees for source {source}, target {target}, proof {proof}: "
                + $"indexed paths [{Paths(indexedPaths)}] recipients [{Recipients(indexedRecipients)}]; "
                + $"searched paths [{Paths(paths)}] recipients [{Recipients(recipients)}].");
    }
}

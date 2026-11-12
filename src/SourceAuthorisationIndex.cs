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
}

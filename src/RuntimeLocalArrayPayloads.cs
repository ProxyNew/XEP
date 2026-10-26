using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Only the possible value enters the ordinary payload-binding path. The
    // receiver, incoming frame and holder checks still run independently.
    // No successful lookup/insertion, quantity or execution is established.
    private static FormKey? FreshLocalArrayPayload(RuntimeLocalArrayIdentity identity, Catalog catalog,
        out RuntimePropertyBinding binding)
    {
        binding = new(identity.Problem ?? "None-only array consumption is outside the bounded possible-item footprint contract", true);
        if (identity.Problem is not null || identity.UnknownIdentityPossible || identity.PossibleIdentities.Length != 1
            || !identity.Evidence.StartsWith("fresh-local-array-identities-v1 ", StringComparison.Ordinal)) return null;
        var key = FormKey.Factory(identity.PossibleIdentities[0]);
        if (!RuntimeRecordWinners(catalog).TryGetValue(key, out var winner) || winner.Record.IsDeleted
            || winner.Record is not IItemGetter && winner.Record is not ILeveledItemGetter)
        {
            binding = new("local array possible payload is not a nondeleted winning item/list", true);
            return null;
        }
        if (winner.Record is IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: not null })
        {
            binding = new("local array possible payload has an unproved attached script capability", true);
            return null;
        }
        binding = new(null, false) { Evidence = "fresh-local-array-possible-item-or-none-v1 " + key + "; " + identity.Evidence
            + "; winning record plugin " + winner.Plugin };
        return key;
    }
}

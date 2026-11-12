using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Planning-only keys are remapped to allocated output records before serialization.
    private static readonly ModKey FallbackPlanningMod = ModKey.FromFileName("__XEP_FallbackPlanning.esp");
    internal sealed record FallbackNode(string Identity, string Category, ILeveledItemGetter Original);
    internal sealed record FallbackEdge(ParentEntry Parent, FormKey Child);

}

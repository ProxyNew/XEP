using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

// Summerturgy (Nexus 124127) merges Summermyst into Thaumaturgy: it replaces Summermyst's
// plugin, removes the enchanted items of the Summermyst enchantments it lists as clashing
// with Thaumaturgy, and keeps Thaumaturgy's versions. When it is active, those families are
// recommended off even if another mod still supplies items for them. A deliberate
// mod-specific rule, authorized by the maintainer for this pair (15 October 2026); see
// .scratch/summerturgy-support. Manual choices still override it.
public static partial class PatcherEngine
{
    static readonly ModKey SummerturgyMarker = ModKey.FromFileName("Summerturgy - Levelled List Abomination.esp");
    static readonly ModKey SummermystPlugin = ModKey.FromFileName("Summermyst - Enchantments of Skyrim.esp");

    // Base enchantments of the listed families that still exist in Summerturgy 1.2's Summermyst
    // plugin. Amplify Alteration/Conjuration/Destruction/Illusion/Restoration, Fortify Sneak
    // Attacks, Defender and Momentum have no records left. Cushioned is not listed: Summerturgy
    // keeps it.
    static readonly uint[] SummerturgyRemovedFamilies =
    [
        0x051368, // Fortify Bashing
        0x051291, // Fortify Speed
        0x0513CE, // Fortify Potions
        0x051513, // Death Shroud
        0x0512A5, // Light
        0x328D95, // Negate Magic
        0x3892CC, // Grit
        0x051536, // Stability
        0x3892C8, // Force
        0x42B37D, // Fortify Shout Power
        0x42B379, // Fortify Shout Rate (listed as Shout Duration)
        0x0513A7, // Recharge Weapons
        0x051150, // Sound
        0x05115F, // Sun Damage
        0x05122E, // Poison Damage
        0x0513EF, // Frenzy
        0x051177, // Command Daedra
        0x05115E, // Disable Regeneration (listed as Halt Regeneration)
        0x0511D6, // Weakness to Fire
        0x0511FE, // Weakness to Frost
        0x0511E9, // Weakness to Shock
        0x05125A, // Weakness to Poison
    ];

    static void AddSummerturgyRecommendations(Catalog catalog, Dictionary<FormKey, EnchantmentRecommendation> recommendations)
    {
        if (!catalog.Mods.Any(m => m.ModKey == SummerturgyMarker)) return;
        var families = SummerturgyRemovedFamilies.Select(id => new FormKey(SummermystPlugin, id)).ToHashSet();
        foreach (var enchantment in catalog.ObjectEffects.Values.Where(e => !e.IsDeleted))
        {
            var family = enchantment.BaseEnchantment.IsNull ? enchantment.FormKey : enchantment.BaseEnchantment.FormKey;
            if (!families.Contains(family) || recommendations.ContainsKey(enchantment.FormKey)) continue;
            recommendations[enchantment.FormKey] = new(
                $"Recommended off: Summerturgy ({SummerturgyMarker.FileName}) removes this Summermyst enchantment in favor of Thaumaturgy's version. Manual choices still override.");
        }
    }
}

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Owned by one completed catalog, after material inference. Input equipment,
    // winning base records and keyword/effect evidence are immutable during planning.
    // Object identity matters: an override copy may share a FormKey but differ in data.
    // Facts may be read from parallel review workers. Each value is computed outside
    // the lock and published once; every value is a pure function of the record.
    private sealed class EquipmentFacts(IMajorRecordGetter item, Catalog catalog)
    {
        private sealed record Known<T>(T Value);
        private Known<string[]>? labels;
        private Known<EquipmentShape?>? shape;
        private Known<EquipmentClass?>? classification;
        private Known<string?>? subtype;
        private Known<int>? value;
        private Known<string>? charge;
        private readonly Dictionary<FormKey, string> identities = [];
        private static T Once<T>(ref Known<T>? slot, Func<T> compute)
            => (Volatile.Read(ref slot) ?? Interlocked.CompareExchange(ref slot, new(compute()), null) ?? slot!).Value;
        public string[] Labels => Once(ref labels, () => Keywords(item).Select(k => catalog.KeywordNames.GetValueOrDefault(k.FormKey, "")).ToArray());
        public EquipmentShape? Shape => Once(ref shape, () => ClassifyShapeFromLabels(item, catalog.KeywordNames, Labels.Where(s => s.Length > 0).ToArray()));
        public EquipmentClass? Class => Once(ref classification,
            () => Shape is { Materials.Length: 1 } s ? new(s.Signature, s.Materials[0], s.Type, s.ArmorType) : null);
        public string? Subtype => Once(ref subtype, () => ComputeDistributionSubtype(item, catalog));
        public int Value => Once(ref value, () => ItemValue(item));
        public string ChargeText => Once(ref charge, () => Charge(item));
        public string Identity(FormKey enchantment)
            => SharedMemo(identities, enchantment, () => $"{EffectIdentity(enchantment, catalog)}|charge={ChargeText}");
    }

    private static EquipmentFacts Facts(IMajorRecordGetter item, Catalog catalog)
        => SharedMemo(catalog.EquipmentFacts, item, () => new EquipmentFacts(item, catalog));
    private static EquipmentShape? CachedShape(IMajorRecordGetter item, Catalog catalog) => Facts(item, catalog).Shape;
    private static EquipmentClass? CachedClass(IMajorRecordGetter item, Catalog catalog) => Facts(item, catalog).Class;
    private static int CachedValue(IMajorRecordGetter item, Catalog catalog) => Facts(item, catalog).Value;
    private static string CachedCharge(IMajorRecordGetter item, Catalog catalog) => Facts(item, catalog).ChargeText;
    private static string PluginName(ModKey key, Catalog catalog)
        => catalog.PluginNames.TryGetValue(key, out var name) ? name : catalog.PluginNames[key] = key.FileName.String;
}

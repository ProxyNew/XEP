using Mutagen.Bethesda.Plugins;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // FormKey equality folds plugin-name case; the old joined text did not.
    // Null ModKeys stringify identically regardless of whitespace/type.
    // ModKey rejects '>' and '|', so ordered path boundaries cannot collide.
    private static (uint Id, string Name, ModType Type) RouteTextKey(FormKey key) => key.ModKey.IsNull
        ? (key.ID, "", ModType.Master) : (key.ID, key.ModKey.Name, key.ModKey.Type);

    // Queries may borrow a path; a retained cache key owns a detached snapshot.
    private readonly record struct RoutePathIdentity(IReadOnlyList<FormKey>? Items)
    {
        public RoutePathIdentity Snapshot() => new(Items is null ? null : Array.AsReadOnly(Items.ToArray()));
        public bool Equals(RoutePathIdentity other)
        {
            var count = Items?.Count ?? 0;
            if (count != (other.Items?.Count ?? 0)) return false;
            for (var i = 0; i < count; i++)
                if (RouteTextKey(Items![i]) != RouteTextKey(other.Items![i])) return false;
            return true;
        }
        public override int GetHashCode()
        {
            var hash = new HashCode(); var count = Items?.Count ?? 0;
            hash.Add(count);
            for (var i = 0; i < count; i++) hash.Add(RouteTextKey(Items![i]));
            return hash.ToHashCode();
        }
    }

    private readonly record struct MirrorRouteIdentity((uint Id, string Name, ModType Type) Destination, string Level,
        RoutePathIdentity Isolation, RoutePathIdentity Material, string Recipient)
    {
        public static MirrorRouteIdentity Of(SpecializedRoute route) => new(RouteTextKey(route.Destination.List), route.Destination.Level.ToString(),
            new(route.IsolationPath), new(route.MaterialPath), route.RecipientIsolation?.Identity ?? "");
        public MirrorRouteIdentity Snapshot() => this with { Isolation = Isolation.Snapshot(), Material = Material.Snapshot() };
    }

    // The source and payload enter only through their walk (MirrorSourcePaths).
    private readonly record struct MirrorGateIdentity(int Walk, (uint Id, string Name, ModType Type) Receiver, MirrorRouteIdentity Route)
    {
        public static MirrorGateIdentity Of(MirrorSite site, MirrorSourceWalk walk, SpecializedRoute route)
            => new(walk.Id, RouteTextKey(site.Receiver), MirrorRouteIdentity.Of(route));
        public MirrorGateIdentity Snapshot() => this with { Route = Route.Snapshot() };
    }
}

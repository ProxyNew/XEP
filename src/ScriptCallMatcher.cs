using System.Globalization;
using System.Text.RegularExpressions;

namespace XEnchantmentPatcher;

// Scoped to one availability analysis. The regex remains the authority; the
// fast path only rejects ASCII bodies that cannot contain the literal name.
internal sealed class ScriptCallMatcher
{
    private sealed record Rule(Regex Pattern, bool AsciiPrefilter);
    private readonly Dictionary<(string Name, string Culture), Rule> rules = new();
    private readonly Dictionary<string, bool> asciiBodies = new(ReferenceEqualityComparer.Instance);

    public IEnumerable<Match> Matches(string name, string body)
    {
        var culture = CultureInfo.CurrentCulture;
        var key = (name, culture.Name);
        if (!rules.TryGetValue(key, out var rule))
        {
            var pattern = new Regex(@"(?im)^\s*(?:(?:(?<recv>\w+)|\(\s*(?<recv>\w+)\s+as\s+(?<cast>\w+)\s*\))\s*\.\s*)?" + Regex.Escape(name) + @"\s*\(\s*\)\s*$");
            // In Turkish/Azeri, ASCII I/i are not the same regex case pair.
            // Non-ASCII names/bodies always take the original regex path.
            bool ascii = ScriptLiteralSearch.CanPrefilter(name, culture);
            rule = new(pattern, ascii);
            rules.Add(key, rule);
        }
        if (rule.AsciiPrefilter)
        {
            if (!asciiBodies.TryGetValue(body, out var ascii)) asciiBodies.Add(body, ascii = ScriptLiteralSearch.IsAscii(body));
            if (ascii && !body.Contains(name, StringComparison.OrdinalIgnoreCase)) return Array.Empty<Match>();
        }
        return rule.Pattern.Matches(body);
    }

}

// A rejection only: possible matches still go through the original regex.
internal static class ScriptLiteralSearch
{
    public static bool CanPrefilter(string name, CultureInfo culture) => IsAscii(name)
        && (name.IndexOf('i', StringComparison.OrdinalIgnoreCase) < 0 || culture.TextInfo.ToLower('I') == 'i');

    public static bool MayContain(string text, string name, ref bool? ascii) =>
        !CanPrefilter(name, CultureInfo.CurrentCulture) || !(ascii ??= IsAscii(text))
        || text.Contains(name, StringComparison.OrdinalIgnoreCase);

    public static bool IsAscii(string text)
    {
        foreach (var c in text) if (c > 127) return false;
        return true;
    }
}

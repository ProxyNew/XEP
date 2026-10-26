using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XEnchantmentPatcher;

public static partial class PatcherEngine
{
    // Read literals before comments so punctuation inside a string cannot
    // erase another argument. Keep positions and line breaks for function offsets.
    private static string? MaskPapyrusText(string text, bool strings)
    {
        var output = text.ToCharArray();
        void Mask(int start, int end)
        {
            for (var p = start; p < end; p++)
                if (output[p] is not ('\r' or '\n')) output[p] = ' ';
        }
        for (var p = 0; p < text.Length;)
        {
            var start = p;
            if (text[p] == '"')
            {
                p++;
                var closed = false;
                while (p < text.Length)
                {
                    if (text[p] is '\r' or '\n') return null;
                    if (text[p] == '\\') { p += 2; continue; }
                    if (text[p++] == '"') { closed = true; break; }
                }
                if (!closed) return null;
                if (strings) Mask(start, p);
            }
            else if (text[p] == ';')
            {
                if (p + 1 < text.Length && text[p + 1] == '/')
                {
                    var end = text.IndexOf("/;", p + 2, StringComparison.Ordinal);
                    if (end < 0) return null;
                    p = end + 2;
                }
                else
                    while (p < text.Length && text[p] is not ('\r' or '\n')) p++;
                Mask(start, p);
            }
            else if (text[p] == '{')
            {
                var end = text.IndexOf('}', p + 1);
                if (end < 0) return null;
                p = end + 1;
                Mask(start, p);
            }
            else p++;
        }
        return new string(output);
    }

    private static (string? Text, string Evidence) PrepareRuntimeScript((string? Text, string Evidence) script)
    {
        if (script.Text is null) return script;
        var prepared = MaskPapyrusText(script.Text, false);
        return (prepared, prepared is null ? script.Evidence + "; malformed script literal/comment" : script.Evidence);
    }

    private static string MaskScriptStrings(string text) => MaskPapyrusText(text, true) ?? "__xep_unparsed_script_text";

    // Masking, encoding and lexical facts of one immutable script/function body.
    // Keyed by the string instance, so different text cannot collide; entries die
    // with their text. Proof verdicts, bindings and contexts are never stored here.
    private static readonly ConditionalWeakTable<string, ScriptTextSyntax> ScriptTextSyntaxes = new();
    private static ScriptTextSyntax TextSyntax(string text) => ScriptTextSyntaxes.GetValue(text, t => new(t));
    private sealed class ScriptTextSyntax(string text)
    {
        private string? masked;
        private string[]? lines, maskedLines;
        private byte[]? utf8;
        private bool? ascii, maskedAscii;
        private readonly Dictionary<(string Name, string Culture), string?> propertyTypes = [];
        private readonly Dictionary<(string Name, string Culture), bool> rebindings = [], localDeclarations = [], methods = [], parameters = [];
        private static (string Name, string Culture) SyntaxKey(string name)
            => (name, System.Globalization.CultureInfo.CurrentCulture.Name);

        // For an ASCII identifier in ASCII text, outside the Turkish-style cultures the
        // literal prefilter already excludes, each per-name regex below reduces to a
        // whole-token comparison: the name must occupy one maximal \w run at the
        // required position. One pass over the text therefore answers every name; any
        // other name or text keeps the original per-name regex.
        // Inline (?i) patterns bind case folding to the culture current at construction;
        // the shared regexes below (and the sets built from them) serve only that culture.
        private static readonly string RegexCulture = System.Globalization.CultureInfo.CurrentCulture.Name;
        private static readonly Regex AutoPropertyDeclarations = new(@"(?im)^\s*(\w+(?:\[\])?)\s+property\s+(\w+)\s+auto(?:readonly)?(?:\s+hidden)?\s*$", RegexOptions.Compiled);
        private static readonly Regex Rebindings = new(@"\b(\w+)\s*(?:[+\-*/]?=|\[)", RegexOptions.Compiled);
        private static readonly Regex LocalDeclarations = new(@"(?im)^\s*(?!(?:return|if|elseif|while)\b)\w+(?:\[\])?\s+(\w+)\s*(?:=|$)", RegexOptions.Compiled);
        private static readonly Regex MethodDeclarations = new(@"(?im)^\s*(?:\w+(?:\[\])?\s+)?(?:function|event)\s+(\w+)\s*\(", RegexOptions.Compiled);
        private static readonly Regex WordTokens = new(@"\w+", RegexOptions.Compiled);
        private Dictionary<string, (string Type, int Count)>? autoProperties;
        private HashSet<string>? rebinders, localDeclarers, methodNames, wordTokens;
        private static bool AsciiIdentifier(string name)
        {
            if (name.Length == 0) return false;
            foreach (var c in name) if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            return culture.Name == RegexCulture && ScriptLiteralSearch.CanPrefilter(name, culture);
        }
        private static HashSet<string> Captured(Regex pattern, string subject)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in pattern.Matches(subject)) names.Add(match.Groups[match.Groups.Count - 1].Value);
            return names;
        }

        public string? AutoPropertyType(string name)
        {
            lock (propertyTypes)
            {
                var key = SyntaxKey(name);
                if (propertyTypes.TryGetValue(key, out var known)) return known;
                using var timing = PhaseTiming.Measure("script-text.property-type");
                if (AsciiIdentifier(name) && (maskedAscii ??= ScriptLiteralSearch.IsAscii(Masked)))
                {
                    if (autoProperties is null)
                    {
                        autoProperties = new(StringComparer.OrdinalIgnoreCase);
                        foreach (Match match in AutoPropertyDeclarations.Matches(Masked))
                        {
                            var declared = match.Groups[2].Value;
                            autoProperties[declared] = autoProperties.TryGetValue(declared, out var seen)
                                ? (seen.Type, seen.Count + 1) : (match.Groups[1].Value, 1);
                        }
                    }
                    return propertyTypes[key] = autoProperties.TryGetValue(name, out var found) && found.Count == 1 ? found.Type : null;
                }
                var matches = Regex.Matches(Masked, @"(?im)^\s*(\w+(?:\[\])?)\s+property\s+" + Regex.Escape(name) + @"\s+auto(?:readonly)?(?:\s+hidden)?\s*$");
                return propertyTypes[key] = matches.Count == 1 ? matches[0].Groups[1].Value : null;
            }
        }
        public bool Rebinds(string name)
        {
            lock (rebindings)
            {
                var key = SyntaxKey(name);
                if (rebindings.TryGetValue(key, out var known)) return known;
                if (AsciiIdentifier(name) && (maskedAscii ??= ScriptLiteralSearch.IsAscii(Masked)))
                    return rebindings[key] = (rebinders ??= Captured(Rebindings, Masked)).Contains(name);
                return rebindings[key] = Regex.IsMatch(Masked, @"(?i)\b" + Regex.Escape(name) + @"\s*(?:[+\-*/]?=|\[)");
            }
        }
        public bool DeclaresLocal(string name)
        {
            lock (localDeclarations)
            {
                var key = SyntaxKey(name);
                if (localDeclarations.TryGetValue(key, out var known)) return known;
                if (AsciiIdentifier(name) && (maskedAscii ??= ScriptLiteralSearch.IsAscii(Masked)))
                    return localDeclarations[key] = (localDeclarers ??= Captured(LocalDeclarations, Masked)).Contains(name);
                return localDeclarations[key] = ScriptLiteralSearch.MayContain(Masked, name, ref maskedAscii) && Regex.IsMatch(Masked,
                    @"(?im)^\s*(?!(?:return|if|elseif|while)\b)\w+(?:\[\])?\s+" + Regex.Escape(name) + @"\s*(?:=|$)");
            }
        }
        public bool HasParameter(string name)
        {
            if (text.Length == 0) return false;
            lock (parameters)
            {
                var key = SyntaxKey(name);
                if (parameters.TryGetValue(key, out var known)) return known;
                if (AsciiIdentifier(name) && (ascii ??= ScriptLiteralSearch.IsAscii(text)))
                    return parameters[key] = (wordTokens ??= Captured(WordTokens, text)).Contains(name);
                return parameters[key] = ScriptLiteralSearch.MayContain(text, name, ref ascii)
                    && Regex.IsMatch(text, @"(?i)\b" + Regex.Escape(name) + @"\b");
            }
        }
        public bool HasMethod(string name)
        {
            lock (methods)
            {
                var key = SyntaxKey(name);
                if (methods.TryGetValue(key, out var known)) return known;
                if (AsciiIdentifier(name) && (ascii ??= ScriptLiteralSearch.IsAscii(text)))
                    return methods[key] = (methodNames ??= Captured(MethodDeclarations, text)).Contains(name);
                return methods[key] = Regex.IsMatch(text,
                    @"(?im)^\s*(?:\w+(?:\[\])?\s+)?(?:function|event)\s+" + Regex.Escape(name) + @"\s*\(");
            }
        }
        public string Masked => masked ??= Build(() => MaskScriptStrings(text));
        public IReadOnlyList<string> Lines => RawLines;
        // Each line masked on its own, exactly as the per-line proofs mask it.
        public IReadOnlyList<string> MaskedLines => maskedLines ??= Build(() => Array.ConvertAll(RawLines, MaskScriptStrings));
        private string[] RawLines => lines ??= text.Split('\n');
        public ReadOnlySpan<byte> Utf8 => utf8 ??= Build(() => Encoding.UTF8.GetBytes(text));
        private static T Build<T>(Func<T> build)
        {
            using var timing = PhaseTiming.Measure("script-text.syntax");
            return build();
        }
    }
}

using System.Text.RegularExpressions;

namespace XEnchantmentPatcher;

// Bounded structural parsing only. Recognizing a predicate is separate from
// proving that the original script eventually executes its guarded mutation.
internal static class ScriptFlowAnalysis
{
    internal sealed record Node(string Text, int Start, int End, string Kind, List<Node> Then, List<Node> Else);
    internal sealed record Result(List<Node> Nodes, string? Problem)
    {
        internal (Node? Statement, string[] Predicates, bool Loop) At(int position)
        {
            (Node?, string[], bool) Visit(IEnumerable<Node> nodes, string[] guards, bool loop)
            {
                foreach (var n in nodes)
                {
                    if (n.Kind == "statement" && position >= n.Start && position < n.End) return (n, guards, loop);
                    if (n.Kind == "if")
                    {
                        var found = Visit(n.Then, [.. guards, n.Text], loop);
                        if (found.Item1 is not null) return found;
                        found = Visit(n.Else, [.. guards, "!(" + n.Text + ")"], loop);
                        if (found.Item1 is not null) return found;
                    }
                    else if (n.Kind == "while")
                    {
                        var found = Visit(n.Then, [.. guards, "while(" + n.Text + ")"], true);
                        if (found.Item1 is not null) return found;
                    }
                }
                return (null, [], loop);
            }
            return Problem is null ? Visit(Nodes, [], false) : (null, [], false);
        }

        internal bool UnconditionalAt(int position)
        {
            var target = At(position);
            if (Problem is not null || target.Statement is null || target.Loop || target.Predicates.Length != 0) return false;
            // An earlier branch may call an unknown helper, block forever or
            // rebind the insertion properties. Its effects are not proved here.
            bool Stops(Node n) => n.Kind is "if" or "while" || Regex.IsMatch(n.Text, @"(?i)^(return\b|stop\s*\(|gotoState\s*\()")
                || n.Then.Any(Stops) || n.Else.Any(Stops);
            return !Nodes.Where(n => n.Start < target.Statement.Start).Any(Stops);
        }
    }

    private sealed class Parser(string body)
    {
        private readonly (string Text, int Start, int End)[] lines = Lines(body);
        private int cursor, nodes;
        private string? problem;
        private static (string, int, int)[] Lines(string body)
        {
            var result = new List<(string, int, int)>(); var offset = 0;
            foreach (var raw in body.Split('\n'))
            {
                var quoted = false; var end = raw.Length;
                for (var i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == '"' && (i == 0 || raw[i - 1] != '\\')) quoted = !quoted;
                    if (!quoted && raw[i] == ';') { end = i; break; }
                }
                var text = raw[..end].Trim();
                if (text.Length > 0) result.Add((text, offset, offset + raw.Length));
                offset += raw.Length + 1;
            }
            return result.ToArray();
        }
        private static string Token(string text) => Regex.Match(text, @"(?i)^(if|elseif|else|endif|while|endwhile)\b").Value.ToLowerInvariant();
        private List<Node> Block(int depth)
        {
            var result = new List<Node>();
            if (depth > 32) { problem = "conditional nesting exceeds 32"; return result; }
            while (cursor < lines.Length && problem is null)
            {
                var line = lines[cursor]; var token = Token(line.Text);
                if (token is "elseif" or "else" or "endif" or "endwhile") break;
                if (++nodes > 16384) { problem = "function exceeds 16384 parsed statements"; break; }
                if (token == "if") result.Add(Conditional(depth, consumeEnd: true));
                else if (token == "while")
                {
                    cursor++; var nested = Block(depth + 1);
                    if (cursor >= lines.Length || Token(lines[cursor].Text) != "endwhile") { problem = "unbalanced while"; break; }
                    cursor++;
                    result.Add(new(line.Text[5..].Trim(), line.Start, line.End, "while", nested, []));
                }
                else { cursor++; result.Add(new(line.Text, line.Start, line.End, "statement", [], [])); }
            }
            return result;
        }
        private Node Conditional(int depth, bool consumeEnd)
        {
            var line = lines[cursor++]; var token = Token(line.Text);
            var guard = line.Text[token.Length..].Trim(); var yes = Block(depth + 1); var no = new List<Node>();
            if (cursor < lines.Length && Token(lines[cursor].Text) == "elseif") no.Add(Conditional(depth + 1, false));
            else if (cursor < lines.Length && Token(lines[cursor].Text) == "else") { cursor++; no = Block(depth + 1); }
            if (consumeEnd)
            {
                if (cursor >= lines.Length || Token(lines[cursor].Text) != "endif") problem = "unbalanced if";
                else cursor++;
            }
            return new(guard, line.Start, line.End, "if", yes, no);
        }
        internal Result Parse()
        {
            var result = Block(0);
            if (problem is null && cursor != lines.Length) problem = "unexpected branch terminator";
            return new(result, problem);
        }
    }

    internal static Result Parse(string body) => new Parser(body).Parse();
    internal static bool SchedulesRetry(Node node)
    {
        if (node.Kind != "statement") return false;
        var call = Regex.Match(node.Text, @"(?i)^(?:self\s*\.\s*)?RegisterForSingleUpdate\s*\(\s*(?<delay>[0-9]+(?:\.[0-9]+)?)(?:\s+as\s+Float)?\s*\)$");
        return call.Success && float.TryParse(call.Groups["delay"].Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var delay)
            && float.IsFinite(delay) && delay > 0;
    }
}

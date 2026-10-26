namespace XEnchantmentPatcher;

// Keep every diagnostic in report order; only explicitly informational messages
// are excluded from warnings. Unclassified diagnostics remain warnings.
internal class DiagnosticMessages : List<string>
{
    private readonly List<string> information = [];
    public IReadOnlyList<string> Information => information;

    public DiagnosticMessages() { }
    public DiagnosticMessages(DiagnosticMessages source) : base(source) => information.AddRange(source.Information);
    public new virtual void Add(string message) => base.Add(message);

    public void AddInformation(string message)
    {
        Add(message);
        information.Add(message);
    }

    public void AddDiagnostic(string message, bool informational)
    {
        if (informational) AddInformation(message);
        else Add(message);
    }

    public void AddRuleProblems(string framework, IEnumerable<string> statuses)
    {
        var problems = statuses.GroupBy(s => s, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (problems.Count > 0)
            Add($"{framework}: {problems.Sum(g => g.Count())} rules need attention ({string.Join(", ", problems.Select(g => $"{g.Count()} {g.Key}"))}); see the captured file/rule provenance.");
    }
}

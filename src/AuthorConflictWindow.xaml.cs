using System.ComponentModel;
using System.Windows;

namespace XEnchantmentPatcher;

internal sealed class AuthorConflictRow(AuthorConflict conflict, string target) : INotifyPropertyChanged
{
    private bool useAuthor;
    public AuthorConflict Conflict { get; } = conflict;
    public string Heading { get; } = $"{FieldLabel(conflict.Field)} · {target}";
    public string Values { get; } = conflict.CrossLevel
        ? $"Author: include this item. Yours: {conflict.UserValue} (the plugin is excluded)."
        : $"Author: {Display(conflict.Field, conflict.AuthorValue)}. Yours: {Display(conflict.Field, conflict.UserValue)}.";
    public string Reason { get; } = string.IsNullOrWhiteSpace(conflict.Reason) ? "" : "Author's reason: " + conflict.Reason;
    public bool UseAuthor
    {
        get => useAuthor;
        set { if (useAuthor == value) return; useAuthor = value; PropertyChanged?.Invoke(this, new(nameof(UseAuthor))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    static string Display(string field, string value) => (field, value) switch
    {
        ("include", "True") => "patch",
        ("include", "False") => "don't patch",
        ("enchantment", "True") => "on",
        ("enchantment", "False") => "off",
        _ => value,
    };

    static string FieldLabel(string field) => field switch
    {
        "patchPolicy" => "Plugin rule",
        "include" => "Patch item",
        "materialFamily" => "Material family",
        "distributionMode" => "Distribution",
        "enchantment" => "Enchantment",
        AuthorDefaults.IncludeUnderExclude => "Patch item in an excluded plugin",
        _ => field,
    };
}

public partial class AuthorConflictWindow : Window
{
    private readonly AuthorConflictRow[] rows;
    internal IEnumerable<(AuthorConflict Conflict, bool UseAuthor)> Answers => rows.Select(row => (row.Conflict, row.UseAuthor));

    // Targets name a record by its item or enchantment name when Scan listed it.
    internal AuthorConflictWindow(IReadOnlyList<AuthorConflict> conflicts, Func<string, string> target)
    {
        rows = conflicts.Select(conflict => new AuthorConflictRow(conflict, target(conflict.Key))).ToArray();
        UiTheme.Attach(this);
        InitializeComponent();
        ConflictList.ItemsSource = rows;
        foreach (var row in rows) row.PropertyChanged += (_, _) => UpdateCount();
        Summary.Text = $"The installed rules file sets {rows.Length} value{(rows.Length == 1 ? "" : "s")} differently from choices you saved. Until you answer, your choices apply.";
        UpdateCount();
    }

    private void UpdateCount()
    {
        var author = rows.Count(row => row.UseAuthor);
        SelectionCount.Text = $"{author} author value{(author == 1 ? "" : "s")}, {rows.Length - author} of yours";
    }

    private void KeepAll(object sender, RoutedEventArgs e) { foreach (var row in rows) row.UseAuthor = false; }
    private void UseAll(object sender, RoutedEventArgs e) { foreach (var row in rows) row.UseAuthor = true; }
    private void Accept(object sender, RoutedEventArgs e) => DialogResult = true;
}

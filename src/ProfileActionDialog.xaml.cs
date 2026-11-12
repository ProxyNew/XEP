using System.Windows;

namespace XEnchantmentPatcher;

internal enum ProfileAction { New, Duplicate }

public partial class ProfileActionDialog : Window
{
    private readonly Func<string, string> _validate;
    internal string AcceptedName { get; private set; } = "";

    internal ProfileActionDialog(ProfileAction action, string currentName, Func<string, string> validate)
    {
        UiTheme.Attach(this);
        InitializeComponent();
        _validate = validate;
        Title = action + " settings profile";
        AcceptButton.Content = action.ToString();
        Explanation.Text = action switch
        {
            ProfileAction.New => "Start with current application defaults. Data and Output folders stay shared.",
            _ => $"Copy the patching choices from '{currentName}'. The copy starts without generation history. Data and Output folders stay shared."
        };
        Loaded += (_, _) => { NameInput.Focus(); NameInput.SelectAll(); };
    }

    private void Accept(object sender, RoutedEventArgs e)
    {
        try
        {
            AcceptedName = _validate(NameInput.Text);
            DialogResult = true;
        }
        catch (ArgumentException ex) { NameError.Text = ex.Message; NameInput.Focus(); }
    }
}

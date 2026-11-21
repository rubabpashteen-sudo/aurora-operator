using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuroraOperator.App.ViewModels;
using AuroraOperator.Core;

namespace AuroraOperator.App;

/// <summary>
/// Main window. All behaviour lives in <see cref="MainViewModel"/>; this
/// file only does what code-behind must do: theme dictionary swaps, the
/// Enter-to-send key handling, and loading a task from a double-click.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        InitializeComponent();

        _viewModel.ThemeToggleRequested += (_, _) => ApplyTheme(_viewModel.IsDarkTheme);
        InputBox.Focus();
    }

    /// <summary>
    /// Swaps the merged theme dictionary between DarkTheme.xaml and
    /// LightTheme.xaml. All brushes are DynamicResource references, so the
    /// whole window re-themes without recreating anything.
    /// </summary>
    private void ApplyTheme(bool dark)
    {
        string themeFile = dark ? "Resources/DarkTheme.xaml" : "Resources/LightTheme.xaml";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        for (int i = dictionaries.Count - 1; i >= 0; i--)
        {
            string? source = dictionaries[i].Source?.OriginalString;
            if (source is not null &&
                (source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
                 source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                dictionaries.RemoveAt(i);
            }
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(themeFile, UriKind.Relative),
        });
    }

    /// <summary>
    /// Enter sends the message; Shift+Enter inserts a newline (the
    /// TextBox default for a multiline box).
    /// </summary>
    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (_viewModel.SendCommand.CanExecute(null))
            {
                _viewModel.SendCommand.Execute(null);
            }
        }
    }

    /// <summary>Double-click a recent task to load it back into the chat.</summary>
    private void RecentTasksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is AgentTask task)
        {
            _viewModel.LoadTaskIntoView(task);
        }
    }
}

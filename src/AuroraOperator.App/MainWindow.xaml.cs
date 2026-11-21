using System.Windows;
using AuroraOperator.App.Services;
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
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);

        _viewModel.ThemeToggleRequested += (_, _) => ApplyTheme(_viewModel.IsDarkTheme);
        SourceInitialized += (_, _) => ApplyBackdrop(_viewModel.IsDarkTheme);
        if (!SystemParameters.ClientAreaAnimation || SystemParameters.HighContrast)
            ActivityDots.Visibility = Visibility.Collapsed;
        Loaded += (_, _) => { LayoutPanels(); InputBox.Focus(); };
        SizeChanged += (_, _) => LayoutPanels();
        _viewModel.InspectorRequested += (_, _) => ShowInspector(this, new RoutedEventArgs());
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ActivePanel))
            {
                _inspectorVisible = true;
                OnPanelChanged();
            }
        };
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
        ApplyBackdrop(dark);
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

    private void OnPanelChanged() => LayoutPanels();

    private void FocusComposer(object sender, RoutedEventArgs e) => InputBox.Focus();

    private void UseSuggestion(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning || sender is not Button { Tag: string text }) return;
        _viewModel.Mode = AgentMode.Autonomous;
        _viewModel.InputText = text;
        InputBox.Focus();
        InputBox.CaretIndex = text.Length;
    }

    private void ApplyBackdrop(bool dark) => WindowBackdrop.Apply(this, dark);

    private bool _navigationVisible = true;
    private bool _inspectorVisible;

    private void LayoutPanels()
    {
        if (NavigationColumn is null || InspectorColumn is null) return;
        bool showNavigation = _navigationVisible && (ActualWidth >= 920 || !_inspectorVisible);
        NavigationColumn.Width = new GridLength(showNavigation ? 210 : 0);
        NavigationPanel.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
        InspectorColumn.Width = new GridLength(_inspectorVisible ? (ActualWidth >= 920 ? 300 : 258) : 0);
        InspectorPanel.Visibility = _inspectorVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleNavigation(object sender, RoutedEventArgs e)
    {
        _navigationVisible = NavigationColumn.Width.Value == 0;
        if (_navigationVisible && ActualWidth < 920) _inspectorVisible = false;
        LayoutPanels();
    }

    private void ToggleInspector(object sender, RoutedEventArgs e)
    {
        _inspectorVisible = !_inspectorVisible;
        LayoutPanels();
    }

    private void ShowInspector(object sender, RoutedEventArgs e)
    {
        _inspectorVisible = true;
        LayoutPanels();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.N && _viewModel.NewTaskCommand.CanExecute(null))
        {
            _viewModel.NewTaskCommand.Execute(null);
            InputBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            _viewModel.ActivePanel = "Recent";
            ShowInspector(this, e);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => Keyboard.Focus(HistorySearch)));
            e.Handled = true;
        }
    }

    private void ConversationScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 28)
            ConversationScroll.ScrollToEnd();
    }

    private void RecentTasksList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RecentTasksList.SelectedItem is not AgentTask task) return;
        _viewModel.LoadTaskIntoView(task);
        e.Handled = true;
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

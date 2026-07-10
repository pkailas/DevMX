using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DevMX.App.ViewModels;

namespace DevMX.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext!;
    private readonly DevMxSettings _settings;
    private AppSession _session;

    public MainWindow()
    {
        InitializeComponent();

        // Composition root: load settings, create AppSession and dispatch delegate
        _settings = DevMxSettings.Load();
        _session = new AppSession(_settings);
        Action<Action> dispatch = (action) => Dispatcher.Invoke(action);

        var vm = new MainViewModel(_settings, _session, dispatch, theme => ThemeManager.Apply(theme));
        DataContext = vm;

        ((INotifyPropertyChanged)vm).PropertyChanged += OnViewModelPropertyChanged;
        UpdateRailVisibility();

        // Kick off async initialization (fire-and-forget with status updates)
        _ = vm.InitializeAsync();

        // Hook window close for cleanup
        Closing += OnWindowClosing;
    }

    private bool _closeCompleted;

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closeCompleted)
            return;

        // Never block the UI thread on async dispose (deadlock) - defer the close,
        // dispose on a worker with a cap, then close for real.
        e.Cancel = true;
        try
        {
            await Task.WhenAny(Task.Run(() => Vm.DisposeSessionAsync().AsTask()), Task.Delay(5000));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainWindow] Dispose error on close: {ex.Message}");
        }
        _closeCompleted = true;
        Close();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarExpanded))
        {
            UpdateRailVisibility();
        }
    }

    private void UpdateRailVisibility()
    {
        if (Vm.IsSidebarExpanded)
        {
            CollapsedRail.Visibility = Visibility.Collapsed;
            ExpandedRail.Visibility = Visibility.Visible;
        }
        else
        {
            CollapsedRail.Visibility = Visibility.Visible;
            ExpandedRail.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Handle Enter key in the chat input to send the message.
    /// </summary>
    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            Vm.Chat.SendCommand?.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Auto-scroll the chat ScrollViewer to the bottom when entries are added.
    /// </summary>
    private void ChatScrollViewer_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            // Subscribe to entry collection changes for auto-scroll
            INotifyCollectionChanged entries = Vm.Chat.Entries;
            entries.CollectionChanged += (s, args) =>
            {
                Dispatcher.BeginInvoke(new Action(() => sv.ScrollToBottom()), DispatcherPriority.Background);
            };

            sv.ScrollToBottom();
        }
    }

    /// <summary>
    /// Show delete button on hover for conversation list items.
    /// </summary>
    private void ListBoxItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem item && item.Content is Grid grid)
        {
            // The delete button visibility is handled in the DataTemplate trigger
        }
    }

    /// <summary>
    /// Handle conversation selection from the ListBox — trigger switch.
    /// </summary>
    private async void ConversationListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is ConversationItemViewModel item)
        {
            await Vm.Sidebar.SelectConversationAsync(item);
        }
    }

    /// <summary>
    /// Double-click on a conversation title begins rename.
    /// </summary>
    private void ConversationTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is TextBlock tb && tb.DataContext is ConversationItemViewModel item)
        {
            Vm.Sidebar.BeginRenameCommand.Execute(item);
            e.Handled = true;
        }
    }

    /// <summary>
    /// LostFocus on the title edit box commits the rename.
    /// </summary>
    private void TitleEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ConversationItemViewModel item)
        {
            _ = Vm.Sidebar.CommitRenameCommand.ExecuteAsync(item);
        }
    }

    /// <summary>
    /// Clear the search text when the × button is clicked.
    /// </summary>
    private void SearchClearBtn_Click(object sender, RoutedEventArgs e)
    {
        Vm.Sidebar.SearchText = string.Empty;
        SearchTextBox?.Focus();
    }
}

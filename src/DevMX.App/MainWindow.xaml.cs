using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DevMX.App.ViewModels;

namespace DevMX.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext!;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();

        ((INotifyPropertyChanged)Vm).PropertyChanged += OnViewModelPropertyChanged;
        UpdateRailVisibility();
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
}

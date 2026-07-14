using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
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

        // Route image/file pastes into chat attachments (text pastes fall through).
        DataObject.AddPastingHandler(ChatInputBox, OnChatInputPasting);

        // Wire up the folder picker for slash command /dir -b
        // The PickFolder callback is set to null in MainViewModel; override it here.
        var slashHandlerField = typeof(ChatViewModel).GetField("_slashCommandHandler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var handler = slashHandlerField.GetValue(vm.Chat) as SlashCommandHandler;
        if (handler != null)
        {
            var callbacksField = typeof(SlashCommandHandler).GetField("_callbacks",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var callbacks = callbacksField.GetValue(handler) as SlashCommandCallbacks;
            if (callbacks != null)
            {
                callbacks.PickFolder = (initialDir) =>
                {
                    try
                    {
                        var dialog = new Microsoft.Win32.OpenFolderDialog
                        {
                            InitialDirectory = Directory.Exists(initialDir) ? initialDir : Environment.GetFolderPath(Environment.SpecialFolder.MyComputer)
                        };
                        if (dialog.ShowDialog(this) == true)
                        {
                            return dialog.FolderName;
                        }
                    }
                    catch
                    {
                        // Ignore dialog errors
                    }
                    return null;
                };
            }
        }

        // Kick off async initialization (fire-and-forget with status updates)
        _ = vm.InitializeAsync();

        // Hook window close for cleanup
        Closing += OnWindowClosing;

        // Hook focus search request from ViewModel (menu / Ctrl+F)
        vm.OnRequestFocusSearch += () =>
        {
            Dispatcher.BeginInvoke(new Action(() => SearchTextBox?.Focus()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        };
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
        else if (e.PropertyName == nameof(MainViewModel.Chat))
        {
            // Reconnect swaps in a fresh ChatViewModel - re-hook auto-scroll to its Entries.
            HookChatAutoScroll();
        }
    }

    private ScrollViewer? _chatScrollViewer;
    private INotifyCollectionChanged? _hookedEntries;
    private NotifyCollectionChangedEventHandler? _entriesHandler;

    private void HookChatAutoScroll()
    {
        if (_chatScrollViewer == null)
            return;
        if (_hookedEntries != null && _entriesHandler != null)
            _hookedEntries.CollectionChanged -= _entriesHandler;

        _hookedEntries = Vm.Chat.Entries;
        _entriesHandler = (s, args) =>
        {
            Dispatcher.BeginInvoke(new Action(() => _chatScrollViewer.ScrollToBottom()), DispatcherPriority.Background);
        };
        _hookedEntries.CollectionChanged += _entriesHandler;
        _chatScrollViewer.ScrollToBottom();
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
    /// Handle Enter in the chat input: plain Enter sends, Shift+Enter inserts a newline.
    /// Preview event because the TextBox (AcceptsReturn) swallows Enter in KeyDown.
    /// </summary>
    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.None)
        {
            Vm.Chat.SendCommand?.Execute(null);
            e.Handled = true;
        }
        else if ((e.Key == Key.V && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
                 || (e.Key == Key.Insert && e.KeyboardDevice.Modifiers == ModifierKeys.Shift))
        {
            // A TextBox disables its paste command when the clipboard holds no text
            // (bitmap-only screenshots, copied files), so the Pasting event never fires.
            // Intercept those here; text-bearing clipboards fall through to normal paste.
            if (TryAttachFromClipboard())
                e.Handled = true;
        }
    }

    /// <summary>Attach clipboard files or a bitmap-only image. Returns false for text pastes.</summary>
    private bool TryAttachFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var file in Clipboard.GetFileDropList())
                {
                    if (!string.IsNullOrEmpty(file))
                        AttachFile(file);
                }
                return true;
            }
            if (!Clipboard.ContainsText() && Clipboard.ContainsImage())
            {
                AttachClipboardImage();
                return true;
            }
        }
        catch (Exception ex)
        {
            Vm.Chat.ReportAttachmentIssue($"paste failed: {ex.Message}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Intercept paste into the chat input: images and files become attachments;
    /// plain text falls through to the default paste.
    /// </summary>
    private void OnChatInputPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.DataObject.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files)
                    AttachFile(file);
            }
            e.CancelCommand();
        }
        else if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)
                 && e.DataObject.GetDataPresent(DataFormats.Bitmap))
        {
            AttachClipboardImage();
            e.CancelCommand();
        }
    }

    private void ChatInput_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void ChatInput_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var file in files)
                    AttachFile(file);
            }
            e.Handled = true;
        }
    }

    private const long MaxImageBytes = 8 * 1024 * 1024;
    private const long MaxTextFileBytes = 1 * 1024 * 1024;

    // Image formats accepted as-is by both providers; everything else WPF can decode is re-encoded to PNG.
    private static readonly Dictionary<string, string> NativeImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    private static readonly HashSet<string> ReencodableImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".tif", ".tiff", ".ico"
    };

    /// <summary>Classify a dropped/pasted file and add it as a chat attachment.</summary>
    private void AttachFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Vm.Chat.ReportAttachmentIssue($"attachment skipped: {path} not found");
                return;
            }

            string ext = info.Extension;
            if (NativeImageMediaTypes.TryGetValue(ext, out var mediaType))
            {
                if (info.Length > MaxImageBytes)
                {
                    Vm.Chat.ReportAttachmentIssue($"attachment skipped: {info.Name} exceeds {MaxImageBytes / (1024 * 1024)} MB image limit");
                    return;
                }
                var bytes = File.ReadAllBytes(path);
                Vm.Chat.AddAttachment(AttachmentViewModel.ForImage(info.Name, mediaType, Convert.ToBase64String(bytes)));
                return;
            }

            if (ReencodableImageExtensions.Contains(ext))
            {
                if (info.Length > MaxImageBytes)
                {
                    Vm.Chat.ReportAttachmentIssue($"attachment skipped: {info.Name} exceeds {MaxImageBytes / (1024 * 1024)} MB image limit");
                    return;
                }
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                Vm.Chat.AddAttachment(AttachmentViewModel.ForImage(
                    Path.ChangeExtension(info.Name, ".png"), "image/png", EncodePngBase64(frame)));
                return;
            }

            // Non-image: embed as text if it looks like text.
            if (info.Length > MaxTextFileBytes)
            {
                Vm.Chat.ReportAttachmentIssue($"attachment skipped: {info.Name} exceeds {MaxTextFileBytes / 1024} KB text file limit");
                return;
            }
            var raw = File.ReadAllBytes(path);
            if (Array.IndexOf(raw, (byte)0) >= 0)
            {
                Vm.Chat.ReportAttachmentIssue($"attachment skipped: {info.Name} is a binary file (only images and text files are supported)");
                return;
            }
            Vm.Chat.AddAttachment(AttachmentViewModel.ForTextFile(info.Name, System.Text.Encoding.UTF8.GetString(raw)));
        }
        catch (Exception ex)
        {
            Vm.Chat.ReportAttachmentIssue($"attachment failed for {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private int _clipboardImageCounter;

    /// <summary>Grab the clipboard bitmap, encode it as PNG, and attach it.</summary>
    private void AttachClipboardImage()
    {
        try
        {
            var image = Clipboard.GetImage();
            if (image == null)
            {
                Vm.Chat.ReportAttachmentIssue("paste failed: no readable image on the clipboard");
                return;
            }
            _clipboardImageCounter++;
            Vm.Chat.AddAttachment(AttachmentViewModel.ForImage(
                $"pasted-image-{_clipboardImageCounter}.png", "image/png", EncodePngBase64(image)));
        }
        catch (Exception ex)
        {
            Vm.Chat.ReportAttachmentIssue($"paste failed: {ex.Message}");
        }
    }

    private static string EncodePngBase64(System.Windows.Media.Imaging.BitmapSource image)
    {
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>
    /// Auto-scroll the chat ScrollViewer to the bottom when entries are added.
    /// </summary>
    private void ChatScrollViewer_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            _chatScrollViewer = sv;
            HookChatAutoScroll();
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
    private void RailChatBtn_Click(object sender, RoutedEventArgs e)
    {
        Vm.IsSidebarExpanded = true;
    }

    private void RailSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        Vm.IsSidebarExpanded = true;
        // Let layout run, then bring the settings section into view.
        Dispatcher.BeginInvoke(new Action(() => SettingsSection.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ===== Menu bar code-behind handlers =====

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        // Reuse the same logic as RailSettingsBtn_Click
        Vm.IsSidebarExpanded = true;
        Dispatcher.BeginInvoke(new Action(() => SettingsSection.BringIntoView()),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MenuRebuildRestart_Click(object sender, RoutedEventArgs e)
    {
        // Confirm with user
        var result = System.Windows.MessageBox.Show(
            "Rebuild from source, run tests, redeploy and restart DevMX?",
            "Rebuild && Restart",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.Yes)
            return;

        // Resolve RepoRoot by walking up from AppContext.BaseDirectory
        string? repoRoot = null;
        string dir = System.IO.Path.GetDirectoryName(AppContext.BaseDirectory)!;
        for (int i = 0; i < 20; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "DevMX.sln")))
            {
                repoRoot = dir;
                break;
            }
            var parent = System.IO.Path.GetDirectoryName(dir);
            if (parent == null) break;
            dir = parent;
        }

        if (repoRoot == null)
        {
            System.Windows.MessageBox.Show(
                "Could not locate DevMX.sln - cannot determine repository root.",
                "Rebuild && Restart",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return;
        }

        var updateScript = System.IO.Path.Combine(repoRoot, "scripts", "update.ps1");
        if (!System.IO.File.Exists(updateScript))
        {
            System.Windows.MessageBox.Show(
                $"Update script not found at: {updateScript}",
                "Rebuild && Restart",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return;
        }

        // Launch detached update script
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{updateScript}\" -AppPid {System.Environment.ProcessId} -RepoRoot \"{repoRoot}\"",
            UseShellExecute = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        System.Diagnostics.Process.Start(psi);

        // Shutdown the app - the Window.Closing handler will run the normal dispose flow
        Application.Current.Shutdown();
    }

}
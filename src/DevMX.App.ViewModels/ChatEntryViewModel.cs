using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class ChatEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private ChatEntryKind kind;

    [ObservableProperty]
    private string text = string.Empty;

    [ObservableProperty]
    private string? filePath;

    /// <summary>Children collection for ToolSummary entries (collapsed tool entries).</summary>
    public ObservableCollection<ChatEntryViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool isExpanded;

    /// <summary>True when this is a Tool entry with a clickable file path.</summary>
    public bool IsClickable => Kind == ChatEntryKind.Tool && FilePath != null;

    public ChatEntryViewModel(ChatEntryKind kind, string text, string? filePath = null)
    {
        Kind = kind;
        Text = text;
        FilePath = filePath;
    }

    /// <summary>Appends text to the current content (used for streaming assistant responses).</summary>
    public void AppendText(string chunk)
    {
        Text += chunk;
    }

    /// <summary>Replaces the current text content.</summary>
    public void SetText(string newText)
    {
        Text = newText;
    }

    /// <summary>Toggles the expanded/collapsed state for ToolSummary entries.</summary>
    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }
}

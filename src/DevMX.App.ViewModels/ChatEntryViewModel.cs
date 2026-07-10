using CommunityToolkit.Mvvm.ComponentModel;

namespace DevMX.App.ViewModels;

public partial class ChatEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private ChatEntryKind kind;

    [ObservableProperty]
    private string text = string.Empty;

    public ChatEntryViewModel(ChatEntryKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    /// <summary>Appends text to the current content (used for streaming assistant responses).</summary>
    public void AppendText(string chunk)
    {
        Text += chunk;
    }
}

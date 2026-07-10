using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ChatEntryViewModel> entries;

    [ObservableProperty]
    private string inputText = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    public ChatViewModel()
    {
        Entries = new ObservableCollection<ChatEntryViewModel>(new[]
        {
            new ChatEntryViewModel(ChatEntryKind.User, "read Program.cs and summarize"),
            new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file(Program.cs)"),
            new ChatEntryViewModel(ChatEntryKind.Assistant, "This project is a C# solution containing a core library (DevMX.Core) with an agentic loop, MCP client, and chat provider implementations for Anthropic and OpenAI-compatible endpoints."),
        });
    }

    [RelayCommand]
    private void Send()
    {
        if (string.IsNullOrWhiteSpace(InputText))
            return;

        Entries.Add(new ChatEntryViewModel(ChatEntryKind.User, InputText));
        InputText = string.Empty;
    }
}

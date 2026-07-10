using DevMX.App.ViewModels;

namespace DevMX.App.ViewModels.Tests;

public class ChatEntryViewModelTests
{
    [Fact]
    public void Constructor_SetsKindAndText()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.User, "Hello");
        Assert.Equal(ChatEntryKind.User, entry.Kind);
        Assert.Equal("Hello", entry.Text);
    }

    [Fact]
    public void FilePath_SetOnConstruction()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file", "/path/to/file.cs");
        Assert.Equal("/path/to/file.cs", entry.FilePath);
    }

    [Fact]
    public void IsClickable_True_ForToolWithFilePath()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] read_file", "/path/to/file.cs");
        Assert.True(entry.IsClickable);
    }

    [Fact]
    public void IsClickable_False_ForToolWithoutFilePath()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Tool, "[tool] some_tool");
        Assert.False(entry.IsClickable);
    }

    [Fact]
    public void IsClickable_False_ForNonToolKind()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.User, "Hello", "/some/path");
        Assert.False(entry.IsClickable);
    }

    [Fact]
    public void AppendText_AppendsToText()
    {
        var entry = new ChatEntryViewModel(ChatEntryKind.Assistant, "Hello");
        entry.AppendText(" World");
        Assert.Equal("Hello World", entry.Text);
    }
}

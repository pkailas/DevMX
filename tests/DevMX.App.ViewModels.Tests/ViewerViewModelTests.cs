using DevMX.App.ViewModels;

namespace DevMX.App.ViewModels.Tests;

public class ViewerViewModelTests
{
    [Fact]
    public void Constructor_NoPlaceholderTabs()
    {
        var vm = new ViewerViewModel();
        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }

    [Fact]
    public void OpenFileTab_AddsTab()
    {
        var vm = new ViewerViewModel();
        vm.OpenFileTab("test.cs", "using System;", ".cs");

        Assert.Single(vm.Tabs);
        var tab = vm.Tabs[0];
        Assert.Equal("test.cs", tab.Title);
        Assert.Equal("using System;", tab.Content);
        Assert.Equal(ViewerTabKind.File, tab.Kind);
        Assert.Equal(".cs", tab.FileExtension);
        Assert.Same(tab, vm.SelectedTab);
    }

    [Fact]
    public void OpenFileTab_DedupeByTitle()
    {
        var vm = new ViewerViewModel();
        vm.OpenFileTab("test.cs", "v1", ".cs");
        vm.OpenFileTab("test.cs", "v2", ".cs");

        Assert.Single(vm.Tabs);
        Assert.Equal("v2", vm.Tabs[0].Content);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
    }

    [Fact]
    public void OpenDiffTab_AddsDiffTab()
    {
        var vm = new ViewerViewModel();
        var diffText = "--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n";
        vm.OpenDiffTab("diff: x.cs", diffText);

        Assert.Single(vm.Tabs);
        var tab = vm.Tabs[0];
        Assert.Equal("diff: x.cs", tab.Title);
        Assert.Equal(diffText, tab.Content);
        Assert.Equal(ViewerTabKind.Diff, tab.Kind);
        Assert.Same(tab, vm.SelectedTab);
    }

    [Fact]
    public void OpenDiffTab_DedupeByTitle()
    {
        var vm = new ViewerViewModel();
        vm.OpenDiffTab("diff: x.cs", "diff v1");
        vm.OpenDiffTab("diff: x.cs", "diff v2");

        Assert.Single(vm.Tabs);
        Assert.Equal("diff v2", vm.Tabs[0].Content);
        Assert.Equal(ViewerTabKind.Diff, vm.Tabs[0].Kind);
    }

    [Fact]
    public void CloseTab_RemovesTab()
    {
        var vm = new ViewerViewModel();
        vm.OpenFileTab("a.cs", "content a", ".cs");
        vm.OpenFileTab("b.cs", "content b", ".cs");

        vm.CloseTabCommand.Execute(vm.Tabs[0]);

        Assert.Single(vm.Tabs);
        Assert.Equal("b.cs", vm.Tabs[0].Title);
        Assert.Same(vm.Tabs[0], vm.SelectedTab);
    }

    [Fact]
    public void CloseLastTab_ClearsSelection()
    {
        var vm = new ViewerViewModel();
        vm.OpenFileTab("only.cs", "content", ".cs");

        vm.CloseTabCommand.Execute(vm.Tabs[0]);

        Assert.Empty(vm.Tabs);
        Assert.Null(vm.SelectedTab);
    }
}

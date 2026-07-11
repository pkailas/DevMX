using DevMX.App.ViewModels;
using Xunit;

namespace DevMX.App.ViewModels.Tests;

public class ChatViewModelTests
{
    private void TaskCache(Action action) => action();

    [Fact]
    public void StopCommand_CanExecute_TracksIsBusy()
    {
        // Arrange
        var session = new AppSession(DevMxSettings.Load());
        var vm = new ChatViewModel(session, TaskCache);

        // Assert: when not busy, StopCommand cannot execute
        Assert.False(vm.StopCommand.CanExecute(null));

        // Simulate busy state
        vm.IsBusy = true;

        // Assert: when busy, StopCommand can execute
        Assert.True(vm.StopCommand.CanExecute(null));

        // Simulate not busy again
        vm.IsBusy = false;

        // Assert: StopCommand cannot execute again
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public void StopCommand_InvokesCancel_OnExecute()
    {
        // Arrange — we verify that calling StopCommand does not crash
        // and that the command is properly wired
        var session = new AppSession(DevMxSettings.Load());
        var vm = new ChatViewModel(session, TaskCache);
        vm.IsBusy = true;

        // Act — calling Stop should not throw
        var ex = Record.Exception(() => vm.StopCommand.Execute(null));

        // Assert
        Assert.Null(ex);
    }

    [Fact]
    public void SendCommand_CanExecute_False_WhenBusy()
    {
        // Arrange
        var session = new AppSession(DevMxSettings.Load());
        var vm = new ChatViewModel(session, TaskCache);
        vm.IsInitialized = true;
        vm.InputText = "hello";

        // Assert: can send when not busy
        Assert.True(vm.SendCommand.CanExecute(null));

        // Simulate busy
        vm.IsBusy = true;

        // Assert: cannot send when busy
        Assert.False(vm.SendCommand.CanExecute(null));
    }
}

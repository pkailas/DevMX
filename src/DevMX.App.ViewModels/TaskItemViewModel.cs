using CommunityToolkit.Mvvm.ComponentModel;

namespace DevMX.App.ViewModels;

public partial class TaskItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string jobId = string.Empty;

    [ObservableProperty]
    private string state = string.Empty;

    [ObservableProperty]
    private string detail = string.Empty;

    public TaskItemViewModel(string jobId, string state, string detail)
    {
        JobId = jobId;
        State = state;
        Detail = detail;
    }
}

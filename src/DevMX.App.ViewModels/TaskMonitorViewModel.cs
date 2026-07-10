using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace DevMX.App.ViewModels;

public partial class TaskMonitorViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<TaskItemViewModel> tasks;

    [ObservableProperty]
    private string journalText = string.Empty;

    public TaskMonitorViewModel()
    {
        Tasks = new ObservableCollection<TaskItemViewModel>(new[]
        {
            new TaskItemViewModel("job-1", "done", "create p0-probe.txt"),
        });

        JournalText = "[2025-01-01 00:00:00] Task monitor initialized.\n[2025-01-01 00:00:01] job-1 completed: create p0-probe.txt";
    }
}

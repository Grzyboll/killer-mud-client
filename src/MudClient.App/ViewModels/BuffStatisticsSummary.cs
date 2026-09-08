using CommunityToolkit.Mvvm.ComponentModel;

namespace MudClient.App.ViewModels;

public sealed partial class BuffStatisticsSummary : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    [ObservableProperty] private string _estimate = "—";
    [ObservableProperty] private string _remaining = "—";
    [ObservableProperty] private string _confidence = "—";
    [ObservableProperty] private int _sessionUses;
    [ObservableProperty] private int _historyUses;
    [ObservableProperty] private int _sampleCount;
}

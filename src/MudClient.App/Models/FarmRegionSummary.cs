using MudClient.Core.Map;

namespace MudClient.App.Models;

/// <summary>One row in the "Obszary farmy" list (see <see cref="ViewModels.MapViewModel.AutoFarmRegionSummaries"/>)
/// — a drawn region paired with a human-readable description, so each can carry its own "usuń"
/// button without the reader having to decode raw coordinates.</summary>
public sealed record FarmRegionSummary(FarmRegion Region, string Description);

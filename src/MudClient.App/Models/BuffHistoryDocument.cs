using MudClient.Core.BuffTimers;

namespace MudClient.App.Models;

public sealed class BuffHistoryDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public BuffCharacterKey? Character { get; set; }

    public List<BuffMeasurement> Measurements { get; set; } = [];

    public List<ActiveBuffCheckpoint> ActiveCheckpoints { get; set; } = [];

    public Dictionary<string, int> CastCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

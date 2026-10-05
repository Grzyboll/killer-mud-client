namespace MudClient.App.Models;

/// <summary>One effect in the compact Efekty line. <see cref="Text"/> already carries its trailing
/// ", " separator (none on the last one), so the line reads as a comma-separated list while still
/// wrapping as a flowing run of pieces; <see cref="Ending"/> drives the same pulse as a full row.</summary>
public sealed record EffectLineItem(string Text, bool Ending);

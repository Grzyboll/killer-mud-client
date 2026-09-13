using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudClient.Core.Equipment;
using MudClient.Core.Text;

namespace MudClient.App.ViewModels;

public sealed partial class EquipmentInventoryViewModel : ObservableObject
{
    public ObservableCollection<EquipmentInventoryRow> Equipment { get; } = [];
    public ObservableCollection<EquipmentInventoryRow> Inventory { get; } = [];
    public ObservableCollection<TattooItem> Tattoos { get; } = [];
    public ObservableCollection<EquipmentBonusSummaryRow> BonusSummary { get; } = [];
    [ObservableProperty] private string _statusText = "Oczekiwanie na pierwszy odczyt.";
    [ObservableProperty] private string _equipmentDisplayText = string.Empty;
    [ObservableProperty] private string _inventoryDisplayText = string.Empty;
    [ObservableProperty] private string _bonusSummaryText = "Brak opisów examine dla założonych przedmiotów.";

    public void Apply(EquipmentInventorySnapshot snapshot, IReadOnlyDictionary<string, string> descriptions, IReadOnlyList<TattooItem> tattoos)
    {
        Replace(Equipment, snapshot.Equipment.Select((item, index) => new EquipmentInventoryRow(
            item.Location, item.Name, descriptions.GetValueOrDefault($"E:{index}"),
            EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, item.Name, false, index),
            EquipmentInventorySnapshotParser.GetDurabilityPercent(item.Name))));
        Replace(Inventory, snapshot.Inventory.Select((item, index) => new EquipmentInventoryRow(
            "", item.Name, descriptions.GetValueOrDefault($"I:{index}"),
            EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, item.Name, true, index),
            EquipmentInventorySnapshotParser.GetDurabilityPercent(item.Name))));
        Replace(Tattoos, tattoos);
        StatusText = $"Ostatni pełny odczyt: ekwipunek {Equipment.Count}, inventory {Inventory.Count}.";
        EquipmentDisplayText = string.Join(Environment.NewLine, Equipment.Select(row => $"{row.Location,-34} {row.Name}"));
        InventoryDisplayText = string.Join(Environment.NewLine, Inventory.Select(row => row.Name));
        var describedItems = Equipment.Where(row => row.ExamineDescription is not null)
            .Select(row => (AnsiText.StripKillerColors(AnsiText.StripAnsi(row.Name)), row.ExamineDescription!)).ToArray();
        var describedCombatItems = Equipment.Where(row => row.ExamineDescription is not null)
            .Select(row => (row.Location, AnsiText.StripKillerColors(AnsiText.StripAnsi(row.Name)), row.ExamineDescription!)).ToArray();
        var describedTattooItems = tattoos.Select(tattoo => (tattoo.Name, tattoo.Description));
        var allDescribedItems = describedItems.Concat(describedTattooItems).ToArray();
        var summary = EquipmentBonusSummary.SummarizeWithSources(allDescribedItems);
        var staticEffects = EquipmentBonusSummary.SummarizeStaticEffectsWithSources(allDescribedItems);
        var combatItems = EquipmentBonusSummary.SummarizeCombatItems(describedCombatItems);
        BonusSummary.Clear();
        string? previousCategory = null;
        foreach (var combatItem in combatItems)
        {
            var category = string.Equals(combatItem.Category, previousCategory, StringComparison.Ordinal) ? null : combatItem.Category;
            var grip = combatItem.Grip is null ? string.Empty : $" — {combatItem.Grip}";
            var stats = combatItem.Stats.Count == 0 ? "brak rozpoznanych statystyk examine" : string.Join("; ", combatItem.Stats);
            BonusSummary.Add(new EquipmentBonusSummaryRow($"{combatItem.Location}: {combatItem.Item}{grip} — {stats}", combatItem.Item, category));
            previousCategory = combatItem.Category;
        }
        foreach (var total in summary)
        {
            var category = string.Equals(total.Category, previousCategory, StringComparison.Ordinal) ? null : total.Category;
            BonusSummary.Add(new EquipmentBonusSummaryRow($"{total.Name}: {total.Value:+#;-#;0}", string.Join(Environment.NewLine, total.Contributions.Select(item => $"{item.Item}: {item.Value:+#;-#;0}")), category));
            previousCategory = total.Category;
        }
        foreach (var effect in staticEffects)
        {
            var category = string.Equals("Stałe efekty", previousCategory, StringComparison.Ordinal) ? null : "Stałe efekty";
            BonusSummary.Add(new EquipmentBonusSummaryRow(effect.Name, string.Join(Environment.NewLine, effect.Contributions.Select(item => item.Item)), category));
            previousCategory = "Stałe efekty";
        }
        BonusSummaryText = summary.Count == 0 && staticEffects.Count == 0 && combatItems.Count == 0 ? "Brak rozpoznanych bonusów lub stałych efektów w opisach examine." : string.Empty;
    }

    public void Reset()
    {
        Equipment.Clear();
        Inventory.Clear();
        Tattoos.Clear();
        BonusSummary.Clear();
        StatusText = "Brak odczytu dla aktualnej postaci.";
        EquipmentDisplayText = string.Empty;
        InventoryDisplayText = string.Empty;
        BonusSummaryText = "Brak opisów examine dla aktualnej postaci.";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    { target.Clear(); foreach (var row in rows) target.Add(row); }
}
/// <summary>One panel row. Menu actions only prepare a visible, editable command in the command bar;
/// they never infer that an item supports a server-side action.</summary>
public sealed record EquipmentInventoryRow(string Location, string Name, string? ExamineDescription, ItemCommandReference CommandReference, int? DurabilityPercent)
{
    public bool IsLowDurability => DurabilityPercent is < 30;
    public string DurabilityWarningText => IsLowDurability ? $"⚠ {DurabilityPercent}%" : string.Empty;
}
public sealed record EquipmentBonusSummaryRow(string Text, string Sources, string? Category)
{
    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);
}

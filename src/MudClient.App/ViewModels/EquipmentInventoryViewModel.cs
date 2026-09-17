using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudClient.Core.Equipment;
using MudClient.Core.Text;

namespace MudClient.App.ViewModels;

public sealed partial class EquipmentInventoryViewModel : ObservableObject
{
    public ObservableCollection<EquipmentInventoryRow> Equipment { get; } = [];
    public ObservableCollection<EquipmentInventoryRow> Inventory { get; } = [];
    public ObservableCollection<EquipmentInventoryRow> GroundItems { get; } = [];
    public ObservableCollection<TattooItem> Tattoos { get; } = [];
    public ObservableCollection<EquipmentBonusSummaryRow> BonusSummary { get; } = [];
    [ObservableProperty] private string _statusText = "Oczekiwanie na pierwszy odczyt.";
    [ObservableProperty] private string _equipmentDisplayText = string.Empty;
    [ObservableProperty] private string _inventoryDisplayText = string.Empty;
    [ObservableProperty] private string _bonusSummaryText = "Brak opisów examine dla założonych przedmiotów.";

    public void Apply(EquipmentInventorySnapshot snapshot, IReadOnlyDictionary<string, string> descriptions, IReadOnlyDictionary<string, IReadOnlyList<InventoryItem>> containerContents, IReadOnlyDictionary<string, ContainerAccessState> containerAccessStates, IReadOnlyList<TattooItem> tattoos, IReadOnlyDictionary<string, string>? rareCategories = null)
    {
        Replace(Equipment, snapshot.Equipment.Select((item, index) => new EquipmentInventoryRow(
            item.Location, item.Name, descriptions.GetValueOrDefault($"E:{index}"),
            EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, item.Name, false, index),
            EquipmentInventorySnapshotParser.GetDurabilityPercent(item.Name))));
        var inventoryNames = snapshot.Inventory.Select(item => item.Name).ToArray();
        var groundNames = snapshot.GroundItems.Select(item => item.Name).ToArray();
        Replace(Inventory, snapshot.Inventory.Select((item, index) =>
        {
            var key = $"I:{index}";
            var isContainer = containerContents.TryGetValue(key, out var detectedContents);
            var contents = detectedContents ?? [];
            var contentRows = isContainer
                ? contents.Select((content, contentIndex) => new ContainerInventoryItem(
                    content.Name,
                    EquipmentInventorySnapshotParser.ResolveItemCommandReference(new EquipmentInventorySnapshot([], contents), content.Name, true, contentIndex),
                    RandomItemNameCatalog.GetPolishSlotLabel(content.Name))).ToArray()
                : [];
            return new EquipmentInventoryRow(
                "", item.Name, descriptions.GetValueOrDefault(key),
                EquipmentInventorySnapshotParser.ResolveItemCommandReference(snapshot, item.Name, true, index),
                EquipmentInventorySnapshotParser.GetDurabilityPercent(item.Name), isContainer, contentRows,
                ContainerAccessState: containerAccessStates.GetValueOrDefault(key),
                RandomSlotLabel: RandomItemNameCatalog.GetPolishSlotLabel(item.Name),
                BulkActions: RandomItemNameCatalog.GetBulkGroups(inventoryNames, item.Name),
                ContainerBulkActions: RandomItemNameCatalog.GetBulkGroups(contents.Select(content => content.Name)));
        }));
        Replace(GroundItems, snapshot.GroundItems.Select((item, index) =>
        {
            var key = $"G:{index}";
            var isContainer = containerContents.TryGetValue(key, out var detectedContents);
            var contents = detectedContents ?? [];
            var contentRows = isContainer
                ? contents.Select((content, contentIndex) => new ContainerInventoryItem(
                    content.Name,
                    EquipmentInventorySnapshotParser.ResolveItemCommandReference(new EquipmentInventorySnapshot([], contents), content.Name, true, contentIndex),
                    RandomItemNameCatalog.GetPolishSlotLabel(content.Name))).ToArray()
                : [];
            return new EquipmentInventoryRow(
                "", item.Name, descriptions.GetValueOrDefault(key),
                EquipmentInventorySnapshotParser.ResolveGroundItemCommandReference(snapshot, item.Name, index),
                EquipmentInventorySnapshotParser.GetDurabilityPercent(item.Name), isContainer, contentRows,
                EquipmentInventorySnapshotParser.IsPotentialGroundContainer(item.Name), GetRareLabel(item.Name, rareCategories),
                EquipmentInventorySnapshotParser.IsGroundCorpse(item.Name),
                containerAccessStates.GetValueOrDefault(key),
                RandomItemNameCatalog.GetPolishSlotLabel(item.Name),
                RandomItemNameCatalog.GetBulkGroups(groundNames, item.Name),
                RandomItemNameCatalog.GetBulkGroups(contents.Select(content => content.Name)));
        }));
        Replace(Tattoos, tattoos);
        StatusText = $"Ostatni pełny odczyt: ekwipunek {Equipment.Count}, inventory {Inventory.Count}, pokój {GroundItems.Count}.";
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
        GroundItems.Clear();
        Tattoos.Clear();
        BonusSummary.Clear();
        StatusText = "Brak odczytu dla aktualnej postaci.";
        EquipmentDisplayText = string.Empty;
        InventoryDisplayText = string.Empty;
        BonusSummaryText = "Brak opisów examine dla aktualnej postaci.";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    { target.Clear(); foreach (var row in rows) target.Add(row); }
    private static string GetRareLabel(string name, IReadOnlyDictionary<string, string>? categories) => categories is not null && categories.TryGetValue(EquipmentInventorySnapshotParser.GetPlainItemName(name), out var category) ? $"({category.ToUpperInvariant()})" : string.Empty;
}
/// <summary>One panel row. Menu actions only prepare a visible, editable command in the command bar;
/// they never infer that an item supports a server-side action.</summary>
public sealed record EquipmentInventoryRow(string Location, string Name, string? ExamineDescription, ItemCommandReference CommandReference, int? DurabilityPercent, bool IsIdentifiedContainer = false, IReadOnlyList<ContainerInventoryItem>? ContainerContents = null, bool IsPotentialContainer = false, string RareLabel = "", bool IsCorpseContainer = false, ContainerAccessState ContainerAccessState = ContainerAccessState.None, string RandomSlotLabel = "", IReadOnlyList<ItemBulkGroup>? BulkActions = null, IReadOnlyList<ItemBulkGroup>? ContainerBulkActions = null)
{
    public bool IsLowDurability => DurabilityPercent is < 30;
    public string DurabilityWarningText => IsLowDurability ? $"⚠ {DurabilityPercent}%" : string.Empty;
    public string RandomSlotDisplayText => RandomSlotLabel.Length > 0 ? $"({RandomSlotLabel})" : string.Empty;
    public bool HasRandomSlotLabel => RandomSlotLabel.Length > 0;
    public string ContainerLabel => IsIdentifiedContainer ? "(kontener)" : string.Empty;
    public string ContainerAccessLabel => ContainerAccessState switch
    {
        ContainerAccessState.Open => "(otwarte)",
        ContainerAccessState.Closed => "(zamkniete)",
        ContainerAccessState.Locked => "(zamkniety na klucz)",
        ContainerAccessState.MissingKey => "(brak klucza)",
        _ => string.Empty
    };
    public bool CanOpenContainer => ContainerAccessState == ContainerAccessState.Closed;
    public bool CanUnlockContainer => ContainerAccessState == ContainerAccessState.Locked;
    public bool CanCloseContainer => !IsCorpseContainer && ContainerAccessState == ContainerAccessState.Open;
    public bool CanLockContainer => !IsCorpseContainer && ContainerAccessState == ContainerAccessState.Closed;
    public IReadOnlyList<ContainerInventoryItem> ContainerContentsOrEmpty => ContainerContents ?? [];
    public IReadOnlyList<ItemBulkGroup> BulkActionsOrEmpty => BulkActions ?? [];
    public IReadOnlyList<ItemBulkGroup> ContainerBulkActionsOrEmpty => ContainerBulkActions ?? [];
    public bool HasBulkActions => BulkActionsOrEmpty.Count > 0;
    public bool HasContainerBulkActions => ContainerBulkActionsOrEmpty.Count > 0;
    public bool IsWaterSource => EquipmentInventorySnapshotParser.IsGroundWaterSource(Name);
    public string WaterSourceCommandArgument => EquipmentInventorySnapshotParser.GetGroundWaterSourceCommandTarget(Name);
    public string MenuDisplayName => AnsiText.StripKillerColors(AnsiText.StripAnsi(Name)).Trim();
}
public sealed record ContainerInventoryItem(string Name, ItemCommandReference CommandReference, string RandomSlotLabel = "")
{
    public string MenuDisplayName
    {
        get
        {
            var name = AnsiText.StripKillerColors(AnsiText.StripAnsi(Name)).Trim();
            return RandomSlotLabel.Length > 0 ? $"{name} ({RandomSlotLabel})" : name;
        }
    }
}
public sealed record EquipmentBonusSummaryRow(string Text, string Sources, string? Category)
{
    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);
}

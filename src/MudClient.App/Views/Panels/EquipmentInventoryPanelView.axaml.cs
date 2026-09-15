using Avalonia.Controls;
using Avalonia.Interactivity;
using MudClient.App.ViewModels;
using MudClient.Core.Equipment;
using MudClient.Core.Gmcp;

namespace MudClient.App.Views.Panels;

public sealed partial class EquipmentInventoryPanelView : UserControl
{
    public EquipmentInventoryPanelView() => InitializeComponent();

    private void GiveInventoryItem_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: EquipmentInventoryRow item }
            || eventArgs.Source is not MenuItem { DataContext: RoomPerson recipient }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.GiveInventoryItem(item, recipient);
    }

    private void PutInventoryItem_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: EquipmentInventoryRow item }
            || eventArgs.Source is not MenuItem { DataContext: EquipmentInventoryRow container }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.PutInventoryItemIntoContainer(item, container);
    }

    private void PutInventoryItemGroup_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: ItemBulkGroup group }
            || eventArgs.Source is not MenuItem { DataContext: EquipmentInventoryRow container }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.PutInventoryGroupIntoContainer(group, container);
    }

    private void TakeContainerItem_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: EquipmentInventoryRow container }
            || eventArgs.Source is not MenuItem { DataContext: ContainerInventoryItem item }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.TakeContainerItem(container, item);
    }

    private void TakeContainerItemGroup_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: EquipmentInventoryRow container }
            || eventArgs.Source is not MenuItem { DataContext: ItemBulkGroup group }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.TakeContainerItemGroup(container, group);
    }

    private void TakeGroundContainerItem_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: EquipmentInventoryRow container }
            || eventArgs.Source is not MenuItem { DataContext: ContainerInventoryItem item }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.TakeGroundContainerItem(container, item);
    }

    private void TakeGroundContainerItemGroup_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem { Tag: EquipmentInventoryRow container }
            || eventArgs.Source is not MenuItem { DataContext: ItemBulkGroup group }
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.TakeGroundContainerItemGroup(container, group);
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using MudClient.App.Models;
using MudClient.App.ViewModels;

namespace MudClient.App.Views.Panels;

public sealed partial class MemSpellsPanelView : UserControl
{
    private BuffWatchEntry? _dragSource;
    private PointerPressedEventArgs? _dragStartEvent;
    private Avalonia.Point _dragStart;
    private bool _dragInProgress;

    public MemSpellsPanelView()
    {
        InitializeComponent();
    }

    // ========================================================================
    // Buff reorder — drag the "⠿" handle to move a watched buff within its set.
    // Same DragDrop.DoDragDropAsync + DragOver/Drop pattern as FolderTreeView,
    // just for a flat list instead of a tree: _dragSource/_dragStartEvent are
    // captured on the handle's PointerPressed, the actual drag starts once the
    // pointer has moved past a small threshold, and the drop target's own
    // DataContext (the BuffWatchEntry under the pointer) is where it lands.
    // ========================================================================

    private void BuffDragHandle_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: BuffWatchEntry entry } &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragSource = entry;
            _dragStartEvent = e;
            _dragStart = e.GetPosition(this);
        }
    }

    private async void BuffsItemsControl_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragSource is null || _dragStartEvent is null || _dragInProgress ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < 4 && Math.Abs(current.Y - _dragStart.Y) < 4)
        {
            return;
        }

        _dragInProgress = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText("KillerMudClient/BuffWatchEntry"));
            await DragDrop.DoDragDropAsync(_dragStartEvent, data, DragDropEffects.Move);
        }
        finally
        {
            _dragInProgress = false;
            _dragSource = null;
            _dragStartEvent = null;
        }
    }

    private void BuffRow_OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _dragSource is not null && sender is Control { DataContext: BuffWatchEntry }
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void BuffRow_OnDrop(object? sender, DragEventArgs e)
    {
        if (_dragSource is not { } source ||
            sender is not Control { DataContext: BuffWatchEntry target } ||
            ReferenceEquals(source, target) ||
            DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var newIndex = viewModel.RequiredBuffs.IndexOf(target);
        if (newIndex >= 0)
        {
            viewModel.MoveBuff(source, newIndex);
        }

        e.Handled = true;
    }
}

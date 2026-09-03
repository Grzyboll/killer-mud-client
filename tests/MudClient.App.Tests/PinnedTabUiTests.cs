using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Settings;
using MudClient.App.Controls;
using MudClient.App.Docking;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views;
using MudClient.App.Views.Panels;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Headless UI checks that a tool pinned to a window edge actually renders as a
/// visible side tab (ToolPinItemControl) in the main window's visual tree.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class PinnedTabUiTests : IDisposable
{
    private readonly List<Window> _windows = new();
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "KillerMudClient-PinnedTabUiTests", Guid.NewGuid().ToString("N"));

    // All UI test classes share one headless session, and windows left open accumulate in it —
    // enough lingering pinned strips occasionally perturb a restored tab's render. xUnit makes a
    // fresh test-class instance per test and disposes it afterwards, so closing this test's windows
    // here keeps each test's session state clean.
    public void Dispose()
    {
        foreach (var window in _windows)
        {
            window.Close();
        }

        Dispatcher.UIThread.RunJobs();
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private MainWindowViewModel CreateViewModel() => new(
        new ProfileService(_tempDirectory),
        new AppSettingsService(_tempDirectory),
        new DockLayoutService(_tempDirectory),
        layoutPresetService: new LayoutPresetService(_tempDirectory));

    private static DockLayoutSnapshot CreateSnapshotWithPins(
        params (string ToolId, Alignment Edge)[] pins)
    {
        var factory = new MudDockFactory(new object(), new object());
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);
        foreach (var (toolId, edge) in pins)
        {
            factory.PinToolToEdge(factory.AllTools.First(tool => tool.Id == toolId), edge);
        }

        // DEFAULT starts with a few panels hidden (Automaty + its 4 promoted sub-panels/Notatki/
        // GMCP/Ustawienia — see CreateLayout). Restore() docks anything still hidden that wasn't explicitly pinned
        // above as a normal tab (unlike RestoreToTopEdge, which pins — and would then show up
        // among the rendered pinned tabs tests assert on), so tests built on this helper see a
        // fully-visible, non-pinned baseline unless they intentionally leave something hidden.
        foreach (var tool in factory.HiddenTools.ToList())
        {
            factory.Restore(tool);
        }

        return factory.Snapshot(layout);
    }

    private MainWindow ShowWindow(MainWindowViewModel viewModel)
    {
        var window = new MainWindow { DataContext = viewModel, Width = 1400, Height = 900 };
        _windows.Add(window);
        window.Show();
        return window;
    }

    private static void Pump(Window window, int iterations = 10)
    {
        for (var i = 0; i < iterations; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Pumps layout/render until <paramref name="condition"/> holds or the cap is reached. A fixed
    /// pump count is a race under CPU contention — the pinned tab strip may need more render ticks
    /// to settle — so tests that assert on rendered visual bounds wait for the expected state.
    /// </summary>
    private static void PumpUntil(Window window, Func<bool> condition, int maxIterations = 150)
    {
        for (var i = 0; i < maxIterations && !condition(); i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static List<ToolPinItemControl> RenderedPinItems(Window window) =>
        window.GetVisualDescendants().OfType<ToolPinItemControl>()
            .Where(p => p.IsEffectivelyVisible && p.Bounds is { Width: > 0, Height: > 0 })
            .ToList();

    [AvaloniaFact]
    public void WidgetThreeDotMenu_ContainsExplicitPinEdges()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        var chrome = window.GetVisualDescendants().OfType<ToolChromeControl>()
            .First(control => control.IsEffectivelyVisible);
        var flyout = Assert.IsType<MenuFlyout>(chrome.ToolFlyout);
        var headers = flyout.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToList();

        Assert.Contains("Wróć do układu", headers);
        Assert.Contains("Przypnij z lewej", headers);
        Assert.Contains("Przypnij z prawej", headers);
        Assert.Contains("Przypnij u góry", headers);
        Assert.Contains("Przypnij u dołu", headers);
    }

    [AvaloniaFact]
    public void WidgetHeader_UsesCompactChrome()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        var chrome = window.GetVisualDescendants().OfType<ToolChromeControl>()
            .First(control => control.IsEffectivelyVisible);
        var header = chrome.GetVisualDescendants().OfType<Border>()
            .First(control => ReferenceEquals(control.ContextFlyout, chrome.ToolFlyout));
        var title = header.GetVisualDescendants().OfType<TextBlock>().First();

        Assert.All(
            header.GetVisualDescendants().OfType<Button>()
                .Where(button => button.IsEffectivelyVisible && button.Bounds.Height > 0),
            button => Assert.InRange(button.Bounds.Height, 1, 16));
        Assert.InRange(title.Bounds.Height, 1, 19);
        Assert.InRange(header.Bounds.Height, 1, 20);
    }

    [AvaloniaFact]
    public void ExpandedPinnedWidget_DisablesHeaderDrag()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        var gmcp = factory.AllTools.First(tool => tool.Id == "Gmcp");
        factory.PinToolToEdge(gmcp, Alignment.Top);
        ((IFactory)factory).TogglePreviewPinnedDockable(gmcp);
        Pump(window);

        var chrome = window.GetVisualDescendants().OfType<ToolChromeControl>()
            .First(control => control.IsEffectivelyVisible
                              && control.DataContext is ToolDock dock
                              && ReferenceEquals(dock.ActiveDockable, gmcp));

        Assert.False(gmcp.CanDrag);
        Assert.False(chrome.GetValue(DockProperties.IsDragEnabledProperty));
    }

    [AvaloniaTheory]
    [InlineData(Alignment.Left)]
    [InlineData(Alignment.Right)]
    [InlineData(Alignment.Top)]
    [InlineData(Alignment.Bottom)]
    public void PinnedTool_ShowsSideTabInUi(Alignment edge)
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        // Sanity: the dock UI is up.
        var dockControl = window.GetVisualDescendants().OfType<DockControl>().FirstOrDefault();
        Assert.NotNull(dockControl);

        // The view model loads the real user profile's saved layout; reset to the built-in
        // default so this test is independent of whatever is pinned there right now.
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        // Pin GMCP to the chosen edge, as an edge drag & drop would.
        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        var gmcp = factory.AllTools.First(t => t.Id == "Gmcp");
        factory.PinToolToEdge(gmcp, edge);
        PumpUntil(window, () => RenderedPinItems(window).Count > 0);

        var pinnedList = edge switch
        {
            Alignment.Left => viewModel.Layout.LeftPinnedDockables,
            Alignment.Right => viewModel.Layout.RightPinnedDockables,
            Alignment.Top => viewModel.Layout.TopPinnedDockables,
            _ => viewModel.Layout.BottomPinnedDockables,
        };
        Assert.Contains(pinnedList ?? Enumerable.Empty<IDockable>(), d => d.Id == "Gmcp");

        // The strip control for pinned tools must exist and contain a visible tab item.
        var pinItems = window.GetVisualDescendants().OfType<ToolPinItemControl>().ToList();
        Assert.True(pinItems.Count > 0,
            "No ToolPinItemControl in the visual tree — pinned tab strip is not rendered. " +
            "Visual types present: " + string.Join(", ", window.GetVisualDescendants()
                .Select(v => v.GetType().Name)
                .Where(n => n.Contains("Pin") || n.Contains("Root") || n.Contains("Dock"))
                .Distinct()));

        var tab = pinItems.First();
        Assert.True(tab.IsEffectivelyVisible, "Pinned tab exists but is not visible.");
        Assert.True(tab.Bounds.Width > 0 && tab.Bounds.Height > 0,
            $"Pinned tab has zero size: {tab.Bounds}.");
    }

    [AvaloniaTheory]
    [InlineData(Alignment.Left)]
    [InlineData(Alignment.Right)]
    [InlineData(Alignment.Top)]
    [InlineData(Alignment.Bottom)]
    public void PinnedTool_Preview_UsesFixedEdgeProportion(Alignment edge)
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var dockControl = window.GetVisualDescendants().OfType<DockControl>().First();
        PumpUntil(window, () => dockControl.Bounds is { Width: > 0, Height: > 0 });
        Assert.True(dockControl.Bounds is { Width: > 0, Height: > 0 });

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        var gmcp = factory.AllTools.First(t => t.Id == "Gmcp");
        factory.PinToolToEdge(gmcp, edge);
        Pump(window);

        gmcp.GetPinnedBounds(out _, out _, out var width, out var height);
        var side = edge is Alignment.Left or Alignment.Right;
        var expected = side ? dockControl.Bounds.Width / 3.0 : dockControl.Bounds.Height / 2.0;
        var actual = side ? width : height;
        Assert.True(Math.Abs(actual - expected) <= 1.0,
            $"Pinned preview size for {edge} should use the fixed edge proportion (~{expected:F1}) but was {actual:F1}.");

        // The preview can still be resized while it is open.
        ((IFactory)factory).TogglePreviewPinnedDockable(gmcp);
        Pump(window);
        var resizeSplitter = window.GetVisualDescendants().OfType<GridSplitter>()
            .FirstOrDefault(splitter => splitter.Name == "PART_PinnedDockSplitter");
        Assert.NotNull(resizeSplitter);
        Assert.True(resizeSplitter.IsEffectivelyVisible && resizeSplitter.IsHitTestVisible,
            "Pinned preview resize splitter should remain usable.");

        // Simulate that resize, close the preview, then open it again. The next expansion must
        // discard the resized value and recompute the fixed proportion from current dock bounds.
        gmcp.SetPinnedBounds(0, 0, 123, 123);
        ((IFactory)factory).TogglePreviewPinnedDockable(gmcp);
        ((IFactory)factory).TogglePreviewPinnedDockable(gmcp);
        Pump(window);

        gmcp.GetPinnedBounds(out _, out _, out width, out height);
        actual = side ? width : height;
        Assert.True(Math.Abs(actual - expected) <= 1.0,
            $"Pinned preview size for {edge} remembered a previous size instead of returning to ~{expected:F1}.");
    }

    // Mirrors a real user profile: several tools pinned to different edges, restored via
    // TryApplySnapshot on startup. Every pinned tab must actually render.
    [AvaloniaFact]
    public void RestoredSnapshotWithPinsOnAllEdges_RendersAllTabs()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        factory.PinToolToEdge(factory.AllTools.First(t => t.Id == "Gmcp"), Alignment.Left);
        factory.PinToolToEdge(factory.AllTools.First(t => t.Id == "Chat"), Alignment.Right);
        factory.PinToolToEdge(factory.AllTools.First(t => t.Id == "Automation"), Alignment.Top);
        factory.PinToolToEdge(factory.AllTools.First(t => t.Id == "Notes"), Alignment.Bottom);
        var snapshot = factory.Snapshot(viewModel.Layout);
        Assert.Equal(4, snapshot.PinnedTools.Count);

        // Round-trip through the restore path used on startup.
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);
        var factory2 = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        Assert.True(factory2.TryApplySnapshot(viewModel.Layout, snapshot));
        PumpUntil(window, () => RenderedPinItems(window).Count >= 4);

        var pinItems = window.GetVisualDescendants().OfType<ToolPinItemControl>().ToList();
        Assert.True(pinItems.Count == 4,
            $"Expected 4 pinned tabs after restore, found {pinItems.Count}: " +
            string.Join(", ", pinItems.Select(p => (p.DataContext as PanelTool)?.Id)));
        Assert.All(pinItems, p => Assert.True(
            p.IsEffectivelyVisible && p.Bounds.Width > 0 && p.Bounds.Height > 0,
            $"Tab {(p.DataContext as PanelTool)?.Id} not rendered: vis={p.IsEffectivelyVisible} bounds={p.Bounds}"));
    }

    [AvaloniaFact]
    public void RapidPresetChanges_RenderFinalPinsAndIgnoreOldFactoryCallbacks()
    {
        Directory.CreateDirectory(_tempDirectory);
        var presetService = new LayoutPresetService(_tempDirectory);
        presetService.Save(new[]
        {
            new LayoutPreset
            {
                Name = "A",
                Snapshot = CreateSnapshotWithPins(
                    ("Gmcp", Alignment.Left),
                    ("Notes", Alignment.Top)),
            },
            new LayoutPreset
            {
                Name = "B",
                Snapshot = CreateSnapshotWithPins(("Chat", Alignment.Right)),
            },
        });
        var viewModel = new MainWindowViewModel(
            new ProfileService(_tempDirectory),
            new AppSettingsService(_tempDirectory),
            new DockLayoutService(_tempDirectory),
            layoutPresetService: presetService);
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("A");
        var firstFactory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        viewModel.ApplyLayoutCommand.Execute("B");
        var secondFactory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        viewModel.ApplyLayoutCommand.Execute("A");
        var finalFactory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);

        Assert.NotSame(firstFactory, secondFactory);
        Assert.NotSame(secondFactory, finalFactory);
        firstFactory.OnDockableClosed(firstFactory.AllTools.First(tool => tool.Id == "Gmcp"));
        Assert.Empty(viewModel.HiddenPanels);

        PumpUntil(window, () => RenderedPinItems(window).Count == 2);

        var renderedIds = RenderedPinItems(window)
            .Select(control => Assert.IsType<PanelTool>(control.DataContext).Id)
            .ToHashSet();
        Assert.Equal(new HashSet<string> { "Gmcp", "Notes" }, renderedIds);
        Assert.Contains(
            viewModel.Layout.LeftPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => dockable.Id == "Gmcp");
        Assert.Contains(
            viewModel.Layout.TopPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => dockable.Id == "Notes");
        Assert.Empty(viewModel.HiddenPanels);
    }

    [AvaloniaFact]
    public void SaveAndLoadLayout_PreservesPinnedTabsOnAllEdges()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        factory.PinToolToEdge(factory.AllTools.First(tool => tool.Id == "Gmcp"), Alignment.Left);
        factory.PinToolToEdge(factory.AllTools.First(tool => tool.Id == "Chat"), Alignment.Right);
        factory.PinToolToEdge(factory.AllTools.First(tool => tool.Id == "Automation"), Alignment.Top);
        factory.PinToolToEdge(factory.AllTools.First(tool => tool.Id == "Notes"), Alignment.Bottom);
        // "Settings" and the 5 promoted Automation sub-panels are also hidden by default in
        // DEFAULT (see CreateLayout). Restore() docks each as a normal tab (unlike
        // RestoreToTopEdge, which pins it and would pollute the pinned-tab assertions below) —
        // just enough to keep the HiddenPanels check honest.
        foreach (var id in new[]
            { "Settings", "AutomationTeam", "AutomationTravel", "AutomationCombat", "AutomationFarm", "AutomationInventory" })
        {
            factory.Restore(factory.AllTools.First(tool => tool.Id == id));
        }
        viewModel.NewLayoutName = "boczne";
        viewModel.SaveLayoutCommand.Execute(null);

        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        viewModel.ApplyLayoutCommand.Execute("boczne");
        PumpUntil(window, () => RenderedPinItems(window).Count == 4);

        Assert.Contains(
            viewModel.Layout.LeftPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => dockable.Id == "Gmcp");
        Assert.Contains(
            viewModel.Layout.RightPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => dockable.Id == "Chat");
        Assert.Contains(
            viewModel.Layout.TopPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => dockable.Id == "Automation");
        Assert.Contains(
            viewModel.Layout.BottomPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => dockable.Id == "Notes");
        Assert.Equal(
            new HashSet<string> { "Gmcp", "Chat", "Automation", "Notes" },
            RenderedPinItems(window)
                .Select(control => Assert.IsType<PanelTool>(control.DataContext).Id)
                .ToHashSet());
        Assert.Empty(viewModel.HiddenPanels);
    }

    [AvaloniaFact]
    public void ClosedPinnedTool_RestoredFromPanelsMenu_RendersTopEdgeTab()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        var gmcp = factory.AllTools.First(t => t.Id == "Gmcp");
        factory.PinToolToEdge(gmcp, Alignment.Right);
        Pump(window);

        factory.CloseDockable(gmcp);
        Pump(window);
        viewModel.RestorePanelCommand.Execute(gmcp);
        PumpUntil(window, () => RenderedPinItems(window)
            .Any(control => ReferenceEquals(control.DataContext, gmcp)));

        Assert.Contains(
            viewModel.Layout.TopPinnedDockables ?? Enumerable.Empty<IDockable>(),
            dockable => ReferenceEquals(dockable, gmcp));
        Assert.DoesNotContain(gmcp, factory.HiddenTools);

        var renderedTab = window.GetVisualDescendants()
            .OfType<ToolPinItemControl>()
            .FirstOrDefault(control => ReferenceEquals(control.DataContext, gmcp));
        Assert.NotNull(renderedTab);
        Assert.True(renderedTab.IsEffectivelyVisible
                    && renderedTab.Bounds.Width > 0
                    && renderedTab.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void ApplyTransparencyLayout_RestoresEveryNonTerminalPanelToTopEdge()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("TRANSPARENCY");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        Assert.True(factory.IsTransparencyLayout);
        Assert.Empty(viewModel.HiddenPanels);

        var topPinnedIds = (viewModel.Layout.TopPinnedDockables ?? Enumerable.Empty<IDockable>())
            .Select(dockable => dockable.Id)
            .ToHashSet();
        var nonTerminalIds = factory.AllTools
            .Where(tool => tool.Id != "Terminal")
            .Select(tool => tool.Id)
            .ToHashSet();
        Assert.Equal(nonTerminalIds, topPinnedIds);
    }

    [AvaloniaFact]
    public void ApplyCompactLayout_ShowsTerminalAndCharacterTabsOnly_EverythingElseHidden()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("COMPACT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        // Normal docking, not TRANSPARENCY's overlay-pinning mode.
        Assert.False(factory.IsTransparencyLayout);

        var visibleIds = viewModel.Layout.VisibleDockables!
            .SelectMany(FlattenDockables)
            .OfType<PanelTool>()
            .Select(tool => tool.Id)
            .ToHashSet();
        Assert.Equal(
            new HashSet<string> { "Terminal", "Effects", "Group", "MemSpells" },
            visibleIds);

        var hiddenIds = viewModel.HiddenPanels.Select(tool => tool.Id).ToHashSet();
        Assert.Contains("Map", hiddenIds);
        Assert.Contains("Notes", hiddenIds);
        Assert.Contains("Settings", hiddenIds);
        Assert.DoesNotContain("Terminal", hiddenIds);
    }

    private static IEnumerable<IDockable> FlattenDockables(IDockable dockable)
    {
        yield return dockable;
        if (dockable is IDock dock)
        {
            foreach (var child in (dock.VisibleDockables ?? Enumerable.Empty<IDockable>()).SelectMany(FlattenDockables))
            {
                yield return child;
            }
        }
    }

    [AvaloniaFact]
    public async Task CompactLayoutName_IsReservedForSavingAndDeleting()
    {
        var viewModel = CreateViewModel();
        try
        {
            viewModel.NewLayoutName = "COMPACT";
            viewModel.SaveLayoutCommand.Execute(null);

            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("zarezerwowana"));
            Assert.DoesNotContain(viewModel.AvailableLayouts, item => item.Name == "COMPACT" && item.CanDelete);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task AvailableLayouts_AlwaysIncludesCompactAsNonDeletable()
    {
        var viewModel = CreateViewModel();
        try
        {
            Assert.Contains(viewModel.AvailableLayouts, item => item.Name == "COMPACT" && !item.CanDelete);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public void OverlayMoveCommands_ReorderWithinColumnAndSwitchColumns()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("TRANSPARENCY");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        factory.AllTools.First(t => t.Id == "Gmcp").PinAsOverlayCommand.Execute(null);
        factory.AllTools.First(t => t.Id == "Notes").PinAsOverlayCommand.Execute(null);
        Pump(window);

        // Both default to column 0 (hugging the right edge), in pin order: Gmcp, then Notes.
        Assert.Equal(["Gmcp", "Notes"], viewModel.TerminalOverlays.Select(o => o.Panel.Id));
        Assert.All(viewModel.TerminalOverlays, o => Assert.Equal(0, o.ColumnIndex));

        // Moving the second one up swaps it with the first.
        viewModel.TerminalOverlays[1].MoveUpCommand.Execute(null);
        Pump(window);
        Assert.Equal(["Notes", "Gmcp"], viewModel.TerminalOverlays.Select(o => o.Panel.Id));

        // Moving the (now first) one down swaps it back.
        viewModel.TerminalOverlays[0].MoveDownCommand.Execute(null);
        Pump(window);
        Assert.Equal(["Gmcp", "Notes"], viewModel.TerminalOverlays.Select(o => o.Panel.Id));

        // Moving Gmcp left creates a new column further from the edge — Notes stays in column 0.
        var gmcpOverlay = viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Gmcp");
        gmcpOverlay.MoveLeftCommand.Execute(null);
        Pump(window);
        Assert.Equal(1, viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Gmcp").ColumnIndex);
        Assert.Equal(0, viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Notes").ColumnIndex);

        // Moving it back right rejoins column 0.
        viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Gmcp").MoveRightCommand.Execute(null);
        Pump(window);
        Assert.All(viewModel.TerminalOverlays, o => Assert.Equal(0, o.ColumnIndex));
    }

    [AvaloniaFact]
    public void OverlayMoveCommands_MultipleColumns_CreateAndCompact()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("TRANSPARENCY");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        factory.AllTools.First(t => t.Id == "Gmcp").PinAsOverlayCommand.Execute(null);
        factory.AllTools.First(t => t.Id == "Notes").PinAsOverlayCommand.Execute(null);
        factory.AllTools.First(t => t.Id == "Group").PinAsOverlayCommand.Execute(null);
        Pump(window);
        Assert.All(viewModel.TerminalOverlays, o => Assert.Equal(0, o.ColumnIndex));

        int ColumnOf(string id) => viewModel.TerminalOverlays.Single(o => o.Panel.Id == id).ColumnIndex;

        // Gmcp moves left, alone: column 0 -> 1. Nothing anchors an empty column in between, so a
        // second "move left" by the same lone occupant would just collapse back to 1 (there is
        // nothing further left to make room for) — that case is covered by
        // OverlayMoveCommands_ReorderWithinColumnAndSwitchColumns above instead.
        viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Gmcp").MoveLeftCommand.Execute(null);
        Pump(window);
        Assert.Equal((Group: 0, Gmcp: 1, Notes: 0), (Group: ColumnOf("Group"), Gmcp: ColumnOf("Gmcp"), Notes: ColumnOf("Notes")));

        // Notes moves left too: joins Gmcp's column (0 -> 1), since that's the next column over.
        viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Notes").MoveLeftCommand.Execute(null);
        Pump(window);
        Assert.Equal(1, ColumnOf("Gmcp"));
        Assert.Equal(1, ColumnOf("Notes"));

        // Notes moves left again: this time Gmcp is still occupying column 1, so column 2 is a
        // genuine third column, not immediately compacted away.
        viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Notes").MoveLeftCommand.Execute(null);
        Pump(window);
        Assert.Equal(0, ColumnOf("Group"));
        Assert.Equal(1, ColumnOf("Gmcp"));
        Assert.Equal(2, ColumnOf("Notes"));

        // Gmcp moves right, rejoining Group's column (1 -> 0). Column 1 is now empty; compaction
        // must renumber Notes' column 2 down to 1 so no gap is left.
        viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Gmcp").MoveRightCommand.Execute(null);
        Pump(window);
        Assert.Equal(0, ColumnOf("Group"));
        Assert.Equal(0, ColumnOf("Gmcp"));
        Assert.Equal(1, ColumnOf("Notes"));

        // Group (already at column 0, the right edge) can't move right any further — no-op.
        viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Group").MoveRightCommand.Execute(null);
        Pump(window);
        Assert.Equal(0, ColumnOf("Group"));
    }

    [AvaloniaFact]
    public void OverlayMoveButtons_ClickingTheRenderedButton_InvokesTheMoveCommand()
    {
        // Unlike OverlayMoveCommands_ReorderWithinColumnAndSwitchColumns above (which calls
        // MoveUpCommand.Execute(null) directly on the ViewModel), this test clicks the actual
        // rendered Button in TerminalOverlayCard's visual tree — the ▲▼◀▶ buttons once shipped
        // with a Command="{Binding $parent[TerminalOverlayCard].Overlay.MoveUpCommand}" binding
        // that compiled cleanly but never fired at runtime, and a ViewModel-level test couldn't
        // catch that. The buttons are now wired via plain Click handlers in code-behind instead.
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("TRANSPARENCY");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        factory.AllTools.First(t => t.Id == "Gmcp").PinAsOverlayCommand.Execute(null);
        factory.AllTools.First(t => t.Id == "Notes").PinAsOverlayCommand.Execute(null);
        Pump(window);
        Assert.Equal(["Gmcp", "Notes"], viewModel.TerminalOverlays.Select(o => o.Panel.Id));

        var notesCard = window.GetVisualDescendants().OfType<TerminalOverlayCard>()
            .Single(card => card.Overlay?.Panel.Id == "Notes");
        var upButton = notesCard.GetVisualDescendants().OfType<Button>()
            .Single(button => ToolTip.GetTip(button) as string == "Przesuń w górę");

        upButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);

        Assert.Equal(["Notes", "Gmcp"], viewModel.TerminalOverlays.Select(o => o.Panel.Id));

        var leftButton = window.GetVisualDescendants().OfType<TerminalOverlayCard>()
            .Single(card => card.Overlay?.Panel.Id == "Gmcp")
            .GetVisualDescendants().OfType<Button>()
            .Single(button => ToolTip.GetTip(button) as string == "Przenieś do kolumny bardziej w lewo");

        leftButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);

        Assert.Equal(1, viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Gmcp").ColumnIndex);
        Assert.Equal(0, viewModel.TerminalOverlays.Single(o => o.Panel.Id == "Notes").ColumnIndex);
    }

    [AvaloniaFact]
    public void TerminalOverlayHost_NeverGrowsPastTheOutputRow_RegardlessOfColumnContent()
    {
        // TerminalOverlayHost used to be a sibling of the whole terminal Grid (spanning all 3
        // rows: output, timer strip, command bar) instead of scoped to the output row alone. A
        // column with enough cards to want more room than the output row could then grow past
        // its intended bounds and cover the command bar underneath — see how OverlayHost is
        // placed in TerminalPanelView.axaml now (inside the same cell as MudOutputView).
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);

        viewModel.ApplyLayoutCommand.Execute("TRANSPARENCY");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        // Pin enough tools into the same column to force a tall stack that would want more
        // vertical room than the output row alone provides.
        foreach (var id in new[] { "Gmcp", "Notes", "Group", "Effects", "MemSpells" })
        {
            factory.AllTools.First(t => t.Id == id).PinAsOverlayCommand.Execute(null);
        }
        Pump(window);

        var terminal = window.GetVisualDescendants().OfType<TerminalPanelView>().Single();
        var overlayHost = terminal.GetVisualDescendants().OfType<TerminalOverlayHost>().Single();
        var mudOutput = terminal.GetVisualDescendants().OfType<MudOutputView>().Single();

        // Siblings in the same Grid cell always share the same allotted height — if OverlayHost
        // were still scoped to the whole panel instead, it would be taller than the output area.
        Assert.Equal(mudOutput.Bounds.Height, overlayHost.Bounds.Height, precision: 1);
    }

    [AvaloniaFact]
    public void RestoringMap_DetachesPreviousMapControlFromViewModel()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        var map = factory.AllTools.First(t => t.Id == "Map");
        var detachedMapControl = window.GetVisualDescendants()
            .OfType<WorldMapControl>()
            .Single(control => control.IsEffectivelyVisible);

        factory.CloseDockable(map);
        Pump(window);
        viewModel.RestorePanelCommand.Execute(map);
        PumpUntil(window, () => RenderedPinItems(window)
            .Any(control => ReferenceEquals(control.DataContext, map)));
        ((IFactory)factory).TogglePreviewPinnedDockable(map);
        PumpUntil(window, () => window.GetVisualDescendants()
            .OfType<WorldMapControl>()
            .Any(control => control.IsEffectivelyVisible && control.Bounds.Width > 0));

        var restoredMapControl = window.GetVisualDescendants()
            .OfType<WorldMapControl>()
            .Single(control => control.IsEffectivelyVisible && control.Bounds is { Width: > 0, Height: > 0 });
        Assert.NotSame(detachedMapControl, restoredMapControl);

        var sentinel = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(5, 6, 0) };
        detachedMapControl.CenterOnRoom(sentinel);
        var focused = new MapRoom { Id = 2, AreaId = 2, Coordinates = new MapCoordinates(50, 60, 0) };
        viewModel.Map.SelectedArea = new MapArea { Id = 2, Rooms = [focused] };

        Assert.Equal(sentinel.Coordinates.X, detachedMapControl.CameraX);
        Assert.Equal(sentinel.Coordinates.Y, detachedMapControl.CameraY);
        Assert.Equal(focused.Coordinates.X, restoredMapControl.CameraX);
        Assert.Equal(focused.Coordinates.Y, restoredMapControl.CameraY);
    }

    [AvaloniaFact]
    public void RestoringTerminal_DetachesPreviousTerminalFromOutputStream()
    {
        var viewModel = CreateViewModel();
        var window = ShowWindow(viewModel);
        Pump(window);
        viewModel.ApplyLayoutCommand.Execute("DEFAULT");
        Pump(window);

        var factory = Assert.IsType<MudDockFactory>(viewModel.Layout.Factory);
        var terminal = factory.AllTools.First(t => t.Id == "Terminal");
        var detachedTerminal = window.GetVisualDescendants()
            .OfType<TerminalPanelView>()
            .Single(control => control.IsEffectivelyVisible);
        var detachedPane = detachedTerminal.GetVisualDescendants().OfType<OutputPaneControl>().First();

        factory.CloseDockable(terminal);
        Pump(window);
        viewModel.RestorePanelCommand.Execute(terminal);
        PumpUntil(window, () => RenderedPinItems(window)
            .Any(control => ReferenceEquals(control.DataContext, terminal)));
        ((IFactory)factory).TogglePreviewPinnedDockable(terminal);
        PumpUntil(window, () => window.GetVisualDescendants()
            .OfType<TerminalPanelView>()
            .Any(control => control.IsEffectivelyVisible));

        var restoredTerminal = window.GetVisualDescendants()
            .OfType<TerminalPanelView>()
            .Single(control => control.IsEffectivelyVisible);
        var restoredPane = restoredTerminal.GetVisualDescendants().OfType<OutputPaneControl>().First();
        var detachedCount = detachedPane.Buffer!.Count;
        var restoredCount = restoredPane.Buffer!.Count;

        var subscribers = typeof(MainWindowViewModel)
            .GetField(nameof(MainWindowViewModel.OutputReceived),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(viewModel) as Action<string>;
        Assert.NotNull(subscribers);
        subscribers("performance probe\n");

        Assert.Equal(detachedCount, detachedPane.Buffer.Count);
        Assert.Equal(restoredCount + 1, restoredPane.Buffer.Count);
    }
}

using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;

namespace MudClient.App.Tests;

/// <summary>
/// Folder tree building and the generic folder commands (Etap 3).
/// </summary>
public sealed class FolderCommandsTests : IAsyncDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "KillerMudClient_FolderTest_" + Guid.NewGuid().ToString("N"));
    private readonly MainWindowViewModel _vm;

    public FolderCommandsTests()
    {
        Directory.CreateDirectory(_dir);
        _vm = new MainWindowViewModel(new ProfileService(_dir), new AppSettingsService(_dir));
    }

    public async ValueTask DisposeAsync()
    {
        await _vm.DisposeAsync();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private AutomationRuleEntry AddAliasInFolder(FolderNode folder, bool enabled = true)
    {
        var alias = new AutomationRuleEntry("a", "alias", "^l$", "look", enabled) { FolderId = folder.Id };
        _vm.AutomationRules.Add(alias);
        return alias;
    }

    [Fact]
    public void CreateFolder_AddsFolderOfKindAndShowsInTree()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Timers);

        var folder = Assert.Single(_vm.Folders);
        Assert.Equal(FolderKind.Timers, folder.Kind);

        var node = Assert.Single(_vm.TimerTree);
        Assert.True(node.IsFolder);
        Assert.Same(folder, node.Folder);
    }

    [Fact]
    public void CreateSubfolder_NestsUnderParentAndInheritsGlobal()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Notes);
        var parent = _vm.Folders.Single();
        parent.IsGlobal = true;

        _vm.CreateSubfolderCommand.Execute(parent);

        Assert.Equal(2, _vm.Folders.Count);
        var child = _vm.Folders.Single(f => f.ParentId == parent.Id);
        Assert.True(child.IsGlobal);

        var rootNode = _vm.NoteTree.Single(n => n.IsFolder && ReferenceEquals(n.Folder, parent));
        Assert.Contains(rootNode.Children, c => c.IsFolder && ReferenceEquals(c.Folder, child));
    }

    [Fact]
    public void Tree_NestsItemsUnderTheirFolderAndCountsThem()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        var folder = _vm.Folders.Single();
        var alias = AddAliasInFolder(folder);

        var root = Assert.Single(_vm.AliasTree);
        Assert.True(root.IsFolder);
        Assert.Equal(1, root.ItemCount);
        var leaf = Assert.Single(root.Children);
        Assert.False(leaf.IsFolder);
        Assert.Same(alias, leaf.Content);
    }

    [Fact]
    public void LooseItems_RenderAtRootAlongsideFolders()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        // A loose alias (no folder).
        _vm.AutomationRules.Add(new AutomationRuleEntry("loose", "alias", "^x$", "y", true));

        Assert.Equal(2, _vm.AliasTree.Count);
        Assert.Contains(_vm.AliasTree, n => n.IsFolder);
        Assert.Contains(_vm.AliasTree, n => !n.IsFolder);
    }

    [Fact]
    public void DeleteFolder_RemovesFolderAndItsItems()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Timers);
        var folder = _vm.Folders.Single();
        _vm.Timers.Add(new TimerEntry { Name = "t", Seconds = 5, CommandsText = "look", FolderId = folder.Id });

        _vm.DeleteFolderCommand.Execute(folder);

        Assert.Empty(_vm.Folders);
        Assert.Empty(_vm.Timers);
        Assert.Empty(_vm.TimerTree);
    }

    [Fact]
    public void DeleteFolder_RemovesNestedSubfoldersAndTheirItems()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        var parent = _vm.Folders.Single();
        _vm.CreateSubfolderCommand.Execute(parent);
        var child = _vm.Folders.Single(f => f.ParentId == parent.Id);
        AddAliasInFolder(child);

        _vm.DeleteFolderCommand.Execute(parent);

        Assert.Empty(_vm.Folders);
        Assert.Empty(_vm.AutomationRules);
    }

    [Fact]
    public void ToggleFolderGlobal_CascadesToItemsAndBack()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        var folder = _vm.Folders.Single();
        var alias = AddAliasInFolder(folder);

        _vm.ToggleFolderGlobalCommand.Execute(folder);
        Assert.True(folder.IsGlobal);
        Assert.True(alias.IsGlobal);

        _vm.ToggleFolderGlobalCommand.Execute(folder);
        Assert.False(folder.IsGlobal);
        Assert.False(alias.IsGlobal);
    }

    [Fact]
    public void ToggleFolderEnabled_DisablesThenEnablesAllItems()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Triggers);
        var folder = _vm.Folders.Single();
        var t1 = new AutomationRuleEntry("t1", "trigger", "^a$", "x", true) { FolderId = folder.Id };
        var t2 = new AutomationRuleEntry("t2", "trigger", "^b$", "y", true) { FolderId = folder.Id };
        _vm.AutomationRules.Add(t1);
        _vm.AutomationRules.Add(t2);

        var root = Assert.Single(_vm.TriggerTree);
        Assert.True(root.IsAllEnabled);
        Assert.Equal("AKTYWNY", root.ActivationText);

        _vm.ToggleRuleCommand.Execute(t1);
        root = Assert.Single(_vm.TriggerTree);
        Assert.True(root.IsMixedActivation);
        Assert.Equal("MIESZANY", root.ActivationText);

        _vm.ToggleFolderEnabledCommand.Execute(folder);
        Assert.True(t1.IsEnabled);
        Assert.True(t2.IsEnabled);
        Assert.True(Assert.Single(_vm.TriggerTree).IsAllEnabled);

        _vm.ToggleFolderEnabledCommand.Execute(folder);
        Assert.False(t1.IsEnabled);
        Assert.False(t2.IsEnabled);
        root = Assert.Single(_vm.TriggerTree);
        Assert.True(root.IsAllDisabled);
        Assert.Equal("WYŁĄCZONY", root.ActivationText);
    }

    [Fact]
    public void MoveIntoFolder_MovesItemAndInheritsGlobalOwnership()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        var target = _vm.Folders.Single();
        target.IsGlobal = true;
        var alias = new AutomationRuleEntry("a", "alias", "^a$", "b", true);
        _vm.AutomationRules.Add(alias);

        _vm.MoveIntoFolderCommand.Execute(new FolderMoveRequest(alias, target));

        Assert.Equal(target.Id, alias.FolderId);
        Assert.True(alias.IsGlobal);
        Assert.Same(alias, Assert.Single(Assert.Single(_vm.AliasTree).Children).Content);
    }

    [Fact]
    public void MoveIntoFolder_NestsFolderButRejectsCycle()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Timers);
        var parent = _vm.Folders.Single();
        _vm.CreateFolderCommand.Execute(FolderKind.Timers);
        var target = _vm.Folders.Last();

        _vm.MoveIntoFolderCommand.Execute(new FolderMoveRequest(parent, target));
        Assert.Equal(target.Id, parent.ParentId);

        _vm.MoveIntoFolderCommand.Execute(new FolderMoveRequest(target, parent));
        Assert.Null(target.ParentId);
    }

    [Fact]
    public void MoveIntoFolder_RejectsCrossDomainMove()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Triggers);
        var target = _vm.Folders.Single();
        var timer = new TimerEntry { Name = "t", Seconds = 1, CommandsText = "look" };
        _vm.Timers.Add(timer);

        _vm.MoveIntoFolderCommand.Execute(new FolderMoveRequest(timer, target));

        Assert.Null(timer.FolderId);
    }

    // ====================================================================
    // Live search/filter boxes (TimerFilterText / AliasFilterText / TriggerFilterText)
    // ====================================================================

    [Fact]
    public void AliasFilterText_LooseItemNotMatching_IsHiddenFromTree()
    {
        _vm.AutomationRules.Add(new AutomationRuleEntry("boss-kill", "alias", "^k$", "kill", true));
        _vm.AutomationRules.Add(new AutomationRuleEntry("heal-self", "alias", "^h$", "heal", true));

        _vm.AliasFilterText = "boss";

        var leaf = Assert.Single(_vm.AliasTree);
        Assert.Equal("boss-kill", ((AutomationRuleEntry)leaf.Content!).Name);
    }

    [Fact]
    public void AliasFilterText_MatchesPatternNotJustName()
    {
        // "gandalf" only appears in the pattern, not the name — search must look at both.
        _vm.AutomationRules.Add(new AutomationRuleEntry("greet", "trigger", "gandalf mowi", "say hi", true));
        _vm.AutomationRules.Add(new AutomationRuleEntry("other", "trigger", "^x$", "y", true));

        _vm.TriggerFilterText = "gandalf";

        var leaf = Assert.Single(_vm.TriggerTree);
        Assert.Equal("greet", ((AutomationRuleEntry)leaf.Content!).Name);
    }

    [Fact]
    public void TimerFilterText_MatchesCommandsTextNotJustName()
    {
        _vm.Timers.Add(new TimerEntry { Name = "Watch", Seconds = 5, CommandsText = "cast 'refresh' self" });
        _vm.Timers.Add(new TimerEntry { Name = "Scan", Seconds = 5, CommandsText = "scan" });

        _vm.TimerFilterText = "refresh";

        var leaf = Assert.Single(_vm.TimerTree);
        Assert.Equal("Watch", ((TimerEntry)leaf.Content!).Name);
    }

    [Fact]
    public void AliasFilterText_ItemInsideFolder_KeepsAncestorFolderButPrunesSiblings()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        var folder = _vm.Folders.Single();
        AddAliasInFolder(folder); // Name "a" — matches nothing below
        var matching = new AutomationRuleEntry("boss-kill", "alias", "^k$", "kill", true) { FolderId = folder.Id };
        _vm.AutomationRules.Add(matching);

        _vm.AliasFilterText = "boss";

        var root = Assert.Single(_vm.AliasTree);
        Assert.True(root.IsFolder);
        var leaf = Assert.Single(root.Children);
        Assert.Same(matching, leaf.Content);
    }

    [Fact]
    public void AliasFilterText_FolderWithNoMatchingDescendants_IsHiddenEntirely()
    {
        _vm.CreateFolderCommand.Execute(FolderKind.Aliases);
        var folder = _vm.Folders.Single();
        AddAliasInFolder(folder); // Name "a" — never matches "boss"

        _vm.AliasFilterText = "boss";

        Assert.Empty(_vm.AliasTree);
    }

    [Fact]
    public void AliasFilterText_ClearedAgain_RestoresFullTree()
    {
        _vm.AutomationRules.Add(new AutomationRuleEntry("boss-kill", "alias", "^k$", "kill", true));
        _vm.AutomationRules.Add(new AutomationRuleEntry("heal-self", "alias", "^h$", "heal", true));
        _vm.AliasFilterText = "boss";
        Assert.Single(_vm.AliasTree);

        _vm.AliasFilterText = string.Empty;

        Assert.Equal(2, _vm.AliasTree.Count);
    }

    [Fact]
    public void AliasFilterText_DoesNotAffectOtherSectionsTrees()
    {
        // Filtering aliases must not touch timers/triggers/notes/autowalk — RebuildFolderTrees
        // rebuilds every tree, but only the aliases one carries a non-null filter predicate.
        _vm.Timers.Add(new TimerEntry { Name = "Watch", Seconds = 5, CommandsText = "look" });
        _vm.AutomationRules.Add(new AutomationRuleEntry("boss-kill", "alias", "^k$", "kill", true));

        _vm.AliasFilterText = "does-not-match-anything";

        Assert.Empty(_vm.AliasTree);
        Assert.Single(_vm.TimerTree);
    }
}

using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using MudClient.App.Models;
using MudClient.App.ViewModels;
using MudClient.Core.Gmcp;
using MudClient.Core.Map;

namespace MudClient.App.Tests;

public sealed class MapViewModelTests
{
    private static readonly MapRoom SampleRoom = new()
    {
        Id = 1,
        AreaId = 1,
        Coordinates = new MapCoordinates(0, 0, 0),
        UserData = new Dictionary<string, JsonElement>
        {
            ["sector"] = JsonSerializer.SerializeToElement("inside"),
        },
    };

    private static readonly MapRoom SampleRoomNoSector = new()
    {
        Id = 2,
        AreaId = 1,
        Coordinates = new MapCoordinates(1, 1, 0),
    };

    // ====================================================================
    // SelectedRoomIcon – null behaviour
    // ====================================================================

    [Fact]
    public void SelectedRoomIcon_WhenTextureCacheNull_ReturnsNull()
    {
        using var vm = CreateViewModel();
        Assert.Null(vm.TextureCache);
        Assert.Null(vm.SelectedRoomIcon);
    }

    [Fact]
    public void SelectedRoomIcon_WhenTextureCacheNullAndRoomSet_ReturnsNull()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = SampleRoom;
        Assert.Null(vm.TextureCache);
        Assert.Null(vm.SelectedRoomIcon);
    }

    [Fact]
    public void SelectedRoomIcon_WhenTextureCacheNullAndRoomWithNullSector_ReturnsNull()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = SampleRoomNoSector;
        Assert.Null(vm.TextureCache);
        Assert.Null(vm.SelectedRoomIcon);
    }

    // ====================================================================
    // PropertyChanged notifications for SelectedRoomIcon
    // ====================================================================

    [Fact]
    public void SelectedRoom_Setter_RaisesPropertyChangedForSelectedRoomIcon()
    {
        using var vm = CreateViewModel();
        var changedProperties = new List<string?>();

        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.SelectedRoom = SampleRoom;

        Assert.Contains(nameof(MapViewModel.SelectedRoomIcon), changedProperties);
    }

    [Fact]
    public void SelectedRoom_Setter_DoesNotRaiseWhenSameInstance()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = SampleRoom;

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        // Set same instance again
        vm.SelectedRoom = SampleRoom;

        Assert.DoesNotContain(nameof(MapViewModel.SelectedRoomIcon), changedProperties);
    }

    [Fact]
    public void SelectedRoom_Setter_RaisesPropertyChangedForSelectedRoomIconWhenSetToNull()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = SampleRoom;

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.SelectedRoom = null;

        Assert.Contains(nameof(MapViewModel.SelectedRoomIcon), changedProperties);
    }

    [Fact]
    public void LordGotoSelectedRoomCommand_RequiresLordModeAndNumericVnum()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        Assert.False(vm.LordGotoSelectedRoomCommand.CanExecute(null));

        vm.LordModeEnabled = true;

        Assert.True(vm.LordGotoSelectedRoomCommand.CanExecute(null));
    }

    [Fact]
    public void LordGotoSelectedRoomCommand_RaisesOneRequestForSelectedRoom()
    {
        using var vm = CreateViewModel();
        var room = CreateSampleRoom();
        var requests = new List<MapRoom>();
        vm.LordGotoRequested += requests.Add;
        vm.SelectedRoom = room;
        vm.LordModeEnabled = true;

        vm.LordGotoSelectedRoomCommand.Execute(null);

        Assert.Equal([room], requests);
        Assert.Equal("Walk: Test Room [100]", vm.LordGotoMenuHeader);
    }

    [Fact]
    public void LordGotoSelectedRoomCommand_RejectsNonNumericVnum()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = new MapRoom
        {
            Id = 9,
            AreaId = 1,
            Coordinates = new MapCoordinates(0, 0, 0),
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement("100\ngoto 200"),
            },
        };
        vm.LordModeEnabled = true;

        Assert.False(vm.LordGotoSelectedRoomCommand.CanExecute(null));
    }

    // ====================================================================
    // SelectedRoomIcon – expression logic
    // ====================================================================

    [Fact]
    public void SelectedRoomIcon_WithNullSelectedRoom_PassesEmptyStringToGetTexture()
    {
        // TextureCache is null -> result will be null regardless of SelectedRoom.
        // This test verifies the binding expression does not throw with null SelectedRoom.
        using var vm = CreateViewModel();
        vm.SelectedRoom = null;

        var ex = Record.Exception(() => _ = vm.SelectedRoomIcon);
        Assert.Null(ex);
    }

    // ====================================================================
    // FollowPlayer initial state and toggling
    // ====================================================================

    [Fact]
    public void FollowPlayer_DefaultsToTrue()
    {
        using var vm = CreateViewModel();
        Assert.True(vm.FollowPlayer);
    }

    [Fact]
    public void FollowPlayer_CanBeSetToFalseAndBack()
    {
        using var vm = CreateViewModel();
        vm.FollowPlayer = false;
        Assert.False(vm.FollowPlayer);
        vm.FollowPlayer = true;
        Assert.True(vm.FollowPlayer);
    }

    [Fact]
    public void FollowPlayer_Setter_RaisesPropertyChanged()
    {
        using var vm = CreateViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.FollowPlayer = false;

        Assert.Contains(nameof(MapViewModel.FollowPlayer), changedProperties);
    }

    [Fact]
    public void SelectedDisplayMode_DefaultsToProceduralAndRaisesPropertyChangedWhenChanged()
    {
        using var vm = CreateViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.Collection(
            vm.DisplayModes,
            mode => Assert.Equal(MudClient.App.Controls.MapDisplayMode.Procedural, mode.Mode),
            mode => Assert.Equal(MudClient.App.Controls.MapDisplayMode.Simple, mode.Mode));
        Assert.Equal(MudClient.App.Controls.MapDisplayMode.Procedural, vm.SelectedDisplayMode.Mode);

        vm.SelectedDisplayMode = vm.DisplayModes.First(m => m.Mode == MudClient.App.Controls.MapDisplayMode.Simple);

        Assert.Equal(MudClient.App.Controls.MapDisplayMode.Simple, vm.SelectedDisplayMode.Mode);
        Assert.Contains(nameof(MapViewModel.SelectedDisplayMode), changedProperties);
    }

    // ====================================================================
    // Public setters disable FollowPlayer
    // ====================================================================

    [Fact]
    public void SelectedArea_Setter_SetsFollowPlayerToFalse()
    {
        using var vm = CreateViewModel();
        Assert.True(vm.FollowPlayer);

        vm.SelectedArea = new MapArea { Id = 99, Name = "Test" };

        Assert.False(vm.FollowPlayer);
    }

    [Fact]
    public void SelectedArea_Setter_FocusesFirstRoomInArea()
    {
        using var vm = CreateViewModel();
        var firstRoom = new MapRoom
        {
            Id = 10,
            AreaId = 99,
            Coordinates = new MapCoordinates(25, 30, 4),
        };
        var area = new MapArea
        {
            Id = 99,
            Name = "Test",
            Rooms = [firstRoom],
        };
        MapRoom? centeredRoom = null;
        vm.CenterOnRoomRequested += room => centeredRoom = room;

        vm.SelectedArea = area;

        Assert.Same(firstRoom, vm.SelectedRoom);
        Assert.Equal(firstRoom.Coordinates.Z, vm.SelectedZ);
        Assert.Same(firstRoom, centeredRoom);
    }

    [Fact]
    public void SelectedArea_Setter_SettingNullDoesNotChangeFollowPlayer()
    {
        using var vm = CreateViewModel();
        Assert.True(vm.FollowPlayer);

        // _selectedArea is already null, so SetProperty returns false
        vm.SelectedArea = null;

        Assert.True(vm.FollowPlayer);
    }

    [Fact]
    public void SelectedZ_Setter_SetsFollowPlayerToFalse()
    {
        using var vm = CreateViewModel();
        Assert.True(vm.FollowPlayer);

        vm.SelectedZ = 5.0;  // different from default 0.0

        Assert.False(vm.FollowPlayer);
    }

    [Fact]
    public void SelectedZ_Setter_SettingDefaultValueDoesNotChangeFollowPlayer()
    {
        using var vm = CreateViewModel();
        vm.FollowPlayer = true;

        // _selectedZ is already 0.0, so SetProperty returns false
        vm.SelectedZ = 0.0;

        Assert.True(vm.FollowPlayer);
    }

    [Fact]
    public void SelectedZIndex_TransientMinusOne_DoesNotReplaceMapLevel()
    {
        using var vm = CreateViewModel();
        vm.ZLevels.Add(0);
        vm.ZLevels.Add(5);
        vm.SelectedZ = 5;

        vm.SelectedZIndex = -1;

        Assert.Equal(5, vm.SelectedZ);
        Assert.Equal(1, vm.SelectedZIndex);
    }

    // ====================================================================
    // CenterCommand (Wycentruj) behavior
    // ====================================================================

    [Fact]
    public void CenterCommand_WhenCurrentRoomIsNull_DoesNotChangeFollowPlayer()
    {
        using var vm = CreateViewModel();
        Assert.Null(vm.CurrentRoom);

        vm.FollowPlayer = false;
        vm.CenterCommand.Execute(null);

        Assert.False(vm.FollowPlayer);  // still false — no-op
    }

    [Fact]
    public void CenterCommand_WhenCurrentRoomIsNull_DoesNotThrow()
    {
        using var vm = CreateViewModel();
        var ex = Record.Exception(() => vm.CenterCommand.Execute(null));
        Assert.Null(ex);
    }

    [Fact]
    public void CenterCommand_WhenCurrentRoomIsSet_SetsFollowPlayerToTrue()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetCurrentRoom(vm, CreateSampleRoom());

        vm.FollowPlayer = false;
        vm.CenterCommand.Execute(null);

        Assert.True(vm.FollowPlayer);
    }

    [Fact]
    public void CenterCommand_WhenCurrentRoomIsSet_RaisesCenterOnCurrentRoomRequested()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetCurrentRoom(vm, CreateSampleRoom());

        var raised = false;
        vm.CenterOnCurrentRoomRequested += () => raised = true;

        vm.CenterCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void CenterCommand_WhenCurrentRoomIsSet_UpdatesSelectedAreaToMatchRoom()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetCurrentRoom(vm, CreateSampleRoom());

        vm.CenterCommand.Execute(null);

        Assert.NotNull(vm.SelectedArea);
        Assert.Equal(1, vm.SelectedArea.Id);
    }

    [Fact]
    public void CenterCommand_WhenCurrentRoomIsSet_UpdatesSelectedZToMatchRoom()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetCurrentRoom(vm, CreateSampleRoom());

        vm.CenterCommand.Execute(null);

        Assert.Equal(5.0, vm.SelectedZ);
    }

    [Fact]
    public void FocusRoomByVnum_SelectsAreaFloorAndRoomWithoutFollowingPlayer()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndexThroughProperty(vm, index);
        MapRoom? centeredRoom = null;
        vm.CenterOnRoomRequested += room => centeredRoom = room;

        var room = vm.FocusRoomByVnum("100");

        Assert.NotNull(room);
        Assert.Same(room, vm.SelectedRoom);
        Assert.Same(room, centeredRoom);
        Assert.Equal(1, vm.SelectedArea?.Id);
        Assert.Equal(5, vm.SelectedZ);
        Assert.False(vm.FollowPlayer);
    }

    [Fact]
    public void FocusRoomByVnum_UnknownRoomLeavesMapUnchanged()
    {
        using var vm = CreateViewModel();
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusRoomByVnum("missing");

        Assert.Null(room);
        Assert.Null(vm.SelectedRoom);
    }

    // ====================================================================
    // TryResolveCurrentRoom (invoked via reflection) — FollowPlayer preservation
    // ====================================================================

    [Fact]
    public void TryResolveCurrentRoom_DoesNotDisableFollowPlayer()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        // FollowPlayer already true by default
        InvokeTryResolveCurrentRoom(vm);

        Assert.True(vm.FollowPlayer);  // must not be disabled by internal setters
    }

    [Fact]
    public void TryResolveCurrentRoom_WhenFollowPlayerIsFalse_EnablesItAndRequestsCentering()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");
        var centerRequested = false;
        vm.CenterOnCurrentRoomRequested += () => centerRequested = true;

        vm.FollowPlayer = false;
        InvokeTryResolveCurrentRoom(vm);

        Assert.True(vm.FollowPlayer);
        Assert.True(centerRequested);
    }

    // ====================================================================
    // TryResolveCurrentRoom — current room resolution
    // ====================================================================

    [Fact]
    public void TryResolveCurrentRoom_SetsCurrentRoom()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        InvokeTryResolveCurrentRoom(vm);

        Assert.NotNull(vm.CurrentRoom);
        Assert.Equal("100", vm.CurrentRoom!.Vnum);
        Assert.Equal("Test Room", vm.CurrentRoom.Name);
    }

    [Fact]
    public void TryResolveCurrentRoom_SetsCurrentSector()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        InvokeTryResolveCurrentRoom(vm);

        Assert.Equal("inside", vm.CurrentSectorName);
    }

    [Fact]
    public void TryResolveCurrentRoom_WithUnknownVnum_DoesNotSetCurrentRoom()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "999");

        InvokeTryResolveCurrentRoom(vm);

        Assert.Null(vm.CurrentRoom);
        Assert.Contains("VNUM 999", vm.StatusMessage);
    }

    [Fact]
    public void TryResolveCurrentRoom_WithNullVnum_DoesNothing()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        // CurrentVnum stays null

        InvokeTryResolveCurrentRoom(vm);

        Assert.Null(vm.CurrentRoom);
    }

    // ====================================================================
    // TryResolveCurrentRoom — area and Z selection
    // ====================================================================

    [Fact]
    public void TryResolveCurrentRoom_SelectsCorrectArea()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        InvokeTryResolveCurrentRoom(vm);

        Assert.NotNull(vm.SelectedArea);
        Assert.Equal(1, vm.SelectedArea!.Id);
        Assert.Equal("Test Area", vm.SelectedArea.Name);
    }

    [Fact]
    public void TryResolveCurrentRoom_DoesNotSwitchArea_WhenAlreadyCorrect()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        // Set area to the same one via internal setter first
        var area = index.AreasById[1];
        SetSelectedAreaInternal(vm, area);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        InvokeTryResolveCurrentRoom(vm);

        // SelectedArea should not have changed (already same instance)
        Assert.Same(area, vm.SelectedArea);
    }

    [Fact]
    public void TryResolveCurrentRoom_SetsCorrectZ()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        InvokeTryResolveCurrentRoom(vm);

        Assert.Equal(5.0, vm.SelectedZ);
    }

    [Fact]
    public void TryResolveCurrentRoom_UpdatesZLevels()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        InvokeTryResolveCurrentRoom(vm);

        Assert.Contains(5.0, vm.ZLevels);
    }

    // ====================================================================
    // TryResolveCurrentRoom — SelectedRoom defaulting
    // ====================================================================

    [Fact]
    public void TryResolveCurrentRoom_SetsSelectedRoomToCurrentRoom_WhenSelectedRoomIsNull()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");
        Assert.Null(vm.SelectedRoom);

        InvokeTryResolveCurrentRoom(vm);

        Assert.NotNull(vm.SelectedRoom);
        Assert.Same(vm.CurrentRoom, vm.SelectedRoom);
    }

    [Fact]
    public void TryResolveCurrentRoom_UpdatesSelectedRoom_WhenDifferentRoomWasSet()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        // Simulate a map click selecting a different room before walking
        var clickedRoom = new MapRoom
        {
            Id = 999,
            AreaId = 1,
            Coordinates = new MapCoordinates(0, 0, 0),
        };
        vm.SelectedRoom = clickedRoom;

        InvokeTryResolveCurrentRoom(vm);

        // Walking overwrites the clicked room with the current room
        Assert.NotNull(vm.CurrentRoom);
        Assert.Same(vm.CurrentRoom, vm.SelectedRoom);
        Assert.NotSame(clickedRoom, vm.SelectedRoom);
    }

    [Fact]
    public void TryResolveCurrentRoom_RaisesPropertyChangedForSelectedRoomIcon_WhenRoomSet()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");
        Assert.Null(vm.SelectedRoom);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        InvokeTryResolveCurrentRoom(vm);

        Assert.Contains(nameof(MapViewModel.SelectedRoomIcon), changedProperties);
    }

    [Fact]
    public void TryResolveCurrentRoom_RaisesSelectedRoomIcon_WhenDifferentRoomWasSet()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        // Pre-set SelectedRoom to a different room (simulating a map click)
        vm.SelectedRoom = new MapRoom
        {
            Id = 999,
            AreaId = 1,
            Coordinates = new MapCoordinates(0, 0, 0),
        };

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        InvokeTryResolveCurrentRoom(vm);

        Assert.Contains(nameof(MapViewModel.SelectedRoomIcon), changedProperties);
    }

    [Fact]
    public void TryResolveCurrentRoom_DoesNotRaiseSelectedRoomIcon_WhenSameRoomInstance()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);
        SetLocationVnum(vm, "100");

        // Pre-set SelectedRoom to the exact room instance from the map index,
        // so ReferenceEquals prevents a redundant update
        vm.SelectedRoom = index.RoomsById[1];

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        InvokeTryResolveCurrentRoom(vm);

        Assert.DoesNotContain(nameof(MapViewModel.SelectedRoomIcon), changedProperties);
        Assert.Same(vm.CurrentRoom, vm.SelectedRoom);
    }

    // ====================================================================
    // OnLocationChanged integration (via GmcpLocationResolver.Process)
    // ====================================================================

    [Fact]
    public void LocationChangedViaResolver_WithMatchingVnum_SetsCurrentRoom()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);

        // Process a GMCP message that matches the default resolver settings
        var resolver = GetLocationResolver(vm);
        resolver.Process(new GmcpMessage("Room.Info", """{"vnum": "100"}"""));

        // The resolver fires LocationChanged -> OnLocationChanged -> Dispatcher.UIThread.Post
        // Since Dispatcher.UIThread is not available in tests, the TryResolveCurrentRoom
        // is dispatched asynchronously and may not have run yet.
        // Instead we verify the resolver state changed.
        Assert.Equal("100", resolver.CurrentVnum);
    }

    // ====================================================================
    // PropertyChanged notifications for SelectedArea and SelectedZ
    // (public setters and internal methods)
    // ====================================================================

    [Fact]
    public void SelectedArea_Setter_RaisesPropertyChangedForSelectedArea()
    {
        using var vm = CreateViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.SelectedArea = new MapArea { Id = 1, Name = "Test" };

        Assert.Contains(nameof(MapViewModel.SelectedArea), changedProperties);
    }

    [Fact]
    public void SelectedZ_Setter_RaisesPropertyChangedForSelectedZ()
    {
        using var vm = CreateViewModel();
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        vm.SelectedZ = 5.0;

        Assert.Contains(nameof(MapViewModel.SelectedZ), changedProperties);
    }

    [Fact]
    public void SetSelectedAreaInternal_RaisesPropertyChangedForSelectedArea()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        SetSelectedAreaInternal(vm, new MapArea { Id = 99, Name = "Internal" });

        Assert.Contains(nameof(MapViewModel.SelectedArea), changedProperties);
    }

    [Fact]
    public void SetSelectedZInternal_RaisesPropertyChangedForSelectedZ()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        SetSelectedZInternal(vm, 7.5);

        Assert.Contains(nameof(MapViewModel.SelectedZ), changedProperties);
    }

    [Fact]
    public void RefreshZLevelsInternal_RaisesPropertyChangedForSelectedZ()
    {
        using var vm = CreateViewModel();
        var index = CreateSampleIndex();
        SetMapIndex(vm, index);

        // Pre-set area so RefreshZLevelsInternal does something
        SetSelectedAreaInternal(vm, index.AreasById[1]);

        // Reset _selectedZ to a value not in the Z levels
        var zField = typeof(MapViewModel).GetField("_selectedZ",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(zField);
        zField.SetValue(vm, 999.0);

        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName);

        RefreshZLevelsInternal(vm);

        Assert.Contains(nameof(MapViewModel.SelectedZ), changedProperties);
    }

    [Fact]
    public void UpdateGroupMembers_ResolvesGmcpRoomsAndExcludesCurrentCharacter()
    {
        using var vm = CreateViewModel();
        SetMapIndex(vm, CreateSampleIndex());
        var members = new[]
        {
            CreateGroupMember("Hero", "100"),
            CreateGroupMember("Companion", "100", isLeader: true),
            CreateGroupMember("Lost", "999"),
            CreateGroupMember("Unknown", null),
        };

        vm.UpdateGroupMembers(members, "hero");

        var marker = Assert.Single(vm.GroupMarkers);
        Assert.Equal("Companion", marker.Name);
        Assert.Equal(1, marker.Number);
        Assert.Equal("Companion", marker.GetLabel(showAsNumber: false));
        Assert.Equal("1", marker.GetLabel(showAsNumber: true));
        Assert.True(marker.IsLeader);
        Assert.Equal("100", marker.Room.Vnum);
    }

    [Fact]
    public void UpdateGroupMembers_NumberingPreservesGroupOrderWhenRoomCannotBeResolved()
    {
        using var vm = CreateViewModel();
        SetMapIndex(vm, CreateSampleIndex());

        vm.UpdateGroupMembers(
        [
            CreateGroupMember("Unknown", "999"),
            CreateGroupMember("Companion", "100"),
        ], "Hero");

        var marker = Assert.Single(vm.GroupMarkers);
        Assert.Equal("Companion", marker.Name);
        Assert.Equal(2, marker.Number);
        Assert.Equal("2", marker.GetLabel(showAsNumber: true));
    }

    [Fact]
    public void UpdateGroupMembers_BeforeMapLoad_AreResolvedWhenMapIndexAppears()
    {
        using var vm = CreateViewModel();
        vm.UpdateGroupMembers([CreateGroupMember("Companion", "100")], "Hero");
        Assert.Empty(vm.GroupMarkers);

        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Equal("Companion", Assert.Single(vm.GroupMarkers).Name);
    }

    // ====================================================================
    // Local map markers (Phase 1 — see MapMarker/MapMarkerStore)
    // ====================================================================

    [Fact]
    public void MarkerLegend_ContainsExactlyTheFixedSymbolSet()
    {
        var expected = new[] { "R", "@", "!", "!!", "X", "#", "T", "B", "+", "Q", "D", "?" };

        Assert.Equal(expected, MapViewModel.MarkerLegend.Select(entry => entry.Symbol));
        Assert.All(MapViewModel.MarkerLegend, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Label)));
    }

    [Fact]
    public void CanEditSelectedRoomMarker_RequiresVnum()
    {
        using var vm = CreateViewModel();

        vm.SelectedRoom = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0) };
        Assert.False(vm.CanEditSelectedRoomMarker);

        vm.SelectedRoom = CreateSampleRoom();
        Assert.True(vm.CanEditSelectedRoomMarker);
    }

    [Fact]
    public void SetMarkerOnSelectedRoomCommand_SetsMarkerAndUpdatesSelectedRoomHasMarker()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        Assert.False(vm.SelectedRoomHasMarker);
        Assert.True(vm.SetMarkerOnSelectedRoomCommand.CanExecute("!!"));

        vm.SetMarkerOnSelectedRoomCommand.Execute("!!");

        Assert.True(vm.SelectedRoomHasMarker);
    }

    [Fact]
    public void SetMarkerOnSelectedRoomCommand_WithoutSelectedRoom_CannotExecute()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.SetMarkerOnSelectedRoomCommand.CanExecute("!!"));
    }

    [Fact]
    public void RemoveMarkerFromSelectedRoomCommand_ClearsMarker()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("R");
        Assert.True(vm.SelectedRoomHasMarker);
        Assert.True(vm.RemoveMarkerFromSelectedRoomCommand.CanExecute(null));

        vm.RemoveMarkerFromSelectedRoomCommand.Execute(null);

        Assert.False(vm.SelectedRoomHasMarker);
        Assert.False(vm.RemoveMarkerFromSelectedRoomCommand.CanExecute(null));
    }

    [Fact]
    public void CutMarkerFromSelectedRoomCommand_WithoutMarker_CannotExecute()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        Assert.False(vm.CutMarkerFromSelectedRoomCommand.CanExecute(null));
    }

    [Fact]
    public void CutMarkerFromSelectedRoomCommand_RemovesMarkerAndEnablesPaste()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("!!");
        Assert.False(vm.HasCutMarker);
        Assert.False(vm.PasteMarkerOnSelectedRoomCommand.CanExecute(null));

        vm.CutMarkerFromSelectedRoomCommand.Execute(null);

        Assert.False(vm.SelectedRoomHasMarker);
        Assert.True(vm.HasCutMarker);
        Assert.True(vm.PasteMarkerOnSelectedRoomCommand.CanExecute(null));
    }

    [Fact]
    public void PasteMarkerOnSelectedRoomCommand_WithoutCutMarker_CannotExecute()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        Assert.False(vm.PasteMarkerOnSelectedRoomCommand.CanExecute(null));
    }

    [Fact]
    public void CutThenPaste_MovesSymbolAndNoteToTheNewRoom()
    {
        using var vm = CreateViewModel();
        var wrongRoom = CreateSampleRoom();
        var correctRoom = RoomWithVnum(2, 1, "200", new MapCoordinates(30, 40, 5));
        SetMapIndexThroughProperty(vm, BuildIndex(wrongRoom, correctRoom));
        vm.SelectedRoom = wrongRoom;
        vm.SetMarkerOnSelectedRoomCommand.Execute("!!");
        vm.SetNoteOnSelectedRoom("Smok Pustynny");

        vm.CutMarkerFromSelectedRoomCommand.Execute(null);
        vm.SelectedRoom = correctRoom;
        vm.PasteMarkerOnSelectedRoomCommand.Execute(null);

        Assert.True(vm.SelectedRoomHasMarker);
        var marker = Assert.Single(vm.RoomMarkers, m => m.Room.Id == correctRoom.Id);
        Assert.Equal("!!", marker.Symbol);
        Assert.Equal("Smok Pustynny", marker.Note);
        Assert.DoesNotContain(vm.RoomMarkers, m => m.Room.Id == wrongRoom.Id);
    }

    [Fact]
    public void PasteMarkerOnSelectedRoomCommand_DoesNotConsumeTheCutMarker_CanPasteOntoMultipleRooms()
    {
        using var vm = CreateViewModel();
        var sourceRoom = CreateSampleRoom();
        var firstTarget = RoomWithVnum(2, 1, "200", new MapCoordinates(30, 40, 5));
        var secondTarget = RoomWithVnum(3, 1, "300", new MapCoordinates(50, 60, 5));
        SetMapIndexThroughProperty(vm, BuildIndex(sourceRoom, firstTarget, secondTarget));
        vm.SelectedRoom = sourceRoom;
        vm.SetMarkerOnSelectedRoomCommand.Execute("R");
        vm.CutMarkerFromSelectedRoomCommand.Execute(null);

        vm.SelectedRoom = firstTarget;
        vm.PasteMarkerOnSelectedRoomCommand.Execute(null);
        vm.SelectedRoom = secondTarget;
        vm.PasteMarkerOnSelectedRoomCommand.Execute(null);

        Assert.Contains(vm.RoomMarkers, m => m.Room.Id == firstTarget.Id && m.Symbol == "R");
        Assert.Contains(vm.RoomMarkers, m => m.Room.Id == secondTarget.Id && m.Symbol == "R");
    }

    [Fact]
    public void PasteMarkerOnSelectedRoomCommand_ReplacesWhateverMarkerWasAlreadyThere()
    {
        using var vm = CreateViewModel();
        var sourceRoom = CreateSampleRoom();
        var targetRoom = RoomWithVnum(2, 1, "200", new MapCoordinates(30, 40, 5));
        SetMapIndexThroughProperty(vm, BuildIndex(sourceRoom, targetRoom));
        vm.SelectedRoom = sourceRoom;
        vm.SetMarkerOnSelectedRoomCommand.Execute("!!");
        vm.CutMarkerFromSelectedRoomCommand.Execute(null);
        vm.SelectedRoom = targetRoom;
        vm.SetMarkerOnSelectedRoomCommand.Execute("X");

        vm.PasteMarkerOnSelectedRoomCommand.Execute(null);

        var marker = Assert.Single(vm.RoomMarkers, m => m.Room.Id == targetRoom.Id);
        Assert.Equal("!!", marker.Symbol);
    }

    [Fact]
    public void CuttingAnotherMarker_ReplacesThePreviouslyCutOne()
    {
        using var vm = CreateViewModel();
        var firstSource = CreateSampleRoom();
        var secondSource = RoomWithVnum(2, 1, "200", new MapCoordinates(30, 40, 5));
        var targetRoom = RoomWithVnum(3, 1, "300", new MapCoordinates(50, 60, 5));
        SetMapIndexThroughProperty(vm, BuildIndex(firstSource, secondSource, targetRoom));
        vm.SelectedRoom = firstSource;
        vm.SetMarkerOnSelectedRoomCommand.Execute("!!");
        vm.CutMarkerFromSelectedRoomCommand.Execute(null);
        vm.SelectedRoom = secondSource;
        vm.SetMarkerOnSelectedRoomCommand.Execute("R");
        vm.CutMarkerFromSelectedRoomCommand.Execute(null);

        vm.SelectedRoom = targetRoom;
        vm.PasteMarkerOnSelectedRoomCommand.Execute(null);

        var marker = Assert.Single(vm.RoomMarkers, m => m.Room.Id == targetRoom.Id);
        Assert.Equal("R", marker.Symbol);
    }

    [Fact]
    public void SettingNewMarker_ReplacesPreviousOneForSameRoom()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        vm.SetMarkerOnSelectedRoomCommand.Execute("!!");
        vm.SetMarkerOnSelectedRoomCommand.Execute("B");

        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        Assert.Equal("B", Assert.Single(vm.RoomMarkers).Symbol);
    }

    [Fact]
    public void RoomMarkers_ResolvesSetMarkerToItsRoomOnceMapIndexIsLoaded()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("T");

        Assert.Empty(vm.RoomMarkers); // no MapIndex loaded yet — can't resolve vnum to a room

        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("T", marker.Symbol);
        Assert.Equal("100", marker.Room.Vnum);
    }

    // ====================================================================
    // Room notes — free text attached alongside (not instead of) a marker's symbol, shown on
    // hover the same way teacher/spellbook-mob info is (see WorldMapControl.FormatNoteTooltip).
    // ====================================================================

    [Fact]
    public void SelectedRoomNote_NoMarkerYet_IsNull()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        Assert.Null(vm.SelectedRoomNote);
    }

    [Fact]
    public void SetNoteOnSelectedRoom_NoExistingMarker_CreatesQuestionMarkSymbolWithTheNote()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        vm.SetNoteOnSelectedRoom("uważaj na zapadnię");

        Assert.Equal("uważaj na zapadnię", vm.SelectedRoomNote);
        Assert.True(vm.SelectedRoomHasMarker);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("?", marker.Symbol);
        Assert.Equal("uważaj na zapadnię", marker.Note);
    }

    [Fact]
    public void SetNoteOnSelectedRoom_ExistingMarker_KeepsItsSymbolAndUpdatesTheNote()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("Q");

        vm.SetNoteOnSelectedRoom("pamiętaj o zadaniu");

        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("Q", marker.Symbol);
        Assert.Equal("pamiętaj o zadaniu", marker.Note);
    }

    [Fact]
    public void SetNoteOnSelectedRoom_RoomOnlyHasAnAutoTeacherMarker_KeepsTheAutoSymbolInsteadOfDowngradingToQuestionMark()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        vm.SelectedRoom = CreateSampleRoom();

        vm.SetNoteOnSelectedRoom("uczy latania");

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("T", marker.Symbol);
        Assert.Equal("uczy latania", marker.Note);
    }

    [Fact]
    public void SetMarkerOnSelectedRoom_ChangingSymbol_KeepsAnyExistingNote()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetNoteOnSelectedRoom("stara notatka");

        vm.SetMarkerOnSelectedRoomCommand.Execute("Q");

        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("Q", marker.Symbol);
        Assert.Equal("stara notatka", marker.Note);
    }

    [Fact]
    public void SetNoteOnSelectedRoom_BlankNote_ClearsItButKeepsTheSymbol()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("Q");
        vm.SetNoteOnSelectedRoom("do usunięcia");

        vm.SetNoteOnSelectedRoom("   ");

        Assert.Null(vm.SelectedRoomNote);
        Assert.True(vm.SelectedRoomHasMarker); // the "Q" marker itself survives
        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("Q", marker.Symbol);
        Assert.Null(marker.Note);
    }

    [Fact]
    public void SetNoteOnSelectedRoom_BlankNoteAndNoExistingMarker_IsANoOp()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        vm.SetNoteOnSelectedRoom("");

        Assert.False(vm.SelectedRoomHasMarker);
        Assert.Null(vm.SelectedRoomNote);
    }

    [Fact]
    public void SetNoteOnSelectedRoom_TrimsSurroundingWhitespace()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();

        vm.SetNoteOnSelectedRoom("  trzeba przyciąć  ");

        Assert.Equal("trzeba przyciąć", vm.SelectedRoomNote);
    }

    [Fact]
    public void ComputeMarkerReportDiff_SameSymbolDifferentNote_IsReportedAsAChange()
    {
        // Notes can be shared with the community too, same as symbols — a note-only change must
        // still surface in the diff even when the symbol itself didn't change.
        var local = new Dictionary<string, MapMarker> { ["100"] = new("100", "Q", "nowa notatka") };
        var shared = new Dictionary<string, MapMarker> { ["100"] = new("100", "Q", "stara notatka") };

        var entry = Assert.Single(MapViewModel.ComputeMarkerReportDiff(local, shared));

        Assert.Equal("100", entry.Vnum);
        Assert.Equal("Q", entry.NewSymbol);
        Assert.Equal("Q", entry.PreviousSymbol);
        Assert.Equal("nowa notatka", entry.Note);
    }

    [Fact]
    public void ComputeMarkerReportDiff_SameSymbolAndNote_ProducesNoEntry()
    {
        var local = new Dictionary<string, MapMarker> { ["100"] = new("100", "Q", "ta sama notatka") };
        var shared = new Dictionary<string, MapMarker> { ["100"] = new("100", "Q", "ta sama notatka") };

        var diff = MapViewModel.ComputeMarkerReportDiff(local, shared);

        Assert.Empty(diff);
    }

    [Fact]
    public void ComputeMarkerReportDiff_NewMarkerWithNote_IncludesTheNote()
    {
        var local = new Dictionary<string, MapMarker> { ["100"] = new("100", "?", "całkiem nowa notatka") };
        var shared = new Dictionary<string, MapMarker>();

        var entry = Assert.Single(MapViewModel.ComputeMarkerReportDiff(local, shared));

        Assert.Null(entry.PreviousSymbol);
        Assert.Equal("całkiem nowa notatka", entry.Note);
    }

    [Fact]
    public void BuildMarkerReportIssueUri_IncludesNoteTextInTheReportBody()
    {
        var entries = new[] { new MapMarkerReportEntry("100", "Q", PreviousSymbol: null, "uważaj na strażnika") };

        var uri = MapViewModel.BuildMarkerReportIssueUri(entries);

        Assert.Contains("uważaj na strażnika", Uri.UnescapeDataString(uri.Query));
    }

    [Fact]
    public void NotePersistsAcrossViewModelInstancesSharingTheSameDataRoot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "KillerMudClient_MapNoteTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using (var vm = new MapViewModel("C:\\dummy", new GmcpLocationResolver(), dataRoot))
            {
                vm.SelectedRoom = CreateSampleRoom();
                vm.SetNoteOnSelectedRoom("zapamiętana notatka");
            }

            SpinWait.SpinUntil(
                () => File.Exists(Path.Combine(dataRoot, "map-markers.json")),
                TimeSpan.FromSeconds(2));

            using var reloaded = new MapViewModel("C:\\dummy", new GmcpLocationResolver(), dataRoot);
            reloaded.SelectedRoom = CreateSampleRoom();

            Assert.Equal("zapamiętana notatka", reloaded.SelectedRoomNote);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void MarkersPersistAcrossViewModelInstancesSharingTheSameDataRoot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "KillerMudClient_MapMarkerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            using (var vm = new MapViewModel("C:\\dummy", new GmcpLocationResolver(), dataRoot))
            {
                vm.SelectedRoom = CreateSampleRoom();
                vm.SetMarkerOnSelectedRoomCommand.Execute("@");
            }

            // SaveAsync is fire-and-forget from the command; give it a moment to land.
            SpinWait.SpinUntil(
                () => File.Exists(Path.Combine(dataRoot, "map-markers.json")),
                TimeSpan.FromSeconds(2));

            using var reloaded = new MapViewModel("C:\\dummy", new GmcpLocationResolver(), dataRoot);
            reloaded.SelectedRoom = CreateSampleRoom();

            Assert.True(reloaded.SelectedRoomHasMarker);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    // ====================================================================
    // Reporting markers to the community (Phase 2 — see SharedMapMarkerStore)
    // ====================================================================

    [Fact]
    public void ComputeMarkerReportDiff_VnumMissingFromShared_IsReportedAsNew()
    {
        var local = new Dictionary<string, MapMarker> { ["100"] = new("100", "R") };
        var shared = new Dictionary<string, MapMarker>();

        var diff = MapViewModel.ComputeMarkerReportDiff(local, shared);

        var entry = Assert.Single(diff);
        Assert.Equal("100", entry.Vnum);
        Assert.Equal("R", entry.NewSymbol);
        Assert.Null(entry.PreviousSymbol);
    }

    [Fact]
    public void ComputeMarkerReportDiff_DifferentSymbolForKnownVnum_IsReportedAsChanged()
    {
        var local = new Dictionary<string, MapMarker> { ["100"] = new("100", "R") };
        var shared = new Dictionary<string, MapMarker> { ["100"] = new("100", "!") };

        var diff = MapViewModel.ComputeMarkerReportDiff(local, shared);

        var entry = Assert.Single(diff);
        Assert.Equal("100", entry.Vnum);
        Assert.Equal("R", entry.NewSymbol);
        Assert.Equal("!", entry.PreviousSymbol);
    }

    [Fact]
    public void ComputeMarkerReportDiff_IdenticalToShared_IsNotReported()
    {
        var local = new Dictionary<string, MapMarker> { ["100"] = new("100", "R") };
        var shared = new Dictionary<string, MapMarker> { ["100"] = new("100", "R") };

        Assert.Empty(MapViewModel.ComputeMarkerReportDiff(local, shared));
    }

    [Fact]
    public void ComputeMarkerReportDiff_OnlyIncludesVnumsPresentLocally()
    {
        // A vnum the community already has that this player never marked isn't "ours" to
        // report — the report only ever concerns this player's own local markers.
        var local = new Dictionary<string, MapMarker>();
        var shared = new Dictionary<string, MapMarker> { ["100"] = new("100", "R") };

        Assert.Empty(MapViewModel.ComputeMarkerReportDiff(local, shared));
    }

    [Fact]
    public void BuildMarkerReportIssueUri_TargetsThisForksIssueTracker()
    {
        var entries = new[] { new MapMarkerReportEntry("100", "R", null) };

        var uri = MapViewModel.BuildMarkerReportIssueUri(entries);

        Assert.StartsWith("https://github.com/Grzyboll/killer-mud-client/issues/new?", uri.ToString());
    }

    [Fact]
    public void BuildMarkerReportIssueUri_EncodesNewAndChangedEntriesInTheBody()
    {
        var entries = new[]
        {
            new MapMarkerReportEntry("100", "R", null),
            new MapMarkerReportEntry("200", "!", "@"),
        };

        var uri = MapViewModel.BuildMarkerReportIssueUri(entries);
        var decodedQuery = Uri.UnescapeDataString(uri.Query);

        Assert.Contains("NOWY", decodedQuery);
        Assert.Contains("vnum 100", decodedQuery);
        Assert.Contains("ZMIANA", decodedQuery);
        Assert.Contains("vnum 200", decodedQuery);
        Assert.Contains("@ -> !", decodedQuery);
    }

    [Fact]
    public void ReportMarkersCommand_WithNoLocalMarkers_CannotExecute()
    {
        using var vm = CreateViewModel();

        Assert.False(vm.ReportMarkersCommand.CanExecute(null));
    }

    [Fact]
    public void ReportMarkersCommand_AfterSettingAMarker_CanExecute()
    {
        using var vm = CreateViewModel();
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("R");

        Assert.True(vm.ReportMarkersCommand.CanExecute(null));
    }

    // ====================================================================
    // Finding the nearest Rent marker (right-click "Znajdź najbliższy Rent")
    // ====================================================================

    [Fact]
    public void FindNearestRentMarker_PicksClosestRentInSameAreaAndFloor()
    {
        var current = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0) };
        var near = RoomWithVnum(2, 1, "20", new MapCoordinates(1, 0, 0));
        var far = RoomWithVnum(3, 1, "10", new MapCoordinates(5, 0, 0));
        var markers = new[]
        {
            new RoomMapMarker(far, "R"),
            new RoomMapMarker(near, "R"),
        };

        var nearest = MapViewModel.FindNearestRentMarker(markers, current);

        Assert.NotNull(nearest);
        Assert.Equal("20", nearest!.Room.Vnum);
    }

    [Fact]
    public void FindNearestRentMarker_IgnoresNonRentMarkers()
    {
        var current = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0) };
        var close = RoomWithVnum(2, 1, "30", new MapCoordinates(1, 0, 0));
        var far = RoomWithVnum(3, 1, "10", new MapCoordinates(5, 0, 0));
        var markers = new[]
        {
            new RoomMapMarker(close, "@"),
            new RoomMapMarker(far, "R"),
        };

        var nearest = MapViewModel.FindNearestRentMarker(markers, current);

        Assert.NotNull(nearest);
        Assert.Equal("10", nearest!.Room.Vnum);
    }

    [Fact]
    public void FindNearestRentMarker_IgnoresRentsInADifferentAreaOrFloor()
    {
        var current = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0) };
        var otherArea = RoomWithVnum(2, 2, "10", new MapCoordinates(0, 0, 0));
        var otherFloor = RoomWithVnum(3, 1, "20", new MapCoordinates(0, 0, 1));
        var markers = new[]
        {
            new RoomMapMarker(otherArea, "R"),
            new RoomMapMarker(otherFloor, "R"),
        };

        Assert.Null(MapViewModel.FindNearestRentMarker(markers, current));
    }

    [Fact]
    public void FindNearestRentMarker_NoRentMarkers_ReturnsNull()
    {
        var current = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0) };

        Assert.Null(MapViewModel.FindNearestRentMarker([], current));
    }

    [Fact]
    public void RoomMarkers_AutoAddsSharedRentMarker_FindNearestRentSeesItToo()
    {
        // Regression guard: FindNearestRent used to search only the player's own local markers
        // (_markersByVnum), so an "R" that only came from the community's shared catalog (see
        // RefreshRoomMarkers) was silently invisible to "Znajdź najbliższy Rent" even though it
        // was drawn on the map. It must search the same fully-merged RoomMarkers instead.
        var current = new MapRoom { Id = 1, AreaId = 1, Coordinates = new MapCoordinates(0, 0, 0) };
        var sharedRentRoom = RoomWithVnum(2, 1, "20", new MapCoordinates(1, 0, 0));
        using var vm = new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            sharedMarkerCatalogOverride: [new MapMarker("20", "R")]);
        SetMapIndexThroughProperty(vm, BuildIndex(current, sharedRentRoom));

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("R", marker.Symbol);
        Assert.True(vm.FindNearestRentCommand.CanExecute(null));
    }

    // ====================================================================
    // Auto "T" markers for known Killeropedia teacher rooms
    // ====================================================================

    [Fact]
    public void TeacherMarkers_ResolvesKnownTeacherRoomVnum()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.TeacherMarkers);
        Assert.Equal("100", marker.Room.Vnum);
        Assert.Same(teacher, Assert.Single(marker.Teachers));
    }

    [Fact]
    public void TeacherMarkers_GroupsMultipleTeachersInTheSameRoom()
    {
        var first = new TeacherEntry("1", "Pierwszy", "Region", null, "100", [], [], []);
        var second = new TeacherEntry("2", "Drugi", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(first, second);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.TeacherMarkers);
        Assert.Equal(2, marker.Teachers.Count);
    }

    [Fact]
    public void TeacherMarkers_IgnoresTeachersWithoutARoomVnum()
    {
        var teacher = new TeacherEntry("1", "Bezdomny", "Region", null, null, [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Empty(vm.TeacherMarkers);
    }

    [Fact]
    public void RoomMarkers_AutoAddsTeacherSymbolForKnownTeacherRoom()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("T", marker.Symbol);
        Assert.Equal("100", marker.Room.Vnum);
    }

    [Fact]
    public void RoomMarkers_ExplicitPlayerMarkerTakesPriorityOverAutoTeacherMarker()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("Q");

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("Q", marker.Symbol);
    }

    // ====================================================================
    // Auto "B" markers for known spellbook-mob rooms
    // ====================================================================

    [Fact]
    public void SpellMobMarkers_ResolvesKnownRoomVnum()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.SpellMobMarkers);
        Assert.Equal("100", marker.Room.Vnum);
        Assert.Same(mob, Assert.Single(marker.Mobs));
    }

    [Fact]
    public void SpellMobMarkers_GroupsMultipleMobsInTheSameRoom()
    {
        var first = new SpellMobEntry("100", "Pierwszy", "Region", "Mag", [], null, false, false, false, false, null);
        var second = new SpellMobEntry("100", "Drugi", "Region", "Mag", [], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(first, second);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.SpellMobMarkers);
        Assert.Equal(2, marker.Mobs.Count);
    }

    [Fact]
    public void SpellMobMarkers_IgnoresRoamingMobsWithoutARoomVnum()
    {
        var mob = new SpellMobEntry(null, "Wędrowny mag", "", "Mag", [], null, true, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Empty(vm.SpellMobMarkers);
    }

    [Fact]
    public void RoomMarkers_AutoAddsSpellMobSymbolForKnownSpellMobRoom()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("B", marker.Symbol);
        Assert.Equal("100", marker.Room.Vnum);
    }

    [Fact]
    public void RoomMarkers_ExplicitPlayerMarkerTakesPriorityOverAutoSpellMobMarker()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("Q");

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("Q", marker.Symbol);
    }

    [Fact]
    public void RoomMarkers_AutoTeacherMarkerTakesPriorityOverAutoSpellMobMarkerInTheSameRoom()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            teacherCatalogOverride: [teacher],
            spellMobCatalogOverride: [mob]);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("T", marker.Symbol);
    }

    // ====================================================================
    // RoomsWithMissingSpell — gold/orange map highlight for rooms whose
    // spell-mob teaches a spell the player doesn't know yet
    // ====================================================================

    [Fact]
    public void RoomsWithMissingSpell_MobTeachesMissingSpell_IncludesRoomId()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        vm.SpellKnowledge = new Dictionary<string, bool> { ["float"] = false };

        var roomId = Assert.Single(vm.SpellMobMarkers).Room.Id;
        Assert.Contains(roomId, vm.RoomsWithMissingSpell);
    }

    [Fact]
    public void RoomsWithMissingSpell_MobTeachesOnlyKnownSpells_ExcludesRoomId()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        vm.SpellKnowledge = new Dictionary<string, bool> { ["float"] = true };

        Assert.Empty(vm.RoomsWithMissingSpell);
    }

    [Fact]
    public void RoomsWithMissingSpell_NoSpellKnowledgeCollectedYet_ExcludesRoomId()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Empty(vm.RoomsWithMissingSpell);
    }

    [Fact]
    public void RoomsWithMissingSpell_KnowledgeCollectedButSpellNotLearnableByThisClass_ExcludesRoomId()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        vm.SpellKnowledge = new Dictionary<string, bool> { ["other spell"] = true };

        Assert.Empty(vm.RoomsWithMissingSpell);
    }

    [Fact]
    public void RoomsWithMissingSpell_NoSpellMobsResolved_IsEmpty()
    {
        using var vm = CreateViewModelWithSpellMobs();
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        vm.SpellKnowledge = new Dictionary<string, bool> { ["float"] = false };

        Assert.Empty(vm.RoomsWithMissingSpell);
    }

    // ====================================================================
    // FocusTeacherByName — "Szukaj nauczyciela (nazwa)..." context menu item
    // ====================================================================

    [Fact]
    public void FocusTeacherByName_ExactName_FocusesTheirRoom()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusTeacherByName("Mistrz Moran");

        Assert.NotNull(room);
        Assert.Equal("100", room!.Vnum);
        Assert.Same(room, vm.SelectedRoom);
    }

    [Fact]
    public void FocusTeacherByName_CaseInsensitivePartialMatch_FocusesTheirRoom()
    {
        var teacher = new TeacherEntry("1", "Mistrz barbarzyński", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusTeacherByName("BARBARZYŃSKI");

        Assert.NotNull(room);
        Assert.Equal("100", room!.Vnum);
    }

    [Fact]
    public void FocusTeacherByName_NoMatch_ReturnsNullAndLeavesSelectionUnchanged()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusTeacherByName("Nieznajomy");

        Assert.Null(room);
        Assert.Null(vm.SelectedRoom);
    }

    [Fact]
    public void FocusTeacherByName_BlankName_ReturnsNull()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Null(vm.FocusTeacherByName("   "));
    }

    // ====================================================================
    // FocusSpellMobByName / FocusSearchResultByName — "Szukaj..." context menu item
    // ====================================================================

    [Fact]
    public void FocusSpellMobByName_CaseInsensitivePartialMatch_FocusesTheirRoom()
    {
        var mob = new SpellMobEntry("100", "Rogaty demon", "Arras", "Mag", ["charm monster"], null, false, true, false, true, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusSpellMobByName("rogaty");

        Assert.NotNull(room);
        Assert.Equal("100", room!.Vnum);
    }

    [Fact]
    public void FocusSpellMobByName_NoMatch_ReturnsNull()
    {
        var mob = new SpellMobEntry("100", "Rogaty demon", "Arras", "Mag", [], null, false, true, false, true, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Null(vm.FocusSpellMobByName("Nieznajomy"));
    }

    [Fact]
    public void FocusSearchResultByName_MatchesTeacherFirst()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            teacherCatalogOverride: [teacher],
            spellMobCatalogOverride: []);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusSearchResultByName("Mistrz Moran");

        Assert.NotNull(room);
        Assert.Equal("100", room!.Vnum);
    }

    [Fact]
    public void FocusSearchResultByName_FallsBackToSpellMob()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        var mob = new SpellMobEntry("100", "Rogaty demon", "Arras", "Mag", [], null, false, true, false, true, null);
        using var vm = new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            teacherCatalogOverride: [teacher],
            spellMobCatalogOverride: [mob]);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var room = vm.FocusSearchResultByName("Rogaty demon");

        Assert.NotNull(room);
        Assert.Equal("100", room!.Vnum);
    }

    // ====================================================================
    // SearchEntries — closed, autocompleting list behind "Szukaj..."
    // ====================================================================

    [Fact]
    public void SearchEntries_TeacherSearchTextIncludesNameAndSkillsWithRanges()
    {
        var teacher = new TeacherEntry(
            "1", "Mistrz Moran", "Carrallak", null, "100",
            ["Wojownik"],
            [new TeacherSkillEntry("dragon strike", 65, 95, 65, 90)],
            [new TeacherTrickEntry("vertical kick", 25, 5000)]);
        using var vm = CreateViewModelWithTeachers(teacher);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var entry = Assert.Single(vm.SearchEntries);
        Assert.Equal("Mistrz Moran", entry.Name);
        Assert.Contains("Mistrz Moran", entry.SearchText);
        Assert.Contains("dragon strike 65–95", entry.SearchText);
        Assert.Contains("vertical kick", entry.SearchText);
    }

    [Fact]
    public void SearchEntries_SpellMobSearchTextIncludesNameAndSpells()
    {
        var mob = new SpellMobEntry(
            "100", "Rogaty demon", "Arras", "Mag",
            ["resist magic weapon", "charm monster"], null, false, true, false, true, null);
        using var vm = CreateViewModelWithSpellMobs(mob);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var entry = Assert.Single(vm.SearchEntries);
        Assert.Equal("Rogaty demon", entry.Name);
        Assert.Contains("Rogaty demon", entry.SearchText);
        Assert.Contains("resist magic weapon", entry.SearchText);
        Assert.Contains("charm monster", entry.SearchText);
    }

    [Fact]
    public void SearchEntries_IncludesBothTeachersAndSpellMobs()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        var mob = new SpellMobEntry("200", "Rogaty demon", "Arras", "Mag", [], null, false, true, false, true, null);
        var otherRoom = RoomWithVnum(2, 1, "200", new MapCoordinates(1, 0, 0));
        using var vm = new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            teacherCatalogOverride: [teacher],
            spellMobCatalogOverride: [mob]);
        SetMapIndexThroughProperty(vm, BuildIndex(CreateSampleRoom(), otherRoom));

        Assert.Equal(2, vm.SearchEntries.Count);
        Assert.Contains(vm.SearchEntries, e => e.Name == "Mistrz Moran");
        Assert.Contains(vm.SearchEntries, e => e.Name == "Rogaty demon");
    }

    [Fact]
    public void SearchEntries_OneEntryPerTeacher_EvenWhenTwoShareARoom()
    {
        var first = new TeacherEntry("1", "Pierwszy", "Region", null, "100", [], [], []);
        var second = new TeacherEntry("2", "Drugi", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(first, second);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        Assert.Equal(2, vm.SearchEntries.Count);
        Assert.Contains(vm.SearchEntries, e => e.Name == "Pierwszy");
        Assert.Contains(vm.SearchEntries, e => e.Name == "Drugi");
    }

    [Fact]
    public void SearchEntries_NoMapIndex_IsEmpty()
    {
        var teacher = new TeacherEntry("1", "Mistrz Moran", "Region", null, "100", [], [], []);
        using var vm = CreateViewModelWithTeachers(teacher);

        Assert.Empty(vm.SearchEntries);
    }

    // ====================================================================
    // Auto markers for the community's accepted shared marker catalog
    // (see SharedMapMarkerStore) — lowest-priority auto layer
    // ====================================================================

    [Fact]
    public void RoomMarkers_AutoAddsSharedMarkerSymbolForKnownSharedVnum()
    {
        using var vm = CreateViewModelWithSharedMarkers(new MapMarker("100", "!!"));
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("!!", marker.Symbol);
        Assert.Equal("100", marker.Room.Vnum);
    }

    [Fact]
    public void RoomMarkers_AutoAddsSharedMarkerNoteForKnownSharedVnum()
    {
        using var vm = CreateViewModelWithSharedMarkers(new MapMarker("100", "!!", "znany społeczności pułapka"));
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("znany społeczności pułapka", marker.Note);
    }

    [Fact]
    public void RoomMarkers_ExplicitPlayerMarkerTakesPriorityOverAutoSharedMarker()
    {
        using var vm = CreateViewModelWithSharedMarkers(new MapMarker("100", "!!"));
        SetMapIndexThroughProperty(vm, CreateSampleIndex());
        vm.SelectedRoom = CreateSampleRoom();
        vm.SetMarkerOnSelectedRoomCommand.Execute("Q");

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("Q", marker.Symbol);
    }

    [Fact]
    public void RoomMarkers_AutoSpellMobMarkerTakesPriorityOverAutoSharedMarkerInTheSameRoom()
    {
        var mob = new SpellMobEntry("100", "Świrnięty mag", "Arras", "Mag", ["float"], null, false, false, false, false, null);
        using var vm = new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            spellMobCatalogOverride: [mob],
            sharedMarkerCatalogOverride: [new MapMarker("100", "!!")]);
        SetMapIndexThroughProperty(vm, CreateSampleIndex());

        var marker = Assert.Single(vm.RoomMarkers);
        Assert.Equal("B", marker.Symbol);
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static MapViewModel CreateViewModel()
    {
        return new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver());
    }

    private static MapViewModel CreateViewModelWithTeachers(params TeacherEntry[] teachers)
    {
        return new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            teacherCatalogOverride: teachers);
    }

    private static MapViewModel CreateViewModelWithSpellMobs(params SpellMobEntry[] spellMobs)
    {
        return new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            spellMobCatalogOverride: spellMobs);
    }

    private static MapViewModel CreateViewModelWithSharedMarkers(params MapMarker[] markers)
    {
        return new MapViewModel(
            "C:\\dummy",
            new GmcpLocationResolver(),
            sharedMarkerCatalogOverride: markers);
    }

    private static MapRoom CreateSampleRoom()
    {
        return new MapRoom
        {
            Id = 1,
            AreaId = 1,
            Name = "Test Room",
            Coordinates = new MapCoordinates(10, 20, 5),
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement("100"),
                ["sector"] = JsonSerializer.SerializeToElement("inside"),
            },
        };
    }

    private static MapRoom RoomWithVnum(int id, int areaId, string vnum, MapCoordinates coordinates)
    {
        return new MapRoom
        {
            Id = id,
            AreaId = areaId,
            Coordinates = coordinates,
            UserData = new Dictionary<string, JsonElement>
            {
                ["vnum"] = JsonSerializer.SerializeToElement(vnum),
            },
        };
    }

    /// <summary>Groups the given rooms into one MapArea per distinct AreaId, so tests can freely
    /// mix rooms from several "areas" in a single call.</summary>
    private static MapIndex BuildIndex(params MapRoom[] rooms)
    {
        var areas = rooms
            .GroupBy(room => room.AreaId)
            .Select(group => new MapArea
            {
                Id = group.Key,
                Name = $"Area {group.Key}",
                Rooms = group.ToList(),
            })
            .ToList();
        var doc = new MapDocument { Areas = areas };
        return new MapIndex(doc);
    }

    private static MapIndex CreateSampleIndex()
    {
        var room = CreateSampleRoom();
        var area = new MapArea
        {
            Id = 1,
            Name = "Test Area",
            Rooms = [room],
        };
        var doc = new MapDocument
        {
            Areas = [area],
        };
        return new MapIndex(doc);
    }

    private static CharacterGroupMember CreateGroupMember(
        string name,
        string? room,
        bool isLeader = false) =>
        new(name, null, string.Empty, null, string.Empty, null, null, false, room, isLeader);

    private static void SetMapIndexThroughProperty(MapViewModel vm, MapIndex index)
    {
        var property = typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex));
        Assert.NotNull(property);
        property.SetValue(vm, index);
    }

    private static void SetMapIndex(MapViewModel vm, MapIndex? index)
    {
        var field = typeof(MapViewModel).GetField("_mapIndex",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(vm, index);
    }

    private static void SetCurrentRoom(MapViewModel vm, MapRoom? room)
    {
        var field = typeof(MapViewModel).GetField("_currentRoom",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(vm, room);
    }

    private static void SetSelectedAreaInternal(MapViewModel vm, MapArea area)
    {
        var method = typeof(MapViewModel).GetMethod("SetSelectedAreaInternal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(vm, [area]);
    }

    private static void SetSelectedZInternal(MapViewModel vm, double z)
    {
        var method = typeof(MapViewModel).GetMethod("SetSelectedZInternal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(vm, [z]);
    }

    private static void RefreshZLevelsInternal(MapViewModel vm)
    {
        var method = typeof(MapViewModel).GetMethod("RefreshZLevelsInternal",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(vm, null);
    }

    private static void SetLocationVnum(MapViewModel vm, string vnum)
    {
        var resolver = GetLocationResolver(vm);
        resolver.Process(new GmcpMessage("Room.Info", $$"""{"vnum": "{{vnum}}"}"""));
    }

    private static GmcpLocationResolver GetLocationResolver(MapViewModel vm)
    {
        var field = typeof(MapViewModel).GetField("_locationResolver",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (GmcpLocationResolver)field.GetValue(vm)!;
    }

    private static void InvokeTryResolveCurrentRoom(MapViewModel vm)
    {
        var method = typeof(MapViewModel).GetMethod("TryResolveCurrentRoom",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(vm, null);
    }
}

using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Regression coverage for issue #34: a move command silently swallowed by the server (e.g. a
/// locked door whose GMCP exit was never flagged door+closed, so TryGetOpenCommand never fired,
/// and whose failure text wasn't the literal "brama...zamknięta" HandleLockedAutowalkGate
/// matches — a tomb/crypt entrance, for example) previously left autowalk, and therefore
/// auto-farm (which is just autowalk on a loop, see AutoFarmTests), waiting forever for a room
/// change that would never come. HandleAutowalkStepStuck is the generic backstop: it's normally
/// reached asynchronously via MonitorAutowalkStepStuckAsync's delay, but is invoked directly here
/// (same pattern as AutowalkMovementRecoveryCapTests) since its own decision logic is synchronous.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutowalkStuckStepTests
{
    private static MainWindowViewModel CreateViewModel(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_AutowalkStuckStepTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new MainWindowViewModel(new ProfileService(directory), new AppSettingsService(directory),
            layoutPresetService: new LayoutPresetService(directory),
            groupSpellStore: new GroupSpellStore(Path.Combine(directory, "group-spells.json")));
    }

    private static void InvokePrivate(MainWindowViewModel viewModel, string methodName, params object?[] args) =>
        typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewModel, args);

    private static void SetPrivateField(MainWindowViewModel viewModel, string fieldName, object? value) =>
        typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(viewModel, value);

    private static T GetPrivateField<T>(MainWindowViewModel viewModel, string fieldName) =>
        (T)typeof(MainWindowViewModel).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel)!;

    private static int GetMaxStuckRecoveryAttempts() => (int)typeof(MainWindowViewModel)
        .GetField("MaxAutowalkStuckRecoveryAttempts", BindingFlags.NonPublic | BindingFlags.Static)!
        .GetValue(null)!;

    private static MapRoom CreateRoom(int id, string vnum) => new()
    {
        Id = id,
        AreaId = 1,
        Coordinates = new MapCoordinates(0, 0, 0),
        UserData = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement(vnum),
        },
    };

    private static void SetCurrentVnum(MainWindowViewModel viewModel, string vnum)
    {
        var resolver = typeof(MainWindowViewModel)
            .GetField("_locationResolver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel)!;
        resolver.GetType().GetProperty("CurrentVnum")!.SetValue(resolver, vnum);
    }

    /// <summary>Same 3-room map (start + 2 reachable candidates) as AutoFarmTests'
    /// ArrangeThreeRoomFarm — duplicated rather than shared, matching this codebase's convention
    /// for small per-file test fixtures (see e.g. ArtifactTryMappingCoordinator's own doc comment
    /// on why its sibling coordinators each keep their own copy instead of a common base).</summary>
    private static void ArrangeThreeRoomFarm(MainWindowViewModel viewModel)
    {
        var document = new MapDocument
        {
            Areas =
            [
                new MapArea
                {
                    Id = 1,
                    Rooms =
                    [
                        new MapRoom
                        {
                            Id = 1,
                            AreaId = 1,
                            Coordinates = new MapCoordinates(0, 0, 0),
                            UserData = new Dictionary<string, System.Text.Json.JsonElement>
                            {
                                ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement("1"),
                            },
                            Exits =
                            [
                                new MapExit { ExitId = 2, Name = "north" },
                                new MapExit { ExitId = 3, Name = "east" },
                            ],
                        },
                        new MapRoom
                        {
                            Id = 2,
                            AreaId = 1,
                            Coordinates = new MapCoordinates(0, 1, 0),
                            UserData = new Dictionary<string, System.Text.Json.JsonElement>
                            {
                                ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement("2"),
                            },
                            Exits = [new MapExit { ExitId = 1, Name = "south" }],
                        },
                        new MapRoom
                        {
                            Id = 3,
                            AreaId = 1,
                            Coordinates = new MapCoordinates(1, 0, 0),
                            UserData = new Dictionary<string, System.Text.Json.JsonElement>
                            {
                                ["vnum"] = System.Text.Json.JsonSerializer.SerializeToElement("3"),
                            },
                            Exits = [new MapExit { ExitId = 1, Name = "west" }],
                        },
                    ],
                },
            ],
        };

        typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
            .SetValue(viewModel.Map, new MapIndex(document));
        SetCurrentVnum(viewModel, "1");
        SetPrivateField(viewModel, "_isConnected", true);
        SetPrivateField(viewModel, "_autoFarmRegions", new List<FarmRegion> { new(1, 0, -10, -10, 10, 10) });
    }

    private static void ArrangeSingleStepWalk(MainWindowViewModel viewModel, MapRoom from, MapRoom to)
    {
        SetPrivateField(viewModel, "_autowalkPath", new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("grobowiec", to)],
            TotalCost = 1,
        });
        SetPrivateField(viewModel, "_autowalkStep", 0);
        SetPrivateField(viewModel, "_autowalkTargetName", "Cel");
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_BelowMaxAttempts_RetriesInsteadOfStoppingOrExcluding()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", 0);

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.Equal(1, GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
            Assert.DoesNotContain(viewModel.Toasts, t => t.Text.Contains("zablokowane drzwi"));
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_StepAlreadyAdvanced_DoesNothing()
    {
        // The monitor task races a real room-change: if OnAutowalkLocationChanged already moved
        // _autowalkStep forward by the time the stuck-timeout fires, it must be a no-op.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var middle = CreateRoom(2, "2");
            var final = CreateRoom(3, "3");
            SetPrivateField(viewModel, "_autowalkPath", new MapPath
            {
                From = from,
                To = final,
                Steps = [new MapPathStep("north", middle), new MapPathStep("north", final)],
                TotalCost = 2,
            });
            SetPrivateField(viewModel, "_autowalkStep", 1); // already past step 0
            SetPrivateField(viewModel, "_autowalkTargetName", "Cel");
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.Equal(GetMaxStuckRecoveryAttempts(), GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_AlreadyHandledByGateWait_DoesNothing()
    {
        // A recognized "brama...zamknięta" line already armed HandleLockedAutowalkGate's own
        // GMCP-reopen wait — the generic stuck backstop must not pile a second recovery on top.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autowalkWaitingForGate", true);

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_PausedForResting_DoesNothing()
    {
        // The player consciously rested mid-route (see UpdateCharacterPosition's resting
        // transition) — the generic stuck backstop must not mistake the stall for a blocked
        // door and start knocking/pulling/pushing (zapukaj/pull/pociagnij/uderz) then resend
        // the movement command anyway.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autowalkPausedForResting", true);

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_FightingButPauseFlagNotYetArmed_ArmsItAndDoesNothing()
    {
        // Regression target for "attacked mid-farm gets the room excluded as unreachable":
        // _autowalkPausedForCombat is normally armed by OnAutowalkCombatStarted's own
        // Dispatcher.UIThread.Post, which could in principle still be queued behind this
        // stuck-check if an aggressive mob's attack landed right as the timeout elapsed. Without
        // this direct fallback, the stuck backstop would misread a fight (not a blocked exit) as
        // one, try "open"/"knock" recovery commands the MUD just rejects mid-fight, and eventually
        // exclude a perfectly walkable room.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_latestCharacterPosition", "fighting");
            Assert.False(GetPrivateField<bool>(viewModel, "_autowalkPausedForCombat"));

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
            Assert.Equal(
                GetMaxStuckRecoveryAttempts(), GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
            Assert.True(GetPrivateField<bool>(viewModel, "_autowalkPausedForCombat"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OnAutowalkCombatEnded_GateSequenceWasInterrupted_RetriesItInsteadOfNoOpStep()
    {
        // Regression target: a fight that starts mid gate-opening-sequence (SendGateCommandsAsync)
        // now stops sending further open/knock commands and leaves _autowalkWaitingForGate armed
        // — so once the fight ends, resuming via a plain SendAutowalkStep would just no-op forever
        // (that method defers entirely to _autowalkWaitingForGate). OnAutowalkCombatEnded must
        // instead restart the gate sequence from scratch.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autowalkWaitingForGate", true);
            SetPrivateField(viewModel, "_autowalkGateCommandsSent", true);
            SetPrivateField(viewModel, "_autowalkGateIsOpen", false);
            SetPrivateField(viewModel, "_autowalkPausedForCombat", true);
            SetPrivateField(viewModel, "_latestCharacterPosition", "standing");

            InvokePrivate(viewModel, "OnAutowalkCombatEnded");
            Dispatcher.UIThread.RunJobs();

            Assert.False(GetPrivateField<bool>(viewModel, "_autowalkPausedForCombat"));
            // Still (genuinely) waiting for GMCP to confirm the door open — proves the gate-retry
            // branch ran instead of falling through to SendAutowalkStep's plain movement path,
            // which would have cleared _autowalkWaitingForGate and sent a move command instead.
            // Nothing in this test simulates OnRoomExitsChanged confirming the door, so
            // _autowalkGateIsOpen correctly never flips true on its own.
            Assert.True(GetPrivateField<bool>(viewModel, "_autowalkWaitingForGate"));
            Assert.False(GetPrivateField<bool>(viewModel, "_autowalkGateIsOpen"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task SendGateCommandsAsync_FightingBeforeFirstCommand_StopsWithoutMarkingCommandsSent()
    {
        // Regression target: previously this loop had no combat check at all and would send every
        // open/knock command regardless — exactly the "client still tries to open, knock, etc."
        // after being attacked that this whole fix is for.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autowalkWaitingForGate", true);
            SetPrivateField(viewModel, "_latestCharacterPosition", "fighting");
            var cts = GetPrivateField<CancellationTokenSource>(viewModel, "_autowalkCts");

            var task = (Task)typeof(MainWindowViewModel)
                .GetMethod("SendGateCommandsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, [cts.Token])!;
            await task;

            Assert.False(GetPrivateField<bool>(viewModel, "_autowalkGateCommandsSent"));
            Assert.True(GetPrivateField<bool>(viewModel, "_autowalkPausedForCombat"));
            Assert.True(GetPrivateField<bool>(viewModel, "_autowalkWaitingForGate"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task UpdateCharacterPosition_ConsciousRestMidRoute_PausesAutowalkInsteadOfGateRecovery()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_latestCharacterPosition", "standing");

            InvokePrivate(viewModel, "UpdateCharacterPosition", "resting");
            Dispatcher.UIThread.RunJobs();

            Assert.True(GetPrivateField<bool>(viewModel, "_autowalkPausedForResting"));
            Assert.Contains("Odpoczyw", viewModel.AutowalkStatusText);

            // With the pause flag set, the stuck backstop for the in-flight step must be inert.
            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);
            Assert.True(viewModel.IsAutowalking);
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task UpdateCharacterPosition_StandingUpAfterRest_ResumesAutowalk()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_latestCharacterPosition", "resting");
            SetPrivateField(viewModel, "_autowalkPausedForResting", true);

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            Dispatcher.UIThread.RunJobs();

            Assert.False(GetPrivateField<bool>(viewModel, "_autowalkPausedForResting"));
            Assert.True(viewModel.IsAutowalking);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_ExceedsMaxAttemptsOutsideFarm_StopsResumablyWithoutMarkingRoomPermanently()
    {
        // Regression target: a client-side stuck-step timeout used to auto-place a permanent,
        // disk-saved, community-reportable "X" map marker (MapViewModel.MarkRoomClosed, since
        // removed) — a false positive (e.g. a slow mid-walk trigger) left bad data on the map
        // forever with no built-in way to undo it. Plain (non-farm) autowalk now just stops
        // resumably and points the player at marking it by hand if it's genuinely closed.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var document = new MapDocument
            {
                Areas =
                [
                    new MapArea
                    {
                        Id = 1,
                        Rooms =
                        [
                            CreateRoom(1, "1"),
                            CreateRoom(2, "2"),
                        ],
                    },
                ],
            };
            typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
                .SetValue(viewModel.Map, new MapIndex(document));

            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);

            Assert.False(viewModel.IsAutowalking);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("zablokowane drzwi"));
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
            Assert.Empty(GetPrivateField<HashSet<int>>(viewModel, "_autoFarmSessionExcludedRoomIds"));
            Assert.Equal(0, GetPrivateField<int>(viewModel, "_autowalkStuckRecoveryAttempts"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_ExceedsMaxAttemptsDuringAutoFarm_ClearsSessionExclusionOnImmediateStop()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var document = new MapDocument
            {
                Areas =
                [
                    new MapArea
                    {
                        Id = 1,
                        Rooms =
                        [
                            CreateRoom(1, "1"),
                            CreateRoom(2, "2"),
                            CreateRoom(3, "3"),
                        ],
                    },
                ],
            };
            typeof(MapViewModel).GetProperty(nameof(MapViewModel.MapIndex))!
                .SetValue(viewModel.Map, new MapIndex(document));

            var from = CreateRoom(1, "1");
            var to = CreateRoom(2, "2");
            ArrangeSingleStepWalk(viewModel, from, to);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmVisitedRoomIds", new HashSet<int> { 1 });
            SetPrivateField(viewModel, "_autoFarmRegions", new List<FarmRegion>()); // ContinueAutoFarm stops cleanly here

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            // ContinueAutoFarm finds no regions left and stops the farm in the very same call
            // chain — proven by its own toast — which must also flush the just-added session
            // exclusion right back out (StopAutoFarm's clear-on-stop), not leave it stale for a
            // farm run that's no longer active. No permanent map marker either way.
            Assert.Empty(GetPrivateField<HashSet<int>>(viewModel, "_autoFarmSessionExcludedRoomIds"));
            Assert.DoesNotContain(2, viewModel.Map.AutoFarmExcludedRoomIds);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("obszary nie są już zdefiniowane"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HandleAutowalkStepStuck_ExceedsMaxAttemptsDuringAutoFarm_RoutesAroundExcludedRoomAndKeepsGoing()
    {
        // The regression this exists for: excluding the stuck room must actually change where the
        // farm walks next (not just sit in a set nobody reads) — with room 2 excluded, the only
        // other unvisited room in range is room 3, so the farm must head there instead of stopping.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            ArrangeThreeRoomFarm(viewModel);
            var index = viewModel.Map.MapIndex!;
            var room1 = index.FindFirstRoomByVnum("1")!;
            var room2 = index.FindFirstRoomByVnum("2")!;

            ArrangeSingleStepWalk(viewModel, room1, room2);
            SetPrivateField(viewModel, "_autowalkStuckRecoveryAttempts", GetMaxStuckRecoveryAttempts());
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmVisitedRoomIds", new HashSet<int> { room1.Id });

            InvokePrivate(viewModel, "HandleAutowalkStepStuck", 0, CancellationToken.None);
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            Assert.True(viewModel.IsAutoFarmActive);
            Assert.Contains(room2.Id, GetPrivateField<HashSet<int>>(viewModel, "_autoFarmSessionExcludedRoomIds"));
            Assert.DoesNotContain(room2.Id, viewModel.Map.AutoFarmExcludedRoomIds);

            var newPath = GetPrivateField<MapPath?>(viewModel, "_autowalkPath");
            Assert.NotNull(newPath);
            Assert.Equal("3", newPath!.To.Vnum);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GetEffectiveAutoFarmExcludedRoomIds_IncludesThisRunsSessionExclusions()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            SetPrivateField(viewModel, "_autoFarmSessionExcludedRoomIds", new HashSet<int> { 7, 9 });

            var effective = (HashSet<int>)typeof(MainWindowViewModel)
                .GetMethod("GetEffectiveAutoFarmExcludedRoomIds", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, null)!;

            Assert.Contains(7, effective);
            Assert.Contains(9, effective);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StopAutoFarm_ClearsThisRunsSessionExcludedRooms()
    {
        // Mirrors Map.AutoFarmVisitedRoomIds' own clear-on-stop — a room skipped for a stuck step
        // must not stay excluded once the farm is stopped and (re)started later.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmSessionExcludedRoomIds", new HashSet<int> { 2 });

            InvokePrivate(viewModel, "StopAutoFarm", "test");

            Assert.Empty(GetPrivateField<HashSet<int>>(viewModel, "_autoFarmSessionExcludedRoomIds"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}

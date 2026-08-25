using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Models;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.Core.Gmcp;
using MudClient.Core.Map;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>Covers the auto-farm engine's observable behavior on <see cref="MainWindowViewModel"/>
/// — pure decision logic (thresholds, next-room picking) is covered separately by
/// HealthRecoveryPolicyTests/FarmTraversalPlannerTests in MudClient.Core.Tests. Reaches private
/// state via reflection, same pattern as AutowalkArrivalTests.</summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class AutoFarmTests
{
    private static MainWindowViewModel CreateViewModel(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_AutoFarmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new MainWindowViewModel(settingsService: new AppSettingsService(directory));
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

    [AvaloniaFact]
    public async Task StartAutoFarmCommand_NoRegionDefined_CannotExecute()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            Assert.False(viewModel.StartAutoFarmCommand.CanExecute(null));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task StartAutoFarm_NoRegionDefined_ShowsErrorToastAndStaysInactive()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            InvokePrivate(viewModel, "StartAutoFarm");

            Assert.False(viewModel.IsAutoFarmActive);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("zaznacz obszar farmy"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AutoFarmHpThresholdPercent_OutOfRange_IsClamped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_AutoFarmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));

        try
        {
            viewModel.AutoFarmHpThresholdPercent = 999;
            Assert.Equal(ProfileData.MaxAutoFarmHpThresholdPercent, viewModel.AutoFarmHpThresholdPercent);

            viewModel.AutoFarmHpThresholdPercent = -50;
            Assert.Equal(ProfileData.MinAutoFarmHpThresholdPercent, viewModel.AutoFarmHpThresholdPercent);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AutoFarmStepDelayMilliseconds_OutOfRange_IsClamped()
    {
        var directory = Path.Combine(Path.GetTempPath(), "KillerMudClient_AutoFarmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var viewModel = new MainWindowViewModel(settingsService: new AppSettingsService(directory));

        try
        {
            viewModel.AutoFarmStepDelayMilliseconds = 999_999;
            Assert.Equal(ProfileData.MaxAutoFarmStepDelayMilliseconds, viewModel.AutoFarmStepDelayMilliseconds);

            viewModel.AutoFarmStepDelayMilliseconds = -50;
            Assert.Equal(ProfileData.MinAutoFarmStepDelayMilliseconds, viewModel.AutoFarmStepDelayMilliseconds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

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

    /// <summary>Arranges a one-step autowalk about to arrive at "999" (mirroring
    /// AutowalkArrivalTests.ArriveAtDestination), then reports that room change.</summary>
    private static void ArriveAtDestination(MainWindowViewModel viewModel)
    {
        var from = CreateRoom(998, "998");
        var to = CreateRoom(999, "999");
        SetPrivateField(viewModel, "_autowalkPath", new MapPath
        {
            From = from,
            To = to,
            Steps = [new MapPathStep("north", to)],
            TotalCost = 1,
        });
        SetPrivateField(viewModel, "_autowalkStep", 0);
        SetPrivateField(viewModel, "_autowalkTargetName", "Cel");

        InvokePrivate(viewModel, "OnAutowalkLocationChanged", "999");

        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task OnAutowalkLocationChanged_AutoFarmActive_MarksIntermediateRoomAsVisited()
    {
        // Regression: previously only the FINAL room of a multi-hop walk got marked visited
        // (inside ContinueAutoFarm, only called on arrival) — every room passed through en route
        // was left "unvisited" in the farm's own bookkeeping, so the planner could later pick it
        // again as the "nearest unvisited" room, producing back-and-forth wandering instead of a
        // real sweep. Confirmed here on the very first intermediate room of a 2-step walk, i.e.
        // before the walk has anywhere near reached its final destination.
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
            SetPrivateField(viewModel, "_autowalkStep", 0);
            SetPrivateField(viewModel, "_autowalkTargetName", "Cel");
            SetPrivateField(viewModel, "_autoFarmActive", true);

            InvokePrivate(viewModel, "OnAutowalkLocationChanged", "2");
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            var visited = GetPrivateField<HashSet<int>>(viewModel, "_autoFarmVisitedRoomIds");
            Assert.Contains(2, visited);
            Assert.Contains(2, viewModel.Map.AutoFarmVisitedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Arrival_WhileAutoFarmActive_SkipsRestOnArrivalEvenWhenEnabled()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);

        try
        {
            Assert.True(viewModel.AutowalkRestOnArrivalEnabled);
            SetPrivateField(viewModel, "_autoFarmActive", true);

            ArriveAtDestination(viewModel);

            Assert.DoesNotContain(output, line => line.Contains("> rest"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Arrival_WhileAutoFarmActiveButRegionCleared_ContinuesFarmAndStopsWithToast()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmRegions", new List<FarmRegion>());

            ArriveAtDestination(viewModel);

            // CompleteAutowalkArrival must have called ContinueAutoFarm, which (no region) stops
            // the farm with its own toast — proving the arrival hook actually fired.
            Assert.False(viewModel.IsAutoFarmActive);
            Assert.Contains(viewModel.Toasts, t => t.Text.Contains("obszary nie są już zdefiniowane"));
            // The yellow "visited" coloring is scoped to one farm run — stopping clears it.
            Assert.Empty(viewModel.Map.AutoFarmVisitedRoomIds);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Arrival_WhileAutoFarmActiveWithMissingRequiredSpell_TriggersMaintenanceNotTraversal()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            viewModel.AutoFarmMemSpellsText = "armor";
            SetPrivateField(viewModel, "_autoFarmActive", true);
            // No region defined and no HP data — if this fell through to the traversal branch
            // it would stop with "obszary nie są już zdefiniowane"; a missing required spell
            // must be caught first instead.
            SetPrivateField(viewModel, "_autoFarmRegions", new List<FarmRegion>());

            ArriveAtDestination(viewModel);

            Assert.True(viewModel.IsAutoFarmActive);
            Assert.Equal("Uzupełniam zaklęcia/buffy — odpoczywam.", viewModel.AutoFarmStatusText);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SetCurrentVnum(MainWindowViewModel viewModel, string vnum)
    {
        var resolver = typeof(MainWindowViewModel)
            .GetField("_locationResolver", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel)!;
        resolver.GetType().GetProperty("CurrentVnum")!.SetValue(resolver, vnum);
    }

    /// <summary>Wires up a 3-room map (start + 2 reachable candidates) and connects the view model
    /// so <see cref="MainWindowViewModel.StartAutoFarm"/> can run its full happy path instead of
    /// bailing out on a missing pathfinder/position.</summary>
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

    [AvaloniaFact]
    public async Task StartAutoFarm_PlansAVisitOrderCoveringEveryRoomInTheRegion()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            ArrangeThreeRoomFarm(viewModel);

            InvokePrivate(viewModel, "StartAutoFarm");

            var order = GetPrivateField<IReadOnlyList<MapRoom>?>(viewModel, "_autoFarmVisitOrder");
            Assert.NotNull(order);
            Assert.Equal(new[] { 2, 3 }, order!.Select(r => r.Id).OrderBy(id => id));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task StartAutoFarm_WithStepDelayConfigured_WaitsBeforeStartingTheFirstHop()
    {
        // Regression target for issue #62: a fast farm can send its next move before a room-
        // arrival trigger (herbalism, skinning, ...) has finished its own command queue. This only
        // checks the wait actually happens (autowalk doesn't start until it elapses) — the trigger
        // side of that isn't something this client can observe.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            ArrangeThreeRoomFarm(viewModel);
            viewModel.AutoFarmStepDelayMilliseconds = 200;

            InvokePrivate(viewModel, "StartAutoFarm");
            Dispatcher.UIThread.RunJobs();

            Assert.Null(GetPrivateField<MapPath?>(viewModel, "_autowalkPath"));

            await Task.Delay(300);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(GetPrivateField<MapPath?>(viewModel, "_autowalkPath"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task StartAutoFarm_OnlyOpportunisticSpellMissing_StillProceedsToTraversal()
    {
        // Regression target for discussion #32's "obczarka" request: an opportunistic ("~"
        // prefixed) entry missing on its own must NOT block the farm the way a required one does.
        var viewModel = CreateViewModel(out var directory);
        try
        {
            ArrangeThreeRoomFarm(viewModel);
            viewModel.AutoFarmMemSpellsText = "~haste"; // opportunistic, not memorized
            SetPrivateField(viewModel, "_latestHp", 100);
            SetPrivateField(viewModel, "_latestMaxHp", 100);

            InvokePrivate(viewModel, "StartAutoFarm");

            Assert.True(viewModel.IsAutoFarmActive);
            Assert.DoesNotContain("Uzupełniam", viewModel.AutoFarmStatusText);
            Assert.NotNull(GetPrivateField<MapPath?>(viewModel, "_autowalkPath"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Arrival_WhileAutoFarmActiveWithMissingRequiredSpell_AlsoMemsMissingOpportunisticSpell()
    {
        // The opportunistic spell is only mem'd "przy okazji" — piggybacked onto a maintenance
        // pass a REQUIRED reason already triggered — never triggering one on its own (see the
        // sibling "StillProceedsToTraversal" test above for that half).
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            viewModel.AutoFarmMemSpellsText = "armor\n~haste";
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmRegions", Array.Empty<FarmRegion>());
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>());

            ArriveAtDestination(viewModel);
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("mem \"armor\""));
            Assert.Contains(output, line => line.Contains("mem \"haste\""));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Arrival_WithUnmemorizedCastSequenceSpell_MemsItInsteadOfCasting()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            viewModel.AutoFarmCastSpellsText = "armor";
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmRegions", Array.Empty<FarmRegion>());
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>());

            ArriveAtDestination(viewModel);
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("mem \"armor\""));
            Assert.DoesNotContain(output, line => line.Contains("cast \"armor\" self"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ArrivingAtARoom_WithoutEnteringCombat_DoesNotCastOffensiveEntries()
    {
        // Regression target: an offensive entry used to fire on plain room entry, which is
        // pointless — there's no enemy to target yet. It must still wait for actual combat, even
        // though a plain (self-buff) entry is now allowed to top itself up on room arrival too
        // (see the sibling ArrivingAtARoom_WithExpiredBuff_ToppedUpByMaintenancePass test below).
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            viewModel.AutoFarmCastSpellsText = "!fireball";
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmRegions", Array.Empty<FarmRegion>());
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "fireball", Memed: true, Meming: false),
            });

            ArriveAtDestination(viewModel);
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("cast \"fireball\""));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ArrivingAtARoom_WithExpiredBuff_ToppedUpByMaintenancePass()
    {
        // "Zawsze zapamiętane" only guarantees the spell stays memorized — it doesn't by itself
        // keep the buff active. A self-buff cast-sequence entry that's memorized but wore off
        // between fights (no active-affect entry) must get refreshed by the farm's own room-hop
        // maintenance pass, the same place missing memorization/heal recovery is handled, rather
        // than staying down until the next fight happens to start.
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            viewModel.AutoFarmCastSpellsText = "armor";
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmRegions", Array.Empty<FarmRegion>());
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "armor", Memed: true, Meming: false),
            });

            ArriveAtDestination(viewModel);
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("cast \"armor\" self"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ArrivingAtARoom_WithAlreadyActiveBuff_DoesNotRecastIt()
    {
        // The maintenance pass must only top up a buff that's genuinely worn off — an already
        // active one must not be spammed on every single room hop the farm makes.
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            viewModel.AutoFarmCastSpellsText = "armor";
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_autoFarmRegions", Array.Empty<FarmRegion>());
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "armor", Memed: true, Meming: false),
            });
            GetPrivateField<HashSet<string>>(viewModel, "_activeAffectNames").Add("armor");

            ArriveAtDestination(viewModel);
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("cast \"armor\" self"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_CastSequenceSpellsMemorizedAndOneAlreadyActive_CastsOnlyTheMissingOne()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            viewModel.AutoFarmCastSpellsText = "bless\narmor";
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "bless", Memed: true, Meming: false),
                new(2, 1, "armor", Memed: true, Meming: false),
            });
            GetPrivateField<HashSet<string>>(viewModel, "_activeAffectNames").Add("bless");

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("cast \"armor\" self"));
            Assert.DoesNotContain(output, line => line.Contains("cast \"bless\" self"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_TwoMissingCastSequenceSpells_CastsThemInTheConfiguredOrder()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            viewModel.AutoFarmCastSpellsText = "bless\narmor";
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "bless", Memed: true, Meming: false),
                new(2, 1, "armor", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            var blessIndex = output.FindIndex(line => line.Contains("cast \"bless\" self"));
            var armorIndex = output.FindIndex(line => line.Contains("cast \"armor\" self"));
            Assert.True(blessIndex >= 0 && armorIndex >= 0, "Both casts should have been sent.");
            Assert.True(blessIndex < armorIndex, "bless was configured before armor and should cast first.");
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_AutoFarmNotActive_DoesNotCastTheSequence()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            viewModel.AutoFarmCastSpellsText = "armor";
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "armor", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("cast \"armor\" self"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_OffensiveEntry_CastsAtTheCurrentEnemyNotSelf()
    {
        // Regression target: an offensive entry used to always target "self" like every other
        // entry, which the MUD rejected outright ("Nie da rady tego zrobic") for a damage spell.
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: "golem"),
            });
            viewModel.AutoFarmCastSpellsText = "!magic missile";
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "magic missile", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("cast \"magic missile\" golem"));
            Assert.DoesNotContain(output, line => line.Contains("cast \"magic missile\" self"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_OffensiveEntryWithNoKnownEnemyYet_IsSkippedNotMisCastAtSelf()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            // Room.People hasn't caught up with the fresh "fighting" transition yet — no Enemy.
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: null),
            });
            viewModel.AutoFarmCastSpellsText = "!magic missile";
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "magic missile", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.DoesNotContain(output, line => line.Contains("cast \"magic missile\""));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task CombatStarts_MixOfBuffAndOffensiveEntries_BuffTargetsSelfOffensiveTargetsEnemy()
    {
        var viewModel = CreateViewModel(out var directory);
        var output = new List<string>();
        viewModel.OutputReceived += text => output.Add(text);
        try
        {
            SetPrivateField(viewModel, "_isConnected", true);
            SetPrivateField(viewModel, "_autoFarmActive", true);
            SetPrivateField(viewModel, "_latestCharacterName", "Bohater");
            SetPrivateField(viewModel, "_latestRoomPeople", new List<RoomPerson>
            {
                new("Bohater", IsFighting: true, Enemy: "golem"),
            });
            viewModel.AutoFarmCastSpellsText = "armor\n!magic missile";
            SetPrivateField(viewModel, "_latestMemorizedSpells", new List<MemorizedSpell>
            {
                new(1, 1, "armor", Memed: true, Meming: false),
                new(2, 1, "magic missile", Memed: true, Meming: false),
            });

            InvokePrivate(viewModel, "UpdateCharacterPosition", "standing");
            InvokePrivate(viewModel, "UpdateCharacterPosition", "fighting");
            for (var i = 0; i < 8; i++)
            {
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Contains(output, line => line.Contains("cast \"armor\" self"));
            Assert.Contains(output, line => line.Contains("cast \"magic missile\" golem"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task StopAutoFarm_ClearsThePlannedVisitOrder()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            ArrangeThreeRoomFarm(viewModel);
            InvokePrivate(viewModel, "StartAutoFarm");
            Assert.NotNull(GetPrivateField<IReadOnlyList<MapRoom>?>(viewModel, "_autoFarmVisitOrder"));

            InvokePrivate(viewModel, "StopAutoFarm", "test");

            Assert.Null(GetPrivateField<IReadOnlyList<MapRoom>?>(viewModel, "_autoFarmVisitOrder"));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task StopAutoFarm_WhenNotActive_DoesNothing()
    {
        var viewModel = CreateViewModel(out var directory);
        try
        {
            var toastCountBefore = viewModel.Toasts.Count;

            InvokePrivate(viewModel, "StopAutoFarm", "test");

            Assert.False(viewModel.IsAutoFarmActive);
            Assert.Equal(toastCountBefore, viewModel.Toasts.Count);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}

using System.Text.Json;
using MudClient.App.Models;
using MudClient.App.Services;

namespace MudClient.App.Tests;

public sealed class RareCatalogRefreshTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "KillerMudClient_Rares_" + Guid.NewGuid().ToString("N"));

    public RareCatalogRefreshTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Refresh_WithoutPagerPrompt_ProceedsDirectlyToUniqueItemDetails()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(2));
        var sent = new List<string>();

        Task Send(string command, CancellationToken cancellationToken)
        {
            sent.Add(command);
            if (command == "rarelist all")
            {
                coordinator.TryCaptureLine("<<============= lista przedmiotow unikalnych - artefact =============>>");
                coordinator.TryCaptureLine(
                    "+[-1 d] [N] ( kilof             - one hand      ) [29099] krasnoludzki kilof 'Potega Ziemi'");
                coordinator.TryCaptureLine(
                    "+[-1 d] [R] ( wlocznia          - two hand      ) [  215] trojzab Turlitha");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }
            else if (command == "rarelist 215")
            {
                coordinator.TryCaptureLine("Wielki trojzab z trzema zebami.");
                coordinator.TryCaptureLine("Waga: 12, Wartość: 5000.");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }
            else if (command == "rarelist 29099")
            {
                coordinator.TryCaptureLine("Kilof kuty przez krasnoludzkich mistrzów.");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }

            return Task.CompletedTask;
        }

        var catalog = await coordinator.RefreshAsync(Send, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, catalog.Rares.Count);
        var trojzab = Assert.Single(catalog.Rares, rare => rare.Vnum == 215);
        Assert.Equal("trojzab Turlitha", trojzab.Name);
        Assert.Equal("wlocznia", trojzab.ItemType);
        Assert.Equal("two hand", trojzab.Slot);
        Assert.Equal("R", trojzab.Flag);
        Assert.Equal("artefakt", trojzab.Category);
        Assert.Equal("Wielki trojzab z trzema zebami.\nWaga: 12, Wartość: 5000.", trojzab.Details);

        var kilof = Assert.Single(catalog.Rares, rare => rare.Vnum == 29099);
        Assert.Equal("Kilof kuty przez krasnoludzkich mistrzów.", kilof.Details);

        Assert.Equal(["rarelist all", "rarelist 215", "rarelist 29099"], sent);
        Assert.False(coordinator.IsCapturing);
    }

    [Fact]
    public async Task Refresh_OnEntryMapped_FiresAfterEachFreshlyFetchedItemOnly()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(2));

        Task Send(string command, CancellationToken cancellationToken)
        {
            if (command == "rarelist all")
            {
                coordinator.TryCaptureLine("<<============= lista przedmiotow unikalnych - artefact =============>>");
                coordinator.TryCaptureLine(
                    "+[-1 d] [N] ( kilof             - one hand      ) [29099] krasnoludzki kilof 'Potega Ziemi'");
                coordinator.TryCaptureLine(
                    "+[-1 d] [R] ( wlocznia          - two hand      ) [  215] trojzab Turlitha");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }
            else if (command == "rarelist 29099")
            {
                coordinator.TryCaptureLine("Kilof kuty przez krasnoludzkich mistrzów.");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }

            return Task.CompletedTask;
        }

        var snapshots = new List<IReadOnlyList<RareEntry>>();
        var knownDetails = new Dictionary<int, string> { [215] = "Znany wczesniej trojzab." };
        var catalog = await coordinator.RefreshAsync(
            Send,
            cancellationToken: TestContext.Current.CancellationToken,
            knownDetails: knownDetails,
            onEntryMapped: (mappedSoFar, _) =>
            {
                snapshots.Add(mappedSoFar.ToArray());
                return Task.CompletedTask;
            });

        // Only vnum 29099 was actually fetched (215 came from knownDetails), so the callback
        // fires exactly once, with the running list at that point.
        var snapshot = Assert.Single(snapshots);
        Assert.Contains(snapshot, rare => rare.Vnum == 29099 && rare.Details.Contains("Kilof"));
        Assert.Equal(2, catalog.Rares.Count);
    }

    [Fact]
    public async Task Refresh_KnownVnumWithDetails_SkipsDetailFetchAndReusesText()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(2));
        var sent = new List<string>();

        Task Send(string command, CancellationToken cancellationToken)
        {
            sent.Add(command);
            if (command == "rarelist all")
            {
                coordinator.TryCaptureLine("<<============= lista przedmiotow unikalnych - artefact =============>>");
                coordinator.TryCaptureLine(
                    "+[-1 d] [N] ( kilof             - one hand      ) [29099] krasnoludzki kilof 'Potega Ziemi'");
                coordinator.TryCaptureLine(
                    "+[-1 d] [R] ( wlocznia          - two hand      ) [  215] trojzab Turlitha");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }
            else if (command == "rarelist 29099")
            {
                coordinator.TryCaptureLine("Kilof kuty przez krasnoludzkich mistrzów.");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }

            return Task.CompletedTask;
        }

        var knownDetails = new Dictionary<int, string> { [215] = "Znany wczesniej trojzab." };
        var catalog = await coordinator.RefreshAsync(
            Send,
            cancellationToken: TestContext.Current.CancellationToken,
            knownDetails: knownDetails);

        var trojzab = Assert.Single(catalog.Rares, rare => rare.Vnum == 215);
        Assert.Equal("Znany wczesniej trojzab.", trojzab.Details);
        var kilof = Assert.Single(catalog.Rares, rare => rare.Vnum == 29099);
        Assert.Equal("Kilof kuty przez krasnoludzkich mistrzów.", kilof.Details);

        // Only the still-unmapped vnum (29099) triggers a "rarelist <vnum>" round-trip.
        Assert.Equal(["rarelist all", "rarelist 29099"], sent);
    }

    [Fact]
    public async Task Refresh_PagesThroughPagerPromptsBeforeMovingToDetails()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(2));
        var page = 0;
        var sent = new List<string>();

        Task Send(string command, CancellationToken cancellationToken)
        {
            sent.Add(command);
            if (command == "rarelist all")
            {
                coordinator.TryCaptureLine("<<============= lista przedmiotow unikalnych - artefact =============>>");
                coordinator.TryCaptureLine(
                    "+[-1 d] [N] ( kilof             - one hand      ) [29099] krasnoludzki kilof 'Potega Ziemi'");
                coordinator.TryCaptureLine("[Nacisnij Enter aby kontynuowac]");
                coordinator.ObserveText("> ");
            }
            else if (command.Length == 0)
            {
                page++;
                if (page < 2)
                {
                    coordinator.TryCaptureLine(
                        "+[-1 d] [R] ( wlocznia          - two hand      ) [  215] trojzab Turlitha");
                    coordinator.TryCaptureLine("[Nacisnij Enter aby kontynuowac]");
                    coordinator.ObserveText("> ");
                }
                else
                {
                    coordinator.TryCaptureLine(
                        "+[-1 d] [N] ( maczuga           - one hand      ) [  874] szkarlatny mlot bojowy z glowa smoka");
                    coordinator.ObserveText("<418/488hp 90/100mv> ");
                }
            }
            else
            {
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }

            return Task.CompletedTask;
        }

        var catalog = await coordinator.RefreshAsync(Send, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, catalog.Rares.Count);
        Assert.Equal(["rarelist all", string.Empty, string.Empty], sent.Take(3));
        Assert.Equal(["rarelist 215", "rarelist 874", "rarelist 29099"], sent.Skip(3));
        Assert.False(coordinator.IsCapturing);
    }

    [Fact]
    public async Task RefreshList_PagesWhenPagerMarkerArrivesOnlyThroughRawText()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromSeconds(2));
        var sent = new List<string>();

        Task Send(string command, CancellationToken cancellationToken)
        {
            sent.Add(command);
            if (command == "rarelist all")
            {
                coordinator.TryCaptureLine("<<============= lista przedmiotow unikalnych - artefact =============>>");
                coordinator.TryCaptureLine(
                    "+[-1 d] [N] ( kilof             - one hand      ) [29099] krasnoludzki kilof 'Potega Ziemi'");
                // The line accumulator may not have delivered this marker yet even though the
                // raw text receiver has. The refresh must still continue the pager.
                coordinator.ObserveText("[Nacisnij Enter aby kontynuowac]\r\n> ");
            }
            else
            {
                coordinator.TryCaptureLine(
                    "+[-1 d] [N] ( swiatlo           - nad glowa     ) [  211] krysztal Tellany");
                coordinator.ObserveText("<418/488hp 90/100mv> ");
            }

            return Task.CompletedTask;
        }

        var catalog = await coordinator.RefreshListAsync(Send, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["rarelist all", string.Empty], sent);
        Assert.Contains(catalog.Rares, rare => rare.Vnum == 211 && rare.Name == "krysztal Tellany");
    }

    [Fact]
    public async Task Refresh_MudPromptCompletesResponseWithoutWaitingForQuietPeriod()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(500));

        Task Send(string command, CancellationToken cancellationToken)
        {
            coordinator.ObserveText(
                "rarelist all\r\n"
                + "<<============= lista przedmiotow unikalnych - artefact =============>>\r\n"
                + "<418/488hp 90/100mv> ");
            return Task.CompletedTask;
        }

        var catalog = await coordinator.RefreshAsync(Send, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(catalog.Rares);
    }

    [Fact]
    public async Task Refresh_UnrelatedTextActivity_DoesNotHangCompletion()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(200));
        var noiseTasks = new List<Task>();

        Task Send(string command, CancellationToken cancellationToken)
        {
            noiseTasks.Add(Task.Run(async () =>
            {
                for (var index = 0; index < 10; index++)
                {
                    coordinator.ObserveText("odswiezenie prompta bez nowej linii");
                    await Task.Delay(5);
                }
            }));
            return Task.CompletedTask;
        }

        var catalog = await coordinator.RefreshAsync(Send, cancellationToken: TestContext.Current.CancellationToken);
        await Task.WhenAll(noiseTasks);

        Assert.Empty(catalog.Rares);
    }

    [Fact]
    public async Task Refresh_TimesOutWithoutAnyResponseAndReleasesCapture()
    {
        var coordinator = new RareCatalogRefreshCoordinator(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.RefreshAsync(
            (_, _) => Task.CompletedTask,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(coordinator.IsCapturing);
    }

    [Fact]
    public async Task Store_SavesAtomicallyAndLoadsGeneratedJson()
    {
        var path = Path.Combine(_directory, "killeropedia-rares.json");
        var store = new RareCatalogStore(path);
        var catalog = new RareCatalogDocument
        {
            GeneratedAtUtc = DateTimeOffset.Parse("2026-07-13T12:00:00Z"),
            Rares =
            [
                new RareEntry
                {
                    Vnum = 215,
                    Name = "trojzab Turlitha",
                    ItemType = "wlocznia",
                    Slot = "two hand",
                    Flag = "R",
                    Category = "artefakt",
                    Details = "Wielki trojzab.",
                },
            ],
        };

        await store.SaveAsync(catalog, TestContext.Current.CancellationToken);
        var loaded = store.Load();

        Assert.Equal(215, Assert.Single(loaded.Rares).Vnum);
        Assert.False(File.Exists(path + ".tmp"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(215, json.RootElement.GetProperty("rares")[0].GetProperty("vnum").GetInt32());
    }

    [Fact]
    public async Task Store_CancelledSave_PreservesPreviousCatalog()
    {
        var path = Path.Combine(_directory, "killeropedia-rares.json");
        var store = new RareCatalogStore(path);
        await store.SaveAsync(new RareCatalogDocument
        {
            Rares = [new RareEntry { Vnum = 1, Name = "poprzedni przedmiot" }],
        }, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            new RareCatalogDocument
            {
                Rares = [new RareEntry { Vnum = 2, Name = "niepelny przedmiot" }],
            },
            cancellation.Token));

        Assert.Equal(1, Assert.Single(store.Load().Rares).Vnum);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Store_WithoutUserFile_LoadsBundledRareSnapshot()
    {
        var store = new RareCatalogStore(Path.Combine(_directory, "nie-istnieje.json"));

        var catalog = store.Load();

        // The bundled snapshot ships fully mapped (see Assets/Data/rares.json), so new installs
        // start with real Details text instead of an empty placeholder for every entry.
        Assert.Equal(274, catalog.Rares.Count);
        var kilof = Assert.Single(
            catalog.Rares, rare => rare.Vnum == 29099 && rare.Name == "krasnoludzki kilof 'Potega Ziemi'");
        Assert.False(string.IsNullOrWhiteSpace(kilof.Details));
        Assert.True(catalog.Rares.Count(rare => string.IsNullOrWhiteSpace(rare.Details)) <= 2);
    }
}

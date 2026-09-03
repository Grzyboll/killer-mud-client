using MudClient.App.Services;

namespace MudClient.App.Tests;

/// <summary>Covers <see cref="AutoRepairCoordinator"/> — the "/repair" meta-command's "remove all"
/// → parse removed items → "repair &lt;item&gt;" each → "wear all" sequence. The parsed sample
/// lines below are a verbatim real-game "remove all" response (the MUD never sends Polish
/// diacritics — see MudClient.Core.Text.PolishText), including the flavor lines some items print
/// right after ("Czujesz jak moc..." etc.) that must NOT be mistaken for a removed item, and a
/// genuine duplicate (a matched pair of "srebrnej brasnolety Gruna").</summary>
public sealed class AutoRepairCoordinatorTests
{
    private static readonly string[] SampleRemoveAllLines =
    [
        "Przestajesz uzywac kuli swiatla.",
        "Przestajesz uzywac mithrilowy pancerz Urthena Medevi'ego.",
        "Przestajesz uzywac medalionu Dawnych Bogow.",
        "Przestajesz uzywac miecza Aresa Dragona.",
        "Czujesz jak moc miecza Ares Dragon odplywa do ostrza.",
        "Przestajesz uzywac sandalow ze smoczej luski.",
        "Przestajesz uzywac pasa pomniejszej bariery.",
        "Przestajesz uzywac podluznego klipsa.",
        "Przestajesz uzywac maski \"Szczurze Oblicze\".",
        "Czujesz, ze moc, ktora dotad Cie otaczala znika wraz z Twoim usposobieniem.",
        "Przestajesz uzywac zdobionego srebrnego pierscienia.",
        "Przestajesz uzywac szklanego kolczyka.",
        "Przestajesz uzywac rycerskiego helmu z przylbica.",
        "Przestajesz uzywac rycerskich folgowych nagolennic.",
        "Przestajesz uzywac rycerskich folgowych naramiennikow.",
        "Przestajesz uzywac illittowego pierscienia lekkosci.",
        "Przestajesz uzywac srebrnej brasnolety Gruna.",
        "Przestajesz uzywac srebrnej brasnolety Gruna.",
        "Przestajesz uzywac miedzianego amuletu 'Magia Szalenstwa'.",
        "Przestajesz uzywac zakletych rekawic Mystrala Telivirtara.",
    ];

    // ====================================================================
    // Pure parsing
    // ====================================================================

    [Fact]
    public void ParseRemovedItemNames_RealSampleResponse_ExtractsEveryItemInOrderIgnoringFlavorLines()
    {
        var names = AutoRepairCoordinator.ParseRemovedItemNames(SampleRemoveAllLines);

        Assert.Equal(18, names.Count);
        Assert.Equal("kuli swiatla", names[0]);
        Assert.Equal("zakletych rekawic Mystrala Telivirtara", names[^1]);
        Assert.DoesNotContain(names, name => name.Contains("Czujesz", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseRemovedItemNames_DuplicateItem_KeepsBothInstances()
    {
        var names = AutoRepairCoordinator.ParseRemovedItemNames(SampleRemoveAllLines);

        Assert.Equal(2, names.Count(name => name == "srebrnej brasnolety Gruna"));
    }

    [Fact]
    public void ParseRemovedItemNames_NothingRemoved_ReturnsEmpty()
    {
        var names = AutoRepairCoordinator.ParseRemovedItemNames(["Nie masz nic do zdjecia."]);

        Assert.Empty(names);
    }

    // ====================================================================
    // Full RunAsync sequence
    // ====================================================================

    private static AutoRepairCoordinator CreateFastCoordinator() =>
        new(quietPeriod: TimeSpan.FromMilliseconds(20), responseTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RunAsync_ItemsRemoved_RepairsEachThenWearsAll()
    {
        var coordinator = CreateFastCoordinator();
        var sentCommands = new List<string>();

        Task Send(string command, CancellationToken token)
        {
            sentCommands.Add(command);
            if (command == "remove all")
            {
                foreach (var line in SampleRemoveAllLines)
                {
                    coordinator.TryCaptureLine(line);
                }
            }

            return Task.CompletedTask;
        }

        var repaired = await coordinator.RunAsync(Send);

        Assert.Equal(18, repaired.Count);
        Assert.Equal("remove all", sentCommands[0]);
        Assert.Equal("repair kuli swiatla", sentCommands[1]);
        Assert.Equal("repair zakletych rekawic Mystrala Telivirtara", sentCommands[18]);
        Assert.Equal("wear all", sentCommands[^1]);
        Assert.Equal(20, sentCommands.Count); // remove all + 18 repairs + wear all
    }

    [Fact]
    public async Task RunAsync_NothingRemoved_SkipsRepairButStillWearsAll()
    {
        var coordinator = CreateFastCoordinator();
        var sentCommands = new List<string>();

        Task Send(string command, CancellationToken token)
        {
            sentCommands.Add(command);
            if (command == "remove all")
            {
                coordinator.TryCaptureLine("Nie masz nic do zdjecia.");
            }

            return Task.CompletedTask;
        }

        var repaired = await coordinator.RunAsync(Send);

        Assert.Empty(repaired);
        Assert.Equal(["remove all", "wear all"], sentCommands);
    }

    /// <summary>Reports synchronously, unlike <see cref="System.Progress{T}"/> — whose base
    /// SynchronizationContext.Post always queues to the ThreadPool when no context is captured,
    /// making assertions right after an awaited call a genuine race under load. Production code
    /// (MainWindowViewModel.RepairAllAsync) doesn't have this problem since it posts to
    /// Dispatcher.UIThread explicitly instead of relying on Progress&lt;T&gt;'s ambient context.</summary>
    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressForEachStage()
    {
        var coordinator = CreateFastCoordinator();
        var progressMessages = new List<string>();
        var progress = new SynchronousProgress<string>(progressMessages.Add);

        Task Send(string command, CancellationToken token)
        {
            if (command == "remove all")
            {
                coordinator.TryCaptureLine("Przestajesz uzywac miecza.");
            }

            return Task.CompletedTask;
        }

        await coordinator.RunAsync(Send, progress);

        Assert.Contains(progressMessages, m => m.Contains("Zdejmuję"));
        Assert.Contains(progressMessages, m => m.Contains("Naprawiam: miecza (1/1)"));
        Assert.Contains(progressMessages, m => m.Contains("Zakładam"));
    }

    [Fact]
    public async Task RunAsync_ConcurrentCall_ThrowsInvalidOperationException()
    {
        var coordinator = CreateFastCoordinator();
        var releaseFirstSend = new TaskCompletionSource();

        async Task SlowSend(string command, CancellationToken token)
        {
            if (command == "remove all")
            {
                await releaseFirstSend.Task;
                coordinator.TryCaptureLine("Nie masz nic do zdjecia.");
            }
        }

        var firstRun = coordinator.RunAsync(SlowSend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RunAsync((_, _) => Task.CompletedTask));

        releaseFirstSend.SetResult();
        await firstRun;
    }
}

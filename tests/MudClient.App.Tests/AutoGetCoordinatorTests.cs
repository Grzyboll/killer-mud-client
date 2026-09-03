using MudClient.App.Services;

namespace MudClient.App.Tests;

/// <summary>Covers <see cref="AutoGetCoordinator"/> directly — no view model needed. The view
/// model-level wiring (gating, placeholder substitution, "/autoget" dispatch) is covered in
/// AutoGetMetaCommandTests.</summary>
public sealed class AutoGetCoordinatorTests
{
    // ====================================================================
    // TryGetExamineTarget — pure parsing
    // ====================================================================

    [Theory]
    [InlineData("exa cia", "cia")]
    [InlineData("examine cia", "cia")]
    [InlineData("  exa   cia  ", "cia")]
    [InlineData("EXA CIA", "CIA")]
    public void TryGetExamineTarget_ExamineCommand_ExtractsTheTarget(string command, string expectedTarget)
    {
        Assert.True(AutoGetCoordinator.TryGetExamineTarget(command, out var target));
        Assert.Equal(expectedTarget, target);
    }

    [Theory]
    [InlineData("get all.klej cia")]
    [InlineData("put all.gem 2.tor")]
    [InlineData("exa")]
    [InlineData("exa  ")]
    [InlineData("examine")]
    [InlineData("examinecia")]
    [InlineData("")]
    public void TryGetExamineTarget_NotAnExamineCommandOrNoArgument_ReturnsFalse(string command)
    {
        Assert.False(AutoGetCoordinator.TryGetExamineTarget(command, out var target));
        Assert.Equal(string.Empty, target);
    }

    // ====================================================================
    // RunAsync
    // ====================================================================

    private static AutoGetCoordinator CreateFastCoordinator() =>
        new(quietPeriod: TimeSpan.FromMilliseconds(20), responseTimeout: TimeSpan.FromSeconds(5));

    [Fact]
    public async Task RunAsync_NoExamineLines_SendsEveryCommandVerbatimInOrder()
    {
        var coordinator = CreateFastCoordinator();
        var sent = new List<string>();

        var picked = await coordinator.RunAsync(
            ["get all.klej cia", "get all.gem cia", "put all.klej 2.tor"],
            (command, _) => { sent.Add(command); return Task.CompletedTask; });

        Assert.Empty(picked);
        Assert.Equal(["get all.klej cia", "get all.gem cia", "put all.klej 2.tor"], sent);
    }

    [Fact]
    public async Task RunAsync_ExamineResponseContainsARandomBook_InsertsAGetRightAfterTheExamine()
    {
        var coordinator = CreateFastCoordinator();
        var sent = new List<string>();

        Task Send(string command, CancellationToken token)
        {
            sent.Add(command);
            if (command == "exa cia")
            {
                coordinator.TryCaptureLine("Nosisz: duza ksiega triumfu.");
            }

            return Task.CompletedTask;
        }

        var picked = await coordinator.RunAsync(["exa cia", "put all.klej 2.tor"], Send);

        Assert.Equal(["duza ksiega triumfu"], picked);
        Assert.Equal(["exa cia", "get duza ksiega triumfu cia", "put all.klej 2.tor"], sent);
    }

    [Fact]
    public async Task RunAsync_ExamineResponseWithoutABook_SendsNothingExtra()
    {
        var coordinator = CreateFastCoordinator();
        var sent = new List<string>();

        Task Send(string command, CancellationToken token)
        {
            sent.Add(command);
            if (command == "exa cia")
            {
                coordinator.TryCaptureLine("Nosisz: zardzewialy miecz.");
            }

            return Task.CompletedTask;
        }

        var picked = await coordinator.RunAsync(["exa cia", "put all.klej 2.tor"], Send);

        Assert.Empty(picked);
        Assert.Equal(["exa cia", "put all.klej 2.tor"], sent);
    }

    [Fact]
    public async Task RunAsync_ExamineResponseWithTwoBooks_GetsBothInOrder()
    {
        var coordinator = CreateFastCoordinator();
        var sent = new List<string>();

        Task Send(string command, CancellationToken token)
        {
            sent.Add(command);
            if (command == "exa cia")
            {
                coordinator.TryCaptureLine("Nosisz: duza ksiega triumfu oraz mala ksiazka lasu.");
            }

            return Task.CompletedTask;
        }

        var picked = await coordinator.RunAsync(["exa cia"], Send);

        Assert.Equal(["duza ksiega triumfu", "mala ksiazka lasu"], picked);
        Assert.Equal(
            ["exa cia", "get duza ksiega triumfu cia", "get mala ksiazka lasu cia"],
            sent);
    }

    [Fact]
    public async Task RunAsync_MultipleExamineLines_EachOnlyLooksAtItsOwnResponse()
    {
        var coordinator = CreateFastCoordinator();
        var sent = new List<string>();

        Task Send(string command, CancellationToken token)
        {
            sent.Add(command);
            if (command == "exa cia")
            {
                coordinator.TryCaptureLine("Nosisz: duza ksiega triumfu.");
            }
            else if (command == "exa kosc")
            {
                coordinator.TryCaptureLine("Nic tu nie ma.");
            }

            return Task.CompletedTask;
        }

        var picked = await coordinator.RunAsync(["exa cia", "exa kosc"], Send);

        Assert.Equal(["duza ksiega triumfu"], picked);
        Assert.Equal(["exa cia", "get duza ksiega triumfu cia", "exa kosc"], sent);
    }

    [Fact]
    public async Task RunAsync_ExamineNeverResponds_ThrowsTimeoutException()
    {
        var coordinator = new AutoGetCoordinator(
            quietPeriod: TimeSpan.FromMilliseconds(20),
            responseTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.RunAsync(["exa cia"], (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task RunAsync_ConcurrentCall_ThrowsInvalidOperationException()
    {
        var coordinator = CreateFastCoordinator();
        var releaseFirstExamine = new TaskCompletionSource();

        async Task SlowSend(string command, CancellationToken token)
        {
            if (command == "exa cia")
            {
                await releaseFirstExamine.Task;
                coordinator.TryCaptureLine("Nic tu nie ma.");
            }
        }

        var firstRun = coordinator.RunAsync(["exa cia"], SlowSend);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.RunAsync(["exa cia"], (_, _) => Task.CompletedTask));

        releaseFirstExamine.SetResult();
        await firstRun;
    }
}

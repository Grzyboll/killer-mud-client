using MudClient.App.Services;
using Xunit;

namespace MudClient.App.Tests;

public sealed class SessionRecorderServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SessionRecorderService _recorder = new();

    public SessionRecorderServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "KillerMudClient_SessionRecorderTest_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        _recorder.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void BuildDefaultFilePath_WithProfileName_UsesItAndTimestamp()
    {
        var now = new DateTimeOffset(2026, 9, 23, 14, 30, 5, TimeSpan.Zero);

        var path = SessionRecorderService.BuildDefaultFilePath("C:\\Records", "Gandalf", now);

        Assert.Equal(Path.Combine("C:\\Records", "Gandalf_2026-09-23_14-30-05.txt"), path);
    }

    [Fact]
    public void BuildDefaultFilePath_NoProfileName_FallsBackToSesja()
    {
        var now = new DateTimeOffset(2026, 9, 23, 14, 30, 5, TimeSpan.Zero);

        var path = SessionRecorderService.BuildDefaultFilePath("C:\\Records", null, now);

        Assert.Equal(Path.Combine("C:\\Records", "sesja_2026-09-23_14-30-05.txt"), path);
    }

    [Fact]
    public void BuildDefaultFilePath_InvalidCharsInProfileName_AreSanitized()
    {
        var now = new DateTimeOffset(2026, 9, 23, 14, 30, 5, TimeSpan.Zero);

        var path = SessionRecorderService.BuildDefaultFilePath("C:\\Records", "Bo:b/Max", now);

        Assert.Equal(Path.Combine("C:\\Records", "Bo_b_Max_2026-09-23_14-30-05.txt"), path);
    }

    [Fact]
    public void Start_CreatesDirectoryAndFile()
    {
        var path = Path.Combine(_tempDir, "sub", "test.txt");

        _recorder.Start(path);

        Assert.True(Directory.Exists(Path.Combine(_tempDir, "sub")));
        Assert.True(File.Exists(path));
        Assert.True(_recorder.IsRecording);
        Assert.Equal(path, _recorder.CurrentFilePath);
    }

    [Fact]
    public void Append_WritesTextWithAnsiStripped()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        _recorder.Start(path);

        _recorder.Append("\u001b[96mHello\u001b[0m world\n");
        _recorder.Stop();

        Assert.Equal("Hello world\n", File.ReadAllText(path));
    }

    [Fact]
    public void Append_WhileNotRecording_DoesNothing()
    {
        // Safe to call unconditionally — mirrors how MainWindowViewModel wires it directly to
        // OutputReceived without checking IsRecording first.
        _recorder.Append("some text\n");

        Assert.False(_recorder.IsRecording);
    }

    [Fact]
    public void Start_WhileAlreadyRecording_IsANoOp()
    {
        var firstPath = Path.Combine(_tempDir, "first.txt");
        var secondPath = Path.Combine(_tempDir, "second.txt");
        _recorder.Start(firstPath);

        _recorder.Start(secondPath);

        Assert.Equal(firstPath, _recorder.CurrentFilePath);
        Assert.False(File.Exists(secondPath));
    }

    [Fact]
    public void Stop_ClosesFileAndAllowsStartingElsewhere()
    {
        var firstPath = Path.Combine(_tempDir, "first.txt");
        var secondPath = Path.Combine(_tempDir, "second.txt");
        _recorder.Start(firstPath);
        _recorder.Append("line one\n");

        _recorder.Stop();
        Assert.False(_recorder.IsRecording);
        Assert.Null(_recorder.CurrentFilePath);

        _recorder.Start(secondPath);
        _recorder.Append("line two\n");
        _recorder.Stop();

        Assert.Equal("line one\n", File.ReadAllText(firstPath));
        Assert.Equal("line two\n", File.ReadAllText(secondPath));
    }

    [Fact]
    public void Start_ExistingFile_AppendsRatherThanOverwrites()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(path, "already here\n");

        _recorder.Start(path);
        _recorder.Append("newly recorded\n");
        _recorder.Stop();

        Assert.Equal("already here\nnewly recorded\n", File.ReadAllText(path));
    }
}

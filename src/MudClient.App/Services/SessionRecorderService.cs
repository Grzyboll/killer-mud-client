using MudClient.Core.Text;

namespace MudClient.App.Services;

/// <summary>
/// Captures everything shown in the terminal — MUD text and echoed outgoing commands alike,
/// since both flow through <see cref="ViewModels.MainWindowViewModel.OutputReceived"/> — into a
/// plain-text transcript file (ANSI codes stripped). A manual "record" toggle, off by default and
/// not persisted across restarts: starting again while already recording is a no-op, and the
/// caller must call <see cref="Stop"/> first to point a new recording at a different file.
/// </summary>
public sealed class SessionRecorderService : IDisposable
{
    private StreamWriter? _writer;

    public bool IsRecording => _writer is not null;

    public string? CurrentFilePath { get; private set; }

    /// <summary>Starts recording to <paramref name="path"/>, creating parent directories as
    /// needed. Appends rather than overwrites if the file already exists.</summary>
    public void Start(string path)
    {
        if (IsRecording)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        CurrentFilePath = path;
    }

    public void Stop()
    {
        _writer?.Dispose();
        _writer = null;
        CurrentFilePath = null;
    }

    /// <summary>Appends one chunk of terminal text to the open file, stripped of ANSI color
    /// codes. A no-op while not recording — safe to wire directly to
    /// <see cref="ViewModels.MainWindowViewModel.OutputReceived"/> regardless of state.</summary>
    public void Append(string text) => _writer?.Write(AnsiText.StripAnsi(text));

    /// <summary>Builds the default path for a new recording: <c>&lt;directory&gt;/&lt;profile or
    /// "sesja"&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;.txt</c>. A pure function so the naming can be unit
    /// tested without touching the filesystem.</summary>
    public static string BuildDefaultFilePath(string directory, string? profileName, DateTimeOffset now)
    {
        var safeName = string.IsNullOrWhiteSpace(profileName) ? "sesja" : Sanitize(profileName);
        return Path.Combine(directory, $"{safeName}_{now:yyyy-MM-dd_HH-mm-ss}.txt");
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public void Dispose() => Stop();
}

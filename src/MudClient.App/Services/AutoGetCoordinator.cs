using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using MudClient.Core.Killeropedia;

namespace MudClient.App.Services;

/// <summary>
/// Runs the "/autoget" meta-command's configured command list, one command at a time. Most lines
/// are just forwarded verbatim, but any "exa &lt;cel&gt;"/"examine &lt;cel&gt;" line has its
/// response captured and scanned for a random magic-book name (see
/// <see cref="RandomBookNaming.FindBookNames"/> — Killeropedia's algorithmic random-book word
/// pools, e.g. "duża księga triumfu"); each match found gets a "get &lt;księga&gt; &lt;cel&gt;"
/// command sent right after, so a book revealed by examining a corpse actually gets picked up
/// instead of just sitting there. Everything else in the list is fire-and-forget, same as before —
/// only "exa" lines need to wait for and read a response.
/// </summary>
public sealed class AutoGetCoordinator
{
    private static readonly string[] ExamineVerbs = ["exa", "examine"];

    private readonly object _captureLock = new();
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _responseTimeout;
    private CaptureSession? _activeCapture;

    private static readonly Regex MudPromptRegex = new(
        @"<\d+/\d+hp\b[^\r\n>]*\b\d+/\d+mv\b[^\r\n>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public AutoGetCoordinator(TimeSpan? quietPeriod = null, TimeSpan? responseTimeout = null)
    {
        _quietPeriod = quietPeriod ?? TimeSpan.FromMilliseconds(500);
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(15);
    }

    public bool IsCapturing
    {
        get
        {
            lock (_captureLock)
            {
                return _activeCapture is not null;
            }
        }
    }

    public bool TryCaptureLine(string line)
    {
        lock (_captureLock)
        {
            if (_activeCapture is not { } capture)
            {
                return false;
            }

            capture.Lines.Writer.TryWrite(line);
            capture.Activity.Writer.TryWrite(true);
            return true;
        }
    }

    /// <summary>Signals response activity even when the MUD returned only a prompt without a newline.</summary>
    public void ObserveText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        lock (_captureLock)
        {
            if (_activeCapture is { } capture)
            {
                capture.Text.Writer.TryWrite(text);
                capture.Activity.Writer.TryWrite(true);
            }
        }
    }

    /// <summary>Sends every command in <paramref name="commands"/>, in order. Returns every random
    /// book name picked up along the way (empty when none were found).</summary>
    public async Task<IReadOnlyList<string>> RunAsync(
        IReadOnlyList<string> commands,
        Func<string, CancellationToken, Task> sendCommandAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendCommandAsync);

        var pickedUpBooks = new List<string>();
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetExamineTarget(command, out var target))
            {
                await sendCommandAsync(command, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var responseLines = await CaptureResponseAsync(command, sendCommandAsync, cancellationToken)
                .ConfigureAwait(false);
            foreach (var bookName in responseLines.SelectMany(RandomBookNaming.FindBookNames))
            {
                pickedUpBooks.Add(bookName);
                await sendCommandAsync($"get {bookName} {target}", cancellationToken).ConfigureAwait(false);
            }
        }

        return pickedUpBooks;
    }

    /// <summary>"exa cia"/"examine cia" → target "cia"; anything else (including "exa" with no
    /// argument) → false. Recognizes both the abbreviated and full verb, the same two forms this
    /// MUD accepts from a player.</summary>
    internal static bool TryGetExamineTarget(string command, out string target)
    {
        var trimmed = command.Trim();
        foreach (var verb in ExamineVerbs)
        {
            if (trimmed.Length > verb.Length
                && trimmed.StartsWith(verb, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[verb.Length]))
            {
                var candidate = trimmed[(verb.Length + 1)..].Trim();
                if (candidate.Length > 0)
                {
                    target = candidate;
                    return true;
                }
            }
        }

        target = string.Empty;
        return false;
    }

    private async Task<IReadOnlyList<string>> CaptureResponseAsync(
        string command,
        Func<string, CancellationToken, Task> sendCommandAsync,
        CancellationToken cancellationToken)
    {
        var capture = BeginCapture();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_responseTimeout);
        var lines = new List<string>();

        try
        {
            await SendAndWaitForQuietAsync(
                capture,
                lines,
                token => sendCommandAsync(command, token),
                _quietPeriod,
                timeoutCancellation.Token).ConfigureAwait(false);
            return lines;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MUD nie odpowiedział na komendę „{command}” w wyznaczonym czasie.");
        }
        finally
        {
            EndCapture(capture);
        }
    }

    private static async Task SendAndWaitForQuietAsync(
        CaptureSession capture,
        List<string> lines,
        Func<CancellationToken, Task> sendAsync,
        TimeSpan quietPeriod,
        CancellationToken cancellationToken)
    {
        DrainCapture(capture, lines);
        var responseText = new StringBuilder();
        await sendAsync(cancellationToken).ConfigureAwait(false);

        await capture.Activity.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        DrainCapture(capture, lines, responseText);

        if (MudPromptRegex.IsMatch(responseText.ToString()))
        {
            return;
        }

        while (true)
        {
            await Task.Delay(quietPeriod, cancellationToken).ConfigureAwait(false);
            var drained = DrainCapture(capture, lines, responseText);
            if (MudPromptRegex.IsMatch(responseText.ToString()) || !drained.HadLines)
            {
                return;
            }
        }
    }

    private CaptureSession BeginCapture()
    {
        var capture = new CaptureSession();
        lock (_captureLock)
        {
            if (_activeCapture is not null)
            {
                throw new InvalidOperationException("Inna operacja /autoget jest już w toku.");
            }

            _activeCapture = capture;
        }

        return capture;
    }

    private void EndCapture(CaptureSession capture)
    {
        lock (_captureLock)
        {
            if (ReferenceEquals(_activeCapture, capture))
            {
                _activeCapture = null;
            }
        }

        capture.Lines.Writer.TryComplete();
        capture.Text.Writer.TryComplete();
        capture.Activity.Writer.TryComplete();
    }

    private static DrainResult DrainCapture(
        CaptureSession capture,
        List<string> lines,
        StringBuilder? responseText = null)
    {
        var hadActivity = false;
        var hadLines = false;
        while (capture.Activity.Reader.TryRead(out _))
        {
            hadActivity = true;
        }

        while (capture.Lines.Reader.TryRead(out var line))
        {
            lines.Add(line);
            hadLines = true;
        }

        while (capture.Text.Reader.TryRead(out var text))
        {
            responseText?.Append(text);
        }

        return new DrainResult(hadActivity, hadLines);
    }

    private readonly record struct DrainResult(bool HadActivity, bool HadLines);

    private sealed class CaptureSession
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        public Channel<bool> Activity { get; } = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        public Channel<string> Text { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }
}

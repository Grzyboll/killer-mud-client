using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace MudClient.App.Services;

/// <summary>
/// Runs the "/repair" meta-command (Auto: Ekwipunek): sends "remove all", learns which items came
/// off by capturing the MUD's own "Przestajesz uzywac &lt;przedmiot&gt;." lines — GMCP has no
/// equipment/inventory package for this MUD, so parsing that confirmation text is the only way to
/// know what was worn — sends "repair &lt;przedmiot&gt;" for each one captured (in the order they
/// came off, duplicates included — e.g. two identical rings removed means two "repair" calls), then
/// "wear all" to put everything back on.
/// </summary>
public sealed class AutoRepairCoordinator
{
    private readonly object _captureLock = new();
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _responseTimeout;
    private CaptureSession? _activeCapture;

    // The server never sends Polish diacritics (see MudClient.Core.Text.PolishText), so this is
    // matched verbatim against the exact "Przestajesz uzywac X." text a real session showed.
    private static readonly Regex RemovedItemPattern = new(
        @"^Przestajesz uzywac (?<name>.+)\.$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MudPromptRegex = new(
        @"<\d+/\d+hp\b[^\r\n>]*\b\d+/\d+mv\b[^\r\n>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public AutoRepairCoordinator(TimeSpan? quietPeriod = null, TimeSpan? responseTimeout = null)
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

    /// <summary>Runs the full "remove all" → repair each → "wear all" sequence. Returns the item
    /// names captured from "remove all" (empty when nothing was worn), in the order they were
    /// repaired.</summary>
    public async Task<IReadOnlyList<string>> RunAsync(
        Func<string, CancellationToken, Task> sendCommandAsync,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sendCommandAsync);

        progress?.Report("Zdejmuję ekwipunek...");
        var removedItems = await CaptureRemovedItemsAsync(sendCommandAsync, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < removedItems.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = removedItems[index];
            progress?.Report($"Naprawiam: {item} ({index + 1}/{removedItems.Count})");
            await sendCommandAsync($"repair {item}", cancellationToken).ConfigureAwait(false);
        }

        progress?.Report("Zakładam ekwipunek z powrotem...");
        await sendCommandAsync("wear all", cancellationToken).ConfigureAwait(false);

        return removedItems;
    }

    private async Task<IReadOnlyList<string>> CaptureRemovedItemsAsync(
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
                token => sendCommandAsync("remove all", token),
                _quietPeriod,
                timeoutCancellation.Token).ConfigureAwait(false);
            return ParseRemovedItemNames(lines);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("MUD nie odpowiedział na komendę „remove all” w wyznaczonym czasie.");
        }
        finally
        {
            EndCapture(capture);
        }
    }

    /// <summary>Extracts every removed item's name, in the order the "Przestajesz uzywac X." lines
    /// arrived — duplicates (e.g. a matched pair of rings) are kept, one "repair" per instance.
    /// Unrelated lines (flavor text like "Czujesz jak moc miecza... odpływa do ostrza.") are simply
    /// lines that don't match and are skipped.</summary>
    internal static IReadOnlyList<string> ParseRemovedItemNames(IEnumerable<string> lines)
    {
        var result = new List<string>();
        foreach (var line in lines)
        {
            var match = RemovedItemPattern.Match(line.Trim());
            if (match.Success)
            {
                result.Add(match.Groups["name"].Value);
            }
        }

        return result;
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
                throw new InvalidOperationException("Inna operacja /repair jest już w toku.");
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

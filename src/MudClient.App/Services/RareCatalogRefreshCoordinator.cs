using System.Threading.Channels;
using System.Text;
using System.Text.RegularExpressions;
using MudClient.App.Models;
using MudClient.Core.Killeropedia;

namespace MudClient.App.Services;

public sealed record RareCatalogRefreshProgress(string Stage, int Completed, int Total)
{
    public string DisplayText => Total <= 0 ? Stage : $"{Stage} ({Completed}/{Total})";
}

/// <summary>
/// Coordinates the creator-only rarelist conversation. Incoming complete MUD lines are supplied
/// through <see cref="TryCaptureLine"/> while the coordinator serially sends the list command and
/// then a detail command per unique vnum. Structurally this mirrors
/// <see cref="BookCatalogRefreshCoordinator"/>, but completion detection never depends on
/// spotting a specific header — unlike booklist, the rarelist detail response format isn't known
/// ahead of time, so the only universal signal available is the game's own status prompt
/// reappearing (or, failing that, the response simply going quiet).
/// </summary>
public sealed class RareCatalogRefreshCoordinator
{
    private readonly object _captureLock = new();
    private readonly TimeSpan _listQuietPeriod;
    private readonly TimeSpan _detailQuietPeriod;
    private readonly TimeSpan _responseTimeout;
    private CaptureSession? _activeCapture;

    private static readonly Regex MudPromptRegex = new(
        @"<\d+/\d+hp\b[^\r\n>]*\b\d+/\d+mv\b[^\r\n>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public RareCatalogRefreshCoordinator(
        TimeSpan? listQuietPeriod = null,
        TimeSpan? detailQuietPeriod = null,
        TimeSpan? responseTimeout = null)
    {
        _listQuietPeriod = listQuietPeriod ?? TimeSpan.FromSeconds(1);
        _detailQuietPeriod = detailQuietPeriod ?? TimeSpan.FromMilliseconds(500);
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(60);
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

    /// <summary>Fetches only the pageable <c>rarelist all</c> index. This is suitable for normal
    /// client-side detection: unlike <see cref="RefreshAsync"/>, it never requests per-vnum details.</summary>
    public async Task<RareCatalogDocument> RefreshListAsync(
        Func<string, CancellationToken, Task> sendCommandAsync,
        IProgress<RareCatalogRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new RareCatalogRefreshProgress("Pobieranie listy rzadkich przedmiotów", 0, 0));
        var lines = await CapturePagedListResponseAsync(
            "rarelist all", sendCommandAsync, _listQuietPeriod, _responseTimeout, cancellationToken).ConfigureAwait(false);
        var rares = RareListParser.ParseList(lines)
            .GroupBy(entry => entry.Vnum)
            .Select(group => group.Last())
            .Select(entry => new RareEntry
            {
                Vnum = entry.Vnum,
                Name = entry.Name,
                ItemType = entry.ItemType,
                Slot = entry.Slot,
                Flag = entry.Flag,
                Category = entry.Category,
            })
            .ToList();
        progress?.Report(new RareCatalogRefreshProgress("Zakończono pobieranie listy", rares.Count, rares.Count));
        return new RareCatalogDocument { GeneratedAtUtc = DateTimeOffset.UtcNow, Rares = rares };
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

    private static readonly IReadOnlyDictionary<int, string> EmptyKnownDetails = new Dictionary<int, string>();

    /// <summary>
    /// Refreshes the rarelist catalog. <paramref name="knownDetails"/> lets a caller pass in
    /// already-mapped vnum → Details text from a previously saved catalog — an item found there
    /// is kept as-is instead of being re-fetched with "rarelist &lt;vnum&gt;", since the detail
    /// text for a given vnum never changes. This is what makes repeat refreshes fast: only vnums
    /// that are new or were never successfully mapped incur a round-trip.
    /// <paramref name="onEntryMapped"/>, when given, is awaited right after each vnum newly
    /// fetched from the MUD (not for ones reused from <paramref name="knownDetails"/>) with the
    /// full list mapped so far — a full "rarelist all" refresh can take a very long time, and
    /// without this a disconnect or crash partway through would silently discard everything
    /// mapped in that run instead of letting the caller persist progress as it goes.
    /// </summary>
    public async Task<RareCatalogDocument> RefreshAsync(
        Func<string, CancellationToken, Task> sendCommandAsync,
        IProgress<RareCatalogRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<int, string>? knownDetails = null,
        Func<IReadOnlyList<RareEntry>, CancellationToken, Task>? onEntryMapped = null)
    {
        ArgumentNullException.ThrowIfNull(sendCommandAsync);
        knownDetails ??= EmptyKnownDetails;

        progress?.Report(new RareCatalogRefreshProgress("Pobieranie listy przedmiotów", 0, 0));
        var lines = await CapturePagedListResponseAsync(
            "rarelist all",
            sendCommandAsync,
            _listQuietPeriod,
            _responseTimeout,
            cancellationToken).ConfigureAwait(false);

        var entries = RareListParser.ParseList(lines)
            .GroupBy(entry => entry.Vnum)
            .Select(group => group.Last())
            .OrderBy(entry => entry.Vnum)
            .ToArray();

        var rares = new List<RareEntry>(entries.Length);
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];

            string details;
            var freshlyFetched = false;
            if (knownDetails.TryGetValue(entry.Vnum, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                progress?.Report(new RareCatalogRefreshProgress(
                    $"Znane przedmiot {entry.Vnum} (pominięto)", index, entries.Length));
                details = cached;
            }
            else
            {
                progress?.Report(new RareCatalogRefreshProgress(
                    $"Szczegóły przedmiotu {entry.Vnum}", index, entries.Length));
                var detailLines = await CaptureOpenResponseAsync(
                    token => sendCommandAsync($"rarelist {entry.Vnum}", token),
                    _detailQuietPeriod,
                    _responseTimeout,
                    cancellationToken).ConfigureAwait(false);
                details = RareListParser.ExtractDetailText(detailLines);
                freshlyFetched = true;
            }

            rares.Add(new RareEntry
            {
                Vnum = entry.Vnum,
                Name = entry.Name,
                ItemType = entry.ItemType,
                Slot = entry.Slot,
                Flag = entry.Flag,
                Category = entry.Category,
                Details = details,
            });

            if (freshlyFetched && onEntryMapped is not null)
            {
                await onEntryMapped(rares, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new RareCatalogRefreshProgress("Zapisywanie katalogu", rares.Count, rares.Count));
        return new RareCatalogDocument
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Rares = rares,
        };
    }

    private async Task<IReadOnlyList<string>> CapturePagedListResponseAsync(
        string command,
        Func<string, CancellationToken, Task> sendCommandAsync,
        TimeSpan quietPeriod,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var capture = BeginCapture();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var lines = new List<string>();

        try
        {
            var latestResponse = await SendAndWaitForQuietAsync(
                capture,
                lines,
                token => sendCommandAsync(command, token),
                quietPeriod,
                timeoutCancellation.Token).ConfigureAwait(false);

            while (latestResponse.HasPagerPrompt)
            {
                latestResponse = await SendAndWaitForQuietAsync(
                    capture,
                    lines,
                    token => sendCommandAsync(string.Empty, token),
                    quietPeriod,
                    timeoutCancellation.Token).ConfigureAwait(false);
            }

            return lines;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("MUD nie odpowiedział na komendę lub kontynuację rarelist w wyznaczonym czasie.");
        }
        finally
        {
            EndCapture(capture);
        }
    }

    private async Task<IReadOnlyList<string>> CaptureOpenResponseAsync(
        Func<CancellationToken, Task> sendAsync,
        TimeSpan quietPeriod,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var capture = BeginCapture();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var lines = new List<string>();

        try
        {
            await SendAndWaitForQuietAsync(capture, lines, sendAsync, quietPeriod, timeoutCancellation.Token)
                .ConfigureAwait(false);
            return lines;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("MUD nie zwrócił kompletnej odpowiedzi rarelist w wyznaczonym czasie.");
        }
        finally
        {
            EndCapture(capture);
        }
    }

    private static async Task<CapturedResponse> SendAndWaitForQuietAsync(
        CaptureSession capture,
        List<string> lines,
        Func<CancellationToken, Task> sendAsync,
        TimeSpan quietPeriod,
        CancellationToken cancellationToken)
    {
        DrainCapture(capture, lines);
        var responseStart = lines.Count;
        var responseText = new StringBuilder();
        await sendAsync(cancellationToken).ConfigureAwait(false);

        await capture.Activity.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        DrainCapture(capture, lines, responseText);

        if (MudPromptRegex.IsMatch(responseText.ToString()))
        {
            return CreateCapturedResponse(lines, responseStart, responseText);
        }

        while (true)
        {
            await Task.Delay(quietPeriod, cancellationToken).ConfigureAwait(false);
            var drained = DrainCapture(capture, lines, responseText);
            if (MudPromptRegex.IsMatch(responseText.ToString()) || !drained.HadLines)
            {
                return CreateCapturedResponse(lines, responseStart, responseText);
            }
        }
    }

    private static CapturedResponse CreateCapturedResponse(
        IReadOnlyList<string> lines,
        int responseStart,
        StringBuilder responseText)
    {
        var responseLines = lines.Skip(responseStart).ToArray();
        // The marker may be split between TCP reads: it can be visible to TextReceived before
        // LineReceived has produced the complete line. Raw text is therefore a valid signal
        // to send the next Enter, while the parsed list still uses complete lines only.
        var hasPagerPrompt = RareListParser.ContainsPagerPrompt(responseLines)
            || responseText.ToString().Contains("Nacisnij Enter", StringComparison.OrdinalIgnoreCase);
        return new CapturedResponse(hasPagerPrompt);
    }

    private CaptureSession BeginCapture()
    {
        var capture = new CaptureSession();
        lock (_captureLock)
        {
            if (_activeCapture is not null)
            {
                throw new InvalidOperationException("Inne pobieranie odpowiedzi rarelist jest już aktywne.");
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

    private readonly record struct CapturedResponse(bool HasPagerPrompt);

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

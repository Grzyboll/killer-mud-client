using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

namespace MudClient.App.Controls;

/// <summary>
/// MUD output window. Incoming text is parsed into a shared <see cref="OutputBuffer"/>
/// (a ring of at most <see cref="MaximumLines"/> lines) and rendered by two virtualized
/// <see cref="OutputPaneControl"/> panes: a scrollback pane and, while the user is scrolled
/// up, a live tail pane pinned to the bottom. Appending is O(chunk) regardless of how much
/// scrollback has accumulated, so multi-hour sessions stay responsive. Search works directly
/// on the shared ring buffer and reveals matches through the virtualized scrollback pane.
/// </summary>
public partial class MudOutputView : UserControl
{
    public static readonly StyledProperty<FontFamily> OutputFontFamilyProperty =
        AvaloniaProperty.Register<MudOutputView, FontFamily>(
            nameof(OutputFontFamily), new FontFamily("Consolas"));

    public static readonly StyledProperty<double> OutputFontSizeProperty =
        AvaloniaProperty.Register<MudOutputView, double>(
            nameof(OutputFontSize), 14);

    public static readonly StyledProperty<FontWeight> OutputFontWeightProperty =
        AvaloniaProperty.Register<MudOutputView, FontWeight>(
            nameof(OutputFontWeight), FontWeight.Normal);

    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<MudOutputView, bool>(nameof(WordWrap), true);

    public static readonly StyledProperty<string> TelnetColorSchemeProperty =
        AvaloniaProperty.Register<MudOutputView, string>(
            nameof(TelnetColorScheme), AnsiColorPalette.Warm);

    public static readonly StyledProperty<int> MaxOutputLinesProperty =
        AvaloniaProperty.Register<MudOutputView, int>(nameof(MaxOutputLines), MaximumLines);

    public FontFamily OutputFontFamily
    {
        get => GetValue(OutputFontFamilyProperty);
        set => SetValue(OutputFontFamilyProperty, value);
    }

    public double OutputFontSize
    {
        get => GetValue(OutputFontSizeProperty);
        set => SetValue(OutputFontSizeProperty, value);
    }

    public FontWeight OutputFontWeight
    {
        get => GetValue(OutputFontWeightProperty);
        set => SetValue(OutputFontWeightProperty, value);
    }

    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public string TelnetColorScheme
    {
        get => GetValue(TelnetColorSchemeProperty);
        set => SetValue(TelnetColorSchemeProperty, value);
    }

    /// <summary>Ring buffer capacity in lines — see <see cref="MaxOutputLinesProperty"/>'s own
    /// xmldoc for how changing it at runtime is handled.</summary>
    public int MaxOutputLines
    {
        get => GetValue(MaxOutputLinesProperty);
        set => SetValue(MaxOutputLinesProperty, value);
    }

    /// <summary>Default/fallback ring buffer capacity — see <see cref="Models.AppSettings.DefaultTerminalMaxLines"/>
    /// (the two are meant to stay in sync so an unset/unbound view behaves the same as the
    /// settings default).</summary>
    private const int MaximumLines = 10_000;

    private readonly AnsiStreamParser _parser = new();
    private OutputBuffer _buffer = new(MaximumLines);
    private readonly ScrollViewer _scrollbackScroller;
    private readonly ScrollViewer _liveTailScroller;
    private readonly OutputPaneControl _scrollbackPane;
    private readonly OutputPaneControl _liveTailPane;
    private readonly Grid _splitBar;
    private readonly Grid _grid;
    private bool _isSplitMode;
    private string _searchQuery = string.Empty;
    private OutputMatch? _selectedSearchMatch;

    // Applying a font change invalidates every cached line layout, so dragging the size
    // slider (which fires a change per pixel) would otherwise re-lay-out the panes dozens
    // of times a second. Coalesce rapid changes into a single apply once the value settles.
    private readonly DispatcherTimer _fontApplyTimer;

    public MudOutputView()
    {
        InitializeComponent();
        _scrollbackScroller = this.FindControl<ScrollViewer>("ScrollbackScroller")
            ?? throw new InvalidOperationException("ScrollbackScroller not found.");
        _liveTailScroller = this.FindControl<ScrollViewer>("LiveTailScroller")
            ?? throw new InvalidOperationException("LiveTailScroller not found.");
        _splitBar = this.FindControl<Grid>("SplitBar")
            ?? throw new InvalidOperationException("SplitBar not found.");
        _grid = this.FindControl<Grid>("OutputGrid")
            ?? throw new InvalidOperationException("OutputGrid not found.");
        _scrollbackPane = new OutputPaneControl { Buffer = _buffer, PinToBottom = true };
        _scrollbackScroller.Content = _scrollbackPane;

        _liveTailPane = new OutputPaneControl { Buffer = _buffer, PinToBottom = true };
        _liveTailScroller.Content = _liveTailPane;

        _fontApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fontApplyTimer.Tick += OnFontApplyTimerTick;

        ApplyFontToPanes();
        ApplyWordWrapToPanes();

        // Start in single-pane mode.
        _isSplitMode = false;
        _splitBar.IsVisible = false;
        _liveTailScroller.IsVisible = false;

        _scrollbackScroller.ScrollChanged += OnScrollbackScrollChanged;
    }

    public void AppendText(string text)
    {
        foreach (var token in _parser.Feed(text))
        {
            switch (token)
            {
                case AnsiTextToken textToken:
                    _buffer.Append(textToken.Text, textToken.Style);
                    break;

                case AnsiNewLineToken:
                    _buffer.CompleteLine();
                    break;

                case AnsiCarriageReturnToken:
                    break;
            }
        }

        // No timers, no dispatcher round-trips: the buffer already holds the new text and
        // both panes repaint on the next frame. Under heavy load repaints coalesce per frame.
        _scrollbackPane.NotifyContentChanged();
        if (_isSplitMode)
        {
            _liveTailPane.NotifyContentChanged();
        }
    }

    public void Clear()
    {
        SetSplitMode(false);
        _buffer.Clear();
        _scrollbackPane.ClearSelection();
        _liveTailPane.ClearSelection();
        _scrollbackPane.NotifyContentChanged();
        _liveTailPane.NotifyContentChanged();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if ((change.Property == OutputFontFamilyProperty || change.Property == OutputFontSizeProperty
             || change.Property == OutputFontWeightProperty)
            && _scrollbackPane is not null)
        {
            // Debounce: restart the timer so a burst of changes (slider drag) applies once.
            _fontApplyTimer.Stop();
            _fontApplyTimer.Start();
        }

        else if (change.Property == WordWrapProperty && _scrollbackPane is not null)
        {
            ApplyWordWrapToPanes();
        }
        else if (change.Property == TelnetColorSchemeProperty)
        {
            _parser.SetColorScheme(TelnetColorScheme);
        }
        else if (change.Property == MaxOutputLinesProperty && _scrollbackPane is not null)
        {
            SetMaxLines(MaxOutputLines);
        }
    }

    /// <summary>Reallocates the ring buffer to a new capacity — the settings slider's own
    /// "Ustawienia" xmldoc explains why this is a hard reset (a fresh, empty buffer) rather than
    /// an in-place resize that keeps the most recent lines: <see cref="OutputPaneControl"/> caches
    /// per-line height/layout state keyed off the buffer's own internal ring positions, and this
    /// setting changes rarely enough (a deliberate settings action, not a hot path) that losing
    /// scrollback once is a fair trade against the risk of subtly corrupting that cache. A no-op
    /// if the capacity isn't actually changing.</summary>
    private void SetMaxLines(int maxLines)
    {
        var capacity = Math.Max(2, maxLines);
        if (capacity == _buffer.Capacity)
        {
            return;
        }

        _buffer = new OutputBuffer(capacity);
        _scrollbackPane.Buffer = _buffer;
        _liveTailPane.Buffer = _buffer;
    }

    private void OnFontApplyTimerTick(object? sender, EventArgs e)
    {
        _fontApplyTimer.Stop();
        ApplyFontToPanes();
    }

    private void ApplyFontToPanes()
    {
        _scrollbackPane.SetFont(OutputFontFamily, OutputFontSize, OutputFontWeight);
        _liveTailPane.SetFont(OutputFontFamily, OutputFontSize, OutputFontWeight);
    }

    private void ApplyWordWrapToPanes()
    {
        _scrollbackPane.WordWrap = WordWrap;
        _liveTailPane.WordWrap = WordWrap;
        _scrollbackScroller.HorizontalScrollBarVisibility = WordWrap
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        _liveTailScroller.HorizontalScrollBarVisibility = WordWrap
            ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private async void CopySelection_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await CopySelectionToClipboardAsync();
    }

    internal bool HasSelection =>
        _scrollbackPane.GetSelectedText() is not null
        || _liveTailPane.GetSelectedText() is not null;

    internal async Task CopySelectionToClipboardAsync()
    {
        var text = _scrollbackPane.GetSelectedText() ?? _liveTailPane.GetSelectedText();
        await CopyToClipboardAsync(text);
    }

    private async void CopySelectionAsImage_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        var bitmap = _scrollbackPane.CreateSelectionBitmap()
            ?? _liveTailPane.CreateSelectionBitmap();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;

        if (bitmap is not null && clipboard is not null)
        {
            await clipboard.SetBitmapAsync(bitmap);
        }
    }

    private void ClearOutput_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        Clear();
    }

    private void CloseSplit_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        SetSplitMode(false);
    }

    internal bool UpdateSearch(string query) => Search(query, SearchDirection.Initial);

    internal bool Search(string query, bool newer = false) =>
        Search(query, newer ? SearchDirection.Newer : SearchDirection.Older);

    internal string? SelectedSearchText => _scrollbackPane.GetSelectedText();

    internal long? SelectedSearchGlobalLine => _selectedSearchMatch?.GlobalLine;

    private bool Search(string query, SearchDirection direction)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            _searchQuery = string.Empty;
            _selectedSearchMatch = null;
            _scrollbackPane.ClearSelection();
            return false;
        }

        var matches = FindMatches(query);
        if (matches.Count == 0)
        {
            _searchQuery = query;
            _selectedSearchMatch = null;
            _scrollbackPane.ClearSelection();
            return false;
        }

        var currentIndex = string.Equals(_searchQuery, query, StringComparison.OrdinalIgnoreCase)
            && _selectedSearchMatch is { } current
            ? matches.IndexOf(current)
            : -1;

        var matchIndex = direction switch
        {
            SearchDirection.Newer when currentIndex >= 0 => (currentIndex + 1) % matches.Count,
            SearchDirection.Older when currentIndex >= 0 => (currentIndex - 1 + matches.Count) % matches.Count,
            _ => matches.Count - 1
        };

        var match = matches[matchIndex];
        _searchQuery = query;
        _selectedSearchMatch = match;
        SetSplitMode(true);
        return _scrollbackPane.SelectAndReveal(match.GlobalLine, match.StartCharacter, match.Length);
    }

    private List<OutputMatch> FindMatches(string query)
    {
        var matches = new List<OutputMatch>();
        for (var lineIndex = 0; lineIndex < _buffer.Count; lineIndex++)
        {
            var text = _buffer[lineIndex].Text;
            var start = 0;
            while (start <= text.Length - query.Length)
            {
                var matchIndex = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    break;
                }

                matches.Add(new OutputMatch(
                    _buffer.FirstGlobalIndex + lineIndex,
                    matchIndex,
                    query.Length));
                start = matchIndex + Math.Max(1, query.Length);
            }
        }

        return matches;
    }

    private void Output_OnPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!_isSplitMode
            || !eventArgs.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        SetSplitMode(false);
        eventArgs.Handled = true;
    }

    private async void CopyAll_OnClick(object? sender, RoutedEventArgs eventArgs)
    {
        await CopyToClipboardAsync(_buffer.GetAllText());
    }

    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
        }
    }

    private void OnScrollbackScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        const double bottomTolerance = 10.0;
        const double splitActivationThreshold = 30.0;

        double distanceFromBottom = _scrollbackScroller.Extent.Height
            - _scrollbackScroller.Viewport.Height
            - _scrollbackScroller.Offset.Y;

        if (distanceFromBottom <= bottomTolerance && _isSplitMode)
        {
            SetSplitMode(false);
            return;
        }

        if (distanceFromBottom > splitActivationThreshold
            && !_isSplitMode
            && e.OffsetDelta.Y < 0)
        {
            SetSplitMode(true);
        }
    }

    private void SetSplitMode(bool enabled)
    {
        if (_isSplitMode == enabled)
            return;

        _isSplitMode = enabled;
        _scrollbackPane.PinToBottom = !enabled;

        if (enabled)
        {
            _grid.RowDefinitions[0].Height = new GridLength(2, GridUnitType.Star);
            _grid.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Star);
            _liveTailPane.NotifyContentChanged();
        }
        else
        {
            _grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            _grid.RowDefinitions[2].Height = new GridLength(0);
            _scrollbackPane.NotifyContentChanged();
        }

        _splitBar.IsVisible = enabled;
        _liveTailScroller.IsVisible = enabled;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private enum SearchDirection
    {
        Initial,
        Older,
        Newer
    }

    private readonly record struct OutputMatch(long GlobalLine, int StartCharacter, int Length);
}

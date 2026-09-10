namespace MudClient.Core.BuffTimers;

public sealed class BuffTrackingEngine
{
    private static readonly TimeSpan PendingCastBaseLifetime = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PendingCastQueueAllowance = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, DateTimeOffset> _pendingCasts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveState> _active = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _lastAffects = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasAffectsBaseline;
    private bool _isInCombat;
    private int _level;

    public event Action<BuffMeasurement>? MeasurementCompleted;

    public IReadOnlyList<ActiveBuffCheckpoint> Checkpoints => _active.Values
        .Select(value => value.ToCheckpoint(_isInCombat))
        .ToList();

    public void SetLevel(int level) => _level = Math.Max(0, level);

    public string? ObserveCommand(string command, string? characterName, DateTimeOffset now)
    {
        ExpirePending(now);
        if (SelfBuffCastParser.TryParse(command, characterName, out var buffName))
        {
            // Command stacking sends an entire cast sequence to the server immediately, while
            // the MUD executes it one spell at a time with its own casting delay. Give later
            // casts a larger confirmation window without weakening the conservative 12-second
            // window used for a single spell.
            var castsAhead = _pendingCasts.ContainsKey(buffName)
                ? Math.Max(0, _pendingCasts.Count - 1)
                : _pendingCasts.Count;
            _pendingCasts[buffName] = now
                + PendingCastBaseLifetime
                + TimeSpan.FromTicks(PendingCastQueueAllowance.Ticks * castsAhead);
            return buffName;
        }

        return null;
    }

    public void ProcessAffects(IEnumerable<string> affectNames, DateTimeOffset now)
    {
        Accumulate(now);
        ExpirePending(now);
        var current = affectNames
            .Select(SelfBuffCastParser.NormalizeName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!_hasAffectsBaseline)
        {
            _lastAffects = current;
            _hasAffectsBaseline = true;
            return;
        }

        foreach (var appeared in current.Except(_lastAffects))
        {
            if (_pendingCasts.Remove(appeared, out _))
            {
                _active[appeared] = new ActiveState(appeared, now, now, _level);
            }
        }

        foreach (var disappeared in _lastAffects.Except(current))
        {
            if (_active.Remove(disappeared, out var state))
            {
                Complete(state, now, BuffMeasurementEndReason.NaturalExpiration);
            }
        }

        _lastAffects = current;
    }

    public void SetCombat(bool isInCombat, DateTimeOffset now)
    {
        Accumulate(now);
        _isInCombat = isInCombat;
    }

    public void Tick(DateTimeOffset now) => Accumulate(now);

    public IReadOnlyList<BuffMeasurement> EndSession(DateTimeOffset now, BuffMeasurementEndReason reason)
    {
        Accumulate(now);
        var incomplete = _active.Values.Select(state => CreateMeasurement(state, now, reason)).ToList();
        _active.Clear();
        _pendingCasts.Clear();
        _lastAffects.Clear();
        _hasAffectsBaseline = false;
        _isInCombat = false;
        return incomplete;
    }

    private void Accumulate(DateTimeOffset now)
    {
        foreach (var state in _active.Values)
        {
            var seconds = Math.Max(0, (now - state.LastUpdatedAtUtc).TotalSeconds);
            if (_isInCombat)
            {
                state.CombatSeconds += seconds;
            }
            else
            {
                state.NonCombatSeconds += seconds;
            }

            state.LastUpdatedAtUtc = now;
        }
    }

    private void Complete(ActiveState state, DateTimeOffset now, BuffMeasurementEndReason reason) =>
        MeasurementCompleted?.Invoke(CreateMeasurement(state, now, reason));

    private static BuffMeasurement CreateMeasurement(
        ActiveState state, DateTimeOffset now, BuffMeasurementEndReason reason) => new(
            Guid.NewGuid(), state.BuffName, state.StartedAtUtc, now,
            Math.Max(0, (now - state.StartedAtUtc).TotalSeconds), state.CharacterLevel,
            state.CombatSeconds, state.NonCombatSeconds, reason, now);

    private void ExpirePending(DateTimeOffset now)
    {
        foreach (var name in _pendingCasts
                     .Where(pair => now > pair.Value)
                     .Select(pair => pair.Key).ToList())
        {
            _pendingCasts.Remove(name);
        }
    }

    private sealed class ActiveState(
        string buffName, DateTimeOffset startedAtUtc, DateTimeOffset lastUpdatedAtUtc, int characterLevel)
    {
        public string BuffName { get; } = buffName;
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset LastUpdatedAtUtc { get; set; } = lastUpdatedAtUtc;
        public int CharacterLevel { get; } = characterLevel;
        public double CombatSeconds { get; set; }
        public double NonCombatSeconds { get; set; }

        public ActiveBuffCheckpoint ToCheckpoint(bool isInCombat) => new(
            BuffName, StartedAtUtc, LastUpdatedAtUtc, CharacterLevel,
            CombatSeconds, NonCombatSeconds, isInCombat);
    }
}

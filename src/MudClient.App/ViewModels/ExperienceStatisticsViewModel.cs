using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MudClient.App.Models;
using MudClient.Core.Statistics;

namespace MudClient.App.ViewModels;

public sealed partial class ExperienceStatisticsViewModel : ObservableObject
{
    private ExperienceStatisticsData _data = new();
    private ExperienceSessionData _session = new();
    private readonly List<CombatDamageData> _pendingCombatDamage = [];
    public ObservableCollection<MobExperienceSummary> Mobs { get; } = [];
    public ObservableCollection<SessionExperienceSummary> History { get; } = [];

    [ObservableProperty] private long _gainedExperience;
    [ObservableProperty] private long _damageExperience;
    [ObservableProperty] private long _killExperience;
    [ObservableProperty] private long _lostExperience;
    [ObservableProperty] private long _fleeLoss;
    [ObservableProperty] private long _deathLoss;
    [ObservableProperty] private int _fleeCount;
    [ObservableProperty] private int _deathCount;
    [ObservableProperty] private long _ownCombatDamage;
    [ObservableProperty] private long _groupCombatDamage;
    [ObservableProperty] private int _killCount;
    [ObservableProperty] private string _lastEnemy = "—";
    [ObservableProperty] private long _lastKillExperience;
    [ObservableProperty] private long? _remainingToLevel;

    public DateTimeOffset SessionStartedAt => _session.StartedAt;
    public TimeSpan SessionDuration => DateTimeOffset.Now - _session.StartedAt;
    public string SessionDurationText => FormatDuration(SessionDuration);
    public string DamageAndKillExperienceText => $"{DamageExperience:N0} / {KillExperience:N0}";
    public string FleeAndDeathCountText => $"{FleeCount:N0} / {DeathCount:N0}";
    public string FleeAndDeathLossText => $"{FleeLoss:N0} / {DeathLoss:N0}";
    public string KillsAndAverageText => $"{KillCount:N0} / {AveragePerKill:N0}";
    public string OwnAndGroupDamageText => $"{OwnCombatDamage:N0} / {GroupCombatDamage:N0}";
    public long NetExperience => GainedExperience - LostExperience;
    public double ExperiencePerHour => SessionDuration.TotalHours <= 0 ? 0 : NetExperience / SessionDuration.TotalHours;
    public double AveragePerKill => KillCount == 0 ? 0 : (double)KillExperience / KillCount;
    public double? LastKillProgressPercent => RemainingToLevel is > 0 ? 100d * LastKillExperience / (RemainingToLevel.Value + LastKillExperience) : null;
    public TimeSpan? EstimatedTimeToLevel => RemainingToLevel is > 0 && ExperiencePerHour > 0 ? TimeSpan.FromHours(RemainingToLevel.Value / ExperiencePerHour) : null;
    public long BestExperiencePerHour => History.Count == 0 ? 0 : History.Max(item => item.ExperiencePerHour);
    public long LargestKill => AllChanges.Where(change => change.Kind == ExperienceChangeKind.KillReward).Select(change => change.Amount).DefaultIfEmpty().Max();
    public string LargestKillDetails => DescribeRecord(ExperienceChangeKind.KillReward);
    public string StrongestHitDetails
    {
        get
        {
            var encounter = AllCombatEncounters.Where(item => item.StrongestHitIsOwn)
                .MaxBy(item => item.StrongestHit);
            var pending = _pendingCombatDamage.Where(item => item.IsOwnDamage).MaxBy(item => item.Amount);
            if (pending is not null && (encounter is null || pending.Amount > encounter.StrongestHit))
            {
                return $"~{pending.Amount:N0} — {DisplayEnemy(pending.EnemyName)}, {pending.When:g}";
            }
            return encounter is null
                ? "—"
                : $"~{encounter.StrongestHit:N0} — {DisplayEnemy(encounter.EnemyName)}, {encounter.StrongestHitWhen:g}";
        }
    }
    public int TotalKills => AllChanges.Count(change => change.Kind == ExperienceChangeKind.KillReward);
    public long TotalRecordedExperience => History.Sum(session => session.Experience);
    public string TotalRecordedDurationText => FormatDuration(TimeSpan.FromTicks(History.Sum(session => session.Duration.Ticks)));
    public string LongestSessionDetails => History.OrderByDescending(session => session.Duration).FirstOrDefault() is { } session ? $"{session.DurationText} ({session.StartedAt:g})" : "—";
    public string BestSessionDetails => History.OrderByDescending(session => session.Experience).FirstOrDefault() is { } session ? $"{session.Experience:N0} EXP ({session.StartedAt:g})" : "—";
    public string MostKilledOverall => AllChanges.Where(change => change.Kind == ExperienceChangeKind.KillReward)
        .GroupBy(change => DisplayEnemy(change.EnemyName), StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(group => $"{group.Key} ({group.Count():N0})").FirstOrDefault() ?? "—";
    private IEnumerable<ExperienceChangeData> AllChanges => _data.Sessions.SelectMany(session => session.Changes);
    private IEnumerable<CombatEncounterData> AllCombatEncounters =>
        _data.Sessions.SelectMany(session => session.CombatEncounters);

    public void Start(ExperienceStatisticsData data)
    {
        CompactLegacyCombatDamage(data);
        _data = data;
        _session = new ExperienceSessionData();
        _pendingCombatDamage.Clear();
        _data.Sessions.Add(_session);
        ResetHealthRuntime();
        Refresh();
    }

    public void Apply(IEnumerable<ExperienceChange> changes)
    {
        foreach (var change in changes)
        {
            if (change.Kind == ExperienceChangeKind.KillReward)
            {
                CompleteCombatEncounter(change);
            }
            _session.Changes.Add(new ExperienceChangeData
            {
                Kind = change.Kind, Amount = change.Amount, EnemyName = change.EnemyName,
                Level = change.Level, RemainingToLevel = change.RemainingToLevel, When = change.When,
            });
            _session.LastUpdatedAt = change.When;
        }
        Refresh();
    }

    public void ApplyCombatDamage(int amount, string? enemyName, string? attackerName, bool isOwnDamage, DateTimeOffset? when = null)
    {
        var timestamp = when ?? DateTimeOffset.Now;
        _pendingCombatDamage.RemoveAll(hit => timestamp - hit.When > TimeSpan.FromHours(1));
        _pendingCombatDamage.Add(new CombatDamageData
        {
            Amount = amount,
            EnemyName = enemyName,
            AttackerName = attackerName,
            IsOwnDamage = isOwnDamage,
            When = timestamp,
        });
        RefreshCombatDamageTotals();
        OnPropertyChanged(nameof(OwnAndGroupDamageText));
        OnPropertyChanged(nameof(StrongestHitDetails));
    }

    public ExperienceStatisticsData Data => _data;

    public void Reset()
    {
        Start(new ExperienceStatisticsData());
    }

    private void Refresh()
    {
        var now = DateTimeOffset.Now;
        var gains = _session.Changes.Where(IsGain).ToList();
        var losses = _session.Changes.Where(IsLoss).ToList();
        DamageExperience = gains.Where(change => change.Kind == ExperienceChangeKind.Damage).Sum(change => change.Amount);
        KillExperience = gains.Where(change => change.Kind == ExperienceChangeKind.KillReward).Sum(change => change.Amount);
        GainedExperience = gains.Sum(change => change.Amount);
        FleeLoss = losses.Where(change => change.Kind == ExperienceChangeKind.FleeLoss).Sum(change => change.Amount);
        DeathLoss = losses.Where(change => change.Kind == ExperienceChangeKind.DeathLoss).Sum(change => change.Amount);
        FleeCount = _session.Changes.Count(change => change.Kind == ExperienceChangeKind.Flee);
        DeathCount = _session.Changes.Count(change => change.Kind == ExperienceChangeKind.Death);
        LostExperience = losses.Sum(change => change.Amount);
        RefreshCombatDamageTotals();
        var kills = gains.Where(change => change.Kind == ExperienceChangeKind.KillReward).ToList();
        KillCount = kills.Count;
        if (kills.LastOrDefault() is { } last) { LastEnemy = DisplayEnemy(last.EnemyName); LastKillExperience = last.Amount; }
        RemainingToLevel = _session.Changes.LastOrDefault(change => change.RemainingToLevel.HasValue)?.RemainingToLevel;

        var allChanges = AllChanges.ToList();
        Mobs.Clear();
        foreach (var group in allChanges.Where(change => change.Kind == ExperienceChangeKind.KillReward)
                     .GroupBy(change => DisplayEnemy(change.EnemyName), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => group.Max(change => change.When)))
        {
            var mobChanges = allChanges.Where(change => string.Equals(DisplayEnemy(change.EnemyName), group.Key, StringComparison.OrdinalIgnoreCase)
                    && change.Kind is ExperienceChangeKind.KillReward or ExperienceChangeKind.Damage)
                .OrderBy(change => change.When).ToList();
            var combatEncounters = AllCombatEncounters
                .Where(encounter => string.Equals(
                    DisplayEnemy(encounter.EnemyName), group.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Mobs.Add(new MobExperienceSummary(group.Key, mobChanges, combatEncounters));
        }

        History.Clear();
        foreach (var session in _data.Sessions.OrderByDescending(item => item.StartedAt))
            History.Add(new SessionExperienceSummary(session, ReferenceEquals(session, _session), now));

        OnPropertyChanged(string.Empty);
    }

    private void CompleteCombatEncounter(ExperienceChange change)
    {
        var hits = _pendingCombatDamage.Where(hit =>
                string.IsNullOrWhiteSpace(change.EnemyName) ||
                string.IsNullOrWhiteSpace(hit.EnemyName) ||
                string.Equals(hit.EnemyName, change.EnemyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (hits.Count == 0)
        {
            return;
        }

        var strongest = hits.Where(hit => hit.IsOwnDamage).MaxBy(hit => hit.Amount);
        _session.CombatEncounters.Add(new CombatEncounterData
        {
            EnemyName = change.EnemyName,
            When = change.When,
            OwnDamage = hits.Where(hit => hit.IsOwnDamage).Sum(hit => (long)hit.Amount),
            GroupDamage = hits.Sum(hit => (long)hit.Amount),
            StrongestHit = strongest?.Amount ?? 0,
            StrongestHitAttackerName = strongest?.AttackerName,
            StrongestHitWhen = strongest?.When ?? default,
            StrongestHitIsOwn = strongest is not null,
        });
        _pendingCombatDamage.RemoveAll(hits.Contains);
    }

    private void RefreshCombatDamageTotals()
    {
        OwnCombatDamage = _session.CombatEncounters.Sum(item => item.OwnDamage) +
                          _pendingCombatDamage.Where(hit => hit.IsOwnDamage).Sum(hit => (long)hit.Amount);
        GroupCombatDamage = _session.CombatEncounters.Sum(item => item.GroupDamage) +
                            _pendingCombatDamage.Sum(hit => (long)hit.Amount);
    }

    private static void CompactLegacyCombatDamage(ExperienceStatisticsData data)
    {
        foreach (var session in data.Sessions)
        {
            foreach (var group in session.CombatDamage
                         .Where(hit => hit.EncounterWhen.HasValue)
                         .GroupBy(hit => hit.EncounterWhen!.Value))
            {
                var hits = group.ToList();
                var strongest = hits.Where(hit => hit.IsOwnDamage).MaxBy(hit => hit.Amount);
                session.CombatEncounters.Add(new CombatEncounterData
                {
                    EnemyName = hits.Select(hit => hit.EnemyName)
                        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                    When = group.Key,
                    OwnDamage = hits.Where(hit => hit.IsOwnDamage).Sum(hit => (long)hit.Amount),
                    GroupDamage = hits.Sum(hit => (long)hit.Amount),
                    StrongestHit = strongest?.Amount ?? 0,
                    StrongestHitAttackerName = strongest?.AttackerName,
                    StrongestHitWhen = strongest?.When ?? default,
                    StrongestHitIsOwn = strongest is not null,
                });
            }
            session.CombatDamage.Clear();
        }
    }

    private string DescribeRecord(ExperienceChangeKind kind)
    {
        var record = AllChanges.Where(change => change.Kind == kind).MaxBy(change => change.Amount);
        return record is null ? "—" : $"{record.Amount:N0} EXP — {DisplayEnemy(record.EnemyName)}, {record.When:g}";
    }

    private static bool IsGain(ExperienceChangeData change) => change.Kind is ExperienceChangeKind.Damage or ExperienceChangeKind.KillReward or ExperienceChangeKind.UnknownGain;
    private static bool IsLoss(ExperienceChangeData change) => change.Kind is ExperienceChangeKind.FleeLoss or ExperienceChangeKind.DeathLoss;
    private static string DisplayEnemy(string? enemyName) => string.IsNullOrWhiteSpace(enemyName) ? "Nieznany przeciwnik" : enemyName;
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return $"{(long)duration.TotalHours:00}h {duration.Minutes:00}m {duration.Seconds:00}s";
    }
}

public sealed class MobExperienceSummary
{
    public MobExperienceSummary(string name, IReadOnlyList<ExperienceChangeData> changes, IReadOnlyList<CombatEncounterData>? combatEncounters = null)
    {
        var kills = changes.Where(item => item.Kind == ExperienceChangeKind.KillReward).ToList();
        Name = name; Kills = kills.Count; Total = kills.Sum(item => item.Amount);
        DamageExperience = changes.Where(item => item.Kind == ExperienceChangeKind.Damage).Sum(item => item.Amount);
        Average = Kills == 0 ? 0 : Total / Kills;
        AverageTotalExperience = Kills == 0 ? 0 : (Total + DamageExperience) / Kills;
        TotalGroupCombatDamage = combatEncounters?.Sum(item => item.GroupDamage) ?? 0;
        AverageGroupCombatDamage = combatEncounters is not { Count: > 0 }
            ? 0
            : TotalGroupCombatDamage / combatEncounters.Count;
        Last = kills[^1].Amount; LastKilledAt = kills[^1].When;
        LastTotalExperience = Last + changes.Where(item => item.Kind == ExperienceChangeKind.Damage && item.When == LastKilledAt)
            .Sum(item => item.Amount);
        LastApproximateHp = combatEncounters?.Where(item => item.When == LastKilledAt)
            .Sum(item => item.GroupDamage) ?? 0;
        RecentAverage = (long)kills.TakeLast(Math.Min(5, Kills)).Average(item => item.Amount);
        Trend = Kills < 2 ? "Za mało danych" : RecentAverage < Average * 0.95 ? "Spada" : RecentAverage > Average * 1.05 ? "Rośnie" : "Stabilny";
        var recentKills = kills.TakeLast(Math.Min(20, Kills)).ToList();
        var maximum = recentKills.Select(item => item.Amount).DefaultIfEmpty(1).Max();
        TrendPoints = recentKills.Select(item => new MobTrendPoint(
            item.Amount, 4d + 24d * item.Amount / Math.Max(1d, maximum))).ToList();
    }
    public string Name { get; }
    public int Kills { get; }
    public long Total { get; }
    public long DamageExperience { get; }
    public long Average { get; }
    public long AverageTotalExperience { get; }
    public long TotalGroupCombatDamage { get; }
    public long AverageGroupCombatDamage { get; }
    public long RecentAverage { get; }
    public long Last { get; }
    public long LastTotalExperience { get; }
    public long LastApproximateHp { get; }
    public string Trend { get; }
    public DateTimeOffset LastKilledAt { get; }
    public IReadOnlyList<MobTrendPoint> TrendPoints { get; }
}

public sealed record MobTrendPoint(long Value, double Height);

public sealed class SessionExperienceSummary
{
    public SessionExperienceSummary(ExperienceSessionData session, bool isCurrent = false, DateTimeOffset? now = null)
    {
        StartedAt = session.StartedAt; EndedAt = isCurrent ? now ?? DateTimeOffset.Now : session.LastUpdatedAt;
        if (EndedAt < StartedAt) EndedAt = StartedAt;
        Duration = EndedAt - StartedAt; DurationText = ExperienceStatisticsViewModel.FormatDuration(Duration);
        Experience = session.Changes.Where(change => change.Kind is ExperienceChangeKind.Damage or ExperienceChangeKind.KillReward or ExperienceChangeKind.UnknownGain).Sum(change => change.Amount)
            - session.Changes.Where(change => change.Kind is ExperienceChangeKind.FleeLoss or ExperienceChangeKind.DeathLoss).Sum(change => change.Amount);
        Kills = session.Changes.Count(change => change.Kind == ExperienceChangeKind.KillReward);
        ExperiencePerHour = Duration.TotalHours <= 0 ? 0 : (long)(Experience / Duration.TotalHours);
        MostKilled = session.Changes.Where(change => change.Kind == ExperienceChangeKind.KillReward)
            .GroupBy(change => string.IsNullOrWhiteSpace(change.EnemyName) ? "Nieznany" : change.EnemyName)
            .OrderByDescending(group => group.Count()).FirstOrDefault()?.Key ?? "—";
    }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset EndedAt { get; private set; }
    public TimeSpan Duration { get; }
    public string DurationText { get; }
    public long Experience { get; }
    public long ExperiencePerHour { get; }
    public int Kills { get; }
    public string MostKilled { get; }
}

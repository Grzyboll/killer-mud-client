using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using MudClient.App.Models;

namespace MudClient.App.ViewModels;

public sealed partial class ExperienceStatisticsViewModel
{
    private static readonly TimeSpan HealthLineCorrelationWindow = TimeSpan.FromSeconds(1);
    private int? _lastHealthPoints;
    private int? _lastMaximumHealthPoints;
    private bool _healthInCombat;
    private bool _healthResting;
    private string? _activeHealthCombatId;
    private string? _lastHealthCombatId;
    private string? _pendingHealingSpell;
    private DateTimeOffset _pendingHealingSpellWhen;
    private string? _recentHealingCaster;
    private string? _activePeriodicHealer;
    private readonly HashSet<string> _recordedHealingTargets = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<HealthBreakdownRow> SessionHealthBreakdown { get; } = [];
    public ObservableCollection<HealthBreakdownRow> LastCombatHealthBreakdown { get; } = [];

    public long SessionHealthLost => _session.HealthEvents.Where(IsDamage).Sum(item => (long)item.Amount);
    public long SessionHealthRestored => _session.HealthEvents.Where(IsOwnHealthRestoration).Sum(item => (long)item.Amount);
    public long SessionHealingGiven => _session.HealthEvents.Where(item => item.Kind == HealthEventKind.HealingGiven).Sum(item => (long)item.Amount);
    public long LastCombatHealthLost => LastCombatHealthEvents.Where(IsDamage).Sum(item => (long)item.Amount);
    public long LastCombatHealthRestored => LastCombatHealthEvents.Where(IsOwnHealthRestoration).Sum(item => (long)item.Amount);
    public long LastCombatHealingGiven => LastCombatHealthEvents.Where(item => item.Kind == HealthEventKind.HealingGiven).Sum(item => (long)item.Amount);
    public string SessionHealthBalanceText => $"{SessionHealthRestored - SessionHealthLost:+#,0;-#,0;0} HP";
    public string LastCombatHealthBalanceText => $"{LastCombatHealthRestored - LastCombatHealthLost:+#,0;-#,0;0} HP";
    public int SessionLargestHealthLoss => _session.HealthEvents.Where(IsDamage).Select(item => item.Amount).DefaultIfEmpty().Max();
    public int SessionLargestHealing => _session.HealthEvents.Where(IsOwnHealthRestoration).Select(item => item.Amount).DefaultIfEmpty().Max();
    public int LastCombatLargestHealthLoss => LastCombatHealthEvents.Where(IsDamage).Select(item => item.Amount).DefaultIfEmpty().Max();
    public int LastCombatLargestHealing => LastCombatHealthEvents.Where(IsOwnHealthRestoration).Select(item => item.Amount).DefaultIfEmpty().Max();

    private IEnumerable<HealthEventData> LastCombatHealthEvents => string.IsNullOrWhiteSpace(_lastHealthCombatId)
        ? []
        : _session.HealthEvents.Where(item => item.CombatId == _lastHealthCombatId);

    public bool ObserveHealthVitals(int hitPoints, int maximumHitPoints, bool inCombat, bool resting,
        int characterLevel, DateTimeOffset? when = null)
    {
        var timestamp = when ?? DateTimeOffset.Now;
        SetHealthCombatState(inCombat, timestamp);
        _healthResting = resting;
        if (_lastHealthPoints is null || _lastMaximumHealthPoints != maximumHitPoints)
        {
            _lastHealthPoints = hitPoints;
            _lastMaximumHealthPoints = maximumHitPoints;
            return false;
        }

        var delta = hitPoints - _lastHealthPoints.Value;
        _lastHealthPoints = hitPoints;
        _lastMaximumHealthPoints = maximumHitPoints;
        if (delta == 0) return false;

        _session.HealthEvents.Add(new HealthEventData
        {
            Kind = delta < 0 ? HealthEventKind.DamageOther
                : resting ? HealthEventKind.HealingRest : HealthEventKind.HealingOther,
            Amount = Math.Abs(delta),
            CharacterLevel = characterLevel,
            CombatId = _activeHealthCombatId,
            When = timestamp,
        });
        _session.LastUpdatedAt = timestamp;
        RefreshHealth();
        return true;
    }

    public void ObserveHealthCombatState(bool inCombat, DateTimeOffset? when = null) =>
        SetHealthCombatState(inCombat, when ?? DateTimeOffset.Now);

    public bool ObserveHealthLine(string line, string characterName, int characterLevel,
        DateTimeOffset? when = null)
    {
        var timestamp = when ?? DateTimeOffset.Now;
        var text = line.Trim();
        var changed = false;

        var ownCast = Regex.Match(text, "^Wymawiasz slowa, '([^']+)'\\.$", RegexOptions.IgnoreCase);
        if (ownCast.Success && TryHealingEstimate(ownCast.Groups[1].Value, characterLevel, out _))
        {
            _pendingHealingSpell = ownCast.Groups[1].Value;
            _pendingHealingSpellWhen = timestamp;
            _recentHealingCaster = characterName;
            _recordedHealingTargets.Clear();
        }
        else
        {
            var caster = Regex.Match(text, "^(.+?) wymawia slowa, '([^']+)'\\.$", RegexOptions.IgnoreCase);
            if (caster.Success)
            {
                _recentHealingCaster = caster.Groups[1].Value.Trim();
            }
        }

        var mediatedHealing = Regex.Match(text,
            "^Za posrednictwem (.+?) wypelnia ciebie ogromna uzdrawiajaca energia", RegexOptions.IgnoreCase);
        if (mediatedHealing.Success)
        {
            _activePeriodicHealer = mediatedHealing.Groups[1].Value.Trim();
            _recentHealingCaster = _activePeriodicHealer;
        }

        var latest = _session.HealthEvents.LastOrDefault(item =>
            !item.IsEstimated && (timestamp - item.When).Duration() <= HealthLineCorrelationWindow);
        if (latest is not null)
        {
            changed |= ClassifyOwnHealthChange(latest, text, characterName);
        }

        if (_pendingHealingSpell is not null && timestamp - _pendingHealingSpellWhen <= TimeSpan.FromSeconds(2) &&
            TryReadHealedTarget(text, out var target) && !LooksLikeCharacter(target, characterName) &&
            _recordedHealingTargets.Add(target) &&
            TryHealingEstimate(_pendingHealingSpell, characterLevel, out var estimate))
        {
            _session.HealthEvents.Add(new HealthEventData
            {
                Kind = HealthEventKind.HealingGiven,
                Amount = estimate,
                Source = characterName,
                Ability = _pendingHealingSpell,
                Target = target,
                IsEstimated = true,
                CharacterLevel = characterLevel,
                CombatId = _activeHealthCombatId,
                When = timestamp,
            });
            _session.LastUpdatedAt = timestamp;
            changed = true;
        }

        if (changed) RefreshHealth();
        return changed;
    }

    private bool ClassifyOwnHealthChange(HealthEventData item, string text, string characterName)
    {
        if (item.Kind is HealthEventKind.DamageOther && IsDamageToSelfLine(text))
        {
            item.Kind = text.StartsWith("Zaklecie ", StringComparison.OrdinalIgnoreCase)
                ? HealthEventKind.DamageSpell
                : IsPeriodicDamageLine(text) ? HealthEventKind.DamagePeriodic : HealthEventKind.DamageAttack;
            item.Source = ReadDamageSource(text);
            item.Ability = ReadLeadingAbility(text);
            return true;
        }

        if (item.Kind is HealthEventKind.HealingOther && IsHealingSelfLine(text))
        {
            var own = string.Equals(_recentHealingCaster, characterName, StringComparison.OrdinalIgnoreCase);
            item.Kind = text.Contains("wzbiera", StringComparison.OrdinalIgnoreCase)
                ? HealthEventKind.HealingPeriodic
                : own ? HealthEventKind.HealingSelf : HealthEventKind.HealingReceived;
            item.Source = item.Kind == HealthEventKind.HealingPeriodic
                ? _activePeriodicHealer ?? _recentHealingCaster
                : _recentHealingCaster;
            item.Ability = own ? _pendingHealingSpell : null;
            return true;
        }
        return false;
    }

    private void SetHealthCombatState(bool inCombat, DateTimeOffset timestamp)
    {
        if (inCombat && !_healthInCombat)
        {
            _activeHealthCombatId = $"{timestamp.UtcTicks:x}";
            _lastHealthCombatId = _activeHealthCombatId;
        }
        else if (!inCombat && _healthInCombat)
        {
            _activeHealthCombatId = null;
        }
        _healthInCombat = inCombat;
    }

    private void ResetHealthRuntime()
    {
        _lastHealthPoints = null;
        _lastMaximumHealthPoints = null;
        _healthInCombat = false;
        _healthResting = false;
        _activeHealthCombatId = null;
        _lastHealthCombatId = _session.HealthEvents.LastOrDefault(item => item.CombatId is not null)?.CombatId;
        _pendingHealingSpell = null;
        _recentHealingCaster = null;
        _activePeriodicHealer = null;
        _recordedHealingTargets.Clear();
        RefreshHealth();
    }

    private void RefreshHealth()
    {
        RebuildHealthBreakdown(SessionHealthBreakdown, _session.HealthEvents);
        RebuildHealthBreakdown(LastCombatHealthBreakdown, LastCombatHealthEvents);
        OnPropertyChanged(nameof(SessionHealthLost));
        OnPropertyChanged(nameof(SessionHealthRestored));
        OnPropertyChanged(nameof(SessionHealingGiven));
        OnPropertyChanged(nameof(LastCombatHealthLost));
        OnPropertyChanged(nameof(LastCombatHealthRestored));
        OnPropertyChanged(nameof(LastCombatHealingGiven));
        OnPropertyChanged(nameof(SessionHealthBalanceText));
        OnPropertyChanged(nameof(LastCombatHealthBalanceText));
        OnPropertyChanged(nameof(SessionLargestHealthLoss));
        OnPropertyChanged(nameof(SessionLargestHealing));
        OnPropertyChanged(nameof(LastCombatLargestHealthLoss));
        OnPropertyChanged(nameof(LastCombatLargestHealing));
    }

    private static void RebuildHealthBreakdown(ObservableCollection<HealthBreakdownRow> target,
        IEnumerable<HealthEventData> events)
    {
        target.Clear();
        foreach (var group in events.GroupBy(item => new
                 {
                     item.Kind, Source = item.Source ?? "—", Ability = item.Ability ?? "—",
                     Target = item.Target ?? "—", item.IsEstimated,
                 }).OrderByDescending(group => group.Sum(item => item.Amount)))
        {
            target.Add(new HealthBreakdownRow(KindLabel(group.Key.Kind), group.Key.Source,
                group.Key.Ability, group.Key.Target, group.Sum(item => (long)item.Amount),
                group.Count(), group.Key.IsEstimated));
        }
    }

    private static bool IsDamage(HealthEventData item) => item.Kind <= HealthEventKind.DamageOther;
    private static bool IsHealing(HealthEventData item) => !IsDamage(item);
    private static bool IsOwnHealthRestoration(HealthEventData item) =>
        IsHealing(item) && item.Kind != HealthEventKind.HealingGiven;
    private static bool IsDamageToSelfLine(string text) => Regex.IsMatch(text,
        "(?:rani|muska|masakruje|rozpruwa|niszczy|grzmoci|rozrywa|rozbebesza|dekapituje|unicestwia) (?:cie|cię)(?:[!.]|$)", RegexOptions.IgnoreCase);
    private static bool IsPeriodicDamageLine(string text) => Regex.IsMatch(text,
        "truciz|krwaw|plonie|płonie|oparzen|poraża", RegexOptions.IgnoreCase);
    private static bool IsHealingSelfLine(string text) => Regex.IsMatch(text,
        "Twoje cialo wypelnia lecznicze|Twoje ciało wypełnia lecznicze|Lecznicze cieplo wypelnia twoje|uzdrawiajaca energia wzbiera", RegexOptions.IgnoreCase);

    private static string? ReadDamageSource(string text)
    {
        var match = Regex.Match(text, "^(?:Zaklecie |Ciecie |Dzgniecie |Walniecie |Uderzenie |Smagniecie )?(.+?) (?:muska|lekko rani|rani|mocno rani|dotkliwie rani|powaznie rani)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ReadLeadingAbility(string text)
    {
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : null;
    }

    private static bool TryReadHealedTarget(string text, out string target)
    {
        foreach (var pattern in new[]
                 {
                     "^(?:Niektore z siniakow|Kilka siniakow|Kilka zranien|Kilka ran|Kilka glebokich ran|Wiekszosc ran) (.+?) (?:znika|goi sie)",
                     "^(.+?) wypelnia ogromna uzdrawiajaca energia",
                 })
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success) { target = match.Groups[1].Value.Trim(); return true; }
        }
        target = string.Empty;
        return false;
    }

    private static bool LooksLikeCharacter(string inflectedTarget, string characterName)
    {
        var length = Math.Min(4, Math.Min(inflectedTarget.Length, characterName.Length));
        return length > 0 && inflectedTarget.StartsWith(characterName[..length], StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryHealingEstimate(string spell, int level, out int amount)
    {
        var level31 = spell.ToLowerInvariant() switch
        {
            "cure light" => 31,
            "cure serious" => 117,
            "cure critical" => 188,
            "mass cure critical" => 195,
            "heal" => 218,
            _ => 0,
        };
        amount = level31 == 0 ? 0 : Math.Max(1, (int)Math.Round(level31 * Math.Max(1, level) / 31d));
        return amount > 0;
    }

    private static string KindLabel(HealthEventKind kind) => kind switch
    {
        HealthEventKind.DamageAttack => "Obrażenia — ataki",
        HealthEventKind.DamageSpell => "Obrażenia — czary",
        HealthEventKind.DamagePeriodic => "Obrażenia — okresowe",
        HealthEventKind.DamageOther => "Obrażenia — inne",
        HealthEventKind.HealingSelf => "Leczenie — samemu",
        HealthEventKind.HealingReceived => "Leczenie — otrzymane",
        HealthEventKind.HealingGiven => "Leczenie — udzielone",
        HealthEventKind.HealingPeriodic => "Leczenie — okresowe",
        HealthEventKind.HealingRest => "Leczenie — odpoczynek",
        _ => "Leczenie — inne",
    };
}

public sealed record HealthBreakdownRow(string Category, string Source, string Ability, string Target,
    long Amount, int Count, bool IsEstimated)
{
    public string AmountText => IsEstimated ? $"~{Amount:N0} HP" : $"{Amount:N0} HP";
    public string Details => $"źródło: {Source}; efekt: {Ability}; cel: {Target}; zdarzenia: {Count:N0}";
}

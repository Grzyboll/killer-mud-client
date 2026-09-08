using MudClient.Core.Statistics;

namespace MudClient.Core.Tests;

public sealed class ExperienceTrackerTests
{
    [Fact]
    public void SeparatesSilentDamageFromExplicitKillReward()
    {
        var tracker = new ExperienceTracker { Level = 2 };
        tracker.ProcessLine("<29hp 857 85mv>");
        tracker.ObserveRoomPeople(["Gracz", "Kot"], ["Kot"]);
        tracker.ProcessLine("Kot nie zyje!!");
        tracker.ProcessLine("Zdobyles 133 punktow doswiadczenia.");

        var changes = tracker.ProcessLine("<29/138hp 703 85/100mv>");

        Assert.Collection(changes,
            change => { Assert.Equal(ExperienceChangeKind.Damage, change.Kind); Assert.Equal(21, change.Amount); },
            change => { Assert.Equal(ExperienceChangeKind.KillReward, change.Kind); Assert.Equal(133, change.Amount); Assert.Equal("Kot", change.EnemyName); });
    }

    [Fact]
    public void FleeMessageWithUnchangedPromptDoesNotInventLoss()
    {
        var tracker = new ExperienceTracker();
        tracker.ProcessLine("<30hp 272 90mv>");
        tracker.ProcessLine("Uciekasz z walki!");
        var eventChange = Assert.Single(tracker.ProcessLine("Tracisz troszke punktow doswiadczenia."));

        Assert.Empty(tracker.ProcessLine("<30hp 272 89mv>"));
        Assert.Equal(ExperienceChangeKind.Flee, eventChange.Kind);
    }

    [Fact]
    public void AttributesPositiveRemainingDeltaToDeathLoss()
    {
        var tracker = new ExperienceTracker();
        tracker.ProcessLine("<1hp 240 90mv>");
        var death = Assert.Single(tracker.ProcessLine("Nie zyjesz, co za pech!!!"));

        var change = Assert.Single(tracker.ProcessLine("<41hp 300 50mv>"));
        Assert.Equal(ExperienceChangeKind.Death, death.Kind);
        Assert.Equal(ExperienceChangeKind.DeathLoss, change.Kind);
        Assert.Equal(60, change.Amount);
    }

    [Fact]
    public void LevelAdvanceResetsPromptBaselineWithoutFalseLoss()
    {
        var tracker = new ExperienceTracker { Level = 1 };
        tracker.ProcessLine("<29hp 2 84mv>");

        var advance = tracker.ProcessLine("Zdobywasz poziom!");
        Assert.Equal(2, Assert.Single(advance).Amount);
        Assert.Empty(tracker.ProcessLine("<29hp 857 85mv>"));
        Assert.Equal(2, tracker.Level);
    }

    [Fact]
    public void AttributesRewardWhenFightingOpponentDisappearsBeforeReward()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00+02:00");
        var tracker = new ExperienceTracker();
        tracker.ProcessLine("<50hp 1000 90mv>");
        tracker.ObserveRoomPeople(["Agron", "Ranny kapłan"], ["Ranny kapłan"], now);
        tracker.ObserveRoomPeople(["Agron"], [], now.AddMilliseconds(100));
        tracker.ProcessLine("Zdobyles 200 punktow doswiadczenia.", now.AddMilliseconds(200));

        var kill = Assert.Single(tracker.ProcessLine("<50hp 800 90mv>", now.AddMilliseconds(300)));
        Assert.Equal(ExperienceChangeKind.KillReward, kill.Kind);
        Assert.Equal("Ranny Kapłan", kill.EnemyName);
    }

    [Fact]
    public void AttributesRewardWhenOpponentDisappearsAfterReward()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00+02:00");
        var tracker = new ExperienceTracker();
        tracker.ProcessLine("<50hp 1000 90mv>");
        tracker.ObserveRoomPeople(["Agron", "Ranny kapłan"], ["Ranny kapłan"], now);
        tracker.ProcessLine("Zdobyles 200 punktow doswiadczenia.", now.AddMilliseconds(100));
        tracker.ObserveRoomPeople(["Agron"], [], now.AddMilliseconds(200));

        var kill = Assert.Single(tracker.ProcessLine("<50hp 800 90mv>", now.AddMilliseconds(300)));
        Assert.Equal("Ranny Kapłan", kill.EnemyName);
    }

    [Fact]
    public void DoesNotTreatOpponentWhoRemainsVisibleAsKilled()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00+02:00");
        var tracker = new ExperienceTracker();
        tracker.ProcessLine("<50hp 1000 90mv>");
        tracker.ObserveRoomPeople(["Agron", "Kapłan"], ["Kapłan"], now);
        tracker.ObserveRoomPeople(["Agron", "Kapłan"], [], now.AddMilliseconds(100));
        tracker.ProcessLine("Zdobyles 200 punktow doswiadczenia.", now.AddMilliseconds(200));

        var kill = Assert.Single(tracker.ProcessLine("<50hp 800 90mv>", now.AddMilliseconds(300)));
        Assert.Null(kill.EnemyName);
    }

    [Fact]
    public void DoesNotGuessWhenMultipleCombatOpponentsDisappearTogether()
    {
        var now = DateTimeOffset.Parse("2026-09-01T12:00:00+02:00");
        var tracker = new ExperienceTracker();
        tracker.ProcessLine("<50hp 1000 90mv>");
        tracker.ObserveRoomPeople(
            ["Agron", "Pierwszy kapłan", "Drugi kapłan"],
            ["Pierwszy kapłan", "Drugi kapłan"],
            now);
        tracker.ObserveRoomPeople(["Agron"], [], now.AddMilliseconds(100));
        tracker.ProcessLine("Zdobyles 200 punktow doswiadczenia.", now.AddMilliseconds(200));

        var kill = Assert.Single(tracker.ProcessLine("<50hp 800 90mv>", now.AddMilliseconds(300)));
        Assert.Null(kill.EnemyName);
    }
}

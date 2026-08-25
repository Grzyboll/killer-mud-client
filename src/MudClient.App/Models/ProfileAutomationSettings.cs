namespace MudClient.App.Models;

/// <summary>
/// Per-character automation/preference toggles — the "functions" registered in
/// <c>MainWindowViewModel.CommandToggles</c> (autostand, autoscan, ...) plus their directly
/// associated config (mob name lists, weapon name, follow-up commands). Stored on
/// <see cref="ProfileData.Automation"/> so multiboxed characters don't fight over one shared
/// settings.json. Null on <see cref="ProfileData.Automation"/> means the profile predates this
/// (see <c>MainWindowViewModel.LoadLegacyAutomationSettingsSeed</c> for the one-time migration
/// that seeds it from the old shared file instead of silently resetting to these defaults).
/// </summary>
public sealed class ProfileAutomationSettings
{
    /// <summary>Wraps long MUD output lines to the terminal width.</summary>
    public bool OutputWordWrap { get; set; } = true;

    /// <summary>Shows the vertical HP and MV indicators beside the terminal.</summary>
    public bool ShowTerminalVitalsBars { get; set; } = true;

    /// <summary>Annotates recognized "you dealt damage" combat lines with their approximate
    /// numeric tier.</summary>
    public bool ShowNumericDamageEnabled { get; set; } = true;

    /// <summary>Annotates random magic-book item names with the spellcasting class they belong
    /// to.</summary>
    public bool AnnotateRandomBookClassEnabled { get; set; } = true;

    /// <summary>Annotates each row of the "skill" command's output with who can still train the
    /// player further in that skill.</summary>
    public bool AnnotateSkillTrainersEnabled { get; set; } = true;

    /// <summary>Annotates each still-missing entry of the "spell" command's output with which
    /// known spellbook-dropping mob(s) teach it.</summary>
    public bool AnnotateSpellSourcesEnabled { get; set; } = true;

    /// <summary>Annotates each word-scale stat line of the "score" command's output with its
    /// approximate numeric range.</summary>
    public bool AnnotateScoreEnabled { get; set; } = true;

    /// <summary>Clears the terminal command input after a manually submitted command.</summary>
    public bool ClearCommandInputAfterSend { get; set; }

    /// <summary>Automatically sends <see cref="AutoAssistCommandTemplate"/> when a group member
    /// fights in the current room.</summary>
    public bool AutoAssistEnabled { get; set; }

    /// <summary>Exact GMCP enemy names for which autoassist must not act.</summary>
    public List<string> AutoAssistExcludedMobNames { get; set; } = [];

    /// <summary>The command autoassist sends to enter combat — "as" (bare assist, the default) or
    /// any other opener, e.g. "charge {cel}", "backstab {cel}", "kick {cel}". "{cel}" is replaced
    /// with the fighting group member's enemy name; commands with no "{cel}" are sent as-is, the
    /// same way bare "as" always was.</summary>
    public string AutoAssistCommandTemplate { get; set; } = "as";

    /// <summary>Commands sent immediately after an automatic autoassist command.</summary>
    public string AutoAssistFollowUpCommands { get; set; } = string.Empty;

    /// <summary>Executes strictly formatted orders issued by current GMCP group members.</summary>
    public bool GroupOrdersEnabled { get; set; }

    /// <summary>Executes as a literal command anything <see cref="RemoteControlCharacterName"/>
    /// says with a "!" prefix (e.g. "!stand" -&gt; sends "stand") — a workaround for MUDs that
    /// restrict the "order" command to the formal group leader (see
    /// <see cref="Core.Automation.RemoteCommandPolicy"/>). Requires no group leadership, only
    /// that the two characters can see each other's "say".</summary>
    public bool RemoteControlEnabled { get; set; }

    /// <summary>The one character whose "!"-prefixed say lines get executed — see
    /// <see cref="RemoteControlEnabled"/>. Case-insensitive, empty disables the feature
    /// regardless of the enabled flag.</summary>
    public string RemoteControlCharacterName { get; set; } = string.Empty;

    /// <summary>Sends <see cref="AutoRecastOnLeaderSnapCommandsText"/> when the current GMCP
    /// group's leader sends the "snaps fingers" emote line.</summary>
    public bool AutoRecastOnLeaderSnapEnabled { get; set; }

    /// <summary>Commands sent when the group leader's snap-fingers emote fires.</summary>
    public string AutoRecastOnLeaderSnapCommandsText { get; set; } = "/recast";

    /// <summary>Uses stable group-order numbers instead of member names on map markers.</summary>
    public bool ShowGroupMembersAsNumbers { get; set; }

    /// <summary>Enables creator-only map actions backed by server-side lord commands.</summary>
    public bool LordModeEnabled { get; set; }

    /// <summary>Shows each effect's count/duration and description alongside its name in the
    /// Effects panel, instead of just the name.</summary>
    public bool ShowExtendedEffects { get; set; }

    /// <summary>Double-clicking a room on the map immediately starts walking there, instead of
    /// only previewing the route until confirmed.</summary>
    public bool AutoWalkOnMapDoubleClick { get; set; } = true;

    /// <summary>Enables autowalk's built-in low-movement recovery (cast refresh if memorized,
    /// otherwise rest then stand back up) before each step.</summary>
    public bool AutowalkMovementRecoveryEnabled { get; set; } = true;

    /// <summary>Sends "rest" automatically as soon as autowalk reaches its destination.</summary>
    public bool AutowalkRestOnArrivalEnabled { get; set; } = true;

    /// <summary>When standing up while leading a group, orders every other group member to stand
    /// too ("order &lt;name&gt; stand").</summary>
    public bool AutoStandOrderEnabled { get; set; }

    /// <summary>Mirrors <see cref="AutoStandOrderEnabled"/> for resting.</summary>
    public bool AutoRestOrderEnabled { get; set; }

    /// <summary>For a non-leader group member: automatically walks to the leader's room whenever
    /// GMCP reports it differs from this character's.</summary>
    public bool AutoFollowLeaderEnabled { get; set; }

    /// <summary>For a non-leader group member: mirrors the GMCP-reported leader's stand/sit/rest
    /// state (sends "stand"/"sit"/"rest" to match) — useful when the leader isn't this client
    /// (e.g. a real person on another account) and so never sends an explicit "order ... stand".</summary>
    public bool AutoMirrorLeaderPositionEnabled { get; set; }

    /// <summary>Orders a group member to cast refresh on themselves as soon as GMCP reports their
    /// movement at the worst tier ("zamęczony").</summary>
    public bool AutoGroupRefreshOnExhaustedEnabled { get; set; }

    /// <summary>Orders every NPC in the current GMCP group (a summoned/charmed pet) to assist as
    /// soon as the local character's own position becomes "fighting".</summary>
    public bool AutoAssistNpcEnabled { get; set; }

    /// <summary>Sends "stand" as soon as the local character's GMCP position becomes "lying"
    /// (knocked down).</summary>
    public bool AutoStandOnLyingEnabled { get; set; }

    /// <summary>Sends "get"/"wield" for <see cref="AutowieldWeaponName"/> as soon as a disarm
    /// message is seen in the MUD text.</summary>
    public bool AutowieldEnabled { get; set; }

    /// <summary>Weapon name used by <see cref="AutowieldEnabled"/> for its get/wield commands.</summary>
    public string AutowieldWeaponName { get; set; } = string.Empty;

    /// <summary>Sends "scan" every time GMCP reports the character entering a new room.</summary>
    public bool AutoScanOnRoomEnterEnabled { get; set; }

    /// <summary>Sends "kill &lt;name&gt;" for every name in <see cref="AutoKillMobNames"/> every
    /// time GMCP reports the character entering a new room.</summary>
    public bool AutoKillOnRoomEnterEnabled { get; set; }

    /// <summary>Mob names "kill"ed on sight when <see cref="AutoKillOnRoomEnterEnabled"/> is on.</summary>
    public List<string> AutoKillMobNames { get; set; } = [];
}

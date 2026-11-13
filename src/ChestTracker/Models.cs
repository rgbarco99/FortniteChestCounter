using System.Text.Json;

namespace ChestTracker;

// ---------------------------------------------------------------------------
// Raw facts extracted from one replay. This is what gets cached on disk, so
// changing the chest classification or the Epic id never needs a re-parse.
// ---------------------------------------------------------------------------

public sealed class MatchRecord
{
    public string File { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }
    public int ParserVersion { get; set; }

    /// <summary>Local time of the PC when the match started.</summary>
    public DateTime StartedAt { get; set; }
    public double DurationSec { get; set; }
    public string? Build { get; set; }
    public string? Playlist { get; set; }
    public string? SessionId { get; set; }

    /// <summary>The replay header says the game was still recording when the file was read.</summary>
    public bool IsRecording { get; set; }

    /// <summary>Match in progress whose contents the game has not made readable yet: only the start time is known.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Epic id of the player who recorded the replay, when the replay says so.</summary>
    public string? DetectedOwnerId { get; set; }

    public MatchStats? Stats { get; set; }
    public int? Position { get; set; }
    public int? TotalPlayers { get; set; }

    public List<PlayerInfo> Players { get; set; } = new();
    public List<Opening> Openings { get; set; } = new();
    public List<Elimination> Eliminations { get; set; } = new();

        public string PlayerId { get; set; } = "";
}

public sealed class MatchStats
{
    public int Eliminations { get; set; }
    public int Assists { get; set; }
    /// <summary>Null when the game reports no valid value (for example, no shots fired).</summary>
    public double? Accuracy { get; set; }
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
    public int DamageToStructures { get; set; }
    public int Revives { get; set; }
    public int MaterialsGathered { get; set; }
    public double DistanceKm { get; set; }
}

public sealed class PlayerInfo
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int? Team { get; set; }
    public bool IsBot { get; set; }
    public int? Kills { get; set; }
    public string? Platform { get; set; }
    public int? Level { get; set; }
    public bool ThankedBusDriver { get; set; }
    public int? Reboots { get; set; }
}

/// <summary>One container (chest, ammo box, ...) opened by a known player.</summary>
public sealed class Opening
{
    public double Time { get; set; }
    public string ContainerClass { get; set; } = "";
    public string PlayerId { get; set; } = "";

    /// <summary>"search" (interact key), "destroy" (broken open) or "collapse" (broken without a direct hit).</summary>
    public string Method { get; set; } = "search";
}

public sealed class Elimination
{
    public double Time { get; set; }
    public string Killer { get; set; } = "";
    public string Victim { get; set; } = "";
    public bool IsKnock { get; set; }
    public double? DistanceM { get; set; }
}

// ---------------------------------------------------------------------------
// Settings (settings.json)
// ---------------------------------------------------------------------------

public sealed class TrackerSettings
{
    public string DemosPath { get; set; } = "";
    public string EpicId { get; set; } = "";

    /// <summary>Container class name -> category id, set by the user from the interface.</summary>
    public Dictionary<string, string> ClassOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replay files the user removed from the statistics.</summary>
    public HashSet<string> ExcludedFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replay files that failed and the user chose not to try again.</summary>
    public HashSet<string> IgnoredFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fortnite's log file chosen by the user; empty means next to the replay folder.</summary>
    public string LogPath { get; set; } = "";

    /// <summary>Dashboard layout as saved by the interface; the server never looks inside.</summary>
    public JsonElement? Layout { get; set; }
}

// ---------------------------------------------------------------------------
// What the interface receives (GET /api/state)
// ---------------------------------------------------------------------------

public sealed class StateResponse
{
    public long Revision { get; set; }
    public ScanStatus Status { get; set; } = new();
    public SettingsView Settings { get; set; } = new();
    public List<CategoryInfo> Categories { get; set; } = new();
    public List<ClassInfo> Classes { get; set; } = new();
    public List<MatchSummary> Matches { get; set; } = new();
    public JsonElement? Layout { get; set; }

    /// <summary>Everything the treasure tab keeps (rewards, marks, images); opaque to the server.</summary>
    public JsonElement? Treasure { get; set; }

    /// <summary>The match being played right now, read from the replay while the game records it. Null between matches.</summary>
    public LiveView? Live { get; set; }

    /// <summary>Where the player is right now according to the game's own log.</summary>
    public GameStatus Game { get; set; } = new();

    /// <summary>Version running now and whether a newer one is published.</summary>
    public UpdateInfo Update { get; set; } = new();
}

public sealed class LiveView
{
    public MatchSummary Match { get; set; } = new();

    /// <summary>When the game last wrote the file: the match had got as far as Match.DurationSec at that moment.</summary>
    public DateTime WrittenUtc { get; set; }

    /// <summary>True while the game keeps the contents unreadable: there is a clock but no counts until the match ends.</summary>
    public bool Locked { get; set; }
}

public sealed class SettingsView
{
    public string DemosPath { get; set; } = "";
    public string DefaultDemosPath { get; set; } = "";
    public string EpicId { get; set; } = "";
    public string DataFolder { get; set; } = "";

    /// <summary>Log file chosen by the user (empty: automatic) and the one that would be used automatically.</summary>
    public string LogPath { get; set; } = "";
    public string DefaultLogPath { get; set; } = "";

    public List<string> IgnoredFiles { get; set; } = new();
}

public sealed class ScanStatus
{
    public bool DemosFolderExists { get; set; }
    public int ReplayFilesFound { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public string? ParsingFile { get; set; }

    /// <summary>Replays waiting to be read in the current pass.</summary>
    public int PendingCount { get; set; }

    /// <summary>How many replays the current pass set out to read; with PendingCount it gives the progress.</summary>
    public int BatchTotal { get; set; }

    /// <summary>Replay still being written by the game (a match in progress).</summary>
    public string? LiveFile { get; set; }
    public List<ScanError> Errors { get; set; } = new();
}

public sealed class ScanError
{
    public string File { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>How many times reading it has failed since the program started.</summary>
    public int Attempts { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
}

public sealed class ReplayFilesRequest
{
    public List<string> Files { get; set; } = new();
}

public sealed class CategoryInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class ClassInfo
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string DefaultCategory { get; set; } = "";
    public int OpenedByYou { get; set; }
    public int OpenedByAnyone { get; set; }
}

public sealed class MatchSummary
{
    public string File { get; set; } = "";
    public bool FileStillExists { get; set; }

    /// <summary>Counted with an older version of the reader and the replay is gone, so it cannot be recounted.</summary>
    public bool Outdated { get; set; }
    public bool Excluded { get; set; }
    public DateTime StartedAt { get; set; }
    public double DurationSec { get; set; }
    public double AliveSec { get; set; }
    public string? Build { get; set; }
    public string? Playlist { get; set; }
    public string ModeLabel { get; set; } = "";

    public string? OwnerId { get; set; }
    public string? OwnerName { get; set; }

    public int? Position { get; set; }
    public int? TotalPlayers { get; set; }
    public int Kills { get; set; }
    public MatchStats? Stats { get; set; }

    /// <summary>Category id -> containers you opened.</summary>
    public Dictionary<string, int> Counts { get; set; } = new();
    public int ChestsTotal { get; set; }
    public int ChestsBroken { get; set; }
    public double ChestsPerMin { get; set; }
    public double? FirstChestSec { get; set; }

    public List<TimelineOpening> Openings { get; set; } = new();
    public List<TimelineKill> KillEvents { get; set; } = new();
    public List<TimelineDeath> Deaths { get; set; } = new();
    public List<SquadMember> Squad { get; set; } = new();
}

public sealed class TimelineOpening
{
    public double T { get; set; }
    public string Category { get; set; } = "";
    public string ContainerClass { get; set; } = "";
    public string Method { get; set; } = "search";
}

public sealed class TimelineKill
{
    public double T { get; set; }
    public bool IsKnock { get; set; }
    public double? DistanceM { get; set; }
    public string? VictimName { get; set; }
}

public sealed class TimelineDeath
{
    public double T { get; set; }
    public bool IsKnock { get; set; }
}

public sealed class SquadMember
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool IsYou { get; set; }
    public int? Kills { get; set; }

    /// <summary>Enemies this player knocked down, and times this player was knocked down.</summary>
    public int Knocks { get; set; }
    public int TimesKnocked { get; set; }

    /// <summary>Second of the match when this player was eliminated for good; null if they made it to the end of the replay.</summary>
    public double? EliminatedAt { get; set; }
    public string? EliminatedBy { get; set; }

    public string? Platform { get; set; }
    public int? Level { get; set; }
    public bool ThankedBusDriver { get; set; }
    public int? Reboots { get; set; }

    /// <summary>The game only writes these for the player who recorded the replay.</summary>
    public int? DamageDealt { get; set; }
    public int? DamageTaken { get; set; }
    public int? Assists { get; set; }
    public int? Revives { get; set; }
}

// Request bodies

public sealed class SettingsRequest
{
    public string? DemosPath { get; set; }
    public string? EpicId { get; set; }
    public string? LogPath { get; set; }
    public Dictionary<string, string>? ClassOverrides { get; set; }
}

public sealed class ExcludeRequest
{
    public string File { get; set; } = "";
    public bool Excluded { get; set; }
}

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

    /// <summary>
    /// Where the container stood, in metres of the game's own world (the replay counts in
    /// centimetres; so do the season's published points of interest, which divide by 100 into
    /// these same coordinates). Null when nothing in the replay placed it.
    /// </summary>
    public double? X { get; set; }
    public double? Y { get; set; }
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

    /// <summary>
    /// Shared folders of the squads this player is in, up to <see cref="MAX_SQUADS"/>. Empty is
    /// how every copy starts: nothing leaves this PC until somebody joins a squad on purpose.
    /// </summary>
    public List<string> SquadPaths { get; set; } = new();

    /// <summary>
    /// The single folder of the versions before squads could be several. Read once on start and
    /// folded into <see cref="SquadPaths"/>, so an update never drops somebody out of their squad.
    /// </summary>
    public string SquadPath { get; set; } = "";

    /// <summary>
    /// Four folders syncing at once is already a real cost in disk and in writes, and nobody
    /// plays with more than four groups of people often enough to be worth it.
    /// </summary>
    public const int MAX_SQUADS = 4;

    /// <summary>The tray icon has already said once where the program lives.</summary>
    public bool TrayNoticeShown { get; set; }

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

    /// <summary>What the squad has shared, added up over the matches you played together.</summary>
    public List<SharedPlayer> Shared { get; set; } = new();

    /// <summary>
    /// This player is in at least one squad. It is what tells an empty comparison that is still
    /// loading from one that is never coming, which the page cannot work out on its own.
    /// </summary>
    public bool InSquad { get; set; }

    /// <summary>How the reading of what the squad shares is going right now.</summary>
    public SharingStatus Sharing { get; set; } = new();
}

/// <summary>
/// What the program is doing about the squad's figures at this moment. It exists because waiting
/// is the normal state for a minute or two after a match: their card has to be written on their
/// PC, carried by the cloud and read here, and only the first and last steps are ours. A card
/// that says nothing more than "waiting" cannot be told from one that has given up, so the page
/// says how many times it has looked and how often it looks.
/// </summary>
public sealed class SharingStatus
{
    /// <summary>The cloud the shared folders live in, when it is one this program knows.</summary>
    public string? CloudName { get; set; }

    /// <summary>Whether that cloud's program is running. Null when there is no way to tell.</summary>
    public bool? CloudRunning { get; set; }

    /// <summary>Everybody who shares with you, and when each of them last wrote their file.</summary>
    public List<SharedPublisher> Publishers { get; set; } = new();

    /// <summary>Times the shared folders have been read since the program started.</summary>
    public int Reads { get; set; }

    /// <summary>Since when a squadmate's figures for your last match have been missing. Null when nothing is pending.</summary>
    public DateTime? WaitingSinceUtc { get; set; }

    /// <summary>Reads made since that wait began.</summary>
    public int WaitReads { get; set; }

    /// <summary>Seconds between one look and the next right now: two while waiting, eight otherwise.</summary>
    public double EverySec { get; set; }
}

/// <summary>One squadmate, totalled over the matches of yours they also played.</summary>
public sealed class SharedPlayer
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }

    /// <summary>Matches of yours they shared a card for.</summary>
    public int Matches { get; set; }

    /// <summary>Chests they opened in those matches: the figure your own replay could never see.</summary>
    public int Chests { get; set; }
    public int Kills { get; set; }

    /// <summary>Your own chests in those same matches, so the two can be compared fairly.</summary>
    public int YourChests { get; set; }
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

/// <summary>A named place of the season's island, in the same metres the openings use.</summary>
public sealed class MapPlace
{
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

/// <summary>The season's map as the page gets it.</summary>
public sealed class MapView
{
    public bool HasImage { get; set; }
    public List<MapPlace> Places { get; set; } = new();
    public DateTime? FetchedUtc { get; set; }
}

/// <summary>The item shop as the page draws it: when it changes, and what is in it.</summary>
public sealed class ShopView
{
    public string Language { get; set; } = "";
    public DateTime DateUtc { get; set; }
    public DateTime NextResetUtc { get; set; }
    public DateTime FetchedUtc { get; set; }
    public string? VbucksIcon { get; set; }
    /// <summary>True when this is the last shop that could be brought, not today's.</summary>
    public bool Stale { get; set; }
    /// <summary>True when there was nothing saved and nothing could be brought either.</summary>
    public bool Failed { get; set; }
    public List<ShopItem> Entries { get; set; } = new();
}

/// <summary>One tile of the shop: one thing for sale, which may hold several items.</summary>
public sealed class ShopItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Image { get; set; }
    public int Price { get; set; }
    /// <summary>What it costs without the discount; the same as the price when there is none.</summary>
    public int Was { get; set; }
    public string? Section { get; set; }
    public int SectionRank { get; set; }
    public int SectionIndex { get; set; }
    public int Sort { get; set; }
    public string? Kind { get; set; }
    public string? Rarity { get; set; }
    public string? RarityKey { get; set; }
    public string? Series { get; set; }
    public string? Banner { get; set; }
    public string? BannerKey { get; set; }
    public string? Tag { get; set; }
    public bool IsBundle { get; set; }
    public bool IsTrack { get; set; }
    /// <summary>How many things come in it, which is more than one in a bundle.</summary>
    public int Pieces { get; set; }
    public DateTime InUtc { get; set; }
    public DateTime OutUtc { get; set; }
    public bool IsNew { get; set; }
    public bool Leaving { get; set; }
}

/// <summary>One squadmate's file, as the reader last saw it.</summary>
public sealed class SharedPublisher
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }

    /// <summary>When they last published, by their own clock, as their file says.</summary>
    public DateTime PublishedUtc { get; set; }
}

/// <summary>One of the copies the program keeps of the saved matches.</summary>
public sealed class BackupInfo
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public DateTime WrittenUtc { get; set; }
}

/// <summary>What came of restoring a backup.</summary>
public sealed class ImportOutcome
{
    public int Added { get; set; }

    /// <summary>Matches of the file that were already here, so they were left alone.</summary>
    public int Skipped { get; set; }

    public int Total { get; set; }
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

    /// <summary>Fortnite's own id for the match, the same for everybody in it: the key that
    /// joins your figures to the ones your squad shared.</summary>
    public string? SessionId { get; set; }

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

    /// <summary>Where it stood, in metres, when the replay placed it. Null in matches read before that.</summary>
    public double? X { get; set; }
    public double? Y { get; set; }
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

    /// <summary>
    /// This player shares with you and their card for this match has not arrived yet. It is the
    /// difference between a figure that is coming and one that never will, which is the whole
    /// difference between "waiting" and "no data".
    /// </summary>
    public bool Pending { get; set; }

    /// <summary>
    /// Chests they opened in this match, from the card they shared. Null means nobody published
    /// it: either they are not in a squad folder, or their copy has not synced yet. A replay
    /// cannot answer this, which is the whole reason squads exist.
    /// </summary>
    public int? SharedChests { get; set; }

    /// <summary>They publish into one of the folders this player is in.</summary>
    public bool Sharing { get; set; }

    /// <summary>
    /// Chests of theirs that your own replay happened to record, because they were opened within
    /// its range (about 300 m of you). A floor, never a total: everything they opened on the far
    /// side of the map is missing. Only worth showing when they share nothing.
    /// </summary>
    public int? NearbyChests { get; set; }
}

// ---------------------------------------------------------------------------
// The squad group: a folder shared through OneDrive, Drive or Dropbox.
// SquadMember above is the squad of one match, read from your own replay;
// these are the players who share the folder, which is a different thing.
// ---------------------------------------------------------------------------

/// <summary>A cloud folder of this PC, offered as somewhere to put a group.</summary>
public sealed class CloudRoot
{
    public string Provider { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>What one player writes into the shared folder, in their own file.</summary>
public sealed class SquadFile
{
    public string PlayerId { get; set; } = "";
    public string? Name { get; set; }
    public DateTime UpdatedUtc { get; set; }

    /// <summary>One card per match, oldest first.</summary>
    public List<SquadCard> Cards { get; set; } = new();
}

/// <summary>
/// One match as the rest of the squad sees it: about 250 bytes, and the whole of what leaves
/// this PC. The settings list these fields one by one, so sharing can be read before it is
/// agreed to; anything added here has to be added there too.
///
/// SessionId is the key that joins the four files: Fortnite writes the same one for everybody
/// in the match.
/// </summary>
public sealed class SquadCard
{
    public string SessionId { get; set; } = "";
    public DateTime StartedAt { get; set; }

    /// <summary>Seconds this player lasted, and how long the replay ran.</summary>
    public double AliveSec { get; set; }
    public double DurationSec { get; set; }

    /// <summary>Chests opened, by the three categories the dashboard counts.</summary>
    public int Chests { get; set; }
    public int Special { get; set; }
    public int Spirit { get; set; }

    public int Kills { get; set; }
    public int? Position { get; set; }
    public double? FirstChestSec { get; set; }
}

/// <summary>A player with a file in the shared folder.</summary>
public sealed class SquadMate
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public bool IsYou { get; set; }

    /// <summary>They are in your own replays as a regular squadmate: that is what makes a group recognisable.</summary>
    public bool PlayedWithYou { get; set; }

    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Matches they share, for a member of the group.</summary>
    public int Matches { get; set; }

    /// <summary>Recent matches played together, for one of your own squadmates.</summary>
    public int Together { get; set; }
}

/// <summary>A group found sitting in this PC's cloud folders.</summary>
public sealed class GroupFound
{
    public string Path { get; set; } = "";
    public string Provider { get; set; } = "";
    public List<SquadMate> Members { get; set; } = new();

    /// <summary>How many of its members you have actually played with.</summary>
    public int KnownMates { get; set; }
}

/// <summary>One squad this player is in: its folder, how it is doing and who is in it.</summary>
public sealed class SquadGroup
{
    public string Path { get; set; } = "";

    /// <summary>Shown instead of the whole path, which is long and means nothing to the reader.</summary>
    public string Name { get; set; } = "";

    /// <summary>"ok", "missing", "readonly" or "error".</summary>
    public string Status { get; set; } = "ok";

    public List<SquadMate> Members { get; set; } = new();
}

/// <summary>Everything the squad section of the settings needs (GET /api/squad).</summary>
public sealed class SquadView
{
    /// <summary>The squads in use, in the order they were joined. Empty means none.</summary>
    public List<SquadGroup> Squads { get; set; } = new();

    /// <summary>"ok", or "noepic" when the Epic id is not known yet and nothing can be done.</summary>
    public string Status { get; set; } = "ok";

    public int Max { get; set; } = TrackerSettings.MAX_SQUADS;

    /// <summary>Cloud folders where a squad could be created.</summary>
    public List<CloudRoot> Roots { get; set; } = new();

    /// <summary>Squads sitting in the cloud folders that this player has not joined, best first.</summary>
    public List<GroupFound> Found { get; set; } = new();

    /// <summary>The squadmates of your recent matches, whether or not they are in a squad here.</summary>
    public List<SquadMate> RecentMates { get; set; } = new();

    public string EpicId { get; set; } = "";
    public string? YourName { get; set; }
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

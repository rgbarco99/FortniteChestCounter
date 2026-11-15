namespace ChestTracker;

/// <summary>
/// The other half of sharing: reading what the rest of the squad published. Their cards live in
/// the same folders this player writes into, one file per player, and they are joined to your own
/// matches by SessionId, which Fortnite writes the same for everybody in the match.
///
/// This never touches the saved matches. What the squad shares is read-only data from other
/// people's PCs: it is shown beside your own figures and never mixed into them.
/// </summary>
public sealed class SquadReader : BackgroundService
{
    /// <summary>How often to look when nothing has just happened.</summary>
    private static readonly TimeSpan tsIdleTick = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How often to look just after a match of yours ended. That is the one moment the squad's
    /// figures are being waited for, and the one moment they are arriving: the cloud delivers
    /// them when it delivers them, and checking often is the only way to show them as they land.
    /// </summary>
    private static readonly TimeSpan tsEagerTick = TimeSpan.FromSeconds(2);

    /// <summary>How long after a match ends the eager pace lasts.</summary>
    private static readonly TimeSpan tsEagerFor = TimeSpan.FromMinutes(4);

    private readonly Store _objStore;

    /// <summary>Session id -> player id -> their card for that match.</summary>
    private Dictionary<string, Dictionary<string, SquadCard>> _dicBySession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Player id -> the name they publish under.</summary>
    private Dictionary<string, string?> _dicNames = new(StringComparer.OrdinalIgnoreCase);

    private bool _bComplained;

    public SquadReader(Store objStore) => _objStore = objStore;

    /// <summary>Bumped on every read that changed something, so the page knows to ask again.</summary>
    public long Revision { get; private set; }

    /// <summary>What each squadmate opened in one match, by player id. Empty when nobody shared it.</summary>
    public IReadOnlyDictionary<string, SquadCard> ForSession(string? strSessionId) =>
        strSessionId != null && _dicBySession.TryGetValue(strSessionId, out var dicCards)
            ? dicCards
            : new Dictionary<string, SquadCard>();

    /// <summary>True once anybody other than this player has shared anything at all.</summary>
    public bool HasData => _dicBySession.Count > 0;

    public IReadOnlyDictionary<string, string?> Names => _dicNames;

    protected override async Task ExecuteAsync(CancellationToken objToken)
    {
        while (!objToken.IsCancellationRequested)
        {
            try
            {
                Reload();
            }
            catch (Exception objError)
            {
                if (!_bComplained)
                {
                    _objStore.Log($"No se pudo leer lo que comparte el squad: {objError.Message}");
                    _bComplained = true;
                }
            }

            try { await Task.Delay(JustPlayed() ? tsEagerTick : tsIdleTick, objToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>A match of yours ended a moment ago, so their cards are on their way right now.</summary>
    private bool JustPlayed()
    {
        try
        {
            var objLast = _objStore.GetMatches().MaxBy(objMatch => objMatch.StartedAt);
            if (objLast == null)
            {
                return false;
            }

            var dtEnded = objLast.StartedAt.AddSeconds(objLast.DurationSec);
            return DateTime.Now - dtEnded < tsEagerFor;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Reload()
    {
        var objSettings = _objStore.Settings;
        var strEpicId = objSettings.EpicId;

        if (objSettings.SquadPaths.Count == 0)
        {
            if (_dicBySession.Count > 0)
            {
                _dicBySession = new Dictionary<string, Dictionary<string, SquadCard>>(StringComparer.OrdinalIgnoreCase);
                _dicNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                Revision++;
            }

            return;
        }

        var dicBySession = new Dictionary<string, Dictionary<string, SquadCard>>(StringComparer.OrdinalIgnoreCase);
        var dicNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var intCards = 0;

        foreach (var strFolder in objSettings.SquadPaths)
        {
            if (!Directory.Exists(strFolder))
            {
                continue;       // Syncing or offline: the other folders still have something to say
            }

            foreach (var objFile in Squad.ReadFiles(strFolder))
            {
                // Your own file is what you published; your figures come from your own matches
                if (string.Equals(objFile.PlayerId, strEpicId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                dicNames[objFile.PlayerId] = objFile.Name;

                foreach (var objCard in objFile.Cards)
                {
                    if (string.IsNullOrEmpty(objCard.SessionId))
                    {
                        continue;
                    }

                    if (!dicBySession.TryGetValue(objCard.SessionId, out var dicPlayers))
                    {
                        dicPlayers = new Dictionary<string, SquadCard>(StringComparer.OrdinalIgnoreCase);
                        dicBySession[objCard.SessionId] = dicPlayers;
                    }

                    // The same player can be in two shared folders: the newest card wins
                    if (!dicPlayers.TryGetValue(objFile.PlayerId, out var objHave) || objCard.StartedAt >= objHave.StartedAt)
                    {
                        dicPlayers[objFile.PlayerId] = objCard;
                        intCards++;
                    }
                }
            }
        }

        // A cheap description of what was read, so an unchanged folder costs nothing downstream
        var strNow = $"{dicBySession.Count}|{dicNames.Count}|{intCards}";
        if (strNow != _strRead)
        {
            _strRead = strNow;
            Revision++;
            _objStore.Log($"Leídas {intCards} partidas compartidas por {dicNames.Count} compañeros");
        }

        _dicBySession = dicBySession;
        _dicNames = dicNames;
        _bComplained = false;
    }

    private string _strRead = "";
}

using System.Text;

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
    /// <summary>How often to look when nobody's figures are missing.</summary>
    private static readonly TimeSpan tsIdleTick = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How often to look while a squadmate's figures for a match of yours are still missing.
    /// That is the one moment they are being waited for and the one moment they are arriving:
    /// the cloud delivers them when it delivers them, and looking often is the only way to put
    /// them on screen as they land.
    /// </summary>
    private static readonly TimeSpan tsEagerTick = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a wait is worth keeping up. It is the window in which the comparison of the
    /// previous match is still on screen, because a figure that lands after the card is gone
    /// lands for nothing. Measured from the match, not from when the program noticed it, so a
    /// wait that began while the replay was still being read keeps its whole quarter of an hour.
    /// </summary>
    private static readonly TimeSpan tsWaitFor = TimeSpan.FromMinutes(15);

    private readonly Store _objStore;

    /// <summary>Session id -> player id -> their card for that match.</summary>
    private Dictionary<string, Dictionary<string, SquadCard>> _dicBySession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Player id -> the name they publish under.</summary>
    private Dictionary<string, string?> _dicNames = new(StringComparer.OrdinalIgnoreCase);

    private bool _bComplained;

    /// <summary>Files already reported as still in the cloud, so it is said once and not every pass.</summary>
    private readonly HashSet<string> _setSaidPlaceholder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The match whose missing figures are being waited for right now.</summary>
    private string? _strWaitSession;

    public SquadReader(Store objStore) => _objStore = objStore;

    /// <summary>Bumped on every read that changed something, so the page knows to ask again.</summary>
    public long Revision { get; private set; }

    /// <summary>Times the shared folders have been read since the program started.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// Since when a squadmate's figures for your last match have been missing, in UTC. Null when
    /// nothing is pending. It is what lets the page say that it is still trying, which a spinner
    /// on its own cannot tell apart from one that has given up.
    /// </summary>
    public DateTime? WaitingSinceUtc { get; private set; }

    /// <summary>Reads made since that wait began: the honest answer to whether it has even tried.</summary>
    public int WaitReads { get; private set; }

    /// <summary>Seconds between one look and the next right now.</summary>
    public double EverySec => (WaitingSinceUtc != null ? tsEagerTick : tsIdleTick).TotalSeconds;

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

            try { await Task.Delay(UpdateWait() ? tsEagerTick : tsIdleTick, objToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Whether somebody's figures are still missing, which is what sets the pace. Waiting is not
    /// "a match ended a moment ago": it is "this match of mine has a squadmate who shares and
    /// their card for it has not arrived". Anything else has either been read already or is
    /// never coming, and in both cases looking every two seconds buys nothing.
    /// </summary>
    private bool UpdateWait()
    {
        var strPending = PendingSession();
        if (strPending == null)
        {
            _strWaitSession = null;
            WaitingSinceUtc = null;
            WaitReads = 0;
            return false;
        }

        if (_strWaitSession != strPending)
        {
            _strWaitSession = strPending;
            WaitingSinceUtc = DateTime.UtcNow;
            WaitReads = 0;
        }

        WaitReads++;
        return true;
    }

    /// <summary>
    /// The session id of your last match when a squadmate who shares has not published their card
    /// for it yet; null when there is nothing to wait for. Only players with a file in the folder
    /// count: a stranger a fill put in your team is never going to publish anything, and waiting
    /// for them would mean looking every two seconds for a quarter of an hour for nothing.
    /// </summary>
    private string? PendingSession()
    {
        try
        {
            var objSettings = _objStore.Settings;
            if (objSettings.SquadPaths.Count == 0 || _dicNames.Count == 0)
            {
                return null;
            }

            var objLast = _objStore.GetMatches()
                .Where(objMatch => !string.IsNullOrEmpty(objMatch.SessionId))
                .MaxBy(objMatch => objMatch.StartedAt);
            if (objLast == null || DateTime.Now - objLast.StartedAt.AddSeconds(objLast.DurationSec) > tsWaitFor)
            {
                return null;
            }

            var strYou = objSettings.EpicId;
            var intTeam = objLast.Players.FirstOrDefault(objPlayer => string.Equals(objPlayer.Id, strYou, StringComparison.OrdinalIgnoreCase))?.Team;
            if (intTeam == null)
            {
                return null;
            }

            var dicCards = ForSession(objLast.SessionId);
            var bPending = objLast.Players.Any(objPlayer =>
                objPlayer.Team == intTeam
                && !objPlayer.IsBot
                && !string.Equals(objPlayer.Id, strYou, StringComparison.OrdinalIgnoreCase)
                && _dicNames.ContainsKey(objPlayer.Id)
                && !dicCards.ContainsKey(objPlayer.Id));

            return bPending ? objLast.SessionId : null;
        }
        catch (Exception)
        {
            return null;        // The pace is a detail: working it out must never stop the reading
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

        Reads++;

        var dicBySession = new Dictionary<string, Dictionary<string, SquadCard>>(StringComparer.OrdinalIgnoreCase);
        var dicNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var strFolder in objSettings.SquadPaths)
        {
            if (!Directory.Exists(strFolder))
            {
                continue;       // Syncing or offline: the other folders still have something to say
            }

            // Before reading: the squad's files have to be on this disk to be read at all. On
            // demand they are only names, their contents are fetched when something asks, and
            // what should be a glance at four small files becomes a download.
            foreach (var strCloudOnly in Squad.KeepOnThisDisk(strFolder))
            {
                if (_setSaidPlaceholder.Add(strCloudOnly))
                {
                    _objStore.Log($"{Path.GetFileName(strCloudOnly)} está en la nube y no en este disco; le he pedido a tu nube que lo baje y lo mantenga aquí");
                }
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
                    }
                }
            }
        }

        // What was read, described by its contents and not by how much of it there was. Three
        // counts were cheaper and missed the case that matters most: a card rewritten in place.
        // That is what a squadmate's figures being corrected looks like, and with the counts
        // unchanged the page was never told to ask again, so their chests stayed as first read.
        var strNow = Describe(dicBySession, dicNames);
        if (strNow != _strRead)
        {
            _strRead = strNow;
            Revision++;
            _objStore.Log($"Leídas {dicBySession.Sum(objPair => objPair.Value.Count)} partidas compartidas por {dicNames.Count} compañeros");
        }

        _dicBySession = dicBySession;
        _dicNames = dicNames;
        _bComplained = false;
    }

    private string _strRead = "";

    /// <summary>
    /// Everything read, in a fixed order, as one string to compare against the last pass. The
    /// figures are in it, so a card that changed without the count changing is still noticed;
    /// the names are too, so a squadmate who renames themselves reaches the page at once.
    /// </summary>
    private static string Describe(Dictionary<string, Dictionary<string, SquadCard>> dicBySession, Dictionary<string, string?> dicNames)
    {
        var objText = new StringBuilder();

        foreach (var (strSession, dicPlayers) in dicBySession.OrderBy(objPair => objPair.Key, StringComparer.OrdinalIgnoreCase))
        {
            objText.Append(strSession).Append(':');
            foreach (var (strPlayer, objCard) in dicPlayers.OrderBy(objPair => objPair.Key, StringComparer.OrdinalIgnoreCase))
            {
                objText.Append(strPlayer).Append('=')
                    .Append(objCard.Chests).Append('/')
                    .Append(objCard.Special).Append('/')
                    .Append(objCard.Spirit).Append('/')
                    .Append(objCard.Kills).Append('/')
                    .Append(objCard.Position).Append('/')
                    .Append(objCard.AliveSec).Append('/')
                    .Append(objCard.StartedAt.Ticks).Append(',');
            }

            objText.Append(';');
        }

        foreach (var (strPlayer, strName) in dicNames.OrderBy(objPair => objPair.Key, StringComparer.OrdinalIgnoreCase))
        {
            objText.Append(strPlayer).Append('~').Append(strName).Append(',');
        }

        return objText.ToString();
    }
}

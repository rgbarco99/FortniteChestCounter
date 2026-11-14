namespace ChestTracker;

/// <summary>
/// Keeps this player's file in the shared folder up to date: one card per match, rewritten
/// whole whenever the saved matches change.
///
/// It watches the store's revision instead of hanging off every save, which is what makes a
/// bulk re-read of two hundred replays cost one write instead of two hundred. Writing the list
/// complete every time, rather than appending, is what makes it self-repairing: a match read
/// late, a pass missed while the folder was offline and a file that came back damaged are all
/// put right by the next write, with no state to keep about what was sent before.
///
/// Nothing here runs until the player joins a group: an empty SquadPath means it does nothing.
/// </summary>
public sealed class SquadPublisher : BackgroundService
{
    private static readonly TimeSpan tsTick = TimeSpan.FromSeconds(5);

    private readonly Store _objStore;

    /// <summary>Revision already looked at, so an idle program does no work at all.</summary>
    private long _lngSeenRevision = -1;

    /// <summary>What was last written into each folder, to keep an unrelated change from rewriting it.</summary>
    private readonly Dictionary<string, string> _dicWritten = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A folder that is away (syncing, offline) is normal: it is said once, not every tick.</summary>
    private readonly HashSet<string> _setComplained = new(StringComparer.OrdinalIgnoreCase);

    public SquadPublisher(Store objStore) => _objStore = objStore;

    protected override async Task ExecuteAsync(CancellationToken objToken)
    {
        while (!objToken.IsCancellationRequested)
        {
            try
            {
                Publish();
            }
            catch (Exception objError)
            {
                // Sharing must never take the program down with it
                if (_setComplained.Add("*"))
                {
                    _objStore.Log($"No se pudo publicar en los escuadrones: {objError.Message}");
                }
            }

            try { await Task.Delay(tsTick, objToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void Publish()
    {
        var objSettings = _objStore.Settings;
        var strEpicId = objSettings.EpicId;
        var lstFolders = objSettings.SquadPaths;

        if (lstFolders.Count == 0 || string.IsNullOrEmpty(strEpicId))
        {
            return;
        }

        // Nothing has changed since the last look, and no folder is waiting for a first write:
        // the common case, and it costs one long read
        var lngRevision = _objStore.Revision;
        if (_lngSeenRevision == lngRevision && lstFolders.All(_dicWritten.ContainsKey))
        {
            return;
        }

        var lstCards = BuildCards(objSettings);

        // Same cards as last time: a settings or layout save also moves the revision
        var strSignature = Describe(lstCards);
        var bAllDone = true;

        foreach (var strFolder in lstFolders)
        {
            if (_dicWritten.TryGetValue(strFolder, out var strLast) && strLast == strSignature)
            {
                continue;
            }

            if (!Directory.Exists(strFolder))
            {
                // Still syncing, or the drive is not there: try again on the next tick, and
                // let the other squads be published meanwhile
                if (_setComplained.Add(strFolder))
                {
                    _objStore.Log($"La carpeta del escuadrón no está disponible ahora mismo: {strFolder}");
                }

                bAllDone = false;
                continue;
            }

            try
            {
                var objFile = Squad.ReadMine(strFolder, strEpicId) ?? new SquadFile();
                objFile.PlayerId = strEpicId;
                objFile.Name = GetYourName(objSettings) ?? objFile.Name;
                objFile.UpdatedUtc = DateTime.UtcNow;
                objFile.Cards = lstCards;
                Squad.WriteMine(strFolder, objFile);

                _objStore.Log($"Publicadas {lstCards.Count} partidas en {strFolder}");
                _dicWritten[strFolder] = strSignature;
                _setComplained.Remove(strFolder);
            }
            catch (Exception objError)
            {
                // One squad failing must not stop the others
                if (_setComplained.Add(strFolder))
                {
                    _objStore.Log($"No se pudo publicar en {strFolder}: {objError.Message}");
                }

                bAllDone = false;
            }
        }

        // Folders left behind keep their note, so leaving and joining again writes afresh
        foreach (var strGone in _dicWritten.Keys.Where(strKey => !lstFolders.Contains(strKey, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _dicWritten.Remove(strGone);
            _setComplained.Remove(strGone);
        }

        // Only call the revision done when every squad got it: otherwise the ones that failed
        // would never be tried again until the next match
        if (bAllDone)
        {
            _lngSeenRevision = lngRevision;
        }
    }

    /// <summary>
    /// One card per saved match, counted exactly as the dashboard counts it so the squad compares
    /// like with like. Matches the player took out of their statistics stay out of this too, and
    /// a match with no session id is left out because there would be nothing to join it by.
    /// </summary>
    private List<SquadCard> BuildCards(TrackerSettings objSettings)
    {
        var lstCards = new List<SquadCard>();

        foreach (var objRecord in _objStore.GetMatches())
        {
            if (string.IsNullOrEmpty(objRecord.SessionId) || objSettings.ExcludedFiles.Contains(objRecord.File))
            {
                continue;
            }

            try
            {
                var objSummary = Summarizer.Summarize(objRecord, objSettings, objSettings.DemosPath);
                lstCards.Add(new SquadCard
                {
                    SessionId = objRecord.SessionId,
                    StartedAt = objSummary.StartedAt,
                    AliveSec = Math.Round(objSummary.AliveSec, 1),
                    DurationSec = Math.Round(objSummary.DurationSec, 1),
                    Chests = objSummary.Counts.GetValueOrDefault(Summarizer.CAT_CHEST),
                    Special = objSummary.Counts.GetValueOrDefault(Summarizer.CAT_SPECIAL),
                    Spirit = objSummary.Counts.GetValueOrDefault(Summarizer.CAT_SPIRIT),
                    Kills = objSummary.Kills,
                    Position = objSummary.Position,
                    FirstChestSec = objSummary.FirstChestSec
                });
            }
            catch (Exception) { /* One match that cannot be summarised must not stop the rest */ }
        }

        return lstCards.OrderBy(objCard => objCard.StartedAt).ToList();
    }

    /// <summary>The nick the squad sees, taken from the newest match that names this player.</summary>
    private string? GetYourName(TrackerSettings objSettings)
    {
        foreach (var objRecord in _objStore.GetMatches().OrderByDescending(objItem => objItem.StartedAt))
        {
            var strName = objRecord.Players
                .FirstOrDefault(objPlayer => string.Equals(objPlayer.Id, objSettings.EpicId, StringComparison.OrdinalIgnoreCase))?.Name;
            if (!string.IsNullOrEmpty(strName))
            {
                return strName;
            }
        }

        return null;
    }

    /// <summary>Enough of the cards to tell a real change from a revision that moved for other reasons.</summary>
    private static string Describe(List<SquadCard> lstCards)
    {
        if (lstCards.Count == 0)
        {
            return "0";
        }

        var objLast = lstCards[^1];
        var intChests = lstCards.Sum(objCard => objCard.Chests + objCard.Special + objCard.Spirit);
        return $"{lstCards.Count}|{intChests}|{objLast.SessionId}|{objLast.StartedAt.Ticks}|{objLast.AliveSec}";
    }
}

namespace ChestTracker;

/// <summary>
/// Looks at the replay folder every few seconds and reads whatever is new or changed,
/// newest first. A replay still being written (match in progress) is left alone until
/// it stops growing. Each replay is read on its own, so one bad file never blocks the rest.
/// </summary>
public sealed class ReplayWatcher : BackgroundService
{
    private static readonly TimeSpan tsScanInterval = TimeSpan.FromSeconds(2);
    // A file that is not a match in progress is left alone for a moment in case it is still being copied
    private static readonly TimeSpan tsQuietTime = TimeSpan.FromSeconds(5);

    // The game writes a match in progress every 20 seconds: two missed writes mean it is over
    private static readonly TimeSpan tsLiveWindow = TimeSpan.FromSeconds(45);

    // When the header does not say "recording", one full write cycle without growth is enough
    private static readonly TimeSpan tsClosedWindow = TimeSpan.FromSeconds(25);

    // Fortnite writes the replay key when it closes the file, and that can land a minute or two
    // after the last write of the match (the end-of-match screen, the way back to the lobby). A
    // replay without its key is only worth telling the reader about once nothing touches it any
    // more: before that it is a match still in the game's hands, not a file that went wrong.
    private static readonly TimeSpan tsKeyGrace = TimeSpan.FromMinutes(4);

    /// <summary>Why a replay cannot be read when the game never got round to writing its key.</summary>
    private const string STR_NO_KEY = "Fortnite no llegó a cerrar esta repetición (el juego se cerró a mitad de partida) y quedó cifrada sin clave: no se puede leer.";

    private readonly Store _objStore;
    private readonly GameLogTail _objGameLog;
    private readonly SemaphoreSlim _objWakeUp = new(0, 1);
    private readonly object _objFailuresLock = new();
    private readonly Dictionary<string, (string Fingerprint, string Message, int Attempts, long Size, DateTime ModifiedUtc, bool WaitingForKey)> _dicFailures = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _bForceAll;
    private volatile bool _bRetryFailed;

    // The replay in progress is read again whenever it has grown (the game writes every 20 seconds)
    private string? _strLiveFile;
    private string? _strLiveFingerprint;
    private bool _bLiveHeaderClosed;
    private bool _bLiveSawRecording;

    public ReplayWatcher(Store objStore, GameLogTail objGameLog)
    {
        _objStore = objStore;
        _objGameLog = objGameLog;
    }

    /// <summary>
    /// Scan now. By default only new or changed replays are read.
    /// bRetryFailed also retries the ones that failed; bForceAll reads every replay on disk again.
    /// </summary>
    public void RequestScan(bool bForceAll = false, bool bRetryFailed = false)
    {
        if (bForceAll)
        {
            _bForceAll = true;
        }
        if (bRetryFailed)
        {
            _bRetryFailed = true;
        }

        try { _objWakeUp.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the web server start before the first, possibly long, pass
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Scan(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception objError)
            {
                _objStore.Log($"Error al revisar la carpeta de repeticiones: {objError.Message}");
            }

            try
            {
                await _objWakeUp.WaitAsync(tsScanInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Scan(CancellationToken objCancel)
    {
        var objStatus = _objStore.Status;
        var strDemosPath = _objStore.Settings.DemosPath;
        var bForceAll = _bForceAll;
        var bRetryFailed = _bRetryFailed;
        _bForceAll = false;
        _bRetryFailed = false;

        var bFolderExists = Directory.Exists(strDemosPath);
        var lstFiles = bFolderExists
            ? new DirectoryInfo(strDemosPath).GetFiles("*.replay").OrderByDescending(objFile => objFile.LastWriteTimeUtc).ToList()
            : new List<FileInfo>();

        // Failures only matter while the file is still there and unchanged
        var setNames = lstFiles.Select(objFile => objFile.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_objFailuresLock)
        {
            if (bForceAll || bRetryFailed)
            {
                // Forget the fingerprints so every failed file is read again, but keep counting attempts
                foreach (var strName in _dicFailures.Keys.ToList())
                {
                    _dicFailures[strName] = _dicFailures[strName] with { Fingerprint = "" };
                }
            }
            foreach (var strGone in _dicFailures.Keys.Where(strName => !setNames.Contains(strName)).ToList())
            {
                _dicFailures.Remove(strGone);
            }
        }

        string? strLiveFile = null;
        var lstToRead = new List<(FileInfo File, string Fingerprint)>();

        var setIgnored = _objStore.Settings.IgnoredFiles;
        foreach (var objFile in lstFiles)
        {
            // The user chose not to try this one again
            if (setIgnored.Contains(objFile.Name))
            {
                continue;
            }

            var tsAge = DateTime.UtcNow - objFile.LastWriteTimeUtc;

            // Files come newest first, so only the first one can be the match being played
            if (strLiveFile == null && objFile == lstFiles[0] && IsBeingWritten(objFile, tsAge))
            {
                strLiveFile = objFile.Name;
                continue;
            }

            if (tsAge < tsQuietTime)
            {
                continue;
            }

            var strFingerprint = $"{objFile.Length}|{objFile.LastWriteTimeUtc.Ticks}";
            var objCached = _objStore.FindMatch(objFile.Name);
            var bUpToDate = objCached is { IsLocked: false }
                && objCached.FileSize == objFile.Length
                && objCached.FileModifiedUtc == objFile.LastWriteTimeUtc
                && objCached.ParserVersion == ReplayParser.PARSER_VERSION;

            if (bUpToDate)
            {
                // This one is read and saved. If it failed earlier, that was a half-written copy
                // of the same file: the failure is about a file that no longer exists as it was.
                lock (_objFailuresLock)
                {
                    _dicFailures.Remove(objFile.Name);
                }
                if (!bForceAll)
                {
                    continue;
                }
            }

            // Do not retry a file that already failed unless it changed
            lock (_objFailuresLock)
            {
                if (_dicFailures.TryGetValue(objFile.Name, out var objFailure) && objFailure.Fingerprint == strFingerprint)
                {
                    continue;
                }
            }

            lstToRead.Add((objFile, strFingerprint));
        }

        ReadLive(strLiveFile == null ? null : lstFiles.First(objFile => objFile.Name == strLiveFile));

        // The game's log says what the replay cannot yet: end-of-match screen, searching for a match
        if (_objGameLog.Poll(strDemosPath, _objStore.Settings.LogPath))
        {
            _objStore.Game = _objGameLog.Status;
            _objStore.Touch();
        }

        var bChanged = objStatus.DemosFolderExists != bFolderExists
            || objStatus.ReplayFilesFound != lstFiles.Count
            || objStatus.LiveFile != strLiveFile
            || objStatus.PendingCount != lstToRead.Count;

        objStatus.DemosFolderExists = bFolderExists;
        objStatus.ReplayFilesFound = lstFiles.Count;
        objStatus.LiveFile = strLiveFile;
        objStatus.PendingCount = lstToRead.Count;
        objStatus.BatchTotal = lstToRead.Count;
        PublishErrors();
        if (bChanged)
        {
            _objStore.Touch();
        }

        if (lstToRead.Count > 0)
        {
            if (lstToRead.Count > 1)
            {
                _objStore.Log($"{lstToRead.Count} repeticiones por leer");
            }

            // A backlog is read a few at a time; a single new match uses one reader
            var intReaders = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
            var intPending = lstToRead.Count;

            Parallel.ForEach(lstToRead, new ParallelOptions { MaxDegreeOfParallelism = intReaders, CancellationToken = objCancel }, objItem =>
            {
                objStatus.ParsingFile = objItem.File.Name;
                ReadOne(objItem.File, objItem.Fingerprint);
                objStatus.PendingCount = Interlocked.Decrement(ref intPending);
                PublishErrors();

                // A long backlog must not freeze what the page knows about the match being played
                lock (_objGameLog)
                {
                    if (_objGameLog.Poll(strDemosPath, _objStore.Settings.LogPath))
                    {
                        _objStore.Game = _objGameLog.Status;
                    }
                }
                _objStore.Touch();
            });

            objStatus.ParsingFile = null;
            objStatus.PendingCount = 0;
            objStatus.BatchTotal = 0;
        }

        objStatus.LastScanUtc = DateTime.UtcNow;
        if (lstToRead.Count > 0)
        {
            _objStore.Touch();
        }
    }

    /// <summary>
    /// Is the game still recording into this file? Recently written, and its header has not been
    /// closed. If the header never said "recording" we fall back on whether the file keeps growing.
    /// </summary>
    private bool IsBeingWritten(FileInfo objFile, TimeSpan tsAge)
    {
        // Windows may report an old time and size for a file another program keeps open: when the
        // game's log says it is recording since before this file appeared, believe the log
        var objGame = _objStore.Game;
        if (IsGameRunning() && objGame.LogFound && objGame.RecordingSinceUtc is { } dtRecording
            && (objGame.MatchFinishedUtc == null || objGame.MatchFinishedUtc < dtRecording)
            && objFile.CreationTimeUtc >= dtRecording.AddMinutes(-2))
        {
            return true;
        }

        if (tsAge >= tsLiveWindow)
        {
            return false;
        }
        if (objFile.Name != _strLiveFile || !_bLiveHeaderClosed)
        {
            return true;
        }
        // Header closed: final if we watched it close, otherwise wait a full write cycle
        return !_bLiveSawRecording && tsAge < tsClosedWindow;
    }

    /// <summary>Whether Fortnite is open on this PC (only Windows can tell; elsewhere the answer is no).</summary>
    private static bool IsGameRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("FCC_ASSUME_GAME_RUNNING") == "1";
        }
        var arrProcesses = System.Diagnostics.Process.GetProcessesByName("FortniteClient-Win64-Shipping");
        var bRunning = arrProcesses.Length > 0;
        foreach (var objProcess in arrProcesses) objProcess.Dispose();
        return bRunning;
    }

    /// <summary>Keeps the "match in progress" view up to date. It never touches the saved matches.</summary>
    private void ReadLive(FileInfo? objFile)
    {
        if (objFile == null)
        {
            // The game stopped writing: the normal reading takes over and saves the finished match
            return;
        }

        var strFingerprint = $"{objFile.Length}|{objFile.LastWriteTimeUtc.Ticks}";
        if (objFile.Name == _strLiveFile && strFingerprint == _strLiveFingerprint)
        {
            return;
        }

        if (objFile.Name != _strLiveFile)
        {
            _bLiveHeaderClosed = false;
            _bLiveSawRecording = false;
        }

        try
        {
            // First a cheap look: while the game keeps the contents locked there is nothing to read
            var objRecord = ReplayParser.TryReadLocked(objFile, out var strReason);
            if (objRecord != null)
            {
                if (_objStore.LiveMatch is not { IsLocked: true } objShown || objShown.File != objRecord.File)
                {
                    _objStore.Log($"Partida en curso ({objFile.Name}): solo reloj, {strReason}");
                    _objStore.SetLiveMatch(objRecord);
                }
                _bLiveHeaderClosed = false;
                _bLiveSawRecording = true;
            }
            else
            {
                objRecord = ParseProcess.Parse(objFile.FullName);
                if (objRecord.IsLocked)
                {
                    return;   // the header changed between the two looks; the next round sorts it out
                }
                _bLiveHeaderClosed = !objRecord.IsRecording;
                _bLiveSawRecording |= objRecord.IsRecording;

                if (_bLiveHeaderClosed && _bLiveSawRecording)
                {
                    // We watched the game close the file: the match is over, save it right away
                    _objStore.SaveMatch(objRecord);
                    _objStore.SetLiveMatch(null);
                    // The same file may have failed minutes ago, when the key was not written yet
                    lock (_objFailuresLock)
                    {
                        _dicFailures.Remove(objFile.Name);
                    }
                    PublishErrors();
                    _objStore.Log($"Partida terminada y leída: {objFile.Name}");
                }
                else
                {
                    _objStore.SetLiveMatch(objRecord);
                }
            }
        }
        catch (Exception objError)
        {
            // Normal while the first seconds of a match are on disk; the next write is tried again
            if (objFile.Name != _strLiveFile)
            {
                _objStore.Log($"Partida en curso todavía sin datos legibles ({objFile.Name}): {objError.Message}");
            }
        }

        _strLiveFile = objFile.Name;
        _strLiveFingerprint = strFingerprint;
    }

    private void ReadOne(FileInfo objFile, string strFingerprint)
    {
        try
        {
            var objRecord = ParseProcess.Parse(objFile.FullName);
            if (objRecord.IsLocked)
            {
                // The key is not there yet. It arrives when the game closes the replay, so this is
                // only a real failure once the file has been left alone (see tsKeyGrace): it is
                // remembered quietly, and read again as soon as the game writes to it.
                if (RememberFailure(objFile, strFingerprint, STR_NO_KEY, bWaitingForKey: true) == 1)
                {
                    _objStore.Log($"Todavía sin clave {objFile.Name}: Fortnite la escribe al cerrar la repetición, se reintentará");
                }
                return;
            }
            _objStore.SaveMatch(objRecord);

            // The match that was being followed live is now a saved one
            if (_objStore.LiveMatch?.File == objRecord.File)
            {
                _objStore.SetLiveMatch(null);
            }
            lock (_objFailuresLock) { _dicFailures.Remove(objFile.Name); }
            _objStore.Log($"Leída {objFile.Name}");
        }
        catch (Exception objError)
        {
            RememberFailure(objFile, strFingerprint, objError.Message, bWaitingForKey: false);
            _objStore.Log($"No se pudo leer {objFile.Name}: {objError.Message}");
        }
    }

    /// <summary>Writes down a failed read and returns how many times this file has failed.</summary>
    private int RememberFailure(FileInfo objFile, string strFingerprint, string strMessage, bool bWaitingForKey)
    {
        lock (_objFailuresLock)
        {
            var intAttempts = _dicFailures.TryGetValue(objFile.Name, out var objBefore) ? objBefore.Attempts + 1 : 1;
            _dicFailures[objFile.Name] = (strFingerprint, strMessage, intAttempts, objFile.Length, objFile.LastWriteTimeUtc, bWaitingForKey);
            return intAttempts;
        }
    }

    /// <summary>Forgets a failure, so the file is read again on the next pass (or gone for good).</summary>
    public void ForgetFailure(string strFile)
    {
        lock (_objFailuresLock)
        {
            _dicFailures.Remove(strFile);
        }
        PublishErrors();
    }

    public void PublishErrors()
    {
        var dtNow = DateTime.UtcNow;
        lock (_objFailuresLock)
        {
            _objStore.Status.Errors = _dicFailures
                .Where(objPair => !_objStore.Settings.IgnoredFiles.Contains(objPair.Key))
                // Still waiting for the game to write the key: not something to worry the reader with
                .Where(objPair => !objPair.Value.WaitingForKey || dtNow - objPair.Value.ModifiedUtc >= tsKeyGrace)
                .Select(objPair => new ScanError { File = objPair.Key, Message = objPair.Value.Message, Attempts = objPair.Value.Attempts, Size = objPair.Value.Size, ModifiedUtc = objPair.Value.ModifiedUtc })
                .OrderByDescending(objError => objError.File)
                .ToList();
        }
    }
}

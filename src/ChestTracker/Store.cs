using System.Text;
using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// Settings and parsed matches, kept in memory and mirrored to two JSON files.
/// Parsed matches stay here even after Fortnite deletes the replay file.
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions objJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _objLock = new();
    private readonly string _strSettingsFile;
    private readonly string _strMatchesFile;
    private readonly string _strMatchesLog;
    private readonly string _strTreasureFile;
    private Dictionary<string, MatchRecord> _dicMatches = new(StringComparer.OrdinalIgnoreCase);
    private long _lngRevision = 1;

    public string DataFolder { get; }
    public TrackerSettings Settings { get; private set; } = new();
    public ScanStatus Status { get; } = new();

    /// <summary>Latest reading of the game's log; replaced as a whole by the watcher.</summary>
    public GameStatus Game { get; set; } = new();

    public Store(string strDataFolder, string? strDemosPathOverride)
    {
        DataFolder = strDataFolder;
        Directory.CreateDirectory(DataFolder);
        _strSettingsFile = Path.Combine(DataFolder, "settings.json");
        _strMatchesFile = Path.Combine(DataFolder, "matches.json");
        _strMatchesLog = Path.Combine(DataFolder, "matches.jsonl");
        _strTreasureFile = Path.Combine(DataFolder, "tesoros.json");

        Settings = ReadJson<TrackerSettings>(_strSettingsFile) ?? new TrackerSettings();
        Settings.ClassOverrides = new Dictionary<string, string>(Settings.ClassOverrides ?? new(), StringComparer.OrdinalIgnoreCase);
        Settings.ExcludedFiles = new HashSet<string>(Settings.ExcludedFiles ?? new(), StringComparer.OrdinalIgnoreCase);
        Settings.IgnoredFiles = new HashSet<string>(Settings.IgnoredFiles ?? new(), StringComparer.OrdinalIgnoreCase);
        Settings.LogPath ??= "";
        Settings.SquadPaths ??= new List<string>();

        // Before squads could be several there was one folder in SquadPath: fold it in, so
        // updating never quietly drops somebody out of the squad they were already sharing with
        if (!string.IsNullOrWhiteSpace(Settings.SquadPath))
        {
            if (!Settings.SquadPaths.Contains(Settings.SquadPath, StringComparer.OrdinalIgnoreCase))
            {
                Settings.SquadPaths.Insert(0, Settings.SquadPath);
            }

            Settings.SquadPath = "";
            WriteJson(_strSettingsFile, Settings);
        }

        if (!string.IsNullOrWhiteSpace(strDemosPathOverride))
        {
            Settings.DemosPath = strDemosPathOverride;
        }
        if (string.IsNullOrWhiteSpace(Settings.DemosPath))
        {
            Settings.DemosPath = DefaultDemosPath;
        }

        try
        {
            if (File.Exists(_strTreasureFile))
            {
                using var objDocument = JsonDocument.Parse(File.ReadAllText(_strTreasureFile));
                Treasure = objDocument.RootElement.Clone();
            }
        }
        catch (Exception objError)
        {
            Console.WriteLine($"No se pudo leer {_strTreasureFile}: {objError.Message}");
        }

        var lstMatches = ReadMatchLog() ?? MigrateWholeFile() ?? RecoverMatches();
        foreach (var objMatch in lstMatches)
        {
            _dicMatches[objMatch.File] = objMatch;
        }

        // What was already here does not count as new: the copy follows what is read from now on
        _intMatchesAtLastCopy = _dicMatches.Count;
    }

    // -----------------------------------------------------------------------------------------
    // How the matches sit on disk: one match per line, and a new one is written by adding its own
    // line at the end.
    //
    // They used to be a single JSON array rewritten whole every time a match finished. That is
    // fine at a hundred matches and silly at five hundred: with 209 matches the file was already
    // 10,7 MB, every one of them serialised, written and read back after each match, for one new
    // record of 51 KB. The cost grew with everything the reader had ever played.
    //
    // A match is never deleted on its own (excluding one is a setting, not a deletion), so the
    // only thing an append cannot express is a match being saved again — read a second time, or
    // its class corrected. That writes a second line for the same replay, and reading takes the
    // last line for each one. When those leftovers are worth more than a rewrite, the file is
    // written out compacted; that is the only time the whole thing is written at all.
    //
    // What is exported, backed up and imported is still one JSON array, which is what every copy
    // already saved holds, and it is built from memory instead of read back from disk.
    // -----------------------------------------------------------------------------------------

    /// <summary>How many lines the file holds, to know when the leftovers are worth a rewrite.</summary>
    private int _intLinesOnDisk;

    /// <summary>Leftover lines tolerated before compacting, on top of a quarter of the matches.</summary>
    private const int INT_COMPACT_SLACK = 50;

    /// <summary>
    /// The matches as the log tells them, newest line winning for each replay. Null when there is
    /// no log to read, or when there is one and not a single line of it could be understood, which
    /// is a damaged file and not an empty one: an empty file is somebody having deleted them on
    /// purpose and reads perfectly well as no matches.
    /// </summary>
    private List<MatchRecord>? ReadMatchLog()
    {
        if (!File.Exists(_strMatchesLog))
        {
            return null;
        }

        var dicByFile = new Dictionary<string, MatchRecord>(StringComparer.OrdinalIgnoreCase);
        var intLines = 0;
        var intBroken = 0;
        try
        {
            foreach (var strLine in File.ReadLines(_strMatchesLog))
            {
                if (strLine.Length == 0)
                {
                    continue;
                }
                intLines++;
                try
                {
                    var objMatch = JsonSerializer.Deserialize<MatchRecord>(strLine, objJsonOptions);
                    if (objMatch != null && !string.IsNullOrEmpty(objMatch.File))
                    {
                        dicByFile[objMatch.File] = objMatch;
                    }
                    else
                    {
                        intBroken++;
                    }
                }
                catch (JsonException)
                {
                    // A line cut in half: the power went out between writing it and finishing it.
                    // Only the last line can be in that state, and its match is read again from
                    // its replay if the file is still in the folder.
                    intBroken++;
                }
            }
        }
        catch (Exception objError)
        {
            Console.WriteLine($"No se pudo leer {_strMatchesLog}: {objError.Message}");
            return null;
        }

        if (intLines > 0 && dicByFile.Count == 0)
        {
            Console.WriteLine($"{_strMatchesLog} tiene {intLines} líneas y ninguna se entiende. Se guarda una copia y se recupera.");
            try { File.Copy(_strMatchesLog, _strMatchesLog + ".bak", overwrite: true); } catch { }
            return null;
        }
        if (intBroken > 0)
        {
            _strRecoveryNote = $"{intBroken} línea(s) del archivo de partidas estaban a medias y se han descartado; esas partidas se vuelven a leer de su repetición si sigue en la carpeta.";
        }

        _intLinesOnDisk = intLines;
        return dicByFile.Values.OrderBy(objItem => objItem.StartedAt).ToList();
    }

    /// <summary>
    /// The one-off move from the old single file. The log is written and read back before the old
    /// file is touched: if anything does not add up, the old file stays exactly where it is and
    /// the program carries on with what it read from it.
    /// </summary>
    private List<MatchRecord>? MigrateWholeFile()
    {
        var lstOld = ReadJson<List<MatchRecord>>(_strMatchesFile);
        if (lstOld == null)
        {
            return null;
        }

        var lstMatches = lstOld.Where(objItem => !string.IsNullOrEmpty(objItem.File))
            .GroupBy(objItem => objItem.File, StringComparer.OrdinalIgnoreCase)
            .Select(objGroup => objGroup.Last())
            .OrderBy(objItem => objItem.StartedAt)
            .ToList();

        try
        {
            WriteMatchLog(lstMatches);
            if (ReadMatchLog() is not { } lstBack || lstBack.Count != lstMatches.Count)
            {
                Console.WriteLine("Las partidas pasadas al archivo nuevo no cuadran: se deja el anterior como estaba.");
                return lstMatches;
            }
            File.Move(_strMatchesFile, _strMatchesFile + ".anterior", overwrite: true);
            _strRecoveryNote = $"Las {lstMatches.Count} partidas se han pasado a matches.jsonl, que se escribe partida a partida en vez de entero. El archivo anterior queda como matches.json.anterior.";
        }
        catch (Exception objError)
        {
            Console.WriteLine($"No se pudieron pasar las partidas al archivo nuevo: {objError.Message}");
        }
        return lstMatches;
    }

    /// <summary>One line per match, through a temporary file so a crash never leaves it half written.</summary>
    private void WriteMatchLog(IEnumerable<MatchRecord> lstMatches)
    {
        var strTemp = _strMatchesLog + ".tmp";
        var intLines = 0;
        using (var objWriter = new StreamWriter(strTemp, append: false, new UTF8Encoding(false)))
        {
            foreach (var objMatch in lstMatches)
            {
                objWriter.Write(JsonSerializer.Serialize(objMatch, objCompactJsonOptions));
                objWriter.Write('\n');
                intLines++;
            }
        }
        File.Move(strTemp, _strMatchesLog, overwrite: true);
        _intLinesOnDisk = intLines;
    }

    /// <summary>One match, added at the end. This is what finishing a match costs now.</summary>
    private void AppendMatchLog(MatchRecord objMatch)
    {
        using (var objWriter = new StreamWriter(_strMatchesLog, append: true, new UTF8Encoding(false)))
        {
            objWriter.Write(JsonSerializer.Serialize(objMatch, objCompactJsonOptions));
            objWriter.Write('\n');
        }
        _intLinesOnDisk++;
    }

    /// <summary>
    /// Rewrites the file without the lines that a later one has replaced, when there are enough of
    /// them to be worth it. A reader who never re-reads their folder never gets here.
    /// </summary>
    private void CompactIfWorthIt()
    {
        if (_intLinesOnDisk <= _dicMatches.Count + Math.Max(INT_COMPACT_SLACK, _dicMatches.Count / 4))
        {
            return;
        }
        try
        {
            var intBefore = _intLinesOnDisk;
            WriteMatchLog(_dicMatches.Values.OrderBy(objItem => objItem.StartedAt));
            Log($"Archivo de partidas compactado: {intBefore} líneas a {_intLinesOnDisk}");
        }
        catch (Exception objError)
        {
            // Nothing is lost by failing here: the leftovers are harmless, only wasteful
            Log($"No se pudo compactar el archivo de partidas: {objError.Message}");
        }
    }

    /// <summary>Every match as one JSON array, which is what a copy, an export and an import speak.</summary>
    private string BuildMatchesJson() =>
        JsonSerializer.Serialize(_dicMatches.Values.OrderBy(objItem => objItem.StartedAt).ToList(), objCompactJsonOptions);

    /// <summary>
    /// The matches file is not there, or could not be read: the matches whose replay Fortnite has
    /// already deleted would be lost for good, so the newest copy is put back by itself.
    /// An empty file is a different matter — that is somebody having chosen to delete them — and
    /// never gets here, because an empty list reads perfectly well.
    /// </summary>
    private List<MatchRecord> RecoverMatches()
    {
        try
        {
            if (Backup.ReadNewest(DataFolder) is not { } strJson)
            {
                return new List<MatchRecord>();
            }

            var lstRecovered = JsonSerializer.Deserialize<List<MatchRecord>>(strJson, objJsonOptions) ?? new List<MatchRecord>();
            if (lstRecovered.Count == 0)
            {
                return lstRecovered;
            }

            WriteMatchLog(lstRecovered);
            _strRecoveryNote = $"No estaban las partidas guardadas: se han recuperado {lstRecovered.Count} de la copia de seguridad más reciente.";
            return lstRecovered;
        }
        catch (Exception objError)
        {
            Console.WriteLine($"No se pudo recuperar la copia de seguridad: {objError.Message}");
            return new List<MatchRecord>();
        }
    }

    /// <summary>Said once, as soon as there is a log to say it in: the matches came back from a copy.</summary>
    private string? _strRecoveryNote;

    public string? TakeRecoveryNote()
    {
        var strNote = _strRecoveryNote;
        _strRecoveryNote = null;
        return strNote;
    }

    /// <summary>%LOCALAPPDATA%\FortniteGame\Saved\Demos, where the game writes its replays.</summary>
    public static string DefaultDemosPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    /// <summary>The treasure tab's own data (rewards, marks, pasted images). Kept in its own file because of the images.</summary>
    public JsonElement? Treasure { get; private set; }

    public void SaveTreasure(JsonElement? objTreasure)
    {
        lock (_objLock)
        {
            Treasure = objTreasure?.Clone();
            _lngRevision++;
            if (Treasure is { } objValue)
            {
                var strTemp = _strTreasureFile + ".tmp";
                File.WriteAllText(strTemp, objValue.GetRawText());
                File.Move(strTemp, _strTreasureFile, overwrite: true);
            }
            else if (File.Exists(_strTreasureFile))
            {
                File.Delete(_strTreasureFile);
            }
        }
    }

    /// <summary>Latest reading of the replay the game is recording right now; never written to disk.</summary>
    public MatchRecord? LiveMatch { get; private set; }

    public void SetLiveMatch(MatchRecord? objMatch)
    {
        lock (_objLock)
        {
            if (objMatch == null && LiveMatch == null) return;
            LiveMatch = objMatch;
            _lngRevision++;
        }
    }

    public long Revision { get { lock (_objLock) { return _lngRevision; } } }

    public void Touch() { lock (_objLock) { _lngRevision++; } }

    public List<MatchRecord> GetMatches()
    {
        lock (_objLock) { return _dicMatches.Values.ToList(); }
    }

    public MatchRecord? FindMatch(string strFile)
    {
        lock (_objLock) { return _dicMatches.GetValueOrDefault(strFile); }
    }

    public void SaveMatch(MatchRecord objMatch)
    {
        lock (_objLock)
        {
            // Write first, keep second: if this record cannot be saved, nothing else is affected.
            // One line, whatever the reader has played before.
            AppendMatchLog(objMatch);

            _dicMatches[objMatch.File] = objMatch;
            _lngRevision++;
            CompactIfWorthIt();
            KeepCopy();

            // The first replay with a known owner teaches us who the user is
            if (string.IsNullOrEmpty(Settings.EpicId) && !string.IsNullOrEmpty(objMatch.DetectedOwnerId))
            {
                Settings.EpicId = objMatch.DetectedOwnerId;
                WriteJson(_strSettingsFile, Settings);
            }
        }
    }

    /// <summary>
    /// Keeps today's copy of the saved matches up to date. Called whenever they change; the copy
    /// itself decides whether enough time has passed to be worth rewriting.
    /// </summary>
    private void KeepCopy()
    {
        if (_dicMatches.Count == 0)
        {
            return;     // Nothing worth copying, and never a copy that would undo a deletion
        }

        // Normally the copy waits out its own gap, so an evening of matches is not a copy each.
        // But a folder being read for the first time saves dozens in a row, and a copy holding
        // only the first of them would be worth nothing: enough new matches and it is rewritten.
        var bEnoughNew = _dicMatches.Count - _intMatchesAtLastCopy >= INT_MATCHES_PER_COPY;
        // The text is built only if a copy is really going to be written: most calls decide not to
        if (Backup.Write(DataFolder, BuildMatchesJson, Log, bForce: bEnoughNew))
        {
            _intMatchesAtLastCopy = _dicMatches.Count;
        }
    }

    private const int INT_MATCHES_PER_COPY = 5;
    private int _intMatchesAtLastCopy;

    /// <summary>Makes a copy now, however little time has passed since the last one.</summary>
    public bool CopyNow()
    {
        lock (_objLock)
        {
            return _dicMatches.Count > 0 && Backup.Write(DataFolder, BuildMatchesJson, Log, bForce: true);
        }
    }

    /// <summary>The matches file exactly as it sits on disk, for the reader to keep a copy of.</summary>
    public string ReadRawMatches()
    {
        lock (_objLock)
        {
            return BuildMatchesJson();
        }
    }

    /// <summary>
    /// Puts back the matches of a backup. A match already here is left as it is: restoring the
    /// same file twice changes nothing, and a saved copy never overwrites what was read since.
    /// </summary>
    public ImportOutcome ImportMatches(string strJson)
    {
        var lstIncoming = JsonSerializer.Deserialize<List<MatchRecord>>(strJson, objJsonOptions)
            ?? throw new JsonException("El archivo no contiene una lista de partidas.");

        lock (_objLock)
        {
            var intAdded = 0;
            foreach (var objMatch in lstIncoming.Where(objMatch => !string.IsNullOrEmpty(objMatch.File)))
            {
                if (_dicMatches.ContainsKey(objMatch.File))
                {
                    continue;
                }
                AppendMatchLog(objMatch);
                _dicMatches[objMatch.File] = objMatch;
                intAdded++;
            }

            if (intAdded > 0)
            {
                _lngRevision++;
                CompactIfWorthIt();
                KeepCopy();
            }

            return new ImportOutcome { Added = intAdded, Skipped = lstIncoming.Count - intAdded, Total = _dicMatches.Count };
        }
    }

    /// <summary>Forgets every saved match. Replays still in the folder are read again by the watcher.</summary>
    public void ClearMatches()
    {
        lock (_objLock)
        {
            _dicMatches.Clear();
            _lngRevision++;
            WriteMatchLog(Array.Empty<MatchRecord>());
        }
    }

    /// <summary>Back to a fresh install, keeping only the replay folder.</summary>
    public void ResetSettings()
    {
        lock (_objLock)
        {
            // The group is kept: it is shared with other people, not a preference of this PC,
            // so leaving it stays an explicit act with its own button.
            Settings = new TrackerSettings
            {
                DemosPath = Settings.DemosPath,
                LogPath = Settings.LogPath,
                TrayNoticeShown = Settings.TrayNoticeShown,
                SquadPaths = Settings.SquadPaths
            };
            _lngRevision++;
            WriteJson(_strSettingsFile, Settings);
        }
    }

    public void UpdateSettings(Action<TrackerSettings> fnChange)
    {
        lock (_objLock)
        {
            fnChange(Settings);
            _lngRevision++;
            WriteJson(_strSettingsFile, Settings);
        }
    }

    /// <summary>Appends a line to chestcounter.log in the data folder (kept under about 1 MB).</summary>
    public void Log(string strMessage)
    {
        var strLine = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {strMessage}";
        Console.WriteLine(strLine[11..]);
        try
        {
            var strLogFile = Path.Combine(DataFolder, "chestcounter.log");
            lock (_objLock)
            {
                if (File.Exists(strLogFile) && new FileInfo(strLogFile).Length > 1_000_000)
                {
                    File.Move(strLogFile, strLogFile + ".anterior", overwrite: true);
                }
                File.AppendAllText(strLogFile, strLine + Environment.NewLine);
            }
        }
        catch { /* logging must never break the program */ }
    }

    private static T? ReadJson<T>(string strFile) where T : class
    {
        try
        {
            return File.Exists(strFile) ? JsonSerializer.Deserialize<T>(File.ReadAllText(strFile), objJsonOptions) : null;
        }
        catch (Exception objError)
        {
            // A damaged file must not stop the program: keep a copy and start clean
            Console.WriteLine($"No se pudo leer {strFile}: {objError.Message}. Se guarda una copia y se empieza de cero.");
            try { File.Copy(strFile, strFile + ".bak", overwrite: true); } catch { }
            return null;
        }
    }

    private static readonly JsonSerializerOptions objCompactJsonOptions = new(objJsonOptions) { WriteIndented = false };

    private static void WriteJson<T>(string strFile, T objValue, bool bCompact = false)
    {
        // Write to a temporary file first so a crash never leaves half a file behind
        var strTemp = strFile + ".tmp";
        File.WriteAllText(strTemp, JsonSerializer.Serialize(objValue, bCompact ? objCompactJsonOptions : objJsonOptions));
        File.Move(strTemp, strFile, overwrite: true);
    }
}

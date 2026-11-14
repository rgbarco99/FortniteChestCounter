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

        var lstMatches = ReadJson<List<MatchRecord>>(_strMatchesFile) ?? RecoverMatches();
        foreach (var objMatch in lstMatches)
        {
            _dicMatches[objMatch.File] = objMatch;
        }

        // What was already here does not count as new: the copy follows what is read from now on
        _intMatchesAtLastCopy = _dicMatches.Count;
    }

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

            WriteJson(_strMatchesFile, lstRecovered, bCompact: true);
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
            // Write first, keep second: if this record cannot be saved, nothing else is affected
            var lstToSave = _dicMatches.Values.Where(objItem => !objItem.File.Equals(objMatch.File, StringComparison.OrdinalIgnoreCase))
                .Append(objMatch).OrderBy(objItem => objItem.StartedAt).ToList();
            WriteJson(_strMatchesFile, lstToSave, bCompact: true);

            _dicMatches[objMatch.File] = objMatch;
            _lngRevision++;
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
        if (Backup.Write(DataFolder, File.Exists(_strMatchesFile) ? File.ReadAllText(_strMatchesFile) : "[]", Log, bForce: bEnoughNew))
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
            return _dicMatches.Count > 0 && Backup.Write(DataFolder, File.ReadAllText(_strMatchesFile), Log, bForce: true);
        }
    }

    /// <summary>The matches file exactly as it sits on disk, for the reader to keep a copy of.</summary>
    public string ReadRawMatches()
    {
        lock (_objLock)
        {
            return File.Exists(_strMatchesFile) ? File.ReadAllText(_strMatchesFile) : "[]";
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
                _dicMatches[objMatch.File] = objMatch;
                intAdded++;
            }

            if (intAdded > 0)
            {
                WriteJson(_strMatchesFile, _dicMatches.Values.OrderBy(objItem => objItem.StartedAt).ToList(), bCompact: true);
                _lngRevision++;
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
            WriteJson(_strMatchesFile, new List<MatchRecord>());
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

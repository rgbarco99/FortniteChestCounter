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

        var lstMatches = ReadJson<List<MatchRecord>>(_strMatchesFile) ?? new List<MatchRecord>();
        foreach (var objMatch in lstMatches)
        {
            _dicMatches[objMatch.File] = objMatch;
        }
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

            // The first replay with a known owner teaches us who the user is
            if (string.IsNullOrEmpty(Settings.EpicId) && !string.IsNullOrEmpty(objMatch.DetectedOwnerId))
            {
                Settings.EpicId = objMatch.DetectedOwnerId;
                WriteJson(_strSettingsFile, Settings);
            }
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
            Settings = new TrackerSettings { DemosPath = Settings.DemosPath };
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

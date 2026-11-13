using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ChestTracker;

// ---------------------------------------------------------------------------
// Command line
//   --demos <folder>    replay folder (default: %LOCALAPPDATA%\FortniteGame\Saved\Demos)
//   --data <folder>     where settings and parsed matches are kept
//   --port <number>     preferred port (default 5380)
//   --no-browser        do not open the browser on start
//   --dump <file>       parse one replay, print the result as JSON and exit
//   --log <file>        Fortnite's log (default: next to the replay folder)
//   --wait-pid <id>     wait for that process to close first (used when updating)
// ---------------------------------------------------------------------------

string? GetOption(string strName)
{
    var intIndex = Array.IndexOf(args, strName);
    return intIndex >= 0 && intIndex + 1 < args.Length ? args[intIndex + 1] : null;
}

var objIndentedJson = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

if (GetOption("--dump") is { } strDumpFile)
{
    // The program has no console of its own (it is a WinExe), so a dump typed at a prompt
    // borrows that one. When the output is already a pipe there is nothing to borrow: that
    // is how the program reads every replay, one short-lived copy of itself per file.
    if (!Console.IsOutputRedirected)
    {
        Native.AttachParentConsole();
    }

    try
    {
        var objRecord = ParseProcess.ParseWithLargeStack(strDumpFile);

        // Raw UTF-8 bytes, so player names survive whatever code page the console uses
        using var objOutput = Console.OpenStandardOutput();
        JsonSerializer.Serialize(objOutput, objRecord, objIndentedJson);
        return 0;
    }
    catch (Exception objError)
    {
        Console.Error.WriteLine(objError.Message.ReplaceLineEndings(" "));
        return ParseProcess.EXIT_UNREADABLE;
    }
}

// After an update the previous copy hands over its port: wait until it has closed
Updater.WaitForPreviousCopy(GetOption("--wait-pid"));

var strDataFolder = GetOption("--data") ?? GetDefaultDataFolder();

// One copy at a time: opened again by mistake, it hands the dashboard of the copy already
// running to the browser and closes. --port asks for a copy of its own, and so does
// --wait-pid, which is the new version taking over right after an update.
if (GetOption("--port") == null && GetOption("--wait-pid") == null && Instance.FindRunning(strDataFolder) is { } strRunningUrl)
{
    if (!args.Contains("--no-browser"))
    {
        OpenInBrowser(strRunningUrl);
    }
    return 0;
}

var objStore = new Store(strDataFolder, GetOption("--demos"));
var intPort = FindFreePort(int.TryParse(GetOption("--port"), out var intPreferred) ? intPreferred : 5380);
var strUrl = $"http://127.0.0.1:{intPort}";

var objBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
objBuilder.Logging.ClearProviders();
objBuilder.WebHost.UseUrls(strUrl);
objBuilder.Services.ConfigureHttpJsonOptions(objOptions =>
{
    objOptions.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    objOptions.SerializerOptions.PropertyNameCaseInsensitive = true;
});
objBuilder.Services.AddSingleton(objStore);
objBuilder.Services.AddSingleton(new GameLogTail(GetOption("--log")));
objBuilder.Services.AddSingleton<ReplayWatcher>();
objBuilder.Services.AddHostedService(objServices => objServices.GetRequiredService<ReplayWatcher>());
objBuilder.Services.AddSingleton<Updater>();
objBuilder.Services.AddHostedService(objServices => objServices.GetRequiredService<Updater>());
objBuilder.Services.AddSingleton<TrayIcon>();
objBuilder.Services.AddHostedService(objServices => objServices.GetRequiredService<TrayIcon>());

var objApp = objBuilder.Build();
var objUpdater = objApp.Services.GetRequiredService<Updater>();
objUpdater.Port = intPort;
objApp.Services.GetRequiredService<TrayIcon>().Url = strUrl;

// The interface: one HTML file embedded in the executable.
// FCC_UI can point to a file on disk to edit the page without rebuilding.
objApp.MapGet("/", () =>
{
    var strDevFile = Environment.GetEnvironmentVariable("FCC_UI");
    if (!string.IsNullOrEmpty(strDevFile) && File.Exists(strDevFile))
    {
        return Results.Content(File.ReadAllText(strDevFile), "text/html; charset=utf-8");
    }

    using var objStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!;
    using var objReader = new StreamReader(objStream);
    return Results.Content(objReader.ReadToEnd(), "text/html; charset=utf-8");
});

// Cheap call the page polls to know whether anything changed
objApp.MapGet("/api/revision", () => new { revision = objStore.Revision });

objApp.MapGet("/api/state", () =>
{
    var objSettings = objStore.Settings;
    var lstRecords = objStore.GetMatches();

    return new StateResponse
    {
        Revision = objStore.Revision,
        Status = objStore.Status,
        Game = objStore.Game,
        Update = objUpdater.Info,
        Settings = new SettingsView
        {
            DemosPath = objSettings.DemosPath,
            DefaultDemosPath = Store.DefaultDemosPath,
            EpicId = objSettings.EpicId,
            DataFolder = objStore.DataFolder,
            LogPath = objSettings.LogPath,
            DefaultLogPath = GameLogTail.GetLogPath(objSettings.DemosPath),
            IgnoredFiles = objSettings.IgnoredFiles.OrderByDescending(strFile => strFile).ToList()
        },
        Categories = Summarizer.Categories,
        Classes = Summarizer.ListClasses(lstRecords, objSettings),
        Matches = lstRecords
            .Select(objRecord =>
            {
                try { return Summarizer.Summarize(objRecord, objSettings, objSettings.DemosPath); }
                catch (Exception objError) { objStore.Log($"No se pudo resumir {objRecord.File}: {objError.Message}"); return null; }
            })
            .Where(objMatch => objMatch != null)
            .Select(objMatch => objMatch!)
            .OrderBy(objMatch => objMatch.StartedAt)
            .ToList(),
        Layout = objSettings.Layout,
        Treasure = objStore.Treasure,
        Live = objStore.LiveMatch is { } objLive
            ? new LiveView { Match = Summarizer.Summarize(objLive, objSettings, objSettings.DemosPath), WrittenUtc = DateTime.SpecifyKind(objLive.FileModifiedUtc, DateTimeKind.Utc), Locked = objLive.IsLocked }
            : null
    };
});

objApp.MapPut("/api/settings", (SettingsRequest objRequest, ReplayWatcher objWatcher) =>
{
    objStore.UpdateSettings(objSettings =>
    {
        if (objRequest.DemosPath != null)
        {
            // Accept %LOCALAPPDATA%-style paths typed by hand
            var strPath = Environment.ExpandEnvironmentVariables(objRequest.DemosPath.Trim().Trim('"'));
            objSettings.DemosPath = strPath.Length > 0 ? strPath : Store.DefaultDemosPath;
        }
        if (objRequest.EpicId != null)
        {
            objSettings.EpicId = objRequest.EpicId.Trim().ToUpperInvariant();
        }
        if (objRequest.LogPath != null)
        {
            objSettings.LogPath = Environment.ExpandEnvironmentVariables(objRequest.LogPath.Trim().Trim('"'));
        }
        if (objRequest.ClassOverrides != null)
        {
            objSettings.ClassOverrides = new Dictionary<string, string>(objRequest.ClassOverrides, StringComparer.OrdinalIgnoreCase);
        }
    });

    objWatcher.RequestScan();
    return Results.Ok();
});

// Images pasted into the treasure tab travel inside this document, hence the larger limit
objApp.MapPut("/api/treasure", (JsonElement objTreasure) =>
{
    objStore.SaveTreasure(objTreasure);
    return Results.Ok(new { revision = objStore.Revision });
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(20_000_000));

objApp.MapPut("/api/layout", (JsonElement objLayout) =>
{
    objStore.UpdateSettings(objSettings => objSettings.Layout = objLayout.Clone());
    return Results.Ok();
});

// mode: "new" (default) reads only new or changed replays, "failed" also retries the ones
// that could not be read, "all" reads every replay in the folder again.
objApp.MapPost("/api/rescan", (string? mode, ReplayWatcher objWatcher) =>
{
    objStore.Log($"Lectura pedida desde el panel: {mode ?? "new"}");
    objWatcher.RequestScan(bForceAll: mode == "all", bRetryFailed: mode == "failed");
    return Results.Ok();
});

// scope: "matches" forgets the saved matches; "all" also forgets every setting except the folder.
// Either way the replays still in the folder are read again afterwards.
objApp.MapPost("/api/reset", (string? scope, ReplayWatcher objWatcher) =>
{
    objStore.Log($"Borrado pedido desde el panel: {scope ?? "matches"}");
    objStore.ClearMatches();
    if (scope == "all")
    {
        objStore.ResetSettings();
        objStore.SaveTreasure(null);
    }
    objWatcher.RequestScan(bRetryFailed: true);
    return Results.Ok();
});

objApp.MapPost("/api/update/check", () => { objUpdater.RequestCheck(); return Results.Ok(); });
objApp.MapPost("/api/update/apply", () => objUpdater.StartApply() ? Results.Ok() : Results.Conflict());

// Failed replays: stop trying them, try them again, or delete the files
objApp.MapPost("/api/replays/ignore", (ReplayFilesRequest objRequest, ReplayWatcher objWatcher) =>
{
    objStore.UpdateSettings(objSettings => objRequest.Files.ForEach(strFile => objSettings.IgnoredFiles.Add(strFile)));
    objWatcher.PublishErrors();
    return Results.Ok();
});

objApp.MapPost("/api/replays/unignore", (ReplayFilesRequest objRequest, ReplayWatcher objWatcher) =>
{
    objStore.UpdateSettings(objSettings => objRequest.Files.ForEach(strFile => objSettings.IgnoredFiles.Remove(strFile)));
    objWatcher.RequestScan(bRetryFailed: true);
    return Results.Ok();
});

objApp.MapPost("/api/replays/delete", (ReplayFilesRequest objRequest, ReplayWatcher objWatcher) =>
{
    // Only plain .replay file names inside the replay folder; never a path
    var strFolder = Path.GetFullPath(objStore.Settings.DemosPath);
    var lstProblems = new List<string>();
    foreach (var strFile in objRequest.Files)
    {
        var strPath = Path.GetFullPath(Path.Combine(strFolder, strFile));
        if (Path.GetFileName(strFile) != strFile || !strFile.EndsWith(".replay", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(strPath), strFolder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            lstProblems.Add(strFile);
            continue;
        }
        try
        {
            if (File.Exists(strPath)) File.Delete(strPath);
            objWatcher.ForgetFailure(strFile);
            objStore.Log($"Replay deleted by the user: {strFile}");
        }
        catch (Exception objError) when (objError is IOException or UnauthorizedAccessException)
        {
            lstProblems.Add($"{strFile}: {objError.Message}");
        }
    }
    objStore.UpdateSettings(objSettings => objRequest.Files.ForEach(strFile => objSettings.IgnoredFiles.Remove(strFile)));
    objWatcher.RequestScan();
    return lstProblems.Count == 0 ? Results.Ok() : Results.Problem(string.Join("; ", lstProblems), statusCode: 409);
});

objApp.MapPost("/api/matches/exclude", (ExcludeRequest objRequest) =>
{
    objStore.UpdateSettings(objSettings =>
    {
        if (objRequest.Excluded) objSettings.ExcludedFiles.Add(objRequest.File);
        else objSettings.ExcludedFiles.Remove(objRequest.File);
    });
    return Results.Ok();
});

// There is no window to read: this goes to chestcounter.log, and to the console of
// whoever started the program if there happens to be one (start.cmd, a prompt).
objStore.Log($"Fortnite Chest Counter {Updater.CurrentVersion} · panel en {strUrl} · repeticiones en {objStore.Settings.DemosPath}");

// Where a second copy opened by mistake has to look
Instance.Write(strDataFolder, strUrl);
objApp.Lifetime.ApplicationStopped.Register(() => Instance.Remove(strDataFolder));

if (!args.Contains("--no-browser"))
{
    objApp.Lifetime.ApplicationStarted.Register(() => OpenInBrowser(strUrl));
}

objApp.Run();
return 0;

static void OpenInBrowser(string strAddress)
{
    try { Process.Start(new ProcessStartInfo(strAddress) { UseShellExecute = true }); }
    catch (Exception) { /* No browser available: the address is in the log, and in the tray tooltip */ }
}

// %LOCALAPPDATA%\FortniteChestCounter. Data of the earlier Spanish-named version is copied over once.
static string GetDefaultDataFolder()
{
    var strLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var strFolder = Path.Combine(strLocal, "FortniteChestCounter");
    var strOldFolder = Path.Combine(strLocal, "ContadorCofres");
    if (!Directory.Exists(strFolder) && Directory.Exists(strOldFolder))
    {
        try
        {
            Directory.CreateDirectory(strFolder);
            foreach (var strFile in Directory.GetFiles(strOldFolder))
            {
                var strName = Path.GetFileName(strFile);
                File.Copy(strFile, Path.Combine(strFolder, strName == "contador.log" ? "chestcounter.log" : strName));
            }
        }
        catch (IOException) { /* Start empty rather than not start */ }
    }
    return strFolder;
}

// First free port starting at the preferred one, so a second copy or another program does not block the start
static int FindFreePort(int intPreferred)
{
    for (var intCandidate = intPreferred; intCandidate < intPreferred + 20; intCandidate++)
    {
        try
        {
            var objListener = new TcpListener(IPAddress.Loopback, intCandidate);
            objListener.Start();
            objListener.Stop();
            return intCandidate;
        }
        catch (SocketException) { }
    }
    return intPreferred;
}

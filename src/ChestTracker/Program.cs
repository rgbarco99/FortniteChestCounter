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
objBuilder.Services.AddSingleton<SquadPublisher>();
objBuilder.Services.AddHostedService(objServices => objServices.GetRequiredService<SquadPublisher>());
objBuilder.Services.AddSingleton<SquadReader>();
objBuilder.Services.AddHostedService(objServices => objServices.GetRequiredService<SquadReader>());
objBuilder.Services.AddSingleton<SeasonMap>();
objBuilder.Services.AddSingleton<Shop>();
objBuilder.Services.AddSingleton<TrayIcon>();
objBuilder.Services.AddHostedService(objServices => objServices.GetRequiredService<TrayIcon>());

var objApp = objBuilder.Build();
var objUpdater = objApp.Services.GetRequiredService<Updater>();
objUpdater.Port = intPort;
objApp.Services.GetRequiredService<TrayIcon>().Url = strUrl;

// The interface: one HTML file embedded in the executable.
// FCC_UI can point to a file on disk to edit the page without rebuilding.
// Never from the cache: the page ships inside the executable, so an update replaces it, and a
// tab that reloads itself after one has to be given the new one and not the one it already had.
objApp.MapGet("/", (HttpContext objContext) =>
{
    objContext.Response.Headers.CacheControl = "no-store, must-revalidate";

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
var objSquadReader = objApp.Services.GetRequiredService<SquadReader>();

// Both counters only ever go up, so their sum is a revision too: the page refreshes when your
// own matches change and also when a squadmate publishes something new.
objApp.MapGet("/api/revision", () => new { revision = objStore.Revision + objSquadReader.Revision });

objApp.MapGet("/api/state", () =>
{
    var objSettings = objStore.Settings;
    var lstRecords = objStore.GetMatches();

    var lstMatches = lstRecords
        .Select(objRecord =>
        {
            try { return Summarizer.Summarize(objRecord, objSettings, objSettings.DemosPath); }
            catch (Exception objError) { objStore.Log($"No se pudo resumir {objRecord.File}: {objError.Message}"); return null; }
        })
        .Where(objMatch => objMatch != null)
        .Select(objMatch => objMatch!)
        .OrderBy(objMatch => objMatch.StartedAt)
        .ToList();

    // What the squad shared, joined to your matches by session id. Their figures sit beside
    // yours and are never added into them.
    // Who somebody is, is their Epic id; what they are called is whatever they are called today.
    // A replay keeps the nick its match was played under, so a squadmate who renames themselves
    // turns into two people across your history. The newest name wins everywhere: what they
    // publish right now first, and failing that the most recent match that saw them.
    var dicNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var objMatch in lstMatches)
    {
        foreach (var objMate in objMatch.Squad)
        {
            if (!string.IsNullOrEmpty(objMate.Name))
            {
                dicNames[objMate.Id] = objMate.Name;        // matches are in order, so the last wins
            }
        }
    }

    foreach (var (strId, strName) in objSquadReader.Names)
    {
        if (!string.IsNullOrEmpty(strName))
        {
            dicNames[strId] = strName!;
        }
    }

    var dicTotals = new Dictionary<string, SharedPlayer>(StringComparer.OrdinalIgnoreCase);
    foreach (var objMatch in lstMatches)
    {
        var dicCards = objSquadReader.ForSession(objMatch.SessionId);
        foreach (var objMate in objMatch.Squad)
        {
            if (dicNames.TryGetValue(objMate.Id, out var strCurrent))
            {
                objMate.Name = strCurrent;
            }

            if (objMate.IsYou || !dicCards.TryGetValue(objMate.Id, out var objCard))
            {
                // They have a file in the folder but no card for this match: it is on its way,
                // which is not the same as there being nothing to show
                objMate.Pending = !objMate.IsYou && objSquadReader.Names.ContainsKey(objMate.Id);
                continue;
            }

            objMate.SharedChests = objCard.Chests + objCard.Special + objCard.Spirit;
            objMate.Sharing = true;

            if (!dicTotals.TryGetValue(objMate.Id, out var objTotal))
            {
                objTotal = new SharedPlayer { Id = objMate.Id, Name = objMate.Name };
                dicTotals[objMate.Id] = objTotal;
            }

            objTotal.Matches++;
            objTotal.Chests += objMate.SharedChests.Value;
            objTotal.Kills += objCard.Kills;
            objTotal.YourChests += objMatch.ChestsTotal;
        }
    }

    return new StateResponse
    {
        Shared = dicTotals.Values.OrderByDescending(objPlayer => objPlayer.Matches).ToList(),
        InSquad = objSettings.SquadPaths.Count > 0,
        Sharing = new SharingStatus
        {
            CloudName = objSquadReader.CloudName,
            CloudRunning = objSquadReader.CloudRunning,
            Publishers = objSquadReader.Published
                .Select(objPair => new SharedPublisher
                {
                    Id = objPair.Key,
                    Name = dicNames.GetValueOrDefault(objPair.Key) ?? objSquadReader.Names.GetValueOrDefault(objPair.Key),
                    PublishedUtc = objPair.Value
                })
                .OrderByDescending(objOne => objOne.PublishedUtc)
                .ToList(),
            Reads = objSquadReader.Reads,
            WaitingSinceUtc = objSquadReader.WaitingSinceUtc,
            WaitReads = objSquadReader.WaitReads,
            EverySec = objSquadReader.EverySec
        },
        Revision = objStore.Revision + objSquadReader.Revision,
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
        Matches = lstMatches,
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

// Backup. Fortnite deletes its replays within days, so a match that is only here is gone for
// good if this folder is lost: these two give the reader a copy they keep themselves.
objApp.MapGet("/api/export/matches.json", () =>
    Results.File(System.Text.Encoding.UTF8.GetBytes(objStore.ReadRawMatches()), "application/json", $"cofres-{DateTime.Now:yyyy-MM-dd}.json"));

objApp.MapGet("/api/export/matches.csv", () =>
    Results.File(Export.BuildCsv(objStore), "text/csv", $"cofres-{DateTime.Now:yyyy-MM-dd}.csv"));

// The season's map: the island and its named places, kept in the data folder
objApp.MapGet("/api/map", (SeasonMap objMap) => objMap.Describe());

objApp.MapGet("/api/map/image", (SeasonMap objMap) =>
    objMap.HasImage ? Results.File(objMap.ReadImagePath(), "image/png") : Results.NotFound());

// The item shop, in the language the page is being read in. Only asked for when the tab is open
objApp.MapGet("/api/shop", async (string? lang, Shop objShop, CancellationToken objToken) =>
    await objShop.DescribeAsync(lang, objToken));

// The copies the program keeps by itself, newest first
objApp.MapGet("/api/backups", () => Backup.List(strDataFolder));

objApp.MapPost("/api/backups/now", () => objStore.CopyNow() ? Results.Ok() : Results.Conflict());

objApp.MapPost("/api/backups/restore", (string name, ReplayWatcher objWatcher) =>
{
    try
    {
        var objOutcome = objStore.ImportMatches(Backup.Read(strDataFolder, name));
        objStore.Log($"Recuperadas {objOutcome.Added} partidas de la copia {name} ({objOutcome.Skipped} ya estaban)");
        return Results.Ok(objOutcome);
    }
    catch (Exception objError) when (objError is ArgumentException or IOException or JsonException)
    {
        return Results.BadRequest(new { error = objError.Message });
    }
});

// Restoring is what makes a backup worth taking. Matches already here are left alone, so the
// same file can be loaded twice without harm, and nothing is ever replaced by an older copy.
objApp.MapPost("/api/import", async (HttpRequest objRequest, ReplayWatcher objWatcher) =>
{
    using var objReader = new StreamReader(objRequest.Body);
    var strBody = await objReader.ReadToEndAsync();
    try
    {
        var objOutcome = objStore.ImportMatches(strBody);
        objStore.Log($"Restauradas {objOutcome.Added} partidas de una copia de seguridad ({objOutcome.Skipped} ya estaban)");
        return Results.Ok(objOutcome);
    }
    catch (JsonException objError)
    {
        return Results.BadRequest(new { error = objError.Message });
    }
}).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(200_000_000));

// ---------------------------------------------------------------------------
// Squads: folders shared through OneDrive, Drive or Dropbox, up to four of them. Nothing
// here runs until the player joins one on purpose; an empty SquadPaths means it is off.
// ---------------------------------------------------------------------------

// How many of the last matches are looked at, and how many of them somebody has to appear in
// to count as one of your squad rather than a stranger a fill put there. Measured on real data:
// the regular squad shows up in 29 to 39 of the last 40 matches, every passing stranger in one.
const int INT_RECENT_MATCHES = 40;
const int INT_REGULAR_MATES = 3;

// Your squadmates of the last matches, read from your own replays, most played with first. They
// are what lets the page say "these are the people you played with ten minutes ago" instead of
// asking for a path, so a stranger from one fill must not get in: it would both clutter the
// list of who is left to invite and vouch for a squad that is not yours.
List<SquadMate> GetRecentMates(string strEpicId, out string? strYourName)
{
    var dicCounted = new Dictionary<string, SquadMate>(StringComparer.OrdinalIgnoreCase);
    string? strFound = null;

    foreach (var objRecord in objStore.GetMatches().OrderByDescending(objItem => objItem.StartedAt).Take(INT_RECENT_MATCHES))
    {
        var strOwner = string.IsNullOrEmpty(objRecord.DetectedOwnerId) ? strEpicId : objRecord.DetectedOwnerId;
        var objYou = objRecord.Players.FirstOrDefault(objPlayer => string.Equals(objPlayer.Id, strOwner, StringComparison.OrdinalIgnoreCase));
        strFound ??= objYou?.Name;
        if (objYou?.Team == null)
        {
            continue;
        }

        foreach (var objPlayer in objRecord.Players)
        {
            if (objPlayer.Team != objYou.Team
                || string.IsNullOrEmpty(objPlayer.Id)
                || string.Equals(objPlayer.Id, strOwner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!dicCounted.TryGetValue(objPlayer.Id, out var objMate))
            {
                objMate = new SquadMate { Id = objPlayer.Id, PlayedWithYou = true };
                dicCounted[objPlayer.Id] = objMate;
            }

            objMate.Together++;
            objMate.Name ??= objPlayer.Name;
        }
    }

    strYourName = strFound;
    return dicCounted.Values
        .Where(objMate => objMate.Together >= INT_REGULAR_MATES)
        .OrderByDescending(objMate => objMate.Together)
        .ToList();
}

SquadView BuildSquadView()
{
    var objSettings = objStore.Settings;
    var strEpicId = objSettings.EpicId;
    var lstMates = GetRecentMates(strEpicId, out var strYourName);
    var setMates = lstMates.Select(objMate => objMate.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

    var objView = new SquadView
    {
        EpicId = strEpicId,
        YourName = strYourName,
        RecentMates = lstMates
    };

    // Without an Epic id there is no file name to write: the page says so rather than guessing
    if (string.IsNullOrEmpty(strEpicId))
    {
        objView.Status = "noepic";
        return objView;
    }

    foreach (var strPath in objSettings.SquadPaths)
    {
        var objGroup = new SquadGroup { Path = strPath, Name = Path.GetFileName(strPath.TrimEnd(Path.DirectorySeparatorChar)) };

        if (!Directory.Exists(strPath))
        {
            objGroup.Status = "missing";
        }
        else
        {
            objGroup.Members = Squad.ReadMembers(strPath, strEpicId, setMates);

            // Our own file sitting there is proof enough that this folder is ours to write in,
            // and this runs every few seconds while the settings are open: probing each time
            // would mean a write into a synced folder, with the noise reaching the whole squad.
            objGroup.Status = objGroup.Members.Any(objMember => objMember.IsYou)
                ? "ok"
                : Squad.Check(strPath, strEpicId) ?? "ok";
        }

        objView.Squads.Add(objGroup);
    }

    // Squads already on this disk that are not joined yet: the invitation that has arrived but
    // has not been acted on. Looking costs disk, so it stops once there is no room for more.
    if (objView.Squads.Count < TrackerSettings.MAX_SQUADS)
    {
        objView.Roots = Squad.DetectRoots();
        objView.Found = Squad.FindGroups(objView.Roots, strEpicId, setMates)
            .Where(objFound => !objSettings.SquadPaths.Contains(objFound.Path, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    return objView;
}

// Searching the cloud folders costs a little, and the page asks again on every refresh while
// the settings are open: an answer is reused for a few seconds.
SquadView? objSquadCache = null;
var dtSquadCacheUtc = DateTime.MinValue;

void ForgetSquadCache() => objSquadCache = null;

// fresh=true looks at the disk again right now. The page asks for it while somebody is waiting
// for a shared folder to come down, where a cached answer of "not here" would look like a dead
// button even once the cloud had finished.
objApp.MapGet("/api/squad", (bool? fresh) =>
{
    if (fresh != true && objSquadCache != null && DateTime.UtcNow - dtSquadCacheUtc < TimeSpan.FromSeconds(8))
    {
        return objSquadCache;
    }

    objSquadCache = BuildSquadView();
    dtSquadCacheUtc = DateTime.UtcNow;
    return objSquadCache;
});

// Every path that arrives from the page is tidied the same way before it is compared, saved or
// shown. Without this, the same folder written with the other slash counts as a different squad.
string? TidyPath(string? strPath)
{
    if (string.IsNullOrWhiteSpace(strPath))
    {
        return null;
    }

    try { return Path.GetFullPath(strPath.Trim().Trim('"')); }
    catch (Exception) { return null; }
}

// Adds a folder to the squads, writing this player's file into it. That write is also the only
// honest test of access: a folder shared to view instead of to edit fails here and nowhere else.
IResult JoinSquad(string strPath)
{
    var objSettings = objStore.Settings;
    var strEpicId = objSettings.EpicId;

    if (string.IsNullOrEmpty(strEpicId))
    {
        return Results.BadRequest(new { error = "Aún no se sabe tu ID de Epic: juega una partida y vuelve a intentarlo." });
    }

    // Typed by hand or taken from a search, the path is tidied before it is shown and saved
    if (TidyPath(strPath) is not { } strFolder || !Directory.Exists(strFolder))
    {
        return Results.BadRequest(new { error = "Esa carpeta no existe o todavía no se ha sincronizado." });
    }

    if (objSettings.SquadPaths.Contains(strFolder, StringComparer.OrdinalIgnoreCase))
    {
        return Results.Ok(new { path = strFolder });     // Already in: nothing to do, and no error either
    }

    if (objSettings.SquadPaths.Count >= TrackerSettings.MAX_SQUADS)
    {
        return Results.BadRequest(new { error = $"Ya estás en {TrackerSettings.MAX_SQUADS} escuadrones, que es el máximo. Sal de uno para entrar en otro." });
    }

    try
    {
        GetRecentMates(strEpicId, out var strYourName);
        Squad.Join(strFolder, strEpicId, strYourName);
        objStore.UpdateSettings(objChange => objChange.SquadPaths.Add(strFolder));
        objStore.Log($"Entrado en el escuadrón {strFolder}");
        ForgetSquadCache();
        return Results.Ok(new { path = strFolder });
    }
    catch (UnauthorizedAccessException)
    {
        // Shared to view instead of to edit: the single mistake this is most likely to meet
        return Results.BadRequest(new { error = "Te han compartido la carpeta en solo lectura. Pide que te la compartan con permiso de edición." });
    }
    catch (Exception objError)
    {
        objStore.Log($"No se pudo entrar en el escuadrón {strFolder}: {objError.Message}");
        return Results.BadRequest(new { error = "No se pudo entrar en ese escuadrón.", detail = objError.Message });
    }
}

objApp.MapPost("/api/squad/join", (string path) => JoinSquad(path));

objApp.MapPost("/api/squad/create", (string root) =>
{
    var objSettings = objStore.Settings;
    var strEpicId = objSettings.EpicId;

    if (string.IsNullOrEmpty(strEpicId))
    {
        return Results.BadRequest(new { error = "Aún no se sabe tu ID de Epic: juega una partida y vuelve a intentarlo." });
    }

    if (objSettings.SquadPaths.Count >= TrackerSettings.MAX_SQUADS)
    {
        return Results.BadRequest(new { error = $"Ya estás en {TrackerSettings.MAX_SQUADS} escuadrones, que es el máximo. Sal de uno para entrar en otro." });
    }

    try
    {
        GetRecentMates(strEpicId, out var strYourName);
        var strFolder = Squad.Create(root, strEpicId, strYourName);
        objStore.UpdateSettings(objChange =>
        {
            if (!objChange.SquadPaths.Contains(strFolder, StringComparer.OrdinalIgnoreCase))
            {
                objChange.SquadPaths.Add(strFolder);
            }
        });
        objStore.Log($"Escuadrón creado en {strFolder}");
        ForgetSquadCache();
        return Results.Ok(new { path = strFolder });
    }
    catch (Exception objError)
    {
        objStore.Log($"No se pudo crear el escuadrón en {root}: {objError.Message}");
        return Results.BadRequest(new { error = "No se pudo crear el escuadrón en esa carpeta.", detail = objError.Message });
    }
});

// Leaving only stops using the folder. Nothing in it is deleted: the rest of the squad keeps
// their copy of what was shared, and joining again later finds it untouched.
objApp.MapPost("/api/squad/leave", (string path) =>
{
    var strFolder = TidyPath(path) ?? path;
    objStore.UpdateSettings(objSettings => objSettings.SquadPaths.RemoveAll(strItem => string.Equals(strItem, strFolder, StringComparison.OrdinalIgnoreCase)));
    objStore.Log($"Salida del escuadrón {strFolder}");
    ForgetSquadCache();
    return Results.Ok();
});

objApp.MapPost("/api/squad/open", (string path) =>
{
    if (TidyPath(path) is not { } strFolder || !Directory.Exists(strFolder))
    {
        return Results.BadRequest(new { error = "No hay ninguna carpeta de escuadrón que abrir." });
    }

    Squad.OpenInExplorer(strFolder);
    return Results.Ok();
});

// Opens the invitation the squad sent, or the cloud's own website, in the reader's browser.
// The link is never read for data: accepting it is what makes the cloud put the folder on this
// disk, and only then does the search find it. Only http(s) is opened, because this takes a
// string typed into the page and hands it to the shell.
objApp.MapPost("/api/squad/browse", (string url) =>
{
    if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var objUri)
        || (objUri.Scheme != Uri.UriSchemeHttp && objUri.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "Eso no parece un enlace. Pega el que te ha pasado tu squad, que empieza por https://" });
    }

    OpenInBrowser(objUri.AbsoluteUri);
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

// The matches were missing and came back from a copy: say so where it can be read afterwards
if (objStore.TakeRecoveryNote() is { } strRecovery)
{
    objStore.Log(strRecovery);
}

// Where a second copy opened by mistake has to look
Instance.Write(strDataFolder, strUrl);
objApp.Lifetime.ApplicationStopped.Register(() => Instance.Remove(strDataFolder));

objApp.Lifetime.ApplicationStarted.Register(() =>
    _ = objApp.Services.GetRequiredService<SeasonMap>().RefreshAsync(objApp.Lifetime.ApplicationStopping));

// Only for a reader who has opened the shop tab before: it brings nothing otherwise
objApp.Lifetime.ApplicationStarted.Register(() =>
    _ = objApp.Services.GetRequiredService<Shop>().WarmAsync(objApp.Lifetime.ApplicationStopping));

if (!args.Contains("--no-browser"))
{
    objApp.Lifetime.ApplicationStarted.Register(() => OpenInBrowser(strUrl));
}

// A WinExe has no console, so an exception here would kill the program with no window, no
// message and nothing on screen: the reader would double-click the icon and see nothing happen
// at all. Whatever goes wrong, it gets said out loud and written down.
try
{
    objApp.Run();
}
catch (Exception objError)
{
    objStore.Log($"El programa no pudo arrancar: {objError}");
    Native.ShowStartupFailure(objError.Message, Path.Combine(strDataFolder, "chestcounter.log"));
    return 1;
}

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

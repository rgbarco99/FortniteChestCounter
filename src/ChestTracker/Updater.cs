using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// Keeps the program up to date from the GitHub releases of its repository: checks for a newer
/// version, and on request downloads the new executable, swaps it for the running one and restarts.
/// Only a published build knows its repository (the release workflow writes it in); a build made
/// by hand never updates itself.
/// </summary>
public sealed class Updater : BackgroundService
{
    public const string EXE_NAME = "FortniteChestCounter.exe";
    private static readonly TimeSpan tsCheckInterval = TimeSpan.FromHours(4);

    private readonly IHostApplicationLifetime _objLifetime;
    private readonly Store _objStore;
    private readonly HttpClient _objHttp;
    private readonly SemaphoreSlim _objCheckNow = new(0, 1);
    private readonly object _objLock = new();
    private string? _strDownloadUrl;
    private string? _strSha256;
    private long _lngSize;

    public UpdateInfo Info { get; private set; }

    /// <summary>Port the page is served on; the new version starts on the same one so the open tab reconnects.</summary>
    public int Port { get; set; }

    public Updater(IHostApplicationLifetime objLifetime, Store objStore)
    {
        _objLifetime = objLifetime;
        _objStore = objStore;
        _objHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _objHttp.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FortniteChestCounter", CurrentVersion));
        _objHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var strRepository = Environment.GetEnvironmentVariable("FCC_UPDATE_REPO")
            ?? Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(objMeta => objMeta.Key == "UpdateRepository")?.Value;
        var strExe = Environment.ProcessPath ?? "";
        var bIsOwnExe = Path.GetFileNameWithoutExtension(strExe).Equals(Path.GetFileNameWithoutExtension(EXE_NAME), StringComparison.OrdinalIgnoreCase);

        Info = new UpdateInfo
        {
            CurrentVersion = CurrentVersion,
            Repository = string.IsNullOrWhiteSpace(strRepository) ? null : strRepository,
            Enabled = !string.IsNullOrWhiteSpace(strRepository) && bIsOwnExe
        };
    }

    public static string CurrentVersion
    {
        get
        {
            var strVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var intPlus = strVersion.IndexOf('+');
            return intPlus >= 0 ? strVersion[..intPlus] : strVersion;
        }
    }

    public void RequestCheck()
    {
        try { _objCheckNow.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RemoveLeftovers();
        if (!Info.Enabled)
        {
            return;
        }

        // Let the program start in peace before the first check
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(stoppingToken);
            try { await _objCheckNow.WaitAsync(tsCheckInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(CancellationToken objCancel)
    {
        if (Info.State is "downloading" or "restarting")
        {
            return;
        }

        try
        {
            var strApi = Environment.GetEnvironmentVariable("FCC_UPDATE_API") ?? "https://api.github.com";
            using var objResponse = await _objHttp.GetAsync($"{strApi}/repos/{Info.Repository}/releases/latest", objCancel);
            if (!objResponse.IsSuccessStatusCode)
            {
                // No release published yet, or GitHub is limiting requests: try again later, quietly
                SetInfo(Info with { CheckedUtc = DateTime.UtcNow });
                return;
            }

            using var objJson = JsonDocument.Parse(await objResponse.Content.ReadAsStringAsync(objCancel));
            var objRoot = objJson.RootElement;
            var strTag = objRoot.GetProperty("tag_name").GetString() ?? "";
            var strLatest = strTag.TrimStart('v', 'V');

            string? strDownload = null;
            string? strDigest = null;
            long lngSize = 0;
            foreach (var objAsset in objRoot.GetProperty("assets").EnumerateArray())
            {
                if (string.Equals(objAsset.GetProperty("name").GetString(), EXE_NAME, StringComparison.OrdinalIgnoreCase))
                {
                    strDownload = objAsset.GetProperty("browser_download_url").GetString();
                    lngSize = objAsset.TryGetProperty("size", out var objSize) ? objSize.GetInt64() : 0;
                    // GitHub publishes a SHA-256 for each asset; when present the download is checked against it
                    if (objAsset.TryGetProperty("digest", out var objDigest) && objDigest.GetString() is { } strValue && strValue.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    {
                        strDigest = strValue[7..];
                    }
                }
            }

            lock (_objLock)
            {
                _strDownloadUrl = strDownload;
                _strSha256 = strDigest;
                _lngSize = lngSize;
            }

            var bNewer = strDownload != null && IsNewer(strLatest, Info.CurrentVersion);
            SetInfo(Info with
            {
                LatestVersion = strLatest,
                Available = bNewer,
                Notes = objRoot.TryGetProperty("body", out var objBody) ? objBody.GetString() : null,
                ReleaseUrl = objRoot.TryGetProperty("html_url", out var objUrl) ? objUrl.GetString() : null,
                CheckedUtc = DateTime.UtcNow,
                State = Info.State == "error" ? "idle" : Info.State
            });
        }
        catch (Exception objError) when (objError is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Offline or an unexpected answer: nothing to offer this time
            _objStore.Log($"Update check failed: {objError.Message}");
            SetInfo(Info with { CheckedUtc = DateTime.UtcNow });
        }
    }

    /// <summary>Downloads the new executable, puts it in place of the running one and restarts. Runs in the background.</summary>
    public bool StartApply()
    {
        string? strUrl;
        lock (_objLock)
        {
            if (!Info.Enabled || !Info.Available || Info.State is "downloading" or "restarting")
            {
                return false;
            }
            strUrl = _strDownloadUrl;
            Info = Info with { State = "downloading", Progress = 0, Error = null };
        }
        _objStore.Touch();

        _ = Task.Run(async () =>
        {
            try
            {
                await ApplyAsync(strUrl!);
            }
            catch (Exception objError)
            {
                _objStore.Log($"Update failed: {objError}");
                SetInfo(Info with { State = "error", Error = objError.Message });
            }
        });
        return true;
    }

    private async Task ApplyAsync(string strUrl)
    {
        var strExe = Environment.ProcessPath!;
        var strNew = strExe + ".new";
        var strOld = strExe + ".old";

        using (var objResponse = await _objHttp.GetAsync(strUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            objResponse.EnsureSuccessStatusCode();
            var lngTotal = objResponse.Content.Headers.ContentLength ?? _lngSize;
            await using var objSource = await objResponse.Content.ReadAsStreamAsync();
            await using var objTarget = new FileStream(strNew, FileMode.Create, FileAccess.Write, FileShare.None);
            var arrBuffer = new byte[1 << 16];
            long lngDone = 0;
            int intRead;
            var dtLastReport = DateTime.MinValue;
            while ((intRead = await objSource.ReadAsync(arrBuffer)) > 0)
            {
                await objTarget.WriteAsync(arrBuffer.AsMemory(0, intRead));
                lngDone += intRead;
                if (lngTotal > 0 && DateTime.UtcNow - dtLastReport > TimeSpan.FromMilliseconds(400))
                {
                    dtLastReport = DateTime.UtcNow;
                    SetInfo(Info with { Progress = Math.Min(1, (double)lngDone / lngTotal) });
                }
            }
        }

        // Make sure it is the file GitHub published before replacing anything
        if (_lngSize > 0 && new FileInfo(strNew).Length != _lngSize)
        {
            File.Delete(strNew);
            throw new InvalidDataException("The download is incomplete.");
        }
        if (_strSha256 != null)
        {
            await using var objCheck = File.OpenRead(strNew);
            var strHash = Convert.ToHexString(await SHA256.HashDataAsync(objCheck));
            if (!strHash.Equals(_strSha256, StringComparison.OrdinalIgnoreCase))
            {
                objCheck.Close();
                File.Delete(strNew);
                throw new InvalidDataException("The download does not match the published file.");
            }
        }

        // Windows lets a running executable be renamed, not overwritten: move it aside, put the
        // new one in its place. The copy set aside by a previous update can still be held open
        // by something (an orphaned reader, an antivirus mid-scan), and a leftover nobody can
        // delete is no reason to refuse an update: when it will not go, the old copy is simply
        // parked under another name and swept up later.
        if (File.Exists(strOld))
        {
            try
            {
                File.Delete(strOld);
            }
            catch (Exception objError)
            {
                strOld = $"{strExe}.old.{DateTime.UtcNow:yyyyMMddHHmmss}";
                _objStore.Log($"No se pudo borrar la copia anterior ({objError.Message}); se aparta como {Path.GetFileName(strOld)}");
            }
        }

        File.Move(strExe, strOld);
        try
        {
            File.Move(strNew, strExe);
        }
        catch
        {
            File.Move(strOld, strExe);
            throw;
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(strExe, File.GetUnixFileMode(strOld));
        }

        SetInfo(Info with { State = "restarting", Progress = 1 });

        // Same arguments, same port, no new browser tab: the page that is open reconnects by itself
        var lstArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
        RemoveOption(lstArgs, "--port", true);
        RemoveOption(lstArgs, "--wait-pid", true);
        RemoveOption(lstArgs, "--no-browser", false);
        lstArgs.AddRange(["--port", Port.ToString(), "--wait-pid", Environment.ProcessId.ToString(), "--no-browser"]);
        var objStart = new ProcessStartInfo(strExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(strExe)! };
        lstArgs.ForEach(objStart.ArgumentList.Add);
        Process.Start(objStart);

        // Give the page a moment to see "restarting", then make room for the new version
        await Task.Delay(1500);
        _objLifetime.StopApplication();
    }

    private static void RemoveOption(List<string> lstArgs, string strName, bool bHasValue)
    {
        var intIndex = lstArgs.IndexOf(strName);
        if (intIndex >= 0)
        {
            lstArgs.RemoveRange(intIndex, bHasValue && intIndex + 1 < lstArgs.Count ? 2 : 1);
        }
    }

    /// <summary>The previous executable, left aside by the last update, can go once it is no longer running.</summary>
    private static void RemoveLeftovers()
    {
        var strExe = Environment.ProcessPath;
        if (strExe == null)
        {
            return;
        }

        // Includes the ones parked under a dated name when the plain .old could not be deleted:
        // whatever was holding them is usually long gone by the next start
        var lstLeftovers = new List<string> { strExe + ".new" };
        try
        {
            var strFolder = Path.GetDirectoryName(strExe);
            if (strFolder != null)
            {
                lstLeftovers.AddRange(Directory.EnumerateFiles(strFolder, Path.GetFileName(strExe) + ".old*"));
            }
        }
        catch (Exception) { lstLeftovers.Add(strExe + ".old"); }

        foreach (var strLeftover in lstLeftovers)
        {
            for (var intTry = 0; intTry < 10 && File.Exists(strLeftover); intTry++)
            {
                try { File.Delete(strLeftover); }
                catch (Exception) { Thread.Sleep(500); }
            }
        }
    }

    /// <summary>Waits for the previous copy of the program to close (it hands over its port during an update).</summary>
    public static void WaitForPreviousCopy(string? strPid)
    {
        if (!int.TryParse(strPid, out var intPid))
        {
            return;
        }

        try
        {
            using var objPrevious = Process.GetProcessById(intPid);
            objPrevious.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // Already gone
        }
    }

    private static bool IsNewer(string strCandidate, string strCurrent)
    {
        static Version? Parse(string strText)
        {
            var strCore = strText.Split('-', '+')[0];
            return Version.TryParse(strCore.Count(chr => chr == '.') == 0 ? strCore + ".0" : strCore, out var objVersion) ? objVersion : null;
        }

        var objCandidate = Parse(strCandidate);
        var objCurrent = Parse(strCurrent);
        return objCandidate != null && objCurrent != null && objCandidate > objCurrent;
    }

    private void SetInfo(UpdateInfo objInfo)
    {
        lock (_objLock)
        {
            Info = objInfo;
        }
        _objStore.Touch();
    }
}

public sealed record UpdateInfo
{
    public string CurrentVersion { get; init; } = "0.0.0";
    public string? Repository { get; init; }

    /// <summary>False for builds that do not know their repository, or when not running as the published executable.</summary>
    public bool Enabled { get; init; }

    public bool Available { get; init; }
    public string? LatestVersion { get; init; }
    public string? Notes { get; init; }
    public string? ReleaseUrl { get; init; }
    public DateTime? CheckedUtc { get; init; }

    /// <summary>"idle", "downloading", "restarting" or "error".</summary>
    public string State { get; init; } = "idle";
    public double Progress { get; init; }
    public string? Error { get; init; }
}

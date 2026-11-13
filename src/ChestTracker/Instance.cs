using System.Diagnostics;
using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// Only one copy at a time. The running copy leaves its process id and its address in
/// instance.json, in the data folder; a second one opened by mistake finds it, opens that
/// dashboard in the browser and closes. Without this, and with no window to notice, copies
/// would pile up invisibly on 5381, 5382... all reading the same folder.
/// </summary>
public static class Instance
{
    private sealed class InstanceFile
    {
        public int Pid { get; set; }
        public string Url { get; set; } = "";
    }

    private static string GetPath(string strDataFolder) => Path.Combine(strDataFolder, "instance.json");

    /// <summary>
    /// The address of the copy already running, or null when there is none. A stale file (the
    /// program was killed, or the id now belongs to something else) answers null: the address is
    /// only trusted once this program's own API has answered on it.
    /// </summary>
    public static string? FindRunning(string strDataFolder)
    {
        try
        {
            var strPath = GetPath(strDataFolder);
            if (!File.Exists(strPath))
            {
                return null;
            }

            var objFile = JsonSerializer.Deserialize<InstanceFile>(File.ReadAllText(strPath));
            if (objFile == null || objFile.Pid <= 0 || string.IsNullOrEmpty(objFile.Url) || objFile.Pid == Environment.ProcessId)
            {
                return null;
            }

            try { using var objProcess = Process.GetProcessById(objFile.Pid); }
            catch (ArgumentException) { return null; }     // Nothing is running under that id

            return Answers(objFile.Url) ? objFile.Url : null;
        }
        catch (Exception)
        {
            // A file that cannot be read says nothing: carry on and start normally
            return null;
        }
    }

    /// <summary>Is that address this program, and not something else that took the port?</summary>
    private static bool Answers(string strUrl)
    {
        try
        {
            using var objClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var strBody = objClient.GetStringAsync($"{strUrl.TrimEnd('/')}/api/revision").GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<JsonElement>(strBody).TryGetProperty("revision", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Write(string strDataFolder, string strUrl)
    {
        try
        {
            Directory.CreateDirectory(strDataFolder);
            File.WriteAllText(GetPath(strDataFolder), JsonSerializer.Serialize(new InstanceFile { Pid = Environment.ProcessId, Url = strUrl }));
        }
        catch (IOException) { /* Then a second copy just starts on the next port, as it used to */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// On the way out, and only if the file still points at this copy: right after an update the
    /// new version has already written its own, and the old one must not take it away.
    /// </summary>
    public static void Remove(string strDataFolder)
    {
        try
        {
            var strPath = GetPath(strDataFolder);
            if (!File.Exists(strPath))
            {
                return;
            }

            var objFile = JsonSerializer.Deserialize<InstanceFile>(File.ReadAllText(strPath));
            if (objFile == null || objFile.Pid == Environment.ProcessId)
            {
                File.Delete(strPath);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
    }
}

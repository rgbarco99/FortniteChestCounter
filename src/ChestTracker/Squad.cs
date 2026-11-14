using System.Diagnostics;
using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// The squad group: one folder in OneDrive, Drive or Dropbox that the four players share, so
/// each of them can see what the others opened. A replay only records the chests opened within
/// about 300 m of its owner, so the whole match exists only once the four files are put together.
///
/// The design rests on one rule: one writer per file. Everyone writes only
/// &lt;their epic id&gt;.json and never touches anyone else's. That is what lets this work with no
/// server, no accounts and no locking, and it sidesteps the single thing OneDrive and Drive
/// handle badly, which is two people editing the same file at once.
///
/// This file covers step one: finding the folder, creating it and joining it. Writing the match
/// cards and reading other people's comes next; the presence file written on joining already
/// carries the empty list waiting for them.
/// </summary>
public static class Squad
{
    /// <summary>
    /// Name of the shared folder. OneDrive puts a folder shared with someone under their own
    /// root with this same name, which is why nobody on the receiving end has to type a path.
    /// </summary>
    public const string STR_FOLDER_NAME = "FortniteChestCounter";

    /// <summary>Marks a folder as a group, so finding one is unambiguous and cheap.</summary>
    private const string STR_MARKER = "grupo.json";

    private const string STR_APP = "FortniteChestCounter";

    /// <summary>A cloud root can hold anything, so this caps what a search is allowed to cost.</summary>
    private const int INT_MAX_FOLDERS = 600;

    /// <summary>
    /// Keeps OneDrive from leaving the group as an on-demand placeholder that cannot be read
    /// offline. Unknown to the FileAttributes enum, so it goes in by number.
    /// </summary>
    private const FileAttributes FILE_ATTRIBUTE_PINNED = (FileAttributes)0x00080000;

    private static readonly JsonSerializerOptions objJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // -----------------------------------------------------------------------
    // Finding the cloud
    // -----------------------------------------------------------------------

    /// <summary>The cloud folders of this PC, as roots a group can live in.</summary>
    public static List<CloudRoot> DetectRoots()
    {
        var lstRoots = new List<CloudRoot>();

        void Add(string strProvider, string? strPath)
        {
            if (string.IsNullOrWhiteSpace(strPath))
            {
                return;
            }

            try
            {
                var strFull = Path.GetFullPath(strPath);
                if (!Directory.Exists(strFull)) return;
                if (lstRoots.Any(objRoot => string.Equals(objRoot.Path, strFull, StringComparison.OrdinalIgnoreCase))) return;
                lstRoots.Add(new CloudRoot { Provider = strProvider, Path = strFull });
            }
            catch (Exception) { /* An unusable path is simply not offered */ }
        }

        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveCommercial"));

        // Dropbox writes down where it put its folder: one entry per linked account
        try
        {
            var strInfo = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dropbox", "info.json");
            if (File.Exists(strInfo))
            {
                using var objDoc = JsonDocument.Parse(File.ReadAllText(strInfo));
                foreach (var objAccount in objDoc.RootElement.EnumerateObject())
                {
                    if (objAccount.Value.TryGetProperty("path", out var objPath))
                    {
                        Add("Dropbox", objPath.GetString());
                    }
                }
            }
        }
        catch (Exception) { /* No Dropbox, or a file this version does not understand */ }

        // Google Drive mounts a drive letter, and its label is the only reliable way to tell which
        try
        {
            foreach (var objDrive in DriveInfo.GetDrives())
            {
                string strLabel;
                try { strLabel = objDrive.IsReady ? objDrive.VolumeLabel : ""; }
                catch (Exception) { continue; }

                if (!strLabel.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)) continue;

                var strEnglish = Path.Combine(objDrive.RootDirectory.FullName, "My Drive");
                var strSpanish = Path.Combine(objDrive.RootDirectory.FullName, "Mi unidad");
                Add("Google Drive",
                    Directory.Exists(strEnglish) ? strEnglish
                    : Directory.Exists(strSpanish) ? strSpanish
                    : objDrive.RootDirectory.FullName);
            }
        }
        catch (Exception) { /* No Drive, or drives that cannot be listed */ }

        return lstRoots;
    }

    /// <summary>
    /// Groups already sitting in this PC's cloud folders. Looks at each root and two levels
    /// down, which is where a shared folder lands however the player accepted it.
    /// </summary>
    public static List<GroupFound> FindGroups(List<CloudRoot> lstRoots, string strEpicId, ISet<string> setMates)
    {
        var lstFound = new List<GroupFound>();
        var intBudget = INT_MAX_FOLDERS;

        void Look(string strFolder, CloudRoot objRoot, int intDepth)
        {
            if (intBudget-- <= 0)
            {
                return;
            }

            try
            {
                if (File.Exists(Path.Combine(strFolder, STR_MARKER)) && IsOurs(strFolder))
                {
                    if (!lstFound.Any(objFound => string.Equals(objFound.Path, strFolder, StringComparison.OrdinalIgnoreCase)))
                    {
                        lstFound.Add(new GroupFound
                        {
                            Path = strFolder,
                            Provider = objRoot.Provider,
                            Members = ReadMembers(strFolder, strEpicId, setMates)
                        });
                    }

                    return;     // A group holds no groups: stop going down this branch
                }

                if (intDepth >= 2)
                {
                    return;
                }

                foreach (var strChild in Directory.EnumerateDirectories(strFolder))
                {
                    Look(strChild, objRoot, intDepth + 1);
                }
            }
            catch (Exception) { /* A folder that cannot be read is skipped, never fatal */ }
        }

        foreach (var objRoot in lstRoots)
        {
            Look(objRoot.Path, objRoot, 0);
        }

        foreach (var objFound in lstFound)
        {
            objFound.KnownMates = objFound.Members.Count(objMember => objMember.PlayedWithYou);
        }

        // The one with the most familiar faces first: that is the one the player means
        return lstFound
            .OrderByDescending(objFound => objFound.KnownMates)
            .ThenBy(objFound => objFound.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A marker this program wrote, rather than any file that happens to share the name.</summary>
    private static bool IsOurs(string strFolder)
    {
        try
        {
            using var objDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(strFolder, STR_MARKER)));
            return objDoc.RootElement.TryGetProperty("app", out var objApp)
                && string.Equals(objApp.GetString(), STR_APP, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Reading who is in
    // -----------------------------------------------------------------------

    /// <summary>
    /// The players with a file in the folder. A file is only believed when its name is an Epic
    /// id and the contents claim that same id: that is what keeps a stray or renamed file out of
    /// the squad, and with it the copies OneDrive leaves behind when it hits a conflict.
    /// </summary>
    public static List<SquadMate> ReadMembers(string strFolder, string strEpicId, ISet<string> setMates)
    {
        var lstMembers = new List<SquadMate>();

        try
        {
            foreach (var strFile in Directory.EnumerateFiles(strFolder, "*.json"))
            {
                var strName = Path.GetFileNameWithoutExtension(strFile);
                if (!IsEpicId(strName))
                {
                    continue;
                }

                try
                {
                    var objFile = JsonSerializer.Deserialize<SquadFile>(File.ReadAllText(strFile), objJson);
                    if (objFile == null || !string.Equals(objFile.PlayerId, strName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    lstMembers.Add(new SquadMate
                    {
                        Id = objFile.PlayerId,
                        Name = objFile.Name,
                        IsYou = string.Equals(objFile.PlayerId, strEpicId, StringComparison.OrdinalIgnoreCase),
                        PlayedWithYou = setMates.Contains(objFile.PlayerId),
                        UpdatedUtc = objFile.UpdatedUtc,
                        Matches = objFile.Cards.Count
                    });
                }
                catch (Exception) { /* Half synchronised or damaged: skipped now, read again next pass */ }
            }
        }
        catch (Exception) { /* The folder went away mid-read */ }

        return lstMembers
            .OrderByDescending(objMember => objMember.IsYou)
            .ThenBy(objMember => objMember.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsEpicId(string strText) =>
        strText.Length == 32 && strText.All(chrItem =>
            (chrItem >= '0' && chrItem <= '9') || (chrItem >= 'a' && chrItem <= 'f') || (chrItem >= 'A' && chrItem <= 'F'));

    // -----------------------------------------------------------------------
    // Creating and joining
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates the group inside a cloud root and joins it. Sharing the folder afterwards is the
    /// one step this cannot do: that belongs to OneDrive, and doing it here would mean accounts.
    /// </summary>
    public static string Create(string strRoot, string strEpicId, string? strName)
    {
        // Tidied here rather than at the caller, so the path the player is shown and the one
        // that gets saved are the same however the root arrived
        var strFolder = Path.GetFullPath(Path.Combine(strRoot, STR_FOLDER_NAME));
        Directory.CreateDirectory(strFolder);

        var strMarker = Path.Combine(strFolder, STR_MARKER);
        if (!File.Exists(strMarker))
        {
            WriteAtomic(strMarker, JsonSerializer.Serialize(new
            {
                app = STR_APP,
                version = 1,
                createdUtc = DateTime.UtcNow,
                createdBy = strEpicId,
                createdByName = strName
            }, objJson));
        }

        KeepOffline(strFolder);
        Join(strFolder, strEpicId, strName);
        return strFolder;
    }

    /// <summary>
    /// Announces this player in the folder, keeping whatever their file already held. Joining is
    /// also the only honest test of write access, so it is what tells a folder shared to edit
    /// from one shared to view.
    /// </summary>
    public static void Join(string strFolder, string strEpicId, string? strName)
    {
        var strFile = Path.Combine(strFolder, $"{strEpicId}.json");
        var objFile = new SquadFile { PlayerId = strEpicId };

        try
        {
            if (File.Exists(strFile))
            {
                // Rejoining after leaving must not throw away what was already shared
                objFile = JsonSerializer.Deserialize<SquadFile>(File.ReadAllText(strFile), objJson) ?? objFile;
            }
        }
        catch (Exception) { /* Unreadable: it is replaced by a fresh one */ }

        objFile.PlayerId = strEpicId;
        objFile.Name = strName ?? objFile.Name;
        objFile.UpdatedUtc = DateTime.UtcNow;

        WriteMine(strFolder, objFile);
    }

    /// <summary>
    /// Replaces this player's file with the whole of what they share. The list is always written
    /// complete rather than appended to: a pass that was missed, a match read late and a file
    /// that came back damaged all repair themselves on the next write.
    /// </summary>
    public static void WriteMine(string strFolder, SquadFile objFile)
    {
        WriteAtomic(Path.Combine(strFolder, $"{objFile.PlayerId}.json"), JsonSerializer.Serialize(objFile, objJson));
    }

    /// <summary>This player's own file as it stands, for adding to without losing what is there.</summary>
    public static SquadFile? ReadMine(string strFolder, string strEpicId)
    {
        try
        {
            var strFile = Path.Combine(strFolder, $"{strEpicId}.json");
            return File.Exists(strFile) ? JsonSerializer.Deserialize<SquadFile>(File.ReadAllText(strFile), objJson) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// What is wrong with a folder, as one of the words the page knows: "missing", "readonly"
    /// or "error". Null when nothing is wrong.
    /// </summary>
    public static string? Check(string strFolder, string strEpicId)
    {
        if (!Directory.Exists(strFolder))
        {
            return "missing";
        }

        try
        {
            var strProbe = Path.Combine(strFolder, $".{strEpicId}.tmp");
            File.WriteAllText(strProbe, "x");
            File.Delete(strProbe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "readonly";
        }
        catch (Exception)
        {
            return "error";
        }
    }

    /// <summary>Shows the folder to the player, so they can share it from Explorer.</summary>
    public static void OpenInExplorer(string strFolder)
    {
        try { Process.Start(new ProcessStartInfo(strFolder) { UseShellExecute = true }); }
        catch (Exception) { /* No shell: the path is on the page anyway */ }
    }

    /// <summary>
    /// Asks OneDrive to keep the folder on this disk. Best effort: where it is not a cloud
    /// folder, or the provider ignores the flag, nothing happens and nothing breaks.
    /// </summary>
    private static void KeepOffline(string strFolder)
    {
        try { File.SetAttributes(strFolder, File.GetAttributes(strFolder) | FILE_ATTRIBUTE_PINNED); }
        catch (Exception) { /* Not a cloud folder, or a provider that ignores the flag */ }
    }

    /// <summary>Through a temporary file, so a half-written file never reaches the other players.</summary>
    private static void WriteAtomic(string strPath, string strContents)
    {
        var strTemp = strPath + ".tmp";
        File.WriteAllText(strTemp, strContents);
        File.Move(strTemp, strPath, overwrite: true);
    }
}

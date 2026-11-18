using System.IO.Compression;
using System.Text;

namespace ChestTracker;

/// <summary>
/// Copies of the saved matches, made by the program itself. A match whose replay Fortnite has
/// already deleted only exists in matches.json: one bad write, one wrong click or one dead disk
/// and it is gone. So a copy of that file is kept beside it, one per day, and the program puts
/// it back by itself if it ever starts and finds its matches missing.
///
/// They are compressed because they are never opened by hand: the program lists them and
/// restores them. Compressed, a copy is worth about a tenth of the file it came from.
/// </summary>
public static class Backup
{
    /// <summary>Days kept. Older copies go, so the folder never grows without end.</summary>
    public const int MAX_COPIES = 10;

    /// <summary>A copy is refreshed at most this often, so a busy evening is not a copy per match.</summary>
    private static readonly TimeSpan tsMinGap = TimeSpan.FromMinutes(30);

    private const string STR_PREFIX = "cofres-";
    private const string STR_SUFFIX = ".json.gz";

    public static string GetFolder(string strDataFolder) => Path.Combine(strDataFolder, "copias");

    /// <summary>
    /// Writes today's copy when there is none, or when the one there has gone stale. Returns true
    /// when a copy was made. Failing is never fatal: a copy that cannot be written is logged and
    /// the program carries on.
    ///
    /// The matches arrive as something to call, not as text: most calls here decide there is
    /// nothing to do, and building that text means serialising every match the reader has ever
    /// played. It is asked for once the decision is made.
    /// </summary>
    public static bool Write(string strDataFolder, Func<string> fnJson, Action<string>? fnLog = null, bool bForce = false)
    {
        try
        {
            var strFolder = GetFolder(strDataFolder);
            Directory.CreateDirectory(strFolder);
            var strPath = Path.Combine(strFolder, $"{STR_PREFIX}{DateTime.Now:yyyy-MM-dd}{STR_SUFFIX}");

            if (!bForce && File.Exists(strPath) && DateTime.Now - File.GetLastWriteTime(strPath) < tsMinGap)
            {
                return false;
            }

            // Through a temporary file, so a copy is never left half written
            var strTemp = strPath + ".tmp";
            using (var objFile = File.Create(strTemp))
            using (var objZip = new GZipStream(objFile, CompressionLevel.Optimal))
            {
                var arrBytes = Encoding.UTF8.GetBytes(fnJson());
                objZip.Write(arrBytes, 0, arrBytes.Length);
            }
            File.Move(strTemp, strPath, overwrite: true);

            Prune(strFolder);
            return true;
        }
        catch (Exception objError)
        {
            fnLog?.Invoke($"No se pudo guardar la copia de seguridad: {objError.Message}");
            return false;
        }
    }

    /// <summary>Newest first.</summary>
    public static List<BackupInfo> List(string strDataFolder)
    {
        try
        {
            var objFolder = new DirectoryInfo(GetFolder(strDataFolder));
            if (!objFolder.Exists)
            {
                return new List<BackupInfo>();
            }

            return objFolder.GetFiles($"{STR_PREFIX}*{STR_SUFFIX}")
                .OrderByDescending(objFile => objFile.Name)
                .Select(objFile => new BackupInfo
                {
                    Name = objFile.Name,
                    Bytes = objFile.Length,
                    WrittenUtc = objFile.LastWriteTimeUtc
                })
                .ToList();
        }
        catch (Exception)
        {
            return new List<BackupInfo>();
        }
    }

    /// <summary>The matches of one copy, as the JSON they were saved from.</summary>
    public static string Read(string strDataFolder, string strName)
    {
        // Only a name this program wrote, never a path: the name arrives from the page
        if (strName != Path.GetFileName(strName) || !strName.StartsWith(STR_PREFIX, StringComparison.Ordinal) || !strName.EndsWith(STR_SUFFIX, StringComparison.Ordinal))
        {
            throw new ArgumentException("Ese no es el nombre de una copia.");
        }

        var strPath = Path.Combine(GetFolder(strDataFolder), strName);
        using var objFile = File.OpenRead(strPath);
        using var objZip = new GZipStream(objFile, CompressionMode.Decompress);
        using var objReader = new StreamReader(objZip, Encoding.UTF8);
        return objReader.ReadToEnd();
    }

    /// <summary>The newest copy, for putting the matches back when the file they live in is gone.</summary>
    public static string? ReadNewest(string strDataFolder)
    {
        var objNewest = List(strDataFolder).FirstOrDefault();
        return objNewest == null ? null : Read(strDataFolder, objNewest.Name);
    }

    private static void Prune(string strFolder)
    {
        foreach (var objOld in new DirectoryInfo(strFolder)
            .GetFiles($"{STR_PREFIX}*{STR_SUFFIX}")
            .OrderByDescending(objFile => objFile.Name)
            .Skip(MAX_COPIES))
        {
            try { objOld.Delete(); }
            catch (IOException) { /* In use: it goes on the next pass */ }
        }
    }
}

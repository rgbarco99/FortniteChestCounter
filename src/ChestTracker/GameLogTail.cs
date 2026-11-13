using System.Globalization;
using System.Text;

namespace ChestTracker;

/// <summary>
/// Follows Fortnite's own log file, which the game writes as it runs, to know where the player
/// is right now: in a match, on the end-of-match screen, or searching for the next match.
/// The replay cannot tell any of this until the match is over; the log can. It says nothing
/// about chests.
/// </summary>
public sealed class GameLogTail
{
    private const string LOG_FILE_NAME = "FortniteGame.log";

    // The lines that matter, as the game writes them
    private const string MARK_RECORDING = "RecordReplay: Starting recording";
    private const string MARK_MATCH_FINISHED = "Match finished for ";
    private const string MARK_LEAVING = ", leaving session";
    private const string MARK_SEARCHING = "Starting matchmaking attempt";
    private const string MARK_SEARCH_CANCELLED = "Cancel Matchmaking";

    // The replay viewer also stops a "demo", but that line carries the replay's name
    private const string MARK_RECORDING_STOPPED = "StopDemo: Demo  stopped";

    private readonly string? _strPathOverride;
    private string? _strPath;
    private long _lngPosition;
    private Encoding _objEncoding = Encoding.UTF8;
    private string _strLeftover = "";

    public GameStatus Status { get; private set; } = new();

    public GameLogTail(string? strPathOverride)
    {
        _strPathOverride = strPathOverride;
    }

    /// <summary>The log lives next to the replay folder: ...\FortniteGame\Saved\Logs\FortniteGame.log</summary>
    public static string GetLogPath(string strDemosPath)
    {
        var strSaved = Path.GetDirectoryName(strDemosPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? strDemosPath;
        return Path.Combine(strSaved, "Logs", LOG_FILE_NAME);
    }

    /// <summary>Reads whatever the game has written since the last call. Returns true when the status changed.</summary>
    public bool Poll(string strDemosPath)
    {
        var strPath = _strPathOverride ?? GetLogPath(strDemosPath);
        if (strPath != _strPath)
        {
            _strPath = strPath;
            Reset();
        }

        var objBefore = Status;
        try
        {
            var objFileInfo = new FileInfo(strPath);
            if (!objFileInfo.Exists)
            {
                Status = new GameStatus();
                return objBefore.LogFound;
            }

            // A shorter file is a new game session: the game starts its log again
            if (objFileInfo.Length < _lngPosition)
            {
                Reset();
            }

            var objStatus = Status with { LogFound = true };
            using (var objFile = new FileStream(strPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (_lngPosition == 0)
                {
                    _objEncoding = DetectEncoding(objFile);
                }

                objFile.Seek(_lngPosition, SeekOrigin.Begin);
                var arrBuffer = new byte[1 << 20];
                int intRead;
                while ((intRead = objFile.Read(arrBuffer, 0, arrBuffer.Length)) > 0)
                {
                    // Two-byte text must be cut on a character boundary
                    if (_objEncoding == Encoding.Unicode && intRead % 2 == 1)
                    {
                        intRead--;
                        objFile.Seek(-1, SeekOrigin.Current);
                        if (intRead == 0) break;
                    }

                    _lngPosition += intRead;
                    var strText = _strLeftover + _objEncoding.GetString(arrBuffer, 0, intRead);
                    var intLineStart = 0;
                    int intLineEnd;
                    while ((intLineEnd = strText.IndexOf('\n', intLineStart)) >= 0)
                    {
                        objStatus = Apply(objStatus, strText.AsSpan(intLineStart, intLineEnd - intLineStart));
                        intLineStart = intLineEnd + 1;
                    }
                    _strLeftover = strText[intLineStart..];
                }
            }

            Status = objStatus;
        }
        catch (IOException)
        {
            // The game holds the file for a moment: try again on the next pass
        }
        catch (UnauthorizedAccessException)
        {
            Status = new GameStatus();
        }

        return Status != objBefore;
    }

    private void Reset()
    {
        _lngPosition = 0;
        _strLeftover = "";
        Status = new GameStatus();
    }

    private static Encoding DetectEncoding(FileStream objFile)
    {
        var arrStart = new byte[2];
        objFile.Seek(0, SeekOrigin.Begin);
        var intRead = objFile.Read(arrStart, 0, 2);
        return intRead == 2 && arrStart[0] == 0xFF && arrStart[1] == 0xFE ? Encoding.Unicode : Encoding.UTF8;
    }

    /// <summary>One log line moves the status forward, or leaves it as it is.</summary>
    private static GameStatus Apply(GameStatus objStatus, ReadOnlySpan<char> spnLine)
    {
        // Cheap filter first: nearly every line of a 60 MB log is something else
        if (!spnLine.Contains("atch", StringComparison.Ordinal) && !spnLine.Contains("LogDemo", StringComparison.Ordinal))
        {
            return objStatus;
        }

        var dtWhen = ReadTimestamp(spnLine);
        if (dtWhen == null)
        {
            return objStatus;
        }

        if (spnLine.Contains(MARK_RECORDING, StringComparison.Ordinal))
        {
            return objStatus with { RecordingSinceUtc = dtWhen, MatchFinishedUtc = null, SearchingSinceUtc = null };
        }

        if (spnLine.Contains(MARK_MATCH_FINISHED, StringComparison.Ordinal))
        {
            // "leaving session" is the way back to the lobby; without it the player stays on the end screen
            var bLeaving = spnLine.Contains(MARK_LEAVING, StringComparison.Ordinal);
            return objStatus with
            {
                MatchFinishedUtc = objStatus.MatchFinishedUtc ?? dtWhen,
                SearchingSinceUtc = bLeaving ? null : objStatus.SearchingSinceUtc
            };
        }

        if (spnLine.Contains(MARK_SEARCHING, StringComparison.Ordinal))
        {
            return objStatus with { SearchingSinceUtc = dtWhen };
        }

        if (spnLine.Contains(MARK_SEARCH_CANCELLED, StringComparison.Ordinal))
        {
            return objStatus with { SearchingSinceUtc = null };
        }

        if (spnLine.Contains(MARK_RECORDING_STOPPED, StringComparison.Ordinal))
        {
            return objStatus with { RecordingSinceUtc = null, MatchFinishedUtc = objStatus.MatchFinishedUtc ?? dtWhen };
        }

        return objStatus;
    }

    /// <summary>Log lines start with "[2026.11.12-22.48.24:616]", in UTC.</summary>
    private static DateTime? ReadTimestamp(ReadOnlySpan<char> spnLine)
    {
        if (spnLine.Length < 25 || spnLine[0] != '[' || spnLine[24] != ']')
        {
            return null;
        }

        return DateTime.TryParseExact(spnLine.Slice(1, 23), "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dtWhen) ? dtWhen : null;
    }
}

/// <summary>Where the player is, as far as the game's log tells.</summary>
public sealed record GameStatus
{
    /// <summary>False when the log file is missing or cannot be read: the page then relies on the replay files alone.</summary>
    public bool LogFound { get; init; }

    /// <summary>The game started recording a match at this moment and has not stopped.</summary>
    public DateTime? RecordingSinceUtc { get; init; }

    /// <summary>The match ended for the player (end-of-match screen) at this moment.</summary>
    public DateTime? MatchFinishedUtc { get; init; }

    /// <summary>The player started searching for a match at this moment and nothing has ended the search.</summary>
    public DateTime? SearchingSinceUtc { get; init; }
}

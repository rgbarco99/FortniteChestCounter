using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ChestTracker;

/// <summary>
/// Reads each replay in a separate, short-lived copy of this program (the --dump mode).
/// A replay that hangs or crashes the reader is then just one failed file: the copy is
/// stopped after a time limit and the rest of the folder is still read.
/// </summary>
public static class ParseProcess
{
    private static readonly JsonSerializerOptions objJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// <summary>
    /// Time limit per replay: 60 seconds plus 10 per MB, as a long match on a busy PC (the game is
    /// running, the reader has low priority) can be slow. FCC_TIMEOUT (seconds) overrides it.
    /// </summary>
    public static TimeSpan GetTimeout(string strReplayPath)
    {
        if (double.TryParse(Environment.GetEnvironmentVariable("FCC_TIMEOUT"), System.Globalization.CultureInfo.InvariantCulture, out var dblSeconds) && dblSeconds > 0)
        {
            return TimeSpan.FromSeconds(dblSeconds);
        }
        var dblMegabytes = new FileInfo(strReplayPath).Length / 1048576.0;
        return TimeSpan.FromSeconds(Math.Min(600, 60 + 10 * dblMegabytes));
    }

    /// <summary>Exit code of --dump when the replay itself cannot be read (as opposed to the reader crashing).</summary>
    public const int EXIT_UNREADABLE = 2;

    /// <summary>
    /// Reads a replay on a thread with a large stack. The replay library reads some structures
    /// recursively, and a Windows program's main thread only has 1 MB.
    /// </summary>
    public static MatchRecord ParseWithLargeStack(string strReplayPath)
    {
        MatchRecord? objRecord = null;
        Exception? objFailure = null;
        var objThread = new Thread(() =>
        {
            try { objRecord = ReplayParser.ParseFile(strReplayPath); }
            catch (Exception objError) { objFailure = objError; }
        }, 256 * 1024 * 1024);
        objThread.Start();
        objThread.Join();
        if (objFailure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(objFailure).Throw();
        }
        return objRecord!;
    }

    public static MatchRecord Parse(string strReplayPath)
    {
        var objStart = BuildStartInfo(strReplayPath);
        if (objStart == null)
        {
            return ParseWithLargeStack(strReplayPath);
        }
        var tsTimeout = GetTimeout(strReplayPath);

        Process objProcess;
        try
        {
            objProcess = Process.Start(objStart) ?? throw new InvalidOperationException("No se pudo iniciar el lector.");
        }
        catch (Exception)
        {
            // Could not launch a copy of ourselves: read it here instead
            return ParseWithLargeStack(strReplayPath);
        }

        using (objProcess)
        {
            // Windows kills it with us however we go, so an update or a crash cannot leave it
            // running for hours holding the executable it was launched from
            Native.TieReaderToThisProgram(objProcess.Handle);

            // Stay out of the game's way while it is running
            try { objProcess.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var tskOutput = objProcess.StandardOutput.ReadToEndAsync();
            var tskErrors = objProcess.StandardError.ReadToEndAsync();

            if (!objProcess.WaitForExit((int)tsTimeout.TotalMilliseconds))
            {
                try { objProcess.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"La lectura tardó más de {tsTimeout.TotalSeconds:0} segundos y se canceló.");
            }
            objProcess.WaitForExit();

            if (objProcess.ExitCode == EXIT_UNREADABLE)
            {
                var strError = tskErrors.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                throw new InvalidOperationException(strError ?? $"El lector terminó con el código {objProcess.ExitCode}.");
            }
            if (objProcess.ExitCode != 0)
            {
                // The reader copy crashed (not a bad replay): one more try inside this program, so
                // the cause shows up as a real error message instead of an exit code
                try
                {
                    return ParseWithLargeStack(strReplayPath);
                }
                catch (Exception objError)
                {
                    throw new InvalidOperationException($"El lector terminó con el código {objProcess.ExitCode}. Segundo intento: {objError.Message}", objError);
                }
            }

            return JsonSerializer.Deserialize<MatchRecord>(tskOutput.Result, objJsonOptions)
                ?? throw new InvalidOperationException("El lector no devolvió datos.");
        }
    }

    /// <summary>How to start another copy of this program, whether it runs as an .exe or through "dotnet".</summary>
    private static ProcessStartInfo? BuildStartInfo(string strReplayPath)
    {
        var strProgram = Environment.ProcessPath;
        if (string.IsNullOrEmpty(strProgram))
        {
            return null;
        }

        var objStart = new ProcessStartInfo(strProgram)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (Path.GetFileNameWithoutExtension(strProgram).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var strAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(strAssembly))
            {
                return null;
            }
            objStart.ArgumentList.Add(strAssembly);
        }

        objStart.ArgumentList.Add("--dump");
        objStart.ArgumentList.Add(strReplayPath);
        return objStart;
    }
}

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

    /// <summary>Time limit per replay. FCC_TIMEOUT (seconds) overrides the default of 90.</summary>
    public static TimeSpan Timeout { get; } =
        double.TryParse(Environment.GetEnvironmentVariable("FCC_TIMEOUT"), System.Globalization.CultureInfo.InvariantCulture, out var dblSeconds) && dblSeconds > 0
            ? TimeSpan.FromSeconds(dblSeconds)
            : TimeSpan.FromSeconds(90);

    public static MatchRecord Parse(string strReplayPath)
    {
        var objStart = BuildStartInfo(strReplayPath);
        if (objStart == null)
        {
            return ReplayParser.ParseFile(strReplayPath);
        }

        Process objProcess;
        try
        {
            objProcess = Process.Start(objStart) ?? throw new InvalidOperationException("No se pudo iniciar el lector.");
        }
        catch (Exception)
        {
            // Could not launch a copy of ourselves: read it here instead
            return ReplayParser.ParseFile(strReplayPath);
        }

        using (objProcess)
        {
            // Stay out of the game's way while it is running
            try { objProcess.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var tskOutput = objProcess.StandardOutput.ReadToEndAsync();
            var tskErrors = objProcess.StandardError.ReadToEndAsync();

            if (!objProcess.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { objProcess.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"La lectura tardó más de {Timeout.TotalSeconds:0} segundos y se canceló.");
            }
            objProcess.WaitForExit();

            if (objProcess.ExitCode != 0)
            {
                var strError = tskErrors.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                throw new InvalidOperationException(strError ?? $"El lector terminó con el código {objProcess.ExitCode}.");
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

using System.Runtime.InteropServices;

namespace ChestTracker;

/// <summary>
/// The two things a window-less program needs from Windows itself: the console of
/// whoever ran it (for --dump typed by hand) and the language Windows is set to.
/// </summary>
internal static class Native
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int intProcessId);

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>
    /// Borrows the console of the program that started this one. A WinExe has none of its
    /// own, so without this a dump typed at a prompt would print into nowhere. Does nothing
    /// when there is no console to borrow.
    /// </summary>
    public static void AttachParentConsole()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); }
        catch (DllNotFoundException) { /* Not Windows: the output is a pipe or a file anyway */ }
        catch (EntryPointNotFoundException) { }
    }

    // -----------------------------------------------------------------------
    // Keeping the readers tied to this program
    // -----------------------------------------------------------------------

    private const int JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;
    private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr objAttributes, string? strName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr objJob, int intClass, IntPtr objInfo, int intLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr objJob, IntPtr objProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public int LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public int ActiveProcessLimit;
        public UIntPtr Affinity;
        public int PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public long ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public long ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private static IntPtr objReaderJob = IntPtr.Zero;

    /// <summary>
    /// A job that Windows empties when this program ends, however it ends. Readers are put in it
    /// so that one cannot outlive the program that started it: a reader is meant to be killed by
    /// its parent when it takes too long, but a parent that restarts for an update, or that
    /// crashes, never gets to. The orphan then sits there forever, burning a core and holding
    /// open the copy of the executable it was launched from, which is enough to stop the next
    /// update from tidying that copy away.
    /// </summary>
    public static void TieReaderToThisProgram(IntPtr objProcessHandle)
    {
        try
        {
            if (objReaderJob == IntPtr.Zero)
            {
                objReaderJob = CreateJobObject(IntPtr.Zero, null);
                if (objReaderJob == IntPtr.Zero)
                {
                    return;
                }

                var objLimits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                objLimits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                var intSize = Marshal.SizeOf(objLimits);
                var objBuffer = Marshal.AllocHGlobal(intSize);
                try
                {
                    Marshal.StructureToPtr(objLimits, objBuffer, false);
                    SetInformationJobObject(objReaderJob, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION, objBuffer, intSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(objBuffer);
                }
            }

            AssignProcessToJobObject(objReaderJob, objProcessHandle);
        }
        catch (Exception)
        {
            // Not Windows, or jobs unavailable: the parent's own timeout is still there
        }
    }

    /// <summary>
    /// Says out loud that the program could not start, in the three languages, and offers to
    /// open the log. A WinExe that throws on startup closes with no window at all, which from
    /// the outside looks exactly like double-clicking the icon and nothing happening.
    /// </summary>
    public static void ShowStartupFailure(string strMessage, string strLogPath)
    {
        var intLang = GetLanguageColumn();
        var strTitle = new[] { "Fortnite Chest Counter no pudo arrancar", "Fortnite Chest Counter could not start", "Fortnite Chest Counter n'a pas pu démarrer" }[intLang];
        var strWhat = new[]
        {
            "El programa se ha cerrado nada más empezar. Esto es lo que dijo Windows:",
            "The program closed as soon as it started. This is what Windows said:",
            "Le programme s'est fermé dès son démarrage. Voici ce qu'a dit Windows :"
        }[intLang];
        var strWhere = new[]
        {
            "Se ha apuntado en el registro, por si hace falta:",
            "It has been written to the log, in case it helps:",
            "Cela a été noté dans le journal, au cas où :"
        }[intLang];

        try
        {
            System.Windows.Forms.MessageBox.Show(
                $"{strWhat}\n\n{strMessage}\n\n{strWhere}\n{strLogPath}",
                strTitle,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // No desktop to show it on (a service, a locked session): the log already has it
        }
    }

    /// <summary>
    /// Which of the three languages Windows is set to, as a column of the [Spanish, English,
    /// French] lists the program carries: 0, 1 or 2. The dashboard has its own switch; this is
    /// only for the little that lives outside the browser, the tray icon and its menu.
    /// InvariantGlobalization is on, so CultureInfo cannot answer this and Windows is asked.
    /// </summary>
    public static int GetLanguageColumn()
    {
        try
        {
            // The primary language is the low 10 bits of the language id: 0x0A Spanish, 0x0C French
            return (GetUserDefaultUILanguage() & 0x3FF) switch { 0x0A => 0, 0x0C => 2, _ => 1 };
        }
        catch (Exception)
        {
            return 1;
        }
    }
}

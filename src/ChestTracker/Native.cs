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

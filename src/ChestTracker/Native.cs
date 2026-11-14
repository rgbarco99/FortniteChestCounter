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

using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace ChestTracker;

/// <summary>
/// The program has no window of its own: its interface is the dashboard in the browser.
/// This is how it still shows that it is running, and how it is closed: an icon next to
/// the clock, with the dashboard, a reading of the folder and the way out.
///
/// A tray icon needs a thread with a message loop, so it gets one of its own. Everything
/// else in the program carries on as before.
/// </summary>
public sealed class TrayIcon : IHostedService
{
    private readonly IHostApplicationLifetime _objLifetime;
    private readonly ReplayWatcher _objWatcher;
    private readonly Store _objStore;

    private Thread? _objThread;
    private NotifyIcon? _objIcon;
    private ApplicationContext? _objContext;

    /// <summary>A control that exists only to hand work back to the thread holding the message loop.</summary>
    private Control? _objSync;

    /// <summary>Where the dashboard is; set before the host starts, as the port is picked first.</summary>
    public string Url { get; set; } = "";

    public TrayIcon(IHostApplicationLifetime objLifetime, ReplayWatcher objWatcher, Store objStore)
    {
        _objLifetime = objLifetime;
        _objWatcher = objWatcher;
        _objStore = objStore;
    }

    // Text outside the browser follows the language Windows is set to: [Spanish, English, French]
    private static readonly int intLanguage = Native.GetLanguageColumn();
    private static string Text(string strSpanish, string strEnglish, string strFrench) =>
        intLanguage == 0 ? strSpanish : intLanguage == 2 ? strFrench : strEnglish;

    public Task StartAsync(CancellationToken objToken)
    {
        _objThread = new Thread(Run) { IsBackground = true, Name = "bandeja" };
        _objThread.SetApartmentState(ApartmentState.STA);
        _objThread.Start();
        return Task.CompletedTask;
    }

    private void Run()
    {
        try
        {
            _objSync = new Control();
            _ = _objSync.Handle;     // Creates the handle on this thread, which is what Invoke needs

            var objMenu = new ContextMenuStrip();
            objMenu.Items.Add(Text("Abrir el panel", "Open the dashboard", "Ouvrir le tableau de bord"), null, (_, _) => OpenDashboard());
            objMenu.Items.Add(Text("Buscar repeticiones nuevas", "Look for new replays", "Chercher de nouvelles rediffusions"), null, (_, _) => _objWatcher.RequestScan());
            objMenu.Items.Add(new ToolStripSeparator());
            objMenu.Items.Add(Text("Salir", "Quit", "Quitter"), null, (_, _) => _objLifetime.StopApplication());

            _objIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                // The tooltip Windows shows on hover; it holds little, so just the name and the address
                Text = $"Fortnite Chest Counter\n{Url}",
                ContextMenuStrip = objMenu,
                Visible = true
            };
            _objIcon.DoubleClick += (_, _) => OpenDashboard();

            ShowFirstNotice();

            _objContext = new ApplicationContext();
            Application.Run(_objContext);
        }
        catch (Exception objError)
        {
            // Without the icon the program still works; it just cannot be closed from the clock
            _objStore.Log($"No se pudo crear el icono de la bandeja: {objError.Message}");
        }
        finally
        {
            Hide();
        }
    }

    /// <summary>Once in the life of an installation: the window is gone, so say where the program went.</summary>
    private void ShowFirstNotice()
    {
        if (_objStore.Settings.TrayNoticeShown)
        {
            return;
        }

        _objStore.UpdateSettings(objSettings => objSettings.TrayNoticeShown = true);
        _objIcon?.ShowBalloonTip(
            7000,
            "Fortnite Chest Counter",
            Text(
                "Aquí se queda mientras juegas. Doble clic abre el panel; con el botón derecho, «Salir».",
                "It stays here while you play. Double-click opens the dashboard; right-click to quit.",
                "Il reste ici pendant que tu joues. Double-clic pour ouvrir le tableau de bord ; clic droit pour quitter."),
            ToolTipIcon.None);
    }

    private void OpenDashboard()
    {
        try { Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true }); }
        catch (Exception objError) { _objStore.Log($"No se pudo abrir el panel ({Url}): {objError.Message}"); }
    }

    /// <summary>The icon has to be taken down on the way out, or Windows leaves it there until it is hovered over.</summary>
    private void Hide()
    {
        if (_objIcon != null)
        {
            _objIcon.Visible = false;
            _objIcon.Dispose();
            _objIcon = null;
        }
    }

    public Task StopAsync(CancellationToken objToken)
    {
        var objSync = _objSync;
        try
        {
            if (objSync is { IsHandleCreated: true })
            {
                // Back on the thread that owns the icon, which is the only one allowed to touch it
                objSync.Invoke(() => { Hide(); _objContext?.ExitThread(); });
                return Task.CompletedTask;
            }
        }
        catch (Exception) { /* The thread is already gone: fall through and let the process close it */ }

        Hide();
        return Task.CompletedTask;
    }

    /// <summary>The program's own icon, shipped inside the executable so it is there however it was built.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            using var objStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico");
            if (objStream != null)
            {
                return new Icon(objStream, SystemInformation.SmallIconSize);
            }
        }
        catch (Exception) { /* Any icon is better than no icon at all */ }
        return SystemIcons.Application;
    }
}

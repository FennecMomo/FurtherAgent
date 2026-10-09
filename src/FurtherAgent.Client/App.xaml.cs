using System.Windows;
namespace FurtherAgent.Client;
public partial class App : Application
{
    private Mutex? instance;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? listener;
    private bool ownsInstance;
    private LocalControl? control;
    protected override void OnStartup(StartupEventArgs e)
    {
        string scope = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        instance = new Mutex(false, "Local\\FurtherAgent-" + scope);
        try { ownsInstance = instance.WaitOne(0); } catch (AbandonedMutexException) { ownsInstance = true; }
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FurtherAgent-activate-" + scope);
        bool background = e.Args.Contains("--background");
        if (!ownsInstance) { if (!background) activation.Set(); Shutdown(); return; }
        listener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(() =>
        { if (MainWindow is MainWindow window) window.RestoreFromTray(); }), null, Timeout.Infinite, false);
        base.OnStartup(e);
        var window = new MainWindow(); MainWindow = window;
        control = new LocalControl(command => Dispatcher.InvokeAsync(() => window.Control(command)).Task);
        control.Start();
        if (!background) window.Show();
        window.StartSavedConnection();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        control?.Dispose(); listener?.Unregister(null); activation?.Dispose();
        if (ownsInstance) instance?.ReleaseMutex();
        instance?.Dispose(); base.OnExit(e);
    }
}

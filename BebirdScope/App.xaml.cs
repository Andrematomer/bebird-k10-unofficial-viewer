using System.Windows;

namespace BebirdScope;

public partial class App : Application
{
    static Mutex _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The camera can only be used by one process at a time.
        _singleInstance = new Mutex(true, "BebirdScope.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var worker = new Thread(() =>
            {
                try { SelfTest.Run(e.Args); }
                catch (Exception ex) { Log.Write("selftest crashed: " + ex); }
                finally { Dispatcher.Invoke(Shutdown); }
            });
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            return;
        }

        DispatcherUnhandledException += (s, ex) =>
        {
            Log.Write("UNHANDLED: " + ex.Exception);
            MessageBox.Show("Something went wrong:\n\n" + ex.Exception.Message +
                            "\n\nDetails were written to:\n" + Log.FilePath,
                            "K10 Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        new MainWindow().Show();
    }
}

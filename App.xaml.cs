using System.Windows;
using System.Windows.Threading;

namespace VibeLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                Services.LogService.WriteCrash("AppDomain", args.ExceptionObject?.ToString() ?? "Unknown error");
            }
            catch { }
        };
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Services.LogService.WriteCrash("UI", e.Exception.ToString());
        }
        catch { }

        MessageBox.Show(
            "Произошла непредвиденная ошибка:\n\n" + e.Exception.Message +
            "\n\nПодробности сохранены в папке logs лаунчера.",
            "Vibe Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

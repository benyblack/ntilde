using Ntilde.Shell;
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Ntilde.Platform;
using Ntilde.VT;

namespace Ntilde;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        InstallMacAppMenu();

        // Before any window exists: the UI thread must never pump messages while it waits for a
        // terminal buffer's write lock, or a paint can deadlock against its own resize.
        Ntilde.Shell.Native.NonPumpingSynchronizationContext.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var tracker = StartupPerformanceTracker.Current
                ?? throw new InvalidOperationException(
                    "StartupPerformanceTracker.StartNewCurrent must run before App init.");

            var services = AppServices.Build(
                tracker,
                schedule: action => Avalonia.Threading.Dispatcher.UIThread.Post(
                    action,
                    Avalonia.Threading.DispatcherPriority.Background));

            desktop.MainWindow = new MainWindow(services);
            services.Startup.Mark(StartupPhase.MainWindowConstructed);

            // Enable DevTools for debugging - Press F12 to open
#if DEBUG
            this.AttachDeveloperTools();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Without an explicit application menu, Avalonia's macOS default reads "About Avalonia"
    /// and opens Avalonia's own dialog. Supplying one swaps in our About window; Avalonia still
    /// appends the standard Services / Hide / Show All / Quit items after ours. It must be set
    /// during Initialize: the native menu exporter reads the application menu once, and installs
    /// its own default if none is present yet.
    /// </summary>
    private void InstallMacAppMenu()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var about = new NativeMenuItem("About Ntilde");
        about.Click += async (_, _) =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow main })
                await main.ShowAboutWindowAsync();
        };
        NativeMenu.SetMenu(this, new NativeMenu { Items = { about } });
    }
}

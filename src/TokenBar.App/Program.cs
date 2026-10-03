using Velopack;

namespace TokenBar.App;

/// <summary>
/// Replaces the XAML-generated entry point so Velopack runs before any WinUI
/// initialisation. <c>DisableXamlGeneratedMain</c> removes the generated
/// <c>Program</c>, so everything the generated <c>Main</c> did must be
/// reproduced here, in the same order.
///
/// Velopack's install, update and uninstall operations re-launch this
/// executable with hook arguments and wait for it to exit. Building and
/// running <see cref="VelopackApp"/> first is therefore required: behind
/// <c>Application.Start</c> the hook would sit behind the UI event loop and
/// the operation would block until it times out.
/// </summary>
public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            // Runs before the install directory is removed, with a 30 second
            // budget. Deleting our own Run value is a single registry
            // operation, so it comfortably fits.
            .OnBeforeUninstallFastCallback(_ => AutostartService.SetEnabled(false))
            .Run();

        // Offline tool, not a launch: render the tray gallery to
        // %TEMP%\tray-icons and return before WinUI starts. Nothing the app
        // owns is touched — settings (`AppLanguage` reads settings.json), the
        // single-instance mutex (a running Syrtis would make the app exit
        // without dumping), windows, tray and polling all live behind
        // `Application.Start`. Skipping XAML also lets it run from an SSH
        // session (session 0), where `Microsoft.UI.Xaml` fails to start
        // (0xC000027B). The renderer is GDI+ and reads no localized string, so
        // skipping `AppLanguage` changes no pixel.
        if (args.Contains("--dump-tray-icons"))
        {
            TrayIconGallery.Dump();
            return;
        }

        // Everything below mirrors the Main that Microsoft.UI.Xaml.Markup.Compiler
        // 3.0.0.2607 emits into App.g.i.cs for the pinned WindowsAppSDK
        // 1.8.260710003, action for action.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            // FlyoutWindow resumes on the UI thread after awaiting and then
            // touches AppWindow; without this context those continuations
            // would land off-thread.
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}

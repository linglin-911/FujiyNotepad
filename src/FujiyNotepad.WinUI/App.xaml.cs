using FujiyNotepad.Presentation;
using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FujiyNotepad.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;
        private bool windowActivationLogged;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            SafeTrace("App: constructor entered");

            // Register process-wide handlers before XAML initialization so failures during startup are recorded.
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                SafeTrace($"ProcessExit: ExitCode={Environment.ExitCode}");
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            ApplyLanguageOverride();
            SafeTrace("App: language override applied");

            InitializeComponent();
            SafeTrace("App: application XAML initialized");

            // Do not mark unhandled exceptions as handled: preserve the original failure semantics.
            UnhandledException += OnXamlUnhandledException;
            SafeTrace("App: global XAML exception handler registered");
        }

        // Lets a user preview a translation (issue #78) without changing their Windows display language: set the
        // FUJIY_LANG environment variable to a BCP-47 tag (e.g. "pt-BR") before launching. Must run before any
        // resource/x:Uid is resolved, so it is the very first thing the app does. Empty/unset = follow Windows.
        private static void ApplyLanguageOverride()
        {
            try
            {
                string? lang = Environment.GetEnvironmentVariable("FUJIY_LANG");
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
                }
            }
            catch
            {
                // An invalid tag (or the API being unavailable) just means we fall back to the default language.
            }
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            SafeTrace("App: OnLaunched entered");
            try
            {
                _window = new MainWindow();
                SafeTrace("App: MainWindow constructor completed");
                _window.Activated += (_, _) =>
                {
                    if (windowActivationLogged)
                        return;
                    windowActivationLogged = true;
                    SafeTrace("App: MainWindow first Activated event");
                };
                _window.Activate();
                SafeTrace("App: MainWindow.Activate returned");
            }
            catch (Exception ex)
            {
                SafeLog(logger => logger.Log(ex));
                SafeTrace("App: OnLaunched failed");
                throw;
            }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            SafeLog(logger => logger.Log(e.Exception));
            SafeTrace("TaskScheduler.UnobservedTaskException raised");
            // Intentionally do not call SetObserved: diagnostics must not hide a faulted background task.
        }

        private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            SafeTrace($"Application.UnhandledException: {e.Message}");
            // The WinUI args can surface a null Exception (a failed cross-ABI marshal) while still carrying a
            // message, so fall back to the message in that case.
            if (e.Exception is { } ex)
            {
                SafeLog(logger => logger.Log(ex));
            }
            else
            {
                SafeLog(logger => logger.Write("UnhandledException", e.Message, null));
            }
        }

        private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            SafeTrace($"AppDomain.UnhandledException: IsTerminating={e.IsTerminating}");
            if (e.ExceptionObject is Exception ex)
            {
                SafeLog(logger => logger.Log(ex));
            }
            else
            {
                SafeLog(logger => logger.Write("UnhandledException", e.ExceptionObject?.ToString(), null));
            }
        }

        private static void SafeTrace(string message)
        {
            SafeLog(logger => logger.Trace(message));
        }

        // The crash handler must never throw (a second failure would mask the original crash), so resolving
        // the default logger and writing are both wrapped here.
        private static void SafeLog(Action<CrashLogger> log)
        {
            try
            {
                log(CrashLogger.Default());
            }
            catch
            {
                // Intentionally swallowed.
            }
        }
    }
}


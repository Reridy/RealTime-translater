using System.Windows;
using System.Windows.Threading;
using RealTimeTranslater.App.Diagnostics;

namespace RealTimeTranslater.App;

public partial class App : Application
{
    protected override void OnStartup(
        StartupEventArgs e)
    {
        SessionLog.Initialize();

        DispatcherUnhandledException +=
            OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException +=
            OnDomainUnhandledException;

        TaskScheduler.UnobservedTaskException +=
            OnUnobservedTaskException;

        base.OnStartup(e);
    }

    protected override void OnExit(
        ExitEventArgs e)
    {
        DispatcherUnhandledException -=
            OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException -=
            OnDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -=
            OnUnobservedTaskException;

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
        => SessionLog.Error(
            e.Exception);

    private static void OnDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            SessionLog.Error(
                exception);
        }
        else
        {
            SessionLog.Write(
                "ERROR",
                e.ExceptionObject?.ToString() ??
                "Unknown unhandled exception");
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
        => SessionLog.Error(
            e.Exception);
}

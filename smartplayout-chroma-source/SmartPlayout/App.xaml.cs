using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FFmpegNativePlayer;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            DataStorage.EnsureCreated();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "SMART PLAYOUT could not prepare the D: data storage.\n\n" +
                @"Expected location: D:\Smart Playout" + "\n\n" + ex.Message,
                "SMART PLAYOUT - DATA STORAGE",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender,DispatcherUnhandledExceptionEventArgs e){ModuleDiagnostics.ShowError("MAIN_UI","UNHANDLED_UI",e.Exception);e.Handled=true;}
    private static void OnDomainUnhandledException(object sender,UnhandledExceptionEventArgs e){var ex=e.ExceptionObject as Exception??new Exception(e.ExceptionObject?.ToString()??"Unknown fatal process error");ModuleDiagnostics.ShowError("PROCESS","UNHANDLED_FATAL",ex);}
    private static void OnUnobservedTaskException(object? sender,UnobservedTaskExceptionEventArgs e){ModuleDiagnostics.ShowError("BACKGROUND_TASK","UNOBSERVED",e.Exception);e.SetObserved();}
}

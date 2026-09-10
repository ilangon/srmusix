using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace FFmpegNativePlayer;

internal static class ModuleDiagnostics
{
    static long _errorSequence;
    public static string NewErrorCode(string module,string operation)
    {
        long n=Interlocked.Increment(ref _errorSequence)%10000;
        return $"SP-{SafeModuleName(module)}-{SafeModuleName(operation)}-{DateTime.Now:yyyyMMdd-HHmmss}-{n:0000}";
    }

    public static string ShowError(string module,string operation,Exception ex,Window? owner=null)
    {
        string code=NewErrorCode(module,operation);string log=Write(module+"_"+operation,new Exception("ERROR CODE: "+code,ex));
        string message=$"Module: {module.Replace('_',' ')}\nOperation: {operation.Replace('_',' ')}\nError code: {code}\n\n{ex.Message}\n\nDiagnostic log:\n{log}\n\nTake a screenshot of this dialog and send it for verification.";
        void Show(){if(owner!=null)System.Windows.MessageBox.Show(owner,message,"SMART PLAYOUT • MODULE ERROR",MessageBoxButton.OK,MessageBoxImage.Error);else System.Windows.MessageBox.Show(message,"SMART PLAYOUT • MODULE ERROR",MessageBoxButton.OK,MessageBoxImage.Error);}
        try{var dispatcher=System.Windows.Application.Current?.Dispatcher;if(dispatcher!=null&&!dispatcher.CheckAccess())dispatcher.BeginInvoke((Action)Show);else Show();}catch{}
        return code;
    }
    public static string Write(string module, Exception ex)
    {
        try
        {
            string dir=Path.Combine(DataStorage.DataRoot,"Logs");
            Directory.CreateDirectory(dir);
            string entry=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | ERROR | {module}\r\n{ex}\r\n\r\n";
            string general=Path.Combine(dir,"MODULE_CRASH.log");
            string modulePath=Path.Combine(dir,SafeModuleName(module)+".log");
            File.AppendAllText(general,entry);
            File.AppendAllText(modulePath,entry);
            return modulePath;
        }
        catch { return "Diagnostic log could not be written"; }
    }

    public static string WriteInfo(string module,string message)
    {
        try
        {
            string dir=Path.Combine(DataStorage.DataRoot,"Logs");Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,SafeModuleName(module)+".log");
            File.AppendAllText(path,$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | INFO | {message}\r\n");
            return path;
        }
        catch{return "Diagnostic log could not be written";}
    }

    static string SafeModuleName(string module)
    {
        string value=string.IsNullOrWhiteSpace(module)?"MODULE":module.Trim().ToUpperInvariant();
        foreach(char c in Path.GetInvalidFileNameChars())value=value.Replace(c,'_');
        return value.Replace(' ','_');
    }
}

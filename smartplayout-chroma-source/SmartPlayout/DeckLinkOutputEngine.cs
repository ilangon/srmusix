using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpegNativePlayer;

/// <summary>
/// Final Program -> Blackmagic DeckLink/SDI output sink.
/// This does not replace Program playback. It consumes the same clocked Final Program
/// BGRA + processed PCM bus used by the network writers and sends it to a DeckLink-capable
/// FFmpeg output device. The Blackmagic Desktop Video driver still has to be installed and
/// the selected FFmpeg runtime must have DeckLink OUTPUT compiled in.
/// </summary>
public sealed class DeckLinkOutputEngine : IDisposable
{
    Process? _process;
    readonly object _sync = new();
    public string? ActiveFfmpegPath { get; private set; }
    public string? ActiveDevice { get; private set; }
    public string Status { get; private set; } = "STOPPED";
    public string LastLogPath { get; private set; } = "";
    public bool Running { get { lock(_sync) return _process is { HasExited:false }; } }
    public event Action<string>? StatusChanged;

    public static async Task<(string? Path,List<string> Devices,string Message)> DetectAsync(string? preferredFfmpeg)
    {
        var candidates = CandidateFfmpegPaths(preferredFfmpeg).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string runtimeMessage = DetectDesktopVideoRuntime()
            ? "Blackmagic Desktop Video runtime detected."
            : "Blackmagic Desktop Video runtime not detected.";
        var windowsDevices = await DetectWindowsBlackmagicDevicesAsync().ConfigureAwait(false);
        foreach(var path in candidates)
        {
            if(string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            var devicesText = await CaptureAsync(path,"-hide_banner -devices").ConfigureAwait(false);
            if(!HasDeckLinkOutput(devicesText)) continue;
            var sinks = await CaptureAsync(path,"-hide_banner -sinks decklink").ConfigureAwait(false);
            var legacy = await CaptureAsync(path,"-hide_banner -f decklink -list_devices 1 -i dummy").ConfigureAwait(false);
            var devices = ParseDeviceNames(sinks+"\n"+legacy).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach(var hw in windowsDevices) if(!devices.Contains(hw,StringComparer.OrdinalIgnoreCase)) devices.Add(hw);
            string msg = runtimeMessage + " DeckLink-capable FFmpeg: " + path;
            if(devices.Count==0) msg += " • DeckLink output present, but no card/device was enumerated.";
            else msg += " • " + devices.Count + " device(s) visible across DeckLink/Windows detection.";
            return (path,devices,msg);
        }
        string hwMsg = windowsDevices.Count>0 ? $" Windows sees {windowsDevices.Count} Blackmagic device(s); device selection is shown for diagnostics, but START SDI stays disabled until an actual output backend is available." : " Windows PnP did not enumerate a Blackmagic video device.";
        return (null,windowsDevices,runtimeMessage+hwMsg+" Native BMD output is preferred; no optional DeckLink-capable FFmpeg OUTPUT fallback was found.");
    }

    public void Start(string ffmpegPath,string device,MasterAvBus.Session bus,bool interlaced)
    {
        if(string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath)) throw new FileNotFoundException("DeckLink-capable FFmpeg runtime not found.",ffmpegPath);
        if(string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("Select a DeckLink / SDI output device first.");
        Stop("RESTARTING");

        string fps = bus.Fps.ToString("0.###",CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("-hide_banner -loglevel info -nostdin ");
        sb.Append("-thread_queue_size 256 -f rawvideo -pixel_format bgra -video_size ").Append(bus.Width).Append('x').Append(bus.Height)
          .Append(" -framerate ").Append(fps).Append(" -i \"").Append(E(bus.VideoUrl)).Append("\" ");
        sb.Append("-thread_queue_size 512 -f s16le -ar ").Append(bus.SampleRate).Append(" -ac 2 -i \"").Append(E(bus.AudioUrl)).Append("\" ");
        sb.Append("-map 0:v:0 -map 1:a:0 -vsync cfr ");
        // DeckLink accepts uncompressed broadcast video. Use UYVY 4:2:2 8-bit for widest card/mode compatibility.
        sb.Append("-vf \"format=uyvy422\" -c:v rawvideo -pix_fmt uyvy422 ");
        if(interlaced) sb.Append("-field_order tt ");
        // SDI embedded audio is normalized to the broadcast-standard 48 kHz. FFmpeg resamples from the actual Program PCM input rate.
        sb.Append("-c:a pcm_s16le -ar 48000 -ac 2 ");
        sb.Append("-f decklink \"").Append(E(device)).Append("\"");

        Directory.CreateDirectory(Path.Combine(DataStorage.Root,"Logs","Output"));
        LastLogPath = Path.Combine(DataStorage.Root,"Logs","Output",$"DeckLink_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        var psi = new ProcessStartInfo(ffmpegPath,sb.ToString())
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true,
            WorkingDirectory=Path.GetDirectoryName(ffmpegPath) ?? AppContext.BaseDirectory
        };
        var p=new Process{StartInfo=psi,EnableRaisingEvents=true};
        p.OutputDataReceived += (_,e)=> { if(e.Data!=null) AppendLog("OUT "+e.Data); };
        p.ErrorDataReceived += (_,e)=> { if(e.Data!=null) { AppendLog(e.Data); if(IsHardError(e.Data)) SetStatus("ERROR • "+Trim(e.Data,180)); else if(e.Data.Contains("frame=",StringComparison.OrdinalIgnoreCase)) SetStatus("RUNNING • SDI OUTPUT VERIFIED"); } };
        p.Exited += (_,__)=> { int code=-1; try{code=p.ExitCode;}catch{} lock(_sync){if(ReferenceEquals(_process,p))_process=null;} SetStatus(code==0?"STOPPED":"ERROR • DECKLINK WRITER EXIT "+code+" • LOG SAVED"); try{p.Dispose();}catch{} };
        AppendLog("FFMPEG: "+ffmpegPath+Environment.NewLine+"DEVICE: "+device+Environment.NewLine+"COMMAND: "+sb);
        if(!p.Start()) throw new InvalidOperationException("Could not start DeckLink output writer.");
        lock(_sync){_process=p;ActiveFfmpegPath=ffmpegPath;ActiveDevice=device;}
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        SetStatus("CONNECTING • DECKLINK SDI WRITER STARTING");
    }

    public void Stop(string message="STOPPED BY OPERATOR")
    {
        Process? p;
        lock(_sync){p=_process;_process=null;ActiveDevice=null;}
        if(p!=null){try{if(!p.HasExited){p.Kill(true);p.WaitForExit(1000);}}catch{} try{p.Dispose();}catch{}}
        SetStatus(message);
    }

    void SetStatus(string text){Status=text;try{StatusChanged?.Invoke(text);}catch{}}
    void AppendLog(string line){try{File.AppendAllText(LastLogPath,$"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");}catch{}}
    static bool IsHardError(string s)=> s.Contains("error",StringComparison.OrdinalIgnoreCase)||s.Contains("failed",StringComparison.OrdinalIgnoreCase)||s.Contains("invalid",StringComparison.OrdinalIgnoreCase)||s.Contains("not found",StringComparison.OrdinalIgnoreCase)||s.Contains("could not",StringComparison.OrdinalIgnoreCase);
    static string Trim(string s,int n)=>s.Length<=n?s:s[..n];
    static string E(string s)=>s.Replace("\\","\\\\").Replace("\"","\\\"");

    static IEnumerable<string> CandidateFfmpegPaths(string? preferred)
    {
        if(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTPLAYOUT_DECKLINK_FFMPEG"))) yield return Environment.GetEnvironmentVariable("SMARTPLAYOUT_DECKLINK_FFMPEG")!;
        if(!string.IsNullOrWhiteSpace(preferred)) yield return preferred!;
        yield return RuntimePaths.DeckLinkExe;
    }

    static bool DetectDesktopVideoRuntime()
    {
        try
        {
            string pf=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            bool installed = Directory.Exists(Path.Combine(pf,"Blackmagic Design","Blackmagic Desktop Video")) ||
                             Directory.Exists(Path.Combine(pfx,"Blackmagic Design","Blackmagic Desktop Video"));
            if (installed) return true;
            // DeckLink SDK on Windows is exposed through the Desktop Video COM/driver runtime;
            // do not require a fictitious app-local DeckLinkAPI.dll file.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Blackmagic Design");
            return key != null;
        } catch { return false; }
    }

    static async Task<List<string>> DetectWindowsBlackmagicDevicesAsync()
    {
        var list=new List<string>();
        try
        {
            string ps="$ErrorActionPreference='SilentlyContinue'; Get-PnpDevice -PresentOnly | Where-Object { $_.FriendlyName -match 'Blackmagic|DeckLink|Intensity|UltraStudio' -and $_.Class -match 'MEDIA|AudioEndpoint' } | ForEach-Object { $_.FriendlyName }";
            string text=await CaptureAsync("powershell.exe","-NoProfile -ExecutionPolicy Bypass -Command \""+ps.Replace("\"","`\"")+"\"").ConfigureAwait(false);
            foreach(var raw in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
            {
                var n=raw.Trim(); if(n.Length<3)continue; if(n.Contains("Audio",StringComparison.OrdinalIgnoreCase)||n.Contains("Line In",StringComparison.OrdinalIgnoreCase)||n.Contains("Speakers",StringComparison.OrdinalIgnoreCase))continue;
                if(n.Contains("Blackmagic",StringComparison.OrdinalIgnoreCase)||n.Contains("DeckLink",StringComparison.OrdinalIgnoreCase)||n.Contains("Intensity",StringComparison.OrdinalIgnoreCase)||n.Contains("UltraStudio",StringComparison.OrdinalIgnoreCase)) if(!list.Contains(n,StringComparer.OrdinalIgnoreCase))list.Add(n);
            }
        }catch{}
        return list;
    }

    static bool HasDeckLinkOutput(string devices)
    {
        foreach(var raw in (devices??"").Split('\n'))
        {
            var line=raw.Trim(); int at=line.IndexOf("decklink",StringComparison.OrdinalIgnoreCase); if(at<0) continue;
            var prefix=line[..at].Trim(); if(prefix.StartsWith("E",StringComparison.OrdinalIgnoreCase)||prefix.Contains(" E ")) return true;
            if(line.StartsWith("E ",StringComparison.OrdinalIgnoreCase)||line.StartsWith("DE ",StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    static IEnumerable<string> ParseDeviceNames(string text)
    {
        foreach(var raw in (text??"").Split('\n'))
        {
            var line=raw.Trim(); if(line.Length==0) continue;
            int q1=line.IndexOf('\''); if(q1>=0){int q2=line.IndexOf('\'',q1+1);if(q2>q1+1){var n=line[(q1+1)..q2].Trim();if(n.Length>1&&!n.Equals("dummy",StringComparison.OrdinalIgnoreCase))yield return n;continue;}}
            if(line.Contains("DeckLink",StringComparison.OrdinalIgnoreCase)&&!line.Contains("Unable",StringComparison.OrdinalIgnoreCase)&&!line.Contains("Unknown",StringComparison.OrdinalIgnoreCase)&&!line.Contains("ffmpeg",StringComparison.OrdinalIgnoreCase))
            {
                var n=line.Trim(' ','[',']'); if(n.Length>3) yield return n;
            }
        }
    }
    static async Task<string> CaptureAsync(string exe,string args)
    {
        try
        {
            using var p=new Process{StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}};
            p.Start(); var o=p.StandardOutput.ReadToEndAsync(); var e=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(3500)){try{p.Kill(true);}catch{}}
            return (await o.ConfigureAwait(false))+"\n"+(await e.ConfigureAwait(false));
        } catch(Exception ex){return ex.Message;}
    }
    public void Dispose()=>Stop("STOPPED");
}

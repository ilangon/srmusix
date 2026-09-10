using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace FFmpegNativePlayer;

public partial class PlayoutParityWindow : Window
{
    sealed record LiveVideoSource(string Provider,string Name,string FfmpegPath)
    {
        public override string ToString()=>Provider=="DECKLINK"?$"[DECKLINK] {Name}":Name;
    }

    string? _ffmpeg;
    public PlayoutParityWindow(){InitializeComponent();Loaded+=async(_,__)=>await RefreshAsync();}
    MainWindow? Main=>Owner as MainWindow;
    async void RefreshDevices_Click(object s,RoutedEventArgs e)=>await RefreshAsync();
    void Close_Click(object s,RoutedEventArgs e)=>Close();

    async void TakeLive_Click(object s,RoutedEventArgs e)
    {
        if(Main==null)return;
        var src=VideoDevices.SelectedItem as LiveVideoSource;
        var a=AudioDevices.SelectedItem as string;
        if(src==null){LiveOnAirStatus.Text="Select a VIDEO device.";return;}
        try
        {
            LiveOnAirStatus.Text="Starting LIVE input...";
            if(src.Provider=="DECKLINK") await Main.StartLiveDeckLinkInputAsync(src.FfmpegPath,src.Name,a);
            else await Main.StartLiveInputAsync(src.FfmpegPath,src.Name,a);
            LiveOnAirStatus.Text=$"LIVE INPUT IS ON AIR • {src.Provider} • {src.Name}";
        }
        catch(Exception ex){LiveOnAirStatus.Text="LIVE START FAILED • "+ex.Message;}
    }

    void StopLive_Click(object s,RoutedEventArgs e){Main?.StopLiveInput();LiveOnAirStatus.Text="LIVE STOPPED";}
    void ReturnSchedule_Click(object s,RoutedEventArgs e){Main?.ReturnLiveToSchedule();LiveOnAirStatus.Text="LIVE RELEASED • RETURNING TO SCHEDULE";}

    async Task RefreshAsync()
    {
        LiveProbeStatus.Text="Scanning DirectShow + DeckLink input devices...";
        VideoDevices.ItemsSource=null;AudioDevices.ItemsSource=null;
        var video=new List<LiveVideoSource>(); var audio=new List<string>(); var notes=new List<string>();

        foreach(var ff in FindFfmpegCandidates())
        {
            try
            {
                var dshow=await RunAsync(ff,"-hide_banner -list_devices true -f dshow -i dummy");
                ParseDshow(dshow,out var dv,out var da);
                if(dv.Count==0&&da.Count==0) notes.Add(Path.GetFileName(ff)+": DirectShow input unavailable or no Windows capture devices detected");
                foreach(var n in dv) if(!video.Any(x=>x.Provider=="DSHOW"&&x.Name.Equals(n,StringComparison.OrdinalIgnoreCase))) video.Add(new("DSHOW",n,ff));
                foreach(var n in da) if(!audio.Contains(n,StringComparer.OrdinalIgnoreCase)) audio.Add(n);

                var devices=await RunAsync(ff,"-hide_banner -devices");
                if(HasDeckLinkInput(devices))
                {
                    var sources=await RunAsync(ff,"-hide_banner -sources decklink");
                    var legacy=await RunAsync(ff,"-hide_banner -f decklink -list_devices 1 -i dummy");
                    foreach(var n in ParseDeckLinkNames(sources+"\n"+legacy))
                        if(!video.Any(x=>x.Provider=="DECKLINK"&&x.Name.Equals(n,StringComparison.OrdinalIgnoreCase))) video.Add(new("DECKLINK",n,ff));
                    notes.Add("DeckLink INPUT supported: "+Path.GetFileName(ff));
                }
            }
            catch(Exception ex){notes.Add(Path.GetFileName(ff)+": "+ex.Message);}
        }

        _ffmpeg=video.FirstOrDefault()?.FfmpegPath ?? FindFfmpegCandidates().FirstOrDefault();
        FfmpegText.Text=_ffmpeg==null?"FFmpeg: not found":"FFmpeg primary: "+_ffmpeg;
        VideoDevices.ItemsSource=video;AudioDevices.ItemsSource=audio;
        if(video.Count>0)VideoDevices.SelectedIndex=0;if(audio.Count>0)AudioDevices.SelectedIndex=0;
        int dsv=video.Count(x=>x.Provider=="DSHOW"), dl=video.Count(x=>x.Provider=="DECKLINK");
        LiveProbeStatus.Text=$"DEVICE PROBE READY • DSHOW VIDEO {dsv} • DECKLINK INPUT {dl} • AUDIO {audio.Count}"+(notes.Count>0?" • "+string.Join(" | ",notes.Distinct()):"");
        if(video.Count==0) LiveOnAirStatus.Text="NO VIDEO INPUT FOUND • Install/verify Blackmagic Desktop Video for DeckLink, connect the card, and use a bundled FFmpeg runtime with DirectShow/DeckLink INPUT support.";
        try
        {
            string dir=Path.Combine(DataStorage.DataRoot,"Logs");Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir,"LIVE_INPUT_PROBE.log"),$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {LiveProbeStatus.Text}\r\n");
        }
        catch { }
    }

    static void ParseDshow(string text,out List<string> video,out List<string> audio)
    {
        video=new();audio=new();bool v=false,a=false;
        foreach(var raw in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
        {
            var line=raw.Trim();
            if(line.Contains("DirectShow video devices",StringComparison.OrdinalIgnoreCase)){v=true;a=false;continue;}
            if(line.Contains("DirectShow audio devices",StringComparison.OrdinalIgnoreCase)){v=false;a=true;continue;}
            if(!v&&!a)continue;
            var m=Regex.Match(line,"\\\"(?<n>[^\\\"]+)\\\"");
            if(!m.Success||line.Contains("Alternative name",StringComparison.OrdinalIgnoreCase))continue;
            var name=m.Groups["n"].Value.Trim();if(name.Length==0)continue;
            var list=v?video:audio;if(!list.Contains(name,StringComparer.OrdinalIgnoreCase))list.Add(name);
        }
    }

    static bool HasDeckLinkInput(string devices)
    {
        foreach(var raw in (devices??"").Split('\n'))
        {
            var line=raw.Trim();int at=line.IndexOf("decklink",StringComparison.OrdinalIgnoreCase);if(at<0)continue;
            // FFmpeg -devices flags: D=input, E=output. Accept D or DE.
            var prefix=line[..at].Trim();
            if(prefix.StartsWith("D",StringComparison.OrdinalIgnoreCase)||line.StartsWith("D ",StringComparison.OrdinalIgnoreCase)||line.StartsWith("DE ",StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }

    static IEnumerable<string> ParseDeckLinkNames(string text)
    {
        foreach(var raw in (text??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
        {
            var line=raw.Trim();if(line.Length==0)continue;
            foreach(Match m in Regex.Matches(line,"['\\\"](?<n>[^'\\\"]+)['\\\"]"))
            {
                var n=m.Groups["n"].Value.Trim();
                if(n.Length>1&&!n.Equals("dummy",StringComparison.OrdinalIgnoreCase)&&!n.Contains("decklink",StringComparison.OrdinalIgnoreCase))yield return n;
            }
        }
    }

    static List<string> FindFfmpegCandidates()
    {
        var b=AppContext.BaseDirectory;var list=new List<string>();
        void Add(string? x){if(!string.IsNullOrWhiteSpace(x)&&File.Exists(x)&&!list.Contains(x,StringComparer.OrdinalIgnoreCase))list.Add(x);}
        Add(Environment.GetEnvironmentVariable("SMARTPLAYOUT_DECKLINK_FFMPEG"));
        Add(RuntimePaths.DirectShowExe);Add(RuntimePaths.DeckLinkExe);Add(RuntimePaths.EncoderExe);
        Add(RuntimePaths.DeckLinkExe);
        return list;
    }

    static async Task<string> RunAsync(string exe,string args)
    {
        using var p=new Process{StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}};
        p.Start();var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();
        try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));}catch{try{p.Kill(true);}catch{}}
        return(await o)+"\n"+(await e);
    }
}

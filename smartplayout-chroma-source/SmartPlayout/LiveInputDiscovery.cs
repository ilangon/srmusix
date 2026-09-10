using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FFmpegNativePlayer;

internal sealed record LiveInputSource(string Provider,string Name,string FfmpegPath)
{
    public override string ToString()=>Provider switch{"DECKLINK"=>$"[DECKLINK] {Name}","NDI"=>$"[NDI] {Name}",_=>$"[CAPTURE] {Name}"};
}

internal sealed record LiveInputProbeResult(IReadOnlyList<LiveInputSource> Video,IReadOnlyList<string> Audio,string Status);

internal static class LiveInputDiscovery
{
    public static async Task<LiveInputProbeResult> ProbeAsync()
    {
        var video=new List<LiveInputSource>();var audio=new List<string>();var notes=new List<string>();
        foreach(var ffmpeg in Candidates())
        {
            try
            {
                var dshow=await RunAsync(ffmpeg,"-hide_banner -list_devices true -f dshow -i dummy");
                ParseDshow(dshow,out var cameras,out var microphones);
                foreach(var name in cameras)if(!video.Any(x=>x.Provider=="DSHOW"&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))video.Add(new("DSHOW",name,ffmpeg));
                foreach(var name in microphones)if(!audio.Contains(name,StringComparer.OrdinalIgnoreCase))audio.Add(name);
                var devices=await RunAsync(ffmpeg,"-hide_banner -devices");
                if(HasDeckLinkInput(devices))
                {
                    var names=ParseDeckLinkNames(await RunAsync(ffmpeg,"-hide_banner -sources decklink")+"\n"+await RunAsync(ffmpeg,"-hide_banner -f decklink -list_devices 1 -i dummy"));
                    foreach(var name in names)if(!video.Any(x=>x.Provider=="DECKLINK"&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))video.Add(new("DECKLINK",name,ffmpeg));
                }
                if(HasNdiInput(devices))
                {
                    foreach(var name in ParseNdiNames(await RunAsync(ffmpeg,"-hide_banner -sources libndi_newtek")))
                        if(!video.Any(x=>x.Provider=="NDI"&&x.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))video.Add(new("NDI",name,ffmpeg));
                }
            }
            catch(Exception ex){notes.Add(Path.GetFileName(ffmpeg)+": "+ex.Message);}
        }
        string status=$"DEVICE PROBE • VIDEO {video.Count} • AUDIO {audio.Count}"+(notes.Count>0?" • "+string.Join(" | ",notes.Distinct()):"");
        return new(video,audio,status);
    }

    static IEnumerable<string> Candidates()
    {
        var values=new[]{Environment.GetEnvironmentVariable("SMARTPLAYOUT_DECKLINK_FFMPEG"),Path.Combine(RuntimePaths.MediaCoreFfmpeg,"ffmpeg.exe")};
        return values.Where(x=>!string.IsNullOrWhiteSpace(x)&&File.Exists(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase);
    }
    static async Task<string> RunAsync(string exe,string args)
    {
        using var p=new Process{StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true}};
        p.Start();var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
        try{await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));}catch{try{p.Kill(true);}catch{}}
        return (await stdout)+"\n"+(await stderr);
    }
    static void ParseDshow(string text,out List<string> video,out List<string> audio)
    {
        video=new();audio=new();bool inVideo=false,inAudio=false;
        foreach(var raw in text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
        {
            var line=raw.Trim();
            if(line.Contains("DirectShow video devices",StringComparison.OrdinalIgnoreCase)){inVideo=true;inAudio=false;continue;}
            if(line.Contains("DirectShow audio devices",StringComparison.OrdinalIgnoreCase)){inVideo=false;inAudio=true;continue;}
            if(!inVideo&&!inAudio)continue;var match=Regex.Match(line,"\\\"(?<n>[^\\\"]+)\\\"");
            if(!match.Success||line.Contains("Alternative name",StringComparison.OrdinalIgnoreCase))continue;
            var name=match.Groups["n"].Value.Trim();if(name.Length==0)continue;var list=inVideo?video:audio;if(!list.Contains(name,StringComparer.OrdinalIgnoreCase))list.Add(name);
        }
    }
    static bool HasDeckLinkInput(string text)=>text.Split('\n').Any(line=>line.TrimStart().StartsWith("D")&&line.Contains("decklink",StringComparison.OrdinalIgnoreCase));
    static bool HasNdiInput(string text)=>text.Split('\n').Any(line=>line.TrimStart().StartsWith("D")&&(line.Contains("libndi_newtek",StringComparison.OrdinalIgnoreCase)||line.Contains(" ndi ",StringComparison.OrdinalIgnoreCase)));
    static IEnumerable<string> ParseDeckLinkNames(string text)
    {
        foreach(Match match in Regex.Matches(text??"","['\\\"](?<n>[^'\\\"]+)['\\\"]"))
        {var name=match.Groups["n"].Value.Trim();if(name.Length>1&&!name.Equals("dummy",StringComparison.OrdinalIgnoreCase)&&!name.Contains("decklink",StringComparison.OrdinalIgnoreCase))yield return name;}
    }
    static IEnumerable<string> ParseNdiNames(string text)
    {
        foreach(var raw in (text??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))
        {
            var line=raw.Trim();foreach(Match match in Regex.Matches(line,"['\\\"](?<n>[^'\\\"]+)['\\\"]"))
            {var name=match.Groups["n"].Value.Trim();if(name.Length>1&&!name.Contains("libndi",StringComparison.OrdinalIgnoreCase))yield return name;}
        }
    }
}

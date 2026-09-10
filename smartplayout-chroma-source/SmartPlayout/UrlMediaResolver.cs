using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FFmpegNativePlayer;

internal sealed record ResolvedNetworkMedia(string OriginalUrl,string PlayableUrl,string Provider);

internal static class UrlMediaResolver
{
    public static async Task<ResolvedNetworkMedia> ResolveAsync(string input)
    {
        if(!Uri.TryCreate(input?.Trim(),UriKind.Absolute,out var uri))throw new InvalidOperationException("Enter a valid network URL.");
        string host=uri.Host.ToLowerInvariant();
        if(host.Contains("netflix.com"))throw new NotSupportedException("Netflix uses protected DRM playback and cannot be used as a playout source.");
        bool youtube=host=="youtu.be"||host.EndsWith(".youtube.com")||host=="youtube.com";
        bool facebook=host=="fb.watch"||host.EndsWith(".facebook.com")||host=="facebook.com";
        if(!youtube&&!facebook)return new(input.Trim(),input.Trim(),uri.Scheme.ToUpperInvariant());
        string tool=FindYtDlp()??throw new FileNotFoundException("yt-dlp.exe is required for YouTube/Facebook page URLs. Place it in Runtime\\Tools or beside SMARTPlayout.exe.");
        using var process=new Process{StartInfo=new ProcessStartInfo(tool){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
        process.StartInfo.ArgumentList.Add("--no-playlist");process.StartInfo.ArgumentList.Add("--no-warnings");process.StartInfo.ArgumentList.Add("--no-progress");
        process.StartInfo.ArgumentList.Add("-f");process.StartInfo.ArgumentList.Add("best[acodec!=none][vcodec!=none]/best");process.StartInfo.ArgumentList.Add("-g");process.StartInfo.ArgumentList.Add(input.Trim());
        process.Start();var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));}catch{try{process.Kill(true);}catch{}throw new TimeoutException("The URL resolver timed out.");}
        string output=await stdout,error=await stderr;
        string? playable=output.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).FirstOrDefault(x=>Uri.TryCreate(x,UriKind.Absolute,out _));
        if(process.ExitCode!=0||string.IsNullOrWhiteSpace(playable))throw new InvalidOperationException("URL resolve failed: "+Clean(error));
        return new(input.Trim(),playable,youtube?"YOUTUBE":"FACEBOOK");
    }
    static string? FindYtDlp()
    {
        var root=AppContext.BaseDirectory;var values=new[]{Environment.GetEnvironmentVariable("SMARTPLAYOUT_YTDLP"),Path.Combine(root,"Runtime","Tools","yt-dlp.exe"),Path.Combine(root,"Tools","yt-dlp.exe"),Path.Combine(root,"yt-dlp.exe")};
        return values.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x)&&File.Exists(x));
    }
    static string Clean(string value){var text=(value??"").Replace('\r',' ').Replace('\n',' ').Trim();return text.Length<=500?text:text[..500];}
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FFmpegNativePlayer;

internal sealed record ResolvedNetworkMedia(
    string OriginalUrl,
    string PlayableUrl,
    string Provider,
    string? UserAgent = null,
    string? Referer = null);

internal static class UrlMediaResolver
{
    public static async Task<ResolvedNetworkMedia> ResolveAsync(string input)
    {
        if(!Uri.TryCreate(input?.Trim(),UriKind.Absolute,out var uri))
            throw new InvalidOperationException("Enter a valid network URL.");

        string original=input.Trim();
        string host=uri.Host.ToLowerInvariant();
        if(host.Contains("netflix.com"))
            throw new NotSupportedException("Netflix uses protected DRM playback and cannot be used as a playout source.");

        bool youtube=host=="youtu.be"||host.EndsWith(".youtube.com")||host=="youtube.com";
        bool facebook=host=="fb.watch"||host.EndsWith(".facebook.com")||host=="facebook.com";
        if(!youtube&&!facebook)
            return new(original,original,uri.Scheme.ToUpperInvariant());

        string tool=FindYtDlp()??throw new FileNotFoundException(
            "yt-dlp.exe is required for YouTube/Facebook page URLs. Place it in Runtime\\Tools or beside SMARTPlayout.exe.");

        using var process=new Process
        {
            StartInfo=new ProcessStartInfo(tool)
            {
                UseShellExecute=false,
                CreateNoWindow=true,
                RedirectStandardOutput=true,
                RedirectStandardError=true
            }
        };
        process.StartInfo.ArgumentList.Add("--no-playlist");
        process.StartInfo.ArgumentList.Add("--no-warnings");
        process.StartInfo.ArgumentList.Add("--no-progress");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("best[protocol^=http][acodec!=none][vcodec!=none]/best[acodec!=none][vcodec!=none]/best");
        process.StartInfo.ArgumentList.Add("--dump-single-json");
        process.StartInfo.ArgumentList.Add(original);

        process.Start();
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35));
        }
        catch
        {
            try{process.Kill(true);}catch{}
            throw new TimeoutException("The URL resolver timed out.");
        }

        string output=await stdout;
        string error=await stderr;
        if(process.ExitCode!=0||string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("URL resolve failed: "+Clean(error));

        try
        {
            using var json=JsonDocument.Parse(output);
            var root=json.RootElement;
            string? playable=Text(root,"url");
            if(string.IsNullOrWhiteSpace(playable))
                throw new InvalidOperationException("yt-dlp did not return a directly playable media URL.");

            string? userAgent=null,referer=null;
            if(root.TryGetProperty("http_headers",out var headers)&&headers.ValueKind==JsonValueKind.Object)
            {
                userAgent=Header(headers,"User-Agent");
                referer=Header(headers,"Referer");
            }

            return new(original,playable,youtube?"YOUTUBE":"FACEBOOK",userAgent,referer);
        }
        catch(JsonException ex)
        {
            throw new InvalidOperationException("URL resolver returned invalid media metadata: "+Clean(ex.Message),ex);
        }
    }

    static string? Text(JsonElement element,string name)
        =>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;

    static string? Header(JsonElement headers,string name)
    {
        foreach(var property in headers.EnumerateObject())
            if(property.Name.Equals(name,StringComparison.OrdinalIgnoreCase)&&property.Value.ValueKind==JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    static string? FindYtDlp()
    {
        var root=AppContext.BaseDirectory;
        var values=new[]{
            Environment.GetEnvironmentVariable("SMARTPLAYOUT_YTDLP"),
            Path.Combine(root,"Runtime","Tools","yt-dlp.exe"),
            Path.Combine(root,"Tools","yt-dlp.exe"),
            Path.Combine(root,"yt-dlp.exe")
        };
        return values.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x)&&File.Exists(x));
    }

    static string Clean(string value)
    {
        var text=(value??"").Replace('\r',' ').Replace('\n',' ').Trim();
        return text.Length<=800?text:text[..800];
    }
}

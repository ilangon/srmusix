using System.Diagnostics;

namespace SRMusix.Playout.Services;

public sealed class FfmpegOutputService : IDisposable
{
    private Process? _process;
    public bool IsRunning => _process is { HasExited: false };

    public string BuildArguments(string input, string destination, string codec, int bitrateKbps)
    {
        var encoder = codec == "H.265" ? "hevc_nvenc" : "h264_nvenc";
        var format = destination.StartsWith("rtmp", StringComparison.OrdinalIgnoreCase) ? "flv" : "mpegts";
        return $"-re -i \"{input}\" -c:v {encoder} -preset p4 -b:v {bitrateKbps}k " +
               $"-maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k -c:a aac -b:a 192k -f {format} \"{destination}\"";
    }

    public void Start(string ffmpegPath, string arguments)
    {
        Stop();
        _process = Process.Start(new ProcessStartInfo(ffmpegPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("FFmpeg could not be started.");
    }

    public void Stop()
    {
        if (_process is { HasExited: false }) _process.Kill(true);
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}


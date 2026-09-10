using SmartPlayout.EncoderFFM;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpegNativePlayer;

public enum StreamKind { Rtmp, DvbUdp, DvbSrt }

public sealed class StreamDestination
{
    public string Host { get; set; } = "239.1.1.1";
    public int Port { get; set; } = 1234;
    public override string ToString() => $"{Host}:{Port}";
}

public sealed class StreamProfile
{
    public StreamKind Kind { get; set; }
    public bool Armed { get; set; }
    public string Resolution { get; set; } = "AUTO / SCREEN";
    public string VideoEncoder { get; set; } = "AUTO";
    public string AudioEncoder { get; set; } = "aac";
    public int VideoKbps { get; set; } = 5000;
    public int AudioKbps { get; set; } = 192;
    public string InterfaceIp { get; set; } = "AUTO";
    public string RtmpUrl { get; set; } = "";
    public List<StreamDestination> Destinations { get; set; } = new();
    public int VideoPid { get; set; } = 256;
    public int AudioPid { get; set; } = 257;
    public int PmtPid { get; set; } = 4096;
    public int ServiceId { get; set; } = 1;
    public int Tsid { get; set; } = 1;
    public int Onid { get; set; } = 1;
    public int MuxRate { get; set; } = 8000000;
    public int PcrMs { get; set; } = 20;
    // DVB-compatible MPEG-TS writer defaults (aligned with the supplied Medialooks Writer profile).
    public string ServiceName { get; set; } = "SMART PLAYOUT";
    public string ServiceProvider { get; set; } = "SR NETWORK";
    public int PesPayloadSize { get; set; } = 2930;
    public int TablesVersion { get; set; } = 0;
    public double PatPeriod { get; set; } = 0.1;
    public double SdtPeriod { get; set; } = 0.5;
    public double NitPeriod { get; set; } = 9.0;
    public string SrtMode { get; set; } = "caller";
    public int SrtLatencyMs { get; set; } = 120;
    public string SrtPassphrase { get; set; } = "";
}

public sealed class StreamingSettingsFile
{
    public int Version { get; set; } = 1;
    public StreamProfile Rtmp { get; set; } = new() { Kind = StreamKind.Rtmp, VideoKbps = 5000, AudioKbps = 192 };
    public StreamProfile DvbUdp { get; set; } = new() { Kind = StreamKind.DvbUdp, VideoEncoder = "mpeg2video", VideoKbps = 6000, AudioEncoder = "mp2", AudioKbps = 256, Destinations = new() { new StreamDestination { Host = "239.1.1.1", Port = 1234 } } };
    public StreamProfile DvbSrt { get; set; } = new() { Kind = StreamKind.DvbSrt, VideoEncoder = "AUTO", VideoKbps = 5000, AudioEncoder = "aac", AudioKbps = 192, Destinations = new() { new StreamDestination { Host = "127.0.0.1", Port = 9000 } } };
}

public sealed class EncoderOption
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

public sealed class StreamCapabilities
{
    public List<EncoderOption> VideoEncoders { get; } = new();
    public List<EncoderOption> AudioEncoders { get; } = new();
    public bool HasRtmp { get; set; }
    public bool HasUdp { get; set; }
    public bool HasSrt { get; set; }
    public string Audit { get; set; } = "";
    public string GpuSummary { get; set; } = "GPU: not detected";
    public string AutoVideoEncoder { get; set; } = "libx264";
    public HashSet<string> Filters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string MpegTsMuxerHelp { get; set; } = "";
    public string SrtProtocolHelp { get; set; } = "";
    public bool HasDeckLinkOutput { get; set; }
    public bool DeckLinkRuntimeDetected { get; set; }
    public List<string> DeckLinkDevices { get; } = new();
    public string DeckLinkProbeMessage { get; set; } = "";
}

public sealed class StreamProcessState
{
    public bool Running { get; internal set; }
    public string Message { get; internal set; } = "STOPPED";
    public long Frames { get; internal set; }
    public string LastError { get; internal set; } = "";
    public string LastLogPath { get; internal set; } = "";
}

public sealed class StreamingEngine : IDisposable
{
    private readonly Dictionary<StreamKind, Process?> _processes = new();
    private readonly Dictionary<StreamKind, StreamProcessState> _states = new();
    private StreamCapabilities? _lastCapabilities;
    public event Action<StreamKind, StreamProcessState>? StateChanged;

    public StreamingEngine()
    {
        foreach (StreamKind k in Enum.GetValues<StreamKind>())
        {
            _processes[k] = null;
            _states[k] = new StreamProcessState();
        }
    }

    public StreamProcessState State(StreamKind kind) => _states[kind];

    public static string FfmpegPath
    {
        get => EncoderRuntimePolicy.RequireFfmpegExe(AppContext.BaseDirectory);
    }

    public async Task<StreamCapabilities> DetectCapabilitiesAsync()
    {
        var cap = new StreamCapabilities();
        string enc = await CaptureAsync("-hide_banner -encoders");
        string prot = await CaptureAsync("-hide_banner -protocols");
        string filters = await CaptureAsync("-hide_banner -filters");
        cap.MpegTsMuxerHelp = await CaptureAsync("-hide_banner -h muxer=mpegts");
        cap.SrtProtocolHelp = await CaptureAsync("-hide_banner -h protocol=srt");
        string devices = await CaptureAsync("-hide_banner -devices");
        cap.HasDeckLinkOutput = HasDeckLinkOutputDevice(devices);
        cap.DeckLinkRuntimeDetected = DetectDeckLinkRuntime();
        if (cap.HasDeckLinkOutput)
        {
            // -sinks is the cleanest output-device enumeration when supported. The legacy
            // list_devices probe is retained because some DeckLink FFmpeg builds expose only it.
            string sinks = await CaptureAsync("-hide_banner -sinks decklink");
            string legacy = await CaptureAsync("-hide_banner -f decklink -list_devices 1 -i dummy");
            foreach (var name in ParseDeckLinkDeviceNames(sinks + "\n" + legacy))
                if (!cap.DeckLinkDevices.Contains(name, StringComparer.OrdinalIgnoreCase)) cap.DeckLinkDevices.Add(name);
            cap.DeckLinkProbeMessage = cap.DeckLinkDevices.Count > 0
                ? $"DeckLink output device(s): {string.Join(" | ", cap.DeckLinkDevices)}"
                : "DeckLink output is compiled into FFmpeg, but no physical DeckLink device was enumerated. Check Blackmagic Desktop Video driver/runtime and card connection.";
        }
        else
        {
            cap.DeckLinkProbeMessage = cap.DeckLinkRuntimeDetected
                ? "Blackmagic Desktop Video runtime appears installed, but this bundled FFmpeg does not expose DeckLink OUTPUT. Use an FFmpeg build compiled with DeckLink output support."
                : "DeckLink OUTPUT is not exposed by bundled FFmpeg and Blackmagic Desktop Video runtime was not detected.";
        }
        foreach (var line in filters.Split('\n'))
        {
            var parts=line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length >= 3 && parts[0].All(ch => ch=='.' || char.IsLetter(ch))) cap.Filters.Add(parts[1]);
        }
        cap.HasRtmp = HasLineToken(prot, "rtmp") || prot.Contains("rtmp", StringComparison.OrdinalIgnoreCase);
        cap.HasUdp = HasLineToken(prot, "udp") || prot.Contains("udp", StringComparison.OrdinalIgnoreCase);
        cap.HasSrt = HasLineToken(prot, "srt") || prot.Contains("srt", StringComparison.OrdinalIgnoreCase);

        string gpu = await DetectGpuSummaryAsync();
        cap.GpuSummary = string.IsNullOrWhiteSpace(gpu) ? "GPU: not detected" : gpu;
        cap.VideoEncoders.Add(new EncoderOption { Name="AUTO", Label="AUTO • BEST AVAILABLE GPU / CPU (H.264)" });
        AddVideo(cap, enc, "libx264", "x264 / VideoLAN • H.264 / AVC (libx264)");
        AddVideo(cap, enc, "libx265", "x265 • H.265 / HEVC (libx265)");
        AddVideo(cap, enc, "mpeg2video", "CPU • MPEG-2 Video");
        AddVideo(cap, enc, "libopenh264", "Cisco OpenH264 • H.264");
        AddVideo(cap, enc, "h264_nvenc", "NVIDIA NVENC • H.264");
        AddVideo(cap, enc, "hevc_nvenc", "NVIDIA NVENC • H.265 / HEVC");
        AddVideo(cap, enc, "h264_qsv", "Intel Quick Sync • H.264");
        AddVideo(cap, enc, "hevc_qsv", "Intel Quick Sync • H.265 / HEVC");
        AddVideo(cap, enc, "mpeg2_qsv", "Intel Quick Sync • MPEG-2");
        AddVideo(cap, enc, "h264_amf", "AMD AMF • H.264");
        AddVideo(cap, enc, "hevc_amf", "AMD AMF • H.265 / HEVC");
        AddVideo(cap, enc, "h264_mf", "Windows Media Foundation • H.264");
        AddVideo(cap, enc, "hevc_mf", "Windows Media Foundation • H.265 / HEVC");

        AddAudio(cap, enc, "aac", "AAC");
        AddAudio(cap, enc, "libfdk_aac", "Fraunhofer FDK AAC");
        AddAudio(cap, enc, "mp2", "MPEG Layer II / MP2");
        AddAudio(cap, enc, "ac3", "Dolby AC-3");
        AddAudio(cap, enc, "eac3", "Dolby E-AC-3");
        AddAudio(cap, enc, "libopus", "Opus");
        AddAudio(cap, enc, "opus", "Opus (native)");

        cap.AutoVideoEncoder = ChooseAutoVideoEncoder(cap, enc, gpu);
        cap.Audit = $"FFmpeg: {FfmpegPath}\n{cap.GpuSummary}\nAUTO video encoder: {cap.AutoVideoEncoder}\nProtocols: RTMP={(cap.HasRtmp?"YES":"NO")} UDP={(cap.HasUdp?"YES":"NO")} SRT={(cap.HasSrt?"YES":"NO")}\nDeckLink output in FFmpeg={(cap.HasDeckLinkOutput?"YES":"NO")} runtime={(cap.DeckLinkRuntimeDetected?"YES":"NO")} devices={cap.DeckLinkDevices.Count}\n{cap.DeckLinkProbeMessage}\nVideo encoders: {string.Join(", ", cap.VideoEncoders.Select(x=>x.Name))}\nAudio encoders: {string.Join(", ", cap.AudioEncoders.Select(x=>x.Name))}\nFilters: {string.Join(", ", cap.Filters.OrderBy(x=>x))}";
        _lastCapabilities = cap;
        return cap;
    }

    private static string ChooseAutoVideoEncoder(StreamCapabilities cap, string encoderList, string gpu)
    {
        bool has(string n) => EncoderExists(encoderList,n);
        string g=(gpu??"").ToUpperInvariant();
        if (g.Contains("NVIDIA") && has("h264_nvenc")) return "h264_nvenc";
        if ((g.Contains("INTEL") || g.Contains("IRIS") || g.Contains("UHD GRAPHICS")) && has("h264_qsv")) return "h264_qsv";
        if ((g.Contains("AMD") || g.Contains("RADEON")) && has("h264_amf")) return "h264_amf";
        if (has("h264_mf")) return "h264_mf";
        if (has("libx264")) return "libx264";
        if (has("libopenh264")) return "libopenh264";
        return cap.VideoEncoders.FirstOrDefault(x=>!string.Equals(x.Name,"AUTO",StringComparison.OrdinalIgnoreCase))?.Name ?? "libx264";
    }

    private static async Task<string> DetectGpuSummaryAsync()
    {
        if (!OperatingSystem.IsWindows()) return "GPU: platform probe available on Windows";
        try
        {
            using var p=new Process { StartInfo=new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name\"") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } };
            p.Start(); string o=await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
            var names=o.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>x.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return names.Length==0 ? "GPU: not detected" : "GPU: "+string.Join(" | ",names);
        } catch { return "GPU: detection unavailable"; }
    }

    private string ResolveVideoEncoder(string requested)
    {
        if (!string.Equals(requested,"AUTO",StringComparison.OrdinalIgnoreCase)) return requested;
        return _lastCapabilities?.AutoVideoEncoder ?? "libx264";
    }

    private static bool HasLineToken(string text, string token) => text.Split('\n').Any(x => string.Equals(x.Trim(), token, StringComparison.OrdinalIgnoreCase));

    private static bool HasDeckLinkOutputDevice(string devices)
    {
        foreach (var raw in (devices ?? "").Split('\n'))
        {
            var line=raw.Trim();
            int at=line.IndexOf("decklink",StringComparison.OrdinalIgnoreCase);
            if(at<0) continue;
            string flags=line.Substring(0,at);
            // FFmpeg -devices uses D=input and E=output flags before the device name.
            if(flags.IndexOf('E')>=0 || flags.IndexOf('e')>=0) return true;
        }
        return false;
    }

    private static bool DetectDeckLinkRuntime()
    {
        if(!OperatingSystem.IsWindows()) return false;
        try
        {
            string pf=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pfx=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (Directory.Exists(Path.Combine(pf,"Blackmagic Design","Blackmagic Desktop Video")) ||
                Directory.Exists(Path.Combine(pfx,"Blackmagic Design","Blackmagic Desktop Video"))) return true;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Blackmagic Design");
            return key != null;
        }
        catch { return false; }
    }

    private static IEnumerable<string> ParseDeckLinkDeviceNames(string text)
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var raw in (text??"").Split('\n'))
        {
            string line=raw.Trim();
            if(line.Length==0) continue;
            // Common DeckLink enumeration format: ... 'DeckLink Mini Monitor 4K'
            int q1=line.IndexOf('\'');
            int q2=q1>=0?line.IndexOf('\'',q1+1):-1;
            if(q1>=0 && q2>q1+1)
            {
                string n=line.Substring(q1+1,q2-q1-1).Trim();
                if(n.Length>1 && !n.Equals("dummy",StringComparison.OrdinalIgnoreCase) && seen.Add(n)) yield return n;
                continue;
            }
            // -sinks decklink may emit a simple device line; accept only lines clearly mentioning DeckLink.
            if(line.Contains("DeckLink",StringComparison.OrdinalIgnoreCase) && !line.Contains("Unable",StringComparison.OrdinalIgnoreCase) && !line.Contains("Unknown",StringComparison.OrdinalIgnoreCase))
            {
                int rb=line.LastIndexOf(']');
                string n=(rb>=0?line[(rb+1)..]:line).Trim().Trim('-', ' ');
                if(n.Length>2 && seen.Add(n)) yield return n;
            }
        }
    }
    private static void AddVideo(StreamCapabilities c, string raw, string name, string label) { if (EncoderExists(raw, name)) c.VideoEncoders.Add(new EncoderOption { Name=name, Label=label }); }
    private static void AddAudio(StreamCapabilities c, string raw, string name, string label) { if (EncoderExists(raw, name)) c.AudioEncoders.Add(new EncoderOption { Name=name, Label=label }); }
    private static bool EncoderExists(string raw, string name) => raw.Split('\n').Any(x => x.Contains(" "+name+" ", StringComparison.OrdinalIgnoreCase) || x.TrimEnd().EndsWith(" "+name, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> CaptureAsync(string args)
    {
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo(FfmpegPath, args) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true } };
            p.Start();
            var a=p.StandardOutput.ReadToEndAsync(); var b=p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (await a)+"\n"+(await b);
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Stop(StreamKind kind, string reason="STOPPED")
    {
        var p=_processes[kind];
        try { if (p is { HasExited:false }) p.Kill(true); } catch { }
        try { p?.Dispose(); } catch { }
        _processes[kind]=null;
        var st=_states[kind]; st.Running=false; st.Message=reason; st.Frames=0; StateChanged?.Invoke(kind, st);
    }

    public void StopAll()
    {
        foreach (StreamKind k in Enum.GetValues<StreamKind>()) Stop(k);
    }

    public void Start(StreamProfile profile, string source, double offsetSeconds, double? endSeconds, string screenResolution, int sampleRate, int audioStreamIndex, string videoFilter, string audioFilter, MasterAvBus.Session? masterBus = null)
    {
        Stop(profile.Kind, "RESTARTING");
        if (masterBus == null && (string.IsNullOrWhiteSpace(source) || !File.Exists(source)))
            throw new InvalidOperationException("No current Program source is available for streaming.");

        var args = masterBus != null
            ? BuildMasterBusArguments(profile, masterBus, screenResolution, sampleRate)
            : BuildArguments(profile, source, offsetSeconds, endSeconds, screenResolution, sampleRate, audioStreamIndex, videoFilter, audioFilter);

        string logPath = CreateStreamLog(profile.Kind, args, masterBus != null);
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(FfmpegPath, args)
            {
                UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true
            },
            EnableRaisingEvents=true
        };
        _processes[profile.Kind]=p;
        var st=_states[profile.Kind];
        st.Running=false; st.Message="STARTING"; st.Frames=0; st.LastError=""; st.LastLogPath=logPath;
        StateChanged?.Invoke(profile.Kind,st);

        p.OutputDataReceived += (_,e)=>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) AppendStreamLog(logPath, "OUT  " + e.Data);
            ParseProgress(profile.Kind,e.Data);
        };
        p.ErrorDataReceived += (_,e)=>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            string line=e.Data.Trim();
            AppendStreamLog(logPath, "ERR  " + line);
            if (IsImportantStreamError(line))
            {
                st.LastError=line;
                st.Message="ERROR • "+line+" • LOG SAVED";
                StateChanged?.Invoke(profile.Kind,st);
            }
        };
        p.Exited += (_,__) =>
        {
            if (_processes[profile.Kind] != p) return;
            st.Running=false;
            int code = 0;
            try { code=p.ExitCode; } catch { }
            AppendStreamLog(logPath, $"EXIT code={code}");
            if (!string.IsNullOrWhiteSpace(st.LastError)) st.Message=$"ERROR • {st.LastError} • LOG SAVED";
            else st.Message=$"STOPPED • FFmpeg exit {code} • LOG SAVED";
            StateChanged?.Invoke(profile.Kind,st);
        };

        try
        {
            if (!p.Start()) throw new InvalidOperationException("FFmpeg streaming process could not start.");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppendStreamLog(logPath, "START EXCEPTION  " + ex);
            throw;
        }

        // MWriter reference behavior: WriterNameSet/configure first, then ObjectStart(source).
        // Our native Program is not an MPlatform COM source object, so the equivalent writer
        // stage consumes the FINAL PROGRAM Master A/V bus rather than reopening the media file.
        st.Running=false; st.Message="CONNECTING / WRITER ENCODER STARTING"; st.LastError="";
        StateChanged?.Invoke(profile.Kind,st);
    }

    private static bool IsImportantStreamError(string line)
        => line.Contains("error",StringComparison.OrdinalIgnoreCase)
        || line.Contains("failed",StringComparison.OrdinalIgnoreCase)
        || line.Contains("refused",StringComparison.OrdinalIgnoreCase)
        || line.Contains("invalid",StringComparison.OrdinalIgnoreCase)
        || line.Contains("unable",StringComparison.OrdinalIgnoreCase)
        || line.Contains("not found",StringComparison.OrdinalIgnoreCase)
        || line.Contains("unsupported",StringComparison.OrdinalIgnoreCase);

    private static string CreateStreamLog(StreamKind kind, string args, bool finalProgramBus)
    {
        try
        {
            string dir=Path.Combine(DataStorage.DataRoot,"Logs","Streaming");
            Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,$"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{kind}.log");
            File.WriteAllText(path,
                $"SMART PLAYOUT STREAM WRITER LOG\r\n"+
                $"Time: {DateTime.Now:O}\r\n"+
                $"Kind: {kind}\r\n"+
                $"Input: {(finalProgramBus?"FINAL PROGRAM MASTER A/V BUS":"LEGACY SOURCE FALLBACK")}\r\n"+
                $"FFmpeg: {FfmpegPath}\r\n"+
                $"Command: {FfmpegPath} {args}\r\n\r\n",
                new UTF8Encoding(false));
            return path;
        }
        catch { return ""; }
    }

    private static void AppendStreamLog(string path, string line)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.AppendAllText(path,$"[{DateTime.Now:HH:mm:ss.fff}] {line}\r\n",new UTF8Encoding(false)); } catch { }
    }

    private void ParseProgress(StreamKind kind, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var st=_states[kind];
        if (line.StartsWith("frame=",StringComparison.OrdinalIgnoreCase) && long.TryParse(line[6..].Trim(), out var f)) { st.Frames=f; if(f>0){st.Running=true;st.Message="RUNNING • OUTPUT VERIFIED";} }
        if (line.StartsWith("progress=",StringComparison.OrdinalIgnoreCase) && st.Frames>0) { st.Running=true; st.Message="RUNNING • OUTPUT VERIFIED"; }
        StateChanged?.Invoke(kind,st);
    }


    private string BuildMasterBusArguments(StreamProfile p, MasterAvBus.Session bus, string screenResolution, int sampleRate)
        => BuildRawProgramArguments(p,bus.Width,bus.Height,bus.Fps,bus.SampleRate,bus.DisplayAspect,
            bus.VideoUrl,bus.AudioUrl,screenResolution,sampleRate);

    public string BuildOutputWorkerArgumentsTemplate(StreamProfile p,int width,int height,double fps,int busSampleRate,
        double displayAspect,string screenResolution,int sampleRate)
        => BuildRawProgramArguments(p,width,height,fps,busSampleRate,displayAspect,
            "{VIDEO_PIPE}","{AUDIO_PIPE}",screenResolution,sampleRate);

    private string BuildRawProgramArguments(StreamProfile p,int width,int height,double fps,int busSampleRate,
        double displayAspect,string videoUrl,string audioUrl,string screenResolution,int sampleRate)
    {
        string resolution = string.Equals(p.Resolution,"AUTO / SCREEN",StringComparison.OrdinalIgnoreCase) ? screenResolution : p.Resolution;
        string videoEncoder=ResolveVideoEncoder(p.VideoEncoder);
        var fmt=ParseResolution(resolution);
        var sb=new StringBuilder();

        // Writer-style live input: FINAL PROGRAM video + processed master audio.
        sb.Append("-hide_banner -loglevel info -nostdin -stats_period 1 -progress pipe:1 ");
        sb.Append("-thread_queue_size 256 -f rawvideo -pixel_format bgra -video_size ").Append(width).Append('x').Append(height)
          .Append(" -framerate ").Append(fps.ToString("0.###",CultureInfo.InvariantCulture)).Append(" -i \"").Append(E(videoUrl)).Append("\" ");
        sb.Append("-thread_queue_size 512 -f s16le -ar ").Append(busSampleRate).Append(" -ac 2 -i \"").Append(E(audioUrl)).Append("\" ");
        sb.Append("-map 0:v:0 -map 1:a:0 -max_muxing_queue_size 2048 ");

        // MasterAvBus already carries the exact final output raster. Do not scale/pad a second
        // time here: that old second fit stage could make the transmitted picture look smaller
        // than the MAIN confidence monitor. Only attach the correct display SAR + pixel format.
        string sar = SarFromDisplayAspect(width,height,displayAspect);
        string vf="setsar="+sar+",format=yuv420p";
        sb.Append("-vf \"").Append(vf).Append("\" -pix_fmt yuv420p ");

        sb.Append("-c:v ").Append(videoEncoder).Append(' ').Append(VideoEncoderTuning(videoEncoder));
        int vkbps=Math.Max(250,p.VideoKbps);
        sb.Append("-b:v ").Append(vkbps).Append("k -maxrate ").Append(vkbps).Append("k -bufsize ").Append(Math.Max(500,vkbps*2)).Append("k ");
        double outFps=fmt.Fps>0?fmt.Fps:fps;
        if(outFps>0)
        {
            sb.Append("-r ").Append(outFps.ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
            int gop=Math.Max(12,(int)Math.Round(outFps*2.0));
            sb.Append("-g ").Append(gop).Append(" -keyint_min ").Append(Math.Max(1,gop/2)).Append(' ');
        }
        // H.264 RTMP encoders such as OpenH264/NVENC are kept progressive. Applying
        // generic ILME/ILDCT flags to them can make FFmpeg reject the output graph.
        if(fmt.Interlaced && IsMpeg2Encoder(videoEncoder)) sb.Append("-flags +ilme+ildct -top 1 ");

        int outSr=sampleRate>0?sampleRate:busSampleRate;
        sb.Append("-c:a ").Append(p.AudioEncoder).Append(" -b:a ").Append(Math.Max(32,p.AudioKbps)).Append("k -ar ").Append(outSr).Append(" -ac 2 ");

        if(p.Kind==StreamKind.Rtmp)
        {
            if(!IsH264Encoder(videoEncoder)) throw new InvalidOperationException("Standard RTMP requires H.264 video.");
            if(!string.Equals(p.AudioEncoder,"aac",StringComparison.OrdinalIgnoreCase) && !string.Equals(p.AudioEncoder,"libfdk_aac",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Standard RTMP requires AAC audio.");
            if(string.IsNullOrWhiteSpace(p.RtmpUrl)) throw new InvalidOperationException("RTMP URL is empty.");
            // Keep the RTMP writer command minimal; unnecessary FLV flags are intentionally
            // avoided so provider-specific FFmpeg builds do not reject an otherwise valid URL.
            sb.Append("-f flv \"").Append(E(p.RtmpUrl)).Append("\"");
            return sb.ToString();
        }

        sb.Append("-streamid 0:").Append(p.VideoPid).Append(" -streamid 1:").Append(p.AudioPid).Append(' ');
        if(p.Destinations.Count==0) throw new InvalidOperationException("Add at least one destination IP / port.");
        string mux=BuildDvbMuxOptions(p, videoEncoder, resolution);

        string Url(StreamDestination d)
        {
            if(p.Kind==StreamKind.DvbUdp)
            {
                string q="pkt_size=1316&buffer_size=1048576";
                if(!string.IsNullOrWhiteSpace(p.InterfaceIp)&&p.InterfaceIp!="AUTO")q+="&localaddr="+Uri.EscapeDataString(p.InterfaceIp);
                return $"udp://{d.Host}:{d.Port}?{q}";
            }
            string q2=BuildSrtQuery(p);
            return $"srt://{d.Host}:{d.Port}?{q2}";
        }

        if(p.Destinations.Count==1)
            sb.Append(mux).Append("-f mpegts \"").Append(E(Url(p.Destinations[0]))).Append("\"");
        else
        {
            var branches=p.Destinations.Select(d=>$"[f=mpegts:onfail=ignore:use_fifo=1]"+Url(d));
            sb.Append(mux).Append("-f tee \"").Append(string.Join("|",branches)).Append("\"");
        }
        return sb.ToString();
    }

    private string BuildArguments(StreamProfile p, string source, double offset, double? end, string screenResolution, int sampleRate, int audioStreamIndex, string videoFilter, string audioFilter)
    {
        string resolution = string.Equals(p.Resolution,"AUTO / SCREEN",StringComparison.OrdinalIgnoreCase) ? screenResolution : p.Resolution;
        string videoEncoder = ResolveVideoEncoder(p.VideoEncoder);
        var fmt = ParseResolution(resolution);
        var sb=new StringBuilder();
        sb.Append("-hide_banner -loglevel warning -nostdin -stats_period 1 -progress pipe:1 ");
        if (offset > 0.03) sb.Append("-ss ").Append(offset.ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("-re -i \"").Append(E(source)).Append("\" ");
        if (audioStreamIndex < 0) sb.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=").Append(sampleRate>0?sampleRate:48000).Append(" ");
        if (end.HasValue && end.Value > offset) sb.Append("-t ").Append((end.Value-offset).ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("-map 0:v:0 ");
        if (audioStreamIndex >= 0) sb.Append("-map 0:").Append(audioStreamIndex).Append(" "); else sb.Append("-map 1:a:0 ");
        string vf=SanitizeFilterChain(videoFilter);
        if (fmt.W>0 && fmt.H>0)
        {
            string sar = SampleAspectRatio(resolution);
            string scale=$"scale={fmt.W}:{fmt.H}:force_original_aspect_ratio=decrease:flags=lanczos,pad={fmt.W}:{fmt.H}:(ow-iw)/2:(oh-ih)/2:black,setsar={sar}";
            vf=string.IsNullOrWhiteSpace(vf)?scale:vf+","+scale;
        }
        if (!string.IsNullOrWhiteSpace(vf)) sb.Append("-vf \"").Append(vf).Append("\" ");
        string safeAudioFilter=SanitizeFilterChain(audioFilter);
        if (!string.IsNullOrWhiteSpace(safeAudioFilter)) sb.Append("-af \"").Append(safeAudioFilter).Append("\" ");
        sb.Append("-c:v ").Append(videoEncoder).Append(' ');
        sb.Append(VideoEncoderTuning(videoEncoder));
        sb.Append("-b:v ").Append(Math.Max(250,p.VideoKbps)).Append("k -maxrate ").Append(Math.Max(250,p.VideoKbps)).Append("k -bufsize ").Append(Math.Max(500,p.VideoKbps*2)).Append("k ");
        if (fmt.Fps>0) sb.Append("-r ").Append(fmt.Fps.ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
        if (fmt.Interlaced && IsMpeg2Encoder(videoEncoder)) sb.Append("-flags +ilme+ildct -top 1 ");
        sb.Append("-c:a ").Append(p.AudioEncoder).Append(" -b:a ").Append(Math.Max(32,p.AudioKbps)).Append("k -ar ").Append(sampleRate>0?sampleRate:48000).Append(" -ac 2 ");

        if (p.Kind == StreamKind.Rtmp)
        {
            if (!IsH264Encoder(videoEncoder)) throw new InvalidOperationException("Standard RTMP requires an H.264 video encoder. Select AUTO / H.264 NVENC / QSV / AMF / MF / OpenH264 / x264.");
            if (!string.Equals(p.AudioEncoder,"aac",StringComparison.OrdinalIgnoreCase) && !string.Equals(p.AudioEncoder,"libfdk_aac",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Standard RTMP requires AAC audio. Select AAC / FDK AAC.");
            if (string.IsNullOrWhiteSpace(p.RtmpUrl)) throw new InvalidOperationException("RTMP URL is empty.");
            sb.Append("-flvflags no_duration_filesize -f flv \"").Append(E(p.RtmpUrl)).Append("\"");
            return sb.ToString();
        }

        sb.Append("-streamid 0:").Append(p.VideoPid).Append(" -streamid 1:").Append(p.AudioPid).Append(' ');
        if (p.Destinations.Count==0) throw new InvalidOperationException("Add at least one destination IP / port.");

        // MPEG-TS service/PID options belong to the MPEG-TS muxer. For one destination
        // use a direct muxer URL (most reliable). For multiple destinations use tee and
        // put muxer options in each slave. This avoids the previous malformed tee path.
        string muxOpts=BuildDvbMuxOptions(p, videoEncoder, resolution);

        string DestinationUrl(StreamDestination d)
        {
            if (p.Kind == StreamKind.DvbUdp)
            {
                string q="pkt_size=1316&buffer_size=1048576";
                if (!string.IsNullOrWhiteSpace(p.InterfaceIp) && p.InterfaceIp!="AUTO") q += "&localaddr="+Uri.EscapeDataString(p.InterfaceIp);
                return $"udp://{d.Host}:{d.Port}?{q}";
            }
            string sq=BuildSrtQuery(p);
            return $"srt://{d.Host}:{d.Port}?{sq}";
        }

        if (p.Destinations.Count==1)
        {
            sb.Append(muxOpts).Append("-f mpegts \"").Append(E(DestinationUrl(p.Destinations[0]))).Append("\"");
        }
        else
        {
            // tee requires explicit stream selection because it cannot infer mapped streams.
            // Each slave gets an MPEG-TS muxer and is isolated with FIFO/onfail=ignore.
            var branches=p.Destinations.Select(d => $"[f=mpegts:onfail=ignore:use_fifo=1]"+DestinationUrl(d));
            sb.Append(muxOpts).Append("-f tee \"").Append(string.Join("|",branches)).Append("\"");
        }
        return sb.ToString();
    }


    private string BuildDvbMuxOptions(StreamProfile p, string videoEncoder, string resolution)
    {
        // One common DVB MPEG-TS profile for UDP and SRT. Only emit optional muxer
        // switches that the bundled FFmpeg actually advertises; this prevents an
        // otherwise healthy live writer from dying on a provider-specific build.
        string serviceType = DvbServiceType(videoEncoder, resolution);
        var sb=new StringBuilder();
        bool Has(string option) => string.IsNullOrWhiteSpace(_lastCapabilities?.MpegTsMuxerHelp) || _lastCapabilities!.MpegTsMuxerHelp.Contains("-"+option, StringComparison.OrdinalIgnoreCase);
        sb.Append("-metadata service_name=\"").Append(E(p.ServiceName)).Append("\" ");
        sb.Append("-metadata service_provider=\"").Append(E(p.ServiceProvider)).Append("\" ");
        if(Has("mpegts_service_type")) sb.Append("-mpegts_service_type ").Append(serviceType).Append(' ');
        if(Has("mpegts_pmt_start_pid")) sb.Append("-mpegts_pmt_start_pid ").Append(p.PmtPid).Append(' ');
        if(Has("mpegts_service_id")) sb.Append("-mpegts_service_id ").Append(p.ServiceId).Append(' ');
        if(Has("mpegts_transport_stream_id")) sb.Append("-mpegts_transport_stream_id ").Append(p.Tsid).Append(' ');
        if(Has("mpegts_original_network_id")) sb.Append("-mpegts_original_network_id ").Append(p.Onid).Append(' ');
        if(Has("mpegts_flags")) sb.Append("-mpegts_flags +system_b+nit+resend_headers ");
        if(Has("tables_version")) sb.Append("-tables_version ").Append(Math.Max(0,Math.Min(31,p.TablesVersion))).Append(' ');
        if(p.PesPayloadSize>0 && Has("pes_payload_size")) sb.Append("-pes_payload_size ").Append(p.PesPayloadSize).Append(' ');
        if(p.MuxRate>0 && Has("muxrate")) sb.Append("-muxrate ").Append(p.MuxRate).Append(' ');
        if(p.PcrMs>0 && Has("pcr_period")) sb.Append("-pcr_period ").Append(p.PcrMs).Append(' ');
        if(p.PatPeriod>=0 && Has("pat_period")) sb.Append("-pat_period ").Append(p.PatPeriod.ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
        if(p.SdtPeriod>=0 && Has("sdt_period")) sb.Append("-sdt_period ").Append(p.SdtPeriod.ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
        if(p.NitPeriod>=0 && Has("nit_period")) sb.Append("-nit_period ").Append(p.NitPeriod.ToString("0.###",CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("-flush_packets 1 -max_delay 700000 ");
        return sb.ToString();
    }

    private string BuildSrtQuery(StreamProfile p)
    {
        string help=_lastCapabilities?.SrtProtocolHelp ?? "";
        bool Has(string option) => string.IsNullOrWhiteSpace(help) || help.Contains(option,StringComparison.OrdinalIgnoreCase);
        var q=new List<string> { "mode="+p.SrtMode };
        if(Has("transtype")) q.Add("transtype=live");
        q.Add("latency="+(Math.Max(20,p.SrtLatencyMs)*1000).ToString(CultureInfo.InvariantCulture));
        if(Has("payload_size")) q.Add("payload_size=1316");
        else if(Has("pkt_size")) q.Add("pkt_size=1316");
        if(Has("tlpktdrop")) q.Add("tlpktdrop=1");
        if(Has("messageapi")) q.Add("messageapi=1");
        if(!string.IsNullOrWhiteSpace(p.SrtPassphrase))
        {
            q.Add("pbkeylen=16");
            q.Add("passphrase="+Uri.EscapeDataString(p.SrtPassphrase));
        }
        return string.Join("&",q);
    }

    private static string DvbServiceType(string encoder, string resolution)
    {
        bool hd = resolution.Contains("720",StringComparison.OrdinalIgnoreCase) || resolution.Contains("1080",StringComparison.OrdinalIgnoreCase) || resolution.Contains("2160",StringComparison.OrdinalIgnoreCase) || resolution.Contains("2K",StringComparison.OrdinalIgnoreCase) || resolution.Contains("4K",StringComparison.OrdinalIgnoreCase);
        if(encoder.Contains("hevc",StringComparison.OrdinalIgnoreCase) || encoder.Contains("265",StringComparison.OrdinalIgnoreCase)) return "0x1f"; // HEVC digital HDTV
        if(IsH264Encoder(encoder)) return hd ? "0x19" : "0x16"; // advanced-codec HDTV / SDTV
        if(encoder.Contains("mpeg2",StringComparison.OrdinalIgnoreCase)) return hd ? "0x11" : "0x01";
        return "0x01";
    }

    private string SanitizeFilterChain(string chain)
    {
        if (string.IsNullOrWhiteSpace(chain)) return "";
        var known=_lastCapabilities?.Filters;
        if (known == null || known.Count == 0) return ""; // fail-safe: never kill a live stream for an optional DSP filter
        var kept=new List<string>();
        foreach (var raw in chain.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name=raw;
            int eq=name.IndexOf('='); if(eq>=0) name=name[..eq];
            int at=name.IndexOf('@'); if(at>=0) name=name[..at];
            name=name.Trim();
            if (known.Contains(name)) kept.Add(raw);
        }
        return string.Join(",", kept);
    }

    private static bool IsMpeg2Encoder(string enc)
        => enc.Contains("mpeg2",StringComparison.OrdinalIgnoreCase);

    private static bool IsH264Encoder(string enc)
        => enc.Contains("h264",StringComparison.OrdinalIgnoreCase) || enc.Equals("libx264",StringComparison.OrdinalIgnoreCase) || enc.Equals("libopenh264",StringComparison.OrdinalIgnoreCase);

    private static string VideoEncoderTuning(string enc)
    {
        if (enc.Contains("nvenc",StringComparison.OrdinalIgnoreCase)) return "-preset p4 -tune ll ";
        if (enc.Contains("qsv",StringComparison.OrdinalIgnoreCase)) return "-preset medium ";
        if (enc.Contains("amf",StringComparison.OrdinalIgnoreCase)) return "-quality balanced -usage lowlatency ";
        if (enc=="libx264") return "-preset veryfast -tune zerolatency ";
        if (enc=="libx265") return "-preset veryfast -x265-params log-level=error ";
        return "";
    }

    private static string SarFromDisplayAspect(int width,int height,double displayAspect)
    {
        if(width<=0||height<=0||displayAspect<=0) return "1";
        double sar=displayAspect/((double)width/height);
        // Common broadcast values get exact rational forms.
        if(Math.Abs(sar-(64.0/45.0))<0.002) return "64/45";
        if(Math.Abs(sar-(16.0/15.0))<0.002) return "16/15";
        if(Math.Abs(sar-(40.0/33.0))<0.002) return "40/33";
        if(Math.Abs(sar-(8.0/9.0))<0.002) return "8/9";
        if(Math.Abs(sar-1.0)<0.002) return "1";
        return sar.ToString("0.######",CultureInfo.InvariantCulture);
    }

    private static string SampleAspectRatio(string resolution)
    {
        string r=(resolution??"").ToUpperInvariant();
        if (r.Contains("PAL_16X9")) return "64/45";
        if (r.Contains("PAL")) return "16/15";
        if (r.Contains("NTSC_16X9")) return "40/33";
        if (r.Contains("NTSC")) return "8/9";
        return "1";
    }

    public static (int W,int H,double Fps,bool Interlaced) ParseResolution(string r)
    {
        r=(r??"").ToUpperInvariant();
        // eMVF names describe field rate for interlaced HD modes (50i/59.94i/60i),
        // while FFmpeg rawvideo -framerate/-r expects frames per second. Keep those
        // as 25/29.97/30 fps respectively. This is especially important when SCREEN
        // is inherited by the FINAL PROGRAM writer bus.
        if (r.Contains("NTSC")) return (720,480,29.97,true);
        if (r.Contains("PAL")) return (720,576,25,true);
        int w=r.Contains("4K_DCI")?4096:r.Contains("UHD_2160")?3840:r.Contains("2K_DCI")?2048:r.Contains("HD1080")?1920:r.Contains("HD720")?1280:0;
        int h=r.Contains("4K_DCI")?2160:r.Contains("UHD_2160")?2160:r.Contains("2K_DCI")?1080:r.Contains("HD1080")?1080:r.Contains("HD720")?720:0;
        bool inter=r.EndsWith("I",StringComparison.OrdinalIgnoreCase) || r.Contains("_50I") || r.Contains("_5994I") || r.Contains("_60I");
        double fps;
        if (inter)
            fps=r.Contains("5994I")?29.97:r.Contains("60I")?30:r.Contains("50I")?25:0;
        else
            fps=r.Contains("11988")?119.88:r.Contains("9590")?95.90:r.Contains("5994")?59.94:r.Contains("4795")?47.95:r.Contains("2997")?29.97:r.Contains("2398")?23.976:r.Contains("120P")?120:r.Contains("100P")?100:r.Contains("96P")?96:r.Contains("60P")?60:r.Contains("50P")?50:r.Contains("48P")?48:r.Contains("30P")?30:r.Contains("25P")?25:r.Contains("24P")?24:0;
        return (w,h,fps,inter);
    }

    private static string E(string s)=>s.Replace("\"","\\\"");
    public void Dispose()=>StopAll();
}

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFmpegNativePlayer;

// DirectShow LIVE INPUT ingest for PLAYOUT+. Video and audio are captured as raw
// baseband and handed to the existing continuous FINAL PROGRAM MasterAvBus.
public sealed class LiveCaptureEngine : IDisposable
{
    public const string EmbeddedAudio = "@DECKLINK_EMBEDDED_AUDIO@";
    Process? _video, _audio; CancellationTokenSource? _cts;
    NamedPipeServerStream? _embeddedAudioPipe;
    long _lastVideoFrameTick; int _signalLost;
    public event Action<BitmapSource>? FrameReady;
    public event Action<byte[],int>? PcmReady;
    public event Action<float,float>? LevelChanged;
    public event Action<string>? StatusChanged;
    public bool IsRunning => _video is { HasExited:false };

    public async Task StartAsync(string ffmpeg,string videoDevice,string? audioDevice,int width=1280,int height=720,double fps=25)
    {
        Stop();
        if(!File.Exists(ffmpeg)) throw new FileNotFoundException("FFmpeg not found",ffmpeg);
        if(string.IsNullOrWhiteSpace(videoDevice)) throw new InvalidOperationException("Select a LIVE video device.");
        _cts=new CancellationTokenSource();
        StartSignalWatchdog(_cts.Token);
        string v=$"-hide_banner -loglevel warning -fflags +genpts -f dshow -rtbufsize 256M -i video=\"{Esc(videoDevice)}\" -an -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra\" -pix_fmt bgra -f rawvideo pipe:1";
        _video=Start(ffmpeg,v,true);
        _=Task.Run(()=>VideoLoop(_video,width,height,_cts.Token));
        if(!string.IsNullOrWhiteSpace(audioDevice))
        {
            string a=$"-hide_banner -loglevel warning -fflags +genpts -f dshow -rtbufsize 128M -i audio=\"{Esc(audioDevice)}\" -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le pipe:1";
            _audio=Start(ffmpeg,a,true);
            _=Task.Run(()=>AudioLoop(_audio,_cts.Token));
        }
        StatusChanged?.Invoke($"LIVE ON AIR • {videoDevice}"+(string.IsNullOrWhiteSpace(audioDevice)?" • VIDEO ONLY":$" • AUDIO {audioDevice}"));
        await Task.Delay(120);
        if(_video.HasExited) throw new InvalidOperationException("LIVE video capture could not start. Check device availability / format.");
    }

    public async Task StartDeckLinkAsync(string ffmpeg,string deckLinkDevice,string? audioDevice,int width=1280,int height=720,double fps=25)
    {
        Stop();
        if(!File.Exists(ffmpeg)) throw new FileNotFoundException("DeckLink-capable FFmpeg not found",ffmpeg);
        if(string.IsNullOrWhiteSpace(deckLinkDevice)) throw new InvalidOperationException("Select a DeckLink LIVE input device.");
        _cts=new CancellationTokenSource();
        StartSignalWatchdog(_cts.Token);
        bool embedded=string.Equals(audioDevice,EmbeddedAudio,StringComparison.Ordinal);
        string? pipeName=null,pipePath=null;
        if(embedded)
        {
            pipeName="smartplayout_decklink_audio_"+Guid.NewGuid().ToString("N");
            pipePath=$@"\\.\pipe\{pipeName}";
            _embeddedAudioPipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536);
        }
        string v=$"-hide_banner -loglevel warning -fflags +genpts -thread_queue_size 256 -f decklink -i \"{Esc(deckLinkDevice)}\" -map 0:v:0 -an -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra\" -pix_fmt bgra -f rawvideo pipe:1"+
            (embedded?$" -map 0:a:0? -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le \"{Esc(pipePath!)}\"":"");
        _video=Start(ffmpeg,v,true);
        _=Task.Run(()=>VideoLoop(_video,width,height,_cts.Token));
        if(embedded&&_embeddedAudioPipe!=null)
        {
            try
            {
                await _embeddedAudioPipe.WaitForConnectionAsync(_cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
                _=Task.Run(()=>AudioLoop(_embeddedAudioPipe,_cts.Token));
            }
            catch(Exception ex)
            {
                try{_embeddedAudioPipe.Dispose();}catch{} _embeddedAudioPipe=null;
                Stop();throw new InvalidOperationException("DeckLink embedded audio pipe could not connect. Verify the input carries embedded audio.",ex);
            }
        }
        else if(!string.IsNullOrWhiteSpace(audioDevice))
        {
            string a=$"-hide_banner -loglevel warning -fflags +genpts -f dshow -rtbufsize 128M -i audio=\"{Esc(audioDevice)}\" -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le pipe:1";
            _audio=Start(ffmpeg,a,true);
            _=Task.Run(()=>AudioLoop(_audio,_cts.Token));
        }
        StatusChanged?.Invoke($"DECKLINK LIVE ON AIR • {deckLinkDevice}"+(embedded?" • EMBEDDED AUDIO 48 kHz":string.IsNullOrWhiteSpace(audioDevice)?" • VIDEO ONLY":$" • AUDIO {audioDevice}"));
        await Task.Delay(180);
        if(_video.HasExited) throw new InvalidOperationException("DeckLink LIVE capture could not start. Verify a DeckLink-capable FFmpeg input runtime, Desktop Video driver, connector and video mode.");
    }

    public async Task StartNetworkAsync(string ffmpeg,string sourceUrl,string? audioDevice,int width=1280,int height=720,double fps=25)
    {
        Stop();
        if(!File.Exists(ffmpeg))throw new FileNotFoundException("FFmpeg not found",ffmpeg);
        if(!Uri.TryCreate(sourceUrl,UriKind.Absolute,out var uri)||!new[]{"http","https","rtsp","rtmp","rtmps","srt","udp","rtp"}.Contains(uri.Scheme,StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Enter a valid HTTP/HLS, RTSP, RTMP, SRT, UDP or RTP source URL.");
        _cts=new CancellationTokenSource();StartSignalWatchdog(_cts.Token);string? pipePath=null;bool sourceAudio=string.Equals(audioDevice,EmbeddedAudio,StringComparison.Ordinal);
        if(sourceAudio)
        {
            string pipeName="smartplayout_network_audio_"+Guid.NewGuid().ToString("N");pipePath=$@"\\.\pipe\{pipeName}";
            _embeddedAudioPipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536);
        }
        string inputOptions=uri.Scheme.Equals("rtsp",StringComparison.OrdinalIgnoreCase)?"-rtsp_transport tcp -rw_timeout 10000000 ":"-rw_timeout 10000000 ";
        string args=$"-hide_banner -loglevel warning -fflags +genpts {inputOptions}-i \"{Esc(sourceUrl)}\" -map 0:v:0 -an -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra\" -pix_fmt bgra -f rawvideo pipe:1"+
            (sourceAudio?$" -map 0:a:0? -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le \"{Esc(pipePath!)}\"":"");
        _video=Start(ffmpeg,args,true);_=Task.Run(()=>VideoLoop(_video,width,height,_cts.Token));
        if(sourceAudio&&_embeddedAudioPipe!=null)
        {
            try{await _embeddedAudioPipe.WaitForConnectionAsync(_cts.Token).WaitAsync(TimeSpan.FromSeconds(8));_=Task.Run(()=>AudioLoop(_embeddedAudioPipe,_cts.Token));}
            catch(Exception ex){Stop();throw new InvalidOperationException("Network video opened, but its audio stream could not be connected. Choose Video Only if the source has no audio.",ex);}
        }
        else if(!string.IsNullOrWhiteSpace(audioDevice))
        {
            string audioArgs=$"-hide_banner -loglevel warning -fflags +genpts -f dshow -rtbufsize 128M -i audio=\"{Esc(audioDevice)}\" -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le pipe:1";
            _audio=Start(ffmpeg,audioArgs,true);_=Task.Run(()=>AudioLoop(_audio,_cts.Token));
        }
        string audioStatus=sourceAudio?"AUTO SOURCE 48 kHz":string.IsNullOrWhiteSpace(audioDevice)?"OFF":"EXTERNAL "+audioDevice;
        StatusChanged?.Invoke($"NETWORK LIVE • {uri.Scheme.ToUpperInvariant()} • AUDIO {audioStatus}");
        await Task.Delay(180);if(_video.HasExited)throw new InvalidOperationException("Network source could not start. Verify URL, protocol and network access.");
    }
    public async Task StartNdiAsync(string ffmpeg,string sourceName,string? audioDevice,int width=1280,int height=720,double fps=25)
    {
        Stop();if(!File.Exists(ffmpeg))throw new FileNotFoundException("NDI-capable FFmpeg not found",ffmpeg);if(string.IsNullOrWhiteSpace(sourceName))throw new InvalidOperationException("Select an NDI source.");
        _cts=new CancellationTokenSource();StartSignalWatchdog(_cts.Token);bool sourceAudio=string.Equals(audioDevice,EmbeddedAudio,StringComparison.Ordinal);string? pipePath=null;
        if(sourceAudio){string pipeName="smartplayout_ndi_audio_"+Guid.NewGuid().ToString("N");pipePath=$@"\\.\pipe\{pipeName}";_embeddedAudioPipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536);}
        string args=$"-hide_banner -loglevel warning -fflags +genpts -f libndi_newtek -i \"{Esc(sourceName)}\" -map 0:v:0 -an -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra\" -pix_fmt bgra -f rawvideo pipe:1"+(sourceAudio?$" -map 0:a:0? -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le \"{Esc(pipePath!)}\"":"");
        _video=Start(ffmpeg,args,true);_=Task.Run(()=>VideoLoop(_video,width,height,_cts.Token));
        if(sourceAudio&&_embeddedAudioPipe!=null){try{await _embeddedAudioPipe.WaitForConnectionAsync(_cts.Token).WaitAsync(TimeSpan.FromSeconds(8));_=Task.Run(()=>AudioLoop(_embeddedAudioPipe,_cts.Token));}catch(Exception ex){Stop();throw new InvalidOperationException("NDI embedded audio could not connect.",ex);}}
        else StartExternalAudio(ffmpeg,audioDevice,_cts.Token);
        StatusChanged?.Invoke("NDI LIVE • "+sourceName+(sourceAudio?" • EMBEDDED AUDIO 48 kHz":string.IsNullOrWhiteSpace(audioDevice)?" • VIDEO ONLY":" • EXTERNAL AUDIO"));await Task.Delay(180);
        if(_video.HasExited)throw new InvalidOperationException("NDI source could not start. Verify the NDI runtime and source visibility.");
    }
    public async Task StartScreenAsync(string ffmpeg,string? audioDevice,int width=1280,int height=720,double fps=25)
    {
        Stop();if(!File.Exists(ffmpeg))throw new FileNotFoundException("FFmpeg not found",ffmpeg);_cts=new CancellationTokenSource();StartSignalWatchdog(_cts.Token);
        string args=$"-hide_banner -loglevel warning -fflags +genpts -f gdigrab -framerate {fps.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i desktop -an -vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,format=bgra\" -pix_fmt bgra -f rawvideo pipe:1";
        _video=Start(ffmpeg,args,true);_=Task.Run(()=>VideoLoop(_video,width,height,_cts.Token));StartExternalAudio(ffmpeg,audioDevice,_cts.Token);
        StatusChanged?.Invoke("SCREEN CAPTURE • "+(string.IsNullOrWhiteSpace(audioDevice)?"VIDEO ONLY":"EXTERNAL AUDIO "+audioDevice));await Task.Delay(180);
        if(_video.HasExited)throw new InvalidOperationException("Screen capture could not start. Verify the FFmpeg gdigrab input.");
    }
    public async Task StartTestPatternAsync(string ffmpeg,string? audioDevice,int width=1280,int height=720,double fps=25)
    {
        Stop();if(!File.Exists(ffmpeg))throw new FileNotFoundException("FFmpeg not found",ffmpeg);_cts=new CancellationTokenSource();StartSignalWatchdog(_cts.Token);
        string rate=fps.ToString(System.Globalization.CultureInfo.InvariantCulture);string args=$"-hide_banner -loglevel warning -re -f lavfi -i testsrc2=size={width}x{height}:rate={rate} -an -vf format=bgra -pix_fmt bgra -f rawvideo pipe:1";
        _video=Start(ffmpeg,args,true);_=Task.Run(()=>VideoLoop(_video,width,height,_cts.Token));StartExternalAudio(ffmpeg,audioDevice,_cts.Token);
        StatusChanged?.Invoke("TEST PATTERN • "+(string.IsNullOrWhiteSpace(audioDevice)?"VIDEO ONLY":"EXTERNAL AUDIO "+audioDevice));await Task.Delay(180);
        if(_video.HasExited)throw new InvalidOperationException("Test pattern generator could not start. Verify the FFmpeg lavfi input.");
    }
    void StartExternalAudio(string ffmpeg,string? audioDevice,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(audioDevice)||string.Equals(audioDevice,EmbeddedAudio,StringComparison.Ordinal))return;
        string args=$"-hide_banner -loglevel warning -fflags +genpts -f dshow -rtbufsize 128M -i audio=\"{Esc(audioDevice)}\" -vn -af \"aresample=async=1000:first_pts=0\" -ac 2 -ar 48000 -c:a pcm_s16le -f s16le pipe:1";
        _audio=Start(ffmpeg,args,true);_=Task.Run(()=>AudioLoop(_audio,token));
    }

    static Process Start(string exe,string args,bool stdout)
    {
        var p=new Process{StartInfo=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=stdout,RedirectStandardError=true}};
        p.Start(); _=Task.Run(async()=>{try{await p.StandardError.ReadToEndAsync();}catch{}}); return p;
    }
    async Task VideoLoop(Process p,int w,int h,CancellationToken ct)
    {
        int size=checked(w*h*4); var buf=new byte[size]; var s=p.StandardOutput.BaseStream;
        try{while(!ct.IsCancellationRequested){int n=0;while(n<size){int r=await s.ReadAsync(buf.AsMemory(n,size-n),ct);if(r<=0){MarkSignalLost("VIDEO SOURCE ENDED");return;}n+=r;}Interlocked.Exchange(ref _lastVideoFrameTick,Stopwatch.GetTimestamp());if(Interlocked.Exchange(ref _signalLost,0)!=0)StatusChanged?.Invoke("SIGNAL RECOVERED • LIVE VIDEO RESTORED");var copy=(byte[])buf.Clone();var bmp=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,copy,w*4);bmp.Freeze();FrameReady?.Invoke(bmp);}}
        catch(OperationCanceledException){} catch(Exception ex){StatusChanged?.Invoke("LIVE VIDEO STOPPED • "+ex.Message);}
    }
    void StartSignalWatchdog(CancellationToken token)
    {
        Interlocked.Exchange(ref _lastVideoFrameTick,Stopwatch.GetTimestamp());Interlocked.Exchange(ref _signalLost,0);
        _=Task.Run(async()=>
        {
            try
            {
                while(!token.IsCancellationRequested)
                {
                    await Task.Delay(500,token);long tick=Interlocked.Read(ref _lastVideoFrameTick);
                    double silent=(Stopwatch.GetTimestamp()-tick)/(double)Stopwatch.Frequency;
                    if(silent>=3)MarkSignalLost("NO VIDEO FRAMES FOR 3 SECONDS");
                }
            }
            catch(OperationCanceledException){}
        },token);
    }
    void MarkSignalLost(string reason)
    {
        if(Interlocked.Exchange(ref _signalLost,1)==0)StatusChanged?.Invoke("SIGNAL LOSS • HOLDING LAST GOOD FRAME • "+reason);
    }
    async Task AudioLoop(Process p,CancellationToken ct)
        =>await AudioLoop(p.StandardOutput.BaseStream,ct);
    async Task AudioLoop(Stream s,CancellationToken ct)
    {
        var buf=new byte[3840]; // 20ms, 48k stereo s16
        try{while(!ct.IsCancellationRequested){int n=0;while(n<buf.Length){int r=await s.ReadAsync(buf.AsMemory(n,buf.Length-n),ct);if(r<=0)return;n+=r;}var copy=(byte[])buf.Clone();PcmReady?.Invoke(copy,n);Meter(copy,n,out var l,out var r2);LevelChanged?.Invoke(l,r2);}}
        catch(OperationCanceledException){} catch(Exception ex){StatusChanged?.Invoke("LIVE AUDIO STOPPED • "+ex.Message);}
    }
    static void Meter(byte[] b,int n,out float l,out float r){double ls=0,rs=0;int c=0;for(int i=0;i+3<n;i+=4){short a=(short)(b[i]|b[i+1]<<8),d=(short)(b[i+2]|b[i+3]<<8);ls+=a*(double)a;rs+=d*(double)d;c++;}l=c==0?0:(float)Math.Min(1,Math.Sqrt(ls/c)/32768.0);r=c==0?0:(float)Math.Min(1,Math.Sqrt(rs/c)/32768.0);}
    public void Stop(){try{_cts?.Cancel();}catch{} Kill(_video);Kill(_audio);_video=null;_audio=null;try{_embeddedAudioPipe?.Dispose();}catch{} _embeddedAudioPipe=null;try{_cts?.Dispose();}catch{} _cts=null;StatusChanged?.Invoke("LIVE STOPPED");}
    static void Kill(Process? p){try{if(p is {HasExited:false})p.Kill(true);}catch{}try{p?.Dispose();}catch{}}
    static string Esc(string s)=>s.Replace("\\","\\\\").Replace("\"","\\\"");
    public void Dispose()=>Stop();
}

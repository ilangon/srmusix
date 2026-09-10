using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FFmpegNativePlayer;

// Live FINAL PROGRAM A/V bridge. Program playback remains untouched; each writer session
// receives a stable, clocked video/audio feed at the selected OUTPUT cadence.
public sealed class MasterAvBus : IDisposable
{
    public sealed class Session : IDisposable
    {
        readonly TcpFeed _video;
        readonly TcpFeed _audio;
        readonly CancellationTokenSource _clockCts = new();
        readonly object _videoLock = new();
        readonly object _audioLock = new();
        byte[]? _latestVideo;
        byte[]? _stagedVideo;
        readonly Queue<byte[]> _audioChunks = new();
        int _audioHeadOffset;
        int _audioQueuedBytes;
        readonly Task _videoClockTask;
        readonly Task _audioClockTask;
        readonly long _clockEpochTicks;
        readonly string _sessionName;
        long _videoClockUnits;
        long _audioClockUnits;
        long _lastDiagnosticTicks;
        long _audioOverflowCorrections;
        static readonly object DiagnosticFileLock = new();
        volatile bool _programTransition;

        public int Width { get; }
        public int Height { get; }
        public double Fps { get; }
        public int SampleRate { get; }
        readonly object _formatLock = new();
        public int ScaleMode { get; private set; }
        public double DisplayAspect { get; private set; }
        public string VideoUrl => $"tcp://127.0.0.1:{_video.Port}";
        public string AudioUrl => $"tcp://127.0.0.1:{_audio.Port}";

        // Keep the network writer session/timestamps alive across clip changes, but gate
        // the Program feed while the next file is being prepared. During the gate the
        // writer repeats the last complete video frame and emits silence. New video/audio
        // are staged and released together, preventing stale PCM from the previous clip
        // from trailing the new picture (the schedule-transition A/V mismatch).
        internal void BeginProgramTransition()
        {
            _programTransition=true;
            ClearAudioQueue();
            WriteAvSyncDiagnostic("TRANSITION_BEGIN queue_flushed=YES");
        }

        internal void ResetTransitionAudio()
        {
            ClearAudioQueue();
            lock(_videoLock) _stagedVideo=null;
        }
        internal void EndProgramTransition()
        {
            lock(_videoLock)
            {
                if(_stagedVideo!=null) _latestVideo=_stagedVideo;
                _stagedVideo=null;
            }
            _programTransition=false;
            WriteAvSyncDiagnostic("TRANSITION_END av_release=ATOMIC");
        }

        void ClearAudioQueue()
        {
            lock(_audioLock)
            {
                _audioChunks.Clear();
                _audioHeadOffset=0;
                _audioQueuedBytes=0;
            }
        }

        public Session(string sessionName,int w,int h,double fps,int sr,int scaleMode,double displayAspect)
        {
            _sessionName=string.IsNullOrWhiteSpace(sessionName)?"OUTPUT":sessionName;
            Width=w;
            Height=h;
            Fps=fps>0?fps:25;
            SampleRate=sr>0?sr:48000;
            ScaleMode=Math.Clamp(scaleMode,0,3);
            DisplayAspect=displayAspect>0?displayAspect:(double)w/Math.Max(1,h);
            // Equal time-domain startup queues prevent a late TCP input connection from
            // beginning almost one second behind the other input (especially at 50/60 fps).
            _video=new TcpFeed(Math.Max(8,(int)Math.Ceiling(Fps*0.250)+2));
            _audio=new TcpFeed(Math.Max(8,(int)Math.Ceiling(250.0/20.0)+2));
            _latestVideo = new byte[Math.Max(1, Width * Height * 4)]; // black until first Program frame
            _video.Start();
            _audio.Start();
            // One immutable monotonic epoch owns BOTH clocks. Individual loops may skip late
            // units, but neither loop is allowed to re-anchor itself and create A/V drift.
            _clockEpochTicks=Stopwatch.GetTimestamp();
            _lastDiagnosticTicks=_clockEpochTicks;
            _videoClockTask=Task.Run(VideoClockLoop);
            _audioClockTask=Task.Run(AudioClockLoop);
            WriteAvSyncDiagnostic($"SESSION_START raster={Width}x{Height} fps={Fps:0.###} sr={SampleRate} epoch={_clockEpochTicks}");
        }

        // Program FrameReady only replaces the latest picture. It does NOT dictate writer timing.
        // The video clock repeats the latest good frame when Program delivery is early/late/jittery.
        public void UpdateScaleMode(int scaleMode,double displayAspect)
        {
            lock(_formatLock)
            {
                ScaleMode=Math.Clamp(scaleMode,0,3);
                DisplayAspect=displayAspect>0?displayAspect:(double)Width/Math.Max(1,Height);
            }
        }
        public (int ScaleMode,double DisplayAspect) GetScaleSnapshot()
        {
            lock(_formatLock) return (ScaleMode,DisplayAspect);
        }
        public void PushVideo(BitmapSource frame)
        {
            var f=GetScaleSnapshot();
            // FINAL PROGRAM frames that already match this session raster are pre-framed.
            // Copy only; never apply SMART/16:9/4:3 a second time.
            int mode = frame.PixelWidth==Width && frame.PixelHeight==Height ? 3 : f.ScaleMode;
            SetLatestVideo(FrameToBgra(frame,Width,Height,mode,f.DisplayAspect));
        }
        internal void PushVideoBytes(byte[] bytes) => SetLatestVideo(bytes);
        private void SetLatestVideo(byte[] bytes)
        {
            if(bytes.Length != Width*Height*4) return;
            lock(_videoLock)
            {
                if(_programTransition) _stagedVideo=bytes;
                else _latestVideo=bytes;
            }
        }

        // PCM is buffered independently from decoder callback timing and emitted in exact 20 ms blocks.
        public void PushAudio(byte[] pcm,int count,int inputSampleRate=0,int channels=2)
        {
            if(count<=0) return;
            // The raw output bus has one immutable broadcast format for its whole lifetime.
            // Never reinterpret 44.1 kHz clip PCM as 48 kHz (or vice versa): that changes
            // duration, grows the queue and causes the audible A/V slip seen at A/B takes.
            inputSampleRate=inputSampleRate>0?inputSampleRate:SampleRate;
            channels=channels>0?channels:2;
            var copy = inputSampleRate==SampleRate && channels==2
                ? CopyPcm(pcm,count)
                : ResampleStereoS16(pcm,count,inputSampleRate,channels,SampleRate);
            count=copy.Length;
            if(count<=0) return;
            lock(_audioLock)
            {
                _audioChunks.Enqueue(copy);
                _audioQueuedBytes += count;
                // Keep only a broadcast-safe 200 ms maximum backlog. If a busy UI/metadata
                // operation lets the producer run ahead, converge to 160 ms in whole stereo
                // sample frames instead of carrying a 1-3 second lip-sync error downstream.
                int bytesPerSecond=SampleRate*2*2;
                int maxBytes=Math.Max(4096,(int)Math.Round(bytesPerSecond*0.200));
                int targetBytes=Math.Max(4096,(int)Math.Round(bytesPerSecond*0.160));
                bool corrected=false;
                while(_audioQueuedBytes>maxBytes && _audioChunks.Count>1)
                {
                    var old=_audioChunks.Dequeue();
                    int remaining=Math.Max(0,old.Length-_audioHeadOffset);
                    _audioQueuedBytes-=remaining;
                    _audioHeadOffset=0;
                    corrected=true;
                }
                while(corrected && _audioQueuedBytes>targetBytes && _audioChunks.Count>1)
                {
                    var old=_audioChunks.Dequeue();
                    _audioQueuedBytes-=old.Length;
                }
                if(corrected && _audioQueuedBytes>targetBytes && _audioChunks.Count>0)
                {
                    int discard=(_audioQueuedBytes-targetBytes)/4*4;
                    var head=_audioChunks.Peek();
                    int available=Math.Max(0,head.Length-_audioHeadOffset);
                    int take=Math.Min(discard,available);
                    _audioHeadOffset+=take;
                    _audioQueuedBytes-=take;
                    if(_audioHeadOffset>=head.Length)
                    {
                        _audioChunks.Dequeue();
                        _audioHeadOffset=0;
                    }
                }
                if(corrected) Interlocked.Increment(ref _audioOverflowCorrections);
            }
        }

        static byte[] CopyPcm(byte[] pcm,int count)
        {
            count=Math.Min(Math.Max(0,count),pcm.Length);
            count-=count%4;
            var copy=new byte[count];
            Buffer.BlockCopy(pcm,0,copy,0,count);
            return copy;
        }

        static byte[] ResampleStereoS16(byte[] src,int count,int inRate,int inChannels,int outRate)
        {
            count=Math.Min(Math.Max(0,count),src.Length);
            if(inRate<=0||outRate<=0||inChannels<=0) return Array.Empty<byte>();
            int inFrames=count/(inChannels*2);
            if(inFrames<=0) return Array.Empty<byte>();
            int outFrames=Math.Max(1,(int)Math.Round(inFrames*(double)outRate/inRate));
            var dst=new byte[outFrames*4];
            static short Read(byte[] b,int frame,int ch,int channels)
            {
                int actual=channels==1?0:Math.Min(ch,channels-1);
                int i=(frame*channels+actual)*2;
                return (short)(b[i]|(b[i+1]<<8));
            }
            for(int of=0;of<outFrames;of++)
            {
                double pos=of*(double)inRate/outRate;
                int i0=Math.Min(inFrames-1,(int)pos),i1=Math.Min(inFrames-1,i0+1);
                double t=pos-i0;
                for(int ch=0;ch<2;ch++)
                {
                    short a=Read(src,i0,ch,inChannels),b=Read(src,i1,ch,inChannels);
                    short v=(short)Math.Clamp((int)Math.Round(a+(b-a)*t),short.MinValue,short.MaxValue);
                    int d=(of*2+ch)*2; dst[d]=(byte)v; dst[d+1]=(byte)(v>>8);
                }
            }
            return dst;
        }

        async Task VideoClockLoop()
        {
            var ct=_clockCts.Token;
            double periodTicks=Stopwatch.Frequency/Math.Max(1.0,Fps);
            long frameNo=0;
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    long target=_clockEpochTicks+(long)(frameNo*periodTicks);
                    await DelayUntil(target,ct).ConfigureAwait(false);
                    byte[]? frame;
                    lock(_videoLock) frame=_latestVideo;
                    // During a Program transition _latestVideo remains the last committed
                    // Program frame. Incoming frames are staged and become visible only when
                    // audio/video are released together. Writer cadence never restarts.
                    if(frame!=null) _video.Enqueue(frame);
                    frameNo++;
                    Volatile.Write(ref _videoClockUnits,frameNo);
                    MaybeWritePeriodicDiagnostic();
                    // Skip missed cadence units against the SAME epoch. Never burst and never
                    // independently reset the video clock.
                    long now=Stopwatch.GetTimestamp();
                    if(now-target > Stopwatch.Frequency/2)
                        frameNo=Math.Max(frameNo,(long)Math.Floor((now-_clockEpochTicks)/periodTicks)+1);
                }
            }
            catch(OperationCanceledException) { }
            catch { }
        }

        async Task AudioClockLoop()
        {
            var ct=_clockCts.Token;
            const int blockMs=20;
            int bytesPerBlock=Math.Max(4,(int)Math.Round(SampleRate*2*2*(blockMs/1000.0)));
            bytesPerBlock-=bytesPerBlock%4; // stereo s16 frame alignment
            double periodTicks=Stopwatch.Frequency*(blockMs/1000.0);
            long blockNo=0;
            try
            {
                while(!ct.IsCancellationRequested)
                {
                    long target=_clockEpochTicks+(long)(blockNo*periodTicks);
                    await DelayUntil(target,ct).ConfigureAwait(false);
                    var block=new byte[bytesPerBlock]; // silence fills underflow; writer clock never stalls
                    int written=0;
                    if(!_programTransition)
                    {
                        lock(_audioLock)
                        {
                            while(written<block.Length && _audioChunks.Count>0)
                            {
                                var head=_audioChunks.Peek();
                                int available=head.Length-_audioHeadOffset;
                                int take=Math.Min(available,block.Length-written);
                                Buffer.BlockCopy(head,_audioHeadOffset,block,written,take);
                                _audioHeadOffset+=take;
                                _audioQueuedBytes-=take;
                                written+=take;
                                if(_audioHeadOffset>=head.Length)
                                {
                                    _audioChunks.Dequeue();
                                    _audioHeadOffset=0;
                                }
                            }
                        }
                    }
                    _audio.Enqueue(block);
                    blockNo++;
                    Volatile.Write(ref _audioClockUnits,blockNo);
                    long now=Stopwatch.GetTimestamp();
                    if(now-target > Stopwatch.Frequency/2)
                        blockNo=Math.Max(blockNo,(long)Math.Floor((now-_clockEpochTicks)/periodTicks)+1);
                }
            }
            catch(OperationCanceledException) { }
            catch { }
        }

        void MaybeWritePeriodicDiagnostic()
        {
            long now=Stopwatch.GetTimestamp();
            long last=Volatile.Read(ref _lastDiagnosticTicks);
            if(now-last<Stopwatch.Frequency*5 || Interlocked.CompareExchange(ref _lastDiagnosticTicks,now,last)!=last) return;
            long videoUnits=Volatile.Read(ref _videoClockUnits);
            long audioUnits=Volatile.Read(ref _audioClockUnits);
            double videoMs=videoUnits*1000.0/Math.Max(1.0,Fps);
            double audioMs=audioUnits*20.0;
            int queuedBytes;
            lock(_audioLock) queuedBytes=_audioQueuedBytes;
            double queueMs=queuedBytes*1000.0/Math.Max(1,SampleRate*2*2);
            WriteAvSyncDiagnostic($"CLOCK video_ms={videoMs:0.0} audio_ms={audioMs:0.0} drift_ms={audioMs-videoMs:+0.0;-0.0;0.0} queue_ms={queueMs:0.0} transition={_programTransition} overflow_corrections={Volatile.Read(ref _audioOverflowCorrections)}");
        }

        void WriteAvSyncDiagnostic(string message)
        {
            string line=$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {_sessionName} | {message}{Environment.NewLine}";
            Debug.WriteLine("AV-SYNC | "+_sessionName+" | "+message);
            // Disk I/O must never run on the real-time cadence loops.
            _=Task.Run(() =>
            {
                try
                {
                    string path=Path.Combine(DataStorage.DataRoot,"Logs","OnAir","AV_SYNC_DIAGNOSTIC.log");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    lock(DiagnosticFileLock) File.AppendAllText(path,line);
                }
                catch { }
            });
        }

        static async Task DelayUntil(long target,CancellationToken ct)
        {
            while(true)
            {
                long remain=target-Stopwatch.GetTimestamp();
                if(remain<=0) return;
                double ms=remain*1000.0/Stopwatch.Frequency;
                if(ms>2.0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1.0,ms-1.0)),ct).ConfigureAwait(false);
                else { await Task.Yield(); ct.ThrowIfCancellationRequested(); }
            }
        }

        public void Dispose()
        {
            try { _clockCts.Cancel(); } catch { }
            try { Task.WaitAll(new[]{_videoClockTask,_audioClockTask},250); } catch { }
            _video.Dispose();
            _audio.Dispose();
            _clockCts.Dispose();
        }
    }

    sealed class TcpFeed:IDisposable
    {
        TcpListener? _listener; TcpClient? _client; NetworkStream? _stream; readonly CancellationTokenSource _cts=new();
        readonly BlockingCollection<byte[]> _q;
        public int Port {get;private set;}
        public TcpFeed(int capacity){_q=new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(),Math.Max(8,capacity));}
        public void Start(){_listener=new TcpListener(IPAddress.Loopback,0);_listener.Start();Port=((IPEndPoint)_listener.LocalEndpoint).Port;_ = Task.Run(AcceptAndWrite);}
        public void Enqueue(byte[] b)
        {
            if(_q.IsAddingCompleted) return;
            while(_q.Count>=_q.BoundedCapacity-1)_q.TryTake(out _);
            try { _q.TryAdd(b); } catch { }
        }
        async Task AcceptAndWrite()
        {
            try
            {
                _client=await _listener!.AcceptTcpClientAsync(_cts.Token);
                _client.NoDelay=true;
                _stream=_client.GetStream();
                foreach(var b in _q.GetConsumingEnumerable(_cts.Token)) await _stream.WriteAsync(b,_cts.Token);
            }
            catch { }
        }
        public void Dispose(){try{_q.CompleteAdding();_cts.Cancel();}catch{} try{_stream?.Dispose();_client?.Dispose();_listener?.Stop();}catch{} _cts.Dispose(); _q.Dispose();}
    }

    readonly ConcurrentDictionary<StreamKind,Session> _sessions=new();
    public Session Create(StreamKind kind,int w,int h,double fps,int sr,int scaleMode=0,double displayAspect=0){Remove(kind);var s=new Session(kind.ToString(),w,h,fps,sr,scaleMode,displayAspect);_sessions[kind]=s;return s;}
    public void Remove(StreamKind kind){if(_sessions.TryRemove(kind,out var s))s.Dispose();}
    public void PushVideo(BitmapSource f)
    {
        // Convert once per unique target raster/ACTIVE scale mode even when RTMP/UDP/SRT run together.
        var cache=new Dictionary<string,byte[]>();
        foreach(var s in _sessions.Values)
        {
            var sf=s.GetScaleSnapshot();
            string key=s.Width+"x"+s.Height+"|m"+sf.ScaleMode+"|a"+sf.DisplayAspect.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture);
            if(!cache.TryGetValue(key,out var bytes))
            {
                int effectiveMode = f.PixelWidth==s.Width && f.PixelHeight==s.Height ? 3 : sf.ScaleMode;
                bytes=FrameToBgra(f,s.Width,s.Height,effectiveMode,sf.DisplayAspect);
                cache[key]=bytes;
            }
            s.PushVideoBytes(bytes);
        }
    }
    public void UpdateScaleMode(int scaleMode,double displayAspect)
    {
        foreach(var s in _sessions.Values) s.UpdateScaleMode(scaleMode,displayAspect);
    }
    public void PushAudio(byte[] b,int n,int inputSampleRate=0,int channels=2){foreach(var s in _sessions.Values)s.PushAudio(b,n,inputSampleRate,channels);}
    public void BeginProgramTransition(){foreach(var s in _sessions.Values)s.BeginProgramTransition();}
    public void ResetTransitionAudio(){foreach(var s in _sessions.Values)s.ResetTransitionAudio();}
    public void EndProgramTransition(){foreach(var s in _sessions.Values)s.EndProgramTransition();}

    public static BitmapSource ScaleFrameToRaster(BitmapSource frame,int width,int height,int scaleMode,double displayAspect)
    {
        if(width<=0 || height<=0) return frame;
        // FINAL PROGRAM v0.6.8.50.10: SMART SCALE (mode 0) is already framed by the
        // Program render target when the frame exactly matches the applied output raster.
        // Do not run an identical-size WPF RenderTargetBitmap pass a second time; it adds
        // needless CPU work and can soften an already-correct picture. 16:9 / 4:3 still
        // need their active-picture framing pass, while FULL STRETCH is already exact.
        if(frame.PixelWidth==width && frame.PixelHeight==height && (scaleMode==0 || scaleMode==3)) return frame;

        // ONE shared output-framing implementation for Final Program writers and native DeckLink.
        // 0 SMART SCALE: preserve the whole source picture and AUTO FIT it inside the applied
        //   output raster. Letterbox/pillarbox is allowed; no automatic crop/zoom.
        // 1 16:9: force a 16:9 active-picture canvas while preserving the whole picture.
        // 2 4:3: force a 4:3 active-picture canvas while preserving the whole picture.
        // 3 FULL STRETCH: fill exact raster; distortion is an explicit operator choice.
        double wantedAspect = scaleMode==1 ? 16.0/9.0 : scaleMode==2 ? 4.0/3.0 : displayAspect;
        if(wantedAspect<=0) wantedAspect=(double)width/Math.Max(1,height);
        var dv=new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(dv,EdgeMode.Unspecified);
        using(var dc=dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black,null,new System.Windows.Rect(0,0,width,height));
            if(scaleMode==3)
            {
                dc.DrawImage(frame,new System.Windows.Rect(0,0,width,height));
            }
            else
            {
                double srcW=Math.Max(1,frame.PixelWidth), srcH=Math.Max(1,frame.PixelHeight);
                double srcAspect=srcW/srcH;

                // First define the active-picture box (SMART uses the full output display box).
                double boxW=width, boxH=height;
                double rasterAspect=(double)width/Math.Max(1,height);
                if(scaleMode==1 || scaleMode==2)
                {
                    if(rasterAspect>wantedAspect) boxW=height*wantedAspect;
                    else boxH=width/wantedAspect;
                }

                // Then Uniform-fit the source into that box. This is the missing old AUTO FIT behavior.
                double drawW=boxW, drawH=boxH;
                double boxAspect=boxW/Math.Max(1.0,boxH);
                if(srcAspect>boxAspect) drawH=boxW/srcAspect;
                else drawW=boxH*srcAspect;

                double x=(width-drawW)/2.0;
                double y=(height-drawH)/2.0;
                dc.DrawImage(frame,new System.Windows.Rect(x,y,drawW,drawH));
            }
        }
        var rb=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
        rb.Render(dv); rb.Freeze();
        return rb;
    }

    private static byte[] FrameToBgra(BitmapSource frame,int width,int height,int scaleMode,double displayAspect)
    {
        BitmapSource src=ScaleFrameToRaster(frame,width,height,scaleMode,displayAspect);
        if(src.Format!=PixelFormats.Bgra32)
        {
            var fc=new FormatConvertedBitmap(src,PixelFormats.Bgra32,null,0);
            fc.Freeze();
            src=fc;
        }
        int stride=width*4;
        var bytes=new byte[stride*height];
        src.CopyPixels(bytes,stride,0);
        return bytes;
    }

    public void Dispose(){foreach(var k in _sessions.Keys)Remove(k);}
}

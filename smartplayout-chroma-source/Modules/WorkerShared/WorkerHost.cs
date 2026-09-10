using SmartPlayout.Contracts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace SmartPlayout.WorkerShared;

public static class WorkerHost
{
    public static async Task<int> RunAsync(ModuleKind kind,string[] args)
    {
        string pipe=ReadArg(args,"--pipe")??$"SMARTPlayout.{kind}";
        using var shutdown=new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler=(_,e)=>{e.Cancel=true;TryCancel(shutdown);};
        EventHandler exitHandler=(_,_)=>TryCancel(shutdown);
        Console.CancelKeyPress+=cancelHandler;
        AppDomain.CurrentDomain.ProcessExit+=exitHandler;
        ModuleState state=ModuleState.Starting;
        long lastFrame=0;
        long lastAudio=0,lastVideoPts=0,lastAudioPts=0;
        var moduleState=new ConcurrentDictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string,OutputRouteEngine>? outputRoutes=kind==ModuleKind.OutputWorker?new(StringComparer.OrdinalIgnoreCase):null;
        SharedVideoFrameBuffer? sharedFrames=null;
        Task? frameReader=null;
        if(kind==ModuleKind.OutputWorker)
        {
            sharedFrames=SharedVideoFrameBuffer.CreateOrOpen("SMARTPlayout.Program.BGRA");
            frameReader=Task.Run(async()=>
            {
                byte[]? pixels=null;
                long seen=0;
                while(!shutdown.IsCancellationRequested)
                {
                    if(sharedFrames.TryRead(ref pixels,seen,out var frame)&&frame is not null)
                    {
                        seen=frame.FrameNumber;
                        Interlocked.Exchange(ref lastFrame,seen);
                        Interlocked.Exchange(ref lastVideoPts,frame.PtsTicks);
                        if(outputRoutes is not null)foreach(var route in outputRoutes.Values)route.PushVideo(frame);
                    }
                    try{await Task.Delay(2,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
                }
            },shutdown.Token);
        }
        SharedAudioRingBuffer? sharedAudio=null;
        Task? audioReader=null;
        Task? audioClock=null;
        var audioQueue=new AudioJitterQueue(120,200);
        if(kind==ModuleKind.OutputWorker)
        {
            sharedAudio=SharedAudioRingBuffer.CreateOrOpen("SMARTPlayout.Program.PCM");
            audioReader=Task.Run(async()=>
            {
                byte[]? pcm=null;long seen=0;
                while(!shutdown.IsCancellationRequested)
                {
                    while(sharedAudio.TryReadNext(ref pcm,ref seen,out var packet,out var dropped)&&packet is not null)
                    {
                        if(dropped>0)Console.Error.WriteLine($"SP-OUTPUT-AUDIO-RING-OVERRUN • dropped={dropped}");
                        long overflowBytes=audioQueue.Enqueue(packet);
                        if(overflowBytes>0)Console.Error.WriteLine($"SP-OUTPUT-AUDIO-JITTER-OVERFLOW • droppedBytes={overflowBytes}");
                    }
                    try{await Task.Delay(2,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
                }
            },shutdown.Token);
            audioClock=Task.Run(async()=>
            {
                while(!shutdown.IsCancellationRequested)
                {
                    if(!audioQueue.TryDequeue(out var packet)||packet is null)
                    {
                        try{await Task.Delay(2,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
                        continue;
                    }
                    Interlocked.Exchange(ref lastAudio,packet.Sequence);
                    Interlocked.Exchange(ref lastAudioPts,packet.PtsTicks);
                    if(outputRoutes is not null)foreach(var route in outputRoutes.Values)route.PushAudio(packet);
                    int bytesPerSecond=Math.Max(1,packet.SampleRate*packet.Channels*2);
                    int delay=Math.Max(1,(int)Math.Round(packet.DataLength*1000.0/bytesPerSecond));
                    try{await Task.Delay(delay,shutdown.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
                }
            },shutdown.Token);
        }
        var heartbeat=Task.Run(async()=>
        {
            using var process=Process.GetCurrentProcess();
            TimeSpan previousCpu=process.TotalProcessorTime;
            DateTime previousAt=DateTime.UtcNow;
            while(!shutdown.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000,shutdown.Token).ConfigureAwait(false);
                    process.Refresh();
                    DateTime now=DateTime.UtcNow;
                    TimeSpan cpu=process.TotalProcessorTime;
                    double elapsed=Math.Max(.001,(now-previousAt).TotalSeconds);
                    double cpuPercent=Math.Max(0,(cpu-previousCpu).TotalSeconds/elapsed/Environment.ProcessorCount*100);
                    previousCpu=cpu;previousAt=now;
                    long videoPts=Interlocked.Read(ref lastVideoPts),audioPts=Interlocked.Read(ref lastAudioPts);
                    Console.WriteLine(ModuleJson.Serialize(new ModuleHeartbeat(kind,state,Environment.ProcessId,cpuPercent,process.WorkingSet64,
                        Interlocked.Read(ref lastFrame),Interlocked.Read(ref lastAudio),audioPts-videoPts,DateTimeOffset.UtcNow,pipe)));
                }
                catch(OperationCanceledException){break;}
                catch(Exception ex){Console.Error.WriteLine($"SP-{kind.ToString().ToUpperInvariant()}-HEARTBEAT • {ex.Message}");}
            }
        },shutdown.Token);

        state=ModuleState.Ready;
        Console.WriteLine($"SMART PLAYOUT {kind} WORKER READY • PID {Environment.ProcessId} • PIPE {pipe}");
        try
        {
            while(!shutdown.IsCancellationRequested)
            {
                await using var server=new NamedPipeServerStream(pipe,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
                try{await server.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);}
                catch(OperationCanceledException){break;}
                using var reader=new StreamReader(server,Encoding.UTF8,false,4096,true);
                using var writer=new StreamWriter(server,new UTF8Encoding(false),4096,true){AutoFlush=true};
                while(server.IsConnected&&!shutdown.IsCancellationRequested)
                {
                    string? line;
                    try{line=await reader.ReadLineAsync(shutdown.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException){break;}
                    if(line is null)break;
                    ModuleReply reply;
                    try
                    {
                        var command=ModuleJson.Deserialize<ModuleCommand>(line)??throw new InvalidDataException("Empty command.");
                        if(command.Target!=kind)throw new InvalidOperationException($"Command target {command.Target} does not match {kind}.");
                        reply=HandleCommand(kind,command,moduleState,shutdown,outputRoutes,ref lastFrame,state);
                    }
                    catch(Exception ex)
                    {
                        reply=new ModuleReply("UNKNOWN",false,$"SP-{kind.ToString().ToUpperInvariant()}-COMMAND-ERROR",ex.Message,JsonSerializer.SerializeToElement(new{}),DateTimeOffset.UtcNow);
                    }
                    await writer.WriteLineAsync(ModuleJson.Serialize(reply)).ConfigureAwait(false);
                }
            }
        }
        catch(Exception ex)
        {
            state=ModuleState.Faulted;
            Console.Error.WriteLine($"SP-{kind.ToString().ToUpperInvariant()}-FATAL • {ex}");
            return 20;
        }
        finally
        {
            Console.CancelKeyPress-=cancelHandler;
            AppDomain.CurrentDomain.ProcessExit-=exitHandler;
            TryCancel(shutdown);
            try{await heartbeat.ConfigureAwait(false);}catch{}
            if(frameReader is not null)try{await frameReader.ConfigureAwait(false);}catch{}
            if(audioReader is not null)try{await audioReader.ConfigureAwait(false);}catch{}
            if(audioClock is not null)try{await audioClock.ConfigureAwait(false);}catch{}
            sharedFrames?.Dispose();
            sharedAudio?.Dispose();
            if(outputRoutes is not null)foreach(var route in outputRoutes.Values)route.Dispose();
        }
        return 0;
    }

    static void TryCancel(CancellationTokenSource source)
    {
        try{source.Cancel();}catch(ObjectDisposedException){}
    }

    static ModuleReply Shutdown(ModuleCommand command,CancellationTokenSource shutdown)
    {
        shutdown.CancelAfter(100);
        return Reply(command,true,"","SHUTDOWN ACCEPTED",new{});
    }

    static ModuleReply HandleCommand(ModuleKind kind,ModuleCommand command,ConcurrentDictionary<string,JsonElement> state,
        CancellationTokenSource shutdown,ConcurrentDictionary<string,OutputRouteEngine>? outputRoutes,ref long lastFrame,ModuleState moduleState)
    {
        string op=command.Operation.Trim().ToUpperInvariant();
        if(op=="PING")return Reply(command,true,"","PONG",new{module=kind,pid=Environment.ProcessId,state=moduleState,items=state.Count});
        if(op=="GET_STATE")return Reply(command,true,"","STATE",state.ToDictionary(x=>x.Key,x=>x.Value));
        if(op=="FRAME_ACK")return Reply(command,true,"","FRAME ACKNOWLEDGED",new{frame=Interlocked.Increment(ref lastFrame)});
        if(op=="SHUTDOWN")return Shutdown(command,shutdown);
        if(kind==ModuleKind.OutputWorker&&op=="STOP_ROUTE")
        {
            string stopKey=OperationKey(kind,op,command.Payload)??"route:program";
            string routeId=stopKey.StartsWith("route:",StringComparison.OrdinalIgnoreCase)?stopKey[6..]:stopKey;
            if(outputRoutes is not null&&outputRoutes.TryRemove(routeId,out var stopped))stopped.Dispose();
            state.TryRemove(stopKey,out _);
            return Reply(command,true,"","OUTPUT ROUTE STOPPED",new{});
        }
        if(kind==ModuleKind.OutputWorker&&(op is "REGISTER_ROUTE" or "START_ROUTE" or "UPDATE_ROUTE"))
        {
            var route=command.Payload.Deserialize<OutputRouteRegistration>(ModuleJson.Options);
            string? problem=ValidateRoute(route);
            if(problem is not null)return Reply(command,false,"SP-OUTPUT-ROUTE-INVALID",problem,new{});
            if(outputRoutes is not null)
            {
                var engine=outputRoutes.GetOrAdd(route!.Id,_=>new OutputRouteEngine());
                if(op=="START_ROUTE"||(op=="UPDATE_ROUTE"&&engine.Running))engine.Start(route);
            }
        }

        string? key=OperationKey(kind,op,command.Payload);
        if(key is null)return Reply(command,false,$"SP-{kind.ToString().ToUpperInvariant()}-UNKNOWN-COMMAND",$"Unsupported operation: {command.Operation}",new{});
        if(op.StartsWith("CLEAR_",StringComparison.Ordinal))
        {
            if(op is "CLEAR_LAYERS" or "CLEAR_ALL")state.Clear();else state.TryRemove(key,out _);
            return Reply(command,true,"",op+" APPLIED",new{items=state.Count});
        }
        if(op.StartsWith("REMOVE_",StringComparison.Ordinal)||op.StartsWith("STOP_",StringComparison.Ordinal))
        {
            state.TryRemove(key,out _);
            return Reply(command,true,"",op+" APPLIED",new{items=state.Count});
        }
        state[key]=command.Payload.Clone();
        return Reply(command,true,"",op+" APPLIED",new{key,items=state.Count});
    }

    static string? OperationKey(ModuleKind kind,string operation,JsonElement payload)
    {
        string Id(string fallback)
        {
            if(payload.ValueKind==JsonValueKind.Object&&payload.TryGetProperty("id",out var id)&&id.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(id.GetString()))return id.GetString()!;
            return fallback;
        }
        return kind switch
        {
            ModuleKind.CgStudio when operation is "UPSERT_LAYER"=>"layer:"+Id("primary"),
            ModuleKind.CgStudio when operation is "REMOVE_LAYER"=>"layer:"+Id("primary"),
            ModuleKind.CgStudio when operation is "CLEAR_LAYERS" or "CLEAR_ALL"=>"layers",
            ModuleKind.ChromaStudio when operation is "SET_KEY" or "CLEAR_KEY"=>"key",
            ModuleKind.ChromaStudio when operation is "SET_SOURCE"=>"source",
            ModuleKind.ChromaStudio when operation is "SET_BACKGROUND" or "CLEAR_BACKGROUND"=>"background",
            ModuleKind.PlayoutEngine when operation is "SET_SOURCE" or "PLAY" or "PAUSE" or "STOP" or "SEEK"=>"transport",
            ModuleKind.OutputWorker when operation is "REGISTER_ROUTE" or "START_ROUTE" or "UPDATE_ROUTE"=>"route:"+Id("program"),
            ModuleKind.OutputWorker when operation is "STOP_ROUTE"=>"route:"+Id("program"),
            _=>null
        };
    }

    static string? ValidateRoute(OutputRouteRegistration? route)
    {
        if(route is null)return "Route payload is missing.";
        if(string.IsNullOrWhiteSpace(route.Id)||string.IsNullOrWhiteSpace(route.Kind))return "Route id/kind is required.";
        if(!File.Exists(route.FfmpegPath))return "Shared MediaCore ffmpeg.exe is missing: "+route.FfmpegPath;
        if(string.IsNullOrWhiteSpace(route.Destination))return "Output destination is required.";
        if(route.Width<64||route.Height<48||route.Width>4096||route.Height>2160)return "Output raster is outside the supported 64x48 to 4096x2160 range.";
        if(route.FramesPerSecond<1||route.FramesPerSecond>120)return "Output frame rate is outside the supported 1-120 fps range.";
        if(route.SampleRate is not (32000 or 44100 or 48000 or 96000))return "Unsupported output sample rate.";
        if(route.Channels<1||route.Channels>8)return "Unsupported output channel count.";
        if(route.VideoKbps<100||route.AudioKbps<32)return "Output bitrate is invalid.";
        if(!route.PixelFormat.Equals("bgra",StringComparison.OrdinalIgnoreCase))return "Shared Program pixel format must be BGRA.";
        if(string.IsNullOrWhiteSpace(route.FfmpegArgumentsTemplate)||
           !route.FfmpegArgumentsTemplate.Contains("{VIDEO_PIPE}",StringComparison.Ordinal)||
           !route.FfmpegArgumentsTemplate.Contains("{AUDIO_PIPE}",StringComparison.Ordinal))
            return "Exact FFmpeg arguments template with VIDEO_PIPE and AUDIO_PIPE placeholders is required.";
        return null;
    }

    static ModuleReply Reply(ModuleCommand command,bool success,string error,string message,object payload)=>
        new(command.RequestId,success,error,message,JsonSerializer.SerializeToElement(payload,ModuleJson.Options),DateTimeOffset.UtcNow);

    static string? ReadArg(string[] args,string name)
    {
        for(int i=0;i<args.Length-1;i++)if(args[i].Equals(name,StringComparison.OrdinalIgnoreCase))return args[i+1];
        return null;
    }
}

using SmartPlayout.Contracts;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;

namespace SmartPlayout.WorkerShared;

public sealed class OutputRouteEngine : IDisposable
{
    readonly object _sync=new();
    readonly Channel<byte[]> _video=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
    readonly Channel<byte[]> _audio=Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
    CancellationTokenSource? _cancel;
    Process? _process;
    OutputRouteRegistration? _route;
    public bool Running => _process is { HasExited:false };

    public void Start(OutputRouteRegistration route)
    {
        lock(_sync)
        {
            StopCore();
            string token=Guid.NewGuid().ToString("N");
            string videoPipe="SMARTPlayout.Output.Video."+token,audioPipe="SMARTPlayout.Output.Audio."+token;
            var videoServer=new NamedPipeServerStream(videoPipe,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536);
            var audioServer=new NamedPipeServerStream(audioPipe,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536);
            var cancel=new CancellationTokenSource();
            string args=BuildArguments(route,$@"\\.\pipe\{videoPipe}",$@"\\.\pipe\{audioPipe}");
            var process=new Process{StartInfo=new ProcessStartInfo(route.FfmpegPath,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true},EnableRaisingEvents=true};
            process.OutputDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))Console.WriteLine("OUTPUT ROUTE • "+e.Data);};
            process.ErrorDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))Console.Error.WriteLine("OUTPUT ROUTE • "+e.Data);};
            process.Exited+=(_,_)=>Console.Error.WriteLine("SP-OUTPUT-ROUTE-EXIT • "+SafeExitCode(process));
            if(!process.Start()){videoServer.Dispose();audioServer.Dispose();cancel.Dispose();throw new InvalidOperationException("FFmpeg output process could not start.");}
            process.BeginOutputReadLine();process.BeginErrorReadLine();
            _route=route;_cancel=cancel;_process=process;
            _=PumpAsync(videoServer,_video.Reader,cancel.Token,"VIDEO");
            _=PumpAsync(audioServer,_audio.Reader,cancel.Token,"AUDIO");
        }
    }

    public void PushVideo(SharedVideoFrame frame)
    {
        var route=_route;if(!Running||route is null||frame.Width!=route.Width||frame.Height!=route.Height)return;
        _video.Writer.TryWrite(frame.Pixels.AsSpan(0,frame.DataLength).ToArray());
    }

    public void PushAudio(SharedAudioPacket packet)
    {
        var route=_route;if(!Running||route is null||packet.SampleRate!=route.SampleRate||packet.Channels!=route.Channels)return;
        _audio.Writer.TryWrite(packet.Pcm.AsSpan(0,packet.DataLength).ToArray());
    }

    static async Task PumpAsync(NamedPipeServerStream pipe,ChannelReader<byte[]> reader,CancellationToken token,string label)
    {
        await using(pipe)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await foreach(var data in reader.ReadAllAsync(token).ConfigureAwait(false))
                    await pipe.WriteAsync(data,token).ConfigureAwait(false);
            }
            catch(OperationCanceledException){}
            catch(Exception ex){Console.Error.WriteLine($"SP-OUTPUT-{label}-PIPE • {ex.Message}");}
        }
    }

    static string BuildArguments(OutputRouteRegistration r,string videoPipe,string audioPipe)
    {
        if(string.IsNullOrWhiteSpace(r.FfmpegArgumentsTemplate))
            throw new InvalidOperationException("The exact FFmpeg route template is missing.");
        if(!r.FfmpegArgumentsTemplate.Contains("{VIDEO_PIPE}",StringComparison.Ordinal)||
           !r.FfmpegArgumentsTemplate.Contains("{AUDIO_PIPE}",StringComparison.Ordinal))
            throw new InvalidOperationException("The FFmpeg route template does not contain both shared pipe placeholders.");
        return r.FfmpegArgumentsTemplate
            .Replace("{VIDEO_PIPE}",Escape(videoPipe),StringComparison.Ordinal)
            .Replace("{AUDIO_PIPE}",Escape(audioPipe),StringComparison.Ordinal);
    }

    public void Stop(){lock(_sync)StopCore();}
    void StopCore()
    {
        _cancel?.Cancel();
        try{if(_process is { HasExited:false } p){p.Kill(true);p.WaitForExit(1500);}}catch{}
        try{_process?.Dispose();}catch{}
        _process=null;_route=null;_cancel?.Dispose();_cancel=null;
        while(_video.Reader.TryRead(out _)){}while(_audio.Reader.TryRead(out _)){}
    }
    static int SafeExitCode(Process p){try{return p.ExitCode;}catch{return -1;}}
    // ProcessStartInfo.Arguments expects the Windows named-pipe backslashes verbatim.
    // Only quotes need escaping here; doubling every backslash makes FFmpeg open a
    // different (invalid) pipe name.
    static string Escape(string value)=>value.Replace("\"","\\\"");
    public void Dispose()=>Stop();
}

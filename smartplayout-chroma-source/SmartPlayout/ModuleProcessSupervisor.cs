using SmartPlayout.Contracts;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Timer = System.Threading.Timer;

namespace FFmpegNativePlayer;

/// <summary>
/// Owns out-of-process module lifetimes. A CG/Chroma/output worker failure must
/// never terminate the operator UI or the main playout process.
/// </summary>
internal sealed class ModuleProcessSupervisor : IDisposable
{
    readonly ConcurrentDictionary<ModuleKind, ModuleSlot> _slots=new();
    readonly string _logDirectory=Path.Combine(AppContext.BaseDirectory,"RuntimeData","ModuleLogs");
    readonly string _instanceToken=$"{Environment.ProcessId}.{Guid.NewGuid():N}";
    readonly Timer _watchdog;
    bool _disposed;

    public ModuleProcessSupervisor() => _watchdog=new Timer(CheckHealth,null,2000,2000);

    public event Action<ModuleKind,ModuleState,string>? StateChanged;

    public ModuleState State(ModuleKind kind) =>
        _slots.TryGetValue(kind,out var slot) ? slot.State : ModuleState.Stopped;

    public int? ProcessId(ModuleKind kind) =>
        _slots.TryGetValue(kind,out var slot) && slot.Process is { HasExited:false } p ? p.Id : null;

    public void Register(ModuleKind kind,string executable,string arguments="",bool autoRestart=true)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        string fullPath=Path.GetFullPath(executable);
        string pipeName=$"SMARTPlayout.{kind}.{_instanceToken}";
        string launchArguments=string.IsNullOrWhiteSpace(arguments)?$"--pipe {pipeName}":arguments+$" --pipe {pipeName}";
        _slots.AddOrUpdate(kind,
            _=>new ModuleSlot(kind,fullPath,launchArguments,pipeName,autoRestart),
            (_,old)=>{ StopSlot(old,"MODULE REGISTRATION UPDATED"); return new(kind,fullPath,launchArguments,pipeName,autoRestart); });
    }

    public bool Start(ModuleKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!_slots.TryGetValue(kind,out var slot))
            throw new InvalidOperationException($"Module {kind} is not registered.");
        lock(slot.Sync)
        {
            if(slot.Process is { HasExited:false }) return true;
            if(!File.Exists(slot.Executable))
            {
                SetState(slot,ModuleState.Faulted,$"SP-MODULE-EXE-MISSING • {slot.Executable}");
                return false;
            }
            Directory.CreateDirectory(_logDirectory);
            slot.IntentionalStop=false;
            SetState(slot,ModuleState.Starting,"STARTING");
            try
            {
                var psi=new ProcessStartInfo(slot.Executable,slot.Arguments)
                {
                    UseShellExecute=false,
                    CreateNoWindow=true,
                    RedirectStandardOutput=true,
                    RedirectStandardError=true,
                    WorkingDirectory=Path.GetDirectoryName(slot.Executable)!
                };
                var process=new Process{StartInfo=psi,EnableRaisingEvents=true};
                process.OutputDataReceived+=(_,e)=>HandleOutput(slot,e.Data);
                process.ErrorDataReceived+=(_,e)=>WriteLog(slot,e.Data);
                process.Exited+=(_,_)=>OnExited(slot,process.ExitCode);
                if(!process.Start()) throw new InvalidOperationException("Process.Start returned false.");
                slot.Process=process;
                slot.LastHeartbeatUtc=DateTimeOffset.UtcNow;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                SetState(slot,ModuleState.Ready,$"READY • PID {process.Id}");
                return true;
            }
            catch(Exception ex)
            {
                WriteLog(slot,"SP-MODULE-START-FAILED • "+ex);
                SetState(slot,ModuleState.Faulted,"SP-MODULE-START-FAILED • "+ex.Message);
                return false;
            }
        }
    }

    public void Stop(ModuleKind kind,string reason="STOPPED BY OPERATOR")
    {
        if(_slots.TryGetValue(kind,out var slot)) StopSlot(slot,reason);
    }

    public async Task<ModuleReply> SendAsync(ModuleKind kind,string operation,object? payload=null,CancellationToken cancellationToken=default)
    {
        if(!_slots.TryGetValue(kind,out var slot))throw new InvalidOperationException($"Module {kind} is not registered.");
        if(!Start(kind))throw new InvalidOperationException($"Module {kind} could not be started. See RuntimeData\\ModuleLogs\\{kind}.log.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe=new NamedPipeClientStream(".",slot.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true};
        using var reader=new StreamReader(pipe,Encoding.UTF8,false,4096,true);
        var command=new ModuleCommand(Guid.NewGuid().ToString("N"),kind,operation,
            JsonSerializer.SerializeToElement(payload??new{},ModuleJson.Options),DateTimeOffset.UtcNow);
        await writer.WriteLineAsync(ModuleJson.Serialize(command)).ConfigureAwait(false);
        string? line=await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return ModuleJson.Deserialize<ModuleReply>(line??"")??throw new InvalidDataException($"Module {kind} returned an empty reply.");
    }

    void StopSlot(ModuleSlot slot,string reason)
    {
        lock(slot.Sync)
        {
            slot.IntentionalStop=true;
            if(slot.Process is { HasExited:false } p)
            {
                try{p.Kill(true);p.WaitForExit(1500);}catch(Exception ex){WriteLog(slot,"STOP WARNING • "+ex.Message);}
            }
            try{slot.Process?.Dispose();}catch{}
            slot.Process=null;
            SetState(slot,ModuleState.Stopped,reason);
        }
    }

    void OnExited(ModuleSlot slot,int exitCode)
    {
        bool restart;
        lock(slot.Sync)
        {
            WriteLog(slot,$"SP-MODULE-EXIT • code={exitCode} • intentional={slot.IntentionalStop}");
            bool intentional=slot.IntentionalStop||_disposed;
            restart=!intentional&&slot.AutoRestart;
            slot.Process=null;
            SetState(slot,restart?ModuleState.Reconnecting:intentional?ModuleState.Stopped:ModuleState.Faulted,
                restart?$"SP-MODULE-EXIT-{exitCode} • RECONNECTING":intentional?"STOPPED":$"SP-MODULE-EXIT-{exitCode}");
        }
        if(restart) _=Task.Run(async()=>{await Task.Delay(1000).ConfigureAwait(false);Start(slot.Kind);});
    }

    void WriteLog(ModuleSlot slot,string? line)
    {
        if(string.IsNullOrWhiteSpace(line))return;
        try
        {
            Directory.CreateDirectory(_logDirectory);
            File.AppendAllText(Path.Combine(_logDirectory,slot.Kind+".log"),
                $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}");
        }
        catch{}
    }

    void HandleOutput(ModuleSlot slot,string? line)
    {
        WriteLog(slot,line);
        if(string.IsNullOrWhiteSpace(line)||line[0]!='{')return;
        try
        {
            var heartbeat=ModuleJson.Deserialize<ModuleHeartbeat>(line);
            if(heartbeat is null||heartbeat.Module!=slot.Kind)return;
            slot.LastHeartbeatUtc=heartbeat.TimestampUtc;
            slot.LastCpuPercent=heartbeat.CpuPercent;
            slot.LastWorkingSetBytes=heartbeat.WorkingSetBytes;
            slot.LastFrameNumber=heartbeat.LastFrameNumber;
            slot.LastAudioPacketNumber=heartbeat.LastAudioPacketNumber;
            slot.AvDeltaTicks=heartbeat.AvDeltaTicks;
            if(slot.Kind==ModuleKind.OutputWorker&&heartbeat.LastFrameNumber>0&&heartbeat.LastAudioPacketNumber>0&&Math.Abs(heartbeat.AvDeltaTicks)>TimeSpan.FromMilliseconds(200).Ticks)
                WriteLog(slot,$"SP-OUTPUT-AV-DELTA • {heartbeat.AvDeltaTicks/(double)TimeSpan.TicksPerMillisecond:0.0} ms");
            if(slot.State!=heartbeat.State)SetState(slot,heartbeat.State,$"HEARTBEAT • PID {heartbeat.ProcessId}");
        }
        catch{}
    }

    void CheckHealth(object? _)
    {
        if(_disposed)return;
        DateTimeOffset now=DateTimeOffset.UtcNow;
        foreach(var slot in _slots.Values)
        {
            lock(slot.Sync)
            {
                if(slot.Process is not { HasExited:false } process||slot.IntentionalStop)continue;
                if(now-slot.LastHeartbeatUtc<=TimeSpan.FromSeconds(6))continue;
                WriteLog(slot,"SP-MODULE-HEARTBEAT-TIMEOUT • worker will restart");
                SetState(slot,ModuleState.Reconnecting,"SP-MODULE-HEARTBEAT-TIMEOUT");
                try{process.Kill(true);}catch(Exception ex){WriteLog(slot,"WATCHDOG KILL WARNING • "+ex.Message);}
            }
        }
    }

    void SetState(ModuleSlot slot,ModuleState state,string detail)
    {
        slot.State=state;
        WriteLog(slot,state+" • "+detail);
        try{StateChanged?.Invoke(slot.Kind,state,detail);}catch{}
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _watchdog.Dispose();
        foreach(var slot in _slots.Values)StopSlot(slot,"APPLICATION SHUTDOWN");
        _slots.Clear();
    }

    sealed class ModuleSlot(ModuleKind kind,string executable,string arguments,string pipeName,bool autoRestart)
    {
        public object Sync { get; }=new();
        public ModuleKind Kind { get; }=kind;
        public string Executable { get; }=executable;
        public string Arguments { get; }=arguments;
        public string PipeName { get; }=pipeName;
        public bool AutoRestart { get; }=autoRestart;
        public Process? Process { get; set; }
        public ModuleState State { get; set; }=ModuleState.Stopped;
        public bool IntentionalStop { get; set; }
        public DateTimeOffset LastHeartbeatUtc { get; set; }
        public double LastCpuPercent { get; set; }
        public long LastWorkingSetBytes { get; set; }
        public long LastFrameNumber { get; set; }
        public long LastAudioPacketNumber { get; set; }
        public long AvDeltaTicks { get; set; }
    }
}

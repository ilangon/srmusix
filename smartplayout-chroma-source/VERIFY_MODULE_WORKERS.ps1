param([string]$Root = (Join-Path $PSScriptRoot 'PLAYOUT'))
$ErrorActionPreference = 'Stop'
$report = Join-Path $PSScriptRoot 'MODULE_WORKER_VERIFICATION.log'
"SMART PLAYOUT MODULE WORKER VERIFICATION`r`n$(Get-Date -Format o)" | Set-Content -LiteralPath $report -Encoding UTF8

$workers = @(
    @{ Name='PlayoutEngine'; Exe='SMARTPlayout.Engine.Worker.exe'; Target=0 },
    @{ Name='CgStudio';      Exe='SMARTPlayout.CG.Worker.exe';     Target=1 },
    @{ Name='ChromaStudio';  Exe='SMARTPlayout.Chroma.Worker.exe'; Target=2 },
    @{ Name='OutputWorker';  Exe='SMARTPlayout.Output.Worker.exe'; Target=3 }
)

foreach($worker in $workers) {
    $exe = Join-Path $Root $worker.Exe
    if(-not (Test-Path -LiteralPath $exe)) { throw "SP-WORKER-EXE-MISSING: $exe" }
    $stem = [IO.Path]::GetFileNameWithoutExtension($worker.Exe)
    foreach($extension in @('.dll', '.deps.json', '.runtimeconfig.json')) {
        $companion = Join-Path $Root ($stem + $extension)
        if(-not (Test-Path -LiteralPath $companion -PathType Leaf)) {
            throw "SP-WORKER-PACKAGE-INCOMPLETE: $companion"
        }
    }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $exe
    $pipeName = "SMARTPlayout.Verify.$($worker.Name).$PID.$([Guid]::NewGuid().ToString('N'))"
    $psi.Arguments = "--pipe `"$pipeName`""
    $psi.WorkingDirectory = $Root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    $started = $false
    $pipe = $null
    try {
        if(-not $process.Start()) { throw "SP-WORKER-START-FAILED: $($worker.Name)" }
        $started = $true
        $pipe = [IO.Pipes.NamedPipeClientStream]::new('.',$pipeName,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
        try {
            $pipe.Connect(5000)
            $reader = [IO.StreamReader]::new($pipe,[Text.Encoding]::UTF8,$false,4096,$true)
            $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false),4096,$true)
            $writer.AutoFlush = $true
            $requestId = [Guid]::NewGuid().ToString('N')
            $command = @{ requestId=$requestId; target=$worker.Target; operation='PING'; payload=@{}; createdUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Compress
            $writer.WriteLine($command)
            $replyLine = $reader.ReadLine()
            if([string]::IsNullOrWhiteSpace($replyLine)) { throw "SP-WORKER-PING-EMPTY: $($worker.Name)" }
            $reply = $replyLine | ConvertFrom-Json
            if(-not $reply.success -or $reply.requestId -ne $requestId) { throw "SP-WORKER-PING-FAILED: $($worker.Name) • REPLY=$replyLine" }

            if($worker.Name -eq 'OutputWorker') {
                $routeId = [Guid]::NewGuid().ToString('N')
                $route = @{
                    id='VERIFY'; kind='Rtmp'; ffmpegPath=(Join-Path $Root 'Runtime\MediaCore\FFmpeg\ffmpeg.exe')
                    destination='rtmp://127.0.0.1/live/verify'; width=1920; height=1080; framesPerSecond=50
                    sampleRate=48000; channels=2; videoEncoder='libx264'; audioEncoder='aac'; videoKbps=5000
                    audioKbps=192; pixelFormat='bgra'; sharedFrameName='SMARTPlayout.Program.BGRA'
                    sharedAudioName='SMARTPlayout.Program.PCM'; interlaced=$false
                    ffmpegArgumentsTemplate='-hide_banner -f rawvideo -i "{VIDEO_PIPE}" -f s16le -i "{AUDIO_PIPE}" -f null NUL'
                }
                $register = @{ requestId=$routeId; target=$worker.Target; operation='REGISTER_ROUTE'; payload=$route; createdUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Depth 6 -Compress
                $writer.WriteLine($register)
                $routeReply = $reader.ReadLine() | ConvertFrom-Json
                if(-not $routeReply.success -or $routeReply.requestId -ne $routeId) { throw "SP-OUTPUT-ROUTE-CONTRACT-FAILED: $($routeReply.errorCode) $($routeReply.message)" }
                "[PASS] OutputWorker exact FFmpeg route-template contract" | Add-Content -LiteralPath $report
            }

            $heartbeatFound = $false
            for($i=0;$i -lt 4 -and -not $heartbeatFound;$i++) {
                $task = $process.StandardOutput.ReadLineAsync()
                if($task.Wait(2500)) {
                    $line = $task.Result
                    if($line -and $line.Contains('"timestampUtc"') -and $line.Contains('"processId"')) { $heartbeatFound = $true }
                } else { break }
            }
            if(-not $heartbeatFound) { throw "SP-WORKER-HEARTBEAT-MISSING: $($worker.Name)" }

            $shutdownId = [Guid]::NewGuid().ToString('N')
            $shutdown = @{ requestId=$shutdownId; target=$worker.Target; operation='SHUTDOWN'; payload=@{}; createdUtc=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json -Compress
            $writer.WriteLine($shutdown)
            $shutdownReply = $reader.ReadLine() | ConvertFrom-Json
            if(-not $shutdownReply.success) { throw "SP-WORKER-SHUTDOWN-FAILED: $($worker.Name)" }
            "[PASS] $($worker.Name) PID=$($process.Id) PIPE=$pipeName" | Add-Content -LiteralPath $report
            $writer.Dispose(); $reader.Dispose()
        } finally { if($pipe){$pipe.Dispose()} }
        if(-not $process.WaitForExit(5000)) { throw "SP-WORKER-CLEAN-EXIT-TIMEOUT: $($worker.Name)" }
        if($process.ExitCode -ne 0) { throw "SP-WORKER-EXIT-CODE-$($process.ExitCode): $($worker.Name)" }
    } finally {
        if($started -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(2000) | Out-Null }
        if($process){$process.Dispose()}
    }
}

"[PASS] ALL MODULE WORKERS" | Add-Content -LiteralPath $report
Write-Host "Module workers verified. Report: $report"

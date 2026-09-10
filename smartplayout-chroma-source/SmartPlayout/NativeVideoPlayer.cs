using FFmpeg.AutoGen.Abstractions;
using SmartPlayout.CodecsFFM;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FFmpegNativePlayer;

public unsafe sealed class NativeVideoPlayer : IDisposable
{
    public event Action<BitmapSource>? FrameReady;
    public event Action<string>? Log;
    public event Action? Ended;

    public Func<double?>? MasterClockSeconds { get; set; }

    // AUTO by default. When disabled, the proven software decoder path is used.
    public bool HardwareDecodeEnabled { get; set; } = true;
    public bool DisableAutomaticLetterboxCrop { get; set; }

    // MAIN OUTPUT VIDEO PROCESSING. Neutral defaults preserve the proven decoder path.
    public double OutputBrightness { get; set; } = 0.0;
    public double OutputContrast { get; set; } = 1.0;
    public double OutputSaturation { get; set; } = 1.0;
    public double OutputGamma { get; set; } = 1.0;
    public double OutputBlackLevel { get; set; } = 0.0;
    public double OutputYLevel { get; set; } = 1.0;
    public double OutputULevel { get; set; } = 1.0;
    public double OutputVLevel { get; set; } = 1.0;
    public double OutputSharpness { get; set; } = 0.0;
    public double OutputNoiseReduction { get; set; } = 0.0; // 0..1 conservative spatial restoration
    public bool OutputAutoColor { get; set; } = false;
    public bool OutputAutoQualityEnhance { get; set; } = false;
    public double OutputAutoQualityStrength { get; set; } = 0.55;
    public bool OutputAutoWhiteBalance { get; set; } = false;
    public double OutputWhiteTemperature { get; set; } = 0.0; // -100 cool .. +100 warm
    public double OutputWhiteTint { get; set; } = 0.0;        // -100 green .. +100 magenta
    public int OutputScanMode { get; set; } = 0; // 0 Auto, 1 Progressive, 2 Interlaced, 3 Deinterlace
    // PERFORMANCE: decoding/audio timing can continue while the window is minimized,
    // but expensive BGRA presentation work can be suspended after the first frame.
    // This does NOT change transport, seek, sync, hardware-decode, or audio behavior.
    public volatile bool PresentationEnabled = true;
    // Standalone backend Auto Color: conservative exposure correction only.
    // Good/normal frames remain untouched; correction is smoothed to avoid flicker.
    private double _autoColorGain = 1.0;
    private double _autoColorOffset = 0.0;
    private double _autoWbR = 1.0, _autoWbG = 1.0, _autoWbB = 1.0;
    private volatile int _currentSourceWidth, _currentSourceHeight;
    public string ActiveDecodeBackend { get; private set; } = "Software";
    public double CurrentAvDriftMs { get; private set; }

    private string? _file;
    private CancellationTokenSource? _cts;
    private Task? _decodeTask;
    private volatile bool _paused;
    private volatile bool _initialized;
    private double _seekRequestSeconds = -1;
    // After a BACKWARD keyframe seek, do not display preroll frames before the requested target.
    private double _seekDiscardUntilSeconds = -1;
    private TaskCompletionSource<double>? _seekCompletion;
    private TaskCompletionSource<bool>? _firstFrameCompletion;
    private double _positionSeconds;
    private double _durationSeconds;
    // Common container timeline origin. MPEG-PS/TS/VOB/DAT often start at a non-zero PTS.
    // Present positions to the UI as a zero-based timeline while preserving A/V offsets.
    private double _timelineOriginSeconds;
    // Presentation target in physical-independent pixels. Decode remains at source quality;
    // only the display conversion is scaled to the current viewport to avoid converting/copying
    // full 4K/8K BGRA frames when the player window is much smaller.
    private volatile int _renderTargetWidth;
    private volatile int _renderTargetHeight;

    // v0.6.8.50.14: deterministic decoder backend ownership.
    // Primary = bundled SMART FFmpeg decoder. If it cannot open/probe or cannot produce
    // the first video frame promptly, switch ONCE for that clip to the Windows media stack
    // (Media Foundation-backed MediaPlayer) without searching any other FFmpeg folders.
    private volatile bool _ffmpegOpenHealthy = true;
    private volatile bool _usingMicrosoftFallback;
    private MediaPlayer? _mfPlayer;
    private DispatcherTimer? _mfFrameTimer;
    private bool _mfEndedRaised;

    public double PositionSeconds => _positionSeconds;
    public double DurationSeconds => _durationSeconds;
    public bool IsSeeking => !_usingMicrosoftFallback && (_seekRequestSeconds >= 0 || _seekDiscardUntilSeconds >= 0);
    public Task FirstFrameReadyTask => _firstFrameCompletion?.Task ?? Task.CompletedTask;

    public void SetRenderTarget(int width, int height)
    {
        _renderTargetWidth = Math.Max(0, width);
        _renderTargetHeight = Math.Max(0, height);
    }

    private static (int top, int bottom) DetectStableLetterbox(byte[] bgra, int width, int height, int stride)
    {
        // v1.6.11: robust SD/VCD active-picture detector.
        // Old detector stopped immediately when a VHS/VCD overscan noise line, subtitle/logo edge,
        // or head-switching line appeared inside an otherwise-black bar. That is why some .MPG/.DAT
        // files stayed as a small letterboxed picture while .M2P happened to scale correctly.
        //
        // This remains presentation-only: decoder, hardware path, timestamps and source pixels
        // are untouched. We only crop stable symmetric black bars after repeated confirmation.
        if (width < 300 || height < 220) return (0, 0);

        bool RowLooksLikeBar(int y)
        {
            // Sample the centre 64% so station logos/corner bugs do not defeat bar detection.
            int startX = Math.Max(0, (int)(width * 0.18));
            int endX = Math.Min(width, (int)(width * 0.82));
            int dark = 0, total = 0;

            for (int x = startX; x < endX; x += 6)
            {
                int i = y * stride + x * 4;
                if (i + 2 >= bgra.Length) break;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                int luma = (r * 54 + g * 183 + b * 19) >> 8;
                if (luma <= 34) dark++;
                total++;
            }

            // Allow sparse analogue/VCD noise and white dash lines in an otherwise black bar.
            return total > 0 && dark >= (int)Math.Ceiling(total * 0.82);
        }

        int FindTopBar()
        {
            int maxBar = (int)(height * 0.30);
            int nonBarRun = 0;
            for (int y = 0; y < maxBar; y++)
            {
                if (RowLooksLikeBar(y))
                {
                    nonBarRun = 0;
                    continue;
                }

                nonBarRun++;
                // Require 4 consecutive active-looking rows before declaring picture start.
                // This ignores one/two noisy scan lines inside the black bar.
                if (nonBarRun >= 4)
                    return Math.Max(0, y - nonBarRun + 1);
            }
            return 0;
        }

        int FindBottomBar()
        {
            int maxBar = (int)(height * 0.30);
            int nonBarRun = 0;
            for (int d = 0; d < maxBar; d++)
            {
                int y = height - 1 - d;
                if (RowLooksLikeBar(y))
                {
                    nonBarRun = 0;
                    continue;
                }

                nonBarRun++;
                if (nonBarRun >= 4)
                    return Math.Max(0, d - nonBarRun + 1);
            }
            return 0;
        }

        int top = FindTopBar();
        int bottom = FindBottomBar();

        int minBar = Math.Max(8, (int)(height * 0.045));
        if (top < minBar || bottom < minBar) return (0, 0);

        // Require broadly symmetric letterbox bars. VCD/analogue captures may differ slightly.
        if (Math.Abs(top - bottom) > Math.Max(14, (int)(height * 0.055))) return (0, 0);

        int activeHeight = height - top - bottom;
        if (activeHeight < height / 2) return (0, 0);

        double activeAspect = (double)width / activeHeight;
        // Keep this conservative enough to reject ordinary 4:3 content.
        // Legacy AVI/DivX/VCD masters can use shallower letterbox bars; accepting from
        // ~1.43 still requires symmetric confirmed black bars, so ordinary 4:3 frames
        // without bars are not cropped.
        if (activeAspect < 1.43 || activeAspect > 2.40) return (0, 0);

        return (top, bottom);
    }

    private void Emit(string message) => Log?.Invoke(message);

    internal void EnsureFFmpegInitialized() => EnsureFFmpeg();

    private void EnsureFFmpeg()
    {
        if (_initialized) return;
        try
        {
            string libPath = DecoderRuntimeHost.EnsureInitialized(AppContext.BaseDirectory);
            Emit($"DECODER FFMPEG PATH: {libPath}");
            Emit($"DECODER FFmpeg native API loaded ✓ {ffmpeg.av_version_info() ?? "unknown"}");
            _initialized = true;
        }
        catch (Exception ex)
        {
            Emit($"FFmpeg INIT EXCEPTION: {ex.GetType().FullName}");
            Emit(ex.ToString());
            throw new InvalidOperationException("FFmpeg decoder runtime failed to initialize. Details: " + ex.Message, ex);
        }
    }

    // v0.6.8.50.13: decoder ownership is strict.
    // Never search the app tree/PATH for avcodec; all native media DLLs come from Runtime\MediaCore\FFmpeg.


    public Task OpenAsync(string file)
    {
        Stop();
        _file = file;
        _positionSeconds = 0;
        _durationSeconds = 0;
        CurrentAvDriftMs = 0;
        _ffmpegOpenHealthy = true;
        _usingMicrosoftFallback = false;

        return Task.Run(() =>
        {
            try
            {
                EnsureFFmpeg();
                var compatibility = FfmpegMediaProbe.RequirePlayableVideo(file, AppContext.BaseDirectory);
                Emit($"MEDIA GATE ✓ container={compatibility.Container} video={compatibility.VideoCodec} stream=#{compatibility.VideoStreamIndex} {compatibility.Width}x{compatibility.Height} fps={compatibility.FramesPerSecond:0.###} audioTracks={compatibility.AudioStreams.Count}");
                Probe(file);
            }
            catch (Exception ex)
            {
                _ffmpegOpenHealthy = false;
                ActiveDecodeBackend = "Microsoft Media Foundation (armed)";
                Emit("SMART FFMPEG DECODER OPEN FAILED — MICROSOFT FALLBACK ARMED");
                Emit(ex.Message);
            }
        });
    }

    private void Probe(string file)
    {
        AVFormatContext* format = null;
        try
        {
            Emit("OPEN INPUT ...");
            ThrowIfError(ffmpeg.avformat_open_input(&format, file, null, null));
            Emit("OPEN INPUT ✓");

            Emit("FIND STREAM INFO ...");
            ThrowIfError(ffmpeg.avformat_find_stream_info(format, null));
            Emit("FIND STREAM INFO ✓");

            int videoIndex = FindBestVideoStream(format, out AVCodec* decoder);
            AVStream* stream = format->streams[videoIndex];
            AVCodecParameters* parameters = stream->codecpar;

            _durationSeconds = format->duration > 0
                ? format->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            _timelineOriginSeconds = ComputeCommonTimelineOriginSeconds(format);

            Emit($"VIDEO STREAM: #{videoIndex}");
            Emit($"CODEC: {Ptr(decoder->name)}");
            Emit($"RESOLUTION: {parameters->width}x{parameters->height}");
            Emit($"DURATION: {_durationSeconds:0.###} sec");
            Emit($"TIMELINE ORIGIN: {_timelineOriginSeconds:0.###} sec");

            if (HardwareDecodeEnabled)
                EmitHardwareSupport(decoder);
            else
                Emit("HW DECODE: OFF — software baseline selected");

            Emit("READY TO PLAY ✓");
        }
        finally
        {
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private static double ComputeCommonTimelineOriginSeconds(AVFormatContext* format)
    {
        // Prefer the demuxer's common container start time. If it is unavailable (common with
        // damaged/legacy MPEG-PS/TS/VOB/DAT), derive ONE common origin from the earliest valid
        // stream start time. Video and audio open separate AVFormatContexts, so the calculation
        // must be deterministic across all streams rather than using only the selected stream.
        if (format != null && format->start_time != ffmpeg.AV_NOPTS_VALUE)
            return format->start_time / (double)ffmpeg.AV_TIME_BASE;

        double earliest = double.PositiveInfinity;
        if (format != null)
        {
            for (int i = 0; i < (int)format->nb_streams; i++)
            {
                AVStream* st = format->streams[i];
                if (st == null || st->start_time == ffmpeg.AV_NOPTS_VALUE) continue;
                double value = st->start_time * ffmpeg.av_q2d(st->time_base);
                if (double.IsFinite(value) && value < earliest) earliest = value;
            }
        }
        return double.IsFinite(earliest) ? earliest : 0.0;
    }

    private void EmitHardwareSupport(AVCodec* decoder)
    {
        bool any = false;
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(decoder, i);
            if (cfg == null) break;

            // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX == 0x01
            if ((cfg->methods & 0x01) != 0)
            {
                any = true;
                Emit($"HW CONFIG ✓ {DeviceLabel(cfg->device_type)}");
            }
        }

        if (!any)
            Emit("HW CONFIG: decoder exposes no HW_DEVICE_CTX path; software fallback will be used.");
    }

    public Task PlayAsync()
    {
        if (_file == null) return Task.CompletedTask;

        if (_usingMicrosoftFallback)
        {
            _paused = false;
            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => _mfPlayer?.Play()));
            Emit("RESUME [Microsoft Media Foundation]");
            return Task.CompletedTask;
        }

        if (_decodeTask is { IsCompleted: false })
        {
            _paused = false;
            Emit("RESUME");
            return Task.CompletedTask;
        }

        _paused = false;
        _firstFrameCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_ffmpegOpenHealthy)
        {
            return StartMicrosoftFallbackAsync(_file, 0);
        }

        _cts = new CancellationTokenSource();
        var localCts = _cts;
        _decodeTask = Task.Run(() => DecodeWithFallback(_file, localCts.Token));
        _ = WatchFirstFrameAndFallbackAsync(_file, localCts);
        return Task.CompletedTask;
    }

    private Task WatchFirstFrameAndFallbackAsync(string file, CancellationTokenSource ffmpegCts)
    {
        // NativeVideoPlayer is an unsafe FFmpeg owner. C# does not permit await in an
        // unsafe context, so keep the watchdog fully task-based and never block the decode thread.
        return Task.Delay(2200, ffmpegCts.Token).ContinueWith(delayTask =>
        {
            if (delayTask.IsCanceled || ffmpegCts.IsCancellationRequested || _usingMicrosoftFallback)
                return Task.CompletedTask;
            if (_firstFrameCompletion?.Task.IsCompleted == true)
                return Task.CompletedTask;

            Emit("SMART FFMPEG FIRST-FRAME TIMEOUT — MICROSOFT MEDIA FOUNDATION FALLBACK");
            try { ffmpegCts.Cancel(); } catch { }

            try
            {
                return StartMicrosoftFallbackAsync(file, Math.Max(0, _positionSeconds));
            }
            catch (Exception ex)
            {
                Emit("MICROSOFT FALLBACK WATCHDOG ERROR: " + ex.Message);
                return Task.CompletedTask;
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
    }

    private Task StartMicrosoftFallbackAsync(string file, double startSeconds)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        return dispatcher.InvokeAsync(() =>
        {
            StopMicrosoftFallbackOnUiThread();
            _usingMicrosoftFallback = true;
            _mfEndedRaised = false;
            ActiveDecodeBackend = "Microsoft Media Foundation";
            CurrentAvDriftMs = 0;

            var player = new MediaPlayer
            {
                Volume = 0.0, // NativeAudioPlayer remains the single Program audio path.
                ScrubbingEnabled = true
            };
            _mfPlayer = player;

            player.MediaOpened += (_, __) =>
            {
                if (!ReferenceEquals(_mfPlayer, player)) return;
                if (player.NaturalDuration.HasTimeSpan)
                    _durationSeconds = player.NaturalDuration.TimeSpan.TotalSeconds;
                if (startSeconds > 0.01)
                    player.Position = TimeSpan.FromSeconds(startSeconds);
                Emit($"MICROSOFT MEDIA FOUNDATION OPEN ✓ {player.NaturalVideoWidth}x{player.NaturalVideoHeight}");
            };
            player.MediaFailed += (_, e) =>
            {
                Emit("MICROSOFT MEDIA FOUNDATION DECODE FAILED ✗ " + (e.ErrorException?.Message ?? "unknown error"));
                _firstFrameCompletion?.TrySetException(e.ErrorException ?? new InvalidOperationException("Microsoft decoder failed."));
            };
            player.MediaEnded += (_, __) =>
            {
                if (_mfEndedRaised) return;
                _mfEndedRaised = true;
                Emit("MICROSOFT MEDIA FOUNDATION VIDEO END ✓");
                Ended?.Invoke();
            };

            player.Open(new Uri(file, UriKind.Absolute));
            player.Play();

            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _mfFrameTimer = timer;
            timer.Tick += (_, __) =>
            {
                if (!_usingMicrosoftFallback || !ReferenceEquals(_mfPlayer, player)) return;
                if (_paused) return;
                int naturalW = player.NaturalVideoWidth;
                int naturalH = player.NaturalVideoHeight;
                if (naturalW <= 0 || naturalH <= 0) return;

                int targetW = _renderTargetWidth > 0 ? _renderTargetWidth : naturalW;
                int targetH = _renderTargetHeight > 0 ? _renderTargetHeight : naturalH;
                targetW = Math.Max(2, targetW);
                targetH = Math.Max(2, targetH);

                double scale = Math.Min((double)targetW / naturalW, (double)targetH / naturalH);
                int drawW = Math.Max(2, (int)Math.Round(naturalW * scale));
                int drawH = Math.Max(2, (int)Math.Round(naturalH * scale));
                double x = (targetW - drawW) / 2.0;
                double y = (targetH - drawH) / 2.0;

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, targetW, targetH));
                    dc.DrawVideo(player, new Rect(x, y, drawW, drawH));
                }
                var bmp = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                bmp.Freeze();
                _positionSeconds = player.Position.TotalSeconds;
                FrameReady?.Invoke(bmp);
                if (_firstFrameCompletion?.Task.IsCompleted != true)
                {
                    Emit("FIRST DECODED FRAME ✓ [Microsoft Media Foundation]");
                    Emit("VIDEO PLAYBACK ACTIVE ✓ [Microsoft Media Foundation]");
                    _firstFrameCompletion?.TrySetResult(true);
                }
            };
            timer.Start();
            Emit("VIDEO DECODER LOCK ✓ Microsoft Media Foundation (clip fallback)");
        }).Task;
    }

    private void StopMicrosoftFallbackOnUiThread()
    {
        try { _mfFrameTimer?.Stop(); } catch { }
        _mfFrameTimer = null;
        try { _mfPlayer?.Stop(); _mfPlayer?.Close(); } catch { }
        _mfPlayer = null;
        _usingMicrosoftFallback = false;
    }

    public void TogglePause()
    {
        _paused = !_paused;
        if (_usingMicrosoftFallback)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _ = dispatcher.BeginInvoke(new Action(() =>
            {
                if (_paused) _mfPlayer?.Pause(); else _mfPlayer?.Play();
            }));
        }
        Emit(_paused ? "PAUSE" : "RESUME");
    }

    public void Stop()
    {
        if (_usingMicrosoftFallback || _mfPlayer != null)
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
                if (dispatcher.CheckAccess()) StopMicrosoftFallbackOnUiThread();
                else dispatcher.Invoke(StopMicrosoftFallbackOnUiThread);
            }
            catch { }
        }

        // STOP is a hard transport reset.  Cancel and WAIT briefly for the previous
        // decode worker to leave before a later PLAY is allowed to create a new worker.
        // Without this barrier, rapid STOP -> PLAY could see the old _decodeTask still
        // running and PlayAsync() would treat it as a RESUME, occasionally continuing
        // from the old timeline instead of 00:00.
        var oldCts = _cts;
        var oldTask = _decodeTask;
        try { oldCts?.Cancel(); } catch { }
        try
        {
            if (oldTask != null && !oldTask.IsCompleted)
                oldTask.Wait(1200);
        }
        catch { /* cancellation/decoder shutdown is expected during STOP */ }

        _decodeTask = null;
        _cts = null;
        try { oldCts?.Dispose(); } catch { }

        _paused = false;
        _seekRequestSeconds = -1;
        _seekDiscardUntilSeconds = -1;
        Interlocked.Exchange(ref _seekCompletion, null)?.TrySetCanceled();
        Interlocked.Exchange(ref _firstFrameCompletion, null)?.TrySetCanceled();
        _positionSeconds = 0;
        CurrentAvDriftMs = 0;
        Emit("VIDEO STOP RESET ✓ 0.000 sec");
    }

    public Task SeekAsync(double seconds)
    {
        double target = Math.Max(0, seconds);
        if (_usingMicrosoftFallback)
        {
            _positionSeconds = target;
            var dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            return dispatcher.InvokeAsync(() =>
            {
                if (_mfPlayer != null) _mfPlayer.Position = TimeSpan.FromSeconds(target);
            }).Task;
        }
        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = Interlocked.Exchange(ref _seekCompletion, completion);
        previous?.TrySetCanceled();
        _seekRequestSeconds = target;
        _seekDiscardUntilSeconds = target;
        _positionSeconds = target;
        CurrentAvDriftMs = 0;
        _paused = false;

        // This class is unsafe because it owns FFmpeg pointer state, so C# does not allow
        // an await expression here. Return the completion task directly instead. The task
        // completes when DecodeLoop locks the first frame at/after the requested target.
        var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reg = timeout.Token.Register(() => completion.TrySetResult(target));
        _ = completion.Task.ContinueWith(_ =>
        {
            reg.Dispose();
            timeout.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    private void DecodeWithFallback(string file, CancellationToken token)
    {
        if (HardwareDecodeEnabled)
        {
            try
            {
                DecodeLoop(file, token, tryHardware: true);
                return;
            }
            catch (HardwareDecodeException ex) when (!token.IsCancellationRequested)
            {
                Emit("HW DECODE FAILED — switching to proven SOFTWARE fallback");
                Emit("HW reason: " + ex.Message);
                ActiveDecodeBackend = "Software fallback";
                DecodeLoop(file, token, tryHardware: false);
                return;
            }
        }

        DecodeLoop(file, token, tryHardware: false);
    }

    private void DecodeLoop(string file, CancellationToken token, bool tryHardware)
    {
        AVFormatContext* format = null;
        AVCodecContext* codecContext = null;
        AVPacket* packet = null;
        AVFrame* frame = null;
        AVFrame* swFrame = null;
        SwsContext* sws = null;
        IntPtr convertedBuffer = IntPtr.Zero;

        try
        {
            EnsureFFmpeg();

            ThrowIfError(ffmpeg.avformat_open_input(&format, file, null, null));
            ThrowIfError(ffmpeg.avformat_find_stream_info(format, null));

            int videoIndex = FindBestVideoStream(format, out AVCodec* decoder);
            AVStream* videoStream = format->streams[videoIndex];

            codecContext = ffmpeg.avcodec_alloc_context3(decoder);
            if (codecContext == null)
                throw new ApplicationException("avcodec_alloc_context3 failed.");

            ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, videoStream->codecpar));

            string hwBackend = "";
            AVPixelFormat hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            if (tryHardware)
            {
                if (!TryAttachHardwareDevice(codecContext, decoder, out hwBackend, out hwPixelFormat))
                    throw new HardwareDecodeException("No compatible FFmpeg hardware device could be created for this decoder.");
            }

            int openResult = ffmpeg.avcodec_open2(codecContext, decoder, null);
            if (openResult < 0)
            {
                if (tryHardware)
                    throw new HardwareDecodeException("Hardware decoder open failed: " + ErrorText(openResult));
                ThrowIfError(openResult);
            }

            ActiveDecodeBackend = tryHardware ? hwBackend : "Software";
            Emit($"VIDEO DECODER LOCK ✓ SMART FFmpeg [{ActiveDecodeBackend}]");
            Emit($"DECODER OPEN ✓ {Ptr(decoder->name)}");
            Emit($"DECODE BACKEND ✓ {ActiveDecodeBackend}");

            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            swFrame = ffmpeg.av_frame_alloc();

            if (packet == null || frame == null || swFrame == null)
                throw new ApplicationException("Could not allocate AVPacket/AVFrame.");

            byte_ptr4 dstData = new();
            int4 dstLinesize = new();
            int swsWidth = 0;
            int swsHeight = 0;
            int swsOutputWidth = 0;
            int swsOutputHeight = 0;
            AVPixelFormat swsSourceFormat = AVPixelFormat.AV_PIX_FMT_NONE;
            int convertedBufferSize = 0;
            // PERFORMANCE: BitmapSource.Create copies from this array, so the same managed
            // staging buffer can safely be reused for later frames. This removes the largest
            // per-frame managed allocation and greatly reduces GC/RAM churn at HD resolutions.
            byte[]? managedPixelBuffer = null;

            bool firstPacket = false;
            bool firstFrame = false;
            var clock = Stopwatch.StartNew();
            double clockBasePts = 0;
            bool haveClockBase = false;
            // MPEG-family files can contain missing, backward or discontinuous timestamps.
            // Keep presentation monotonic without changing good MKV/MP4 timestamps.
            double nominalFrameDuration = 1.0 / 25.0;
            // v0.6.8.50.42: use libavformat's effective presentation cadence first.
            // Some MP4/MKV/MOV 50/59.94/60 fps sources report a misleading average rate;
            // pacing every decoded frame from that value makes Main Program video slow
            // while its independent audio stays real-time.
            AVRational guessedRate = ffmpeg.av_guess_frame_rate(format, videoStream, null);
            if (guessedRate.num <= 0 || guessedRate.den <= 0) guessedRate = videoStream->avg_frame_rate;
            if (guessedRate.num <= 0 || guessedRate.den <= 0) guessedRate = videoStream->r_frame_rate;
            if (guessedRate.num > 0 && guessedRate.den > 0)
            {
                double fps = ffmpeg.av_q2d(guessedRate);
                if (double.IsFinite(fps) && fps > 1.0 && fps < 240.0) nominalFrameDuration = 1.0 / fps;
            }
            double lastPresentedPts = double.NaN;
            // SD broadcast masters are sometimes 4:3 720x576 frames containing a baked-in
            // 16:9 picture with black bars. Detect stable top/bottom bars and crop only the
            // presentation bitmap; decode pixels/timestamps remain untouched.
            int cropCandidateTop = 0, cropCandidateBottom = 0, cropCandidateHits = 0;
            int lockedCropTop = 0, lockedCropBottom = 0;
            // Do not show the temporary outer/source-sized frame while letterbox fit is being learned.
            // Once the first frame indicates stable bars, hold only those few probe frames and reveal
            // the picture after the crop lock is established. Seek keeps the existing lock, so it does
            // not re-enter this gate.
            bool initialFitGateActive = false;
            int initialFitProbeFrames = 0;
            const int initialFitProbeLimit = 8;
            double timestampContinuityOffset = 0.0;
            int seekStableFrames = 0;

            while (!token.IsCancellationRequested)
            {
                while (_paused && !token.IsCancellationRequested)
                    Thread.Sleep(20);

                if (_seekRequestSeconds >= 0)
                {
                    double targetSeconds = _seekRequestSeconds;
                    _seekRequestSeconds = -1;

                    double absoluteTargetSeconds = targetSeconds + _timelineOriginSeconds;
                    long targetTs = (long)(absoluteTargetSeconds / ffmpeg.av_q2d(videoStream->time_base));
                    // v1.6.10 recovery: keep the v1.6.7 golden presentation/scaling path untouched.
                    // AVI/OpenDML alone gets a more tolerant indexed seek to avoid post-seek stalls.
                    // IMPORTANT: do not reset crop/scale/SAR/render state here; seek must preserve
                    // the already-stable presentation exactly like normal playback.
                    bool isAviSeek = string.Equals(
                        System.IO.Path.GetExtension(file), ".avi",
                        StringComparison.OrdinalIgnoreCase);

                    int seekResult;
                    if (isAviSeek)
                    {
                        // Legacy AVI/DivX: seek to a real keyframe inside a bounded window.
                        long window = (long)(2.0 / ffmpeg.av_q2d(videoStream->time_base));
                        long minTs = Math.Max(long.MinValue + 1, targetTs - window);
                        long maxTs = Math.Min(long.MaxValue - 1, targetTs + window);
                        seekResult = ffmpeg.avformat_seek_file(
                            format, videoIndex, minTs, targetTs, maxTs, ffmpeg.AVSEEK_FLAG_BACKWARD);

                        if (seekResult < 0)
                            seekResult = ffmpeg.av_seek_frame(
                                format, videoIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    }
                    else
                    {
                        seekResult = ffmpeg.av_seek_frame(
                            format, videoIndex, targetTs, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    }

                    if (seekResult >= 0)
                    {
                        ffmpeg.avcodec_flush_buffers(codecContext);
                        ffmpeg.av_frame_unref(frame);
                        ffmpeg.av_frame_unref(swFrame);
                        _seekDiscardUntilSeconds = targetSeconds;
                        _positionSeconds = targetSeconds;
                        CurrentAvDriftMs = 0;
                        haveClockBase = false;
                        lastPresentedPts = double.NaN;
                        timestampContinuityOffset = 0.0;
                        seekStableFrames = 0;
                        clock.Restart();
                        Emit($"SEEK REQUEST ✓ {targetSeconds:0.###} sec — preroll suppressed");
                    }
                    else
                        Emit($"SEEK FAILED: {ErrorText(seekResult)}");
                }

                int readResult = ffmpeg.av_read_frame(format, packet);
                if (readResult == ffmpeg.AVERROR_EOF)
                    break;

                ThrowIfError(readResult);

                try
                {
                    if (packet->stream_index != videoIndex)
                        continue;

                    if (!firstPacket)
                    {
                        firstPacket = true;
                        Emit("FIRST VIDEO PACKET ✓");
                    }

                    int sendResult = ffmpeg.avcodec_send_packet(codecContext, packet);
                    if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    {
                        if (tryHardware)
                            throw new HardwareDecodeException("Hardware send packet failed: " + ErrorText(sendResult));
                        ThrowIfError(sendResult);
                    }

                    while (!token.IsCancellationRequested)
                    {
                        int receiveResult = ffmpeg.avcodec_receive_frame(codecContext, frame);

                        if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                            receiveResult == ffmpeg.AVERROR_EOF)
                            break;

                        if (receiveResult < 0)
                        {
                            if (tryHardware)
                                throw new HardwareDecodeException("Hardware receive frame failed: " + ErrorText(receiveResult));
                            ThrowIfError(receiveResult);
                        }

                        AVFrame* displayFrame = frame;

                        if (tryHardware)
                        {
                            // A HW device context does not guarantee every decoded frame is a HW surface.
                            // Some FFmpeg decoders legitimately negotiate a software pixel format even when
                            // a compatible device is attached. Transferring such a software frame with
                            // av_hwframe_transfer_data() fails and used to force an unnecessary decoder restart.
                            // Transfer only when FFmpeg actually returned the selected HW pixel format.
                            AVPixelFormat decodedFormat = (AVPixelFormat)frame->format;
                            if (hwPixelFormat != AVPixelFormat.AV_PIX_FMT_NONE && decodedFormat == hwPixelFormat)
                            {
                                ffmpeg.av_frame_unref(swFrame);
                                int transfer = ffmpeg.av_hwframe_transfer_data(swFrame, frame, 0);
                                if (transfer < 0)
                                    throw new HardwareDecodeException("GPU->CPU frame transfer failed: " + ErrorText(transfer));
                                displayFrame = swFrame;
                            }
                        }

                        int width = displayFrame->width > 0 ? displayFrame->width : codecContext->width;
                        int height = displayFrame->height > 0 ? displayFrame->height : codecContext->height;
                        AVPixelFormat sourceFormat = (AVPixelFormat)displayFrame->format;

                        // Convert anamorphic/non-square-pixel SD sources to the correct square-pixel
                        // display aspect ratio before WPF scales the image. PAL/SD MPEG/VOB/DAT often
                        // stores 720x576 while signalling 4:3 or 16:9 through sample_aspect_ratio.
                        AVRational sar = displayFrame->sample_aspect_ratio;
                        if (sar.num <= 0 || sar.den <= 0) sar = videoStream->sample_aspect_ratio;
                        double sarValue = (sar.num > 0 && sar.den > 0) ? ffmpeg.av_q2d(sar) : 1.0;
                        if (!double.IsFinite(sarValue) || sarValue <= 0.0 || sarValue > 8.0) sarValue = 1.0;
                        double displayAspect = ((double)width * sarValue) / Math.Max(1, height);

                        // AVI-only legacy SD aspect recovery.
                        // Supplied failing sample: DIVX AVI, 720x480, SAR 1:1, reported DAR 3:2.
                        // Treat only 720/704x480 AVI with square-pixel metadata as legacy NTSC 4:3.
                        bool isAviPresentation = string.Equals(
                            System.IO.Path.GetExtension(file), ".avi",
                            StringComparison.OrdinalIgnoreCase);

                        bool legacyNtScAvi =
                            isAviPresentation &&
                            height == 480 &&
                            (width == 720 || width == 704) &&
                            Math.Abs(sarValue - 1.0) < 0.01;

                        if (legacyNtScAvi)
                        {
                            displayAspect = 4.0 / 3.0;
                            Emit($"AVI LEGACY SD ASPECT ✓ {width}x{height} SAR 1:1 -> display 4:3");
                        }

                        _currentSourceWidth = width; _currentSourceHeight = height;
                        int outputWidth = Math.Max(2, ((int)Math.Round(height * displayAspect)) & ~1);
                        int outputHeight = Math.Max(2, height & ~1);

                        // Viewport-aware downscale for large sources, but keep the corrected display
                        // aspect for SD. WPF Uniform then enlarges SD cleanly to the whole viewport
                        // (with only the expected pillar/letter bars; no small centered source box).
                        int targetWidth = _renderTargetWidth;
                        int targetHeight = _renderTargetHeight;
                        if (targetWidth > 0 && targetHeight > 0)
                        {
                            // Universal aspect-preserving Fit-to-Viewport.
                            // Resolution is never treated as the on-screen size. Whether the source is
                            // 320x240, 352x288, 640x480, 720x576, HD or 4K, calculate one uniform scale
                            // from the corrected display aspect and the CURRENT video viewport.
                            //
                            // This can upscale small legacy files and downscale large files, but it never
                            // stretches 4:3 into 16:9 and never crops the picture. Any remaining black
                            // pillar/letter space is the geometrically correct result of preserving aspect.
                            double fitScale = Math.Min(
                                (double)targetWidth / Math.Max(1, outputWidth),
                                (double)targetHeight / Math.Max(1, outputHeight));

                            if (double.IsFinite(fitScale) && fitScale > 0.0 &&
                                Math.Abs(fitScale - 1.0) > 0.01)
                            {
                                outputWidth = Math.Max(2, ((int)Math.Round(outputWidth * fitScale)) & ~1);
                                outputHeight = Math.Max(2, ((int)Math.Round(outputHeight * fitScale)) & ~1);
                            }
                        }

                        if (sws == null ||
                            swsWidth != width ||
                            swsHeight != height ||
                            swsOutputWidth != outputWidth ||
                            swsOutputHeight != outputHeight ||
                            swsSourceFormat != sourceFormat)
                        {
                            if (sws != null)
                            {
                                ffmpeg.sws_freeContext(sws);
                                sws = null;
                            }

                            if (convertedBuffer != IntPtr.Zero)
                            {
                                Marshal.FreeHGlobal(convertedBuffer);
                                convertedBuffer = IntPtr.Zero;
                            }

                            sws = ffmpeg.sws_getContext(
                                width, height, sourceFormat,
                                outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                                (int)SwsFlags.SWS_LANCZOS,
                                null, null, null);

                            if (sws == null)
                            {
                                if (tryHardware)
                                    throw new HardwareDecodeException($"sws_getContext failed after HW transfer. Pixel format={(int)sourceFormat}");
                                throw new ApplicationException("sws_getContext failed.");
                            }

                            convertedBufferSize = ffmpeg.av_image_get_buffer_size(
                                AVPixelFormat.AV_PIX_FMT_BGRA, outputWidth, outputHeight, 1);
                            if (convertedBufferSize <= 0)
                                throw new ApplicationException("Invalid converted frame buffer size.");

                            convertedBuffer = Marshal.AllocHGlobal(convertedBufferSize);
                            dstData = new byte_ptr4();
                            dstLinesize = new int4();

                            ThrowIfError(ffmpeg.av_image_fill_arrays(
                                ref dstData,
                                ref dstLinesize,
                                (byte*)convertedBuffer,
                                AVPixelFormat.AV_PIX_FMT_BGRA,
                                outputWidth,
                                outputHeight,
                                1));

                            swsWidth = width;
                            swsHeight = height;
                            swsOutputWidth = outputWidth;
                            swsOutputHeight = outputHeight;
                            swsSourceFormat = sourceFormat;
                            Emit($"AUTO FIT ✓ source={width}x{height} display={outputWidth}x{outputHeight} viewport={targetWidth}x{targetHeight} aspect={displayAspect:0.###}");
                        }

                        bool hasTimestamp = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE;
                        double rawPtsSeconds = hasTimestamp
                            ? frame->best_effort_timestamp * ffmpeg.av_q2d(videoStream->time_base) - _timelineOriginSeconds
                            : (double.IsFinite(lastPresentedPts) ? lastPresentedPts + nominalFrameDuration : _positionSeconds);
                        double ptsSeconds = rawPtsSeconds + timestampContinuityOffset;

                        // av_seek_frame(...BACKWARD) intentionally lands on an earlier keyframe.
                        // Those decode-preroll frames are required internally, but showing them
                        // causes the visible backward-jump/catch-up effect after a seek.
                        double seekFloor = _seekDiscardUntilSeconds;
                        if (seekFloor >= 0)
                        {
                            if (ptsSeconds + 0.002 < seekFloor)
                            {
                                ffmpeg.av_frame_unref(frame);
                                ffmpeg.av_frame_unref(swFrame);
                                continue;
                            }

                            // MPEG-2/PS/VOB/DAT decoders can emit one visually damaged frame
                            // immediately after keyframe preroll even though its PTS is already at
                            // the requested target. Keep the last good picture on screen until two
                            // consecutive target-side frames have decoded. This removes the brief
                            // broken-picture flash without changing normal playback.
                            seekStableFrames++;
                            if (seekStableFrames < 2)
                            {
                                ffmpeg.av_frame_unref(frame);
                                ffmpeg.av_frame_unref(swFrame);
                                continue;
                            }

                            _seekDiscardUntilSeconds = -1;
                            _positionSeconds = ptsSeconds;
                            CurrentAvDriftMs = 0;
                            haveClockBase = false;
                            clock.Restart();
                            Emit($"SEEK CLEAN-FRAME LOCK ✓ video={ptsSeconds:0.###}s target={seekFloor:0.###}s");
                            Interlocked.Exchange(ref _seekCompletion, null)?.TrySetResult(ptsSeconds);
                        }

                        // Outside seek preroll, repair only clearly broken timestamp jumps. Normal
                        // frame-rate jitter and legitimate small gaps pass through untouched. This
                        // prevents MPEG/VOB/DAT video from racing/dropping frames to catch a corrupt PTS.
                        if (double.IsFinite(lastPresentedPts))
                        {
                            double expected = lastPresentedPts + nominalFrameDuration;
                            double jump = ptsSeconds - expected;
                            if (jump < -0.350 || jump > 2.000)
                            {
                                timestampContinuityOffset += expected - ptsSeconds;
                                ptsSeconds = expected;
                                Emit($"VIDEO TIMESTAMP DISCONTINUITY repaired ✓ jump={jump:0.###}s");
                                haveClockBase = false;
                                clock.Restart();
                            }
                        }
                        if (!hasTimestamp && double.IsFinite(lastPresentedPts))
                            Emit("VIDEO PTS MISSING — monotonic fallback used");

                        if (!haveClockBase)
                        {
                            clockBasePts = ptsSeconds;
                            clock.Restart();
                            haveClockBase = true;
                        }

                        double expectedElapsed = ptsSeconds - clockBasePts;
                        double delay = expectedElapsed - clock.Elapsed.TotalSeconds;
                        bool dropForSync = false;

                        double? master = MasterClockSeconds?.Invoke();
                        if (master.HasValue && master.Value > 0.001)
                        {
                            double drift = ptsSeconds - master.Value;
                            if (drift > 0.015 && drift < 0.500)
                                Thread.Sleep((int)Math.Min(200, drift * 1000.0));
                            if (drift < -0.120)
                                dropForSync = true;
                            CurrentAvDriftMs = drift * 1000.0;
                        }
                        else
                        {
                            CurrentAvDriftMs = 0;
                            if (delay > 0 && delay < 1.0)
                                Thread.Sleep((int)(delay * 1000.0));
                            // Main Program additionally feeds the output raster, monitor,
                            // MasterAvBus and DeckLink. If that work overruns, converge to
                            // wall-clock time by dropping only already-late presentation frames.
                            // Never decode and display an old backlog as visible slow motion.
                            double lateLimit = Math.Max(0.050, nominalFrameDuration * 1.75);
                            if (firstFrame && delay < -lateLimit)
                                dropForSync = true;
                        }

                        lastPresentedPts = ptsSeconds;
                        if (dropForSync)
                        {
                            _positionSeconds = Math.Max(0, ptsSeconds);
                            // A dropped video frame still owns FFmpeg frame references.
                            // Release both before continuing or long playout can accumulate buffers.
                            ffmpeg.av_frame_unref(frame);
                            ffmpeg.av_frame_unref(swFrame);
                            continue;
                        }

                        // PERFORMANCE: once playback has produced its first frame, a minimized
                        // window does not need BGRA conversion, color pass, crop analysis or WPF bitmap
                        // creation. Decode/timestamps continue so restore resumes at the correct position.
                        if (!PresentationEnabled && firstFrame)
                        {
                            _positionSeconds = Math.Max(0, ptsSeconds);
                            ffmpeg.av_frame_unref(frame);
                            ffmpeg.av_frame_unref(swFrame);
                            continue;
                        }

                        ffmpeg.sws_scale(
                            sws,
                            displayFrame->data,
                            displayFrame->linesize,
                            0,
                            height,
                            dstData,
                            dstLinesize);

                        int stride = dstLinesize[0];
                        int copySize = stride * outputHeight;
                        if (managedPixelBuffer == null || managedPixelBuffer.Length != copySize)
                            managedPixelBuffer = new byte[copySize];

                        byte[] pixels = managedPixelBuffer;
                        Marshal.Copy(convertedBuffer, pixels, 0, copySize);

                        // OPERATOR MAIN-OUTPUT VIDEO PROCESSING.
                        // This runs after decode + swscale, so codec selection, timestamps, seek and A/V sync are untouched.
                        ApplyOutputVideoProcessing(pixels, outputWidth, outputHeight, stride);

                        BitmapSource bitmap = BitmapSource.Create(
                            outputWidth, outputHeight, 96, 96,
                            PixelFormats.Bgra32, null, pixels, stride);

                        // Active-picture fit for SD MPEG/VOB/DAT/M2P. Some broadcast files are
                        // physically 720x576 4:3 but carry a 16:9 picture letterboxed inside it.
                        // WPF Stretch=Uniform correctly preserves the OUTER 4:3 frame, which makes
                        // the useful picture look small. Detect only stable, symmetric black bars
                        // and crop them from presentation. Never stretch/distort the active picture.
                        bool holdInitialPresentation = false;
                        if (!DisableAutomaticLetterboxCrop && outputWidth <= 1100 && outputHeight <= 800)
                        {
                            var detected = DetectStableLetterbox(pixels, outputWidth, outputHeight, stride);
                            if (detected.top > 0 && detected.bottom > 0)
                            {
                                // The very first positive detection tells us this source needs active-picture
                                // fitting. From this point, keep the previous/black UI frame until crop lock,
                                // instead of visibly showing outer frame size and then jumping to fitted size.
                                if (!firstFrame && lockedCropTop == 0 && lockedCropBottom == 0)
                                    initialFitGateActive = true;

                                if (Math.Abs(detected.top - cropCandidateTop) <= 3 &&
                                    Math.Abs(detected.bottom - cropCandidateBottom) <= 3)
                                    cropCandidateHits++;
                                else
                                {
                                    cropCandidateTop = detected.top;
                                    cropCandidateBottom = detected.bottom;
                                    cropCandidateHits = 1;
                                }

                                // Five consecutive frames prevents fades/scene cuts from changing fit.
                                if (cropCandidateHits >= 5 && lockedCropTop == 0 && lockedCropBottom == 0)
                                {
                                    lockedCropTop = cropCandidateTop;
                                    lockedCropBottom = cropCandidateBottom;
                                    initialFitGateActive = false;
                                    Emit($"SD ACTIVE PICTURE ✓ letterbox crop top={lockedCropTop}px bottom={lockedCropBottom}px");
                                }
                            }

                            if (initialFitGateActive && lockedCropTop == 0 && lockedCropBottom == 0)
                            {
                                initialFitProbeFrames++;
                                if (initialFitProbeFrames <= initialFitProbeLimit)
                                    holdInitialPresentation = true;
                                else
                                    initialFitGateActive = false; // fail-safe: never hide valid video indefinitely
                            }

                            if (lockedCropTop > 0 && lockedCropBottom > 0)
                            {
                                int activeHeight = outputHeight - lockedCropTop - lockedCropBottom;
                                if (activeHeight > outputHeight / 2)
                                    bitmap = new CroppedBitmap(bitmap, new Int32Rect(0, lockedCropTop, outputWidth, activeHeight));
                            }
                        }

                        bitmap.Freeze();
                        if (!holdInitialPresentation)
                            FrameReady?.Invoke(bitmap);

                        _positionSeconds = Math.Max(0, ptsSeconds);

                        if (!firstFrame)
                        {
                            firstFrame = true;
                            Emit("FIRST DECODED FRAME ✓");
                            Emit($"VIDEO PLAYBACK ACTIVE ✓ [{ActiveDecodeBackend}]");
                            _firstFrameCompletion?.TrySetResult(true);
                        }

                        ffmpeg.av_frame_unref(frame);
                        ffmpeg.av_frame_unref(swFrame);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            if (!token.IsCancellationRequested)
            {
                Emit("VIDEO DECODE END ✓");
                Ended?.Invoke();
            }
            else
            {
                Emit("VIDEO DECODE STOPPED ✓ (manual transport reset)");
            }
        }
        catch (HardwareDecodeException)
        {
            throw;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Emit("DECODE ERROR ✗");
            Emit(ex.Message);
        }
        finally
        {
            if (convertedBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(convertedBuffer);
            if (sws != null)
                ffmpeg.sws_freeContext(sws);
            if (packet != null)
                ffmpeg.av_packet_free(&packet);
            if (frame != null)
                ffmpeg.av_frame_free(&frame);
            if (swFrame != null)
                ffmpeg.av_frame_free(&swFrame);
            if (codecContext != null)
                ffmpeg.avcodec_free_context(&codecContext);
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private bool TryAttachHardwareDevice(AVCodecContext* codecContext, AVCodec* decoder, out string backend, out AVPixelFormat hwPixelFormat)
    {
        backend = "";
        hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;

        // Preference order:
        // NVIDIA: CUDA/NVDEC
        // Intel: QSV
        // Windows generic: D3D11VA (including AMD hardware decode)
        // Legacy Windows: DXVA2
        AVHWDeviceType[] preference =
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_QSV,
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2
        };

        foreach (AVHWDeviceType type in preference)
        {
            if (!TryGetDecoderHardwarePixelFormat(decoder, type, out AVPixelFormat candidatePixelFormat))
                continue;

            AVBufferRef* device = null;
            int result = ffmpeg.av_hwdevice_ctx_create(&device, type, null, null, 0);
            if (result < 0 || device == null)
            {
                Emit($"HW DEVICE unavailable: {DeviceLabel(type)} ({ErrorText(result)})");
                continue;
            }

            codecContext->hw_device_ctx = device;
            backend = DeviceLabel(type);
            hwPixelFormat = candidatePixelFormat;
            Emit($"HW DEVICE SELECTED ✓ {backend} / pix_fmt={(int)hwPixelFormat}");
            return true;
        }

        return false;
    }

    private static bool TryGetDecoderHardwarePixelFormat(AVCodec* decoder, AVHWDeviceType type, out AVPixelFormat pixelFormat)
    {
        pixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(decoder, i);
            if (cfg == null) return false;

            if ((cfg->methods & 0x01) != 0 && cfg->device_type == type)
            {
                pixelFormat = cfg->pix_fmt;
                return true;
            }
        }
    }

    private static string DeviceLabel(AVHWDeviceType type) =>
        type switch
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => "NVIDIA CUDA/NVDEC",
            AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => "Intel Quick Sync (QSV)",
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "D3D11VA (AMD/Intel/NVIDIA)",
            AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "DXVA2",
            _ => type.ToString()
        };

    private static int FindBestVideoStream(AVFormatContext* format, out AVCodec* decoder)
    {
        AVCodec* found = null;
        int index = ffmpeg.av_find_best_stream(
            format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &found, 0);

        ThrowIfError(index);
        if (found == null)
            throw new ApplicationException("No FFmpeg video decoder was found.");

        decoder = found;
        return index;
    }

    private static string Ptr(byte* value) =>
        value == null ? "" : Marshal.PtrToStringAnsi((IntPtr)value) ?? "";

    private static string ErrorText(int error)
    {
        if (error >= 0) return "OK";
        byte* buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        return Marshal.PtrToStringAnsi((IntPtr)buffer) ?? $"FFmpeg error {error}";
    }

    private static void ThrowIfError(int error)
    {
        if (error < 0)
            throw new ApplicationException(ErrorText(error));
    }

    private void ApplyOutputVideoProcessing(byte[] pixels, int width, int height, int stride)
    {
        if (pixels == null || pixels.Length < 16 || width < 2 || height < 2) return;

        // Sparse frame analysis. This is deliberately lightweight and does not touch decode timing.
        long sumY = 0, sumY2 = 0, sumR = 0, sumG = 0, sumB = 0; int count = 0;
        bool needAnalysis = OutputAutoColor || OutputAutoQualityEnhance || OutputAutoWhiteBalance;
        if (needAnalysis)
        {
            int sampleStep = Math.Max(4, (pixels.Length / 4 / 1800) * 4);
            for (int i = 0; i + 2 < pixels.Length; i += sampleStep)
            {
                int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                int y = (77 * r + 150 * g + 29 * b) >> 8;
                sumY += y; sumY2 += (long)y * y; sumR += r; sumG += g; sumB += b; count++;
            }
        }
        double avgY = count > 0 ? (double)sumY / count : 128.0;
        double stdY = count > 0 ? Math.Sqrt(Math.Max(0.0, (double)sumY2 / count - avgY * avgY)) : 55.0;

        double autoGain = 1.0;
        if (OutputAutoColor && count > 0)
        {
            double wanted = avgY < 50 ? Math.Min(1.18, 60.0 / Math.Max(1.0, avgY)) : avgY > 205 ? Math.Max(0.90, 195.0 / avgY) : 1.0;
            _autoColorGain += (wanted - _autoColorGain) * 0.025;
            autoGain = _autoColorGain;
        }
        else _autoColorGain += (1.0 - _autoColorGain) * 0.10;

        // Smart Quality is resolution-aware but conservative. It improves presentation; it does not invent source detail.
        double adaptiveStrength = 0.0, adaptiveContrast = 1.0, adaptiveSat = 1.0, adaptiveSharp = 0.0, adaptiveBrightness = 0.0;
        if (OutputAutoQualityEnhance)
        {
            double user = Math.Clamp(OutputAutoQualityStrength, 0.0, 1.0);
            int sh = _currentSourceHeight > 0 ? _currentSourceHeight : height;
            double resolutionNeed = sh <= 576 ? 1.0 : sh <= 720 ? 0.72 : sh < 1080 ? 0.42 : 0.18;
            adaptiveStrength = user * resolutionNeed;
            if (stdY < 48) adaptiveContrast = 1.0 + Math.Min(0.16, (48.0 - stdY) / 48.0 * 0.16) * adaptiveStrength;
            if (avgY < 72) adaptiveBrightness = Math.Min(10.0, (72.0 - avgY) * 0.14) * adaptiveStrength;
            adaptiveSat = 1.0 + 0.08 * adaptiveStrength;
            adaptiveSharp = 0.55 * adaptiveStrength;
        }

        // Gray-world Auto White Balance with limited/smoothed gains to avoid pumping and color casts.
        if (OutputAutoWhiteBalance && count > 0)
        {
            double ar=(double)sumR/count, ag=(double)sumG/count, ab=(double)sumB/count, gray=(ar+ag+ab)/3.0;
            double wr=Math.Clamp(gray/Math.Max(1.0,ar),0.90,1.10), wg=Math.Clamp(gray/Math.Max(1.0,ag),0.90,1.10), wb=Math.Clamp(gray/Math.Max(1.0,ab),0.90,1.10);
            _autoWbR += (wr-_autoWbR)*0.025; _autoWbG += (wg-_autoWbG)*0.025; _autoWbB += (wb-_autoWbB)*0.025;
        }
        else { _autoWbR+=(1-_autoWbR)*0.08; _autoWbG+=(1-_autoWbG)*0.08; _autoWbB+=(1-_autoWbB)*0.08; }

        double temp=Math.Clamp(OutputWhiteTemperature,-100,100)/100.0;
        double tint=Math.Clamp(OutputWhiteTint,-100,100)/100.0;
        double manualR=1.0+0.12*temp+0.035*tint, manualB=1.0-0.12*temp+0.035*tint, manualG=1.0-0.07*tint;

        double br = Math.Clamp(OutputBrightness, -1.0, 1.0) * 255.0 + adaptiveBrightness;
        double ct = Math.Clamp(OutputContrast, 0.50, 2.00) * adaptiveContrast;
        double sat = Math.Clamp(OutputSaturation, 0.0, 2.0) * adaptiveSat;
        double gam = Math.Clamp(OutputGamma, 0.50, 2.0);
        double black = Math.Clamp(OutputBlackLevel, -32.0, 32.0);
        double yg = Math.Clamp(OutputYLevel, 0.50, 1.50);
        double ug = Math.Clamp(OutputULevel, 0.50, 1.50);
        double vg = Math.Clamp(OutputVLevel, 0.50, 1.50);
        bool wbNeutral = Math.Abs(_autoWbR-1)<0.002 && Math.Abs(_autoWbG-1)<0.002 && Math.Abs(_autoWbB-1)<0.002 && Math.Abs(temp)<0.001 && Math.Abs(tint)<0.001;
        bool neutral = Math.Abs(br) < 0.01 && Math.Abs(ct-1)<0.001 && Math.Abs(sat-1)<0.001 && Math.Abs(gam-1)<0.001 && Math.Abs(black)<0.01 && Math.Abs(yg-1)<0.001 && Math.Abs(ug-1)<0.001 && Math.Abs(vg-1)<0.001 && Math.Abs(autoGain-1)<0.002 && wbNeutral;
        if (!neutral)
        {
            double invGamma = 1.0 / gam;
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int i = row + x * 4; if (i + 2 >= pixels.Length) break;
                    double b = pixels[i]*_autoWbB*manualB, g = pixels[i+1]*_autoWbG*manualG, r = pixels[i+2]*_autoWbR*manualR;
                    double Y = 0.299*r + 0.587*g + 0.114*b;
                    double U = (b - Y) * 0.565 * ug;
                    double V = (r - Y) * 0.713 * vg;
                    Y = ((Y - 128.0) * ct + 128.0 + br + black) * yg * autoGain;
                    U *= sat; V *= sat;
                    r = Y + 1.403*V; b = Y + 1.770*U; g = Y - 0.344*U - 0.714*V;
                    r = 255.0 * Math.Pow(Math.Clamp(r,0,255)/255.0, invGamma);
                    g = 255.0 * Math.Pow(Math.Clamp(g,0,255)/255.0, invGamma);
                    b = 255.0 * Math.Pow(Math.Clamp(b,0,255)/255.0, invGamma);
                    pixels[i]=(byte)Math.Clamp((int)Math.Round(b),0,255); pixels[i+1]=(byte)Math.Clamp((int)Math.Round(g),0,255); pixels[i+2]=(byte)Math.Clamp((int)Math.Round(r),0,255);
                }
            }
        }
        if (OutputScanMode == 3 && height > 2)
        {
            byte[] src = (byte[])pixels.Clone();
            for (int y = 1; y < height - 1; y += 2)
            {
                int row=y*stride, up=(y-1)*stride, dn=(y+1)*stride;
                for (int x=0; x<width*4 && row+x<pixels.Length; x++) pixels[row+x]=(byte)(((int)src[up+x]+src[dn+x])>>1);
            }
        }
        // RESTORATION / NOISE REDUCTION restored from the earlier MASTER processing concept.
        // It is deliberately conservative and applied before sharpening so low-quality SD/legacy
        // material is not sharpened together with compression noise.
        double denoise = Math.Clamp(OutputNoiseReduction, 0.0, 1.0);
        if (OutputAutoQualityEnhance)
            denoise = Math.Max(denoise, 0.22 * adaptiveStrength);
        if (denoise > 0.01 && width > 4 && height > 4)
        {
            byte[] src = (byte[])pixels.Clone();
            double mix = Math.Min(0.42, denoise * 0.42);
            for (int y = 1; y < height - 1; y++)
            {
                int row = y * stride;
                for (int x = 1; x < width - 1; x++)
                {
                    int i = row + x * 4;
                    for (int c = 0; c < 3; c++)
                    {
                        int avg = (src[i+c] + src[i-4+c] + src[i+4+c] + src[i-stride+c] + src[i+stride+c]) / 5;
                        pixels[i+c] = (byte)Math.Clamp((int)Math.Round(src[i+c] * (1.0-mix) + avg * mix), 0, 255);
                    }
                }
            }
        }

        double sharp = Math.Clamp(OutputSharpness + adaptiveSharp, 0.0, 2.0);
        if (sharp > 0.01 && width > 4 && height > 4)
        {
            byte[] src=(byte[])pixels.Clone(); double amount=0.30*sharp;
            for(int y=1;y<height-1;y++)
            {
                int row=y*stride;
                for(int x=1;x<width-1;x++)
                {
                    int i=row+x*4;
                    for(int c=0;c<3;c++)
                    {
                        int avg=(src[i-4+c]+src[i+4+c]+src[i-stride+c]+src[i+stride+c])/4;
                        pixels[i+c]=(byte)Math.Clamp((int)Math.Round(src[i+c]+(src[i+c]-avg)*amount),0,255);
                    }
                }
            }
        }
    }

    public void Dispose() => Stop();

    private sealed class HardwareDecodeException : Exception
    {
        public HardwareDecodeException(string message) : base(message) { }
    }
}

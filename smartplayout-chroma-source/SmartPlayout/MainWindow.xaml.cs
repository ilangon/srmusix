using Microsoft.Win32;
using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SmartPlayout.Contracts;

namespace FFmpegNativePlayer;

public partial class MainWindow : Window
{
    private sealed class OperatorSettings
    {
        public int VideoScaleMode { get; set; } = 0;
        public bool HardwareDecodeEnabled { get; set; } = true;
        public bool HardwareEncodePreferred { get; set; } = true;
        public double Brightness { get; set; } = 0;
        public double Contrast { get; set; } = 1;
        public double Saturation { get; set; } = 1;
        public double Gamma { get; set; } = 1;
        public double BlackLevel { get; set; } = 0;
        public double YLevel { get; set; } = 1;
        public double ULevel { get; set; } = 1;
        public double VLevel { get; set; } = 1;
        public double Sharpness { get; set; } = 0;
        public double NoiseReduction { get; set; } = 0;
        public bool AutoColor { get; set; } = false;
        public bool AutoQualityEnhance { get; set; } = false;
        public double AutoQualityStrength { get; set; } = 0.55;
        public bool AutoWhiteBalance { get; set; } = false;
        public double WhiteTemperature { get; set; } = 0;
        public double WhiteTint { get; set; } = 0;
        public int ScanMode { get; set; } = 0;
        public double MasterGainDb { get; set; } = 0;
        public double BassDb { get; set; } = 0;
        public double TrebleDb { get; set; } = 0;
        public double Balance { get; set; } = 0;
        public bool AudioNormalize { get; set; } = true;
        public bool AudioLoudnessControl { get; set; } = false;
        public double[] EqualizerDb { get; set; } = new double[10];
        public int OutputResolution { get; set; } = 4;
        public int OutputSampleRate { get; set; } = 2;
    }

    private sealed class Row
    {
        public int Index { get; set; }
        public string FilePath { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string Name => !string.IsNullOrWhiteSpace(ProgramName) ? ProgramName : Path.GetFileName(FilePath);
        public string Type => Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
        public double DurationSeconds { get; set; }
        public double SourceDurationSeconds { get; set; }
        public double InPointSeconds { get; set; }
        public double OutPointSeconds { get; set; }
        public string Description { get; set; } = "";
        public string StartTimeText { get; set; } = "—";
        public double PlayDurationSeconds => Math.Max(0, (OutPointSeconds > InPointSeconds ? OutPointSeconds - InPointSeconds : DurationSeconds));
        public string DurationText => DurationSeconds > 0 ? Clock(DurationSeconds) : "—";
        public string PlayDurationText => PlayDurationSeconds > 0 ? Clock(PlayDurationSeconds) : "—";
        public string InText => EditorClock(InPointSeconds);
        public string OutText => EditorClock(OutPointSeconds > 0 ? OutPointSeconds : Math.Max(SourceDurationSeconds, DurationSeconds));
        public ObservableCollection<string> AvailableAudioLanguages { get; } = new() { AudioLanguageHelper.DefaultLanguage };
        public string AudioLanguage { get; set; } = AudioLanguageHelper.DefaultLanguage;
    }

    private sealed class ScheduleRow
    {
        public DateTime When { get; set; }
        public DateTime? EndWhen { get; set; }
        public string EndMode { get; set; } = "MANUAL END";
        public string StartMode { get; set; } = "EXACT";
        public bool ExactEnd { get; set; } = true;
        public bool AutoChain { get; set; } = true;
        public bool Pending { get; set; }
        public string FilePath { get; set; } = "";
        public string Kind { get; set; } = "MEDIA";
        public List<string> Files { get; set; } = new();
        public List<double> Durations { get; set; } = new();
        public List<double> InPoints { get; set; } = new();
        public List<double> OutPoints { get; set; } = new();
        public List<string> Descriptions { get; set; } = new();
        public string ProgramName { get; set; } = "";
        public string AudioLanguage { get; set; } = AudioLanguageHelper.DefaultLanguage;
        public List<string> AudioLanguages { get; set; } = new();
        public string Name => !string.IsNullOrWhiteSpace(ProgramName)
            ? ProgramName
            : Path.GetFileName(FilePath);
        public string TimeText => When.ToString("dd/MM hh:mm:ss tt");
        public string EndTimeText => EndWhen.HasValue ? EndWhen.Value.ToString("dd/MM hh:mm:ss tt") : "CONTINUE";
        public string StartModeText => StartMode == "TIME" ? "TIME" : "EXACT";
        public bool Fired { get; set; }
    }

    private sealed class OnAirItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        public int Index { get; set; }
        public string FilePath { get; set; } = "";
        public string Name => Path.GetFileName(FilePath);
        public string Description { get; set; } = "";
        public double In { get; set; }
        public double Out { get; set; }
        public double Duration { get; set; }
        public string Audio { get; set; } = AudioLanguageHelper.DefaultLanguage;
        public string InText => TimeSpan.FromSeconds(Math.Max(0, In)).ToString(@"hh\:mm\:ss\.fff");
        public string OutText => TimeSpan.FromSeconds(Math.Max(0, Out)).ToString(@"hh\:mm\:ss\.fff");
        public string DurationText => TimeSpan.FromSeconds(Math.Max(0, Duration)).ToString(Duration >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
        private string _format = "—", _resolution = "—", _videoCodec = "—", _audioInfo = "—", _fps = "—", _mediaDuration = "—";
        public string Format { get => _format; set { if (_format == value) return; _format = value; Changed(nameof(Format)); } }
        public string Resolution { get => _resolution; set { if (_resolution == value) return; _resolution = value; Changed(nameof(Resolution)); } }
        public string VideoCodec { get => _videoCodec; set { if (_videoCodec == value) return; _videoCodec = value; Changed(nameof(VideoCodec)); } }
        public string AudioInfo { get => _audioInfo; set { if (_audioInfo == value) return; _audioInfo = value; Changed(nameof(AudioInfo)); } }
        public string Fps { get => _fps; set { if (_fps == value) return; _fps = value; Changed(nameof(Fps)); } }
        public string MediaDuration { get => _mediaDuration; set { if (_mediaDuration == value) return; _mediaDuration = value; Changed(nameof(MediaDuration)); } }
    }

    private sealed class SavedProgramModel
    {
        public string Name { get; set; } = "";
        public List<string> Files { get; set; } = new();
        public List<double> Durations { get; set; } = new();
        public List<double> SourceDurations { get; set; } = new();
        public List<double> InPoints { get; set; } = new();
        public List<double> OutPoints { get; set; } = new();
        public List<string> Descriptions { get; set; } = new();
        public List<string> AudioLanguages { get; set; } = new();
    }

    private sealed class SavedPlaylistItemModel
    {
        public string FilePath { get; set; } = "";
        public string AudioLanguage { get; set; } = AudioLanguageHelper.DefaultLanguage;
        public double SourceDurationSeconds { get; set; }
        public double InPointSeconds { get; set; }
        public double OutPointSeconds { get; set; }
        public string Description { get; set; } = "";
    }
    private sealed class SavedPlaylistModel
    {
        public int Version { get; set; } = 3;
        public List<SavedPlaylistItemModel> Items { get; set; } = new();
    }

    private sealed class ScheduleFileModel
    {
        public DateTime When { get; set; }
        public DateTime? EndWhen { get; set; }
        public string EndMode { get; set; } = "MANUAL END";
        public string StartMode { get; set; } = "EXACT";
        public bool ExactEnd { get; set; } = true;
        public bool AutoChain { get; set; } = true;
        public string FilePath { get; set; } = "";
        public string ProgramName { get; set; } = "";
        public string AudioLanguage { get; set; } = AudioLanguageHelper.DefaultLanguage;
        public List<string> AudioLanguages { get; set; } = new();
        public string Kind { get; set; } = "MEDIA";
        public List<string> Files { get; set; } = new();
        public List<double> Durations { get; set; } = new();
        public List<double> InPoints { get; set; } = new();
        public List<double> OutPoints { get; set; } = new();
        public List<string> Descriptions { get; set; } = new();
    }

    private readonly ObservableCollection<Row> _items = new();
    private readonly ObservableCollection<ScheduleRow> _schedule = new();
    private readonly ObservableCollection<OnAirItem> _onAirItems = new();
    private readonly SemaphoreSlim _mediaProbeGate = new(2,2);
    private string _dashboardListKey = "";
    private string? _dashboardMediaFile;
    private readonly NativeVideoPlayer _preview = new();
    private readonly NativeAudioPlayer _previewAudio = new();
    private NativeVideoPlayer _program = new();
    private NativeAudioPlayer _programAudio = new();
    // v0.6.8.50.35 A/B PRELOAD SEAMLESS SWITCH CORRECTION:
    // Slot A is the current _program/_programAudio pair.  Slot B is opened and
    // decoder-ready while A remains on air, then consumed atomically by Take.
    private readonly object _programPreloadLock = new();
    private NativeVideoPlayer? _preloadedProgramVideo;
    private NativeAudioPlayer? _preloadedProgramAudio;
    private string? _preloadedProgramFile;
    private string _preloadedProgramLanguage = AudioLanguageHelper.DefaultLanguage;
    private double _preloadedProgramStart;
    private double? _preloadedProgramEnd;
    private int _programPreloadGeneration;
    private const float PreviewFixedVolume = 0.70f;
    private bool _previewMuted = true; // Local Instant Preview starts muted; operator may unmute. Isolated from ON-AIR master audio.
    private bool _masterMuted;
    private float _masterVolume = 1.0f;
    private bool _previewPauseState;
    private readonly DispatcherTimer _timer;
    private string? _previewFile;
    private string? _programFile;
    private string? _nextFile;
    private string _nextAudioLanguage = AudioLanguageHelper.DefaultLanguage;
    private string _programAudioLanguage = AudioLanguageHelper.DefaultLanguage;
    private double _nextStartSeconds;
    private double? _nextEndSeconds;
    private double _programClipStartSeconds;
    private double? _programClipEndSeconds;
    private bool _schedulerBusy;
    private readonly Queue<string> _scheduledRunQueue = new();
    private bool _scheduledPlaylistActive;
    private ScheduleRow? _activeScheduledJob;
    private DateTime? _activeScheduleEnd;
    private bool _loadingPersistentState;
    private Row? _instantPreviewRow;
    private double _instantPreviewFps = 25.0;
    private bool _breakActive;
    private string? _breakResumeFile;
    private double _breakResumePosition;
    private string _breakResumeAudio = AudioLanguageHelper.DefaultLanguage;
    private double? _breakResumeEnd;
    private bool _instantSeekDragging;
    private bool _instantSeekSyncInProgress;
    private double _instantWorkingIn;
    private double _instantWorkingOut;
    private bool _loadingInstantEditor;
    private readonly List<string> _fillerFiles = new();
    private int _fillerIndex;
    private bool _fillerActive;
    private bool _fillerStarting;
    private string? _fillerResumeFile;
    private double _fillerResumePosition;
    private bool _fillerResumePending;
    // SmartPlayer v1.7.2.x presentation model: keep only the newest completed frame
    // and never queue a backlog of stale WPF image updates.
    private BitmapSource? _latestPreviewFrame;
    private BitmapSource? _latestProgramFrame;
    private BitmapSource? _pendingProgramSourceFrame;
    private BitmapSource? _latestLiveSourceFrame;
    private int _previewFrameDispatchPending;
    private int _programFrameDispatchPending;
    private int _programComposePumpActive;
    private long _lastProgramPreviewDispatchTick;
    private int _liveFrameProcessing;
    private int _liveFrameDispatchPending;
    private int _previewRenderWidth, _previewRenderHeight;
    private int _programRenderWidth, _programRenderHeight;
    private bool _onAirSeekDragging, _onAirSeekInProgress;
    private bool _onAirEnabled = true, _operatorStopped;
    private int _videoScaleMode; // 0 Smart, 1 16:9, 2 4:3, 3 Full Stretch
    private bool _hardwareDecodeEnabled = true;
    private bool _hardwareEncodePreferred = true;
    private bool _loadingOperatorSettings;
    private int _appliedOutputResolutionIndex = 4; // eMVF_PAL = 720x576 SD
    private int _appliedOutputSampleRateIndex = 2; // 48000 Hz broadcast default
    private int _programTakeInProgress;
    // Decoder callbacks run on worker threads. Cache the applied profile as plain CLR
    // data on the UI thread; never read ComboBox/DependencyObject state while publishing.
    private string _appliedOutputResolutionName = "eMVF_PAL";
    private bool _screenOutputPending;
    private readonly StreamingEngine _streaming = new();
    private readonly MasterAvBus _masterAvBus = new();
    private readonly ModuleProcessSupervisor _moduleSupervisor = new();
    private SharedVideoFrameBuffer? _sharedProgramFrames;
    private SharedAudioRingBuffer? _sharedProgramAudio;
    private byte[]? _sharedProgramPixels;
    private long _sharedProgramFrameNumber;
    private readonly LiveCaptureEngine _liveCapture = new();
    internal event Action<float,float>? StudioAudioLevelChanged;
    private bool _liveInputActive;
    private readonly FinalProgramCompositor _finalProgramCompositor = new();
    private readonly NativeVideoPlayer _studioPipPlayer = new();
    private readonly NativeVideoPlayer _studioKeyBackgroundPlayer = new();
    private string? _studioKeyBackgroundFile;
    private bool _studioKeyBackgroundLoop;
    private bool _studioKeyBackgroundRestarting;
    internal event Action<BitmapSource>? StudioKeyBackgroundFrameChanged;
    private BitmapSource? _studioPipFrame;
    private string? _studioPipFile;
    private bool _studioPipLoop=true;
    private bool _studioPipRestarting;
    private StreamingSettingsFile _streamSettings = new();
    private StreamCapabilities _streamCapabilities = new();
    private GraphicsCardOutputWindow? _graphicsOutputWindow;
    private readonly DeckLinkOutputEngine _deckLinkOutput = new();
    private readonly DeckLinkNativeOutputEngine _deckLinkNativeOutput = new();
    private MasterAvBus.Session? _deckLinkBusSession;
    private string? _deckLinkFfmpegPath;
    private readonly List<string> _deckLinkDetectedDevices = new();
    private readonly List<string> _deckLinkNativeDevices = new();
    private readonly List<DeckLinkOutputConnection> _deckLinkConnections = new();
    private readonly List<DeckLinkOutputMode> _deckLinkModes = new();
    private readonly Dictionary<int,List<DeckLinkOutputConnection>> _deckLinkConnectionCache = new();
    private readonly Dictionary<string,List<DeckLinkOutputMode>> _deckLinkModeCache = new(StringComparer.OrdinalIgnoreCase);
    private int _outputCapabilityRefreshActive;
    private bool _outputCapabilitiesLoaded;
    private bool _deckLinkUiUpdating;
    private bool _deckLinkNativeAvailable;
    private string _deckLinkNativeProbeMessage = "Native DeckLink SDK not probed.";
    private sealed class DeckLinkPersistState
    {
        public bool Armed { get; set; }
        public string Device { get; set; } = "";
        public uint ConnectionValue { get; set; }
        public bool AutoMode { get; set; } = true;
        public uint ModeId { get; set; }
    }
    private DeckLinkPersistState _deckLinkPersist = new();
    private bool _deckLinkPersistRestorePending;
    private int _deckLinkAutoStartInProgress;
    private DateTime _deckLinkAutoStartRetryAfterUtc=DateTime.MinValue;
    private static readonly string StreamSettingsPath = Path.Combine(DataStorage.Settings, "StreamingProfiles.json");
    private static readonly string DeckLinkSettingsPath = Path.Combine(DataStorage.Settings, "DeckLinkOutput.json");
    private bool _loadingStreamSettings;
    private static readonly string OperatorSettingsPath = Path.Combine(DataStorage.Settings, "OperatorSettings.json");
    private static readonly string CgDesignerProfilePath = Path.Combine(DataStorage.Settings, "CgDesigner.json");
    private static readonly string LegacyCgDesignerProfilePath = Path.Combine(DataStorage.Root, "Settings", "CgDesigner.json");
    private sealed class StudioWatermarkPersistState
    {
        public string WatermarkText { get; set; } = "SR MUSIC";
        public string WatermarkFont { get; set; } = "Segoe UI Semibold";
        public bool WatermarkBold { get; set; } = true;
        public bool WatermarkItalic { get; set; }
        public string WatermarkColor { get; set; } = "#FFFFFF";
        public double WatermarkOpacity { get; set; } = .45;
        public double WatermarkSize { get; set; } = 42;
        public double WatermarkX { get; set; } = .82;
        public double WatermarkY { get; set; } = .08;
        public double WatermarkScale { get; set; } = 1;
        public double WatermarkRotation { get; set; }
        public double WatermarkOutline { get; set; } = 1;
        public bool WatermarkShadow { get; set; } = true;
        public int WatermarkZ { get; set; } = 180;
        public bool WatermarkAlwaysOn { get; set; }
    }
    private string? _nowThumbnailFile, _nextThumbnailFile;
    private double _studioPipX=.70,_studioPipY=.08,_studioPipW=.26,_studioPipH=.26,_studioPipOpacity=1;
    private float _onAirLevelL, _onAirLevelR;
    private readonly Process _uiProcess = Process.GetCurrentProcess();
    private TimeSpan _lastCpuProcessTime = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastCpuWallTime = DateTime.UtcNow;
    private long _onAirDecodedFrameCount;
    private long _onAirPublishedFrameCount;
    private bool _programPaused;
    private double _programAudioMediaSeconds;
    private readonly object _onAirDiagLock = new();
    private string OnAirDiagnosticLogPath => Path.Combine(DataStorage.DataRoot, "Logs", "OnAir", "ON_AIR_FRAME_FLOW.log");

    private void OnAirDiag(string message)
    {
        try
        {
            string path = OnAirDiagnosticLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}";
            lock (_onAirDiagLock) File.AppendAllText(path, line);
            Debug.WriteLine("ON-AIR | " + message);
        }
        catch { }
    }

    private sealed class FillerPersistState { public List<string> Files { get; set; } = new(); public int NextIndex { get; set; } public string? ResumeFile { get; set; } public double ResumePosition { get; set; } public bool ResumePending { get; set; } }

    private static readonly string[] MediaExt =
    {
        ".mp4",".mkv",".mov",".avi",".mxf",".mpg",".mpeg",".ts",".m2ts",".mts",".vob",".dat",
        ".webm",".wmv",".flv",".m4v",".3gp",".ogv",".m2p",".m2v",".mpv",".asf",".f4v",".ogg",
        ".rm",".rmvb",".nut",".y4m",".dv",".divx",".xvid",".264",".h264",".265",".h265",".hevc",".3g2",
        ".mp3",".wav",".aac",".m4a",".flac",".wma",".opus"
    };

    private void AttachProgramPipeline(NativeVideoPlayer video, NativeAudioPlayer audio)
    {
        // Program decoders render toward the one authoritative applied output canvas.
        // Preview remains independent; this flag only disables the old SD auto-crop pass.
        video.DisableAutomaticLetterboxCrop = true;
        // v0.6.8.50.33 MAIN PROGRAM VIDEO CLOCK FREEZE FIX:
        // Keep the Main ON-AIR NativeVideoPlayer presentation clock identical to Instant/Program preview.
        // The previous decoded-audio PTS master could remain ahead of video and trigger NativeVideoPlayer's
        // drift rule (drift < -120 ms), continuously dropping every subsequent video frame after the first.
        // That exactly produces a frozen first frame while audio continues. Broadcast A/V cadence/sync remains
        // owned downstream by MasterAvBus/output timing; decoder presentation must not discard Program frames.
        video.MasterClockSeconds = null;
        video.Log += message =>
        {
            if (ReferenceEquals(video, _program) || message.Contains("DECODER", StringComparison.OrdinalIgnoreCase) || message.Contains("FIRST", StringComparison.OrdinalIgnoreCase))
                OnAirDiag($"VIDEO[{video.ActiveDecodeBackend}] {message}");
        };

        video.FrameReady += f =>
        {
            long decoded = Interlocked.Increment(ref _onAirDecodedFrameCount);
            if (decoded <= 3 || decoded % 300 == 0)
                OnAirDiag($"FRAME_READY #{decoded} {f.PixelWidth}x{f.PixelHeight} selected={(ReferenceEquals(video, _program) ? "YES" : "STAGING")}");
            if (!ReferenceEquals(video, _program)) return;
            PublishProgramFrame(f);
        };
        video.Ended += () =>
        {
            if (!ReferenceEquals(video, _program)) return;
            Dispatcher.BeginInvoke(async () => await HandleProgramEndedAsync());
        };
        audio.Diagnostic += message =>
        {
            if (ReferenceEquals(audio, _programAudio) ||
                message.Contains("AUDIO", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("DECODER", StringComparison.OrdinalIgnoreCase))
                OnAirDiag($"AUDIO {message}");
        };
        audio.MediaTimestampReady += seconds =>
        {
            if (!ReferenceEquals(audio, _programAudio)) return;
            Volatile.Write(ref _programAudioMediaSeconds, Math.Max(0, seconds));
        };
        audio.LevelChanged += (l, r) =>
        {
            if (!ReferenceEquals(audio, _programAudio)) return;
            Dispatcher.BeginInvoke(new Action(() => { UpdateOnAirAudioMeters(l, r); StudioAudioLevelChanged?.Invoke(l,r); }));
        };
        audio.ProcessedPcmReady += (pcm,count) =>
        {
            if (!ReferenceEquals(audio, _programAudio)) return;
            _masterAvBus.PushAudio(pcm,count,audio.OutputSampleRate,2);
            _deckLinkBusSession?.PushAudio(pcm,count,audio.OutputSampleRate,2);
            _deckLinkNativeOutput.PushAudio(pcm,count,audio.OutputSampleRate,2);
            PublishSharedProgramAudio(pcm,count,audio.OutputSampleRate,2);
        };
    }

    private void PublishProgramFrame(BitmapSource sourceFrame)
    {
        long published = Interlocked.Increment(ref _onAirPublishedFrameCount);
        if (published <= 3 || published % 300 == 0)
            OnAirDiag($"PUBLISH_ENTER #{published} source={sourceFrame.PixelWidth}x{sourceFrame.PixelHeight}");
        // The decoder callback must never perform full-HD scaling, CG/chroma composition,
        // WPF preview work and output copies synchronously. Keep only the newest immutable
        // source frame; a dedicated worker composes it. If composition is slower than a
        // 50/60-fps source, stale frames are coalesced instead of becoming visible slow motion.
        Interlocked.Exchange(ref _pendingProgramSourceFrame,sourceFrame);
        if(Interlocked.Exchange(ref _programComposePumpActive,1)!=0)return;
        _=Task.Run(PumpLatestProgramFrame);
    }

    private void PumpLatestProgramFrame()
    {
        try
        {
            while(true)
            {
                var sourceFrame=Interlocked.Exchange(ref _pendingProgramSourceFrame,null);
                if(sourceFrame==null)break;
                try
                {
                    var raster=RasterizeFinalProgramSource(sourceFrame);
                    var finalFrame=_finalProgramCompositor.Compose(raster);
                    _latestProgramFrame=finalFrame;
                    if(_deckLinkPersist.Armed&&_outputCapabilitiesLoaded&&!_deckLinkOutput.Running&&!_deckLinkNativeOutput.Running)TryAutoStartArmedDeckLink();
                    _masterAvBus.PushVideo(finalFrame);
                    _deckLinkBusSession?.PushVideo(finalFrame);
                    _deckLinkNativeOutput.PushVideo(finalFrame);
                    PublishSharedProgramFrame(finalFrame);
                    QueueProgramConfidencePreview();
                }
                catch(Exception ex){OnAirDiag("PROGRAM_COMPOSE_ERROR "+ex);ModuleDiagnostics.Write("PROGRAM_COMPOSITOR",ex);}
            }
        }
        finally
        {
            Interlocked.Exchange(ref _programComposePumpActive,0);
            if(Volatile.Read(ref _pendingProgramSourceFrame)!=null&&Interlocked.Exchange(ref _programComposePumpActive,1)==0)_=Task.Run(PumpLatestProgramFrame);
        }
    }

    private void QueueProgramConfidencePreview()
    {
        // Confidence preview is deliberately bounded to 25 fps. Broadcast outputs keep
        // their own selected cadence (25/29.97/30/50/59.94/60) and never wait for WPF.
        // At high source rates this removes UI render pressure without altering PTS,
        // MasterAvBus, streaming, graphics composition or DeckLink delivery.
        long now=Stopwatch.GetTimestamp();
        long minimumTicks=Stopwatch.Frequency/25;
        long previous=Volatile.Read(ref _lastProgramPreviewDispatchTick);
        if(now-previous<minimumTicks)return;
        if(Interlocked.CompareExchange(ref _lastProgramPreviewDispatchTick,now,previous)!=previous)return;
        if (Interlocked.Exchange(ref _programFrameDispatchPending, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            try
            {
                var latest = _latestProgramFrame;
                if (latest != null)
                {
                    ProgramImage.Source = latest;
                    _graphicsOutputWindow?.UpdateFrame(latest);
                    if (!string.Equals(_nowThumbnailFile, _programFile, StringComparison.OrdinalIgnoreCase) || NowThumbImage.Source == null)
                    {
                        NowThumbImage.Source = latest;
                        _nowThumbnailFile = _programFile;
                    }
                }
            }
            finally{Interlocked.Exchange(ref _programFrameDispatchPending,0);}
        }));
    }

    private BitmapSource RasterizeFinalProgramSource(BitmapSource frame)
    {
        // v0.6.8.50.36 MAIN FRAME THREAD-OWNERSHIP FIX:
        // This method is called by NativeVideoPlayer's decode worker. Reading
        // OutputResolutionCombo here threw a WPF cross-thread exception after the first
        // frames, terminated video decode, and left the independent audio path playing.
        string appliedScreen = Volatile.Read(ref _appliedOutputResolutionName);
        var fmt = StreamingEngine.ParseResolution(appliedScreen);
        int w = fmt.W > 0 ? fmt.W : frame.PixelWidth;
        int h = fmt.H > 0 ? fmt.H : frame.PixelHeight;
        double aspect = OutputDisplayAspect(appliedScreen, _videoScaleMode, w, h);
        return MasterAvBus.ScaleFrameToRaster(frame, w, h, _videoScaleMode, aspect);
    }

    private void CopyProgramProcessingSettings(NativeVideoPlayer video, NativeAudioPlayer audio)
    {
        video.HardwareDecodeEnabled = _hardwareDecodeEnabled;
        video.OutputBrightness = BrightnessSlider?.Value ?? 0;
        video.OutputContrast = ContrastSlider?.Value ?? 1;
        video.OutputSaturation = SaturationSlider?.Value ?? 1;
        video.OutputGamma = GammaSlider?.Value ?? 1;
        video.OutputBlackLevel = BlackLevelSlider?.Value ?? 0;
        video.OutputYLevel = YLevelSlider?.Value ?? 1;
        video.OutputULevel = ULevelSlider?.Value ?? 1;
        video.OutputVLevel = VLevelSlider?.Value ?? 1;
        video.OutputSharpness = SharpnessSlider?.Value ?? 0;
        video.OutputNoiseReduction = NoiseReductionSlider?.Value ?? 0;
        video.OutputAutoColor = AutoColorCheck?.IsChecked == true;
        video.OutputAutoQualityEnhance = AutoQualityCheck?.IsChecked == true;
        video.OutputAutoQualityStrength = AutoQualityStrengthSlider?.Value ?? 0.55;
        video.OutputAutoWhiteBalance = AutoWhiteBalanceCheck?.IsChecked == true;
        video.OutputWhiteTemperature = WhiteTemperatureSlider?.Value ?? 0;
        video.OutputWhiteTint = WhiteTintSlider?.Value ?? 0;
        video.OutputScanMode = Math.Max(0, ScanModeCombo?.SelectedIndex ?? 0);

        audio.MasterGainDb = (float)(MasterGainSlider?.Value ?? 0);
        audio.BassDb = (float)(BassSlider?.Value ?? 0);
        audio.TrebleDb = (float)(TrebleSlider?.Value ?? 0);
        audio.Balance = (float)(BalanceSlider?.Value ?? 0);
        audio.NormalizeEnabled = AudioNormalizeCheck?.IsChecked == true;
        audio.LoudnessControlEnabled = AudioLoudnessCheck?.IsChecked == true;
        if (Eq31 != null)
            audio.EqualizerDb = new float[] { (float)Eq31.Value,(float)Eq62.Value,(float)Eq125.Value,(float)Eq250.Value,(float)Eq500.Value,(float)Eq1k.Value,(float)Eq2k.Value,(float)Eq4k.Value,(float)Eq8k.Value,(float)Eq16k.Value };
    }

    public MainWindow()
    {
        InitializeComponent();
        RegisterModuleWorkers();
        try{_sharedProgramFrames=SharedVideoFrameBuffer.CreateOrOpen("SMARTPlayout.Program.BGRA");}
        catch(Exception ex){ModuleDiagnostics.Write("SHARED_PROGRAM_FRAME_INIT",ex);}
        try{_sharedProgramAudio=SharedAudioRingBuffer.CreateOrOpen("SMARTPlayout.Program.PCM");}
        catch(Exception ex){ModuleDiagnostics.Write("SHARED_PROGRAM_AUDIO_INIT",ex);}
        _moduleSupervisor.StateChanged += (module,state,detail) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ModuleDiagnostics.WriteInfo(module.ToString().ToUpperInvariant(),$"WORKER {state} • {detail}");
            if(state is ModuleState.Faulted or ModuleState.Reconnecting) StatusText.Text=$"{module} WORKER • {detail}";
        }));
        PlaylistList.ItemsSource = _items;
        ScheduleList.ItemsSource = _schedule;
        OnAirContentList.ItemsSource = _onAirItems;
        _streaming.StateChanged += (kind,state) => Dispatcher.BeginInvoke(new Action(() => UpdateStreamStatus(kind,state)));
        InitializeStreamingWorkspace();
        EmbeddedProgramManager.AddToScheduleRequested += async file => await ScheduleProgramFileAsync(file);
        EmbeddedProgramManager.PlayNowRequested += async (file, language, inPoint, outPoint) =>
        {
            _scheduledPlaylistActive = false; _activeScheduledJob = null; _activeScheduleEnd = null;
            await TakeToAirAtAsync(file, inPoint, language, outPoint > inPoint ? outPoint : null);
            ShowWorkspace("ON AIR");
        };
        EmbeddedProgramManager.PlayNextRequested += (file, language, inPoint, outPoint) =>
        {
            _nextFile = file; _nextAudioLanguage = language; _nextStartSeconds = Math.Max(0, inPoint); _nextEndSeconds = outPoint > inPoint ? outPoint : null;
            NextText.Text = Path.GetFileName(file); StatusText.Text = $"PROGRAM • PLAY NEXT SET • {language}";
            ArmProgramPreload(_nextFile, _nextStartSeconds, _nextAudioLanguage, _nextEndSeconds);
        };
        EmbeddedProgramManager.CloseRequested += () => ShowWorkspace("ON AIR");
        _items.CollectionChanged += (_, _) => SaveAutoPlaylistState();
        _schedule.CollectionChanged += (_, _) => SaveAutoScheduleState();
        // Metadata duration scanning uses the same proven FFmpeg bindings.
        // Initialize them once before any background compatibility probe runs.
        _preview.EnsureFFmpegInitialized();

        _preview.FrameReady += f =>
        {
            _latestPreviewFrame = f;
            if (Interlocked.Exchange(ref _previewFrameDispatchPending, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var latest = _latestPreviewFrame;
                if (latest != null) PreviewImage.Source = latest;
                InstantPreviewEmptyText.Visibility = Visibility.Collapsed;
                Interlocked.Exchange(ref _previewFrameDispatchPending, 0);
            }));
        };
        AttachProgramPipeline(_program, _programAudio);
        _studioPipPlayer.FrameReady += f =>
        {
            _studioPipFrame=f;
            _finalProgramCompositor.SetImage("studio.pip.video",f,new Rect(_studioPipX,_studioPipY,_studioPipW,_studioPipH),_studioPipOpacity,160);
        };
        _studioPipPlayer.Ended += () =>
        {
            if(!_studioPipLoop||string.IsNullOrWhiteSpace(_studioPipFile)||_studioPipRestarting)return;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if(_studioPipRestarting||!_studioPipLoop||string.IsNullOrWhiteSpace(_studioPipFile))return;
                _studioPipRestarting=true;
                try{await _studioPipPlayer.OpenAsync(_studioPipFile);await _studioPipPlayer.PlayAsync();}
                finally{_studioPipRestarting=false;}
            }));
        };
        // Chroma background owns a VIDEO-ONLY decoder. No NativeAudioPlayer is created,
        // so the background clip can never enter Program/MasterAvBus audio.
        _studioKeyBackgroundPlayer.FrameReady += frame =>
        {
            _finalProgramCompositor.SetImage("studio.key.background",frame,new Rect(0,0,1,1),1,-100);
            Dispatcher.BeginInvoke(new Action(() => StudioKeyBackgroundFrameChanged?.Invoke(frame)));
        };
        _studioKeyBackgroundPlayer.Ended += () =>
        {
            if(!_studioKeyBackgroundLoop||string.IsNullOrWhiteSpace(_studioKeyBackgroundFile)||_studioKeyBackgroundRestarting)return;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if(_studioKeyBackgroundRestarting||!_studioKeyBackgroundLoop||string.IsNullOrWhiteSpace(_studioKeyBackgroundFile))return;
                _studioKeyBackgroundRestarting=true;
                try{await _studioKeyBackgroundPlayer.OpenAsync(_studioKeyBackgroundFile);await _studioKeyBackgroundPlayer.PlayAsync();}
                catch(Exception ex){_studioKeyBackgroundLoop=false;StatusText.Text="CHROMA BACKGROUND LOOP FAILED • "+ex.Message;ModuleDiagnostics.Write("CHROMA_BACKGROUND_LOOP",ex);}
                finally{_studioKeyBackgroundRestarting=false;}
            }));
        };

        // Preview and On-Air are completely separate audio/video clock domains.
        _preview.MasterClockSeconds = null;
        _deckLinkOutput.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() => { if(DeckLinkOutputStatusText!=null) DeckLinkOutputStatusText.Text=status; }));
        _deckLinkNativeOutput.StatusChanged += status => Dispatcher.BeginInvoke(new Action(() => { if(DeckLinkOutputStatusText!=null) DeckLinkOutputStatusText.Text=status; }));
        // LIVE ingest must never run chroma/compositor work on the WPF dispatcher.
        // Keep only the newest frame while a previous frame is being processed, then
        // marshal the completed display frame back to the UI. This prevents an
        // unbounded dispatcher queue and the apparent hang/close seen in Key Studio.
        _liveCapture.FrameReady += QueueLiveFrame;
        _liveCapture.PcmReady += (pcm,count) => { if(_liveInputActive){ _masterAvBus.PushAudio(pcm,count); _deckLinkBusSession?.PushAudio(pcm,count); _deckLinkNativeOutput.PushAudio(pcm,count,48000,2); PublishSharedProgramAudio(pcm,count,48000,2); } };
        _liveCapture.LevelChanged += (l,r) => Dispatcher.BeginInvoke(new Action(() => { if(_liveInputActive){UpdateOnAirAudioMeters(l,r);StudioAudioLevelChanged?.Invoke(l,r);} }));
        _liveCapture.StatusChanged += text => Dispatcher.BeginInvoke(new Action(() => { if(_liveInputActive) StatusText.Text=text; }));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            double pos = Math.Max(0, _program.PositionSeconds);
            double dur = Math.Max(0, _program.DurationSeconds);
            double rem = dur > 0 ? Math.Max(0, dur - pos) : 0;
            double effectiveEnd = _programClipEndSeconds.HasValue ? Math.Min(dur > 0 ? dur : _programClipEndSeconds.Value, _programClipEndSeconds.Value) : dur;
            double effectiveRem = effectiveEnd > 0 ? Math.Max(0, effectiveEnd - pos) : rem;
            ProgramTimeText.Text = $"{Clock(pos)} / {Clock(effectiveEnd > 0 ? effectiveEnd : dur)}   REM {Clock(effectiveRem)}";
            UpdateBroadcastStatusStrip(pos, effectiveEnd > 0 ? effectiveEnd : dur, effectiveRem);
            if (!_onAirSeekDragging && !_onAirSeekInProgress && !_program.IsSeeking)
            {
                double seekStart = Math.Max(0, _programClipStartSeconds);
                double seekEnd = effectiveEnd > seekStart ? effectiveEnd : (dur > seekStart ? dur : seekStart + 0.001);
                OnAirSeekSlider.Minimum = seekStart;
                OnAirSeekSlider.Maximum = Math.Max(seekStart + 0.001, seekEnd);
                OnAirSeekSlider.Value = Math.Clamp(pos, OnAirSeekSlider.Minimum, OnAirSeekSlider.Maximum);
            }
            if (_programClipEndSeconds.HasValue && pos >= _programClipEndSeconds.Value - 0.04 && !_schedulerBusy)
            {
                _programClipEndSeconds = null;
                Dispatcher.BeginInvoke(async () => await HandleProgramEndedAsync());
            }
            UpdateInstantPreviewEditorUi();
            UpdateDashboardStatus();
            CheckScheduler();
            if (_onAirEnabled && !_operatorStopped && _programFile == null && !_fillerStarting && !_schedulerBusy && !_scheduledPlaylistActive && _activeScheduledJob == null && _fillerFiles.Count > 0)
                Dispatcher.BeginInvoke(async () => { _fillerStarting = true; try { await TryPlayFillerAsync(); } finally { _fillerStarting = false; } });
            UpdateStableRenderTarget(_preview, PreviewImage, ref _previewRenderWidth, ref _previewRenderHeight);
            UpdateProgramBroadcastRenderTarget();
        };
        LoadOperatorSettings();
        ApplyOperatorSettingsToPlayers();
        LoadStreamingSettings();
        LoadDeckLinkSettings();
        _ = StartDeferredCapabilityDetectionAsync();
        LoadPersistentState();
        LoadFillerState();
        RestoreStudioWatermarkIfAlwaysOn();
        UpdateDashboardStatus();
        _timer.Start();
        Closed += (_, _) =>
        {
            CaptureFillerResumePoint();
            SaveAutoPlaylistState();
            SaveAutoScheduleState();
            _timer.Stop();
            _preview.Dispose(); _previewAudio.Dispose();
            _program.Dispose(); _programAudio.Dispose();
            DisposeProgramPreload();
            _studioPipPlayer.Dispose(); _studioKeyBackgroundPlayer.Dispose();
            try { _graphicsOutputWindow?.Close(); } catch { }
            _deckLinkOutput.Dispose(); _deckLinkNativeOutput.Dispose(); _deckLinkBusSession?.Dispose(); _deckLinkBusSession=null;
            _liveCapture.Dispose(); _streaming.Dispose(); _masterAvBus.Dispose();
            _moduleSupervisor.Dispose();
            _sharedProgramFrames?.Dispose(); _sharedProgramFrames=null; _sharedProgramPixels=null;
            _sharedProgramAudio?.Dispose(); _sharedProgramAudio=null;
        };
    }

    private void PublishSharedProgramFrame(BitmapSource frame)
    {
        // Copy once only while an external output consumer is active. The UI preview,
        // decoder and MasterAvBus never wait for this transport.
        if(_sharedProgramFrames is null||_moduleSupervisor.State(ModuleKind.OutputWorker)!=ModuleState.Ready)return;
        try
        {
            BitmapSource source=frame;
            if(source.Format!=PixelFormats.Bgra32&&source.Format!=PixelFormats.Pbgra32)
            {
                var converted=new FormatConvertedBitmap(source,PixelFormats.Pbgra32,null,0);
                converted.Freeze();source=converted;
            }
            int stride=checked(source.PixelWidth*4),length=checked(stride*source.PixelHeight);
            if(_sharedProgramPixels is null||_sharedProgramPixels.Length<length)_sharedProgramPixels=new byte[length];
            source.CopyPixels(_sharedProgramPixels,stride,0);
            long number=Interlocked.Increment(ref _sharedProgramFrameNumber);
            long pts=TimeSpan.FromSeconds(Math.Max(0,_program.PositionSeconds)).Ticks;
            _sharedProgramFrames.Write(_sharedProgramPixels,source.PixelWidth,source.PixelHeight,stride,number,pts);
        }
        catch(Exception ex){ModuleDiagnostics.Write("SHARED_PROGRAM_FRAME_WRITE",ex);}
    }

    private void PublishSharedProgramAudio(byte[] pcm,int count,int sampleRate,int channels)
    {
        if(_sharedProgramAudio is null||_moduleSupervisor.State(ModuleKind.OutputWorker)!=ModuleState.Ready)return;
        try
        {
            const int maxPacket=32768;
            int offset=0;
            long basePts=TimeSpan.FromSeconds(Math.Max(0,Volatile.Read(ref _programAudioMediaSeconds))).Ticks;
            int bytesPerSecond=Math.Max(1,sampleRate*channels*2);
            while(offset<count)
            {
                int bytes=Math.Min(maxPacket,count-offset);
                bytes-=bytes%(channels*2);
                if(bytes<=0)break;
                long pts=basePts+(long)((double)offset/bytesPerSecond*TimeSpan.TicksPerSecond);
                _sharedProgramAudio.Write(pcm,offset,bytes,sampleRate,channels,pts);
                offset+=bytes;
            }
        }
        catch(Exception ex){ModuleDiagnostics.Write("SHARED_PROGRAM_AUDIO_WRITE",ex);}
    }

    private void RegisterModuleWorkers()
    {
        string root=AppContext.BaseDirectory;
        _moduleSupervisor.Register(ModuleKind.PlayoutEngine,Path.Combine(root,"SMARTPlayout.Engine.Worker.exe"));
        _moduleSupervisor.Register(ModuleKind.CgStudio,Path.Combine(root,"SMARTPlayout.CG.Worker.exe"));
        _moduleSupervisor.Register(ModuleKind.ChromaStudio,Path.Combine(root,"SMARTPlayout.Chroma.Worker.exe"));
        _moduleSupervisor.Register(ModuleKind.OutputWorker,Path.Combine(root,"SMARTPlayout.Output.Worker.exe"));
    }

    private void EnsureModuleWorker(ModuleKind module)
    {
        if(_moduleSupervisor.Start(module))return;
        ModuleDiagnostics.WriteInfo(module.ToString().ToUpperInvariant(),"WORKER UNAVAILABLE • EMBEDDED COMPATIBILITY PATH RETAINED");
    }

    private async Task MirrorModuleCommandAsync(ModuleKind module,string operation,object payload)
    {
        try
        {
            var reply=await _moduleSupervisor.SendAsync(module,operation,payload).ConfigureAwait(false);
            if(!reply.Success)ModuleDiagnostics.WriteInfo(module.ToString().ToUpperInvariant(),$"{reply.ErrorCode} • {reply.Message}");
        }
        catch(Exception ex)
        {
            // Control-plane mirroring is fail-safe: embedded on-air rendering remains authoritative
            // until each render engine is fully migrated and Windows hardware verification passes.
            ModuleDiagnostics.Write(module+"_IPC",ex);
        }
    }


    private static void UpdateStableRenderTarget(NativeVideoPlayer player, FrameworkElement view, ref int lastWidth, ref int lastHeight)
    {
        // A collapsed workspace reports 0x0. Never retarget FFmpeg to 2x2 while navigating.
        // Also avoid rebuilding swscale for tiny layout fluctuations. This is the same
        // principle used by the final SmartPlayer build for smooth MPEG/legacy playback.
        int width = (int)Math.Round(view.ActualWidth);
        int height = (int)Math.Round(view.ActualHeight);
        if (!view.IsVisible || width < 64 || height < 48) return;
        if (Math.Abs(width - lastWidth) < 4 && Math.Abs(height - lastHeight) < 4) return;
        lastWidth = width; lastHeight = height;
        // Same Smart Screen principle as the standalone player: give the decoder a little
        // render headroom so tiny layout changes do not rebuild swscale and the WPF surface
        // can fill the monitor without repeatedly retargeting the decoder.
        int renderWidth = Math.Max(64, (int)Math.Ceiling(width * 1.10));
        int renderHeight = Math.Max(48, (int)Math.Ceiling(height * 1.10));
        player.SetRenderTarget(renderWidth, renderHeight);
    }

    private void UpdateProgramBroadcastRenderTarget() => UpdateProgramBroadcastRenderTarget(_program);

    private void UpdateProgramBroadcastRenderTarget(NativeVideoPlayer player)
    {
        // v0.6.8.50.37 SINGLE HIGH-QUALITY OUTPUT RASTER:
        // Decode/convert once toward SCREEN/APPLIED. 16:9 sources reach the exact canvas;
        // 4:3/other sources reach the largest aspect-correct active picture and the Final
        // scaler only pads/frames it. This removes the old small intermediate raster and
        // prevents double-upscale softness before DeckLink/streaming.
        string screen = Volatile.Read(ref _appliedOutputResolutionName);
        var fmt = StreamingEngine.ParseResolution(screen);
        _programRenderWidth = fmt.W > 0 ? fmt.W : 1920;
        _programRenderHeight = fmt.H > 0 ? fmt.H : 1080;
        player.SetRenderTarget(_programRenderWidth, _programRenderHeight);
    }

    private static bool IsMedia(string p) => MediaExt.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase);
    private static string Clock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss");
    private static string EditorClock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"hh\:mm\:ss\.fff");

    private void RefreshRows()
    {
        var estimate = DateTime.Now;
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i].Index = i + 1;
            _items[i].StartTimeText = estimate.ToString("hh:mm:ss tt");
            estimate = estimate.AddSeconds(_items[i].PlayDurationSeconds);
        }
        PlaylistList.Items.Refresh();
        PlaylistCountText.Text = $"{_items.Count} ITEMS";
    }

    private void AddPaths(System.Collections.Generic.IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(File.Exists).Where(IsMedia))
        {
            if (_items.Any(x => string.Equals(x.FilePath, p, StringComparison.OrdinalIgnoreCase)))
                continue;

            var row = new Row { FilePath = p, Description = Path.GetFileNameWithoutExtension(p), AudioLanguage = AudioLanguageHelper.DefaultLanguage };
            _items.Add(row);
            _ = PopulateMediaInfoAsync(row);
        }

        RefreshRows();
    }

    private async Task PopulateMediaInfoAsync(Row row)
    {
        await _mediaProbeGate.WaitAsync();
        try
        {
            var result = await Task.Run(() => AudioLanguageHelper.Probe(row.FilePath));
            double probedDuration = Math.Max(0, result.DurationSeconds);
            if (row.DurationSeconds <= 0) row.DurationSeconds = probedDuration;
            if (row.SourceDurationSeconds <= 0) row.SourceDurationSeconds = probedDuration;
            if (row.OutPointSeconds <= 0) row.OutPointSeconds = row.SourceDurationSeconds;
            if (string.IsNullOrWhiteSpace(row.Description)) row.Description = Path.GetFileNameWithoutExtension(row.FilePath);
            var languages = await Task.Run(() => AudioLanguageHelper.GetLanguages(row.FilePath));
            await Dispatcher.InvokeAsync(() =>
            {
                row.AvailableAudioLanguages.Clear();
                foreach (var lang in languages) row.AvailableAudioLanguages.Add(lang);
                if (!row.AvailableAudioLanguages.Any(l => string.Equals(l, row.AudioLanguage, StringComparison.OrdinalIgnoreCase)))
                    row.AvailableAudioLanguages.Insert(0, row.AudioLanguage);
                RefreshRows();
            });
        }
        catch
        {
            // Keep the file usable even if metadata probing fails.
        }
        finally { _mediaProbeGate.Release(); }
    }

    private async Task StartDeferredCapabilityDetectionAsync()
    {
        // Let current Program/filler and its B-slot establish first. Capability probing
        // launches helper processes and must never compete with cold-start playback.
        await Task.Delay(3000);
        try { await RefreshStreamingCapabilitiesAsync(); } catch { }
        try { await RefreshOutputCapabilitiesAsync(false); } catch { }
    }

    private void InstantAudioLanguage_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingPersistentState) return;
        SaveAutoPlaylistState();
        if (sender is System.Windows.Controls.ComboBox cb && cb.DataContext is Row row)
            StatusText.Text = $"INSTANT PLAYLIST AUDIO • {row.AudioLanguage}";
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Media files|*.*" };
        if (d.ShowDialog() == true) AddPaths(d.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        // "Add Folder / Multi Add" should let the operator SEE the files and choose
        // multiple media files, instead of showing the folder-only browser.
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Folder / Multi Add — select one or more media files",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.mxf;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.flv;*.m4v;*.3gp;*.ogv;*.m2p;*.m2v;*.mpv;*.asf;*.f4v;*.ogg;*.rm;*.rmvb;*.nut;*.y4m;*.dv;*.divx;*.xvid;*.h264;*.h265;*.hevc;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.opus|All files|*.*"
        };

        if (d.ShowDialog() == true)
            AddPaths(d.FileNames);
    }

    private async void SavePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var d = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save SMART PLAYOUT Playlist",
            Filter = "SMART PLAYOUT Playlist|*.splaylist|JSON|*.json",
            DefaultExt = ".splaylist",
            AddExtension = true,
            FileName = "playlist",
            InitialDirectory = DataStorage.Playlists
        };

        if (d.ShowDialog() != true) return;

        try
        {
            var model = new SavedPlaylistModel
            {
                Items = _items.Select(x => new SavedPlaylistItemModel { FilePath = x.FilePath, AudioLanguage = x.AudioLanguage, SourceDurationSeconds = x.SourceDurationSeconds, InPointSeconds = x.InPointSeconds, OutPointSeconds = x.OutPointSeconds, Description = x.Description }).ToList()
            };
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            string persistent = DataStorage.PlaylistPath(Path.GetFileNameWithoutExtension(d.FileName));
            if (!string.Equals(Path.GetFullPath(d.FileName), Path.GetFullPath(persistent), StringComparison.OrdinalIgnoreCase))
                await File.WriteAllTextAsync(d.FileName, json);
            DataStorage.WritePersistentText(persistent, json, "Playlists");
            SaveAutoPlaylistState();
            StatusText.Text = "PLAYLIST SAVED";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not save playlist.\n\n" + ex.Message, "SMART PLAYOUT",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load SMART PLAYOUT Playlist",
            Filter = "SMART PLAYOUT Playlist|*.splaylist;*.json|All files|*.*",
            Multiselect = false,
            InitialDirectory = DataStorage.Playlists
        };

        if (d.ShowDialog() != true) return;

        try
        {
            string json = await File.ReadAllTextAsync(d.FileName);
            var loaded = ParsePlaylist(json);

            _items.Clear();
            _nextFile = null;
            NextText.Text = "—";
            foreach (var item in loaded)
            {
                if (!File.Exists(item.FilePath) || !IsMedia(item.FilePath)) continue;
                var row = new Row { FilePath = item.FilePath, AudioLanguage = string.IsNullOrWhiteSpace(item.AudioLanguage) ? AudioLanguageHelper.DefaultLanguage : item.AudioLanguage, SourceDurationSeconds = item.SourceDurationSeconds, InPointSeconds = Math.Max(0, item.InPointSeconds), OutPointSeconds = Math.Max(0, item.OutPointSeconds), Description = string.IsNullOrWhiteSpace(item.Description) ? Path.GetFileNameWithoutExtension(item.FilePath) : item.Description };
                _items.Add(row); _ = PopulateMediaInfoAsync(row);
            }
            RefreshRows();
            StatusText.Text = "PLAYLIST LOADED";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not load playlist.\n\n" + ex.Message, "SMART PLAYOUT",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static List<SavedPlaylistItemModel> ParsePlaylist(string json)
    {
        try
        {
            var model = JsonSerializer.Deserialize<SavedPlaylistModel>(json);
            if (model?.Items != null && model.Items.Count > 0) return model.Items;
        }
        catch { }
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return paths.Select(p => new SavedPlaylistItemModel { FilePath = p, AudioLanguage = AudioLanguageHelper.DefaultLanguage }).ToList();
        }
        catch { return new List<SavedPlaylistItemModel>(); }
    }

    private void SortScheduleRows()
    {
        var sorted = _schedule.OrderBy(s => s.When).ToList();
        _schedule.Clear();
        foreach (var item in sorted) _schedule.Add(item);
    }

    private bool IsProgramOnAir() =>
        _programFile != null && !string.Equals(ProgramNameText.Text, "OFF AIR", StringComparison.OrdinalIgnoreCase);

    private DateTime? GetAutoContinuityStart()
    {
        // Professional continuity: next START is the latest calculated END.
        // DateTime naturally carries across midnight (11:xx PM -> next day 12:xx AM).
        return _schedule
            .Where(s => s.EndWhen.HasValue)
            .OrderByDescending(s => s.EndWhen!.Value)
            .Select(s => s.EndWhen)
            .FirstOrDefault();
    }

    private ScheduleDialog CreateScheduleDialog(string name, double duration)
    {
        return new ScheduleDialog(name, duration, GetAutoContinuityStart()) { Owner = this };
    }

    private void ApplyScheduleOptions(ScheduleRow row, ScheduleDialog dialog)
    {
        row.When = dialog.StartWhen;
        row.EndWhen = dialog.EndWhen;
        row.EndMode = dialog.EndMode;
        row.StartMode = dialog.StartMode;
        row.ExactEnd = dialog.ExactEnd;
        row.AutoChain = dialog.AutoChain;
    }

    private async Task<bool> StartPendingScheduleIfAnyAsync()
    {
        var pending = _schedule
            .Where(s => s.Pending && !s.Fired && s.When <= DateTime.Now)
            .OrderBy(s => s.When)
            .FirstOrDefault();

        if (pending == null) return false;

        pending.Pending = false;
        pending.Fired = true;
        ScheduleList.Items.Refresh();
        await StartScheduleJobAsync(pending);
        return true;
    }

    private void CaptureFillerResumePoint()
    {
        if (!_fillerActive || string.IsNullOrWhiteSpace(_programFile) || !File.Exists(_programFile)) return;
        _fillerResumeFile = _programFile;
        _fillerResumePosition = Math.Max(0, _programAudio.IsOpen ? _programAudio.PlaybackPositionSeconds : _program.PositionSeconds);
        _fillerResumePending = true;
        int idx = _fillerFiles.FindIndex(f => string.Equals(f, _fillerResumeFile, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) _fillerIndex = (idx + 1) % Math.Max(1, _fillerFiles.Count);
        SaveFillerState();
    }

    private async Task StartScheduleJobAsync(ScheduleRow job)
    {
        CaptureFillerResumePoint();
        bool isTimelineProgram =
            string.Equals(job.Kind, "PLAYLIST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(job.Kind, "PROGRAM", StringComparison.OrdinalIgnoreCase);

        if (isTimelineProgram)
        {
            StatusText.Text = job.StartMode == "TIME"
                ? "TIME SCHEDULE • CURRENT ITEM FINISHED"
                : $"EXACT SCHEDULE {job.Kind} TAKE TO AIR";
            await StartScheduledPlaylistAsync(job);
        }
        else
        {
            _activeScheduledJob = null;
            _scheduledPlaylistActive = false;
            _activeScheduleEnd = job.ExactEnd ? job.EndWhen : null;
            StatusText.Text = job.StartMode == "TIME"
                ? "TIME SCHEDULE • CURRENT ITEM FINISHED"
                : "EXACT SCHEDULE TAKE TO AIR";
            await TakeToAirAtAsync(job.FilePath, job.InPoints.Count > 0 ? job.InPoints[0] : 0, job.AudioLanguage, job.OutPoints.Count > 0 && job.OutPoints[0] > (job.InPoints.Count > 0 ? job.InPoints[0] : 0) ? job.OutPoints[0] : null);
        }
    }

    private void ProgramManager_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkspace("PROGRAM");
    }

    private async void AddProgramSchedule_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Schedule Saved Program",
            Filter = "SMART PLAYOUT Program (*.sprogram)|*.sprogram|JSON (*.json)|*.json",
            Multiselect = false,
            InitialDirectory = DataStorage.Programs
        };
        if (d.ShowDialog() == true)
            await ScheduleProgramFileAsync(d.FileName);
    }

    private async Task ScheduleProgramFileAsync(string programFile)
    {
        try
        {
            var model = JsonSerializer.Deserialize<SavedProgramModel>(File.ReadAllText(programFile));
            if (model == null || model.Files.Count == 0)
            {
                System.Windows.MessageBox.Show("This Program has no media.", "SMART PLAYOUT");
                return;
            }

            var validIndexed = model.Files.Select((file, i) => new { file, i }).Where(x => File.Exists(x.file)).ToList();
            var validFiles = validIndexed.Select(x => x.file).ToList();
            var audioLanguages = validIndexed.Select(x => x.i < model.AudioLanguages.Count && !string.IsNullOrWhiteSpace(model.AudioLanguages[x.i])
                ? model.AudioLanguages[x.i] : AudioLanguageHelper.DefaultLanguage).ToList();
            var inPoints = validIndexed.Select(x => x.i < model.InPoints.Count ? Math.Max(0, model.InPoints[x.i]) : 0).ToList();
            var outPoints = validIndexed.Select(x => x.i < model.OutPoints.Count ? Math.Max(0, model.OutPoints[x.i]) : 0).ToList();
            var descriptions = validIndexed.Select(x => x.i < model.Descriptions.Count && !string.IsNullOrWhiteSpace(model.Descriptions[x.i])
                ? model.Descriptions[x.i] : Path.GetFileNameWithoutExtension(x.file)).ToList();
            if (validFiles.Count == 0)
            {
                System.Windows.MessageBox.Show("No valid media files were found in this Program.", "SMART PLAYOUT");
                return;
            }

            StatusText.Text = "CALCULATING PROGRAM TIMELINE...";
            var durations = new List<double>();
            foreach (var file in validFiles)
            {
                double duration = 0;
                int originalIndex = model.Files.FindIndex(p =>
                    string.Equals(p, file, StringComparison.OrdinalIgnoreCase));
                if (originalIndex >= 0 && originalIndex < model.Durations.Count)
                    duration = Math.Max(0, model.Durations[originalIndex]);
                if (duration <= 0)
                {
                    var existing = _items.FirstOrDefault(r =>
                        string.Equals(r.FilePath, file, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && existing.DurationSeconds > 0)
                        duration = existing.DurationSeconds;
                }
                if (duration <= 0)
                {
                    try
                    {
                        var probe = await Task.Run(() => MediaCompatibilityProbe.Probe(file));
                        duration = Math.Max(0, probe.DurationSeconds);
                    }
                    catch { }
                }
                durations.Add(duration);
            }

            double total = durations.Sum();
            string displayName = string.IsNullOrWhiteSpace(model.Name)
                ? Path.GetFileNameWithoutExtension(programFile)
                : model.Name.Trim();
            var dialog = CreateScheduleDialog(displayName, total);
            if (dialog.ShowDialog() != true) return;

            var row = new ScheduleRow
            {
                When = dialog.StartWhen,
                EndWhen = dialog.EndWhen,
                FilePath = programFile,
                ProgramName = displayName,
                Kind = "PROGRAM",
                Files = validFiles,
                Durations = durations,
                InPoints = inPoints,
                OutPoints = outPoints,
                Descriptions = descriptions,
                AudioLanguages = audioLanguages,
                StartMode = dialog.StartMode,
                EndMode = dialog.EndMode,
                ExactEnd = dialog.ExactEnd,
                AutoChain = dialog.AutoChain
            };
            _schedule.Add(row);
            SortScheduleRows();
            SaveAutoScheduleState();
            StatusText.Text = $"PROGRAM SCHEDULE ADDED • {displayName} • {validFiles.Count} ITEMS • {Clock(total)}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "SMART PLAYOUT");
        }
    }

    private async void AddPlaylistSchedule_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Schedule Saved Playlist",
            Filter = "SMART PLAYOUT Playlist (*.splaylist)|*.splaylist|JSON Playlist (*.json)|*.json|All files (*.*)|*.*"
        };
        if (d.ShowDialog() != true) return;
        try
        {
            var playlistItems = ParsePlaylist(File.ReadAllText(d.FileName));
            var validItems = playlistItems.Where(i => File.Exists(i.FilePath)).ToList();
            var valid = validItems.Select(i => i.FilePath).ToList();
            var playlistAudioLanguages = validItems.Select(i => string.IsNullOrWhiteSpace(i.AudioLanguage) ? AudioLanguageHelper.DefaultLanguage : i.AudioLanguage).ToList();
            var playlistInPoints = validItems.Select(i => Math.Max(0, i.InPointSeconds)).ToList();
            var playlistOutPoints = validItems.Select(i => Math.Max(0, i.OutPointSeconds)).ToList();
            var playlistDescriptions = validItems.Select(i => string.IsNullOrWhiteSpace(i.Description) ? Path.GetFileNameWithoutExtension(i.FilePath) : i.Description).ToList();
            if (valid.Count == 0)
            {
                System.Windows.MessageBox.Show("This playlist has no available media files.", "SMART PLAYOUT",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            StatusText.Text = "CALCULATING PLAYLIST TIMELINE...";
            var durations = new List<double>();
            for (int i = 0; i < valid.Count; i++)
            {
                var file = valid[i];
                double sourceDuration = validItems[i].SourceDurationSeconds;
                if (sourceDuration <= 0)
                {
                    var existing = _items.FirstOrDefault(x => string.Equals(x.FilePath, file, StringComparison.OrdinalIgnoreCase));
                    sourceDuration = existing != null && existing.SourceDurationSeconds > 0 ? existing.SourceDurationSeconds : (existing?.DurationSeconds ?? 0);
                }
                if (sourceDuration <= 0)
                {
                    try { sourceDuration = Math.Max(0, (await Task.Run(() => MediaCompatibilityProbe.Probe(file))).DurationSeconds); }
                    catch { }
                }
                if (playlistOutPoints[i] <= 0) playlistOutPoints[i] = sourceDuration;
                double effective = playlistOutPoints[i] > playlistInPoints[i] ? playlistOutPoints[i] - playlistInPoints[i] : sourceDuration;
                durations.Add(Math.Max(0, effective));
            }

            double total = durations.Sum();
            var scheduleDialog = CreateScheduleDialog(Path.GetFileNameWithoutExtension(d.FileName), total);
            if (scheduleDialog.ShowDialog() != true) return;

            var scheduleRow = new ScheduleRow
            {
                FilePath = d.FileName,
                Kind = "PLAYLIST",
                Files = valid,
                Durations = durations,
                InPoints = playlistInPoints,
                OutPoints = playlistOutPoints,
                Descriptions = playlistDescriptions,
                AudioLanguages = playlistAudioLanguages
            };
            ApplyScheduleOptions(scheduleRow, scheduleDialog);
            _schedule.Add(scheduleRow);
            SortScheduleRows();
            StatusText.Text = $"PLAYLIST SCHEDULED • {valid.Count} ITEMS • {Clock(total)}";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not load playlist.\n" + ex.Message, "SMART PLAYOUT",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StartScheduledPlaylistAsync(ScheduleRow job)
    {
        _activeScheduledJob = job;
        _activeScheduleEnd = job.ExactEnd ? job.EndWhen : null;
        _scheduledPlaylistActive = true;
        await PlayScheduledTimelinePositionAsync(job);
    }

    private async Task PlayScheduledTimelinePositionAsync(ScheduleRow job)
    {
        if (job.Files.Count == 0)
        {
            _scheduledPlaylistActive = false;
            _activeScheduledJob = null;
            StatusText.Text = "SCHEDULE PLAYLIST EMPTY";
            return;
        }

        if (job.ExactEnd && job.EndWhen.HasValue && DateTime.Now >= job.EndWhen.Value)
        {
            StopScheduledWindow("EXACT SCHEDULE END");
            return;
        }

        // Absolute scheduled timeline:
        // elapsed wall-clock time since the scheduled start decides which clip
        // and which position inside that clip should currently be ON AIR.
        double elapsed = Math.Max(0, (DateTime.Now - job.When).TotalSeconds);
        double total = job.Durations.Sum();

        if (total > 0 && elapsed >= total)
        {
            _scheduledPlaylistActive = false;
            _activeScheduledJob = null;
            _scheduledRunQueue.Clear();
            _nextFile = null;
            NextText.Text = "—";
            _program.Stop();
            _programAudio.Stop();
            ProgramImage.Source = null;
            ProgramNameText.Text = "OFF AIR";
            StatusText.Text = "SCHEDULE PLAYLIST ENDED";
            _programFile = null;
            Dispatcher.BeginInvoke(async () => await TryPlayFillerAsync());
            return;
        }

        int index = 0;
        double startSeconds = elapsed;

        for (int i = 0; i < job.Files.Count; i++)
        {
            double duration = (i < job.Durations.Count) ? Math.Max(0, job.Durations[i]) : 0;

            // Unknown duration cannot safely be skipped; play that item from the
            // computed local offset only when it is the current candidate.
            if (duration <= 0)
            {
                index = i;
                startSeconds = 0;
                break;
            }

            if (startSeconds < duration)
            {
                index = i;
                break;
            }

            startSeconds -= duration;
            index = i + 1;
        }

        if (index >= job.Files.Count)
        {
            _scheduledPlaylistActive = false;
            _activeScheduledJob = null;
            _scheduledRunQueue.Clear();
            _nextFile = null;
            NextText.Text = "—";
            StatusText.Text = "SCHEDULE PLAYLIST ENDED";
            _programFile = null;
            Dispatcher.BeginInvoke(async () => await TryPlayFillerAsync());
            return;
        }

        _scheduledRunQueue.Clear();
        for (int i = index + 1; i < job.Files.Count; i++)
        {
            if (File.Exists(job.Files[i]))
                _scheduledRunQueue.Enqueue(job.Files[i]);
        }

        string scheduledLanguage = index < job.AudioLanguages.Count && !string.IsNullOrWhiteSpace(job.AudioLanguages[index])
            ? job.AudioLanguages[index] : AudioLanguageHelper.DefaultLanguage;
        double clipIn = index < job.InPoints.Count ? Math.Max(0, job.InPoints[index]) : 0;
        double clipOut = index < job.OutPoints.Count ? Math.Max(0, job.OutPoints[index]) : 0;
        double absoluteStart = clipIn + startSeconds;
        await TakeToAirAtAsync(job.Files[index], absoluteStart, scheduledLanguage, clipOut > clipIn ? clipOut : null);
        UpdateScheduledNext();

        StatusText.Text = startSeconds > 0.5
            ? $"SCHEDULE SYNC • +{Clock(startSeconds)}"
            : "SCHEDULE PLAYLIST ON AIR";
    }

    private void UpdateScheduledNext()
    {
        _nextFile = _scheduledRunQueue.Count > 0 ? _scheduledRunQueue.Peek() : null;
        NextText.Text = _nextFile == null ? "—" : Path.GetFileName(_nextFile);
        ArmProgramPreload(_nextFile, 0, AudioLanguageHelper.DefaultLanguage, null);
    }

    private async Task HandleProgramEndedAsync()
    {
        // A natural Filler end advances the persistent rotation cursor. It must not
        // be treated like an interruption/resume and must never jump into Instant Next.
        if (_fillerActive)
        {
            _fillerResumePending = false;
            _fillerResumeFile = null;
            _fillerResumePosition = 0;
            _fillerActive = false;
            _programFile = null;
            _nextFile = null;
            SaveFillerState();
            await TryPlayFillerAsync();
            return;
        }

        if (await StartPendingScheduleIfAnyAsync())
        {
            _breakActive = false;
            _breakResumeFile = null;
            return;
        }

        if (_breakActive)
        {
            _breakActive = false;
            if (_scheduledPlaylistActive && _activeScheduledJob != null)
            {
                await PlayScheduledTimelinePositionAsync(_activeScheduledJob);
                return;
            }
            var resumeFile = _breakResumeFile;
            var resumeAt = _breakResumePosition;
            var resumeAudio = _breakResumeAudio;
            var resumeEnd = _breakResumeEnd;
            _breakResumeFile = null;
            _breakResumePosition = 0;
            _breakResumeEnd = null;
            if (!string.IsNullOrWhiteSpace(resumeFile) && File.Exists(resumeFile))
            {
                await TakeToAirAtAsync(resumeFile, resumeAt, resumeAudio, resumeEnd);
                StatusText.Text = "BREAK COMPLETE • PROGRAM RESUMED";
                return;
            }
        }

        if (_scheduledPlaylistActive && _activeScheduledJob != null)
        {
            // Recalculate from the schedule clock instead of blindly advancing.
            // This prevents drift and also catches up after any interruption.
            await PlayScheduledTimelinePositionAsync(_activeScheduledJob);
            return;
        }

        await PlayNextAsync();
    }

    private void SaveAutoPlaylistState()
    {
        if (_loadingPersistentState) return;
        try
        {
            var model = new SavedPlaylistModel
            {
                Items = _items.Select(x => new SavedPlaylistItemModel { FilePath = x.FilePath, AudioLanguage = x.AudioLanguage, SourceDurationSeconds = x.SourceDurationSeconds, InPointSeconds = x.InPointSeconds, OutPointSeconds = x.OutPointSeconds, Description = x.Description }).ToList()
            };
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            DataStorage.WritePersistentText(DataStorage.CurrentPlaylist, json, "AutoState");
        }
        catch { }
    }

    private void SaveAutoScheduleState()
    {
        if (_loadingPersistentState) return;
        try
        {
            var model = _schedule.Select(s => new ScheduleFileModel
            {
                When = s.When,
                EndWhen = s.EndWhen,
                EndMode = s.EndMode,
                StartMode = s.StartMode,
                ExactEnd = s.ExactEnd,
                AutoChain = s.AutoChain,
                FilePath = s.FilePath,
                ProgramName = s.ProgramName,
                AudioLanguage = s.AudioLanguage,
                AudioLanguages = s.AudioLanguages.ToList(),
                Kind = s.Kind,
                Files = s.Files.ToList(),
                Durations = s.Durations.ToList(),
                InPoints = s.InPoints.ToList(),
                OutPoints = s.OutPoints.ToList(),
                Descriptions = s.Descriptions.ToList()
            }).ToList();
            string json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            DataStorage.WritePersistentText(DataStorage.CurrentSchedule, json, "AutoState");
        }
        catch { }
    }

    private void LoadPersistentState()
    {
        _loadingPersistentState = true;
        try
        {
            if (File.Exists(DataStorage.CurrentPlaylist))
            {
                var loaded = ParsePlaylist(File.ReadAllText(DataStorage.CurrentPlaylist));
                _items.Clear();
                foreach (var item in loaded)
                {
                    if (!File.Exists(item.FilePath) || !IsMedia(item.FilePath)) continue;
                    var row = new Row { FilePath = item.FilePath, AudioLanguage = string.IsNullOrWhiteSpace(item.AudioLanguage) ? AudioLanguageHelper.DefaultLanguage : item.AudioLanguage, SourceDurationSeconds = item.SourceDurationSeconds, InPointSeconds = Math.Max(0, item.InPointSeconds), OutPointSeconds = Math.Max(0, item.OutPointSeconds), Description = string.IsNullOrWhiteSpace(item.Description) ? Path.GetFileNameWithoutExtension(item.FilePath) : item.Description };
                    _items.Add(row); _ = PopulateMediaInfoAsync(row);
                }
                RefreshRows();
            }

            if (File.Exists(DataStorage.CurrentSchedule))
            {
                var model = JsonSerializer.Deserialize<List<ScheduleFileModel>>(File.ReadAllText(DataStorage.CurrentSchedule))
                    ?? new List<ScheduleFileModel>();
                _schedule.Clear();
                foreach (var s in model.OrderBy(x => x.When))
                {
                    bool isTimeline = string.Equals(s.Kind, "PLAYLIST", StringComparison.OrdinalIgnoreCase) ||
                                      string.Equals(s.Kind, "PROGRAM", StringComparison.OrdinalIgnoreCase);
                    if (isTimeline)
                    {
                        var files = (s.Files ?? new List<string>()).Where(File.Exists).ToList();
                        if (files.Count == 0) continue;
                        var durations = s.Durations ?? new List<double>();
                        durations = files.Select((_, i) => i < durations.Count ? Math.Max(0, durations[i]) : 0).ToList();
                        _schedule.Add(new ScheduleRow
                        {
                            When = s.When,
                            EndWhen = s.EndWhen,
                            EndMode = s.EndMode,
                            StartMode = s.StartMode,
                            ExactEnd = s.ExactEnd,
                            AutoChain = s.AutoChain,
                            FilePath = s.FilePath,
                            ProgramName = s.ProgramName,
                            AudioLanguage = string.IsNullOrWhiteSpace(s.AudioLanguage) ? AudioLanguageHelper.DefaultLanguage : s.AudioLanguage,
                            AudioLanguages = (s.AudioLanguages ?? new List<string>()).Select(l => string.IsNullOrWhiteSpace(l) ? AudioLanguageHelper.DefaultLanguage : l).ToList(),
                            Kind = s.Kind,
                            Files = files,
                            Durations = durations,
                            InPoints = s.InPoints ?? new List<double>(),
                            OutPoints = s.OutPoints ?? new List<double>(),
                            Descriptions = s.Descriptions ?? new List<string>(),
                            Fired = s.When < DateTime.Now
                        });
                    }
                    else if (File.Exists(s.FilePath))
                    {
                        _schedule.Add(new ScheduleRow
                        {
                            When = s.When,
                            EndWhen = s.EndWhen,
                            EndMode = s.EndMode,
                            StartMode = s.StartMode,
                            ExactEnd = s.ExactEnd,
                            AutoChain = s.AutoChain,
                            FilePath = s.FilePath,
                            ProgramName = s.ProgramName,
                            AudioLanguage = string.IsNullOrWhiteSpace(s.AudioLanguage) ? AudioLanguageHelper.DefaultLanguage : s.AudioLanguage,
                            Kind = "MEDIA",
                            InPoints = s.InPoints ?? new List<double>(),
                            OutPoints = s.OutPoints ?? new List<double>(),
                            Descriptions = s.Descriptions ?? new List<string>(),
                            Fired = s.When < DateTime.Now
                        });
                    }
                }
                SortScheduleRows();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "DATA RESTORE WARNING";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        finally
        {
            _loadingPersistentState = false;
        }
    }

    private void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Row row)
        {
            System.Windows.MessageBox.Show("Select one media item in the Playlist first.", "SMART PLAYOUT",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var scheduleDialog = CreateScheduleDialog(row.Name, row.PlayDurationSeconds);
        if (scheduleDialog.ShowDialog() != true) return;

        var scheduleRow = new ScheduleRow
        {
            FilePath = row.FilePath,
            AudioLanguage = row.AudioLanguage,
            Kind = "MEDIA",
            InPoints = new List<double> { row.InPointSeconds },
            OutPoints = new List<double> { row.OutPointSeconds },
            Descriptions = new List<string> { row.Description }
        };
        ApplyScheduleOptions(scheduleRow, scheduleDialog);
        _schedule.Add(scheduleRow);
        SortScheduleRows();
        StatusText.Text = $"SCHEDULE ADDED • {scheduleRow.StartModeText}";
    }

    private void SaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save SMART PLAYOUT Schedule",
            Filter = "SMART PLAYOUT Schedule (*.sschedule)|*.sschedule|JSON (*.json)|*.json",
            DefaultExt = ".sschedule",
            AddExtension = true,
            InitialDirectory = DataStorage.Schedules
        };
        if (d.ShowDialog() != true) return;

        try
        {
            var model = _schedule.Select(s => new ScheduleFileModel
            {
                When = s.When,
                EndWhen = s.EndWhen,
                EndMode = s.EndMode,
                StartMode = s.StartMode,
                ExactEnd = s.ExactEnd || (s.EndWhen.HasValue && !string.Equals(s.EndMode, "CONTINUE", StringComparison.OrdinalIgnoreCase)),
                AutoChain = s.AutoChain,
                FilePath = s.FilePath,
                ProgramName = s.ProgramName,
                AudioLanguage = s.AudioLanguage,
                AudioLanguages = s.AudioLanguages.ToList(),
                Kind = s.Kind,
                Files = s.Files.ToList(),
                Durations = s.Durations.ToList(),
                InPoints = s.InPoints.ToList(),
                OutPoints = s.OutPoints.ToList(),
                Descriptions = s.Descriptions.ToList()
            }).ToList();

            var json = System.Text.Json.JsonSerializer.Serialize(model,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string persistent = DataStorage.SchedulePath(Path.GetFileNameWithoutExtension(d.FileName));
            if (!string.Equals(Path.GetFullPath(d.FileName), Path.GetFullPath(persistent), StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(d.FileName, json);
            DataStorage.WritePersistentText(persistent, json, "Schedules");
            SaveAutoScheduleState();
            StatusText.Text = $"SCHEDULE SAVED • {model.Count} JOBS";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not save schedule.\n" + ex.Message,
                "SMART PLAYOUT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadSchedule_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load SMART PLAYOUT Schedule",
            Filter = "SMART PLAYOUT Schedule (*.sschedule)|*.sschedule|JSON (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = DataStorage.Schedules
        };
        if (d.ShowDialog() != true) return;

        try
        {
            var model = System.Text.Json.JsonSerializer.Deserialize<List<ScheduleFileModel>>(
                File.ReadAllText(d.FileName)) ?? new List<ScheduleFileModel>();

            _schedule.Clear();

            foreach (var s in model.OrderBy(s => s.When))
            {
                if (s.EndWhen.HasValue && s.EndWhen.Value <= s.When)
                    continue;

                bool isPlaylist =
                    string.Equals(s.Kind, "PLAYLIST", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Kind, "PROGRAM", StringComparison.OrdinalIgnoreCase);

                // Keep only entries whose referenced source still exists.
                if (isPlaylist)
                {
                    var validFiles = (s.Files ?? new List<string>()).Where(File.Exists).ToList();
                    if (validFiles.Count == 0) continue;

                    var durations = s.Durations ?? new List<double>();
                    if (durations.Count != validFiles.Count)
                    {
                        // Timeline can be recalculated by re-adding the playlist if files changed.
                        // Keep available values aligned safely.
                        durations = validFiles.Select((_, i) =>
                            i < durations.Count ? Math.Max(0, durations[i]) : 0).ToList();
                    }

                    _schedule.Add(new ScheduleRow
                    {
                        When = s.When,
                        EndWhen = s.EndWhen,
                        EndMode = string.IsNullOrWhiteSpace(s.EndMode)
                            ? (s.EndWhen.HasValue ? "MANUAL END" : "CONTINUE") : s.EndMode,
                        StartMode = string.IsNullOrWhiteSpace(s.StartMode) ? "EXACT" : s.StartMode,
                        ExactEnd = s.ExactEnd || (s.EndWhen.HasValue && !string.Equals(s.EndMode, "CONTINUE", StringComparison.OrdinalIgnoreCase)),
                        AutoChain = s.AutoChain,
                        FilePath = s.FilePath,
                        ProgramName = s.ProgramName,
                        Kind = string.IsNullOrWhiteSpace(s.Kind) ? "PLAYLIST" : s.Kind,
                        Files = validFiles,
                        Durations = durations,
                        InPoints = s.InPoints ?? new List<double>(),
                        OutPoints = s.OutPoints ?? new List<double>(),
                        Descriptions = s.Descriptions ?? new List<string>(),
                        AudioLanguages = s.AudioLanguages ?? new List<string>(),
                        Fired = s.When < DateTime.Now
                    });
                }
                else
                {
                    if (!File.Exists(s.FilePath)) continue;
                    _schedule.Add(new ScheduleRow
                    {
                        When = s.When,
                        EndWhen = s.EndWhen,
                        EndMode = string.IsNullOrWhiteSpace(s.EndMode)
                            ? (s.EndWhen.HasValue ? "MANUAL END" : "CONTINUE") : s.EndMode,
                        StartMode = string.IsNullOrWhiteSpace(s.StartMode) ? "EXACT" : s.StartMode,
                        ExactEnd = s.ExactEnd,
                        FilePath = s.FilePath,
                        Kind = "MEDIA",
                        InPoints = s.InPoints ?? new List<double>(),
                        OutPoints = s.OutPoints ?? new List<double>(),
                        Descriptions = s.Descriptions ?? new List<string>(),
                        Fired = s.When < DateTime.Now
                    });
                }
            }

            SortScheduleRows();
            StatusText.Text = $"SCHEDULE LOADED • {_schedule.Count} JOBS";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not load schedule.\n" + ex.Message,
                "SMART PLAYOUT", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleList.SelectedItem is ScheduleRow s) _schedule.Remove(s);
    }

    private void ClearSchedule_Click(object sender, RoutedEventArgs e) => _schedule.Clear();

    private void StopScheduledWindow(string status)
    {
        _program.Stop();
        _programAudio.Stop();
        ProgramImage.Source = null;
        ProgramNameText.Text = "OFF AIR";

        _scheduledPlaylistActive = false;
        _activeScheduledJob = null;
        _activeScheduleEnd = null;
        _scheduledRunQueue.Clear();
        _nextFile = null;
        NextText.Text = "—";
        StatusText.Text = status;
        _programFile = null;
        Dispatcher.BeginInvoke(async () => await TryPlayFillerAsync());
    }

    private async void CheckScheduler()
    {
        if (!_onAirEnabled) return;
        if (_schedulerBusy) return;

        if (_activeScheduleEnd.HasValue && DateTime.Now >= _activeScheduleEnd.Value)
            StopScheduledWindow("EXACT SCHEDULE END");

        var due = _schedule.FirstOrDefault(s =>
            !s.Fired &&
            !s.Pending &&
            s.When <= DateTime.Now &&
            (!s.EndWhen.HasValue || DateTime.Now < s.EndWhen.Value) &&
            File.Exists(s.FilePath));

        if (due == null) return;

        // TIME mode does not interrupt the item already on air.
        // It becomes pending and starts from HandleProgramEndedAsync.
        if (string.Equals(due.StartMode, "TIME", StringComparison.OrdinalIgnoreCase) && IsProgramOnAir())
        {
            due.Pending = true;
            StatusText.Text = $"TIME SCHEDULE WAITING • {due.Name}";
            ScheduleList.Items.Refresh();
            return;
        }

        _schedulerBusy = true;
        due.Fired = true;
        ScheduleList.Items.Refresh();
        try
        {
            await StartScheduleJobAsync(due);
        }
        finally
        {
            _schedulerBusy = false;
        }
    }

    private async void PlaylistList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Row r)
        {
            StatusText.Text = "SELECTED — " + r.Name;
            if (InstantEditorBorder.Visibility == Visibility.Visible && ReferenceEquals(_instantPreviewRow, r))
                LoadInstantEditorFields(r);
        }
        await Task.CompletedTask;
    }

    private async Task StartInstantPreviewAsync(Row r)
    {
        if (!File.Exists(r.FilePath)) return;
        ShowInstantEditor();
        _instantPreviewRow = r;
        LoadInstantEditorFields(r);

        try
        {
            _preview.Stop();
            _previewAudio.Stop();
            _previewPauseState = false;

            _previewFile = r.FilePath;
            try
            {
                var precisionProbe = await Task.Run(() => MediaCompatibilityProbe.Probe(r.FilePath));
                _instantPreviewFps = precisionProbe.Fps > 1 ? precisionProbe.Fps : 25.0;
            }
            catch { _instantPreviewFps = 25.0; }
            PreviewNameText.Text = r.Name;
            StatusText.Text = $"INSTANT PREVIEW • {r.AudioLanguage} • {(_previewMuted ? "MUTED" : "AUDIO ON")}";

            await _preview.OpenAsync(r.FilePath);
            _preview.SetRenderTarget(
                (int)Math.Max(2, PreviewImage.ActualWidth),
                (int)Math.Max(2, PreviewImage.ActualHeight));

            // Dedicated Instant Preview audio instance.
            // It uses the row's selected language, fixed preview level, and starts MUTED by default.
            try
            {
                int audioStream = await Task.Run(() =>
                    AudioLanguageHelper.ResolveStreamIndex(r.FilePath, r.AudioLanguage));

                await _previewAudio.OpenAndPlayAsync(
                    r.FilePath,
                    Math.Max(0, r.InPointSeconds),
                    startPaused: true,
                    preferredStreamIndex: audioStream,
                    requireExactStream: audioStream >= 0);

                _previewAudio.SetVolume(PreviewFixedVolume);
                _previewAudio.SetMuted(_previewMuted);
            }
            catch
            {
                // Video-only files remain valid preview items.
            }

            await _preview.PlayAsync();
            if (r.InPointSeconds > 0.01)
            {
                try { await _preview.SeekAsync(r.InPointSeconds); } catch { }
            }
            try { await _preview.FirstFrameReadyTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (_previewAudio.IsOpen) _previewAudio.Resume();
        }
        catch (Exception ex)
        {
            StatusText.Text = "INSTANT PREVIEW ERROR";
            System.Windows.MessageBox.Show(ex.Message, "SMART PLAYOUT");
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Row r)
            await StartInstantPreviewAsync(r);
    }

    private async void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Row selected &&
            !string.Equals(_previewFile, selected.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await StartInstantPreviewAsync(selected);
            return;
        }

        if (string.IsNullOrWhiteSpace(_previewFile))
        {
            if (PlaylistList.SelectedItem is Row row)
                await StartInstantPreviewAsync(row);
            return;
        }

        // If Stop was used, reopen the selected/current row so video + audio restart together.
        if (!_previewAudio.IsOpen && PlaylistList.SelectedItem is Row restart)
        {
            await StartInstantPreviewAsync(restart);
            return;
        }

        if (_instantWorkingOut > _instantWorkingIn + 0.001 &&
            CurrentInstantPreviewPosition >= _instantWorkingOut - 0.02)
            await SeekInstantPreviewAsync(_instantWorkingIn);

        _previewPauseState = false;
        await _preview.PlayAsync();
        if (_previewAudio.IsOpen) _previewAudio.Resume();
        StatusText.Text = $"INSTANT PREVIEW • {(_previewMuted ? "MUTED" : "AUDIO ON")}";
    }

    private void PreviewPause_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewFile)) return;

        _preview.TogglePause();
        _previewPauseState = !_previewPauseState;

        if (_previewAudio.IsOpen)
        {
            if (_previewPauseState) _previewAudio.Pause();
            else _previewAudio.Resume();
        }

        StatusText.Text = _previewPauseState ? "INSTANT PREVIEW • PAUSED" : "INSTANT PREVIEW • PLAYING";
    }

    private void PreviewStop_Click(object sender, RoutedEventArgs e)
    {
        _preview.Stop();
        _previewAudio.Stop();
        _previewPauseState = false;
        PreviewImage.Source = null;
        InstantPreviewEmptyText.Visibility = Visibility.Visible;
        InstantPreviewEmptyText.Text = "Preview stopped";
        HideInstantEditor();
        StatusText.Text = "INSTANT PREVIEW • STOPPED";
    }

    private void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        // PREVIEW ONLY. NativeAudioPlayer applies software mute per instance,
        // so this can never change Program / On-Air audio.
        _previewMuted = !_previewMuted;

        if (_previewAudio.IsOpen)
            _previewAudio.SetMuted(_previewMuted);

        PreviewMuteButton.Content = _previewMuted ? "\uE74F" : "\uE767";
        PreviewMuteButton.ToolTip = _previewMuted ? "Unmute Instant Preview" : "Mute Instant Preview";
        StatusText.Text = _previewMuted ? "INSTANT PREVIEW • MUTED" : "INSTANT PREVIEW • AUDIO ON";
    }


    private void ShowInstantEditor()
    {
        InstantEditorBorder.Visibility = Visibility.Visible;
        InstantEditorRow.Height = new GridLength(330);
    }

    private void HideInstantEditor()
    {
        InstantEditorBorder.Visibility = Visibility.Collapsed;
        InstantEditorRow.Height = new GridLength(0);
    }

    private void LoadInstantEditorFields(Row row)
    {
        _loadingInstantEditor = true;
        try
        {
            double source = Math.Max(row.SourceDurationSeconds, row.DurationSeconds);
            if (source <= 0) source = Math.Max(row.OutPointSeconds, _preview.DurationSeconds);
            _instantWorkingIn = Math.Max(0, row.InPointSeconds);
            _instantWorkingOut = row.OutPointSeconds > 0 ? row.OutPointSeconds : source;
            InstantSeekSlider.Maximum = Math.Max(0.001, source);
            InstantSeekSlider.Value = Math.Clamp(_instantWorkingIn, 0, InstantSeekSlider.Maximum);
            InstantInText.Text = EditorClock(_instantWorkingIn);
            InstantOutText.Text = EditorClock(_instantWorkingOut);
            InstantDurationText.Text = EditorClock(Math.Max(0, _instantWorkingOut - _instantWorkingIn));
            InstantDescriptionBox.Text = string.IsNullOrWhiteSpace(row.Description) ? Path.GetFileNameWithoutExtension(row.FilePath) : row.Description;
            InstantEditorAudioCombo.ItemsSource = row.AvailableAudioLanguages;
            InstantEditorAudioCombo.SelectedItem = row.AudioLanguage;
            PreviewNameText.Text = row.Name;
            InstantPreviewEmptyText.Text = "Loading preview...";
            InstantPreviewEmptyText.Visibility = PreviewImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loadingInstantEditor = false; }
    }

    private void UpdateInstantPreviewEditorUi()
    {
        if (InstantEditorBorder.Visibility != Visibility.Visible) return;
        double pos = _previewAudio.IsOpen ? _previewAudio.PlaybackPositionSeconds : _preview.PositionSeconds;
        double dur = Math.Max(_preview.DurationSeconds, _instantPreviewRow?.SourceDurationSeconds ?? 0);
        if (!_instantSeekDragging)
        {
            InstantSeekSlider.Maximum = Math.Max(0.001, dur);
            InstantSeekSlider.Value = Math.Clamp(pos, 0, InstantSeekSlider.Maximum);
        }
        InstantCurrentText.Text = EditorClock(pos);
        InstantTotalText.Text = EditorClock(dur);
        InstantPlayheadText.Text = $"PLAYHEAD {EditorClock(pos)}";

        // Editor seek remains full-source, but preview playback respects the saved/working IN-OUT range.
        if (!_instantSeekDragging && !_instantSeekSyncInProgress && !_previewPauseState &&
            _instantWorkingOut > _instantWorkingIn + 0.001 && pos >= _instantWorkingOut - 0.02)
        {
            _preview.TogglePause();
            _previewPauseState = true;
            if (_previewAudio.IsOpen) _previewAudio.Pause();
            StatusText.Text = $"INSTANT PREVIEW • OUT REACHED {EditorClock(_instantWorkingOut)}";
        }
    }

    private async Task SeekInstantPreviewAsync(double seconds)
    {
        if (string.IsNullOrWhiteSpace(_previewFile)) return;
        double duration = Math.Max(_preview.DurationSeconds, _instantPreviewRow?.SourceDurationSeconds ?? 0);
        double target = duration > 0 ? Math.Clamp(seconds, 0, duration) : Math.Max(0, seconds);
        bool wasPaused = _previewPauseState;

        _instantSeekSyncInProgress = true;
        try
        {
            // Hold audio before MPEG keyframe seek. While the video decoder prerolls to the
            // requested clean frame, temporarily remove the audio master clock so the video
            // cannot race/drop frames trying to catch an already-relocated audio clock.
            if (_previewAudio.IsOpen)
            {
                _previewAudio.Pause();
                _previewAudio.Seek(target);
            }

            await _preview.SeekAsync(target);

            // MPEG/PS/VOB can lock the first clean frame slightly after the nominal target.
            // Anchor audio to that actual decoded video position before transport is released.
            if (_previewAudio.IsOpen)
            {
                double cleanVideoAnchor = Math.Max(0, _preview.PositionSeconds);
                if (Math.Abs(cleanVideoAnchor - target) > 0.001)
                    _previewAudio.Seek(cleanVideoAnchor);
                if (wasPaused) _previewAudio.Pause(); else _previewAudio.Resume();
            }
        }
        catch (TaskCanceledException) { }
        catch { }
        finally
        {
            _instantSeekSyncInProgress = false;
        }
    }

    private void InstantSeek_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _instantSeekDragging = true;

    private async void InstantSeek_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        await SeekInstantPreviewAsync(InstantSeekSlider.Value);
        _instantSeekDragging = false;
    }

    private void InstantSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_instantSeekDragging) return;
        InstantCurrentText.Text = EditorClock(e.NewValue);
        InstantPlayheadText.Text = $"PLAYHEAD {EditorClock(e.NewValue)}";
    }

    private double CurrentInstantPreviewPosition => _previewAudio.IsOpen ? Math.Max(0, _previewAudio.PlaybackPositionSeconds) : Math.Max(0, _preview.PositionSeconds);

    private async void InstantFrameBack_Click(object sender, RoutedEventArgs e)
    {
        double step = 1.0 / Math.Max(1.0, _instantPreviewFps);
        await SeekInstantPreviewAsync(Math.Max(0, CurrentInstantPreviewPosition - step));
        StatusText.Text = $"FRAME -1 • {_instantPreviewFps:0.###} FPS";
    }

    private async void InstantFrameForward_Click(object sender, RoutedEventArgs e)
    {
        double step = 1.0 / Math.Max(1.0, _instantPreviewFps);
        await SeekInstantPreviewAsync(CurrentInstantPreviewPosition + step);
        StatusText.Text = $"FRAME +1 • {_instantPreviewFps:0.###} FPS";
    }

    private void InstantSetIn_Click(object sender, RoutedEventArgs e)
    {
        if (_instantPreviewRow is not Row row) return;
        double source = Math.Max(row.SourceDurationSeconds, _preview.DurationSeconds);
        _instantWorkingIn = Math.Clamp(CurrentInstantPreviewPosition, 0, source > 0 ? source : double.MaxValue);
        if (_instantWorkingOut <= _instantWorkingIn) _instantWorkingOut = Math.Max(_instantWorkingIn, source);
        InstantInText.Text = EditorClock(_instantWorkingIn);
        InstantDurationText.Text = EditorClock(Math.Max(0, _instantWorkingOut - _instantWorkingIn));
        StatusText.Text = $"IN MARK • {InstantInText.Text}";
    }

    private void InstantSetOut_Click(object sender, RoutedEventArgs e)
    {
        if (_instantPreviewRow is not Row row) return;
        double source = Math.Max(row.SourceDurationSeconds, _preview.DurationSeconds);
        _instantWorkingOut = Math.Clamp(CurrentInstantPreviewPosition, 0, source > 0 ? source : double.MaxValue);
        if (_instantWorkingOut < _instantWorkingIn) _instantWorkingOut = _instantWorkingIn;
        InstantOutText.Text = EditorClock(_instantWorkingOut);
        InstantDurationText.Text = EditorClock(Math.Max(0, _instantWorkingOut - _instantWorkingIn));
        StatusText.Text = $"OUT MARK • {InstantOutText.Text}";
    }

    private void InstantApplyInOut_Click(object sender, RoutedEventArgs e)
    {
        if (_instantPreviewRow is not Row row) return;
        double source = Math.Max(row.SourceDurationSeconds, _preview.DurationSeconds);
        double input = Math.Clamp(_instantWorkingIn, 0, source > 0 ? source : _instantWorkingIn);
        double output = _instantWorkingOut > 0 ? _instantWorkingOut : source;
        if (source > 0) output = Math.Clamp(output, input, source);
        if (output <= input)
        {
            System.Windows.MessageBox.Show("OUT point must be after IN point.", "SMART PLAYOUT");
            return;
        }
        row.SourceDurationSeconds = source;
        row.DurationSeconds = source;
        row.InPointSeconds = input;
        row.OutPointSeconds = output;
        row.Description = InstantDescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(row.Description)) row.Description = Path.GetFileNameWithoutExtension(row.FilePath);
        RefreshRows();
        SaveAutoPlaylistState();
        LoadInstantEditorFields(row);
        StatusText.Text = $"IN/OUT SET • {EditorClock(row.PlayDurationSeconds)} • SAVED";
    }

    private void InstantResetInOut_Click(object sender, RoutedEventArgs e)
    {
        if (_instantPreviewRow is not Row row) return;
        double source = Math.Max(row.SourceDurationSeconds, Math.Max(row.DurationSeconds, _preview.DurationSeconds));
        _instantWorkingIn = 0;
        _instantWorkingOut = source;
        InstantInText.Text = EditorClock(0);
        InstantOutText.Text = EditorClock(source);
        InstantDurationText.Text = EditorClock(source);
        StatusText.Text = "IN/OUT RESET READY • PRESS SET TO SAVE";
    }

    private void InstantSplit_Click(object sender, RoutedEventArgs e)
    {
        if (_instantPreviewRow is not Row row) return;
        int index = _items.IndexOf(row);
        if (index < 0) return;
        double source = Math.Max(row.SourceDurationSeconds, Math.Max(row.DurationSeconds, _preview.DurationSeconds));
        double originalIn = Math.Max(0, row.InPointSeconds);
        double originalOut = row.OutPointSeconds > 0 ? row.OutPointSeconds : source;
        double split = CurrentInstantPreviewPosition;
        if (split <= originalIn + 0.02 || split >= originalOut - 0.02)
        {
            System.Windows.MessageBox.Show("Move the playhead inside the current IN/OUT range before Split.", "SMART PLAYOUT");
            return;
        }
        string baseDescription = string.IsNullOrWhiteSpace(row.Description) ? Path.GetFileNameWithoutExtension(row.FilePath) : row.Description;
        Row Make(double i, double o, string suffix)
        {
            var r = new Row { FilePath = row.FilePath, ProgramName = row.ProgramName, DurationSeconds = source, SourceDurationSeconds = source, InPointSeconds = i, OutPointSeconds = o, Description = baseDescription + suffix, AudioLanguage = row.AudioLanguage };
            r.AvailableAudioLanguages.Clear();
            foreach (var lang in row.AvailableAudioLanguages) r.AvailableAudioLanguages.Add(lang);
            return r;
        }
        var first = Make(originalIn, split, " • PART 1");
        var second = Make(split, originalOut, " • PART 2");
        _items.RemoveAt(index);
        _items.Insert(index, first);
        _items.Insert(index + 1, second);
        RefreshRows();
        PlaylistList.SelectedIndex = index + 1;
        _instantPreviewRow = second;
        LoadInstantEditorFields(second);
        SaveAutoPlaylistState();
        StatusText.Text = $"SPLIT • {EditorClock(split)} • NON-DESTRUCTIVE • SAVED";
    }

    private void InstantDescriptionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingInstantEditor || _instantPreviewRow is not Row row) return;
        row.Description = InstantDescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(row.Description)) row.Description = Path.GetFileNameWithoutExtension(row.FilePath);
        RefreshRows();
        SaveAutoPlaylistState();
    }

    private void InstantEditorAudio_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingInstantEditor || _instantPreviewRow is not Row row || InstantEditorAudioCombo.SelectedItem is not string language) return;
        row.AudioLanguage = language;
        PlaylistList.Items.Refresh();
        SaveAutoPlaylistState();
    }

    private void InstantEditorClose_Click(object sender, RoutedEventArgs e) => PreviewStop_Click(sender, e);

    private async void BreakNow_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Break / Commercial Media",
            Filter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.mpg;*.mpeg;*.m2v;*.ts;*.m2ts;*.vob;*.dat;*.wmv;*.webm;*.mp3;*.wav;*.aac;*.m4a|All files (*.*)|*.*"
        };
        if (d.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(_programFile) || !File.Exists(_programFile))
        {
            await TakeToAirAtAsync(d.FileName, 0, AudioLanguageHelper.DefaultLanguage, null);
            return;
        }

        _breakResumeFile = _programFile;
        _breakResumePosition = Math.Max(0, _program.PositionSeconds);
        _breakResumeAudio = _programAudioLanguage;
        _breakResumeEnd = _programClipEndSeconds;
        _breakActive = true;
        _dashboardListKey = "";
        await TakeToAirAtAsync(d.FileName, 0, AudioLanguageHelper.DefaultLanguage, null);
        StatusText.Text = "BREAK ON AIR • WILL RESUME / RE-SYNC AFTER BREAK";
    }

    private async void TakeToAir_Click(object sender, RoutedEventArgs e)
    {
        var selected = PlaylistList.SelectedItem as Row;
        var file = _previewFile ?? selected?.FilePath;
        if (file == null) return;
        var row = selected ?? _items.FirstOrDefault(r => string.Equals(r.FilePath, file, StringComparison.OrdinalIgnoreCase));
        if (row != null)
            await TakeToAirAtAsync(file, row.InPointSeconds, row.AudioLanguage, row.OutPointSeconds > row.InPointSeconds ? row.OutPointSeconds : null);
        else
            await TakeToAirAsync(file, AudioLanguageHelper.DefaultLanguage);
    }

    private Task TakeToAirAsync(string file, string audioLanguage = AudioLanguageHelper.DefaultLanguage) => TakeToAirAtAsync(file, 0, audioLanguage, null);

    private void DisposeProgramPreload()
    {
        NativeVideoPlayer? video;
        NativeAudioPlayer? audio;
        lock (_programPreloadLock)
        {
            unchecked { _programPreloadGeneration++; }
            video = _preloadedProgramVideo;
            audio = _preloadedProgramAudio;
            _preloadedProgramVideo = null;
            _preloadedProgramAudio = null;
            _preloadedProgramFile = null;
        }
        try { video?.Stop(); video?.Dispose(); } catch { }
        try { audio?.Stop(); audio?.Dispose(); } catch { }
    }

    private void ArmProgramPreload(string? file, double startSeconds, string audioLanguage, double? endSeconds)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            DisposeProgramPreload();
            return;
        }

        string language = string.IsNullOrWhiteSpace(audioLanguage) ? AudioLanguageHelper.DefaultLanguage : audioLanguage;
        startSeconds = Math.Max(0, startSeconds);
        endSeconds = endSeconds.HasValue && endSeconds.Value > startSeconds ? endSeconds : null;
        int generation;
        lock (_programPreloadLock)
        {
            if (string.Equals(_preloadedProgramFile, file, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_preloadedProgramLanguage, language, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(_preloadedProgramStart - startSeconds) < 0.01 && _preloadedProgramEnd == endSeconds)
                return;
            generation = unchecked(++_programPreloadGeneration);
        }
        _ = PrepareProgramPreloadAsync(file, startSeconds, language, endSeconds, generation);
    }

    private async Task PrepareProgramPreloadAsync(string file, double startSeconds, string language, double? endSeconds, int generation)
    {
        var video = new NativeVideoPlayer();
        var audio = new NativeAudioPlayer();
        AttachProgramPipeline(video, audio);
        CopyProgramProcessingSettings(video, audio);
        UpdateProgramBroadcastRenderTarget(video);
        try
        {
            await video.OpenAsync(file);
            try
            {
                int audioStream = await Task.Run(() => AudioLanguageHelper.ResolveStreamIndex(file, language));
                await audio.OpenAndPlayAsync(file, startSeconds, startPaused: true, preferredStreamIndex: audioStream, requireExactStream: audioStream >= 0);
                audio.SetVolume(_masterVolume);
                audio.SetMuted(_masterMuted);
            }
            catch (Exception ex) { OnAirDiag($"PRELOAD_AUDIO_FAIL file={Path.GetFileName(file)} error={ex.GetType().Name}: {ex.Message}"); }

            NativeVideoPlayer? staleVideo = null;
            NativeAudioPlayer? staleAudio = null;
            lock (_programPreloadLock)
            {
                if (generation != _programPreloadGeneration)
                    staleVideo = video;
                else
                {
                    staleVideo = _preloadedProgramVideo;
                    staleAudio = _preloadedProgramAudio;
                    _preloadedProgramVideo = video;
                    _preloadedProgramAudio = audio;
                    _preloadedProgramFile = file;
                    _preloadedProgramLanguage = language;
                    _preloadedProgramStart = startSeconds;
                    _preloadedProgramEnd = endSeconds;
                    video = null!;
                    audio = null!;
                    OnAirDiag($"B_SLOT_READY file={Path.GetFileName(file)} backend={_preloadedProgramVideo.ActiveDecodeBackend}");
                }
            }
            try { staleVideo?.Stop(); staleVideo?.Dispose(); } catch { }
            try { staleAudio?.Stop(); staleAudio?.Dispose(); } catch { }
        }
        catch (Exception ex) { OnAirDiag($"PRELOAD_FAIL file={Path.GetFileName(file)} error={ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            try { video?.Stop(); video?.Dispose(); } catch { }
            try { audio?.Stop(); audio?.Dispose(); } catch { }
        }
    }

    private async Task TakeToAirAtAsync(string file, double startSeconds, string audioLanguage = AudioLanguageHelper.DefaultLanguage, double? endSeconds = null)
    {
        if (!_onAirEnabled) return;
        if(Interlocked.Exchange(ref _programTakeInProgress,1)!=0)
        {
            OnAirDiag($"TAKE_IGNORED already_in_progress file={Path.GetFileName(file)}");
            return;
        }
        if (_liveInputActive) StopLiveInput();
        _operatorStopped = false;
        ProgramStateOverlay.Visibility = Visibility.Collapsed;
        startSeconds = Math.Max(0, startSeconds);
        Interlocked.Exchange(ref _onAirDecodedFrameCount, 0);
        Interlocked.Exchange(ref _onAirPublishedFrameCount, 0);
        OnAirDiag($"TAKE_BEGIN file={Path.GetFileName(file)} start={startSeconds:0.###}");

        var oldVideo = _program;
        var oldAudio = _programAudio;
        NativeVideoPlayer? nextVideo = null;
        NativeAudioPlayer? nextAudio = null;
        bool consumedPreload = false;
        string requestedLanguage = string.IsNullOrWhiteSpace(audioLanguage) ? AudioLanguageHelper.DefaultLanguage : audioLanguage;
        double? requestedEnd = endSeconds.HasValue && endSeconds.Value > startSeconds ? endSeconds.Value : null;
        lock (_programPreloadLock)
        {
            if (string.Equals(_preloadedProgramFile, file, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_preloadedProgramLanguage, requestedLanguage, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(_preloadedProgramStart - startSeconds) < 0.01 && _preloadedProgramEnd == requestedEnd)
            {
                nextVideo = _preloadedProgramVideo;
                nextAudio = _preloadedProgramAudio;
                _preloadedProgramVideo = null;
                _preloadedProgramAudio = null;
                _preloadedProgramFile = null;
                unchecked { _programPreloadGeneration++; }
                consumedPreload = true;
                OnAirDiag($"B_SLOT_CONSUMED file={Path.GetFileName(file)}");
            }
        }
        nextVideo ??= new NativeVideoPlayer();
        nextAudio ??= new NativeAudioPlayer();
        if (!consumedPreload)
        {
            AttachProgramPipeline(nextVideo, nextAudio);
            CopyProgramProcessingSettings(nextVideo, nextAudio);
            UpdateProgramBroadcastRenderTarget(nextVideo);
        }

        BitmapSource? stagedFrame = null;
        var stagedReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void CaptureNext(BitmapSource f) { stagedFrame = f; stagedReady.TrySetResult(true); }
        nextVideo.FrameReady += CaptureNext;

        string nextLanguage = requestedLanguage;
        double? nextClipEnd = requestedEnd;

        try
        {
            if (!consumedPreload)
            {
                await nextVideo.OpenAsync(file);
            }
            if (!nextAudio.IsOpen)
            {
                try
                {
                    int audioStream = await Task.Run(() => AudioLanguageHelper.ResolveStreamIndex(file, nextLanguage));
                    await nextAudio.OpenAndPlayAsync(file, startSeconds, startPaused: true, preferredStreamIndex: audioStream, requireExactStream: audioStream >= 0);
                    nextAudio.SetVolume(_masterVolume);
                    nextAudio.SetMuted(_masterMuted);
                }
                catch (Exception ex) { OnAirDiag($"AUDIO_OPEN_FAIL file={Path.GetFileName(file)} error={ex.GetType().Name}: {ex.Message}"); }
            }

            await nextVideo.PlayAsync();
            if (startSeconds > 0.05)
            {
                try { await nextVideo.SeekAsync(startSeconds); } catch { }
            }

            await Task.WhenAny(stagedReady.Task, nextVideo.FirstFrameReadyTask, Task.Delay(3000));
            if (stagedFrame == null)
            {
                await Task.WhenAny(stagedReady.Task, Task.Delay(1500));
                if (stagedFrame == null)
                {
                    OnAirDiag($"TAKE_ABORT no first video frame backend={nextVideo.ActiveDecodeBackend}");
                    throw new InvalidOperationException("NEXT media produced no video frame; CURRENT Program was kept on air.");
                }
            }
            OnAirDiag($"STAGED_FIRST_FRAME_OK backend={nextVideo.ActiveDecodeBackend} size={stagedFrame.PixelWidth}x{stagedFrame.PixelHeight}");

            // v0.6.8.50.11 ON-AIR TRANSPORT FIX:
            // Do NOT pause the newly decoded video after first-frame staging. The Instant/Program
            // preview paths stay stable because they keep one continuous decoder transport. The
            // old ON-AIR path toggled pause here and toggled again immediately after the player
            // swap; under timing pressure that could leave the native decode worker paused while
            // the separately prepared audio resumed, producing audio-only / frozen Program monitor.
            // Keep nextVideo running continuously; it is still invisible until _program is swapped.

            _masterAvBus.BeginProgramTransition();
            _deckLinkBusSession?.BeginProgramTransition();
            _masterAvBus.ResetTransitionAudio();
            _deckLinkBusSession?.ResetTransitionAudio();

            _program = nextVideo;
            _programAudio = nextAudio;
            _programPaused = false;
            Volatile.Write(ref _programAudioMediaSeconds, Math.Max(0, startSeconds));
            _programFile = file;
            _programAudioLanguage = nextLanguage;
            _programClipStartSeconds = Math.Max(0, startSeconds);
            _programClipEndSeconds = nextClipEnd;
            _dashboardMediaFile = null;
            _fillerActive = _fillerFiles.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));

            NowText.Text = Path.GetFileName(file);
            ProgramNameText.Text = Path.GetFileName(file);
            StatusText.Text = "ON AIR • SEAMLESS TAKE";
            OnAirDiag($"PROGRAM_SWAP_OK backend={nextVideo.ActiveDecodeBackend}");

            PublishProgramFrame(stagedFrame);
            // Release audio against the already-running video transport. MasterClockSeconds becomes
            // active only after the _program/_programAudio swap above, so normal A/V drift correction
            // now takes over without a second video pause/resume state change.
            if (nextAudio.IsOpen) nextAudio.Resume();

            _masterAvBus.EndProgramTransition();
            _deckLinkBusSession?.EndProgramTransition();

            try { oldVideo.Stop(); } catch { }
            try { oldAudio.Stop(); } catch { }
            try { oldVideo.Dispose(); } catch { }
            try { oldAudio.Dispose(); } catch { }

            if (_scheduledPlaylistActive)
                UpdateScheduledNext();
            else if (_fillerActive)
            {
                _nextFile = null;
                NextText.Text = _fillerFiles.Count > 0 ? Path.GetFileName(_fillerFiles[_fillerIndex % _fillerFiles.Count]) : "—";
            }
            else
                UpdateNextFromPlaylist(file);

            _ = RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped: true);
        }
        catch
        {
            try { nextVideo.Stop(); nextVideo.Dispose(); } catch { }
            try { nextAudio.Stop(); nextAudio.Dispose(); } catch { }
            _masterAvBus.EndProgramTransition();
            _deckLinkBusSession?.EndProgramTransition();
            throw;
        }
        finally
        {
            nextVideo.FrameReady -= CaptureNext;
            Volatile.Write(ref _programTakeInProgress,0);
        }
    }

    private void UpdateNextFromPlaylist(string current)
    {
        if (!string.IsNullOrWhiteSpace(_nextFile) && File.Exists(_nextFile))
        {
            NextText.Text = Path.GetFileName(_nextFile);
            return;
        }

        int i = _items.ToList().FindIndex(x => string.Equals(x.FilePath, current, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < _items.Count)
        {
            _nextFile = _items[i + 1].FilePath;
            _nextAudioLanguage = _items[i + 1].AudioLanguage;
            _nextStartSeconds = 0;
            _nextEndSeconds = null;
        }
        else { _nextFile = null; _nextAudioLanguage = AudioLanguageHelper.DefaultLanguage; _nextStartSeconds = 0; _nextEndSeconds = null; }
        NextText.Text = _nextFile == null ? "—" : Path.GetFileName(_nextFile);
        ArmProgramPreload(_nextFile, _nextStartSeconds, _nextAudioLanguage, _nextEndSeconds);
    }

    private async Task PlayNextAsync()
    {
        if (!string.IsNullOrWhiteSpace(_nextFile) && File.Exists(_nextFile))
        {
            var next = _nextFile;
            _nextFile = null;
            var start = _nextStartSeconds;
            var end = _nextEndSeconds;
            _nextStartSeconds = 0;
            _nextEndSeconds = null;
            await TakeToAirAtAsync(next, start, _nextAudioLanguage, end);
        }
        else if (!await TryPlayFillerAsync())
        {
            StatusText.Text = "PROGRAM ENDED • NO FILLER";
            _fillerActive = false;
        }
    }

    private async void ReturnToSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerBusy) return;

        var now = DateTime.Now;

        // Prefer a schedule whose time window currently contains NOW.
        var current = _schedule
            .Where(s => s.When <= now &&
                        (!s.EndWhen.HasValue || now < s.EndWhen.Value) &&
                        File.Exists(s.FilePath))
            .OrderByDescending(s => s.When)
            .FirstOrDefault();

        // If nothing is active right now, arm the next future schedule and leave output stopped.
        if (current == null)
        {
            var next = _schedule
                .Where(s => !s.Fired && s.When > now && File.Exists(s.FilePath))
                .OrderBy(s => s.When)
                .FirstOrDefault();

            _program.Stop();
            _programAudio.Stop();
            ProgramImage.Source = null;
            ProgramNameText.Text = "OFF AIR";
            _programFile = null;
            _scheduledPlaylistActive = false;
            _activeScheduledJob = null;
            _activeScheduleEnd = null;
            _scheduledRunQueue.Clear();

            StatusText.Text = next == null
                ? "NO CURRENT / FUTURE SCHEDULE"
                : $"SCHEDULE ARMED • {next.TimeText} • {next.Name}";
            return;
        }

        _schedulerBusy = true;
        try
        {
            // Operator explicitly returns control to automation.
            // Re-arm the selected schedule even if it fired before.
            current.Fired = true;
            current.Pending = false;

            if (string.Equals(current.Kind, "PLAYLIST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(current.Kind, "PROGRAM", StringComparison.OrdinalIgnoreCase))
            {
                _activeScheduledJob = current;
                _scheduledPlaylistActive = true;
                _activeScheduleEnd = current.ExactEnd ? current.EndWhen : null;

                // Existing absolute wall-clock timeline routine calculates
                // which playlist item and in-item offset must be on air NOW.
                await PlayScheduledTimelinePositionAsync(current);
            }
            else
            {
                _activeScheduledJob = null;
                _scheduledPlaylistActive = false;
                _activeScheduleEnd = current.ExactEnd ? current.EndWhen : null;
                await TakeToAirAsync(current.FilePath, current.AudioLanguage);
                StatusText.Text = "RETURNED TO CURRENT SCHEDULE";
            }

            ScheduleList.Items.Refresh();
        }
        finally
        {
            _schedulerBusy = false;
        }
    }

    private async void ProgramPlay_Click(object sender, RoutedEventArgs e)
    {
        if (!_onAirEnabled) return;
        _operatorStopped = false;
        ProgramStateOverlay.Visibility = Visibility.Collapsed;
        if (_fillerResumePending && !_scheduledPlaylistActive && _activeScheduledJob == null)
        {
            await TryPlayFillerAsync();
            return;
        }
        if (_scheduledPlaylistActive && _activeScheduledJob != null)
        {
            await PlayScheduledTimelinePositionAsync(_activeScheduledJob);
            return;
        }

        if (_programFile == null) return;

        // STOP closes the audio decoder/output completely. On a later PLAY we must
        // reopen audio instead of only calling Resume(), otherwise video restarts
        // correctly but audio stays silent.
        bool audioWasClosed = !_programAudio.IsOpen;
        if (audioWasClosed)
        {
            double startSeconds = Math.Max(0, _program.PositionSeconds);
            try
            {
                int audioStream = await Task.Run(() => AudioLanguageHelper.ResolveStreamIndex(_programFile, _programAudioLanguage));
                await _programAudio.OpenAndPlayAsync(_programFile, startSeconds, startPaused: true, preferredStreamIndex: audioStream, requireExactStream: audioStream >= 0);
                _programAudio.SetVolume(_masterVolume);
                _programAudio.SetMuted(_masterMuted);
            }
            catch (Exception ex)
            {
                OnAirDiag($"AUDIO_REOPEN_FAIL file={Path.GetFileName(_programFile)} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        await _program.PlayAsync();

        if (audioWasClosed)
        {
            try { await Task.WhenAny(_program.FirstFrameReadyTask, Task.Delay(2000)); } catch { }
        }

        if (_programAudio.IsOpen)
            _programAudio.Resume();

        _programPaused = false;
        StatusText.Text = "ON AIR";
        _ = RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped: true);
    }

    private void ProgramPause_Click(object sender, RoutedEventArgs e)
    {
        if (_programFile == null) return;

        _program.TogglePause();
        _programPaused = !_programPaused;

        if (_programAudio.IsOpen)
        {
            if (_programPaused) _programAudio.Pause();
            else _programAudio.Resume();
        }

        OnAirDiag($"PROGRAM_TRANSPORT {(_programPaused ? "PAUSE" : "RESUME")} audioOpen={_programAudio.IsOpen}");
        if (_programPaused)
            SuspendArmedStreams("ARMED • PROGRAM PAUSED");
        else
        {
            StatusText.Text = "ON AIR";
            _ = RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped: true);
        }
    }

    private void ProgramStop_Click(object sender, RoutedEventArgs e)
    {
        CaptureFillerResumePoint();
        _program.Stop(); _programAudio.Stop();
        _programPaused = false;
        _programClipStartSeconds = 0;
        _programClipEndSeconds = null;
        _operatorStopped = true; _fillerActive = false;
        ProgramImage.Source = null;
        StandbySlate.Visibility = Visibility.Visible;
        ProgramStateOverlay.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 7, 10));
        ProgramStateOverlay.Visibility = Visibility.Visible;
        ProgramNameText.Text = "STANDBY"; StatusText.Text = "PROGRAM STOPPED • STANDBY";
        SuspendArmedStreams("ARMED • PROGRAM STANDBY");
    }

    private async void OnAirPower_Click(object sender, RoutedEventArgs e)
    {
        _onAirEnabled = !_onAirEnabled;
        if (!_onAirEnabled)
        {
            CaptureFillerResumePoint();
            _program.Stop(); _programAudio.Stop(); _operatorStopped = false; _fillerActive = false;
            ProgramImage.Source = null;
            StandbySlate.Visibility = Visibility.Collapsed;
            ProgramStateOverlay.Background = System.Windows.Media.Brushes.Black;
            ProgramStateOverlay.Visibility = Visibility.Visible;
            OnAirLed.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 73, 91));
            OnAirLed.Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 180, 188));
            OnAirLed.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(255,73,91), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 };
            OnAirPowerButton.Style = (Style)FindResource("MainRedIcon"); OnAirPowerText.Text = "AIR OFF";
            StatusText.Text = "ON AIR OFF • BLACKOUT";
            SuspendArmedStreams("ARMED • AIR OFF / BLACKOUT");
            return;
        }

        OnAirLed.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 242, 140));
        OnAirLed.Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(183, 255, 208));
        OnAirLed.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(82,242,140), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 };
        OnAirPowerButton.Style = (Style)FindResource("MainGreenIcon"); OnAirPowerText.Text = "AIR ON";
        ProgramStateOverlay.Visibility = Visibility.Collapsed;
        StatusText.Text = "ON AIR ON";
        if (!string.IsNullOrWhiteSpace(_programFile) && File.Exists(_programFile)) ProgramPlay_Click(sender, e);
        else if (!_schedulerBusy) { CheckScheduler(); if (_programFile == null) await TryPlayFillerAsync(); }
    }

    private void ApplyVideoScaleMode(int mode, bool save = true)
    {
        _videoScaleMode = Math.Clamp(mode, 0, 3);
        ProgramImage.Width = double.NaN; ProgramImage.Height = double.NaN;
        ProgramImage.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        ProgramImage.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        string label;
        switch (_videoScaleMode)
        {
            case 1:
                ProgramImage.Stretch = Stretch.UniformToFill; label = "16:9"; break;
            case 2:
                ProgramImage.Stretch = Stretch.Uniform;
                ProgramImage.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                ProgramImage.VerticalAlignment = System.Windows.VerticalAlignment.Center;
                label = "4:3"; break;
            case 3:
                ProgramImage.Stretch = Stretch.Fill; label = "FULL STRETCH"; break;
            default:
                ProgramImage.Stretch = Stretch.Fill; label = "SMART SCALE"; break;
        }
        if (VideoScaleSettingText != null) VideoScaleSettingText.Text = label;
        if (VideoScaleCombo != null && VideoScaleCombo.SelectedIndex != _videoScaleMode) VideoScaleCombo.SelectedIndex = _videoScaleMode;

        // v0.6.8.50.9: Scale/aspect is LIVE output state, not a session-start snapshot.
        // Push the new mode immediately into every running Final Program consumer.
        string appliedScreen=ComboTextAt(OutputResolutionCombo,_appliedOutputResolutionIndex);
        var appliedFmt=StreamingEngine.ParseResolution(appliedScreen);
        int aw=appliedFmt.W>0?appliedFmt.W:Math.Max(1,_latestProgramFrame?.PixelWidth??1920);
        int ah=appliedFmt.H>0?appliedFmt.H:Math.Max(1,_latestProgramFrame?.PixelHeight??1080);
        double liveAspect=OutputDisplayAspect(appliedScreen,_videoScaleMode,aw,ah);
        _masterAvBus.UpdateScaleMode(_videoScaleMode,liveAspect);
        _deckLinkBusSession?.UpdateScaleMode(_videoScaleMode,liveAspect);
        _deckLinkNativeOutput.UpdateScaleMode(_videoScaleMode,liveAspect);

        StatusText.Text = $"VIDEO OUTPUT ADJUSTMENT • {label} • LIVE TO ALL OUTPUTS";
        if (save && !_loadingOperatorSettings) SaveOperatorSettings();
    }

    private void VideoScaleCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingOperatorSettings || VideoScaleCombo.SelectedIndex < 0) return;
        ApplyVideoScaleMode(VideoScaleCombo.SelectedIndex);
    }

    private void DecodeHardware_Click(object sender, RoutedEventArgs e) { DecodeModeCombo.SelectedIndex = 0; }
    private void DecodeSoftware_Click(object sender, RoutedEventArgs e) { DecodeModeCombo.SelectedIndex = 1; }
    private void EncodeHardware_Click(object sender, RoutedEventArgs e) { EncodeModeCombo.SelectedIndex = 0; }
    private void EncodeSoftware_Click(object sender, RoutedEventArgs e) { EncodeModeCombo.SelectedIndex = 1; }
    private void UpdateEngineModeButtons()
    {
        if (DecodeHardwareButton == null || DecodeSoftwareButton == null || EncodeHardwareButton == null || EncodeSoftwareButton == null) return;
        DecodeHardwareButton.Style = (Style)FindResource(_hardwareDecodeEnabled ? "MainGreenIcon" : "MainBlueIcon");
        DecodeSoftwareButton.Style = (Style)FindResource(_hardwareDecodeEnabled ? "MainBlueIcon" : "MainGreenIcon");
        EncodeHardwareButton.Style = (Style)FindResource(_hardwareEncodePreferred ? "MainAmberIcon" : "MainBlueIcon");
        EncodeSoftwareButton.Style = (Style)FindResource(_hardwareEncodePreferred ? "MainBlueIcon" : "MainAmberIcon");
    }

    private void DecodeModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingOperatorSettings || DecodeModeCombo.SelectedIndex < 0) return;
        _hardwareDecodeEnabled = DecodeModeCombo.SelectedIndex == 0;
        ApplyOperatorSettingsToPlayers();
        DecodeModeStatusText.Text = _hardwareDecodeEnabled ? "ACTIVE • GPU / HARDWARE + CPU FALLBACK" : "ACTIVE • CPU / SOFTWARE";
        UpdateEngineModeButtons();
        StatusText.Text = _hardwareDecodeEnabled ? "DECODE • AUTO HARDWARE" : "DECODE • SOFTWARE CPU";
        SaveOperatorSettings();
    }

    private void EncodeModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingOperatorSettings || EncodeModeCombo.SelectedIndex < 0) return;
        _hardwareEncodePreferred = EncodeModeCombo.SelectedIndex == 0;
        EncodeModeStatusText.Text = _hardwareEncodePreferred ? "ACTIVE • GPU / HARDWARE" : "ACTIVE • CPU / SOFTWARE";
        UpdateEngineModeButtons();
        StatusText.Text = _hardwareEncodePreferred ? "ENCODE PREFERENCE • AUTO HARDWARE" : "ENCODE PREFERENCE • SOFTWARE CPU";
        SaveOperatorSettings();
    }

    private void ApplyOperatorSettingsToPlayers()
    {
        _preview.HardwareDecodeEnabled = _hardwareDecodeEnabled;
        _program.HardwareDecodeEnabled = _hardwareDecodeEnabled;
        EmbeddedProgramManager.SetHardwareDecodeEnabled(_hardwareDecodeEnabled);
        ApplyOutputProcessingSettings();
    }

    private void ApplyOutputProcessingSettings()
    {
        if (BrightnessSlider == null) return;
        _program.OutputBrightness = BrightnessSlider.Value;
        _program.OutputContrast = ContrastSlider.Value;
        _program.OutputSaturation = SaturationSlider.Value;
        _program.OutputGamma = GammaSlider.Value;
        _program.OutputBlackLevel = BlackLevelSlider.Value;
        _program.OutputYLevel = YLevelSlider.Value;
        _program.OutputULevel = ULevelSlider.Value;
        _program.OutputVLevel = VLevelSlider.Value;
        _program.OutputSharpness = SharpnessSlider.Value;
        _program.OutputNoiseReduction = NoiseReductionSlider.Value;
        _program.OutputAutoColor = AutoColorCheck.IsChecked == true;
        _program.OutputAutoQualityEnhance = AutoQualityCheck.IsChecked == true;
        _program.OutputAutoQualityStrength = AutoQualityStrengthSlider.Value;
        _program.OutputAutoWhiteBalance = AutoWhiteBalanceCheck.IsChecked == true;
        _program.OutputWhiteTemperature = WhiteTemperatureSlider.Value;
        _program.OutputWhiteTint = WhiteTintSlider.Value;
        _program.OutputScanMode = Math.Max(0, ScanModeCombo.SelectedIndex);

        _programAudio.MasterGainDb = (float)MasterGainSlider.Value;
        _programAudio.BassDb = (float)BassSlider.Value;
        _programAudio.TrebleDb = (float)TrebleSlider.Value;
        _programAudio.Balance = (float)BalanceSlider.Value;
        _programAudio.NormalizeEnabled = AudioNormalizeCheck.IsChecked == true;
        _programAudio.LoudnessControlEnabled = AudioLoudnessCheck.IsChecked == true;
        _programAudio.EqualizerDb = new float[] { (float)Eq31.Value,(float)Eq62.Value,(float)Eq125.Value,(float)Eq250.Value,(float)Eq500.Value,(float)Eq1k.Value,(float)Eq2k.Value,(float)Eq4k.Value,(float)Eq8k.Value,(float)Eq16k.Value };

        // A preloaded B slot must receive corrections changed while A is on air.
        NativeVideoPlayer? preloadVideo; NativeAudioPlayer? preloadAudio;
        lock (_programPreloadLock) { preloadVideo = _preloadedProgramVideo; preloadAudio = _preloadedProgramAudio; }
        if (preloadVideo != null && preloadAudio != null)
            CopyProgramProcessingSettings(preloadVideo, preloadAudio);

        // This is the authoritative Final Program processing stage. The processed video
        // frames and processed PCM are what the Master A/V Bus distributes to every output.
        if (OutputProcessingSummaryText != null)
        {
            string scale = VideoScaleCombo?.SelectedItem is System.Windows.Controls.ComboBoxItem si ? (si.Content?.ToString() ?? "SMART SCALE") : "SMART SCALE";
            string norm = AudioNormalizeCheck?.IsChecked == true ? "NORMALIZE ON" : "NORMALIZE OFF";
            bool eqActive = new[]{Eq31,Eq62,Eq125,Eq250,Eq500,Eq1k,Eq2k,Eq4k,Eq8k,Eq16k}.Any(x => x != null && Math.Abs(x.Value) > 0.01);
            OutputProcessingSummaryText.Text = $"VIDEO • {scale} / COLOR PROCESSING   |   AUDIO • {norm} / 10-BAND EQ {(eqActive ? "ACTIVE" : "FLAT")}";
        }
    }

    private void UpdateVideoProcessingValueLabels()
    {
        // IMPORTANT: Slider.ValueChanged can fire while InitializeComponent is still
        // constructing the XAML tree. Every label/slider is therefore guarded so
        // startup cannot fail just because a later named control is not created yet.
        if (BrightnessValue != null && BrightnessSlider != null) BrightnessValue.Text = BrightnessSlider.Value.ToString("+0.00;-0.00;0.00");
        if (ContrastValue != null && ContrastSlider != null) ContrastValue.Text = ContrastSlider.Value.ToString("0.00");
        if (SaturationValue != null && SaturationSlider != null) SaturationValue.Text = SaturationSlider.Value.ToString("0.00");
        if (GammaValue != null && GammaSlider != null) GammaValue.Text = GammaSlider.Value.ToString("0.00");
        if (BlackLevelValue != null && BlackLevelSlider != null) BlackLevelValue.Text = BlackLevelSlider.Value.ToString("+0;-0;0");
        if (YLevelValue != null && YLevelSlider != null) YLevelValue.Text = YLevelSlider.Value.ToString("0.00");
        if (ULevelValue != null && ULevelSlider != null) ULevelValue.Text = ULevelSlider.Value.ToString("0.00");
        if (VLevelValue != null && VLevelSlider != null) VLevelValue.Text = VLevelSlider.Value.ToString("0.00");
        if (SharpnessValue != null && SharpnessSlider != null) SharpnessValue.Text = SharpnessSlider.Value.ToString("0.00");
        if (NoiseReductionValue != null && NoiseReductionSlider != null) NoiseReductionValue.Text = NoiseReductionSlider.Value.ToString("0.00");
        if (AutoQualityStrengthValue != null && AutoQualityStrengthSlider != null) AutoQualityStrengthValue.Text = $"{AutoQualityStrengthSlider.Value * 100:0}%";
        if (WhiteTemperatureValue != null && WhiteTemperatureSlider != null) WhiteTemperatureValue.Text = WhiteTemperatureSlider.Value.ToString("+0;-0;0");
        if (WhiteTintValue != null && WhiteTintSlider != null) WhiteTintValue.Text = WhiteTintSlider.Value.ToString("+0;-0;0");
    }

    private void UpdateAudioProcessingValueLabels()
    {
        // ValueChanged can fire during XAML construction, so keep this fully null-safe.
        if (MasterGainValue != null && MasterGainSlider != null) MasterGainValue.Text = $"{MasterGainSlider.Value:+0.0;-0.0;0.0} dB";
        if (BassValue != null && BassSlider != null) BassValue.Text = $"{BassSlider.Value:+0.0;-0.0;0.0} dB";
        if (TrebleValue != null && TrebleSlider != null) TrebleValue.Text = $"{TrebleSlider.Value:+0.0;-0.0;0.0} dB";
        if (BalanceValue != null && BalanceSlider != null)
        {
            double b = BalanceSlider.Value;
            BalanceValue.Text = Math.Abs(b) < 0.005 ? "CENTER" : (b < 0 ? $"L {Math.Abs(b) * 100:0}%" : $"R {b * 100:0}%");
        }
        var eq = new (System.Windows.Controls.TextBlock? Label, System.Windows.Controls.Slider? Slider)[]
        {
            (Eq31Value,Eq31),(Eq62Value,Eq62),(Eq125Value,Eq125),(Eq250Value,Eq250),(Eq500Value,Eq500),
            (Eq1kValue,Eq1k),(Eq2kValue,Eq2k),(Eq4kValue,Eq4k),(Eq8kValue,Eq8k),(Eq16kValue,Eq16k)
        };
        foreach (var pair in eq)
            if (pair.Label != null && pair.Slider != null) pair.Label.Text = pair.Slider.Value.ToString("+0;-0;0");
    }

    private void OutputProcessing_Changed(object sender, RoutedEventArgs e)
    {
        UpdateVideoProcessingValueLabels();
        UpdateAudioProcessingValueLabels();
        if (_loadingOperatorSettings || !IsLoaded) return;
        ApplyOutputProcessingSettings(); SaveOperatorSettings();
        StatusText.Text = "OUTPUT PROCESSING • UPDATED";
    }

    private void ResetVideoProcessing_Click(object sender, RoutedEventArgs e)
    {
        BrightnessSlider.Value=0; ContrastSlider.Value=1; SaturationSlider.Value=1; GammaSlider.Value=1; BlackLevelSlider.Value=0; YLevelSlider.Value=1; ULevelSlider.Value=1; VLevelSlider.Value=1; SharpnessSlider.Value=0; NoiseReductionSlider.Value=0; AutoColorCheck.IsChecked=false;
        AutoQualityCheck.IsChecked=false; AutoQualityStrengthSlider.Value=0.55; AutoWhiteBalanceCheck.IsChecked=false; WhiteTemperatureSlider.Value=0; WhiteTintSlider.Value=0; ScanModeCombo.SelectedIndex=0;
        UpdateVideoProcessingValueLabels(); ApplyOutputProcessingSettings(); SaveOperatorSettings();
    }

    private void ResetAudioProcessing_Click(object sender, RoutedEventArgs e)
    {
        MasterGainSlider.Value=0; BassSlider.Value=0; TrebleSlider.Value=0; BalanceSlider.Value=0; AudioNormalizeCheck.IsChecked=true; AudioLoudnessCheck.IsChecked=false;
        foreach (var sl in new[]{Eq31,Eq62,Eq125,Eq250,Eq500,Eq1k,Eq2k,Eq4k,Eq8k,Eq16k}) sl.Value=0;
        UpdateAudioProcessingValueLabels(); ApplyOutputProcessingSettings(); SaveOperatorSettings();
    }

    private void LoadOperatorSettings()
    {
        _loadingOperatorSettings = true;
        try
        {
            DataStorage.EnsureCreated();
            if (File.Exists(OperatorSettingsPath))
            {
                var settings = JsonSerializer.Deserialize<OperatorSettings>(File.ReadAllText(OperatorSettingsPath));
                if (settings != null)
                {
                    _videoScaleMode = Math.Clamp(settings.VideoScaleMode, 0, 3);
                    _hardwareDecodeEnabled = settings.HardwareDecodeEnabled;
                    _hardwareEncodePreferred = settings.HardwareEncodePreferred;
                    BrightnessSlider.Value=settings.Brightness; ContrastSlider.Value=settings.Contrast; SaturationSlider.Value=settings.Saturation; GammaSlider.Value=settings.Gamma;
                    BlackLevelSlider.Value=settings.BlackLevel; YLevelSlider.Value=settings.YLevel; ULevelSlider.Value=settings.ULevel; VLevelSlider.Value=settings.VLevel; SharpnessSlider.Value=settings.Sharpness; NoiseReductionSlider.Value=settings.NoiseReduction;
                    AutoColorCheck.IsChecked=settings.AutoColor; AutoQualityCheck.IsChecked=settings.AutoQualityEnhance; AutoQualityStrengthSlider.Value=Math.Clamp(settings.AutoQualityStrength,0,1); AutoWhiteBalanceCheck.IsChecked=settings.AutoWhiteBalance; WhiteTemperatureSlider.Value=Math.Clamp(settings.WhiteTemperature,-100,100); WhiteTintSlider.Value=Math.Clamp(settings.WhiteTint,-100,100); ScanModeCombo.SelectedIndex=Math.Clamp(settings.ScanMode,0,3);
                    MasterGainSlider.Value=settings.MasterGainDb; BassSlider.Value=settings.BassDb; TrebleSlider.Value=settings.TrebleDb; BalanceSlider.Value=settings.Balance; AudioNormalizeCheck.IsChecked=settings.AudioNormalize; AudioLoudnessCheck.IsChecked=settings.AudioLoudnessControl;
                    var q=settings.EqualizerDb ?? new double[10]; var eqs=new[]{Eq31,Eq62,Eq125,Eq250,Eq500,Eq1k,Eq2k,Eq4k,Eq8k,Eq16k}; for(int i=0;i<eqs.Length;i++) eqs[i].Value=i<q.Length?q[i]:0;
                    _appliedOutputResolutionIndex=Math.Clamp(settings.OutputResolution,0,OutputResolutionCombo.Items.Count-1);
                    _appliedOutputSampleRateIndex=Math.Clamp(settings.OutputSampleRate,0,OutputSampleRateCombo.Items.Count-1);
                    OutputResolutionCombo.SelectedIndex=_appliedOutputResolutionIndex;
                    OutputSampleRateCombo.SelectedIndex=_appliedOutputSampleRateIndex;
                }
            }
            if(OutputResolutionCombo.SelectedIndex<0){_appliedOutputResolutionIndex=4;OutputResolutionCombo.SelectedIndex=_appliedOutputResolutionIndex;}
            if(OutputSampleRateCombo.SelectedIndex<0){_appliedOutputSampleRateIndex=2;OutputSampleRateCombo.SelectedIndex=_appliedOutputSampleRateIndex;}
            Volatile.Write(ref _appliedOutputResolutionName, ComboTextAt(OutputResolutionCombo, _appliedOutputResolutionIndex));
            _screenOutputPending=false;
            UpdateScreenOutputSummary();
            VideoScaleCombo.SelectedIndex = _videoScaleMode;
            DecodeModeCombo.SelectedIndex = _hardwareDecodeEnabled ? 0 : 1;
            EncodeModeCombo.SelectedIndex = _hardwareEncodePreferred ? 0 : 1;
            DecodeModeStatusText.Text = _hardwareDecodeEnabled ? "ACTIVE • GPU / HARDWARE + CPU FALLBACK" : "ACTIVE • CPU / SOFTWARE";
        UpdateEngineModeButtons();
            EncodeModeStatusText.Text = _hardwareEncodePreferred ? "ACTIVE • GPU / HARDWARE" : "ACTIVE • CPU / SOFTWARE";
        UpdateEngineModeButtons();
            ApplyVideoScaleMode(_videoScaleMode, save: false);
            UpdateVideoProcessingValueLabels();
            UpdateAudioProcessingValueLabels();
            ApplyOutputProcessingSettings();
        }
        catch
        {
            VideoScaleCombo.SelectedIndex = 0; DecodeModeCombo.SelectedIndex = 0; EncodeModeCombo.SelectedIndex = 0;
        }
        finally { _loadingOperatorSettings = false; }
    }

    private void SaveOperatorSettings()
    {
        try
        {
            var settings = new OperatorSettings
            {
                VideoScaleMode = _videoScaleMode,
                HardwareDecodeEnabled = _hardwareDecodeEnabled,
                HardwareEncodePreferred = _hardwareEncodePreferred,
                Brightness=BrightnessSlider.Value, Contrast=ContrastSlider.Value, Saturation=SaturationSlider.Value, Gamma=GammaSlider.Value, BlackLevel=BlackLevelSlider.Value, YLevel=YLevelSlider.Value, ULevel=ULevelSlider.Value, VLevel=VLevelSlider.Value, Sharpness=SharpnessSlider.Value, NoiseReduction=NoiseReductionSlider.Value, AutoColor=AutoColorCheck.IsChecked==true,
                AutoQualityEnhance=AutoQualityCheck.IsChecked==true, AutoQualityStrength=AutoQualityStrengthSlider.Value, AutoWhiteBalance=AutoWhiteBalanceCheck.IsChecked==true, WhiteTemperature=WhiteTemperatureSlider.Value, WhiteTint=WhiteTintSlider.Value, ScanMode=Math.Max(0,ScanModeCombo.SelectedIndex),
                MasterGainDb=MasterGainSlider.Value, BassDb=BassSlider.Value, TrebleDb=TrebleSlider.Value, Balance=BalanceSlider.Value, AudioNormalize=AudioNormalizeCheck.IsChecked==true, AudioLoudnessControl=AudioLoudnessCheck.IsChecked==true,
                EqualizerDb=new[]{Eq31.Value,Eq62.Value,Eq125.Value,Eq250.Value,Eq500.Value,Eq1k.Value,Eq2k.Value,Eq4k.Value,Eq8k.Value,Eq16k.Value},
                OutputResolution=_appliedOutputResolutionIndex,
                OutputSampleRate=_appliedOutputSampleRateIndex
            };
            DataStorage.WritePersistentText(OperatorSettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), "Settings");
            if (SettingsSavedText != null) SettingsSavedText.Text = @"AUTO SAVED • D:\Smart Playout\Data\Settings";
        }
        catch { if (SettingsSavedText != null) SettingsSavedText.Text = "SETTINGS SAVE WARNING"; }
    }


    private void InitializeStreamingWorkspace()
    {
        PopulateStreamResolutionCombos();
        PopulateStreamBitrateCombos();
        RefreshNetworkInterfaces();
        SrtModeCombo.SelectedIndex = 0;
    }

    private void PopulateStreamResolutionCombos()
    {
        var values = new List<string> { "AUTO / SCREEN" };
        if (OutputResolutionCombo != null)
            foreach (var item in OutputResolutionCombo.Items.OfType<System.Windows.Controls.ComboBoxItem>())
                if (item.Content != null) values.Add(item.Content.ToString() ?? "");
        foreach (var cb in new[] { RtmpResolutionCombo, UdpResolutionCombo, SrtResolutionCombo })
        {
            cb.ItemsSource = values.ToList();
            cb.SelectedIndex = 0;
        }
    }


    private sealed class BitrateOption
    {
        public int Kbps { get; init; }
        public string Label { get; init; } = "";
        public override string ToString() => Label;
    }

    private void PopulateStreamBitrateCombos()
    {
        var videoRates = new[] { 500,750,1000,1500,2000,2500,3000,3500,4000,4500,5000,6000,7000,8000,10000,12000,15000,20000,25000,30000,40000,50000,60000,80000,100000 };
        var audioRates = new[] { 64,80,96,112,128,160,192,224,256,320,384,448,512,640 };
        var v = videoRates.Select(k => new BitrateOption { Kbps=k, Label = k >= 1000 ? $"{k/1000.0:0.###} Mbps  •  {k} kbps" : $"{k} kbps" }).ToList();
        var a = audioRates.Select(k => new BitrateOption { Kbps=k, Label=$"{k} kbps" }).ToList();
        foreach (var cb in new[] { RtmpVideoBitrateCombo, UdpVideoBitrateCombo, SrtVideoBitrateCombo }) cb.ItemsSource = v.ToList();
        foreach (var cb in new[] { RtmpAudioBitrateCombo, UdpAudioBitrateCombo, SrtAudioBitrateCombo }) cb.ItemsSource = a.ToList();
    }

    private static int ReadBitrateKbps(System.Windows.Controls.ComboBox cb, int fallback, int min, int max)
    {
        if (cb.SelectedItem is BitrateOption o) return Math.Clamp(o.Kbps,min,max);
        string raw = (cb.Text ?? "").Trim().ToLowerInvariant().Replace("/s","");
        var m = System.Text.RegularExpressions.Regex.Match(raw,@"([0-9]+(?:\.[0-9]+)?)\s*(mbps|mb|m|kbps|kb|k)?");
        if (!m.Success || !double.TryParse(m.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n)) return fallback;
        string unit=m.Groups[2].Value;
        int kbps=(int)Math.Round((unit.StartsWith("m") ? n*1000.0 : n));
        return Math.Clamp(kbps,min,max);
    }

    private static void SelectBitrate(System.Windows.Controls.ComboBox cb, int kbps)
    {
        var found=cb.Items.OfType<BitrateOption>().FirstOrDefault(x=>x.Kbps==kbps);
        if(found!=null) cb.SelectedItem=found; else cb.Text=kbps>=1000?$"{kbps/1000.0:0.###} Mbps":$"{kbps} kbps";
    }

    private sealed class NicOption
    {
        public string Ip { get; init; } = "AUTO";
        public string Label { get; init; } = "AUTO / WINDOWS ROUTING";
        public override string ToString() => Label;
    }

    private void RefreshNetworkInterfaces()
    {
        var nics = new List<NicOption> { new() };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var a in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    nics.Add(new NicOption { Ip = a.Address.ToString(), Label = $"{nic.Name} • {a.Address}" });
            }
        }
        catch { }
        foreach (var cb in new[] { RtmpNicCombo, UdpNicCombo, SrtNicCombo })
        {
            var selected = (cb.SelectedItem as NicOption)?.Ip ?? "AUTO";
            cb.ItemsSource = nics.ToList();
            cb.SelectedItem = cb.Items.OfType<NicOption>().FirstOrDefault(x => x.Ip == selected) ?? cb.Items[0];
        }
    }

    private async Task RefreshStreamingCapabilitiesAsync()
    {
        StreamRuntimeAuditText.Text = "FFMPEG • DETECTING...";
        _streamCapabilities = await _streaming.DetectCapabilitiesAsync();
        var video = _streamCapabilities.VideoEncoders;
        var audio = _streamCapabilities.AudioEncoders;
        foreach (var cb in new[] { RtmpVideoEncoderCombo, UdpVideoEncoderCombo, SrtVideoEncoderCombo }) cb.ItemsSource = video.ToList();
        foreach (var cb in new[] { RtmpAudioEncoderCombo, UdpAudioEncoderCombo, SrtAudioEncoderCombo }) cb.ItemsSource = audio.ToList();
        RestoreStreamEncoderSelections();
        RefreshNetworkInterfaces();
        StreamRuntimeAuditText.Text = $"FFMPEG • RTMP {(_streamCapabilities.HasRtmp ? "OK" : "NO")} • UDP {(_streamCapabilities.HasUdp ? "OK" : "NO")} • SRT {(_streamCapabilities.HasSrt ? "OK" : "NO")} • AUTO {_streamCapabilities.AutoVideoEncoder}";
        RtmpDetailText.Text = _streamCapabilities.HasRtmp ? $"RTMP ready • AUTO encoder: {_streamCapabilities.AutoVideoEncoder} • {_streamCapabilities.GpuSummary}." : "RTMP protocol not present in this FFmpeg runtime.";
        UdpDetailText.Text = _streamCapabilities.HasUdp ? "UDP protocol available. NIC localaddr is used for explicit UDP routing." : "UDP protocol not present in this FFmpeg runtime.";
        SrtDetailText.Text = _streamCapabilities.HasSrt ? "SRT protocol available. SRT mode / latency controls are active." : "SRT protocol not present in this FFmpeg runtime. Install/use a matching FFmpeg build with libsrt support.";
    }

    private void StreamRefreshCapabilities_Click(object sender, RoutedEventArgs e) => _ = RefreshStreamingCapabilitiesAsync();

    private void RestoreStreamEncoderSelections()
    {
        SelectEncoder(RtmpVideoEncoderCombo, _streamSettings.Rtmp.VideoEncoder);
        SelectEncoder(UdpVideoEncoderCombo, _streamSettings.DvbUdp.VideoEncoder);
        SelectEncoder(SrtVideoEncoderCombo, _streamSettings.DvbSrt.VideoEncoder);
        SelectEncoder(RtmpAudioEncoderCombo, _streamSettings.Rtmp.AudioEncoder);
        SelectEncoder(UdpAudioEncoderCombo, _streamSettings.DvbUdp.AudioEncoder);
        SelectEncoder(SrtAudioEncoderCombo, _streamSettings.DvbSrt.AudioEncoder);
    }

    private static void SelectEncoder(System.Windows.Controls.ComboBox cb, string name)
    {
        var found = cb.Items.OfType<EncoderOption>().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        cb.SelectedItem = found ?? cb.Items.OfType<EncoderOption>().FirstOrDefault();
    }

    private void LoadStreamingSettings()
    {
        _loadingStreamSettings = true;
        try
        {
            var json = File.Exists(StreamSettingsPath) ? File.ReadAllText(StreamSettingsPath) : "";
            if (!string.IsNullOrWhiteSpace(json)) _streamSettings = JsonSerializer.Deserialize<StreamingSettingsFile>(json) ?? new();
        }
        catch { _streamSettings = new(); }
        try
        {
            LoadStreamPanel(_streamSettings.Rtmp, RtmpResolutionCombo, RtmpVideoBitrateCombo, RtmpAudioBitrateCombo, RtmpNicCombo, RtmpAutoStartCheck);
            LoadStreamPanel(_streamSettings.DvbUdp, UdpResolutionCombo, UdpVideoBitrateCombo, UdpAudioBitrateCombo, UdpNicCombo, UdpAutoStartCheck);
            LoadStreamPanel(_streamSettings.DvbSrt, SrtResolutionCombo, SrtVideoBitrateCombo, SrtAudioBitrateCombo, SrtNicCombo, SrtAutoStartCheck);
            RtmpUrlBox.Text = _streamSettings.Rtmp.RtmpUrl;
            UdpDestinationsList.ItemsSource = _streamSettings.DvbUdp.Destinations;
            SrtDestinationsList.ItemsSource = _streamSettings.DvbSrt.Destinations;
            LoadDvbFields(_streamSettings.DvbUdp, UdpVideoPidBox,UdpAudioPidBox,UdpPmtPidBox,UdpServiceIdBox,UdpTsidBox,UdpOnidBox,UdpMuxRateBox,UdpPcrBox);
            LoadDvbFields(_streamSettings.DvbSrt, SrtVideoPidBox,SrtAudioPidBox,SrtPmtPidBox,SrtServiceIdBox,SrtTsidBox,SrtOnidBox,SrtMuxRateBox,SrtPcrBox);
            SrtModeCombo.SelectedIndex = Math.Max(0,new[]{"caller","listener","rendezvous"}.ToList().FindIndex(x=>x==_streamSettings.DvbSrt.SrtMode));
            SrtLatencyBox.Text = _streamSettings.DvbSrt.SrtLatencyMs.ToString();
            SrtPassphraseBox.Password = _streamSettings.DvbSrt.SrtPassphrase ?? "";
            RestoreStreamEncoderSelections();
        }
        finally { _loadingStreamSettings = false; }
    }

    private void LoadStreamPanel(StreamProfile p, System.Windows.Controls.ComboBox resolution, System.Windows.Controls.ComboBox vbr, System.Windows.Controls.ComboBox abr, System.Windows.Controls.ComboBox nic, System.Windows.Controls.CheckBox auto)
    {
        resolution.SelectedItem = resolution.Items.Cast<object>().FirstOrDefault(x => string.Equals(x?.ToString(),p.Resolution,StringComparison.OrdinalIgnoreCase)) ?? resolution.Items[0];
        SelectBitrate(vbr,p.VideoKbps); SelectBitrate(abr,p.AudioKbps); auto.IsChecked = p.Armed;
        var match = nic.Items.OfType<NicOption>().FirstOrDefault(x=>x.Ip==p.InterfaceIp); if(match!=null) nic.SelectedItem=match;
    }

    private static void LoadDvbFields(StreamProfile p, params System.Windows.Controls.TextBox[] b)
    {
        int[] v={p.VideoPid,p.AudioPid,p.PmtPid,p.ServiceId,p.Tsid,p.Onid,p.MuxRate,p.PcrMs}; for(int i=0;i<b.Length&&i<v.Length;i++) b[i].Text=v[i].ToString();
    }

    private void SaveStreamingSettings()
    {
        if (_loadingStreamSettings) return;
        _streamSettings.Rtmp = ReadProfile(StreamKind.Rtmp);
        _streamSettings.DvbUdp = ReadProfile(StreamKind.DvbUdp);
        _streamSettings.DvbSrt = ReadProfile(StreamKind.DvbSrt);
        DataStorage.WritePersistentText(StreamSettingsPath, JsonSerializer.Serialize(_streamSettings,new JsonSerializerOptions{WriteIndented=true}), "Settings");
    }

    private void LoadDeckLinkSettings()
    {
        try
        {
            if(File.Exists(DeckLinkSettingsPath))
                _deckLinkPersist=JsonSerializer.Deserialize<DeckLinkPersistState>(File.ReadAllText(DeckLinkSettingsPath)) ?? new();
        }
        catch { _deckLinkPersist=new(); }
        _deckLinkPersistRestorePending=true;
    }

    private void SaveDeckLinkSettings()
    {
        try
        {
            DataStorage.WritePersistentText(DeckLinkSettingsPath,JsonSerializer.Serialize(_deckLinkPersist,new JsonSerializerOptions{WriteIndented=true}),"Settings");
        }
        catch { }
    }

    private void CaptureDeckLinkRoute(bool armed)
    {
        string selected=DeckLinkDeviceCombo.SelectedItem?.ToString()??"";
        _deckLinkPersist.Armed=armed;
        _deckLinkPersist.Device=selected;
        if(DeckLinkConnectionCombo.SelectedItem is DeckLinkOutputConnection c) _deckLinkPersist.ConnectionValue=c.Value;
        _deckLinkPersist.AutoMode=DeckLinkModeCombo.SelectedItem is string;
        if(DeckLinkModeCombo.SelectedItem is DeckLinkOutputMode m) _deckLinkPersist.ModeId=m.Id;
        SaveDeckLinkSettings();
    }

    private void TryAutoStartArmedDeckLink()
    {
        if(!_deckLinkPersist.Armed || !_outputCapabilitiesLoaded || _latestProgramFrame==null) return;
        if(_deckLinkOutput.Running || _deckLinkNativeOutput.Running) return;
        if(DateTime.UtcNow<_deckLinkAutoStartRetryAfterUtc) return;
        if(Interlocked.Exchange(ref _deckLinkAutoStartInProgress,1)!=0) return;
        _deckLinkAutoStartRetryAfterUtc=DateTime.UtcNow.AddSeconds(10);
        Dispatcher.BeginInvoke(new Action(() => DeckLinkOutputStart_Click(DeckLinkStartButton,new RoutedEventArgs())));
    }

    private StreamProfile ReadProfile(StreamKind kind)
    {
        StreamProfile p = kind switch { StreamKind.Rtmp => _streamSettings.Rtmp, StreamKind.DvbUdp => _streamSettings.DvbUdp, _ => _streamSettings.DvbSrt };
        p.Kind=kind;
        var resolution = kind==StreamKind.Rtmp?RtmpResolutionCombo:kind==StreamKind.DvbUdp?UdpResolutionCombo:SrtResolutionCombo;
        var venc = kind==StreamKind.Rtmp?RtmpVideoEncoderCombo:kind==StreamKind.DvbUdp?UdpVideoEncoderCombo:SrtVideoEncoderCombo;
        var aenc = kind==StreamKind.Rtmp?RtmpAudioEncoderCombo:kind==StreamKind.DvbUdp?UdpAudioEncoderCombo:SrtAudioEncoderCombo;
        var vbr = kind==StreamKind.Rtmp?RtmpVideoBitrateCombo:kind==StreamKind.DvbUdp?UdpVideoBitrateCombo:SrtVideoBitrateCombo;
        var abr = kind==StreamKind.Rtmp?RtmpAudioBitrateCombo:kind==StreamKind.DvbUdp?UdpAudioBitrateCombo:SrtAudioBitrateCombo;
        var nic = kind==StreamKind.Rtmp?RtmpNicCombo:kind==StreamKind.DvbUdp?UdpNicCombo:SrtNicCombo;
        var auto = kind==StreamKind.Rtmp?RtmpAutoStartCheck:kind==StreamKind.DvbUdp?UdpAutoStartCheck:SrtAutoStartCheck;
        p.Resolution=resolution.SelectedItem?.ToString()??"AUTO / SCREEN";
        p.VideoEncoder=(venc.SelectedItem as EncoderOption)?.Name ?? p.VideoEncoder;
        p.AudioEncoder=(aenc.SelectedItem as EncoderOption)?.Name ?? p.AudioEncoder;
        p.VideoKbps=ReadBitrateKbps(vbr,p.VideoKbps,250,200000);
        p.AudioKbps=ReadBitrateKbps(abr,p.AudioKbps,32,4096);
        p.InterfaceIp=(nic.SelectedItem as NicOption)?.Ip??"AUTO";
        p.Armed=auto.IsChecked==true;
        if(kind==StreamKind.Rtmp) p.RtmpUrl=RtmpUrlBox.Text.Trim();
        else if(kind==StreamKind.DvbUdp) ReadDvbFields(p,UdpVideoPidBox,UdpAudioPidBox,UdpPmtPidBox,UdpServiceIdBox,UdpTsidBox,UdpOnidBox,UdpMuxRateBox,UdpPcrBox);
        else
        {
            ReadDvbFields(p,SrtVideoPidBox,SrtAudioPidBox,SrtPmtPidBox,SrtServiceIdBox,SrtTsidBox,SrtOnidBox,SrtMuxRateBox,SrtPcrBox);
            p.SrtMode=(SrtModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString()??"caller";
            if(int.TryParse(SrtLatencyBox.Text,out var latency)) p.SrtLatencyMs=Math.Clamp(latency,20,8000);
            p.SrtPassphrase=SrtPassphraseBox.Password;
        }
        return p;
    }

    private static void ReadDvbFields(StreamProfile p, params System.Windows.Controls.TextBox[] b)
    {
        int[] v={p.VideoPid,p.AudioPid,p.PmtPid,p.ServiceId,p.Tsid,p.Onid,p.MuxRate,p.PcrMs}; for(int i=0;i<b.Length&&i<v.Length;i++) if(int.TryParse(b[i].Text,out var n)) v[i]=n;
        p.VideoPid=Math.Clamp(v[0],32,8186); p.AudioPid=Math.Clamp(v[1],32,8186); p.PmtPid=Math.Clamp(v[2],32,8186); p.ServiceId=Math.Max(1,v[3]); p.Tsid=Math.Max(1,v[4]); p.Onid=Math.Max(1,v[5]); p.MuxRate=Math.Max(0,v[6]); p.PcrMs=Math.Clamp(v[7],5,1000);
    }

    private void StreamSave_Click(object sender, RoutedEventArgs e)
    {
        SaveStreamingSettings(); StatusText.Text="STREAM • profiles saved";
    }

    private void UdpAddDestination_Click(object sender, RoutedEventArgs e) => AddDestination(_streamSettings.DvbUdp,UdpHostBox,UdpPortBox,UdpDestinationsList);
    private void SrtAddDestination_Click(object sender, RoutedEventArgs e) => AddDestination(_streamSettings.DvbSrt,SrtHostBox,SrtPortBox,SrtDestinationsList);
    private void UdpRemoveDestination_Click(object sender, RoutedEventArgs e) => RemoveDestination(_streamSettings.DvbUdp,UdpDestinationsList);
    private void SrtRemoveDestination_Click(object sender, RoutedEventArgs e) => RemoveDestination(_streamSettings.DvbSrt,SrtDestinationsList);

    private void AddDestination(StreamProfile p, System.Windows.Controls.TextBox host, System.Windows.Controls.TextBox port, System.Windows.Controls.ListBox list)
    {
        if(string.IsNullOrWhiteSpace(host.Text)||!int.TryParse(port.Text,out var n)||n<1||n>65535) return;
        if(!p.Destinations.Any(x=>x.Host==host.Text.Trim()&&x.Port==n)) p.Destinations.Add(new StreamDestination{Host=host.Text.Trim(),Port=n});
        list.ItemsSource=null; list.ItemsSource=p.Destinations; SaveStreamingSettings();
    }

    private void RemoveDestination(StreamProfile p, System.Windows.Controls.ListBox list)
    {
        if(list.SelectedItem is StreamDestination d) p.Destinations.Remove(d); list.ItemsSource=null; list.ItemsSource=p.Destinations; SaveStreamingSettings();
    }

    private async void RtmpStart_Click(object sender, RoutedEventArgs e) { RtmpAutoStartCheck.IsChecked=true; SaveStreamingSettings(); await StartStreamSafeAsync(StreamKind.Rtmp); }
    private async void UdpStart_Click(object sender, RoutedEventArgs e) { UdpAutoStartCheck.IsChecked=true; SaveStreamingSettings(); await StartStreamSafeAsync(StreamKind.DvbUdp); }
    private async void SrtStart_Click(object sender, RoutedEventArgs e) { SrtAutoStartCheck.IsChecked=true; SaveStreamingSettings(); await StartStreamSafeAsync(StreamKind.DvbSrt); }
    private void RtmpStop_Click(object sender, RoutedEventArgs e) { RtmpAutoStartCheck.IsChecked=false; _streamSettings.Rtmp.Armed=false; SaveStreamingSettings(); _streaming.Stop(StreamKind.Rtmp,"STOPPED BY OPERATOR"); }
    private void UdpStop_Click(object sender, RoutedEventArgs e) { UdpAutoStartCheck.IsChecked=false; _streamSettings.DvbUdp.Armed=false; SaveStreamingSettings(); _streaming.Stop(StreamKind.DvbUdp,"STOPPED BY OPERATOR"); }
    private void SrtStop_Click(object sender, RoutedEventArgs e) { SrtAutoStartCheck.IsChecked=false; _streamSettings.DvbSrt.Armed=false; SaveStreamingSettings(); _streaming.Stop(StreamKind.DvbSrt,"STOPPED BY OPERATOR"); }

    private void SuspendArmedStreams(string reason)
    {
        foreach(var k in new[]{StreamKind.Rtmp,StreamKind.DvbUdp,StreamKind.DvbSrt})
        {
            var p=k==StreamKind.Rtmp?_streamSettings.Rtmp:k==StreamKind.DvbUdp?_streamSettings.DvbUdp:_streamSettings.DvbSrt;
            if(p.Armed && _streaming.State(k).Running) _streaming.Stop(k,reason);
        }
    }

    private async Task RestartArmedAutoResolutionStreamsAsync()
    {
        foreach(var k in new[]{StreamKind.Rtmp,StreamKind.DvbUdp,StreamKind.DvbSrt})
        {
            var p=k==StreamKind.Rtmp?_streamSettings.Rtmp:k==StreamKind.DvbUdp?_streamSettings.DvbUdp:_streamSettings.DvbSrt;
            if(p.Armed && string.Equals(p.Resolution,"AUTO / SCREEN",StringComparison.OrdinalIgnoreCase) && _streaming.State(k).Running)
                await StartStreamSafeAsync(k);
        }
    }

    private async Task StartStreamSafeAsync(StreamKind kind)
    {
        try
        {
            if(_streamCapabilities.VideoEncoders.Count==0 || _streamCapabilities.AudioEncoders.Count==0) await RefreshStreamingCapabilitiesAsync();
            await StartStreamAsync(kind);
        }
        catch(Exception ex)
        {
            UpdateStreamStatus(kind,new StreamProcessState{Message="ERROR • "+ex.Message});
            StatusText.Text="STREAM ERROR • "+ex.Message;
        }
    }

    private async Task RestartArmedStreamsForCurrentProgramAsync(bool onlyIfStopped=false)
    {
        if(string.IsNullOrWhiteSpace(_programFile)||!File.Exists(_programFile)) return;
        SaveStreamingSettings();
        foreach(var k in new[]{StreamKind.Rtmp,StreamKind.DvbUdp,StreamKind.DvbSrt})
        {
            var p=k==StreamKind.Rtmp?_streamSettings.Rtmp:k==StreamKind.DvbUdp?_streamSettings.DvbUdp:_streamSettings.DvbSrt;
            if(!p.Armed) continue; if(onlyIfStopped && _streaming.State(k).Running) continue;
            try { await StartStreamAsync(k); } catch { }
        }
    }

    private async Task StartStreamAsync(StreamKind kind)
    {
        EnsureModuleWorker(ModuleKind.OutputWorker);
        SaveStreamingSettings();
        if(string.IsNullOrWhiteSpace(_programFile)||!File.Exists(_programFile)) { UpdateStreamStatus(kind,new StreamProcessState{Message="ARMED • WAITING FOR PLAYOUT"}); return; }
        if(kind==StreamKind.Rtmp&&!_streamCapabilities.HasRtmp) throw new InvalidOperationException("Detected FFmpeg runtime does not expose RTMP protocol.");
        if(kind==StreamKind.DvbUdp&&!_streamCapabilities.HasUdp) throw new InvalidOperationException("Detected FFmpeg runtime does not expose UDP protocol.");
        if(kind==StreamKind.DvbSrt&&!_streamCapabilities.HasSrt) throw new InvalidOperationException("Detected FFmpeg runtime does not expose SRT protocol / libsrt.");
        var p=kind==StreamKind.Rtmp?_streamSettings.Rtmp:kind==StreamKind.DvbUdp?_streamSettings.DvbUdp:_streamSettings.DvbSrt;
        // External writers attach to the already-running MasterAvBus. Their logical clip
        // position follows the committed Program media timeline, never the local sound-card
        // buffered playback position.
        double pos=Math.Max(_programClipStartSeconds,Math.Max(0,_program.PositionSeconds));
        if(_programClipEndSeconds.HasValue) pos=Math.Min(pos,_programClipEndSeconds.Value);
        string screen=ComboTextAt(OutputResolutionCombo,_appliedOutputResolutionIndex);
        int sr=ParseSampleRate(ComboTextAt(OutputSampleRateCombo,_appliedOutputSampleRateIndex));
        string vf=BuildStreamingVideoFilter();
        string af=BuildStreamingAudioFilter();
        int audioStream=AudioLanguageHelper.ResolveStreamIndex(_programFile!,_programAudioLanguage);
        var frame=_latestProgramFrame ?? throw new InvalidOperationException("Program video frame is not ready yet.");
        // v0.6.8.50.9: SCREEN/APPLIED resolution is the one authoritative FINAL PROGRAM raster.
        // Per-writer resolution selectors are compatibility UI only; every live output consumes
        // the same Program canvas so RTMP / DVB UDP / DVB SRT / Graphics / DeckLink cannot drift
        // away from the operator's applied main output registration.
        string effectiveResolution = screen;
        p.Resolution = "AUTO / SCREEN";
        var busFormat = StreamingEngine.ParseResolution(effectiveResolution);
        int busWidth = busFormat.W > 0 ? busFormat.W : frame.PixelWidth;
        int busHeight = busFormat.H > 0 ? busFormat.H : frame.PixelHeight;
        double busFps = busFormat.Fps > 0 ? busFormat.Fps : AppliedOutputFrameRate(screen);
        // Stable writer raster for the full session: clip changes never renegotiate the raw bus.
        double displayAspect = OutputDisplayAspect(effectiveResolution, _videoScaleMode, busWidth, busHeight);
        int busSampleRate=sr>0?sr:48000;
        string routeDestination=kind==StreamKind.Rtmp?p.RtmpUrl:string.Join(";",p.Destinations.Select(d=>$"{d.Host}:{d.Port}"));
        string workerArguments=_streaming.BuildOutputWorkerArgumentsTemplate(p,busWidth,busHeight,busFps,busSampleRate,
            displayAspect,screen,busSampleRate);
        _=MirrorModuleCommandAsync(ModuleKind.OutputWorker,"REGISTER_ROUTE",new OutputRouteRegistration(
            p.Kind.ToString(),p.Kind.ToString(),StreamingEngine.FfmpegPath,routeDestination,busWidth,busHeight,busFps,busSampleRate,2,
            p.VideoEncoder,p.AudioEncoder,p.VideoKbps,p.AudioKbps,"bgra","SMARTPlayout.Program.BGRA","SMARTPlayout.Program.PCM",false,
            workerArguments));
        var busSession=_masterAvBus.Create(p.Kind,busWidth,busHeight,busFps,busSampleRate,_videoScaleMode,displayAspect);
        try { await Task.Run(()=>_streaming.Start(p,_programFile!,pos,_programClipEndSeconds,screen,sr,audioStream,vf,af,busSession)); }
        catch { _masterAvBus.Remove(p.Kind); throw; }
    }


    private static double OutputDisplayAspect(string resolution,int scaleMode,int width,int height)
    {
        if(scaleMode==1) return 16.0/9.0;
        if(scaleMode==2) return 4.0/3.0;
        string r=(resolution??"").ToUpperInvariant();
        if(r.Contains("PAL_16X9")||r.Contains("NTSC_16X9")) return 16.0/9.0;
        if(r.Contains("PAL")||r.Contains("NTSC")) return 4.0/3.0;
        if(r.Contains("2K_DCI")||r.Contains("4K_DCI")) return 256.0/135.0;
        if(width>0&&height>0) return (double)width/height;
        return 16.0/9.0;
    }

    private static double AppliedOutputFrameRate(string screen)
    {
        string r=(screen??"").ToUpperInvariant();
        // Interlaced eMVF names carry field rate; the live writer bus carries full frames.
        if(r.Contains("_5994I")) return 29.97;
        if(r.Contains("_60I")) return 30.0;
        if(r.Contains("_50I")) return 25.0;
        if(r.Contains("NTSC")) return 29.97;
        if(r.Contains("PAL")) return 25.0;
        if(r.Contains("11988")) return 119.88;
        if(r.Contains("9590")) return 95.90;
        if(r.Contains("5994")) return 59.94;
        if(r.Contains("4795")) return 47.95;
        if(r.Contains("2997")) return 29.97;
        if(r.Contains("2398")) return 23.976;
        if(r.Contains("120P")) return 120;
        if(r.Contains("100P")) return 100;
        if(r.Contains("96P")) return 96;
        if(r.Contains("60P")) return 60;
        if(r.Contains("50P")) return 50;
        if(r.Contains("48P")) return 48;
        if(r.Contains("30P")) return 30;
        if(r.Contains("25P")) return 25;
        if(r.Contains("24P")) return 24;
        return 25;
    }

    private int ParseSampleRate(string s)
    {
        var digits=new string((s??"").Where(char.IsDigit).ToArray()); return int.TryParse(digits,out var n)?n:48000;
    }

    private string BuildStreamingVideoFilter()
    {
        var filters=new List<string>();
        double b=BrightnessSlider.Value,c=ContrastSlider.Value,s=SaturationSlider.Value,g=GammaSlider.Value;
        filters.Add($"eq=brightness={b.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)}:contrast={c.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)}:saturation={s.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)}:gamma={g.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)}");
        if(SharpnessSlider.Value>0.01) filters.Add($"unsharp=5:5:{SharpnessSlider.Value.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)}");
        return string.Join(",",filters);
    }

    private string BuildStreamingAudioFilter()
    {
        var filters=new List<string>();
        var ci=System.Globalization.CultureInfo.InvariantCulture;
        if(Math.Abs(MasterGainSlider.Value)>0.01) filters.Add($"volume={MasterGainSlider.Value.ToString("0.##",ci)}dB");
        if(Math.Abs(BassSlider.Value)>0.01) filters.Add($"bass=g={BassSlider.Value.ToString("0.##",ci)}");
        if(Math.Abs(TrebleSlider.Value)>0.01) filters.Add($"treble=g={TrebleSlider.Value.ToString("0.##",ci)}");
        var bands=new[]{31d,62d,125d,250d,500d,1000d,2000d,4000d,8000d,16000d};
        var vals=new[]{Eq31.Value,Eq62.Value,Eq125.Value,Eq250.Value,Eq500.Value,Eq1k.Value,Eq2k.Value,Eq4k.Value,Eq8k.Value,Eq16k.Value};
        for(int i=0;i<bands.Length;i++) if(Math.Abs(vals[i])>0.01) filters.Add($"equalizer=f={bands[i].ToString("0",ci)}:t=q:w=1:g={vals[i].ToString("0.##",ci)}");
        if(AudioLoudnessCheck.IsChecked==true) filters.Add("alimiter=limit=0.95");
        return string.Join(",",filters);
    }

    private void UpdateStreamStatus(StreamKind kind, StreamProcessState st)
    {
        var text=kind==StreamKind.Rtmp?RtmpStatusText:kind==StreamKind.DvbUdp?UdpStatusText:SrtStatusText;
        text.Text=st.Running?$"RUNNING • OUTPUT VERIFIED • {st.Frames}f":st.Message;
        text.Foreground=st.Running?System.Windows.Media.Brushes.LightGreen:((st.Message.Contains("WAITING")||st.Message.Contains("CONNECTING"))?System.Windows.Media.Brushes.Gold:System.Windows.Media.Brushes.LightCoral);
    }

    private static string ComboText(System.Windows.Controls.ComboBox combo)
    {
        return (combo?.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "AUTO";
    }

    private string ComboTextAt(System.Windows.Controls.ComboBox combo, int index)
    {
        if (combo == null || index < 0 || index >= combo.Items.Count) return "AUTO";
        return (combo.Items[index] as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "AUTO";
    }

    private void UpdateScreenOutputSummary()
    {
        if (OutputProfileSummaryText == null) return;
        string appliedR=ComboTextAt(OutputResolutionCombo,_appliedOutputResolutionIndex);
        string appliedA=ComboTextAt(OutputSampleRateCombo,_appliedOutputSampleRateIndex);
        string selectedR=ComboText(OutputResolutionCombo);
        string selectedA=ComboText(OutputSampleRateCombo);
        OutputProfileSummaryText.Text=$"APPLIED: {appliedR}  •  {appliedA}";
        _screenOutputPending = OutputResolutionCombo.SelectedIndex != _appliedOutputResolutionIndex || OutputSampleRateCombo.SelectedIndex != _appliedOutputSampleRateIndex;
        if (ScreenPendingText != null)
            ScreenPendingText.Text = _screenOutputPending ? $"PENDING: {selectedR}  •  {selectedA} — press APPLY CHANGES" : "No pending changes";
        if (ScreenSavedText != null)
            ScreenSavedText.Text = _screenOutputPending ? "PENDING • NOT APPLIED" : "APPLIED";
    }

    private void ScreenOutputSetting_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingOperatorSettings || !IsLoaded) return;
        // Selection is preview/pending only. Never change the live output here.
        UpdateScreenOutputSummary();
        StatusText.Text = _screenOutputPending ? "SCREEN • pending output change — press APPLY CHANGES" : "SCREEN • output profile unchanged";
    }

    private void ApplyScreenChanges_Click(object sender, RoutedEventArgs e)
    {
        if (OutputResolutionCombo.SelectedIndex < 0 || OutputSampleRateCombo.SelectedIndex < 0) return;

        // Atomic output-profile commit. Source decode/player state is deliberately untouched:
        // no Open/Stop/Seek, no colour pipeline reset and no A/V clock reset here.
        _appliedOutputResolutionIndex = OutputResolutionCombo.SelectedIndex;
        _appliedOutputSampleRateIndex = OutputSampleRateCombo.SelectedIndex;
        Volatile.Write(ref _appliedOutputResolutionName, ComboTextAt(OutputResolutionCombo, _appliedOutputResolutionIndex));
        _screenOutputPending = false;
        SaveOperatorSettings();
        UpdateScreenOutputSummary();
        // Commit the broadcast canvas immediately. The Program confidence-monitor box is display-only
        // and must never become the signal raster.
        UpdateProgramBroadcastRenderTarget();
        lock (_programPreloadLock)
        {
            if (_preloadedProgramVideo != null)
                UpdateProgramBroadcastRenderTarget(_preloadedProgramVideo);
        }
        // Re-query/filter DeckLink modes so the selected hardware raster follows this same applied profile.
        RefreshDeckLinkModes();

        // Output consumers (stream / SDI / hardware card) read these applied values when
        // they create or refresh their output graph. Keeping this commit separate from the
        // source player prevents avoidable black frames, source reopen and playback lag.
        StatusText.Text = $"SCREEN APPLIED • {ComboText(OutputResolutionCombo)} • {ComboText(OutputSampleRateCombo)} • source playback untouched";
        _ = RestartArmedAutoResolutionStreamsAsync();
        if(_deckLinkOutput.Running || _deckLinkNativeOutput.Running)
        {
            // A hardware output cannot keep transmitting an old raster after SCREEN/APPLY. Stop and
            // restart it through the normal guarded path, which revalidates card/connector/mode.
            _deckLinkOutput.Stop("SCREEN MODE CHANGE • RESTARTING OUTPUT");
            _deckLinkNativeOutput.Stop("SCREEN MODE CHANGE • RESTARTING OUTPUT");
            try{_deckLinkBusSession?.Dispose();}catch{} _deckLinkBusSession=null;
            RefreshDeckLinkRouteOptions();
            DeckLinkOutputStart_Click(DeckLinkStartButton,new RoutedEventArgs());
        }
    }

    private Row? SelectedInstantRow() => PlaylistList.SelectedItem as Row;

    private async void InstantPlayNow_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstantRow() is not Row row || !File.Exists(row.FilePath)) return;
        // Manual Play Now deliberately takes operator control of Program output.
        _scheduledPlaylistActive = false;
        _activeScheduledJob = null;
        _activeScheduleEnd = null;
        await TakeToAirAtAsync(row.FilePath, row.InPointSeconds, row.AudioLanguage, row.OutPointSeconds > row.InPointSeconds ? row.OutPointSeconds : null);
        StatusText.Text = $"PLAY NOW • {row.Name} • {row.AudioLanguage}";
    }

    private void InstantPlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstantRow() is not Row row) return;
        _nextFile = row.FilePath;
        _nextAudioLanguage = row.AudioLanguage;
        _nextStartSeconds = row.InPointSeconds;
        _nextEndSeconds = row.OutPointSeconds > row.InPointSeconds ? row.OutPointSeconds : null;
        NextText.Text = row.Name;
        StatusText.Text = $"PLAY NEXT SET • {row.AudioLanguage}";
    }

    private Microsoft.Win32.OpenFileDialog InstantSingleMediaDialog(string title) => new()
    {
        Title = title,
        Multiselect = false,
        CheckFileExists = true,
        Filter = "Media files|*.mp4;*.mkv;*.mov;*.avi;*.mxf;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.flv;*.m4v;*.3gp;*.ogv;*.m2p;*.m2v;*.mpv;*.asf;*.f4v;*.ogg;*.rm;*.rmvb;*.nut;*.y4m;*.dv;*.divx;*.xvid;*.h264;*.h265;*.hevc;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.opus|All files|*.*"
    };

    private async Task InstantInsertAtSelectionAsync(bool before)
    {
        int selected = PlaylistList.SelectedIndex;
        if (selected < 0) return;

        var d = InstantSingleMediaDialog(before ? "Insert Media Before" : "Insert Media After");
        if (d.ShowDialog() != true || !File.Exists(d.FileName)) return;

        int index = before ? selected : selected + 1;
        var row = new Row
        {
            FilePath = d.FileName,
            AudioLanguage = AudioLanguageHelper.DefaultLanguage
        };

        _items.Insert(index, row);
        RefreshRows();
        PlaylistList.SelectedIndex = index;
        await PopulateMediaInfoAsync(row);
        SaveAutoPlaylistState();
        StatusText.Text = before ? "INSTANT MEDIA INSERTED BEFORE" : "INSTANT MEDIA INSERTED AFTER";
    }

    private async void InstantInsertBefore_Click(object sender, RoutedEventArgs e) =>
        await InstantInsertAtSelectionAsync(before: true);

    private async void InstantInsertAfter_Click(object sender, RoutedEventArgs e) =>
        await InstantInsertAtSelectionAsync(before: false);

    private async void InstantReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        int index = PlaylistList.SelectedIndex;
        if (index < 0) return;

        var d = InstantSingleMediaDialog("Replace / Change Media File");
        if (d.ShowDialog() != true || !File.Exists(d.FileName)) return;

        var replacement = new Row
        {
            FilePath = d.FileName,
            Description = Path.GetFileNameWithoutExtension(d.FileName),
            AudioLanguage = AudioLanguageHelper.DefaultLanguage
        };

        _items[index] = replacement;
        RefreshRows();
        PlaylistList.SelectedIndex = index;
        await PopulateMediaInfoAsync(replacement);
        SaveAutoPlaylistState();
        StatusText.Text = "INSTANT MEDIA REPLACED";
    }

    private void InstantDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstantRow() is not Row row) return;
        int index = PlaylistList.SelectedIndex;

        var copy = new Row
        {
            FilePath = row.FilePath,
            ProgramName = row.ProgramName,
            DurationSeconds = row.DurationSeconds,
            SourceDurationSeconds = row.SourceDurationSeconds,
            InPointSeconds = row.InPointSeconds,
            OutPointSeconds = row.OutPointSeconds,
            Description = row.Description,
            AudioLanguage = row.AudioLanguage
        };
        copy.AvailableAudioLanguages.Clear();
        foreach (var lang in row.AvailableAudioLanguages)
            copy.AvailableAudioLanguages.Add(lang);

        _items.Insert(index + 1, copy);
        RefreshRows();
        PlaylistList.SelectedIndex = index + 1;
        SaveAutoPlaylistState();
        StatusText.Text = "INSTANT MEDIA DUPLICATED";
    }

    private void InstantProperties_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstantRow() is not Row row) return;
        System.Windows.MessageBox.Show(
            $"Media: {row.Name}\n\n" +
            $"Path: {row.FilePath}\n\n" +
            $"Type: {row.Type}\n" +
            $"Source Duration: {row.DurationText}\n" +
            $"IN: {row.InText}\n" +
            $"OUT: {row.OutText}\n" +
            $"Play Duration: {row.PlayDurationText}\n" +
            $"Audio Language: {row.AudioLanguage}\n" +
            $"Description: {row.Description}",
            "Instant Playlist Media Properties",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void InstantContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        InstantAudioContextMenu.Items.Clear();
        if (SelectedInstantRow() is not Row row)
        {
            InstantAudioContextMenu.IsEnabled = false;
            return;
        }

        InstantAudioContextMenu.IsEnabled = true;
        foreach (var language in row.AvailableAudioLanguages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = language,
                Tag = language,
                IsCheckable = true,
                IsChecked = string.Equals(language, row.AudioLanguage, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += InstantAudioContextItem_Click;
            InstantAudioContextMenu.Items.Add(item);
        }
    }

    private void InstantAudioContextItem_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedInstantRow() is not Row row ||
            sender is not System.Windows.Controls.MenuItem item ||
            item.Tag is not string language)
            return;

        row.AudioLanguage = language;
        PlaylistList.Items.Refresh();
        SaveAutoPlaylistState();
        StatusText.Text = $"INSTANT AUDIO • {language}";
    }

    private void SetNext_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is not Row r) return;
        _nextFile = r.FilePath; _nextAudioLanguage = r.AudioLanguage; _nextStartSeconds = r.InPointSeconds; _nextEndSeconds = r.OutPointSeconds > r.InPointSeconds ? r.OutPointSeconds : null; NextText.Text = r.Name; StatusText.Text = $"NEXT SET • {r.AudioLanguage}";
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        int i = PlaylistList.SelectedIndex; if (i <= 0) return;
        _items.Move(i, i - 1); PlaylistList.SelectedIndex = i - 1; RefreshRows();
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        int i = PlaylistList.SelectedIndex; if (i < 0 || i >= _items.Count - 1) return;
        _items.Move(i, i + 1); PlaylistList.SelectedIndex = i + 1; RefreshRows();
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        var rnd = new Random();
        var shuffled = _items.OrderBy(_ => rnd.Next()).ToList();
        _items.Clear(); foreach (var x in shuffled) _items.Add(x); RefreshRows();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Row r) _items.Remove(r); RefreshRows();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _items.Clear(); _nextFile = null; NextText.Text = "—"; RefreshRows();
    }

    private void RefreshOnAirContentList()
    {
        string key = _activeScheduledJob != null
            ? "S:" + _activeScheduledJob.When.Ticks + ":" + string.Join("|", _activeScheduledJob.Files)
            : _fillerActive ? "F:" + string.Join("|", _fillerFiles)
            : _programFile != null ? "I:" + string.Join("|", _items.Select(x => x.FilePath))
            : "IDLE";
        if (key == _dashboardListKey) return;
        _dashboardListKey = key;
        _onAirItems.Clear();
        if (_activeScheduledJob != null && _activeScheduledJob.Files.Count > 0)
        {
            for (int i=0;i<_activeScheduledJob.Files.Count;i++)
            {
                var f=_activeScheduledJob.Files[i];
                double ip=i<_activeScheduledJob.InPoints.Count?_activeScheduledJob.InPoints[i]:0;
                double op=i<_activeScheduledJob.OutPoints.Count?_activeScheduledJob.OutPoints[i]:0;
                double du=i<_activeScheduledJob.Durations.Count?_activeScheduledJob.Durations[i]:Math.Max(0,op-ip);
                _onAirItems.Add(new OnAirItem{Index=i+1,FilePath=f,Description=i<_activeScheduledJob.Descriptions.Count && !string.Equals(_activeScheduledJob.Descriptions[i],Path.GetFileNameWithoutExtension(f),StringComparison.OrdinalIgnoreCase)?_activeScheduledJob.Descriptions[i]:"",In=ip,Out=op,Duration=du,Audio=i<_activeScheduledJob.AudioLanguages.Count?_activeScheduledJob.AudioLanguages[i]:_activeScheduledJob.AudioLanguage});
            }
        }
        else if (_fillerActive)
        {
            for(int i=0;i<_fillerFiles.Count;i++) _onAirItems.Add(new OnAirItem{Index=i+1,FilePath=_fillerFiles[i],Description="Filler",Audio=AudioLanguageHelper.DefaultLanguage});
        }
        else
        {
            for(int i=0;i<_items.Count;i++){var r=_items[i];_onAirItems.Add(new OnAirItem{Index=i+1,FilePath=r.FilePath,Description=r.Description,In=r.InPointSeconds,Out=r.OutPointSeconds,Duration=r.PlayDurationSeconds,Audio=r.AudioLanguage});}
        }
        foreach (var media in _onAirItems) _ = EnrichOnAirItemAsync(media.FilePath);
        if (_programFile != null)
        {
            var current=_onAirItems.FirstOrDefault(x=>string.Equals(x.FilePath,_programFile,StringComparison.OrdinalIgnoreCase));
            if(current!=null){OnAirContentList.SelectedItem=current;OnAirContentList.ScrollIntoView(current);}
        }
    }

    private async void UpdateDashboardMediaInfo(string? file)
    {
        _dashboardMediaFile = file;
        DashSourceDetailsText.Text = string.IsNullOrWhiteSpace(file) ? "No active on-air source." :
            (_breakActive?"BREAK • MANUAL COMMERCIAL / INTERRUPTION":_activeScheduledJob!=null?$"{_activeScheduledJob.Kind} • {_activeScheduledJob.Name} • {_activeScheduledJob.When:dd/MM/yyyy hh:mm:ss tt} → {_activeScheduledJob.EndTimeText}":_fillerActive?"FILLER • FALLBACK SOURCE":"INSTANT / MANUAL PLAYOUT");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;
        await EnrichOnAirItemAsync(file);
    }

    private async Task EnrichOnAirItemAsync(string file)
    {
        try
        {
            var result = await Task.Run(() => MediaCompatibilityProbe.Probe(file));
            var item = _onAirItems.FirstOrDefault(x => string.Equals(x.FilePath, file, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;
            // Display the actual file container, not FFmpeg's comma-separated alias family.
            string extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            item.Format = string.IsNullOrWhiteSpace(extension)
                ? (result.Container.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "—").ToUpperInvariant()
                : extension;
            item.Resolution = result.Width > 0 && result.Height > 0 ? $"{result.Width}×{result.Height}" : "Audio";
            item.VideoCodec = result.VideoCodec;
            string layout = result.AudioChannels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", > 0 => $"{result.AudioChannels}ch", _ => "—" };
            item.AudioInfo = result.AudioTracks.Count == 0
                ? "NO AUDIO"
                : string.Join(" | ", result.AudioTracks.Select((track, index) =>
                {
                    string trackLayout = track.Channels switch { 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", > 0 => $"{track.Channels}ch", _ => "—" };
                    string language = string.IsNullOrWhiteSpace(track.Language) ? "Unknown" : track.Language;
                    string rate = track.SampleRate > 0 ? $" • {track.SampleRate/1000.0:0.#}kHz" : "";
                    return $"#{index + 1} {track.Codec.ToUpperInvariant()} • {trackLayout} • {language}{rate}";
                }));
            item.Fps = result.Fps > 0 ? $"{result.Fps:0.###}" : "—";
            item.MediaDuration = EditorClock(result.DurationSeconds);
            if (string.Equals(file, _programFile, StringComparison.OrdinalIgnoreCase)) AudioChannelLayoutText.Text = $"SOURCE {layout.ToUpperInvariant()}  →  OUTPUT L/R";
            // OnAirItem updates only changed cells; do not rebuild the whole ListView
            // during a TAKE and compete with Main Program frame presentation.
        }
        catch { }
    }

    private static double ToDb(float value) => value <= 0.00001f ? -90 : 20.0 * Math.Log10(value);
    private void UpdateOnAirAudioMeters(float left, float right)
    {
        _onAirLevelL = Math.Max(left, _onAirLevelL * 0.82f); _onAirLevelR = Math.Max(right, _onAirLevelR * 0.82f);
        double ldb = ToDb(_onAirLevelL), rdb = ToDb(_onAirLevelR), peak = Math.Max(ldb, rdb);
        AudioMeterL.Value = Math.Clamp((ldb + 60) / 60 * 100, 0, 100); AudioMeterR.Value = Math.Clamp((rdb + 60) / 60 * 100, 0, 100);
        AudioMeterLText.Text = ldb <= -89 ? "-∞ dB" : $"{ldb:0.0} dB"; AudioMeterRText.Text = rdb <= -89 ? "-∞ dB" : $"{rdb:0.0} dB";
        AudioPeakText.Text = peak <= -89 ? "PEAK -∞ dBFS" : $"PEAK {peak:0.0} dBFS";
    }

    private void OnAirSeekSlider_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _onAirSeekDragging = true;
    private async void OnAirSeekSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_onAirSeekDragging || _programFile == null) return; _onAirSeekDragging = false;
        double seekStart = Math.Max(0, _programClipStartSeconds);
        double seekEnd = _programClipEndSeconds.HasValue && _programClipEndSeconds.Value > seekStart
            ? _programClipEndSeconds.Value
            : Math.Max(seekStart, _program.DurationSeconds);
        double target = seekEnd > seekStart
            ? Math.Clamp(OnAirSeekSlider.Value, seekStart, seekEnd)
            : Math.Max(seekStart, OnAirSeekSlider.Value);
        bool wasPaused = _programAudio.IsPaused;
        _onAirSeekInProgress = true;
        try
        {
            if (_programAudio.IsOpen) { _programAudio.Pause(); _programAudio.Seek(target); }
            await _program.SeekAsync(target);
            double actual = Math.Max(0, _program.PositionSeconds);
            Volatile.Write(ref _programAudioMediaSeconds, actual);
            if (_programAudio.IsOpen) { _programAudio.Seek(actual); if (!wasPaused) _programAudio.Resume(); }
        }
        catch { }
        finally { _onAirSeekInProgress = false; }
    }

    private async Task<System.Windows.Media.ImageSource?> LoadStaticThumbnailAsync(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return null;
        NativeVideoPlayer? thumb = null;
        try
        {
            thumb = new NativeVideoPlayer { HardwareDecodeEnabled = _hardwareDecodeEnabled }; thumb.EnsureFFmpegInitialized(); thumb.SetRenderTarget(160, 90);
            var tcs = new TaskCompletionSource<System.Windows.Media.ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
            thumb.FrameReady += f => tcs.TrySetResult(f);
            await thumb.OpenAsync(file); await thumb.PlayAsync();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(1800));
            return done == tcs.Task ? await tcs.Task : null;
        }
        catch { return null; }
        finally { thumb?.Dispose(); }
    }

    private async void RefreshNextThumbnail()
    {
        string? file = _nextFile;
        if (string.Equals(file, _nextThumbnailFile, StringComparison.OrdinalIgnoreCase)) return;
        _nextThumbnailFile = file; NextThumbImage.Source = null;
        var image = await LoadStaticThumbnailAsync(file);
        if (string.Equals(file, _nextFile, StringComparison.OrdinalIgnoreCase)) NextThumbImage.Source = image;
    }

    private void UpdateBroadcastStatusStrip(double pos, double effectiveEnd, double effectiveRem)
    {
        var localNow = DateTime.Now;
        StatusClockText.Text = localNow.ToString("hh:mm:ss tt");
        StatusDateText.Text = localNow.ToString("dddd, dd MMM yyyy");
        StatusElapsedText.Text = Clock(pos);
        StatusRemainingText.Text = $"REM {Clock(effectiveRem)}";
        StatusTotalText.Text = $"  TOT {Clock(effectiveEnd)}";
        StatusPlayProgress.Value = effectiveEnd > 0 ? Math.Clamp(pos / effectiveEnd * 100.0, 0, 100) : 0;
        StatusNowNextText.Text = $"NOW: {(string.IsNullOrWhiteSpace(NowText.Text) ? "—" : NowText.Text)}";
        StatusScheduleLineText.Text = $"NEXT: {(string.IsNullOrWhiteSpace(NextText.Text) ? "—" : NextText.Text)}";

        var wallNow = DateTime.UtcNow;
        var cpuNow = _uiProcess.TotalProcessorTime;
        var wallMs = (wallNow - _lastCpuWallTime).TotalMilliseconds;
        if (wallMs >= 500)
        {
            var cpuMs = (cpuNow - _lastCpuProcessTime).TotalMilliseconds;
            var pct = wallMs > 0 ? cpuMs / (wallMs * Math.Max(1, Environment.ProcessorCount)) * 100.0 : 0;
            pct = Math.Clamp(pct, 0, 100);
            StatusCpuText.Text = $"{pct:0}%";
            StatusCpuBar.Value = pct;
            _lastCpuWallTime = wallNow;
            _lastCpuProcessTime = cpuNow;
        }
    }

    private void UpdateDashboardStatus()
    {
        string source = _fillerActive ? "FILLER" : _activeScheduledJob != null ? "SCHEDULE" : _programFile != null ? "MANUAL / INSTANT" : "IDLE";
        CurrentSourceText.Text = source;
        NavCurrentSourceText.Text = source;
        CurrentSourceText.Foreground = source == "FILLER" ? System.Windows.Media.Brushes.Gold : source == "SCHEDULE" ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Cyan;
        NavCurrentSourceText.Foreground = CurrentSourceText.Foreground;
        StatusNextTypeText.Text = _activeScheduledJob != null ? "SCHEDULE / QUEUE" : (_nextFile != null ? "PLAY NEXT" : "QUEUE / SCHEDULE");
        StatusNowNextText.Text = $"NOW: {(string.IsNullOrWhiteSpace(NowText.Text) ? "—" : NowText.Text)}";
        StatusScheduleLineText.Text = $"NEXT: {(string.IsNullOrWhiteSpace(NextText.Text) ? "—" : NextText.Text)}";

        if (_activeScheduledJob != null)
            HeaderCurrentScheduleText.Text = $"CURRENT SCHEDULE  {_activeScheduledJob.Name}  •  {_activeScheduledJob.When:hh:mm:ss tt}";
        else
            HeaderCurrentScheduleText.Text = "CURRENT SCHEDULE  —";

        var next = _schedule.Where(x => !x.Fired && x.When > DateTime.Now).OrderBy(x => x.When).FirstOrDefault();
        HeaderNextScheduleText.Text = next == null ? "NEXT SCHEDULE  —" : $"NEXT SCHEDULE  {next.Name}  •  {next.When:dd/MM hh:mm:ss tt}";

        var currentMeta = GetOnAirHeaderMeta(_programFile);
        NowProgramInfoText.Text = $"PROGRAM  {currentMeta.Program}";
        NowDescriptionText.Text = $"DESC  {currentMeta.Description}";
        NowAudioText.Text = $"AUDIO  {currentMeta.Audio}";
        var nextMeta = GetOnAirHeaderMeta(_nextFile, true);
        NextProgramInfoText.Text = $"PROGRAM  {nextMeta.Program}";
        NextDescriptionText.Text = $"DESC  {nextMeta.Description}";
        NextAudioText.Text = $"AUDIO  {nextMeta.Audio}";

        FillerStatusText.Text = _fillerActive ? "FILLER  ACTIVE" : $"FILLER  STANDBY  •  {_fillerFiles.Count} ITEM(S)";
        FillerStatusText.Foreground = _fillerActive ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Gold;
        RefreshOnAirContentList();
        RefreshNextThumbnail();
        if (!string.Equals(_dashboardMediaFile, _programFile, StringComparison.OrdinalIgnoreCase)) UpdateDashboardMediaInfo(_programFile);
    }

    private (string Program, string Description, string Audio) GetOnAirHeaderMeta(string? file, bool nextItem = false)
    {
        if (string.IsNullOrWhiteSpace(file)) return ("—", "—", "—");
        string program = _activeScheduledJob?.ProgramName;
        if (string.IsNullOrWhiteSpace(program)) program = _activeScheduledJob?.Name;
        if (string.IsNullOrWhiteSpace(program)) program = _fillerActive ? "FILLER" : "INSTANT / MANUAL";

        string description = Path.GetFileNameWithoutExtension(file);
        string audio = nextItem ? _nextAudioLanguage : _programAudioLanguage;

        if (_activeScheduledJob != null)
        {
            int i = _activeScheduledJob.Files.FindIndex(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (i < _activeScheduledJob.Descriptions.Count && !string.IsNullOrWhiteSpace(_activeScheduledJob.Descriptions[i])) description = _activeScheduledJob.Descriptions[i];
                if (i < _activeScheduledJob.AudioLanguages.Count && !string.IsNullOrWhiteSpace(_activeScheduledJob.AudioLanguages[i])) audio = _activeScheduledJob.AudioLanguages[i];
            }
        }
        else
        {
            var row = _items.FirstOrDefault(x => string.Equals(x.FilePath, file, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                if (!string.IsNullOrWhiteSpace(row.Description)) description = row.Description;
                if (!string.IsNullOrWhiteSpace(row.AudioLanguage)) audio = row.AudioLanguage;
            }
        }
        if (string.IsNullOrWhiteSpace(audio)) audio = AudioLanguageHelper.DefaultLanguage;
        return (program ?? "—", description, audio);
    }

    private void MasterMute_Click(object sender, RoutedEventArgs e)
    {
        _masterMuted = !_masterMuted;
        _programAudio.SetMuted(_masterMuted);
        MasterMuteButton.Content = _masterMuted ? "\uE74F" : "\uE767";
        MasterMuteButton.ToolTip = _masterMuted ? "Unmute Master On-Air Audio" : "Mute Master On-Air Audio";
        StatusText.Text = _masterMuted ? "MASTER AUDIO MUTED" : $"MASTER AUDIO • {Math.Round(_masterVolume * 100)}%";
    }

    private void MasterVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _masterVolume = (float)Math.Clamp(e.NewValue / 100.0, 0.0, 1.0);
        _programAudio.SetVolume(_masterVolume);
        if (MasterVolumeText != null) MasterVolumeText.Text = $"{Math.Round(_masterVolume * 100)}%";
    }

    private async void StatusPrevious_Click(object sender, RoutedEventArgs e)
    {
        var item = GetAdjacentActiveSourceItem(-1);
        if (item == null) { StatusText.Text = "NO PREVIOUS ITEM IN ACTIVE SOURCE"; return; }
        await TakeToAirAtAsync(item.Value.File, item.Value.InPoint, item.Value.Audio, item.Value.OutPoint);
    }

    private async void StatusNext_Click(object sender, RoutedEventArgs e)
    {
        var item = GetAdjacentActiveSourceItem(1);
        if (item == null) { StatusText.Text = "NO NEXT ITEM IN ACTIVE SOURCE"; return; }
        await TakeToAirAtAsync(item.Value.File, item.Value.InPoint, item.Value.Audio, item.Value.OutPoint);
    }

    private (string File, double InPoint, string Audio, double? OutPoint)? GetAdjacentActiveSourceItem(int delta)
    {
        if (string.IsNullOrWhiteSpace(_programFile)) return null;
        if (_activeScheduledJob != null && _activeScheduledJob.Files.Count > 0)
        {
            int i = _activeScheduledJob.Files.FindIndex(x => string.Equals(x, _programFile, StringComparison.OrdinalIgnoreCase));
            int n = i + delta;
            if (i >= 0 && n >= 0 && n < _activeScheduledJob.Files.Count)
            {
                double ip = n < _activeScheduledJob.InPoints.Count ? Math.Max(0, _activeScheduledJob.InPoints[n]) : 0;
                double op = n < _activeScheduledJob.OutPoints.Count ? _activeScheduledJob.OutPoints[n] : 0;
                string audio = n < _activeScheduledJob.AudioLanguages.Count && !string.IsNullOrWhiteSpace(_activeScheduledJob.AudioLanguages[n]) ? _activeScheduledJob.AudioLanguages[n] : AudioLanguageHelper.DefaultLanguage;
                return (_activeScheduledJob.Files[n], ip, audio, op > ip ? op : null);
            }
        }
        if (_fillerActive && _fillerFiles.Count > 0)
        {
            int i = _fillerFiles.FindIndex(x => string.Equals(x, _programFile, StringComparison.OrdinalIgnoreCase));
            int n = i + delta;
            if (i >= 0 && n >= 0 && n < _fillerFiles.Count) return (_fillerFiles[n], 0, AudioLanguageHelper.DefaultLanguage, null);
        }
        int rowIndex = _items.ToList().FindIndex(x => string.Equals(x.FilePath, _programFile, StringComparison.OrdinalIgnoreCase));
        int nextIndex = rowIndex + delta;
        if (rowIndex >= 0 && nextIndex >= 0 && nextIndex < _items.Count)
        {
            var row = _items[nextIndex];
            return (row.FilePath, row.InPointSeconds, row.AudioLanguage, row.OutPointSeconds > row.InPointSeconds ? row.OutPointSeconds : null);
        }
        return null;
    }

    internal async Task StartLiveInputAsync(string ffmpeg,string videoDevice,string? audioDevice)
    {
        if(!_onAirEnabled) return;
        _scheduledPlaylistActive=false; _activeScheduledJob=null; _activeScheduleEnd=null;
        _program.Stop(); _programAudio.Stop();
        _masterAvBus.BeginProgramTransition(); _deckLinkBusSession?.BeginProgramTransition();
        _masterAvBus.ResetTransitionAudio(); _deckLinkBusSession?.ResetTransitionAudio();
        _liveInputActive=true; _programFile=null; _fillerActive=false; _operatorStopped=false;
        ProgramStateOverlay.Visibility=Visibility.Collapsed;
        ProgramNameText.Text="LIVE INPUT"; NowText.Text="LIVE INPUT"; NextText.Text="—";
        try
        {
            await _liveCapture.StartAsync(ffmpeg,videoDevice,audioDevice,1280,720,25);
            _masterAvBus.EndProgramTransition(); _deckLinkBusSession?.EndProgramTransition();
            StatusText.Text="LIVE INPUT • ON AIR";
            _ = RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped:true);
        }
        catch
        {
            _liveInputActive=false; _masterAvBus.EndProgramTransition(); _deckLinkBusSession?.EndProgramTransition(); throw;
        }
    }

    internal async Task StartLiveDeckLinkInputAsync(string ffmpeg,string deckLinkDevice,string? audioDevice)
    {
        if(!_onAirEnabled) return;
        _scheduledPlaylistActive=false; _activeScheduledJob=null; _activeScheduleEnd=null;
        _program.Stop(); _programAudio.Stop();
        _masterAvBus.BeginProgramTransition(); _deckLinkBusSession?.BeginProgramTransition();
        _masterAvBus.ResetTransitionAudio(); _deckLinkBusSession?.ResetTransitionAudio();
        _liveInputActive=true; _programFile=null; _fillerActive=false; _operatorStopped=false;
        ProgramStateOverlay.Visibility=Visibility.Collapsed;
        ProgramNameText.Text="DECKLINK LIVE INPUT"; NowText.Text="DECKLINK LIVE INPUT"; NextText.Text="—";
        try
        {
            await _liveCapture.StartDeckLinkAsync(ffmpeg,deckLinkDevice,audioDevice,1280,720,25);
            _masterAvBus.EndProgramTransition(); _deckLinkBusSession?.EndProgramTransition();
            StatusText.Text="DECKLINK LIVE INPUT • ON AIR";
            _ = RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped:true);
        }
        catch
        {
            _liveInputActive=false; _masterAvBus.EndProgramTransition(); _deckLinkBusSession?.EndProgramTransition(); throw;
        }
    }
    internal async Task StartLiveNetworkInputAsync(string ffmpeg,string sourceUrl,string? audioDevice,string? userAgent=null,string? referer=null)
    {
        if(!_onAirEnabled)return;
        _scheduledPlaylistActive=false;_activeScheduledJob=null;_activeScheduleEnd=null;_program.Stop();_programAudio.Stop();
        _masterAvBus.BeginProgramTransition();_deckLinkBusSession?.BeginProgramTransition();_masterAvBus.ResetTransitionAudio();_deckLinkBusSession?.ResetTransitionAudio();
        _liveInputActive=true;_programFile=null;_fillerActive=false;_operatorStopped=false;ProgramStateOverlay.Visibility=Visibility.Collapsed;
        ProgramNameText.Text="NETWORK LIVE INPUT";NowText.Text="NETWORK LIVE INPUT";NextText.Text="—";
        try{await _liveCapture.StartNetworkAsync(ffmpeg,sourceUrl,audioDevice,1280,720,25,userAgent,referer);_masterAvBus.EndProgramTransition();_deckLinkBusSession?.EndProgramTransition();StatusText.Text="NETWORK LIVE INPUT • ON AIR";_=RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped:true);}
        catch{_liveInputActive=false;_masterAvBus.EndProgramTransition();_deckLinkBusSession?.EndProgramTransition();throw;}
    }
    internal async Task StartLiveNdiInputAsync(string ffmpeg,string sourceName,string? audioDevice)
    {
        if(!_onAirEnabled)return;_scheduledPlaylistActive=false;_activeScheduledJob=null;_activeScheduleEnd=null;_program.Stop();_programAudio.Stop();
        _masterAvBus.BeginProgramTransition();_deckLinkBusSession?.BeginProgramTransition();_masterAvBus.ResetTransitionAudio();_deckLinkBusSession?.ResetTransitionAudio();
        _liveInputActive=true;_programFile=null;_fillerActive=false;_operatorStopped=false;ProgramStateOverlay.Visibility=Visibility.Collapsed;ProgramNameText.Text="NDI LIVE INPUT";NowText.Text="NDI LIVE INPUT";NextText.Text="—";
        try{await _liveCapture.StartNdiAsync(ffmpeg,sourceName,audioDevice,1280,720,25);_masterAvBus.EndProgramTransition();_deckLinkBusSession?.EndProgramTransition();StatusText.Text="NDI LIVE INPUT • ON AIR";_=RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped:true);}
        catch{_liveInputActive=false;_masterAvBus.EndProgramTransition();_deckLinkBusSession?.EndProgramTransition();throw;}
    }
    internal async Task StartLiveSpecialInputAsync(string ffmpeg,string sourceKind,string? audioDevice)
    {
        if(!_onAirEnabled)return;_scheduledPlaylistActive=false;_activeScheduledJob=null;_activeScheduleEnd=null;_program.Stop();_programAudio.Stop();
        _masterAvBus.BeginProgramTransition();_deckLinkBusSession?.BeginProgramTransition();_masterAvBus.ResetTransitionAudio();_deckLinkBusSession?.ResetTransitionAudio();
        _liveInputActive=true;_programFile=null;_fillerActive=false;_operatorStopped=false;ProgramStateOverlay.Visibility=Visibility.Collapsed;
        ProgramNameText.Text=sourceKind.ToUpperInvariant();NowText.Text=sourceKind.ToUpperInvariant();NextText.Text="—";
        try{if(sourceKind.Equals("Screen Capture",StringComparison.OrdinalIgnoreCase))await _liveCapture.StartScreenAsync(ffmpeg,audioDevice,1280,720,25);else await _liveCapture.StartTestPatternAsync(ffmpeg,audioDevice,1280,720,25);_masterAvBus.EndProgramTransition();_deckLinkBusSession?.EndProgramTransition();StatusText.Text=sourceKind.ToUpperInvariant()+" • ON AIR";_=RestartArmedStreamsForCurrentProgramAsync(onlyIfStopped:true);}
        catch{_liveInputActive=false;_masterAvBus.EndProgramTransition();_deckLinkBusSession?.EndProgramTransition();throw;}
    }

    private void QueueLiveFrame(BitmapSource frame)
    {
        Volatile.Write(ref _latestLiveSourceFrame,frame);
        if(Interlocked.Exchange(ref _liveFrameProcessing,1)!=0)return;
        _=Task.Run(ProcessLatestLiveFrames);
    }

    private void ProcessLatestLiveFrames()
    {
        BitmapSource? processed=null;
        try
        {
            while(Volatile.Read(ref _liveInputActive))
            {
                var latest=Volatile.Read(ref _latestLiveSourceFrame);
                if(latest==null||ReferenceEquals(latest,processed))break;
                processed=latest;
                PresentLiveFrame(latest);
            }
        }
        catch(Exception ex)
        {
            ModuleDiagnostics.Write("LIVE_FRAME_COMPOSITOR",ex);
            Dispatcher.BeginInvoke(new Action(()=>StatusText.Text="LIVE FRAME ERROR • "+ex.Message));
        }
        finally
        {
            Interlocked.Exchange(ref _liveFrameProcessing,0);
            var pending=Volatile.Read(ref _latestLiveSourceFrame);
            if(Volatile.Read(ref _liveInputActive)&&pending!=null&&!ReferenceEquals(pending,processed))QueueLiveFrame(pending);
        }
    }

    private void PresentLiveFrame(BitmapSource frame)
    {
        if(!Volatile.Read(ref _liveInputActive)) return;
        var finalFrame=_finalProgramCompositor.Compose(frame);
        if(!Volatile.Read(ref _liveInputActive))return;
        _latestProgramFrame=finalFrame;
        _masterAvBus.PushVideo(finalFrame); _deckLinkBusSession?.PushVideo(finalFrame); _deckLinkNativeOutput.PushVideo(finalFrame); PublishSharedProgramFrame(finalFrame);
        if(Interlocked.Exchange(ref _liveFrameDispatchPending,1)!=0)return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>
        {
            try
            {
                if(!_liveInputActive)return;
                var latest=_latestProgramFrame;if(latest==null)return;
                ProgramImage.Source=latest;_graphicsOutputWindow?.UpdateFrame(latest);
            }
            finally{Interlocked.Exchange(ref _liveFrameDispatchPending,0);}
        }));
    }

    internal void StopLiveInput()
    {
        if(!_liveInputActive) return;
        _liveInputActive=false;Volatile.Write(ref _latestLiveSourceFrame,null);_liveCapture.Stop();
        ProgramImage.Source=null; ProgramNameText.Text="LIVE STOPPED"; NowText.Text="—";
        StatusText.Text="LIVE INPUT STOPPED";
    }

    internal void ReturnLiveToSchedule()
    {
        StopLiveInput();
        ReturnToSchedule_Click(this,new RoutedEventArgs());
    }

    private void ShowWorkspace(string name)
    {
        OnAirWorkspace.Visibility = name == "ON AIR" ? Visibility.Visible : Visibility.Collapsed;
        InstantWorkspace.Visibility = name == "INSTANT" ? Visibility.Visible : Visibility.Collapsed;
        ProgramWorkspaceHost.Visibility = name == "PROGRAM" ? Visibility.Visible : Visibility.Collapsed;
        SchedulerWorkspace.Visibility = name == "SCHEDULER" ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspace.Visibility = name == "SETTINGS" ? Visibility.Visible : Visibility.Collapsed;
        ScreenWorkspace.Visibility = name == "SCREEN" ? Visibility.Visible : Visibility.Collapsed;
        StreamWorkspace.Visibility = name == "STREAM" ? Visibility.Visible : Visibility.Collapsed;
        OutputWorkspace.Visibility = name == "OUTPUT" ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = name == "ON AIR" ? "MAIN" : name;
        if (name == "ON AIR") { RefreshOnAirContentList(); UpdateDashboardMediaInfo(_programFile); }
    }

    private void DashboardNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("ON AIR");
    private void InstantNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("INSTANT");
    private void SchedulerNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("SCHEDULER");
    private void ScheduleListNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("SCHEDULER");
    private void CgNav_Click(object sender, RoutedEventArgs e)
    {
        EnsureModuleWorker(ModuleKind.CgStudio);
        OpenOperatorWindow(() => new BroadcastStudioWindow(this), "CG_DESIGNER");
    }
    private void MixerNav_Click(object sender, RoutedEventArgs e) { var w=new AudioMixerWindow(this){Owner=this}; w.Show(); }
    private void LiveNav_Click(object sender, RoutedEventArgs e) { EnsureModuleWorker(ModuleKind.PlayoutEngine); OpenOperatorWindow(() => new PlayoutParityWindow(), "LIVE_INPUT"); }
    private void BreakNav_Click(object sender, RoutedEventArgs e) { var w=new BreakManagerWindow(this){Owner=this}; w.Show(); }
    private void KeyerNav_Click(object sender, RoutedEventArgs e) { EnsureModuleWorker(ModuleKind.ChromaStudio); OpenOperatorWindow(() => new ChromaKeyWindow(this), "CHROMA_KEY"); }
    private readonly Dictionary<string,Window> _operatorWindows = new(StringComparer.OrdinalIgnoreCase);
    private void OpenOperatorWindow(Func<Window> create, string module)
    {
        try
        {
            if(_operatorWindows.TryGetValue(module,out var existing))
            {
                if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;
                existing.Activate();existing.Focus();
                StatusText.Text=module.Replace('_',' ') + " • ALREADY OPEN";
                return;
            }
            var w=create();w.Owner=this;_operatorWindows[module]=w;
            w.Closed+=(_,__)=>{if(_operatorWindows.TryGetValue(module,out var current)&&ReferenceEquals(current,w))_operatorWindows.Remove(module);};
            try{w.Show();}catch{_operatorWindows.Remove(module);throw;}
            StatusText.Text=module.Replace('_',' ') + " • READY";
        }
        catch(Exception ex)
        {
            StatusText.Text=module.Replace('_',' ') + " • OPEN FAILED • " + ex.Message;
            ModuleDiagnostics.ShowError(module,"OPEN",ex,this);
        }
    }
    internal BitmapSource? GetStudioPreviewFrame()=>_latestProgramFrame;
    internal void ApplyStudioCgText(string text,double x,double y,double size)
    {
        _finalProgramCompositor.SetText("studio.cg.primary", text ?? "", Math.Clamp(x,0,1), Math.Clamp(y,0,1), Math.Clamp(size,8,180), System.Windows.Media.Brushes.White, 100);
        _=MirrorModuleCommandAsync(ModuleKind.CgStudio,"UPSERT_LAYER",new{id="studio.cg.primary",type="text",text,x,y,size,z=100});
        StatusText.Text="CG • FINAL PROGRAM LAYER ACTIVE";
    }
    internal void ClearStudioCg(){_finalProgramCompositor.Remove("studio.cg.primary");_=MirrorModuleCommandAsync(ModuleKind.CgStudio,"REMOVE_LAYER",new{id="studio.cg.primary"});StatusText.Text="CG • CLEARED";}
    internal void ApplyStudioSecondaryText(string text,double x,double y,double size)
    {
        _finalProgramCompositor.SetText("studio.cg.secondary", text ?? "", Math.Clamp(x,0,1), Math.Clamp(y,0,1), Math.Clamp(size,8,180), System.Windows.Media.Brushes.White, 110);
        StatusText.Text="CG • SECONDARY LAYER ACTIVE";
    }
    internal void ClearStudioSecondaryText()=>_finalProgramCompositor.Remove("studio.cg.secondary");
    internal void ApplyStudioLogo(string file,double x,double y,double w,double h,double opacity,System.Windows.Media.Stretch stretch)
    {
        if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new FileNotFoundException("Logo/image file not found.",file);
        var bi=new BitmapImage(); bi.BeginInit(); bi.CacheOption=BitmapCacheOption.OnLoad; bi.UriSource=new Uri(file,UriKind.Absolute); bi.EndInit(); bi.Freeze();
        _finalProgramCompositor.SetImage("studio.logo.primary",bi,new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(w,0.01,1),Math.Clamp(h,0.01,1)),Math.Clamp(opacity,0,1),90,stretch);
        _=MirrorModuleCommandAsync(ModuleKind.CgStudio,"UPSERT_LAYER",new{id="studio.logo.primary",type="image",file,x,y,w,h,opacity,stretch=stretch.ToString(),z=90});
        StatusText.Text="COMPOSER • IMAGE/LOGO ON FINAL PROGRAM";
    }
    internal void ClearStudioLogo(){_finalProgramCompositor.Remove("studio.logo.primary");_=MirrorModuleCommandAsync(ModuleKind.CgStudio,"REMOVE_LAYER",new{id="studio.logo.primary"});StatusText.Text="COMPOSER • IMAGE/LOGO CLEARED";}
    internal void ApplyStudioWatermark(string text,double x,double y,double size,string font,bool bold,bool italic,
        byte r,byte g,byte b,double opacity,double scale,double rotation,double outline,bool shadow,int z)
    {
        _finalProgramCompositor.SetWatermark("studio.cg.watermark",text??"",Math.Clamp(x,-1,1),Math.Clamp(y,-1,1),
            Math.Clamp(size,8,240),font,bold,italic,System.Windows.Media.Color.FromRgb(r,g,b),Math.Clamp(opacity,0,1),
            Math.Clamp(scale,.2,4),Math.Clamp(rotation,-360,360),Math.Clamp(outline,0,12),shadow,Math.Clamp(z,-500,500));
        _=MirrorModuleCommandAsync(ModuleKind.CgStudio,"UPSERT_LAYER",new{id="studio.cg.watermark",type="watermark",text,x,y,size,font,bold,italic,r,g,b,opacity,scale,rotation,outline,shadow,z});
        StatusText.Text="CG • TEXT WATERMARK ON FINAL PROGRAM";
    }
    internal void ClearStudioWatermark(){_finalProgramCompositor.Remove("studio.cg.watermark");_=MirrorModuleCommandAsync(ModuleKind.CgStudio,"REMOVE_LAYER",new{id="studio.cg.watermark"});StatusText.Text="CG • TEXT WATERMARK CLEARED";}
    private void RestoreStudioWatermarkIfAlwaysOn()
    {
        try
        {
            string path=File.Exists(CgDesignerProfilePath)?CgDesignerProfilePath:LegacyCgDesignerProfilePath;
            if(!File.Exists(path))return;
            var state=JsonSerializer.Deserialize<StudioWatermarkPersistState>(File.ReadAllText(path));
            if(state?.WatermarkAlwaysOn!=true||string.IsNullOrWhiteSpace(state.WatermarkText))return;
            System.Windows.Media.Color color;
            try{color=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(state.WatermarkColor);}
            catch{color=System.Windows.Media.Colors.White;}
            ApplyStudioWatermark(state.WatermarkText,state.WatermarkX,state.WatermarkY,state.WatermarkSize,state.WatermarkFont,
                state.WatermarkBold,state.WatermarkItalic,color.R,color.G,color.B,state.WatermarkOpacity,state.WatermarkScale,
                state.WatermarkRotation,state.WatermarkOutline,state.WatermarkShadow,state.WatermarkZ);
            ModuleDiagnostics.WriteInfo("CG_WATERMARK","Always-on text watermark restored from saved CG profile.");
        }
        catch(Exception ex){ModuleDiagnostics.Write("CG_WATERMARK_RESTORE",ex);}
    }
    internal void ApplyStudioLowerThird(string text,double x,double y,double w,double h,double size)
    {
        _finalProgramCompositor.SetLowerThird("studio.cg.lowerthird",text??"",new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(w,.05,1),Math.Clamp(h,.03,.5)),Math.Clamp(size,8,120),100);
        StatusText.Text="CG • LOWER THIRD ACTIVE";
    }
    internal void ApplyStudioLowerThirdAdvanced(string text,string secondary,double x,double y,double w,double h,double size,string font,
        byte textR,byte textG,byte textB,byte bgR,byte bgG,byte bgB,double bgOpacity,string animationIn,string animationOut,double duration,int z)
    {
        _finalProgramCompositor.SetLowerThirdAdvanced("studio.cg.lowerthird",text??"",secondary??"",new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(w,.05,1),Math.Clamp(h,.03,.5)),Math.Clamp(size,8,160),font,
            System.Windows.Media.Color.FromRgb(textR,textG,textB),System.Windows.Media.Color.FromRgb(bgR,bgG,bgB),Math.Clamp(bgOpacity,0,1),animationIn,animationOut,Math.Clamp(duration,.05,10),Math.Clamp(z,-500,500));
        _=MirrorModuleCommandAsync(ModuleKind.CgStudio,"UPSERT_LAYER",new{id="studio.cg.lowerthird",type="lowerThird",text,secondary,x,y,w,h,size,font,textR,textG,textB,bgR,bgG,bgB,bgOpacity,animationIn,animationOut,duration,z});
        StatusText.Text="CG • ADVANCED LOWER THIRD ACTIVE";
    }
    internal void RemoveStudioLowerThirdAnimated()=>_finalProgramCompositor.BeginLowerThirdOut("studio.cg.lowerthird");
    internal void SetStudioLayerVisible(string id,bool visible)=>_finalProgramCompositor.SetLayerVisible(id,visible);
    internal void SetStudioLayerZ(string id,int z)=>_finalProgramCompositor.SetLayerZ(id,z);
    internal bool DuplicateStudioLayer(string sourceId,string destinationId,int z)=>_finalProgramCompositor.DuplicateLayer(sourceId,destinationId,z);
    internal void ClearStudioLayer(string id)=>_finalProgramCompositor.Remove(id);
    internal void ClearAllStudioCg()
    {
        foreach(var id in new[]{"studio.cg.primary","studio.cg.secondary","studio.cg.lowerthird","studio.logo.primary","studio.cg.watermark","studio.cg.ticker","studio.cg.roll","studio.cg.clock","studio.cg.timer","studio.cg.stopwatch","studio.cg.now","studio.cg.next","studio.cg.shape","studio.cg.fullscreen","studio.image.sequence"})_finalProgramCompositor.Remove(id);
        _=MirrorModuleCommandAsync(ModuleKind.CgStudio,"CLEAR_ALL",new{});
        _finalProgramCompositor.SetLShape(false,new Rect(0,0,1,1));StopStudioPip();StatusText.Text="CG • ALL DESIGNER LAYERS CLEARED";
    }
    internal void ApplyStudioTicker(string text,double y,double size,double speed)
    {
        _finalProgramCompositor.SetMovingText("studio.cg.ticker",text??"",Math.Clamp(y,0,1),Math.Clamp(size,8,100),Math.Clamp(speed,.01,1),false,120); StatusText.Text="CG • TICKER ACTIVE";
    }
    internal void ApplyStudioRoll(string text,double x,double size,double speed)
    {
        _finalProgramCompositor.SetMovingText("studio.cg.roll",text??"",Math.Clamp(x,0,1),Math.Clamp(size,8,100),Math.Clamp(speed,.01,1),true,120); StatusText.Text="CG • VERTICAL ROLL ACTIVE";
    }
    internal void ApplyStudioClock(double x,double y,double size)
    {
        _finalProgramCompositor.SetClock("studio.cg.clock",Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(size,8,120),130); StatusText.Text="CG • CLOCK ACTIVE";
    }
    internal void ApplyStudioTimer(double x,double y,double size,bool stopwatch)
    {
        _finalProgramCompositor.SetTimer(stopwatch?"studio.cg.stopwatch":"studio.cg.timer",Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(size,8,120),stopwatch,135); StatusText.Text=stopwatch?"CG • STOPWATCH ACTIVE":"CG • TIMER ACTIVE";
    }
    internal void ClearStudioFullScreen()=>_finalProgramCompositor.Remove("studio.cg.fullscreen");
    internal void ClearStudioAdvancedCg()
    {
        foreach(var id in new[]{"studio.cg.lowerthird","studio.cg.ticker","studio.cg.roll","studio.cg.clock","studio.cg.timer","studio.cg.stopwatch","studio.cg.now","studio.cg.next","studio.cg.fullscreen"}) _finalProgramCompositor.Remove(id);
        StatusText.Text="CG • ADVANCED LAYERS CLEARED";
    }
    internal void ApplyStudioNowNext(string now,string next,double x,double y,double size)
    {
        _finalProgramCompositor.SetLowerThird("studio.cg.now",$"NOW  {now}",new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),.55,.075),Math.Clamp(size,8,90),140);
        _finalProgramCompositor.SetLowerThird("studio.cg.next",$"NEXT  {next}",new Rect(Math.Clamp(x,0,1),Math.Clamp(y+.08,0,1),.55,.065),Math.Clamp(size*.78,8,80),141); StatusText.Text="CG • NOW/NEXT ACTIVE";
    }
    internal void ApplyStudioShape(double x,double y,double w,double h,byte r,byte g,byte b,double opacity)
    { _finalProgramCompositor.SetShape("studio.cg.shape",new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(w,.01,1),Math.Clamp(h,.01,1)),System.Windows.Media.Color.FromRgb(r,g,b),Math.Clamp(opacity,0,1),80); StatusText.Text="CG • SHAPE ACTIVE"; }
    internal void ApplyStudioLShape(bool enabled,double x,double y,double w,double h)
    { _finalProgramCompositor.SetLShape(enabled,new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(w,.1,1),Math.Clamp(h,.1,1))); StatusText.Text=enabled?"CG • L-SHAPE ACTIVE":"CG • L-SHAPE OFF"; }
    internal void ApplyStudioFullScreenGraphic(string file,double opacity)
    {
        if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new FileNotFoundException("Full-screen graphic not found.",file);
        var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.UriSource=new Uri(file,UriKind.Absolute);bi.EndInit();bi.Freeze();
        _finalProgramCompositor.SetImage("studio.cg.fullscreen",bi,new Rect(0,0,1,1),Math.Clamp(opacity,0,1),200); StatusText.Text="CG • FULL SCREEN GRAPHIC ACTIVE";
    }

    internal async Task StartStudioPipAsync(string file,double x,double y,double w,double h,double opacity,bool loop)
    {
        if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new FileNotFoundException("PIP video file not found.",file);
        _studioPipX=Math.Clamp(x,0,1);_studioPipY=Math.Clamp(y,0,1);_studioPipW=Math.Clamp(w,.05,1);_studioPipH=Math.Clamp(h,.05,1);_studioPipOpacity=Math.Clamp(opacity,0,1);_studioPipLoop=loop;_studioPipFile=file;
        _studioPipPlayer.HardwareDecodeEnabled=_hardwareDecodeEnabled;
        _studioPipPlayer.SetRenderTarget(640,360);
        await _studioPipPlayer.OpenAsync(file);
        await _studioPipPlayer.PlayAsync();
        StatusText.Text="COMPOSER • MOVING VIDEO PIP ACTIVE";
    }
    internal void StopStudioPip(){_studioPipLoop=false;_studioPipFile=null;_studioPipPlayer.Stop();_studioPipFrame=null;_finalProgramCompositor.Remove("studio.pip.video");StatusText.Text="COMPOSER • PIP CLEARED";}
    internal void ApplyStudioImageSequence(IEnumerable<string> files,double x,double y,double w,double h,double opacity,double fps,bool loop)
    {
        var frames=new List<BitmapSource>();
        foreach(var file in files.Where(File.Exists))
        {
            var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.UriSource=new Uri(file,UriKind.Absolute);bi.EndInit();bi.Freeze();frames.Add(bi);
        }
        if(frames.Count==0)throw new InvalidOperationException("No valid image-sequence frames were selected.");
        _finalProgramCompositor.SetImageSequence("studio.image.sequence",frames,new Rect(Math.Clamp(x,0,1),Math.Clamp(y,0,1),Math.Clamp(w,.05,1),Math.Clamp(h,.05,1)),Math.Clamp(opacity,0,1),Math.Clamp(fps,.1,60),loop,150);
        StatusText.Text=$"COMPOSER • IMAGE SEQUENCE ACTIVE • {frames.Count} FRAMES";
    }
    internal void ClearStudioImageSequence(){_finalProgramCompositor.Remove("studio.image.sequence");StatusText.Text="COMPOSER • IMAGE SEQUENCE CLEARED";}

    internal void ConfigureStudioChromaKey(bool enabled,byte r,byte g,byte b,double tolerance,double softness)
    {
        _finalProgramCompositor.SetChromaKey(enabled,System.Windows.Media.Color.FromRgb(r,g,b),tolerance,softness);
        _=MirrorModuleCommandAsync(ModuleKind.ChromaStudio,enabled?"SET_KEY":"CLEAR_KEY",new{enabled,r,g,b,tolerance,softness});
        StatusText.Text=enabled?"CHROMA KEY • FINAL PROGRAM ACTIVE":"CHROMA KEY • OFF";
    }
    internal void ConfigureStudioChromaKeyAdvanced(bool enabled,byte r,byte g,byte b,double tolerance,double softness,
        double spill,double dullness,double spectralBalance,double edgeSoftness,double erodeDilate,double shadowRecovery,System.Windows.Rect transform,
        System.Windows.Thickness crop,double rotation,System.Windows.Media.Stretch stretch)
    {
        _finalProgramCompositor.SetChromaKeyAdvanced(enabled,System.Windows.Media.Color.FromRgb(r,g,b),tolerance,softness,
            spill,dullness,spectralBalance,edgeSoftness,erodeDilate,shadowRecovery,transform,crop,rotation,stretch);
        _=MirrorModuleCommandAsync(ModuleKind.ChromaStudio,enabled?"SET_KEY":"CLEAR_KEY",new{enabled,r,g,b,tolerance,softness,spill,dullness,spectralBalance,edgeSoftness,erodeDilate,shadowRecovery,transform=new{transform.X,transform.Y,transform.Width,transform.Height},crop=new{crop.Left,crop.Top,crop.Right,crop.Bottom},rotation,stretch=stretch.ToString()});
        StatusText.Text=enabled?"CHROMA KEY • ADVANCED KEY ACTIVE":"CHROMA KEY • OFF";
    }
    internal void ApplyStudioKeyBackground(string file)
    {
        if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new FileNotFoundException("Chroma background file not found.",file);
        // Switching from a moving background to a still must stop the old decoder first;
        // otherwise a late video frame could overwrite the selected image layer.
        _studioKeyBackgroundLoop=false; _studioKeyBackgroundFile=null; _studioKeyBackgroundPlayer.Stop();
        var bi=new BitmapImage(); bi.BeginInit(); bi.CacheOption=BitmapCacheOption.OnLoad; bi.UriSource=new Uri(file,UriKind.Absolute); bi.EndInit(); bi.Freeze();
        _finalProgramCompositor.SetImage("studio.key.background",bi,new Rect(0,0,1,1),1,-100); StatusText.Text="CHROMA KEY • BACKGROUND READY";
        _=MirrorModuleCommandAsync(ModuleKind.ChromaStudio,"SET_BACKGROUND",new{type="image",file,audio=false});
        StudioKeyBackgroundFrameChanged?.Invoke(bi);
    }
    internal async Task ApplyStudioKeyBackgroundVideoAsync(string file,bool loop=true)
    {
        if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new FileNotFoundException("Chroma background video not found.",file);
        _studioKeyBackgroundLoop=false; _studioKeyBackgroundPlayer.Stop();
        _studioKeyBackgroundFile=file; _studioKeyBackgroundLoop=loop;
        try
        {
            _studioKeyBackgroundPlayer.HardwareDecodeEnabled=_hardwareDecodeEnabled;
            _studioKeyBackgroundPlayer.SetRenderTarget(1280,720);
            await _studioKeyBackgroundPlayer.OpenAsync(file); await _studioKeyBackgroundPlayer.PlayAsync();
            _=MirrorModuleCommandAsync(ModuleKind.ChromaStudio,"SET_BACKGROUND",new{type="video",file,loop,audio=false});
            StatusText.Text="CHROMA KEY • MUTED BACKGROUND VIDEO ACTIVE";
        }
        catch
        {
            _studioKeyBackgroundLoop=false; _studioKeyBackgroundFile=null; _studioKeyBackgroundPlayer.Stop();
            throw;
        }
    }
    internal void ClearStudioKeyBackground(){_studioKeyBackgroundLoop=false;_studioKeyBackgroundFile=null;_studioKeyBackgroundPlayer.Stop();_finalProgramCompositor.Remove("studio.key.background");_=MirrorModuleCommandAsync(ModuleKind.ChromaStudio,"CLEAR_BACKGROUND",new{});}
    internal (double Gain,bool Normalize) GetStudioAudioState()=> (MasterGainSlider.Value,AudioNormalizeCheck.IsChecked==true);
    internal void ApplyStudioAudioState(double gain,bool normalize)
    {
        MasterGainSlider.Value=Math.Clamp(gain,-24,12); AudioNormalizeCheck.IsChecked=normalize;
        UpdateAudioProcessingValueLabels(); ApplyOutputProcessingSettings(); SaveOperatorSettings();
        StatusText.Text=$"AUDIO MIXER • MASTER {MasterGainSlider.Value:+0.0;-0.0;0.0} dB • NORMALIZE {(normalize?"ON":"OFF")}";
    }
    internal async Task PlayStudioBreakAsync(string file)
    {
        if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))throw new FileNotFoundException("Break media not found.",file);
        if(string.IsNullOrWhiteSpace(_programFile)||!File.Exists(_programFile)){await TakeToAirAtAsync(file,0,AudioLanguageHelper.DefaultLanguage,null);return;}
        _breakResumeFile=_programFile; _breakResumePosition=Math.Max(0,_program.PositionSeconds); _breakResumeAudio=_programAudioLanguage; _breakResumeEnd=_programClipEndSeconds; _breakActive=true; _dashboardListKey="";
        await TakeToAirAtAsync(file,0,AudioLanguageHelper.DefaultLanguage,null); StatusText.Text="BREAK ON AIR • AUTO RETURN ARMED";
    }
    internal void OpenLiveStudio(){var w=new PlayoutParityWindow{Owner=this};w.Show();}
    internal void OpenAudioProcessing()=>ShowWorkspace("SETTINGS");
    internal void OpenStreamStudio()=>ShowWorkspace("STREAM");

    private void PlayoutPlusNav_Click(object sender, RoutedEventArgs e)
    {
        var w = new PlayoutParityWindow { Owner = this };
        w.ShowDialog();
    }
    private void OutputNav_Click(object sender, RoutedEventArgs e)
    {
        RefreshOutputWorkspace();
        ShowWorkspace("OUTPUT");
        // Navigation is display-only. Never probe hardware or launch FFmpeg merely because
        // an operator opens OUTPUT; cached data is refreshed independently in background.
        if(!_outputCapabilitiesLoaded && DeckLinkOutputStatusText!=null)
            DeckLinkOutputStatusText.Text="DETECTING IN BACKGROUND • PLAYOUT UNAFFECTED";
    }

    private void RefreshOutputWorkspace()
    {
        try
        {
            var screens=System.Windows.Forms.Screen.AllScreens;
            GraphicsMonitorCombo.Items.Clear();
            for(int i=0;i<screens.Length;i++)
            {
                var b=screens[i].Bounds;
                GraphicsMonitorCombo.Items.Add($"DISPLAY {i+1} • {screens[i].DeviceName} • {b.Width}x{b.Height}{(screens[i].Primary?" • PRIMARY":"")}");
            }
            if(GraphicsMonitorCombo.Items.Count>0 && GraphicsMonitorCombo.SelectedIndex<0) GraphicsMonitorCombo.SelectedIndex=Math.Min(1,GraphicsMonitorCombo.Items.Count-1);
            GraphicsOutputStatusText.Text=_graphicsOutputWindow?.IsVisible==true ? "RUNNING • FINAL PROGRAM" : "STOPPED";

            string? selectedBefore=DeckLinkDeviceCombo.SelectedItem?.ToString();
            if(_deckLinkPersistRestorePending && !string.IsNullOrWhiteSpace(_deckLinkPersist.Device)) selectedBefore=_deckLinkPersist.Device;
            _deckLinkUiUpdating=true;
            DeckLinkDeviceCombo.Items.Clear();
            foreach(var dev in _deckLinkDetectedDevices) DeckLinkDeviceCombo.Items.Add(dev);
            if(DeckLinkDeviceCombo.Items.Count>0)
            {
                int keep=-1;
                if(!string.IsNullOrWhiteSpace(selectedBefore))
                    for(int i=0;i<DeckLinkDeviceCombo.Items.Count;i++) if(string.Equals(DeckLinkDeviceCombo.Items[i]?.ToString(),selectedBefore,StringComparison.OrdinalIgnoreCase)){keep=i;break;}
                DeckLinkDeviceCombo.SelectedIndex=keep>=0?keep:0;
            }
            _deckLinkUiUpdating=false;

            bool running=_deckLinkOutput.Running || _deckLinkNativeOutput.Running;
            DeckLinkDeviceCombo.IsEnabled=DeckLinkDeviceCombo.Items.Count>0 && !running;
            bool selectedNative=(DeckLinkDeviceCombo.SelectedItem?.ToString()??"").StartsWith("[NATIVE SDK] ",StringComparison.OrdinalIgnoreCase);
            DeckLinkConnectionCombo.IsEnabled=selectedNative && _deckLinkNativeAvailable && _deckLinkConnections.Count>0 && !running;
            DeckLinkModeCombo.IsEnabled=selectedNative && _deckLinkNativeAvailable && _deckLinkModes.Count>0 && !running;

            bool nativeRouteReady=selectedNative && _deckLinkNativeAvailable && DeckLinkConnectionCombo.SelectedItem is DeckLinkOutputConnection && ResolveDeckLinkModeForAppliedOutput()!=null;
            bool fallbackReady=!selectedNative && !string.IsNullOrWhiteSpace(_deckLinkFfmpegPath) && DeckLinkDeviceCombo.SelectedItem!=null;
            DeckLinkStartButton.IsEnabled=!running && (nativeRouteReady || fallbackReady);
            DeckLinkStopButton.IsEnabled=running;

            if(_deckLinkNativeOutput.Running)
            {
                DeckLinkReadyBadge.Text="●  ON AIR"; DeckLinkReadyBadge.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(105,240,197));
                DeckLinkStatusText.Text="RUNNING • NATIVE SDK • "+(_deckLinkNativeOutput.ActiveDevice??"")+" • "+(_deckLinkNativeOutput.ActiveConnection??"")+" • "+(_deckLinkNativeOutput.ActiveMode??"");
                DeckLinkStatusText.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(105,240,197));
            }
            else if(_deckLinkOutput.Running)
            {
                DeckLinkReadyBadge.Text="●  FALLBACK ON AIR"; DeckLinkReadyBadge.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,211,106));
                DeckLinkStatusText.Text="RUNNING • FFMPEG DECKLINK FALLBACK • "+(_deckLinkOutput.ActiveDevice??"");
                DeckLinkStatusText.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,211,106));
            }
            else if(_deckLinkNativeAvailable && _deckLinkNativeDevices.Count>0)
            {
                DeckLinkReadyBadge.Text=nativeRouteReady?"●  READY":"●  SELECT ROUTE"; DeckLinkReadyBadge.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(nativeRouteReady?105:255),(byte)(nativeRouteReady?240:211),(byte)(nativeRouteReady?197:106)));
                DeckLinkStatusText.Text="READY • NATIVE DECKLINK SDK • "+_deckLinkNativeDevices.Count+" DEVICE(S) • choose physical output + supported mode";
                DeckLinkStatusText.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(105,240,197));
            }
            else if(!string.IsNullOrWhiteSpace(_deckLinkFfmpegPath) && _deckLinkDetectedDevices.Count>0)
            {
                DeckLinkReadyBadge.Text="●  FALLBACK ONLY"; DeckLinkReadyBadge.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,211,106));
                DeckLinkStatusText.Text="FFMPEG DECKLINK FALLBACK AVAILABLE • native card routing not ready";
                DeckLinkStatusText.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,211,106));
            }
            else
            {
                DeckLinkReadyBadge.Text="●  NOT READY"; DeckLinkReadyBadge.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,120,120));
                DeckLinkStatusText.Text="NOT READY • "+_streamCapabilities.DeckLinkProbeMessage;
                DeckLinkStatusText.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,211,106));
            }
            DeckLinkOutputStatusText.Text=_deckLinkNativeOutput.Running?_deckLinkNativeOutput.Status:_deckLinkOutput.Status;
        }
        catch(Exception ex)
        {
            GraphicsOutputStatusText.Text="OUTPUT ENUMERATION ERROR • "+ex.Message;
        }
    }

    private void OutputRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshOutputCapabilitiesAsync(true);
    }

    private async Task RefreshOutputCapabilitiesAsync(bool operatorRequested=true)
    {
        if(_deckLinkOutput.Running || _deckLinkNativeOutput.Running)
        {
            if(operatorRequested) DeckLinkOutputStatusText.Text="RUNNING • HARDWARE REFRESH DEFERRED UNTIL DECKLINK OFF";
            return;
        }
        if(Interlocked.Exchange(ref _outputCapabilityRefreshActive,1)!=0) return;
        try
        {
            if(operatorRequested) DeckLinkOutputStatusText.Text="DETECTING IN BACKGROUND • PROGRAM CLOCK PROTECTED";
            var nativeTask=Task.Run(() =>
            {
                var detected=_deckLinkNativeOutput.Detect();
                var connections=new Dictionary<int,List<DeckLinkOutputConnection>>();
                var modes=new Dictionary<string,List<DeckLinkOutputMode>>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<detected.Devices.Count;i++)
                {
                    var c=_deckLinkNativeOutput.GetConnections(i);
                    connections[i]=c;
                    foreach(var connection in c)
                        modes[$"{i}|{connection.Value}"]=_deckLinkNativeOutput.GetModes(i,connection.Value);
                }
                return (detected,connections,modes);
            });
            var fallbackTask=DeckLinkOutputEngine.DetectAsync(StreamingEngine.FfmpegPath);
            var snapshot=await nativeTask;
            var native=snapshot.detected;
            _deckLinkNativeAvailable=native.Available;
            _deckLinkNativeDevices.Clear();
            _deckLinkNativeDevices.AddRange(native.Devices);
            _deckLinkNativeProbeMessage=native.Message;
            _deckLinkConnectionCache.Clear();
            foreach(var pair in snapshot.connections) _deckLinkConnectionCache[pair.Key]=pair.Value;
            _deckLinkModeCache.Clear();
            foreach(var pair in snapshot.modes) _deckLinkModeCache[pair.Key]=pair.Value;

            var dl=await fallbackTask;
            _deckLinkFfmpegPath=dl.Path;
            _deckLinkDetectedDevices.Clear();
            foreach(var dev in _deckLinkNativeDevices) _deckLinkDetectedDevices.Add("[NATIVE SDK] "+dev);
            foreach(var dev in dl.Devices)
                if(!_deckLinkDetectedDevices.Exists(x=>x.EndsWith(dev,StringComparison.OrdinalIgnoreCase))) _deckLinkDetectedDevices.Add("[FFMPEG/PnP] "+dev);
            _streamCapabilities.DeckLinkProbeMessage=_deckLinkNativeProbeMessage+" • "+dl.Message;
            _outputCapabilitiesLoaded=true;
        }
        catch(Exception ex)
        {
            _deckLinkFfmpegPath=null; _deckLinkDetectedDevices.Clear(); _deckLinkNativeDevices.Clear(); _deckLinkNativeAvailable=false;
            _streamCapabilities.DeckLinkProbeMessage="DeckLink detection error • "+ex.Message;
        }
        finally { Interlocked.Exchange(ref _outputCapabilityRefreshActive,0); }
        // One atomic UI refresh from cached results. No SDK call occurs below.
        RefreshOutputWorkspace();
        RefreshDeckLinkRouteOptions();
        _deckLinkPersistRestorePending=false;
        TryAutoStartArmedDeckLink();
    }

    private void DeckLinkDeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(_deckLinkUiUpdating) return;
        RefreshDeckLinkRouteOptions();
        RefreshOutputWorkspace();
    }

    private void DeckLinkConnectionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(_deckLinkUiUpdating) return;
        RefreshDeckLinkModes();
        RefreshOutputWorkspace();
    }

    private void RefreshDeckLinkRouteOptions()
    {
        _deckLinkUiUpdating=true;
        try
        {
            _deckLinkConnections.Clear(); _deckLinkModes.Clear(); DeckLinkConnectionCombo.Items.Clear(); DeckLinkModeCombo.Items.Clear();
            string selected=DeckLinkDeviceCombo.SelectedItem?.ToString()??"";
            if(!selected.StartsWith("[NATIVE SDK] ",StringComparison.OrdinalIgnoreCase) || !_deckLinkNativeAvailable) return;
            string device=selected.Replace("[NATIVE SDK] ","");
            int nativeIndex=_deckLinkNativeDevices.FindIndex(x=>string.Equals(x,device,StringComparison.OrdinalIgnoreCase));
            if(nativeIndex<0) return;
            if(_deckLinkConnectionCache.TryGetValue(nativeIndex,out var cachedConnections))
                _deckLinkConnections.AddRange(cachedConnections);
            foreach(var c in _deckLinkConnections) DeckLinkConnectionCombo.Items.Add(c);
            if(DeckLinkConnectionCombo.Items.Count>0)
            {
                var saved=DeckLinkConnectionCombo.Items.OfType<DeckLinkOutputConnection>().FirstOrDefault(x=>x.Value==_deckLinkPersist.ConnectionValue);
                DeckLinkConnectionCombo.SelectedItem=saved ?? DeckLinkConnectionCombo.Items[0];
            }
            RefreshDeckLinkModesCore(nativeIndex);
        }
        finally { _deckLinkUiUpdating=false; }
    }

    private void RefreshDeckLinkModes()
    {
        _deckLinkUiUpdating=true;
        try
        {
            string selected=DeckLinkDeviceCombo.SelectedItem?.ToString()??"";
            if(!selected.StartsWith("[NATIVE SDK] ",StringComparison.OrdinalIgnoreCase)) return;
            string device=selected.Replace("[NATIVE SDK] ","");
            int nativeIndex=_deckLinkNativeDevices.FindIndex(x=>string.Equals(x,device,StringComparison.OrdinalIgnoreCase));
            if(nativeIndex>=0) RefreshDeckLinkModesCore(nativeIndex);
        }
        finally { _deckLinkUiUpdating=false; }
    }

    private DeckLinkOutputMode? ResolveDeckLinkModeForAppliedOutput()
    {
        if(DeckLinkModeCombo.SelectedItem is DeckLinkOutputMode manual) return manual;
        if(DeckLinkModeCombo.SelectedItem is not string auto || !auto.StartsWith("AUTO / MAIN OUTPUT",StringComparison.OrdinalIgnoreCase)) return null;
        string screen=ComboTextAt(OutputResolutionCombo,_appliedOutputResolutionIndex);
        var fmt=StreamingEngine.ParseResolution(screen);
        double targetFps=fmt.Fps>0?fmt.Fps:AppliedOutputFrameRate(screen);
        return _deckLinkModes.FirstOrDefault(m=>m.Width==fmt.W && m.Height==fmt.H && Math.Abs(m.Fps-targetFps)<0.15 && m.Interlaced==fmt.Interlaced);
    }

    private void RefreshDeckLinkModesCore(int nativeIndex)
    {
        _deckLinkModes.Clear(); DeckLinkModeCombo.Items.Clear();
        if(DeckLinkConnectionCombo.SelectedItem is not DeckLinkOutputConnection c) return;
        if(_deckLinkModeCache.TryGetValue($"{nativeIndex}|{c.Value}",out var cachedModes))
            _deckLinkModes.AddRange(cachedModes);
        if(_deckLinkModes.Count==0) return;
        // Default route follows SCREEN/APPLIED automatically. Manual hardware modes remain available
        // below it for diagnostics/special routing, but AUTO is the normal broadcast path.
        DeckLinkModeCombo.Items.Add("AUTO / MAIN OUTPUT");
        foreach(var m in _deckLinkModes) DeckLinkModeCombo.Items.Add(m);
        if(_deckLinkPersistRestorePending && !_deckLinkPersist.AutoMode)
        {
            var saved=DeckLinkModeCombo.Items.OfType<DeckLinkOutputMode>().FirstOrDefault(x=>x.Id==_deckLinkPersist.ModeId);
            DeckLinkModeCombo.SelectedItem=saved ?? DeckLinkModeCombo.Items[0];
        }
        else DeckLinkModeCombo.SelectedIndex=0;
    }

    private void GraphicsOutputStart_Click(object sender, RoutedEventArgs e)
    {
        EnsureModuleWorker(ModuleKind.OutputWorker);
        int index=Math.Max(0,GraphicsMonitorCombo.SelectedIndex);
        try
        {
            _graphicsOutputWindow?.Close();
            _graphicsOutputWindow=new GraphicsCardOutputWindow();
            _graphicsOutputWindow.Closed += (_,__) => { _graphicsOutputWindow=null; if(GraphicsOutputStatusText!=null) GraphicsOutputStatusText.Text="STOPPED"; };
            _graphicsOutputWindow.ShowOnMonitor(index);
            if(_latestProgramFrame!=null) _graphicsOutputWindow.UpdateFrame(_latestProgramFrame);
            GraphicsOutputStatusText.Text="RUNNING • FINAL PROGRAM • DISPLAY "+(index+1);
        }
        catch(Exception ex)
        {
            GraphicsOutputStatusText.Text="ERROR • "+ex.Message;
        }
    }

    private void GraphicsOutputStop_Click(object sender, RoutedEventArgs e)
    {
        try { _graphicsOutputWindow?.Close(); } catch { }
        _graphicsOutputWindow=null;
        GraphicsOutputStatusText.Text="STOPPED";
    }


    private async void DeckLinkOutputStart_Click(object sender, RoutedEventArgs e)
    {
        EnsureModuleWorker(ModuleKind.OutputWorker);
        if(_deckLinkOutput.Running || _deckLinkNativeOutput.Running) return;
        DeckLinkStartButton.IsEnabled=false;
        DeckLinkOutputStatusText.Text="VALIDATING HARDWARE ROUTE...";
        try
        {
            string selected=DeckLinkDeviceCombo.SelectedItem?.ToString()??"";
            string device=selected.Replace("[NATIVE SDK] ","").Replace("[FFMPEG/PnP] ","");
            if(string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("No DeckLink device is selected / enumerated.");
            if(_latestProgramFrame==null) throw new InvalidOperationException("Final Program video frame is not ready. Start Program playout first.");
            string screen=ComboTextAt(OutputResolutionCombo,_appliedOutputResolutionIndex);
            int sr=ParseSampleRate(ComboTextAt(OutputSampleRateCombo,_appliedOutputSampleRateIndex));
            var fmt=StreamingEngine.ParseResolution(screen);
            int w=fmt.W>0?fmt.W:_latestProgramFrame.PixelWidth, h=fmt.H>0?fmt.H:_latestProgramFrame.PixelHeight;
            double fps=fmt.Fps>0?fmt.Fps:AppliedOutputFrameRate(screen);
            double aspect=OutputDisplayAspect(screen,_videoScaleMode,w,h);
            int busInputSampleRate=sr>0?sr:48000;
            bool selectedNative=selected.StartsWith("[NATIVE SDK] ",StringComparison.OrdinalIgnoreCase);

            if(selectedNative)
            {
                if(!_deckLinkNativeAvailable) throw new InvalidOperationException("Native DeckLink bridge is not READY. START is locked to prevent freeze/close.");
                if(DeckLinkConnectionCombo.SelectedItem is not DeckLinkOutputConnection connection) throw new InvalidOperationException("Select a physical DeckLink output connection.");
                var mode=ResolveDeckLinkModeForAppliedOutput() ?? throw new InvalidOperationException("AUTO / MAIN OUTPUT could not find an exact supported DeckLink mode for the applied SCREEN resolution/FPS/scan. Choose another physical connector, change SCREEN output registration, or select a supported manual mode.");
                // Main SCREEN/APPLIED registration is authoritative. Never silently send a different
                // DeckLink raster merely because it happens to be selected in the hardware list.
                bool rasterMatch = mode.Width==w && mode.Height==h;
                bool scanMatch = mode.Interlaced==fmt.Interlaced;
                bool fpsMatch = Math.Abs(mode.Fps-fps)<0.15;
                if(!rasterMatch || !scanMatch || !fpsMatch)
                    throw new InvalidOperationException($"Applied OUTPUT is {w}x{h} {(fmt.Interlaced?"interlaced":"progressive")} {fps:0.###} fps, but selected DeckLink mode is {mode.Name}. Choose a matching hardware mode/connection or change SCREEN output registration.");
                int nativeIndex=_deckLinkNativeDevices.FindIndex(x=>string.Equals(x,device,StringComparison.OrdinalIgnoreCase));
                if(nativeIndex<0) throw new InvalidOperationException("Selected native DeckLink SDK device is no longer available. Refresh devices.");
                // DeckLink mode is the hardware output raster. It is intentionally independent
                // from the confidence-monitor/UI frame size and from source-file dimensions.
                // The native output engine performs an aspect-preserving scale + black pad when
                // Final Program does not already match the selected hardware mode.
                _deckLinkBusSession?.Dispose(); _deckLinkBusSession=null;
                DeckLinkOutputStatusText.Text=$"CONNECTING • {device} • {connection.Name} • {mode.Name}";
                // Native initialization can take a few seconds while the driver locks hardware. Never block the WPF UI thread.
                await Task.Run(()=>_deckLinkNativeOutput.Start(nativeIndex,device,connection,mode));
                _deckLinkNativeOutput.UpdateScaleMode(_videoScaleMode,aspect);
                _deckLinkNativeOutput.PushVideo(_latestProgramFrame);
            }
            else
            {
                if(string.IsNullOrWhiteSpace(_deckLinkFfmpegPath)) throw new InvalidOperationException("Native DeckLink SDK route is not selected and no DeckLink-capable FFmpeg diagnostic fallback is available.");
                _deckLinkBusSession?.Dispose();
                _deckLinkBusSession=new MasterAvBus.Session("DECKLINK_FFM",w,h,fps,busInputSampleRate,_videoScaleMode,aspect);
                _deckLinkBusSession.PushVideo(_latestProgramFrame);
                _deckLinkOutput.Start(_deckLinkFfmpegPath,device,_deckLinkBusSession,fmt.Interlaced);
                DeckLinkOutputStatusText.Text="CONNECTING • FFMPEG DECKLINK DIAGNOSTIC FALLBACK";
            }
            CaptureDeckLinkRoute(true);
            RefreshOutputWorkspace();
        }
        catch(Exception ex)
        {
            try{_deckLinkBusSession?.Dispose();}catch{} _deckLinkBusSession=null;
            RefreshOutputWorkspace();
            DeckLinkOutputStatusText.Text="ERROR • "+ex.Message;
            DeckLinkStatusText.Text="ERROR • "+ex.Message;
            DeckLinkStatusText.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,120,120));
            DeckLinkReadyBadge.Text="●  ERROR";
            DeckLinkReadyBadge.Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,120,120));
        }
        finally { Interlocked.Exchange(ref _deckLinkAutoStartInProgress,0); }
    }

    private void DeckLinkOutputStop_Click(object sender, RoutedEventArgs e)
    {
        CaptureDeckLinkRoute(false);
        _deckLinkOutput.Stop("STOPPED BY OPERATOR");
        _deckLinkNativeOutput.Stop("STOPPED BY OPERATOR");
        try{_deckLinkBusSession?.Dispose();}catch{} _deckLinkBusSession=null;
        RefreshOutputWorkspace();
    }

    private void StreamNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("STREAM");
    private void ScreenNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("SCREEN");
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowWorkspace("SETTINGS");
    private void OutputProcessingOpen_Click(object sender, RoutedEventArgs e) => ShowWorkspace("SETTINGS");

    private void FillerNav_Click(object sender, RoutedEventArgs e)
    {
        var w = new FillerWindow(_fillerFiles) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _fillerFiles.Clear();
            _fillerFiles.AddRange(w.Files.Where(File.Exists));
            SaveFillerState();
            UpdateDashboardStatus();
        }
    }

    private void LoadFillerState()
    {
        try
        {
            _fillerFiles.Clear();
            if (!File.Exists(DataStorage.CurrentFiller)) return;
            string json = File.ReadAllText(DataStorage.CurrentFiller);
            try
            {
                var state = JsonSerializer.Deserialize<FillerPersistState>(json);
                if (state?.Files != null) _fillerFiles.AddRange(state.Files.Where(File.Exists).Where(IsMedia));
                _fillerIndex = Math.Max(0, state?.NextIndex ?? 0);
                if (_fillerFiles.Count > 0) _fillerIndex %= _fillerFiles.Count;
                _fillerResumeFile = state?.ResumeFile;
                _fillerResumePosition = Math.Max(0, state?.ResumePosition ?? 0);
                _fillerResumePending = state?.ResumePending == true && !string.IsNullOrWhiteSpace(_fillerResumeFile) && File.Exists(_fillerResumeFile);
            }
            catch
            {
                var files = JsonSerializer.Deserialize<List<string>>(json) ?? new();
                _fillerFiles.AddRange(files.Where(File.Exists).Where(IsMedia));
            }
        }
        catch { }
    }
    private void SaveFillerState()
    {
        try { var state = new FillerPersistState { Files = _fillerFiles.ToList(), NextIndex = _fillerIndex, ResumeFile = _fillerResumeFile, ResumePosition = _fillerResumePosition, ResumePending = _fillerResumePending }; DataStorage.WritePersistentText(DataStorage.CurrentFiller, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), "AutoState"); } catch { }
    }
    private async Task<bool> TryPlayFillerAsync()
    {
        if (!_onAirEnabled || _operatorStopped) return false;
        if (_activeScheduledJob != null || _scheduledPlaylistActive) return false;
        _fillerFiles.RemoveAll(f => !File.Exists(f));
        if (_fillerFiles.Count == 0) return false;
        if (_fillerIndex >= _fillerFiles.Count) _fillerIndex = 0;
        string file;
        double resumeAt = 0;
        if (_fillerResumePending && !string.IsNullOrWhiteSpace(_fillerResumeFile) && File.Exists(_fillerResumeFile) && _fillerFiles.Any(f => string.Equals(f, _fillerResumeFile, StringComparison.OrdinalIgnoreCase)))
        {
            file = _fillerResumeFile;
            resumeAt = Math.Max(0, _fillerResumePosition);
            _fillerResumePending = false;
            _fillerResumeFile = null;
            _fillerResumePosition = 0;
        }
        else file = _fillerFiles[_fillerIndex++];
        _fillerActive = true;
        StatusText.Text = resumeAt > 0.25 ? $"FILLER • RESUME {Clock(resumeAt)}" : "FILLER • FALLBACK ON AIR";
        await TakeToAirAtAsync(file, resumeAt, AudioLanguageHelper.DefaultLanguage, null);
        SaveFillerState();
        _fillerActive = true;
        UpdateDashboardStatus();
        return true;
    }

}

using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media.Imaging;

namespace FFmpegNativePlayer;

public partial class ProgramManagerWindow : Window
{
    private sealed class ProgramItem
    {
        public int Number { get; set; }
        public string FilePath { get; set; } = "";
        public string Name => Path.GetFileName(FilePath);
        public double DurationSeconds { get; set; }
        public double SourceDurationSeconds { get; set; }
        public double InPointSeconds { get; set; }
        public double OutPointSeconds { get; set; }
        public string Description { get; set; } = "";
        public ObservableCollection<string> AvailableAudioLanguages { get; } = new() { AudioLanguageHelper.DefaultLanguage };
        public string AudioLanguage { get; set; } = AudioLanguageHelper.DefaultLanguage;
        public string DurationText => DurationSeconds > 0
            ? TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"hh\:mm\:ss\.fff" : @"mm\:ss\.fff")
            : "--:--";
        public string InText => FormatTime(InPointSeconds);
        public string OutText => FormatTime(OutPointSeconds > 0 ? OutPointSeconds : SourceDurationSeconds);
        private static string FormatTime(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"hh\:mm\:ss\.fff");
    }

    private sealed class ProgramFileModel
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

    private sealed class ProgramLibraryItem
    {
        public string Name { get; init; } = "";
        public string FilePath { get; init; } = "";
    }

    private readonly ObservableCollection<ProgramItem> _items = new();
    private readonly ObservableCollection<ProgramLibraryItem> _programLibrary = new();
    private string? _currentFile;
    private bool _loadingProgram;
    private readonly NativeVideoPlayer _preview = new();
    private readonly NativeAudioPlayer _previewAudio = new();
    private readonly DispatcherTimer _previewUiTimer;
    private readonly DispatcherTimer _seekDebounceTimer;
    private string? _previewFile;
    private bool _previewMuted = true; // Local preview starts muted; never changes ON-AIR master audio.
    private const float PreviewFixedVolume = 0.70f;
    private readonly Random _shuffleRandom = new();
    private bool _seekDragging;
    private bool _seekBusy;
    private bool _seekSyncInProgress;
    private double _pendingSeekSeconds;
    private double _workingIn;
    private double _workingOut;
    private BitmapSource? _latestPreviewFrame;
    private int _previewFrameDispatchPending;
    private int _previewRenderWidth, _previewRenderHeight;

    private void UpdateStablePreviewRenderTarget()
    {
        int width = (int)Math.Round(PreviewImage.ActualWidth);
        int height = (int)Math.Round(PreviewImage.ActualHeight);
        if (!PreviewImage.IsVisible || width < 64 || height < 48) return;
        if (Math.Abs(width - _previewRenderWidth) < 4 && Math.Abs(height - _previewRenderHeight) < 4) return;
        _previewRenderWidth = width;
        _previewRenderHeight = height;
        int renderWidth = Math.Max(64, (int)Math.Ceiling(width * 1.10));
        int renderHeight = Math.Max(48, (int)Math.Ceiling(height * 1.10));
        _preview.SetRenderTarget(renderWidth, renderHeight);
    }

    public event Action<string>? AddToScheduleRequested;
    public event Action<string, string, double, double>? PlayNowRequested;
    public event Action<string, string, double, double>? PlayNextRequested;

    public ProgramManagerWindow()
    {
        InitializeComponent();
        MediaList.ItemsSource = _items;
        ProgramLibraryList.ItemsSource = _programLibrary;
        DataStorage.EnsureCreated();
        RefreshProgramLibrary();

        _preview.EnsureFFmpegInitialized();
        _preview.FrameReady += frame =>
        {
            _latestPreviewFrame = frame;
            if (Interlocked.Exchange(ref _previewFrameDispatchPending, 1) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                var latest = _latestPreviewFrame;
                if (latest != null) PreviewImage.Source = latest;
                PreviewEmptyText.Visibility = Visibility.Collapsed;
                Interlocked.Exchange(ref _previewFrameDispatchPending, 0);
            }));
        };
        _preview.MasterClockSeconds = null;
        _previewUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _previewUiTimer.Tick += (_, _) =>
        {
            UpdateStablePreviewRenderTarget();
            double pos = _previewAudio.IsOpen ? _previewAudio.PlaybackPositionSeconds : _preview.PositionSeconds;
            double dur = Math.Max(_preview.DurationSeconds, MediaList.SelectedItem is ProgramItem p ? p.SourceDurationSeconds : 0);
            if (!_seekDragging)
            {
                PreviewSeekSlider.Maximum = Math.Max(0.001, dur);
                PreviewSeekSlider.Value = Math.Clamp(pos, 0, PreviewSeekSlider.Maximum);
            }
            PreviewCurrentText.Text = FormatEditorTime(pos);
            PreviewTotalText.Text = FormatEditorTime(dur);
            PreviewPlayheadText.Text = $"PLAYHEAD  {FormatEditorTime(pos)}";

            // Keep editor seeking available across the full source, but normal preview playback
            // must stop at the working OUT point so IN/OUT behaves like the eventual playout range.
            if (!_seekDragging && !_seekSyncInProgress && !_previewPauseState &&
                _workingOut > _workingIn + 0.001 && pos >= _workingOut - 0.02)
            {
                _preview.TogglePause();
                _previewPauseState = true;
                if (_previewAudio.IsOpen) _previewAudio.Pause();
                StatusText.Text = $"PROGRAM PREVIEW • OUT REACHED {FormatEditorTime(_workingOut)}";
            }
        };
        _previewUiTimer.Start();

        _seekDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };

        Closed += (_, _) =>
        {
            _previewUiTimer.Stop();
            _seekDebounceTimer.Stop();
            _preview.Stop();
            _previewAudio.Stop();
            _preview.Dispose();
            _previewAudio.Dispose();
        };
    }

    private static string ProgramFilter =>
        "SMART PLAYOUT Program (*.sprogram)|*.sprogram|SMART PLAYOUT Playlist (*.splaylist)|*.splaylist|JSON (*.json)|*.json";

    private void RefreshProgramLibrary()
    {
        DataStorage.EnsureCreated();
        _programLibrary.Clear();
        foreach (var file in Directory.GetFiles(DataStorage.Programs, "*.sprogram")
                     .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase))
        {
            string displayName = Path.GetFileNameWithoutExtension(file);
            try
            {
                var model = JsonSerializer.Deserialize<ProgramFileModel>(File.ReadAllText(file));
                if (!string.IsNullOrWhiteSpace(model?.Name))
                    displayName = model.Name.Trim();
            }
            catch { }
            _programLibrary.Add(new ProgramLibraryItem { Name = displayName, FilePath = file });
        }
        ProgramCountText.Text = $"{_programLibrary.Count} PROGRAMS";
    }

    private async Task LoadProgramFileAsync(string file)
    {
        _preview.Stop();
        _previewAudio.Stop();
        HideProgramEditor();
        _loadingProgram = true;
        try
        {
            var model = JsonSerializer.Deserialize<ProgramFileModel>(File.ReadAllText(file));
            if (model == null) return;
            _items.Clear();
            ProgramNameBox.Text = model.Name;
            for (int i = 0; i < model.Files.Count; i++)
            {
                if (File.Exists(model.Files[i]))
                {
                    var sourceDuration = i < model.SourceDurations.Count && model.SourceDurations[i] > 0
                        ? model.SourceDurations[i]
                        : (i < model.Durations.Count ? model.Durations[i] : 0);
                    var inPoint = i < model.InPoints.Count ? Math.Max(0, model.InPoints[i]) : 0;
                    var outPoint = i < model.OutPoints.Count && model.OutPoints[i] > 0
                        ? model.OutPoints[i]
                        : sourceDuration;
                    var effectiveDuration = Math.Max(0, outPoint - inPoint);
                    var pi = new ProgramItem
                    {
                        FilePath = model.Files[i],
                        SourceDurationSeconds = sourceDuration,
                        InPointSeconds = inPoint,
                        OutPointSeconds = outPoint,
                        DurationSeconds = effectiveDuration > 0 ? effectiveDuration : (i < model.Durations.Count ? model.Durations[i] : sourceDuration),
                        Description = i < model.Descriptions.Count && !string.IsNullOrWhiteSpace(model.Descriptions[i])
                            ? model.Descriptions[i] : Path.GetFileNameWithoutExtension(model.Files[i]),
                        AudioLanguage = i < model.AudioLanguages.Count && !string.IsNullOrWhiteSpace(model.AudioLanguages[i])
                            ? model.AudioLanguages[i]
                            : AudioLanguageHelper.DefaultLanguage
                    };
                    _items.Add(pi);
                    _ = PopulateAudioLanguagesAsync(pi);
                }
            }
            _currentFile = file;
            ProgramFileText.Text = file;
            Renumber();
            StatusText.Text = "PROGRAM LOADED FROM LIBRARY";
            await Task.CompletedTask;
        }
        finally { _loadingProgram = false; }
    }

    private void AutoSaveProgram()
    {
        if (_loadingProgram || _items.Count == 0) return;
        var name = ProgramNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        SaveTo(DataStorage.ProgramPath(name), silent: true);
    }

    private void Renumber()
    {
        for (int i=0;i<_items.Count;i++) _items[i].Number=i+1;
        MediaList.Items.Refresh();
        TotalDurationText.Text = TimeSpan.FromSeconds(_items.Sum(i=>i.DurationSeconds)).ToString(@"hh\:mm\:ss");
    }

    private async Task AddPaths(IEnumerable<string> paths)
    {
        foreach (var file in paths.Where(File.Exists))
        {
            var item = new ProgramItem { FilePath=file, Description = Path.GetFileNameWithoutExtension(file) };
            _items.Add(item);
            try
            {
                var probe = await Task.Run(()=>AudioLanguageHelper.Probe(file));
                item.SourceDurationSeconds = Math.Max(0, probe.DurationSeconds);
                item.InPointSeconds = 0;
                item.OutPointSeconds = item.SourceDurationSeconds;
                item.DurationSeconds = item.SourceDurationSeconds;
                await PopulateAudioLanguagesAsync(item);
            }
            catch { }
            Renumber();
        }
        AutoSaveProgram();
    }

    private async Task PopulateAudioLanguagesAsync(ProgramItem item)
    {
        try
        {
            var languages = await Task.Run(() => AudioLanguageHelper.GetLanguages(item.FilePath));
            await Dispatcher.InvokeAsync(() =>
            {
                item.AvailableAudioLanguages.Clear();
                foreach (var lang in languages) item.AvailableAudioLanguages.Add(lang);
                if (!item.AvailableAudioLanguages.Any(l => string.Equals(l, item.AudioLanguage, StringComparison.OrdinalIgnoreCase)))
                    item.AvailableAudioLanguages.Insert(0, item.AudioLanguage);
            });
        }
        catch { }
    }

    private void ProgramAudioLanguage_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingProgram) return;
        AutoSaveProgram();
        if (sender is System.Windows.Controls.ComboBox cb && cb.DataContext is ProgramItem item)
            StatusText.Text = $"AUDIO LANGUAGE • {item.AudioLanguage} • AUTO SAVED";
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        PreviewStop_Click(sender, e);
        _items.Clear(); _currentFile=null; ProgramNameBox.Text="";
        ProgramFileText.Text="Not saved"; Renumber(); StatusText.Text="NEW PROGRAM";
    }

    private async void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var d=new Microsoft.Win32.OpenFileDialog { Multiselect=false, Filter="Media Files|*.mp4;*.mov;*.mxf;*.avi;*.mkv;*.mpg;*.mpeg;*.m2p;*.m2v;*.mpv;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.asf;*.flv;*.f4v;*.m4v;*.3gp;*.3g2;*.ogv;*.ogg;*.rm;*.rmvb;*.nut;*.y4m;*.dv;*.divx;*.xvid;*.264;*.h264;*.265;*.h265;*.hevc;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.opus|All Files|*.*" };
        if (d.ShowDialog()==true) await AddPaths(new[]{d.FileName});
    }

    private async void AddMulti_Click(object sender, RoutedEventArgs e)
    {
        var d=new Microsoft.Win32.OpenFileDialog { Multiselect=true, Filter="Media Files|*.mp4;*.mov;*.mxf;*.avi;*.mkv;*.mpg;*.mpeg;*.m2p;*.m2v;*.mpv;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.asf;*.flv;*.f4v;*.m4v;*.3gp;*.3g2;*.ogv;*.ogg;*.rm;*.rmvb;*.nut;*.y4m;*.dv;*.divx;*.xvid;*.264;*.h264;*.265;*.h265;*.hevc;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.opus|All Files|*.*" };
        if (d.ShowDialog()==true) await AddPaths(d.FileNames);
    }

    private static Microsoft.Win32.OpenFileDialog MediaDialog(bool multiselect = false) =>
        new()
        {
            Multiselect = multiselect,
            Filter = "Media Files|*.mp4;*.mov;*.mxf;*.avi;*.mkv;*.mpg;*.mpeg;*.m2p;*.m2v;*.mpv;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.asf;*.flv;*.f4v;*.m4v;*.3gp;*.3g2;*.ogv;*.ogg;*.rm;*.rmvb;*.nut;*.y4m;*.dv;*.divx;*.xvid;*.264;*.h264;*.265;*.h265;*.hevc;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.opus|All Files|*.*"
        };

    private async Task<double> ProbeDurationAsync(string file)
    {
        try
        {
            var probe = await Task.Run(() => MediaCompatibilityProbe.Probe(file));
            return Math.Max(0, probe.DurationSeconds);
        }
        catch { return 0; }
    }


    private void ShowProgramEditor()
    {
        ProgramEditorBorder.Visibility = Visibility.Visible;
        EditorRow.Height = new GridLength(276);
    }

    private void HideProgramEditor()
    {
        ProgramEditorBorder.Visibility = Visibility.Collapsed;
        EditorRow.Height = new GridLength(0);
    }

    private async Task PreviewItemAsync(ProgramItem? item, bool autoplay = true)
    {
        if (item == null || !File.Exists(item.FilePath)) return;
        ShowProgramEditor();
        try
        {
            _preview.Stop();
            _previewAudio.Stop();
            _previewPauseState = false;
            _previewFile = item.FilePath;
            _workingIn = Math.Max(0, item.InPointSeconds);
            _workingOut = item.OutPointSeconds > 0 ? item.OutPointSeconds : item.SourceDurationSeconds;
            LoadEditorFields(item);
            PreviewNameText.Text = item.Name;
            PreviewEmptyText.Visibility = Visibility.Visible;
            PreviewEmptyText.Text = "Loading preview...";
            await _preview.OpenAsync(item.FilePath);
            UpdateStablePreviewRenderTarget();
            if (!autoplay) return;

            // Preview uses its own audio path and never touches Program/Air output.
            try
            {
                int audioStream = await Task.Run(() => AudioLanguageHelper.ResolveStreamIndex(item.FilePath, item.AudioLanguage));
                await _previewAudio.OpenAndPlayAsync(item.FilePath, item.InPointSeconds, startPaused: true, preferredStreamIndex: audioStream, requireExactStream: audioStream >= 0);
                _previewAudio.SetVolume(PreviewFixedVolume);
                _previewAudio.SetMuted(_previewMuted);
            }
            catch { /* video-only files are valid preview sources */ }

            await _preview.PlayAsync();
            if (item.InPointSeconds > 0.01)
            {
                try { await _preview.SeekAsync(item.InPointSeconds); } catch { }
            }
            try { await _preview.FirstFrameReadyTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (_previewAudio.IsOpen) _previewAudio.Resume();
            PreviewEmptyText.Text = "";
        }
        catch (Exception ex)
        {
            StatusText.Text = "PREVIEW ERROR";
            System.Windows.MessageBox.Show(ex.Message, "SMART PLAYOUT");
        }
    }

    private async void PreviewSelected_Click(object sender, RoutedEventArgs e) =>
        await PreviewItemAsync(MediaList.SelectedItem as ProgramItem);

    private void ProgramPlayNow_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item || !File.Exists(item.FilePath)) return;
        PlayNowRequested?.Invoke(item.FilePath, item.AudioLanguage, item.InPointSeconds, item.OutPointSeconds);
        StatusText.Text = $"PLAY NOW • {item.Name} • {item.AudioLanguage}";
    }

    private void ProgramPlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item || !File.Exists(item.FilePath)) return;
        PlayNextRequested?.Invoke(item.FilePath, item.AudioLanguage, item.InPointSeconds, item.OutPointSeconds);
        StatusText.Text = $"PLAY NEXT SET • {item.Name} • {item.AudioLanguage}";
    }

    private void ProgramContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ProgramAudioContextMenu.Items.Clear();
        if (MediaList.SelectedItem is not ProgramItem item)
        {
            ProgramAudioContextMenu.IsEnabled = false;
            return;
        }

        ProgramAudioContextMenu.IsEnabled = true;
        foreach (var language in item.AvailableAudioLanguages.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var menu = new System.Windows.Controls.MenuItem
            {
                Header = language,
                Tag = language,
                IsCheckable = true,
                IsChecked = string.Equals(language, item.AudioLanguage, StringComparison.OrdinalIgnoreCase)
            };
            menu.Click += ProgramAudioContextItem_Click;
            ProgramAudioContextMenu.Items.Add(menu);
        }
    }

    private void ProgramAudioContextItem_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item ||
            sender is not System.Windows.Controls.MenuItem menu ||
            menu.Tag is not string language)
            return;

        item.AudioLanguage = language;
        MediaList.Items.Refresh();
        AutoSaveProgram();
        StatusText.Text = $"PROGRAM AUDIO • {language} • AUTO SAVED";
    }

    private async void ContextPreview_Click(object sender, RoutedEventArgs e) =>
        await PreviewItemAsync(MediaList.SelectedItem as ProgramItem);

    private async void MediaList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        await PreviewItemAsync(MediaList.SelectedItem as ProgramItem);

    private async void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is ProgramItem selected &&
            !string.Equals(_previewFile, selected.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            await PreviewItemAsync(selected);
            return;
        }
        if (string.IsNullOrWhiteSpace(_previewFile) && MediaList.SelectedItem is ProgramItem item)
        {
            await PreviewItemAsync(item);
            return;
        }
        if (!_previewAudio.IsOpen && MediaList.SelectedItem is ProgramItem restart)
        {
            await PreviewItemAsync(restart);
            return;
        }
        ShowProgramEditor();
        if (_workingOut > _workingIn + 0.001 &&
            CurrentPreviewPosition >= _workingOut - 0.02)
            await SeekPreviewAsync(_workingIn);

        _previewPauseState = false;
        await _preview.PlayAsync();
        if (_previewAudio.IsOpen) _previewAudio.Resume();
    }

    private void PreviewPause_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_previewFile)) return;

        _preview.TogglePause();
        _previewPauseState = !_previewPauseState;
        if (_previewAudio.IsOpen)
        {
            if (_previewPauseState) _previewAudio.Pause(); else _previewAudio.Resume();
        }
        StatusText.Text = _previewPauseState ? "PROGRAM PREVIEW • PAUSED" : "PROGRAM PREVIEW • PLAYING";
    }

    private bool _previewPauseState;

    private void PreviewStop_Click(object sender, RoutedEventArgs e)
    {
        _preview.Stop();
        _previewAudio.Stop();
        _previewPauseState = false;
        PreviewImage.Source = null;
        PreviewEmptyText.Text = "Preview stopped";
        PreviewEmptyText.Visibility = Visibility.Visible;
        HideProgramEditor();
    }

    private void PreviewMute_Click(object sender, RoutedEventArgs e)
    {
        // PREVIEW-ONLY mute. Fixed preview level; never touch Program/On-Air audio state here.
        _previewMuted = !_previewMuted;
        if (_previewAudio.IsOpen)
            _previewAudio.SetMuted(_previewMuted);

        PreviewMuteButton.Content = _previewMuted ? "\uE74F" : "\uE767";
        PreviewMuteButton.ToolTip = _previewMuted ? "Unmute Preview" : "Mute Preview";
    }

    private static string FormatEditorTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss\.fff");

    private double CurrentPreviewPosition =>
        _previewAudio.IsOpen ? Math.Max(0, _previewAudio.PlaybackPositionSeconds) : Math.Max(0, _preview.PositionSeconds);

    private void LoadEditorFields(ProgramItem item)
    {
        double source = Math.Max(item.SourceDurationSeconds, item.OutPointSeconds);
        if (source <= 0) source = Math.Max(item.DurationSeconds, _preview.DurationSeconds);
        PreviewSeekSlider.Maximum = Math.Max(0.001, source);
        PreviewSeekSlider.Value = Math.Clamp(item.InPointSeconds, 0, PreviewSeekSlider.Maximum);
        _workingIn = Math.Max(0, item.InPointSeconds);
        _workingOut = item.OutPointSeconds > 0 ? item.OutPointSeconds : source;
        InPointText.Text = FormatEditorTime(_workingIn);
        OutPointText.Text = FormatEditorTime(_workingOut);
        ClipDurationText.Text = FormatEditorTime(Math.Max(0, _workingOut - _workingIn));
        DescriptionBox.Text = string.IsNullOrWhiteSpace(item.Description)
            ? Path.GetFileNameWithoutExtension(item.FilePath) : item.Description;
    }

    private async Task SeekPreviewAsync(double seconds)
    {
        if (string.IsNullOrWhiteSpace(_previewFile)) return;
        double duration = Math.Max(_preview.DurationSeconds,
            MediaList.SelectedItem is ProgramItem item ? item.SourceDurationSeconds : 0);
        double target = duration > 0 ? Math.Clamp(seconds, 0, duration) : Math.Max(0, seconds);
        bool audioWasPaused = _previewAudio.IsOpen && _previewAudio.IsPaused;

        _seekSyncInProgress = true;
        try
        {
            if (_previewAudio.IsOpen)
            {
                _previewAudio.Pause();
                _previewAudio.Seek(target);
            }
            await _preview.SeekAsync(target);
            if (_previewAudio.IsOpen)
            {
                double cleanVideoAnchor = Math.Max(0, _preview.PositionSeconds);
                if (Math.Abs(cleanVideoAnchor - target) > 0.001)
                    _previewAudio.Seek(cleanVideoAnchor);
                if (audioWasPaused) _previewAudio.Pause(); else _previewAudio.Resume();
            }
        }
        catch (TaskCanceledException) { }
        catch { }
        finally { _seekSyncInProgress = false; }
    }

    private void PreviewSeek_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _seekDragging = true;
        _pendingSeekSeconds = PreviewSeekSlider.Value;
    }

    private async void PreviewSeek_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _pendingSeekSeconds = PreviewSeekSlider.Value;
        await SeekPreviewAsync(_pendingSeekSeconds);
        _seekDragging = false;
    }

    private void PreviewSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_seekDragging) return;
        _pendingSeekSeconds = e.NewValue;
        PreviewCurrentText.Text = FormatEditorTime(e.NewValue);
        PreviewPlayheadText.Text = $"PLAYHEAD  {FormatEditorTime(e.NewValue)}";
    }

    private void SetIn_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item) return;
        _workingIn = Math.Clamp(CurrentPreviewPosition, 0, Math.Max(item.SourceDurationSeconds, _preview.DurationSeconds));
        if (_workingOut <= _workingIn)
            _workingOut = Math.Max(_workingIn, item.OutPointSeconds > 0 ? item.OutPointSeconds : item.SourceDurationSeconds);
        InPointText.Text = FormatEditorTime(_workingIn);
        ClipDurationText.Text = FormatEditorTime(Math.Max(0, _workingOut - _workingIn));
        StatusText.Text = $"IN MARK • {InPointText.Text}";
    }

    private void SetOut_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item) return;
        double source = Math.Max(item.SourceDurationSeconds, _preview.DurationSeconds);
        _workingOut = Math.Clamp(CurrentPreviewPosition, 0, source > 0 ? source : double.MaxValue);
        if (_workingOut < _workingIn) _workingOut = _workingIn;
        OutPointText.Text = FormatEditorTime(_workingOut);
        ClipDurationText.Text = FormatEditorTime(Math.Max(0, _workingOut - _workingIn));
        StatusText.Text = $"OUT MARK • {OutPointText.Text}";
    }

    private void ApplyInOut_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item) return;
        double source = Math.Max(item.SourceDurationSeconds, _preview.DurationSeconds);
        double inPoint = Math.Clamp(_workingIn, 0, source > 0 ? source : _workingIn);
        double outPoint = _workingOut > 0 ? _workingOut : source;
        if (source > 0) outPoint = Math.Clamp(outPoint, inPoint, source);
        if (outPoint <= inPoint)
        {
            System.Windows.MessageBox.Show("OUT point must be after IN point.", "SMART PLAYOUT");
            return;
        }
        item.SourceDurationSeconds = source;
        item.InPointSeconds = inPoint;
        item.OutPointSeconds = outPoint;
        item.DurationSeconds = outPoint - inPoint;
        item.Description = DescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(item.Description))
            item.Description = Path.GetFileNameWithoutExtension(item.FilePath);
        Renumber();
        LoadEditorFields(item);
        AutoSaveProgram();
        StatusText.Text = $"IN/OUT SET • {FormatEditorTime(item.DurationSeconds)} • AUTO SAVED";
    }

    private void ResetInOut_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item) return;
        double source = Math.Max(item.SourceDurationSeconds, _preview.DurationSeconds);
        _workingIn = 0;
        _workingOut = source;
        InPointText.Text = FormatEditorTime(_workingIn);
        OutPointText.Text = FormatEditorTime(_workingOut);
        ClipDurationText.Text = FormatEditorTime(source);
        StatusText.Text = "IN/OUT RESET READY • PRESS SET TO SAVE";
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem item) return;
        int index = MediaList.SelectedIndex;
        double split = CurrentPreviewPosition;
        double source = Math.Max(item.SourceDurationSeconds, _preview.DurationSeconds);
        double originalIn = Math.Max(0, item.InPointSeconds);
        double originalOut = item.OutPointSeconds > 0 ? item.OutPointSeconds : source;
        if (split <= originalIn + 0.02 || split >= originalOut - 0.02)
        {
            System.Windows.MessageBox.Show("Move the playhead inside the current IN/OUT range before Split.", "SMART PLAYOUT");
            return;
        }

        var first = new ProgramItem
        {
            FilePath = item.FilePath, SourceDurationSeconds = source,
            InPointSeconds = originalIn, OutPointSeconds = split,
            DurationSeconds = split - originalIn,
            AudioLanguage = item.AudioLanguage,
            Description = (string.IsNullOrWhiteSpace(item.Description) ? Path.GetFileNameWithoutExtension(item.FilePath) : item.Description) + " • PART 1"
        };
        var second = new ProgramItem
        {
            FilePath = item.FilePath, SourceDurationSeconds = source,
            InPointSeconds = split, OutPointSeconds = originalOut,
            DurationSeconds = originalOut - split,
            AudioLanguage = item.AudioLanguage,
            Description = (string.IsNullOrWhiteSpace(item.Description) ? Path.GetFileNameWithoutExtension(item.FilePath) : item.Description) + " • PART 2"
        };
        foreach (var l in item.AvailableAudioLanguages)
        {
            if (!first.AvailableAudioLanguages.Contains(l)) first.AvailableAudioLanguages.Add(l);
            if (!second.AvailableAudioLanguages.Contains(l)) second.AvailableAudioLanguages.Add(l);
        }

        _items.RemoveAt(index);
        _items.Insert(index, first);
        _items.Insert(index + 1, second);
        Renumber();
        MediaList.SelectedIndex = index + 1;
        AutoSaveProgram();
        StatusText.Text = $"SPLIT • {FormatEditorTime(split)} • NON-DESTRUCTIVE • AUTO SAVED";
    }

    private void DescriptionBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loadingProgram || MediaList.SelectedItem is not ProgramItem item) return;
        item.Description = DescriptionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(item.Description))
            item.Description = Path.GetFileNameWithoutExtension(item.FilePath);
        MediaList.Items.Refresh();
        AutoSaveProgram();
    }

    private void MediaList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is ProgramItem item)
            LoadEditorFields(item);
    }

    private async void InsertBefore_Click(object sender, RoutedEventArgs e) => await InsertAtSelectionAsync(before: true);
    private async void InsertAfter_Click(object sender, RoutedEventArgs e) => await InsertAtSelectionAsync(before: false);

    private async Task InsertAtSelectionAsync(bool before)
    {
        int selected = MediaList.SelectedIndex;
        if (selected < 0) return;
        var d = MediaDialog();
        if (d.ShowDialog() != true) return;
        double duration = await ProbeDurationAsync(d.FileName);
        int index = before ? selected : selected + 1;
        var inserted = new ProgramItem { FilePath = d.FileName, SourceDurationSeconds = duration, InPointSeconds = 0, OutPointSeconds = duration, DurationSeconds = duration, Description = Path.GetFileNameWithoutExtension(d.FileName), AudioLanguage = AudioLanguageHelper.DefaultLanguage };
        _items.Insert(index, inserted); _ = PopulateAudioLanguagesAsync(inserted);
        Renumber();
        MediaList.SelectedIndex = index;
        AutoSaveProgram();
        StatusText.Text = before ? "MEDIA INSERTED BEFORE" : "MEDIA INSERTED AFTER";
    }

    private async void ReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        int index = MediaList.SelectedIndex;
        if (index < 0) return;
        var d = MediaDialog();
        if (d.ShowDialog() != true) return;
        double duration = await ProbeDurationAsync(d.FileName);
        var replaced = new ProgramItem { FilePath = d.FileName, SourceDurationSeconds = duration, InPointSeconds = 0, OutPointSeconds = duration, DurationSeconds = duration, Description = Path.GetFileNameWithoutExtension(d.FileName), AudioLanguage = AudioLanguageHelper.DefaultLanguage };
        _items[index] = replaced; _ = PopulateAudioLanguagesAsync(replaced);
        Renumber();
        MediaList.SelectedIndex = index;
        AutoSaveProgram();
        StatusText.Text = "MEDIA REPLACED";
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not ProgramItem p) return;
        System.Windows.MessageBox.Show(
            $"File: {p.Name}\n\n" +
            $"Path: {p.FilePath}\n\n" +
            $"Source Duration: {FormatEditorTime(p.SourceDurationSeconds)}\n" +
            $"IN: {FormatEditorTime(p.InPointSeconds)}\n" +
            $"OUT: {FormatEditorTime(p.OutPointSeconds)}\n" +
            $"Play Duration: {p.DurationText}\n" +
            $"Audio Language: {p.AudioLanguage}\n" +
            $"Description: {p.Description}",
            "Program Media Properties", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count < 2) return;
        var list = _items.ToList();
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = _shuffleRandom.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        _items.Clear();
        foreach (var item in list) _items.Add(item);
        Renumber();
        AutoSaveProgram();
        StatusText.Text = "PROGRAM SHUFFLED • AUTO SAVED";
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        int i=MediaList.SelectedIndex; if(i<=0)return; var v=_items[i]; _items.RemoveAt(i); _items.Insert(i-1,v); MediaList.SelectedIndex=i-1; Renumber(); AutoSaveProgram();
    }
    private void Down_Click(object sender, RoutedEventArgs e)
    {
        int i=MediaList.SelectedIndex; if(i<0||i>=_items.Count-1)return; var v=_items[i]; _items.RemoveAt(i); _items.Insert(i+1,v); MediaList.SelectedIndex=i+1; Renumber(); AutoSaveProgram();
    }
    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if(MediaList.SelectedItem is not ProgramItem p)return;
        int i=MediaList.SelectedIndex; var copy=new ProgramItem{FilePath=p.FilePath,SourceDurationSeconds=p.SourceDurationSeconds,InPointSeconds=p.InPointSeconds,OutPointSeconds=p.OutPointSeconds,DurationSeconds=p.DurationSeconds,Description=p.Description,AudioLanguage=p.AudioLanguage}; foreach(var l in p.AvailableAudioLanguages) if(!copy.AvailableAudioLanguages.Contains(l)) copy.AvailableAudioLanguages.Add(l); _items.Insert(i+1,copy); Renumber(); AutoSaveProgram();
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if(MediaList.SelectedItem is ProgramItem p)_items.Remove(p); Renumber(); AutoSaveProgram();
    }
    private void Clear_Click(object sender, RoutedEventArgs e){_items.Clear();Renumber(); AutoSaveProgram();}

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var d=new Microsoft.Win32.OpenFileDialog{Filter=ProgramFilter, InitialDirectory=DataStorage.Programs};
        if(d.ShowDialog()!=true)return;
        try
        {
            _items.Clear();
            if(Path.GetExtension(d.FileName).Equals(".splaylist",StringComparison.OrdinalIgnoreCase))
            {
                var paths=JsonSerializer.Deserialize<string[]>(File.ReadAllText(d.FileName))??Array.Empty<string>();
                await AddPaths(paths);
                ProgramNameBox.Text=Path.GetFileNameWithoutExtension(d.FileName);
            }
            else
            {
                var model=JsonSerializer.Deserialize<ProgramFileModel>(File.ReadAllText(d.FileName));
                if(model==null)return;
                ProgramNameBox.Text=model.Name;
                for(int i=0;i<model.Files.Count;i++)
                    if(File.Exists(model.Files[i]))
                        { var source=i<model.SourceDurations.Count&&model.SourceDurations[i]>0?model.SourceDurations[i]:(i<model.Durations.Count?model.Durations[i]:0); var inp=i<model.InPoints.Count?Math.Max(0,model.InPoints[i]):0; var outp=i<model.OutPoints.Count&&model.OutPoints[i]>0?model.OutPoints[i]:source; var pi=new ProgramItem{FilePath=model.Files[i],SourceDurationSeconds=source,InPointSeconds=inp,OutPointSeconds=outp,DurationSeconds=Math.Max(0,outp-inp)>0?Math.Max(0,outp-inp):(i<model.Durations.Count?model.Durations[i]:source),Description=i<model.Descriptions.Count&&!string.IsNullOrWhiteSpace(model.Descriptions[i])?model.Descriptions[i]:Path.GetFileNameWithoutExtension(model.Files[i]),AudioLanguage=i<model.AudioLanguages.Count&&!string.IsNullOrWhiteSpace(model.AudioLanguages[i])?model.AudioLanguages[i]:AudioLanguageHelper.DefaultLanguage}; _items.Add(pi); _=PopulateAudioLanguagesAsync(pi); }
                Renumber();
            }
            _currentFile=d.FileName; ProgramFileText.Text=d.FileName; StatusText.Text="PROGRAM LOADED";
        }
        catch(Exception ex){System.Windows.MessageBox.Show(ex.Message,"SMART PLAYOUT");}
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(_currentFile)){SaveAs_Click(sender,e);return;}
        SaveTo(_currentFile);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var name=ProgramNameBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(name)){System.Windows.MessageBox.Show("Enter a Program Name first.","SMART PLAYOUT");return;}
        var d=new Microsoft.Win32.SaveFileDialog{Filter="SMART PLAYOUT Program (*.sprogram)|*.sprogram|JSON (*.json)|*.json",FileName=name+".sprogram",InitialDirectory=DataStorage.Programs};
        if(d.ShowDialog()!=true)return;
        _currentFile=d.FileName; SaveTo(d.FileName);
    }

    private void SaveTo(string file, bool silent = false)
    {
        var name=ProgramNameBox.Text.Trim();
        if(string.IsNullOrWhiteSpace(name)){System.Windows.MessageBox.Show("Enter a Program Name first.","SMART PLAYOUT");return;}
        var model=new ProgramFileModel{Name=name,Files=_items.Select(i=>i.FilePath).ToList(),Durations=_items.Select(i=>i.DurationSeconds).ToList(),SourceDurations=_items.Select(i=>i.SourceDurationSeconds).ToList(),InPoints=_items.Select(i=>i.InPointSeconds).ToList(),OutPoints=_items.Select(i=>i.OutPointSeconds).ToList(),Descriptions=_items.Select(i=>i.Description).ToList(),AudioLanguages=_items.Select(i=>i.AudioLanguage).ToList()};
        string json=JsonSerializer.Serialize(model,new JsonSerializerOptions{WriteIndented=true});
        // Every saved Program is persisted on D:. If the operator selected another
        // location, keep that compatibility copy too without mixing it with build files.
        string persistent=DataStorage.ProgramPath(name);
        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(persistent), StringComparison.OrdinalIgnoreCase))
            File.WriteAllText(file,json);
        DataStorage.WritePersistentText(persistent,json,"Programs");
        _currentFile=persistent;
        ProgramFileText.Text=persistent;
        RefreshProgramLibrary();
        if (!silent) StatusText.Text=$"PROGRAM SAVED • {_items.Count} ITEMS";
        else StatusText.Text=$"AUTO SAVED • {name}";
    }

    private async void ProgramLibraryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProgramLibraryList.SelectedItem is ProgramLibraryItem item && File.Exists(item.FilePath))
            await LoadProgramFileAsync(item.FilePath);
    }

    private void ProgramNameBox_LostFocus(object sender, RoutedEventArgs e) => AutoSaveProgram();

    private void AddToSchedule_Click(object sender, RoutedEventArgs e)
    {
        AutoSaveProgram();
        if (string.IsNullOrWhiteSpace(_currentFile) || !File.Exists(_currentFile))
        {
            System.Windows.MessageBox.Show("Enter a Program Name and add media first.", "SMART PLAYOUT");
            return;
        }
        AddToScheduleRequested?.Invoke(_currentFile);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        AutoSaveProgram();
        Close();
    }
}

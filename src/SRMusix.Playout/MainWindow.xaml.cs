using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using SRMusix.Playout.Models;
using SRMusix.Playout.Services;

namespace SRMusix.Playout;

public partial class MainWindow : Window
{
    public ObservableCollection<PlaylistItem> Playlist { get; } = [];
    private readonly LibVLC _libVlc = new("--no-video-title-show", "--file-caching=300");
    private readonly MediaPlayer _player;
    private readonly FfmpegOutputService _output = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _currentIndex = -1;
    private bool _seeking;

    public MainWindow()
    {
        InitializeComponent(); DataContext = this;
        ScheduleDate.SelectedDate = DateTime.Today;
        _player = new MediaPlayer(_libVlc); VideoView.MediaPlayer = _player;
        _player.EndReached += (_, _) => Dispatcher.Invoke(Next);
        _player.EncounteredError += (_, _) => Dispatcher.Invoke(() => WriteLog("Playback error: check codec/file."));
        _timer.Tick += Timer_Tick; _timer.Start();
        Closed += (_, _) => { _output.Dispose(); _player.Dispose(); _libVlc.Dispose(); };
    }

    private static readonly string[] Extensions = [".mpg", ".mpeg", ".vob", ".dat", ".mp4", ".mov", ".mkv", ".avi", ".ts", ".m2ts", ".mp3", ".wav"];
    private void AddFiles(IEnumerable<string> files)
    {
        foreach (var file in files.Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            Playlist.Add(new PlaylistItem { FilePath = file });
        UpdateNowNext(); WriteLog($"Playlist contains {Playlist.Count} item(s).");
    }

    private void Add_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Multiselect = true, Filter = "Media|*.mpg;*.mpeg;*.vob;*.dat;*.mp4;*.mov;*.mkv;*.avi;*.ts;*.m2ts;*.mp3;*.wav|All files|*.*" }; if (d.ShowDialog() == true) AddFiles(d.FileNames); }
    private void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] f) AddFiles(f); }
    private void Remove_Click(object sender, RoutedEventArgs e) { if (Playlist.SelectedItem is PlaylistItem i) Playlist.Remove(i); }
    private void Clear_Click(object sender, RoutedEventArgs e) { Stop(); Playlist.Clear(); _currentIndex = -1; UpdateNowNext(); }
    private void Playlist_DoubleClick(object sender, MouseButtonEventArgs e) { if (Playlist.SelectedIndex >= 0) PlayIndex(Playlist.SelectedIndex); }
    private void Play_Click(object sender, RoutedEventArgs e) { if (_player.Media is null) PlayIndex(Playlist.SelectedIndex >= 0 ? Playlist.SelectedIndex : Math.Max(0, _currentIndex)); else _player.Play(); }
    private void Pause_Click(object sender, RoutedEventArgs e) => _player.Pause();
    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    private void Next_Click(object sender, RoutedEventArgs e) => Next();
    private void Stop() { _player.Stop(); StatusText.Text = "STOPPED"; }
    private void Next() { if (Playlist.Count > 0) PlayIndex((_currentIndex + 1) % Playlist.Count); }
    private void PlayIndex(int index)
    {
        if (index < 0 || index >= Playlist.Count) return;
        _currentIndex = index; Playlist.SelectedIndex = index;
        using var media = new Media(_libVlc, Playlist[index].FilePath, FromType.FromPath);
        _player.Play(media); Playlist[index].Status = "On Air";
        StatusText.Text = $"ON AIR — {Playlist[index].Title}"; WriteLog(StatusText.Text); UpdateNowNext();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_player.Length > 0 && !_seeking) Position.Value = _player.Time * 1000d / _player.Length;
        TimeDisplay.Text = $"{TimeSpan.FromMilliseconds(Math.Max(0, _player.Time)):hh\\:mm\\:ss} / {TimeSpan.FromMilliseconds(Math.Max(0, _player.Length)):hh\\:mm\\:ss}";
        var due = Playlist.Select((item, index) => (item, index)).FirstOrDefault(x => x.item.ScheduledAt is not null && x.item.ScheduledAt <= DateTime.Now && x.item.Status == "Ready");
        if (due.item is not null) PlayIndex(due.index);
    }
    private void Position_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_seeking && _player.Length > 0) _player.Time = (long)(_player.Length * e.NewValue / 1000d); }
    protected override void OnMouseDown(MouseButtonEventArgs e) { _seeking = e.LeftButton == MouseButtonState.Pressed && Position.IsMouseOver; base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseButtonEventArgs e) { if (_seeking) { _player.Time = (long)(_player.Length * Position.Value / 1000d); _seeking = false; } base.OnMouseUp(e); }
    private void SetSchedule_Click(object sender, RoutedEventArgs e) { if (Playlist.SelectedItem is not PlaylistItem item || ScheduleDate.SelectedDate is null || !TimeSpan.TryParse(ScheduleTime.Text, out var time)) { WriteLog("Select an item and enter time as HH:mm:ss."); return; } item.ScheduledAt = ScheduleDate.SelectedDate.Value.Date + time; Playlist.Items.Refresh(); WriteLog($"Scheduled {item.Title} at {item.ScheduledAt:dd MMM HH:mm:ss}."); }
    private void UpdateNowNext() { var now = _currentIndex >= 0 && _currentIndex < Playlist.Count ? Playlist[_currentIndex].Title : "Standby"; var next = Playlist.Count > _currentIndex + 1 ? Playlist[_currentIndex + 1].Title : "—"; NowNextText.Text = $"NOW: {now}\nNEXT: {next}"; }
    private void OverlayToggle_Click(object sender, RoutedEventArgs e) { LogoOverlay.Visibility = ShowLogo.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; NowNextOverlay.Visibility = ShowNowNext.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; TickerOverlay.Visibility = ShowTicker.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
    private void TickerInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) { if (TickerText is not null) TickerText.Text = TickerInput.Text; }
    private void StartOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || !File.Exists(Playlist[_currentIndex].FilePath)) { WriteLog("Start a playlist item first."); return; }
        var ffmpeg = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
        if (!File.Exists(ffmpeg)) { WriteLog($"FFmpeg missing: {ffmpeg}"); return; }
        if (!int.TryParse(Bitrate.Text, out var bitrate)) bitrate = 8000;
        var codec = ((System.Windows.Controls.ComboBoxItem)Codec.SelectedItem).Content.ToString()!;
        try { _output.Start(ffmpeg, _output.BuildArguments(Playlist[_currentIndex].FilePath, Destination.Text, codec, bitrate)); OutputLamp.Fill = Brushes.LimeGreen; WriteLog("Output started."); } catch (Exception ex) { WriteLog(ex.Message); }
    }
    private void StopOutput_Click(object sender, RoutedEventArgs e) { _output.Stop(); OutputLamp.Fill = Brushes.IndianRed; WriteLog("Output stopped."); }
    private void WriteLog(string message) { Log.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}"); }
}

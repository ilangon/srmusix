using Microsoft.Win32;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace FFmpegNativePlayer;

public partial class FillerWindow : Window
{
    private readonly ObservableCollection<string> _files = new();
    public IReadOnlyList<string> Files => _files.ToList();
    public FillerWindow(IEnumerable<string> files)
    {
        InitializeComponent();
        foreach (var f in files.Where(File.Exists)) _files.Add(f);
        FilesList.ItemsSource = _files; RefreshCount();
    }
    private void RefreshCount() => CountText.Text = $"{_files.Count} filler item(s) • sequential loop";
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title="Add Filler Media", Multiselect=true, Filter="Media files|*.mp4;*.mkv;*.mov;*.avi;*.mxf;*.mpg;*.mpeg;*.m2p;*.m2v;*.mpv;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.flv;*.m4v;*.mp3;*.wav;*.aac;*.m4a;*.flac;*.wma;*.opus|All files|*.*" };
        if (d.ShowDialog(this) != true) return;
        foreach(var f in d.FileNames) if(!_files.Contains(f)) _files.Add(f); RefreshCount();
    }
    private void Up_Click(object sender, RoutedEventArgs e) { int i=FilesList.SelectedIndex; if(i>0){var v=_files[i];_files.RemoveAt(i);_files.Insert(i-1,v);FilesList.SelectedIndex=i-1;} }
    private void Down_Click(object sender, RoutedEventArgs e) { int i=FilesList.SelectedIndex; if(i>=0&&i<_files.Count-1){var v=_files[i];_files.RemoveAt(i);_files.Insert(i+1,v);FilesList.SelectedIndex=i+1;} }
    private void Remove_Click(object sender, RoutedEventArgs e) { int i=FilesList.SelectedIndex; if(i>=0)_files.RemoveAt(i); RefreshCount(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { _files.Clear(); RefreshCount(); }
    private void Save_Click(object sender, RoutedEventArgs e) { DialogResult=true; Close(); }
}

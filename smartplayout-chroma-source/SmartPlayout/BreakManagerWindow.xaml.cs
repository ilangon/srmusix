using System;using System.Collections.Generic;using System.IO;using System.Linq;using System.Windows;
namespace FFmpegNativePlayer;
public partial class BreakManagerWindow:Window
{
 readonly MainWindow _main; readonly List<string> _files=new();
 public BreakManagerWindow(MainWindow main){InitializeComponent();_main=main;Refresh();}
 void Add_Click(object s,RoutedEventArgs e){var d=new Microsoft.Win32.OpenFileDialog{Title="Add Break / Commercial Media",Multiselect=true,Filter="Media files|*.mp4;*.mkv;*.mov;*.avi;*.mpg;*.mpeg;*.m2v;*.ts;*.m2ts;*.vob;*.dat;*.wmv;*.webm;*.mp3;*.wav;*.aac;*.m4a|All files (*.*)|*.*"};if(d.ShowDialog()!=true)return;foreach(var f in d.FileNames)if(!_files.Contains(f,StringComparer.OrdinalIgnoreCase))_files.Add(f);Refresh();}
 void Remove_Click(object s,RoutedEventArgs e){var i=BreakList.SelectedIndex;if(i>=0&&i<_files.Count)_files.RemoveAt(i);Refresh();}
 void Clear_Click(object s,RoutedEventArgs e){_files.Clear();Refresh();}
 async void Take_Click(object s,RoutedEventArgs e){var i=BreakList.SelectedIndex;if(i<0||i>=_files.Count){Status.Text="SELECT A BREAK ITEM";return;}try{Status.Text="TAKING BREAK...";await _main.PlayStudioBreakAsync(_files[i]);Status.Text="BREAK ON AIR • AUTO RETURN ARMED";}catch(Exception ex){Status.Text="BREAK FAILED • "+ex.Message;}}
 void Refresh(){BreakList.ItemsSource=null;BreakList.ItemsSource=_files.Select(Path.GetFileName).ToList();if(_files.Count>0)BreakList.SelectedIndex=0;}
 void Close_Click(object s,RoutedEventArgs e)=>Close();
}

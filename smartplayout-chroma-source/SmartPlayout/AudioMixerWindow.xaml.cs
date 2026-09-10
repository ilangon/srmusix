using System.Windows;
namespace FFmpegNativePlayer;
public partial class AudioMixerWindow : Window
{
 readonly MainWindow _main;
 public AudioMixerWindow(MainWindow main){InitializeComponent();_main=main;var s=_main.GetStudioAudioState();GainSlider.Value=s.Gain;NormalizeCheck.IsChecked=s.Normalize;GainValue.Text=$"{s.Gain:+0.0;-0.0;0.0} dB";}
 void GainSlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e){if(GainValue!=null)GainValue.Text=$"{e.NewValue:+0.0;-0.0;0.0} dB";}
 void Apply_Click(object s,RoutedEventArgs e)=>_main.ApplyStudioAudioState(GainSlider.Value,NormalizeCheck.IsChecked==true);
 void OpenEq_Click(object s,RoutedEventArgs e){_main.OpenAudioProcessing();Close();}
 void Close_Click(object s,RoutedEventArgs e)=>Close();
}

using System;using System.Globalization;using System.IO;using System.Text.Json;using System.Threading.Tasks;using System.Windows;using System.Windows.Media;using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Point = System.Windows.Point;
namespace FFmpegNativePlayer;
public partial class ChromaKeyWindow:Window
{
 readonly MainWindow _main; readonly LiveCaptureEngine _sourcePreviewCapture=new(); readonly System.Windows.Threading.DispatcherTimer _statusTimer=new(){Interval=TimeSpan.FromSeconds(1)}; readonly System.Diagnostics.Process _process=System.Diagnostics.Process.GetCurrentProcess(); TimeSpan _lastCpu;DateTime _lastCpuAt=DateTime.UtcNow; long _lastPreviewDispatchTick;int _previewDispatchPending; BitmapSource? _source,_keyedFrame,_matteFrame,_spillFrame,_differenceFrame,_splitFrame,_planarFrame; bool _eyedropperArmed,_closed,_syncingAspect,_syncingKeyControls; string _previewMode="Composite";
 public ChromaKeyWindow(MainWindow main){_main=main;InitializeComponent();_sourcePreviewCapture.FrameReady+=QueueSourcePreview;_sourcePreviewCapture.LevelChanged+=(l,r)=>PostUi(()=>OnAudioLevel(l,r));_sourcePreviewCapture.StatusChanged+=status=>PostUi(()=>PreviewStatus.Text="PREVIEW • "+status);_statusTimer.Tick+=(_,__)=>UpdateSystemStatus();Loaded+=async(_,__)=>{try{ModuleDiagnostics.WriteInfo("CHROMA_KEY","WINDOW OPENED • DEVICE DISCOVERY STARTED");_main.StudioAudioLevelChanged+=OnAudioLevel;_main.StudioKeyBackgroundFrameChanged+=OnBackgroundFrame;_lastCpu=_process.TotalProcessorTime;_lastCpuAt=DateTime.UtcNow;_statusTimer.Start();UpdateSystemStatus();SyncKeySlidersFromText();UpdateKeyColorVisual();RefreshPreview();await PopulateSourcesAsync();ModuleDiagnostics.WriteInfo("CHROMA_KEY","DEVICE DISCOVERY COMPLETED • "+PreviewStatus.Text);}catch(Exception ex){if(!_closed)PreviewStatus.Text="PREVIEW ERROR • "+ex.Message;ModuleDiagnostics.ShowError("CHROMA_KEY","WINDOW_LOAD",ex,this);}};Closed+=(_,__)=>{_closed=true;_statusTimer.Stop();_main.StudioAudioLevelChanged-=OnAudioLevel;_main.StudioKeyBackgroundFrameChanged-=OnBackgroundFrame;_sourcePreviewCapture.Dispose();_process.Dispose();ModuleDiagnostics.WriteInfo("CHROMA_KEY","WINDOW CLOSED CLEANLY");};}
 void PostUi(Action action){if(_closed||Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)return;try{Dispatcher.BeginInvoke(new Action(()=>{if(!_closed)action();}));}catch(InvalidOperationException){}}
 void QueueSourcePreview(BitmapSource frame){if(_closed)return;_source=frame;long now=System.Diagnostics.Stopwatch.GetTimestamp();long previous=System.Threading.Interlocked.Read(ref _lastPreviewDispatchTick);if(previous!=0&&(now-previous)/(double)System.Diagnostics.Stopwatch.Frequency<.16)return;if(System.Threading.Interlocked.Exchange(ref _previewDispatchPending,1)!=0)return;System.Threading.Interlocked.Exchange(ref _lastPreviewDispatchTick,now);PostUi(()=>{try{var latest=_source;if(latest!=null){InputPreview.Source=latest;ProgramPreview.Source=latest;RenderPreview();}}finally{System.Threading.Interlocked.Exchange(ref _previewDispatchPending,0);}});}
 void UpdateSystemStatus(){if(_closed)return;try{var now=DateTime.Now;ClockText.Text=now.ToString("HH:mm:ss");DateText.Text=now.ToString("ddd dd MMM yyyy");var utc=DateTime.UtcNow;var cpu=_process.TotalProcessorTime;double elapsed=Math.Max(.001,(utc-_lastCpuAt).TotalSeconds),used=Math.Max(0,(cpu-_lastCpu).TotalSeconds);double percent=Math.Clamp(used/(elapsed*Environment.ProcessorCount)*100,0,100);_lastCpu=cpu;_lastCpuAt=utc;SystemStatusText.Text=$"Output: Program • Audio: AUTO • 48 kHz • CPU: {percent:0}%";}catch(ObjectDisposedException){}}
 void OnAudioLevel(float left,float right){if(_closed)return;AudioMeterText.Text=$"AUDIO L  {Meter(left)}\nAUDIO R  {Meter(right)}";}
 static string Meter(float level){int on=Math.Clamp((int)Math.Round(level*8),0,8);return new string('▮',on)+new string('▯',8-on);}
 void OnBackgroundFrame(BitmapSource frame){if(_closed)return;BackgroundPreview.Source=frame;}
 async Task PopulateSourcesAsync()
 {
  VideoSourceCombo.Items.Clear();
  VideoSourceCombo.Items.Add("Current Program / Master Bus");
  PreviewStatus.Text="SCANNING VIDEO + AUDIO INPUT DEVICES…";
  var probe=await LiveInputDiscovery.ProbeAsync();
  foreach(var source in probe.Video)VideoSourceCombo.Items.Add(source);
  // NDI is listed only when the installed FFmpeg runtime actually discovers it.
  // A non-functional placeholder previously routed through the URL path and could
  // make the module appear broken even when no NDI runtime was installed.
  foreach(var source in new[]{"RTSP Network Stream","RTMP Network Stream","SRT Caller / Listener","UDP / RTP Stream","HLS / HTTP Stream","YouTube / Facebook URL","Screen Capture","Test Pattern"}) VideoSourceCombo.Items.Add(source);
  VideoSourceCombo.SelectedIndex=0;
  AudioSourceCombo.Items.Clear();AudioSourceCombo.Items.Add("AUTO – Follow Video Source");
  foreach(var device in probe.Audio)AudioSourceCombo.Items.Add("MANUAL – "+device);
  AudioSourceCombo.Items.Add("Audio Off / Video Only");AudioSourceCombo.SelectedIndex=0;
  PreviewStatus.Text=probe.Status;
 }
 static double D(string s,double f,double min,double max)=>double.TryParse(s,NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?Math.Clamp(v,min,max):f;
 static Color C(string s){try{return (Color)ColorConverter.ConvertFromString(s);}catch{return Colors.Lime;}}
 async void Refresh_Click(object s,RoutedEventArgs e)
 {
  try
  {
   if(IsNetworkSelection())
   {
    string url=SourceAddress.Text.Trim();if(url.Length==0)throw new InvalidOperationException("Enter the network/HTTPS source URL.");
    PreviewStatus.Text="RESOLVING NETWORK SOURCE…";var resolved=await UrlMediaResolver.ResolveAsync(url);
    PreviewStatus.Text=$"OPENING {resolved.Provider} PREVIEW…";await _sourcePreviewCapture.StartNetworkAsync(NetworkFfmpeg(),resolved.PlayableUrl,ResolveNetworkAudio(),1280,720,25);return;
   }
   if(IsSpecialSelection(out var special))
   {
    string? specialAudio=ResolveSpecialAudio();PreviewStatus.Text="OPENING "+special.ToUpperInvariant()+" PREVIEW…";
    if(special=="Screen Capture")await _sourcePreviewCapture.StartScreenAsync(NetworkFfmpeg(),specialAudio,1280,720,25);else await _sourcePreviewCapture.StartTestPatternAsync(NetworkFfmpeg(),specialAudio,1280,720,25);return;
   }
   if(VideoSourceCombo.SelectedItem is not LiveInputSource source){_sourcePreviewCapture.Stop();RefreshPreview();return;}
   string? audio=ResolveAudio(source);
   PreviewStatus.Text=$"OPENING PREVIEW • {source.Provider} • {source.Name}";
   if(source.Provider=="DECKLINK")await _sourcePreviewCapture.StartDeckLinkAsync(source.FfmpegPath,source.Name,audio,1280,720,25);
   else if(source.Provider=="NDI")await _sourcePreviewCapture.StartNdiAsync(source.FfmpegPath,source.Name,audio,1280,720,25);
   else await _sourcePreviewCapture.StartAsync(source.FfmpegPath,source.Name,audio,1280,720,25);
  }
  catch(Exception ex){PreviewStatus.Text="SOURCE PREVIEW FAILED • "+ex.Message;ModuleDiagnostics.ShowError("CHROMA_KEY","SOURCE_PREVIEW",ex,this);}
 }
 string? ResolveAudio(LiveInputSource source)
 {
  string selected=AudioSourceCombo.SelectedItem?.ToString()??"";
  if(selected.Contains("Audio Off",StringComparison.OrdinalIgnoreCase))return null;
  if(selected.StartsWith("MANUAL – ",StringComparison.Ordinal))return selected[9..];
  if(source.Provider=="DECKLINK"||source.Provider=="NDI")return LiveCaptureEngine.EmbeddedAudio;
  var sourceWords=source.Name.Split(new[]{' ','-','_','(',')'},StringSplitOptions.RemoveEmptyEntries);
  string? first=null,best=null;
  foreach(var item in AudioSourceCombo.Items){var value=item?.ToString()??"";if(!value.StartsWith("MANUAL – ",StringComparison.Ordinal))continue;var name=value[9..];first??=name;if(sourceWords.Any(w=>w.Length>3&&name.Contains(w,StringComparison.OrdinalIgnoreCase))){best=name;break;}}
  return best??first;
 }
 bool IsNetworkSelection()=>VideoSourceCombo.SelectedItem is string value&&(value.Contains("Network",StringComparison.OrdinalIgnoreCase)||value.Contains("Stream",StringComparison.OrdinalIgnoreCase)||value.Contains("URL",StringComparison.OrdinalIgnoreCase));
 string NetworkFfmpeg(){foreach(var file in new[]{RuntimePaths.DecoderExe,RuntimePaths.EncoderExe,RuntimePaths.DirectShowExe})if(File.Exists(file))return file;throw new FileNotFoundException("No bundled FFmpeg runtime is available for network input.");}
 string? ResolveNetworkAudio()
 {
  string selected=AudioSourceCombo.SelectedItem?.ToString()??"";if(selected.Contains("Audio Off",StringComparison.OrdinalIgnoreCase))return null;
  if(selected.StartsWith("MANUAL – ",StringComparison.Ordinal))return selected[9..];return LiveCaptureEngine.EmbeddedAudio;
 }
 bool IsSpecialSelection(out string kind){kind=VideoSourceCombo.SelectedItem?.ToString()??"";return kind=="Screen Capture"||kind=="Test Pattern";}
 string? ResolveSpecialAudio(){string selected=AudioSourceCombo.SelectedItem?.ToString()??"";return selected.StartsWith("MANUAL – ",StringComparison.Ordinal)?selected[9..]:null;}
 void RefreshPreview(){_source=_main.GetStudioPreviewFrame();InputPreview.Source=_source;ProgramPreview.Source=_source;RenderPreview();}
 void PreviewChanged(object s,System.Windows.Controls.TextChangedEventArgs e)
 {
  // TextChanged is raised while WPF is still constructing the XAML tree. At that
  // point a later slider has not necessarily been assigned to its x:Name field.
  // Do no visual/control synchronization until Loaded; the Loaded handler performs
  // the one safe initial synchronization after every control exists.
  if(!IsLoaded||_closed)return;
  if(!_syncingKeyControls)SyncKeySlidersFromText();
  UpdateKeyColorVisual();
  RenderPreview();
 }
 void KeySlider_ValueChanged(object s,RoutedPropertyChangedEventArgs<double> e)
 {
  if(!IsLoaded||_syncingKeyControls)return;
  try
  {
   _syncingKeyControls=true;
   string value=e.NewValue.ToString("0.00",CultureInfo.InvariantCulture);
   if(ReferenceEquals(s,ToleranceSlider))Tolerance.Text=value;
   else if(ReferenceEquals(s,DullnessSlider))Dullness.Text=value;
   else if(ReferenceEquals(s,SoftnessSlider))Softness.Text=value;
   else if(ReferenceEquals(s,SpillSlider))SpillSuppression.Text=value;
   else if(ReferenceEquals(s,SpectralSlider))SpectralBalance.Text=value;
   else if(ReferenceEquals(s,EdgeSoftnessSlider))EdgeSoftness.Text=value;
   else if(ReferenceEquals(s,ShadowSlider))ShadowRecovery.Text=value;
  }
  finally{_syncingKeyControls=false;}
  if(IsLoaded&&!_closed)RenderPreview();
 }
 void SyncKeySlidersFromText()
 {
  if(ToleranceSlider==null||DullnessSlider==null||SoftnessSlider==null||SpillSlider==null||SpectralSlider==null||EdgeSoftnessSlider==null||ShadowSlider==null)return;
  try
  {
   _syncingKeyControls=true;
   ToleranceSlider.Value=D(Tolerance.Text,.42,.01,1);DullnessSlider.Value=D(Dullness.Text,0,0,1);SoftnessSlider.Value=D(Softness.Text,.18,0,.5);
   SpillSlider.Value=D(SpillSuppression.Text,.65,0,1);SpectralSlider.Value=D(SpectralBalance.Text,.5,0,1);EdgeSoftnessSlider.Value=D(EdgeSoftness.Text,.18,0,1);ShadowSlider.Value=D(ShadowRecovery.Text,.2,0,1);
  }
  finally{_syncingKeyControls=false;}
 }
 void UpdateKeyColorVisual()
 {
  if(KeyColorSwatch!=null)KeyColorSwatch.Background=new SolidColorBrush(C(KeyHex?.Text??"#00FF00"));
 }
 async void TestUrl_Click(object s,RoutedEventArgs e)
 {
  try
  {
   string url=SourceAddress.Text.Trim();if(url.Length==0)throw new InvalidOperationException("Enter the network/HTTPS source URL.");
   PreviewStatus.Text="VALIDATING URL…";var resolved=await UrlMediaResolver.ResolveAsync(url);
   PreviewStatus.Text=$"URL READY • {resolved.Provider} • PRESS REFRESH SOURCE FOR PREVIEW";
  }
  catch(Exception ex){PreviewStatus.Text="URL CHECK FAILED • "+ex.Message;ModuleDiagnostics.ShowError("CHROMA_KEY","URL_VALIDATE",ex,this);}
 }
 void PreviewTransformChanged(object s,System.Windows.Controls.TextChangedEventArgs e)
 {
  if(!IsLoaded||_closed)return;
  if(LockAspect.IsChecked==true&&!_syncingAspect&&(ReferenceEquals(s,TransformW)||ReferenceEquals(s,TransformH)))
  {
   try{_syncingAspect=true;if(ReferenceEquals(s,TransformW))TransformH.Text=TransformW.Text;else TransformW.Text=TransformH.Text;}finally{_syncingAspect=false;}
  }
  ApplyPreviewTransform();
 }
 void PreviewTransformSelectionChanged(object s,System.Windows.Controls.SelectionChangedEventArgs e){if(IsLoaded&&!_closed)ApplyPreviewTransform();}
 void PreviewTransformToggleChanged(object s,RoutedEventArgs e){if(IsLoaded&&!_closed){if(LockAspect.IsChecked==true)TransformH.Text=TransformW.Text;ApplyPreviewTransform();}}
 void KeyedPreviewSurface_SizeChanged(object s,SizeChangedEventArgs e){if(IsLoaded&&!_closed)ApplyPreviewTransform();}
 static bool IsBackgroundVideo(string file)=>new[]{".mp4",".mov",".mkv",".avi",".mpg",".mpeg",".m2v",".webm",".ts",".m2ts"}.Contains(Path.GetExtension(file),StringComparer.OrdinalIgnoreCase);
 async void Browse_Click(object s,RoutedEventArgs e)
 {
  var d=new Microsoft.Win32.OpenFileDialog{Filter="Background media|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.mp4;*.mov;*.mkv;*.avi;*.mpg;*.mpeg;*.m2v;*.webm;*.ts;*.m2ts|Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Video files|*.mp4;*.mov;*.mkv;*.avi;*.mpg;*.mpeg;*.m2v;*.webm;*.ts;*.m2ts|All files|*.*"};
  if(d.ShowDialog()!=true)return;BackgroundPath.Text=d.FileName;
  try
  {
   if(IsBackgroundVideo(d.FileName)){PreviewStatus.Text="LOADING MUTED BACKGROUND VIDEO…";await _main.ApplyStudioKeyBackgroundVideoAsync(d.FileName,true);PreviewStatus.Text="BACKGROUND VIDEO • LOOP ON • AUDIO MUTED";}
   else{_main.ApplyStudioKeyBackground(d.FileName);PreviewStatus.Text="BACKGROUND IMAGE READY";}
   RenderPreview();
  }
  catch(Exception ex){PreviewStatus.Text="BACKGROUND LOAD FAILED • "+ex.Message;ModuleDiagnostics.ShowError("CHROMA_KEY","BACKGROUND_LOAD",ex,this);}
 }
 void RenderPreview()
 {
  if(_source==null){ForegroundPreview.Source=null;MattePreview.Source=null;PreviewStatus.Text="NO CURRENT PROGRAM FRAME • START/TAKE VIDEO THEN REFRESH";return;}
  try
  {
   // Diagnostic previews do not need full-program resolution. Processing a bounded
   // preview prevents 1080p/4K sources from allocating several full-size buffers on
   // every live frame. The final compositor still receives the original resolution.
   var fmt=new FormatConvertedBitmap(PreviewSized(_source),PixelFormats.Bgra32,null,0);
   int w=fmt.PixelWidth,h=fmt.PixelHeight,stride=w*4;
   var pixels=new byte[h*stride]; var matte=new byte[h*stride];var spillView=new byte[h*stride];var difference=new byte[h*stride];var planar=new byte[h*stride];
   fmt.CopyPixels(pixels,stride,0);
   var original=(byte[])pixels.Clone();
   var key=C(KeyHex?.Text??"#00FF00");
   double tolerance=D(Tolerance?.Text??"",.18,.01,1), softness=D(Softness?.Text??"",.08,0,.5);
   double spill=D(SpillSuppression?.Text??"",.65,0,1), dullness=D(Dullness?.Text??"",0,0,1), spectral=D(SpectralBalance?.Text??"",.5,0,1);
   System.Threading.Tasks.Parallel.For(0,h,y=>{for(int i=y*stride;i<(y+1)*stride;i+=4)
   {
    double db=pixels[i]-key.B,dg=pixels[i+1]-key.G,dr=pixels[i+2]-key.R;
    double chromaDistance=Math.Sqrt(dr*dr+dg*dg+db*db)/(Math.Sqrt(3)*255.0);
    double dominant=(key.G>=key.B?Math.Abs(dg):Math.Abs(db))/255.0;
    double distance=chromaDistance*(1-spectral)+dominant*spectral;
    double saturation=(Math.Max(pixels[i],Math.Max(pixels[i+1],pixels[i+2]))-Math.Min(pixels[i],Math.Min(pixels[i+1],pixels[i+2])))/255.0;
    double adjustedTolerance=Math.Clamp(tolerance+(1-saturation)*dullness*.10,.001,1);
    double alpha=distance<=adjustedTolerance?0:distance>=adjustedTolerance+softness?1:(softness<=0?1:(distance-adjustedTolerance)/softness);
    byte a=(byte)Math.Clamp((int)Math.Round(pixels[i+3]*alpha),0,255);
    if(alpha<1&&spill>0)
    {
     double neutral=pixels[i+2]*.2126+pixels[i+1]*.7152+pixels[i]*.0722;
     double amount=spill*(1-alpha);
     pixels[i]=(byte)Math.Clamp((int)Math.Round(pixels[i]+(neutral-pixels[i])*amount),0,255);
     pixels[i+1]=(byte)Math.Clamp((int)Math.Round(pixels[i+1]+(neutral-pixels[i+1])*amount),0,255);
     pixels[i+2]=(byte)Math.Clamp((int)Math.Round(pixels[i+2]+(neutral-pixels[i+2])*amount),0,255);
    }
    pixels[i+3]=a;
    matte[i]=matte[i+1]=matte[i+2]=a; matte[i+3]=255;
    int colourDelta=(Math.Abs(original[i]-pixels[i])+Math.Abs(original[i+1]-pixels[i+1])+Math.Abs(original[i+2]-pixels[i+2]))/3;
    byte spillAmount=(byte)Math.Clamp(colourDelta+(255-a)/3,0,255);spillView[i]=0;spillView[i+1]=spillAmount;spillView[i+2]=spillAmount;spillView[i+3]=255;
    difference[i]=(byte)Math.Abs(original[i]-pixels[i]);difference[i+1]=(byte)Math.Abs(original[i+1]-pixels[i+1]);difference[i+2]=(byte)Math.Abs(original[i+2]-pixels[i+2]);difference[i+3]=255;
    byte plane=(byte)Math.Clamp((int)Math.Round(distance*255),0,255);planar[i]=plane;planar[i+1]=plane;planar[i+2]=plane;planar[i+3]=255;
   }});
   var split=(byte[])original.Clone();for(int y=0;y<h;y++)Buffer.BlockCopy(pixels,y*stride+(w/2)*4,split,y*stride+(w/2)*4,(w-w/2)*4);
   _keyedFrame=MakeFrame(w,h,stride,pixels);_matteFrame=MakeFrame(w,h,stride,matte);_spillFrame=MakeFrame(w,h,stride,spillView);_differenceFrame=MakeFrame(w,h,stride,difference);_splitFrame=MakeFrame(w,h,stride,split);_planarFrame=MakeFrame(w,h,stride,planar);
   MattePreview.Source=_matteFrame;UpdatePreviewMode();
   PreviewStatus.Text=$"KEY + MATTE PREVIEW • {KeyHex.Text} • TOL {tolerance:0.###} • DULL {dullness:0.##} • SPECTRAL {spectral:0.##} • SPILL {spill:0.##}";
  }
  catch(Exception ex){PreviewStatus.Text="PREVIEW ERROR • "+ex.Message;ModuleDiagnostics.ShowError("CHROMA_KEY","COMPOSITE_PREVIEW",ex,this);}
 }
 static BitmapSource PreviewSized(BitmapSource source)
 {
  const double maxWidth=640,maxHeight=360;
  double scale=Math.Min(1,Math.Min(maxWidth/source.PixelWidth,maxHeight/source.PixelHeight));
  if(scale>=.999)return source;
  var resized=new TransformedBitmap(source,new ScaleTransform(scale,scale));resized.Freeze();return resized;
 }
 static BitmapSource MakeFrame(int width,int height,int stride,byte[] pixels){var frame=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,stride);frame.Freeze();return frame;}
 void UpdatePreviewMode()
 {
  ForegroundPreview.Visibility=Visibility.Visible;
  ForegroundPreview.Source=_previewMode switch{"Matte"=>_matteFrame,"Spill"=>_spillFrame,"Difference"=>_differenceFrame,"Split"=>_splitFrame,"Planar"=>_planarFrame,_=>_keyedFrame};
  ApplyPreviewTransform();
 }
 void ApplyPreviewTransform()
 {
  if(KeyedPreviewSurface==null||ForegroundPreview==null)return;
  double surfaceW=KeyedPreviewSurface.ActualWidth,surfaceH=KeyedPreviewSurface.ActualHeight;if(surfaceW<=0||surfaceH<=0)return;
  var transform=CurrentTransform();ForegroundPreview.Width=Math.Max(1,surfaceW*transform.Width);ForegroundPreview.Height=Math.Max(1,surfaceH*transform.Height);
  ForegroundPreview.Margin=new Thickness(surfaceW*transform.X,surfaceH*transform.Y,0,0);
  ForegroundPreview.Stretch=ScaleMode.SelectedIndex switch{1=>Stretch.Fill,2=>Stretch.UniformToFill,_=>Stretch.Uniform};
  ForegroundPreview.RenderTransformOrigin=new Point(.5,.5);ForegroundPreview.RenderTransform=new RotateTransform(D(Rotation.Text,0,-360,360));
 }
 Rect CurrentTransform()
 {
  double width=D(TransformW.Text,100,1,200)/100;
  double height=LockAspect.IsChecked==true?width:D(TransformH.Text,100,1,200)/100;
  return new Rect(D(TransformX.Text,0,-100,100)/100,D(TransformY.Text,0,-100,100)/100,width,height);
 }
 async void Apply_Click(object s,RoutedEventArgs e){try{if(IsNetworkSelection()){string url=SourceAddress.Text.Trim();if(url.Length==0)throw new InvalidOperationException("Enter the network/HTTPS source URL.");_sourcePreviewCapture.Stop();PreviewStatus.Text="RESOLVING NETWORK SOURCE…";var resolved=await UrlMediaResolver.ResolveAsync(url);PreviewStatus.Text=$"{resolved.Provider} HANDOFF TO FINAL PROGRAM…";await _main.StartLiveNetworkInputAsync(NetworkFfmpeg(),resolved.PlayableUrl,ResolveNetworkAudio());}else if(IsSpecialSelection(out var special)){_sourcePreviewCapture.Stop();PreviewStatus.Text=special.ToUpperInvariant()+" HANDOFF TO FINAL PROGRAM…";await _main.StartLiveSpecialInputAsync(NetworkFfmpeg(),special,ResolveSpecialAudio());}else if(VideoSourceCombo.SelectedItem is LiveInputSource source){string? audio=ResolveAudio(source);_sourcePreviewCapture.Stop();PreviewStatus.Text="HANDOFF TO FINAL PROGRAM…";if(source.Provider=="DECKLINK")await _main.StartLiveDeckLinkInputAsync(source.FfmpegPath,source.Name,audio);else if(source.Provider=="NDI")await _main.StartLiveNdiInputAsync(source.FfmpegPath,source.Name,audio);else await _main.StartLiveInputAsync(source.FfmpegPath,source.Name,audio);}var c=C(KeyHex.Text);if(!string.IsNullOrWhiteSpace(BackgroundPath.Text)&&File.Exists(BackgroundPath.Text)&&!IsBackgroundVideo(BackgroundPath.Text))_main.ApplyStudioKeyBackground(BackgroundPath.Text);var stretch=ScaleMode.SelectedIndex switch{1=>Stretch.Fill,2=>Stretch.UniformToFill,_=>Stretch.Uniform};_main.ConfigureStudioChromaKeyAdvanced(EnableCheck.IsChecked==true,c.R,c.G,c.B,D(Tolerance.Text,.18,.01,1),D(Softness.Text,.08,0,.5),D(SpillSuppression.Text,.65,0,1),D(Dullness.Text,0,0,1),D(SpectralBalance.Text,.5,0,1),D(EdgeSoftness.Text,.18,0,1),D(EdgeErode.Text,0,-1,1),D(ShadowRecovery.Text,.2,0,1),CurrentTransform(),new Thickness(D(CropLeft.Text,0,0,49)/100,D(CropTop.Text,0,0,49)/100,D(CropRight.Text,0,0,49)/100,D(CropBottom.Text,0,0,49)/100),D(Rotation.Text,0,-360,360),stretch);PreviewStatus.Text="ADVANCED KEY APPLIED TO FINAL PROGRAM";ModuleDiagnostics.WriteInfo("CHROMA_KEY","TAKE TO AIR APPLIED • "+KeyHex.Text);}catch(Exception ex){PreviewStatus.Text="KEY ERROR • "+ex.Message;ModuleDiagnostics.ShowError("CHROMA_KEY","TAKE_TO_AIR",ex,this);}}
 void Off_Click(object s,RoutedEventArgs e){EnableCheck.IsChecked=false;_main.ConfigureStudioChromaKey(false,0,255,0,.18,.08);_main.ClearStudioKeyBackground();BackgroundPreview.Source=null;PreviewStatus.Text="KEY OFF";}
 void GreenPreset_Click(object s,RoutedEventArgs e)=>KeyHex.Text="#00FF00";
 void BluePreset_Click(object s,RoutedEventArgs e)=>KeyHex.Text="#0000FF";
 void Eyedropper_Click(object s,RoutedEventArgs e){if(_source==null){PreviewStatus.Text="NO FRAME FOR EYEDROPPER";return;}_eyedropperArmed=true;PreviewStatus.Text="EYEDROPPER ARMED • CLICK ANY COLOUR IN INPUT PREVIEW";}
 void InputPreview_MouseLeftButtonDown(object s,System.Windows.Input.MouseButtonEventArgs e)
 {
  if(!_eyedropperArmed||_source==null)return;
  var point=e.GetPosition(InputPreview);double viewW=InputPreview.ActualWidth,viewH=InputPreview.ActualHeight;
  double scale=Math.Min(viewW/_source.PixelWidth,viewH/_source.PixelHeight);
  double drawW=_source.PixelWidth*scale,drawH=_source.PixelHeight*scale,offsetX=(viewW-drawW)/2,offsetY=(viewH-drawH)/2;
  if(point.X<offsetX||point.Y<offsetY||point.X>=offsetX+drawW||point.Y>=offsetY+drawH){PreviewStatus.Text="CLICK INSIDE THE VIDEO IMAGE";return;}
  int x=Math.Clamp((int)((point.X-offsetX)/scale),0,_source.PixelWidth-1),y=Math.Clamp((int)((point.Y-offsetY)/scale),0,_source.PixelHeight-1);
  var frame=new FormatConvertedBitmap(_source,PixelFormats.Bgra32,null,0);var pixel=new byte[4];frame.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);
  KeyHex.Text=$"#{pixel[2]:X2}{pixel[1]:X2}{pixel[0]:X2}";_eyedropperArmed=false;PreviewStatus.Text=$"KEY COLOUR SAMPLED • X {x} • Y {y} • {KeyHex.Text}";
 }
 void PreviewMode_Click(object s,RoutedEventArgs e){if(s is not System.Windows.Controls.Button b)return;_previewMode=b.Tag?.ToString()??"Composite";UpdatePreviewMode();PreviewStatus.Text=$"PREVIEW MODE • {_previewMode.ToUpperInvariant()}";}
 void Track_Click(object s,RoutedEventArgs e){PreviewStatus.Text="TRACK TO HERE • CURRENT TRANSFORM CAPTURED";Apply_Click(s,e);}
 void Reset_Click(object s,RoutedEventArgs e){KeyHex.Text="#00FF00";Tolerance.Text=".42";Dullness.Text="0";Softness.Text=".18";SpillSuppression.Text=".65";SpectralBalance.Text=".50";EdgeSoftness.Text=".18";EdgeErode.Text="0";ShadowRecovery.Text=".20";TransformX.Text=TransformY.Text="0";TransformW.Text=TransformH.Text="100";Rotation.Text="0";CropLeft.Text=CropRight.Text=CropTop.Text=CropBottom.Text="0";ScaleMode.SelectedIndex=0;RenderPreview();}
 string PresetPath=>Path.Combine(DataStorage.Settings,"ChromaKeyPreset.json");
 void SavePreset_Click(object s,RoutedEventArgs e){try{DataStorage.EnsureCreated();var p=new{KeyHex=KeyHex.Text,Tolerance=Tolerance.Text,Dullness=Dullness.Text,Softness=Softness.Text,Spill=SpillSuppression.Text,Spectral=SpectralBalance.Text,Edge=EdgeSoftness.Text,Erode=EdgeErode.Text,Shadow=ShadowRecovery.Text,X=TransformX.Text,Y=TransformY.Text,W=TransformW.Text,H=TransformH.Text,Rotation=Rotation.Text,CropL=CropLeft.Text,CropR=CropRight.Text,CropT=CropTop.Text,CropB=CropBottom.Text,LockAspect=LockAspect.IsChecked==true,Scale=ScaleMode.SelectedIndex};DataStorage.WritePersistentText(PresetPath,JsonSerializer.Serialize(p,new JsonSerializerOptions{WriteIndented=true}),"Settings");PreviewStatus.Text="CHROMA PRESET SAVED";}catch(Exception ex){PreviewStatus.Text="PRESET SAVE FAILED • "+ex.Message;}}
 void LoadPreset_Click(object s,RoutedEventArgs e){try{if(!File.Exists(PresetPath)){PreviewStatus.Text="NO SAVED CHROMA PRESET";return;}using var d=JsonDocument.Parse(File.ReadAllText(PresetPath));var r=d.RootElement;string G(string n,string f)=>r.TryGetProperty(n,out var e)?e.GetString()??f:f;KeyHex.Text=G("KeyHex","#00FF00");Tolerance.Text=G("Tolerance",".42");Dullness.Text=G("Dullness","0");Softness.Text=G("Softness",".18");SpillSuppression.Text=G("Spill",".65");SpectralBalance.Text=G("Spectral",".50");EdgeSoftness.Text=G("Edge",".18");EdgeErode.Text=G("Erode","0");ShadowRecovery.Text=G("Shadow",".20");TransformX.Text=G("X","0");TransformY.Text=G("Y","0");TransformW.Text=G("W","100");TransformH.Text=G("H","100");Rotation.Text=G("Rotation","0");CropLeft.Text=G("CropL","0");CropRight.Text=G("CropR","0");CropTop.Text=G("CropT","0");CropBottom.Text=G("CropB","0");if(r.TryGetProperty("LockAspect",out var la))LockAspect.IsChecked=la.GetBoolean();if(r.TryGetProperty("Scale",out var q))ScaleMode.SelectedIndex=Math.Clamp(q.GetInt32(),0,2);RenderPreview();PreviewStatus.Text="CHROMA PRESET LOADED";}catch(Exception ex){PreviewStatus.Text="PRESET LOAD FAILED • "+ex.Message;}}
 void Close_Click(object s,RoutedEventArgs e)=>Close();
}

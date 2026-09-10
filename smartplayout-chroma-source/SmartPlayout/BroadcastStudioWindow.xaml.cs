using System;using System.Collections.Generic;using System.Collections.ObjectModel;using System.Globalization;using System.IO;using System.Linq;using System.Text.Json;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Input;using System.Windows.Media;using System.Windows.Media.Imaging;using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using ColorConverter = System.Windows.Media.ColorConverter;
namespace FFmpegNativePlayer;
public partial class BroadcastStudioWindow:Window
{
 readonly MainWindow _main; readonly List<string> _sequenceFiles=new(); readonly string ProfilePath=Path.Combine(DataStorage.Settings,"CgDesigner.json");readonly string LegacyProfilePath=Path.Combine(DataStorage.Root,"Settings","CgDesigner.json");
 readonly ObservableCollection<CgLayerItem> _layers=new();readonly Dictionary<string,CancellationTokenSource> _layerSchedules=new();readonly DispatcherTimer _uiTimer=new(){Interval=TimeSpan.FromMilliseconds(100)};string? _templatePath;bool _previewRunning;int _duplicateCounter;DateTime _previewStarted;
 FrameworkElement? _dragElement; string _dragKind=""; Point _dragStart; double _dragStartX,_dragStartY,_watermarkResizeStartScale,_watermarkResizeStartSize,_logoResizeStartW,_logoResizeStartH,_lowerResizeStartW,_lowerResizeStartH; bool _watermarkOnAir;
 sealed class CgProfile
 {
  public string Cg1{get;set;}="SMART PLAYOUT";public double X{get;set;}=.05;public double Y{get;set;}=.82;public double Size{get;set;}=36;
  public string Logo{get;set;}="";public double LogoX{get;set;}=.75;public double LogoY{get;set;}=.05;public double LogoW{get;set;}=.2;public double LogoH{get;set;}=.15;public double LogoOpacity{get;set;}=1;public int LogoStretch{get;set;}public bool LogoLockAspect{get;set;}=true;
  public string WatermarkText{get;set;}="SR MUSIC";public string WatermarkFont{get;set;}="Segoe UI Semibold";public bool WatermarkBold{get;set;}=true;public bool WatermarkItalic{get;set;}public string WatermarkColor{get;set;}="#FFFFFF";
  public double WatermarkOpacity{get;set;}=.45;public double WatermarkSize{get;set;}=42;public double WatermarkX{get;set;}=.82;public double WatermarkY{get;set;}=.08;public double WatermarkScale{get;set;}=1;public double WatermarkRotation{get;set;}public double WatermarkOutline{get;set;}=1;
  public bool WatermarkShadow{get;set;}=true;public bool WatermarkLockAspect{get;set;}=true;public bool WatermarkSafeArea{get;set;}=true;public int WatermarkZ{get;set;}=180;public bool WatermarkAlwaysOn{get;set;}
  public string LowerTemplateName{get;set;}="News Lower Third - Standard";public string LowerText{get;set;}="SARAH MITCHELL";public string LowerSecondary{get;set;}="NEWS PRESENTER";public string LowerFont{get;set;}="Segoe UI";public double LowerFontSize{get;set;}=48;public string LowerTextColor{get;set;}="#FFFFFF";public string LowerBackgroundColor{get;set;}="#0B4ED7";public double LowerBackgroundOpacity{get;set;}=.9;public double LowerX{get;set;}=.05;public double LowerY{get;set;}=.75;public double LowerW{get;set;}=.60;public double LowerH{get;set;}=.12;public string LowerAnimationIn{get;set;}="Slide In Left";public string LowerAnimationOut{get;set;}="Slide Out Left";public double LowerAnimationDuration{get;set;}=.6;public List<CgLayerState> Layers{get;set;}=new();
  public string Cg2{get;set;}="SECONDARY TITLE";public double Cg2X{get;set;}=.05;public double Cg2Y{get;set;}=.9;public double Cg2Size{get;set;}=24;
  public string MoveText{get;set;}="BREAKING NEWS • SMART PLAYOUT";public double MoveAxis{get;set;}=.88;public double MoveSize{get;set;}=28;public double MoveSpeed{get;set;}=.12;
  public double ClockX{get;set;}=.82;public double ClockY{get;set;}=.04;public double ClockSize{get;set;}=30;public string NowText{get;set;}="NOW PLAYING";public string NextText{get;set;}="NEXT PLAYING";
  public double ShapeX{get;set;}=.02;public double ShapeY{get;set;}=.72;public double ShapeW{get;set;}=.70;public double ShapeH{get;set;}=.16;public string ShapeHex{get;set;}="#17364D";
  public string FullPath{get;set;}="";public double FullOpacity{get;set;}=1;public string PipPath{get;set;}="";public double PipX{get;set;}=.70;public double PipY{get;set;}=.08;public double PipW{get;set;}=.26;public double PipH{get;set;}=.26;public double PipOpacity{get;set;}=1;public bool PipLoop{get;set;}=true;
  public List<string> SequenceFiles{get;set;}=new();public double SeqX{get;set;}=.05;public double SeqY{get;set;}=.05;public double SeqW{get;set;}=.30;public double SeqH{get;set;}=.30;public double SeqOpacity{get;set;}=1;public double SeqFps{get;set;}=12;public bool SeqLoop{get;set;}=true;
  }
 sealed class CgLayerState{public int Order{get;set;}public string Name{get;set;}="";public string Id{get;set;}="";public string Card{get;set;}="";public bool Visible{get;set;}=true;public bool Locked{get;set;}public string StartText{get;set;}="00:00:00";public string DurationText{get;set;}="00:00:30";}
 sealed class CgLayerItem
 {
  public int Order{get;set;}public string Name{get;set;}="";public string Id{get;set;}="";public string Card{get;set;}="";public bool Visible{get;set;}=true;public bool Locked{get;set;}public string StartText{get;set;}="00:00:00";public string DurationText{get;set;}="00:00:30";public Brush BarBrush{get;set;}=Brushes.DodgerBlue;
 }
 public BroadcastStudioWindow(MainWindow main){_main=main;InitializeComponent();InitializeLayers();_uiTimer.Tick+=(_,__)=>UpdateStudioHeader();Loaded+=(_,__)=>{_main.StudioAudioLevelChanged+=OnAudioLevel;_uiTimer.Start();LoadProfile(false);SelectCard(LowerCard);SelectLayerById("studio.cg.lowerthird");RefreshCanvas();};Closed+=(_,__)=>{_uiTimer.Stop();_main.StudioAudioLevelChanged-=OnAudioLevel;foreach(var c in _layerSchedules.Values)c.Cancel();_layerSchedules.Clear();};}
 void OnAudioLevel(float left,float right){CgAudioL.Value=Math.Clamp(left,0,1);CgAudioR.Value=Math.Clamp(right,0,1);}
 void UpdateStudioHeader(){CgHeaderClock.Text=DateTime.Now.ToString("HH:mm:ss",CultureInfo.InvariantCulture);if(_previewRunning)CgTimelineClock.Text=$"{(DateTime.UtcNow-_previewStarted):hh\\:mm\\:ss} / 00:00:30";}
 void InitializeLayers()
 {
  AddLayer("Background","studio.cg.fullscreen","Full",Color.FromRgb(100,120,140));AddLayer("Shape","studio.cg.shape","Shape",Color.FromRgb(128,70,205));AddLayer("Lower Third","studio.cg.lowerthird","Lower",Color.FromRgb(0,132,220));AddLayer("Logo / Image","studio.logo.primary","Logo",Color.FromRgb(45,190,105));AddLayer("Text Watermark","studio.cg.watermark","Watermark",Color.FromRgb(30,190,205));AddLayer("Clock","studio.cg.clock","Clock",Color.FromRgb(245,173,55));AddLayer("Ticker","studio.cg.ticker","Move",Color.FromRgb(225,45,65));AddLayer("Vertical Roll","studio.cg.roll","Move",Color.FromRgb(210,80,120));AddLayer("Now / Next","studio.cg.now","Now",Color.FromRgb(70,140,225));AddLayer("L-Shape","studio.cg.lshape","Shape",Color.FromRgb(120,95,210));AddLayer("PIP","studio.pip.video","Pip",Color.FromRgb(45,175,175));AddLayer("Image Sequence","studio.image.sequence","Sequence",Color.FromRgb(180,115,35));
  LayerList.ItemsSource=_layers;
 }
 void AddLayer(string name,string id,string card,Color color)=>_layers.Add(new CgLayerItem{Order=_layers.Count+1,Name=name,Id=id,Card=card,BarBrush=new SolidColorBrush(color)});
 static double D(TextBox b,double f,double min=double.MinValue,double max=double.MaxValue)=>double.TryParse(b.Text,NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?Math.Clamp(v,min,max):f;
 static double Percent(TextBox b,double f,double min=-100,double max=100)=>D(b,f*100,min,max)/100.0;
 static int I(TextBox b,int f,int min,int max)=>int.TryParse(b.Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var v)?Math.Clamp(v,min,max):f;
 static string ComboText(ComboBox combo,string fallback)=>combo.SelectedItem is ComboBoxItem item&&item.Content!=null?item.Content.ToString()??fallback:fallback;
 Stretch LogoStretchMode()=>LogoStretch.SelectedIndex switch{1=>Stretch.Fill,2=>Stretch.UniformToFill,_=>Stretch.Uniform};
 Color WatermarkParsedColor(){try{return(Color)ColorConverter.ConvertFromString(WatermarkColor.Text);}catch{return Colors.White;}}
 (bool bold,bool italic) WatermarkStyleFlags(){string style=ComboText(WatermarkStyle,"Bold");return(style.Contains("Bold",StringComparison.OrdinalIgnoreCase),style.Contains("Italic",StringComparison.OrdinalIgnoreCase));}
 IEnumerable<FrameworkElement> PropertyCards(){yield return WatermarkCard;yield return TextCard;yield return LowerCard;yield return LogoCard;yield return MoveCard;yield return ClockCard;yield return NowCard;yield return ShapeCard;yield return FullCard;yield return PipCard;yield return SequenceCard;}
 void SelectCard(FrameworkElement selected){foreach(var card in PropertyCards())card.Visibility=ReferenceEquals(card,selected)?Visibility.Visible:Visibility.Collapsed;selected.BringIntoView();}
 void RefreshPreview_Click(object s,RoutedEventArgs e)=>RefreshCanvas();
 void RefreshCanvas(){ProgramPreview.Source=_main.GetStudioPreviewFrame();RenderCanvasPreview();}
 void PreviewTextChanged(object s,TextChangedEventArgs e){if(IsLoaded)RenderCanvasPreview();}
 void RenderCanvasPreview(){
  if(CgCanvas==null)return; CgCanvas.Children.Clear();
  // 90%/80% action/title safe guides, similar to a broadcast CG layout editor.
  if(WatermarkSafeArea?.IsChecked!=false){AddGuide(48,27,864,486,"ACTION SAFE"); AddGuide(96,54,768,432,"TITLE SAFE");}
  AddTextPreview("PRIMARY",CgText?.Text??"",D(CgX,.05,0,1),D(CgY,.82,0,1),D(CgSize,36,8,120),Brushes.White,145);
  AddTextPreview("SECONDARY",Cg2Text?.Text??"",D(Cg2X,.05,0,1),D(Cg2Y,.90,0,1),D(Cg2Size,24,8,120),Brushes.White,135);
  var lowerBg=Hex(LowerBackgroundColor?.Text??"#0B4ED7");var lowerTextColor=Hex(LowerTextColor?.Text??"#FFFFFF");var lowerContent=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,2,8,2)};lowerContent.Children.Add(new TextBlock{Text=LowerText?.Text??"",Foreground=new SolidColorBrush(Color.FromRgb(lowerTextColor.r,lowerTextColor.g,lowerTextColor.b)),FontFamily=new FontFamily(ComboText(LowerFont,"Segoe UI")),FontWeight=FontWeights.Bold,FontSize=D(LowerSize,48,8,160)*.5,TextTrimming=TextTrimming.CharacterEllipsis});lowerContent.Children.Add(new TextBlock{Text=LowerSecondaryText?.Text??"",Foreground=new SolidColorBrush(Color.FromRgb(lowerTextColor.r,lowerTextColor.g,lowerTextColor.b)),FontFamily=new FontFamily(ComboText(LowerFont,"Segoe UI")),FontSize=Math.Max(8,D(LowerSize,48,8,160)*.27),TextTrimming=TextTrimming.CharacterEllipsis});
  double lowerWidth=960*D(LowerW,.60,.05,1),lowerHeight=540*D(LowerH,.12,.03,.5);var lowerHolder=new Canvas{Tag="LOWER",Width=lowerWidth,Height=lowerHeight,Background=Brushes.Transparent};var lower=new Border{IsHitTestVisible=false,Background=new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(D(LowerBackgroundOpacity,90,0,100)*2.55,0,255),lowerBg.r,lowerBg.g,lowerBg.b)),BorderBrush=Brushes.White,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(3),Width=lowerWidth,Height=lowerHeight,Child=lowerContent};lowerHolder.Children.Add(lower);AddResizeHandle(lowerHolder,-4,-4,"LOWER_RESIZE");AddResizeHandle(lowerHolder,lowerWidth-4,-4,"LOWER_RESIZE");AddResizeHandle(lowerHolder,-4,lowerHeight-4,"LOWER_RESIZE");AddResizeHandle(lowerHolder,lowerWidth-4,lowerHeight-4,"LOWER_RESIZE");
  Canvas.SetLeft(lowerHolder,960*D(LowerX,.04,0,1));Canvas.SetTop(lowerHolder,540*D(LowerY,.72,0,1));CgCanvas.Children.Add(lowerHolder);
  if(LogoPath!=null&&!string.IsNullOrWhiteSpace(LogoPath.Text)&&File.Exists(LogoPath.Text))
  {
   try
   {
    var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.UriSource=new Uri(LogoPath.Text);bi.EndInit();bi.Freeze();double w=960*D(LogoW,.2,.01,1),h=540*D(LogoH,.15,.01,1);
    var holder=new Canvas{Tag="LOGO",Width=w,Height=h,Background=Brushes.Transparent};var img=new Image{IsHitTestVisible=false,Source=bi,Width=w,Height=h,Opacity=D(LogoOpacity,1,0,1),Stretch=LogoStretchMode()};holder.Children.Add(img);
    var selection=new Border{IsHitTestVisible=false,Width=w,Height=h,BorderBrush=new SolidColorBrush(Color.FromRgb(99,231,229)),BorderThickness=new Thickness(1)};holder.Children.Add(selection);
    AddResizeHandle(holder,-4,-4,"LOGO_RESIZE");AddResizeHandle(holder,w-4,-4,"LOGO_RESIZE");AddResizeHandle(holder,-4,h-4,"LOGO_RESIZE");AddResizeHandle(holder,w-4,h-4,"LOGO_RESIZE");
    Canvas.SetLeft(holder,960*D(LogoX,.75,0,1));Canvas.SetTop(holder,540*D(LogoY,.05,0,1));CgCanvas.Children.Add(holder);
   }
   catch{}
  }
  var sh=Hex(ShapeHex?.Text??"#17364D");var shape=new Border{Tag="SHAPE",Width=960*D(ShapeW,.7,.01,1),Height=540*D(ShapeH,.16,.01,1),Background=new SolidColorBrush(Color.FromArgb(150,sh.r,sh.g,sh.b)),BorderBrush=new SolidColorBrush(Color.FromArgb(190,255,255,255)),BorderThickness=new Thickness(1)};Canvas.SetLeft(shape,960*D(ShapeX,.02,0,1));Canvas.SetTop(shape,540*D(ShapeY,.72,0,1));CgCanvas.Children.Insert(Math.Min(2,CgCanvas.Children.Count),shape);
  AddWatermarkPreview();
 }
 void AddGuide(double x,double y,double w,double h,string label){var b=new Border{IsHitTestVisible=false,Width=w,Height=h,BorderBrush=new SolidColorBrush(Color.FromArgb(85,120,205,255)),BorderThickness=new Thickness(1)};Canvas.SetLeft(b,x);Canvas.SetTop(b,y);CgCanvas.Children.Add(b);var t=new TextBlock{IsHitTestVisible=false,Text=label,Foreground=new SolidColorBrush(Color.FromArgb(110,170,220,255)),FontSize=10};Canvas.SetLeft(t,x+4);Canvas.SetTop(t,y+3);CgCanvas.Children.Add(t);}
 void AddTextPreview(string kind,string text,double x,double y,double size,Brush brush,byte alpha){var tb=new TextBlock{Tag=kind,Text=text,FontSize=size,Foreground=brush,Background=new SolidColorBrush(Color.FromArgb(alpha,0,0,0)),Padding=new Thickness(7),TextWrapping=TextWrapping.NoWrap};Canvas.SetLeft(tb,960*x);Canvas.SetTop(tb,540*y);CgCanvas.Children.Add(tb);}
 void AddWatermarkPreview()
 {
  if(WatermarkText==null||string.IsNullOrWhiteSpace(WatermarkText.Text))return;
  var color=WatermarkParsedColor();WatermarkColorSwatch.Background=new SolidColorBrush(color);
  var flags=WatermarkStyleFlags();double scale=D(WatermarkScale,100,20,400)/100.0;double size=D(WatermarkSize,42,8,240)*scale*.5;
  double estimatedWidth=Math.Clamp(WatermarkText.Text.Length*size*.68+20,42,900);double estimatedHeight=Math.Clamp(size*1.55+12,28,300);
  var holder=new Canvas{Tag="WATERMARK",Width=estimatedWidth,Height=estimatedHeight,Background=Brushes.Transparent,RenderTransformOrigin=new Point(.5,.5),RenderTransform=new RotateTransform(D(WatermarkRotation,0,-360,360))};
  var text=new TextBlock{IsHitTestVisible=false,Text=WatermarkText.Text,Opacity=D(WatermarkOpacity,45,0,100)/100.0,Foreground=new SolidColorBrush(color),FontFamily=new FontFamily(ComboText(WatermarkFont,"Segoe UI Semibold")),FontSize=size,FontWeight=flags.bold?FontWeights.Bold:FontWeights.Normal,FontStyle=flags.italic?FontStyles.Italic:FontStyles.Normal,Padding=new Thickness(7,3,7,3)};
  if(WatermarkShadow.IsChecked==true)text.Effect=new System.Windows.Media.Effects.DropShadowEffect{Color=Colors.Black,BlurRadius=Math.Max(2,D(WatermarkOutline,1,0,12)*2),ShadowDepth=2,Opacity=.75};
  var selection=new Border{IsHitTestVisible=false,Width=estimatedWidth,Height=estimatedHeight,BorderBrush=new SolidColorBrush(Color.FromRgb(99,231,229)),BorderThickness=new Thickness(1),Child=text};holder.Children.Add(selection);
  AddResizeHandle(holder,-4,-4,"WATERMARK_RESIZE");AddResizeHandle(holder,estimatedWidth-4,-4,"WATERMARK_RESIZE");AddResizeHandle(holder,-4,estimatedHeight-4,"WATERMARK_RESIZE");AddResizeHandle(holder,estimatedWidth-4,estimatedHeight-4,"WATERMARK_RESIZE");
  Canvas.SetLeft(holder,960*Percent(WatermarkX,.82));Canvas.SetTop(holder,540*Percent(WatermarkY,.08));CgCanvas.Children.Add(holder);
 }
 static void AddResizeHandle(Canvas holder,double x,double y,string tag){var h=new Border{Tag=tag,Width=9,Height=9,Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(99,231,229)),BorderThickness=new Thickness(1),Cursor=Cursors.SizeNWSE};Canvas.SetLeft(h,x);Canvas.SetTop(h,y);holder.Children.Add(h);}
 FrameworkElement? FindTaggedElement(object? source)
 {
  var current=source as DependencyObject;
  while(current!=null&&current!=CgCanvas){if(current is FrameworkElement fe&&!string.IsNullOrWhiteSpace(fe.Tag?.ToString()))return fe;current=VisualTreeHelper.GetParent(current);}
  return null;
 }
 void CgCanvas_MouseLeftButtonDown(object s,MouseButtonEventArgs e)
 {
  var tagged=FindTaggedElement(e.OriginalSource);if(tagged==null)return;string kind=tagged.Tag?.ToString()??"";
  string? id=kind switch{"WATERMARK" or "WATERMARK_RESIZE"=>"studio.cg.watermark","LOGO" or "LOGO_RESIZE"=>"studio.logo.primary","LOWER" or "LOWER_RESIZE"=>"studio.cg.lowerthird","SHAPE"=>"studio.cg.shape","PRIMARY"=>"studio.cg.primary","SECONDARY"=>"studio.cg.secondary",_=>null};
  if(id!=null&&_layers.FirstOrDefault(x=>x.Id==id)?.Locked==true){CgStatus.Text="LAYER LOCKED • UNLOCK TO TRANSFORM";return;}
  if(kind=="WATERMARK_RESIZE")
  {
   _dragElement=VisualTreeHelper.GetParent(tagged) as FrameworkElement;_dragKind="WATERMARK_RESIZE";_dragStart=e.GetPosition(CgCanvas);
   _watermarkResizeStartScale=D(WatermarkScale,100,20,400);_watermarkResizeStartSize=D(WatermarkSize,42,8,240);CgCanvas.CaptureMouse();e.Handled=true;return;
  }
  if(kind=="LOGO_RESIZE")
  {
   _dragElement=VisualTreeHelper.GetParent(tagged) as FrameworkElement;_dragKind="LOGO_RESIZE";_dragStart=e.GetPosition(CgCanvas);_logoResizeStartW=D(LogoW,.2,.01,1);_logoResizeStartH=D(LogoH,.15,.01,1);CgCanvas.CaptureMouse();e.Handled=true;return;
  }
  if(kind=="LOWER_RESIZE")
  {
   _dragElement=VisualTreeHelper.GetParent(tagged) as FrameworkElement;_dragKind="LOWER_RESIZE";_dragStart=e.GetPosition(CgCanvas);_lowerResizeStartW=D(LowerW,.6,.05,1);_lowerResizeStartH=D(LowerH,.12,.03,.5);CgCanvas.CaptureMouse();e.Handled=true;return;
  }
  if(kind is not ("PRIMARY" or "SECONDARY" or "LOWER" or "LOGO" or "SHAPE" or "WATERMARK"))return;
  _dragElement=tagged;_dragKind=kind;_dragStart=e.GetPosition(CgCanvas);_dragStartX=Canvas.GetLeft(tagged);_dragStartY=Canvas.GetTop(tagged);if(double.IsNaN(_dragStartX))_dragStartX=0;if(double.IsNaN(_dragStartY))_dragStartY=0;CgCanvas.CaptureMouse();e.Handled=true;
 }
 void CgCanvas_MouseMove(object s,MouseEventArgs e)
 {
  if(_dragElement==null||e.LeftButton!=MouseButtonState.Pressed)return;var p=e.GetPosition(CgCanvas);
  if(_dragKind=="WATERMARK_RESIZE")
  {
   double delta=(p.X-_dragStart.X+p.Y-_dragStart.Y)*.35;
   if(WatermarkLockAspect.IsChecked==true)WatermarkScale.Text=Math.Clamp(_watermarkResizeStartScale+delta,20,400).ToString("0",CultureInfo.InvariantCulture);
   else{WatermarkScale.Text=Math.Clamp(_watermarkResizeStartScale+(p.X-_dragStart.X)*.35,20,400).ToString("0",CultureInfo.InvariantCulture);WatermarkSize.Text=Math.Clamp(_watermarkResizeStartSize+(p.Y-_dragStart.Y)*.20,8,240).ToString("0",CultureInfo.InvariantCulture);}
   RenderCanvasPreview();return;
  }
  if(_dragKind=="LOGO_RESIZE")
  {
   double dx=(p.X-_dragStart.X)/960.0,dy=(p.Y-_dragStart.Y)/540.0;double w=Math.Clamp(_logoResizeStartW+dx,.01,1),h=Math.Clamp(_logoResizeStartH+dy,.01,1);
   if(LogoLockAspect.IsChecked==true){double ratio=_logoResizeStartH/Math.Max(.001,_logoResizeStartW);double dominant=Math.Abs(dx)>=Math.Abs(dy)?w:Math.Clamp(h/Math.Max(.001,ratio),.01,1);w=dominant;h=Math.Clamp(dominant*ratio,.01,1);}
   LogoW.Text=w.ToString("0.###",CultureInfo.InvariantCulture);LogoH.Text=h.ToString("0.###",CultureInfo.InvariantCulture);RenderCanvasPreview();return;
  }
  if(_dragKind=="LOWER_RESIZE")
  {
   LowerW.Text=Math.Clamp(_lowerResizeStartW+(p.X-_dragStart.X)/960.0,.05,1).ToString("0.###",CultureInfo.InvariantCulture);LowerH.Text=Math.Clamp(_lowerResizeStartH+(p.Y-_dragStart.Y)/540.0,.03,.5).ToString("0.###",CultureInfo.InvariantCulture);RenderCanvasPreview();return;
  }
  double maxX=Math.Max(0,960-_dragElement.ActualWidth),maxY=Math.Max(0,540-_dragElement.ActualHeight);double x=Math.Clamp(_dragStartX+(p.X-_dragStart.X),0,maxX),y=Math.Clamp(_dragStartY+(p.Y-_dragStart.Y),0,maxY);Canvas.SetLeft(_dragElement,x);Canvas.SetTop(_dragElement,y);SetDraggedPosition(_dragKind,x/960.0,y/540.0);
 }
 void CgCanvas_MouseLeftButtonUp(object s,MouseButtonEventArgs e){if(_dragElement==null)return;_dragElement=null;_dragKind="";CgCanvas.ReleaseMouseCapture();RenderCanvasPreview();e.Handled=true;}
 void SetDraggedPosition(string kind,double x,double y){string X(double v)=>v.ToString("0.###",CultureInfo.InvariantCulture);switch(kind){case "PRIMARY":CgX.Text=X(x);CgY.Text=X(y);break;case "SECONDARY":Cg2X.Text=X(x);Cg2Y.Text=X(y);break;case "LOWER":LowerX.Text=X(x);LowerY.Text=X(y);break;case "LOGO":LogoX.Text=X(x);LogoY.Text=X(y);break;case "SHAPE":ShapeX.Text=X(x);ShapeY.Text=X(y);break;case "WATERMARK":WatermarkX.Text=(x*100).ToString("0.#",CultureInfo.InvariantCulture);WatermarkY.Text=(y*100).ToString("0.#",CultureInfo.InvariantCulture);break;}}
 void Focus(FrameworkElement e)=>SelectCard(e);
 void SelectLayerById(string id){var layer=_layers.FirstOrDefault(x=>x.Id==id);if(layer!=null)LayerList.SelectedItem=layer;}
 void FocusWatermark_Click(object s,RoutedEventArgs e){Focus(WatermarkCard);SelectLayerById("studio.cg.watermark");}void FocusText_Click(object s,RoutedEventArgs e)=>Focus(TextCard);void FocusLower_Click(object s,RoutedEventArgs e){Focus(LowerCard);SelectLayerById("studio.cg.lowerthird");}void FocusLogo_Click(object s,RoutedEventArgs e){Focus(LogoCard);SelectLayerById("studio.logo.primary");}void FocusMove_Click(object s,RoutedEventArgs e){Focus(MoveCard);SelectLayerById("studio.cg.ticker");}void FocusClock_Click(object s,RoutedEventArgs e){Focus(ClockCard);SelectLayerById("studio.cg.clock");}void FocusNow_Click(object s,RoutedEventArgs e){Focus(NowCard);SelectLayerById("studio.cg.now");}void FocusShape_Click(object s,RoutedEventArgs e){Focus(ShapeCard);SelectLayerById("studio.cg.shape");}void FocusFull_Click(object s,RoutedEventArgs e){Focus(FullCard);SelectLayerById("studio.cg.fullscreen");}void FocusPip_Click(object s,RoutedEventArgs e){Focus(PipCard);SelectLayerById("studio.pip.video");}void FocusSequence_Click(object s,RoutedEventArgs e){Focus(SequenceCard);SelectLayerById("studio.image.sequence");}
 FrameworkElement CardFor(CgLayerItem layer)=>layer.Card switch{"Lower"=>LowerCard,"Logo"=>LogoCard,"Watermark"=>WatermarkCard,"Move"=>MoveCard,"Clock"=>ClockCard,"Now"=>NowCard,"Shape"=>ShapeCard,"Full"=>FullCard,"Pip"=>PipCard,"Sequence"=>SequenceCard,_=>TextCard};
 void LayerList_SelectionChanged(object s,SelectionChangedEventArgs e){if(LayerList.SelectedItem is CgLayerItem layer)SelectCard(CardFor(layer));}
 void LayerVisible_Click(object s,RoutedEventArgs e){if((s as FrameworkElement)?.DataContext is not CgLayerItem layer)return;_main.SetStudioLayerVisible(layer.Id,layer.Visible);CgStatus.Text=$"{layer.Name} • {(layer.Visible?"VISIBLE":"HIDDEN")}";}
 void LayerLock_Click(object s,RoutedEventArgs e){if((s as FrameworkElement)?.DataContext is not CgLayerItem layer)return;CgStatus.Text=$"{layer.Name} • {(layer.Locked?"LOCKED":"UNLOCKED")}";}
 void RefreshLayerOrder(){for(int i=0;i<_layers.Count;i++){_layers[i].Order=i+1;_main.SetStudioLayerZ(_layers[i].Id,70+i*10);}LayerList.Items.Refresh();}
 void SelectedLayerUp_Click(object s,RoutedEventArgs e){if(LayerList.SelectedItem is not CgLayerItem layer)return;int i=_layers.IndexOf(layer);if(i<=0)return;_layers.Move(i,i-1);RefreshLayerOrder();LayerList.SelectedItem=layer;}
 void SelectedLayerDown_Click(object s,RoutedEventArgs e){if(LayerList.SelectedItem is not CgLayerItem layer)return;int i=_layers.IndexOf(layer);if(i<0||i>=_layers.Count-1)return;_layers.Move(i,i+1);RefreshLayerOrder();LayerList.SelectedItem=layer;}
 void DuplicateLayer_Click(object s,RoutedEventArgs e){if(LayerList.SelectedItem is not CgLayerItem layer)return;string id=layer.Id+".copy."+(++_duplicateCounter);int index=_layers.IndexOf(layer)+1;var copy=new CgLayerItem{Order=index+1,Name=layer.Name+" Copy",Id=id,Card=layer.Card,Visible=layer.Visible,Locked=false,StartText=layer.StartText,DurationText=layer.DurationText,BarBrush=layer.BarBrush};if(!_main.DuplicateStudioLayer(layer.Id,id,70+index*10)){CgStatus.Text="TAKE ORIGINAL LAYER TO AIR BEFORE DUPLICATING";return;}_layers.Insert(index,copy);RefreshLayerOrder();LayerList.SelectedItem=copy;CgStatus.Text=$"{copy.Name} • DUPLICATED";}
 void DeleteLayer_Click(object s,RoutedEventArgs e){if(LayerList.SelectedItem is not CgLayerItem layer)return;_main.ClearStudioLayer(layer.Id);layer.Visible=false;LayerList.Items.Refresh();CgStatus.Text=$"{layer.Name} • DELETED FROM AIR";}
 void WatermarkPreviewChanged(object s,RoutedEventArgs e){if(IsLoaded)RenderCanvasPreview();}
 void LogoPreviewChanged(object s,RoutedEventArgs e){if(IsLoaded)RenderCanvasPreview();}
 void LowerPreviewChanged(object s,RoutedEventArgs e){if(IsLoaded)RenderCanvasPreview();}
 void ChooseWatermarkColor_Click(object s,RoutedEventArgs e)
 {
  var current=WatermarkParsedColor();using var dialog=new System.Windows.Forms.ColorDialog{FullOpen=true,AnyColor=true,Color=System.Drawing.Color.FromArgb(current.R,current.G,current.B)};
  if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;WatermarkColor.Text=$"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";RenderCanvasPreview();
 }
 void ApplyWatermarkToAir()
 {
  var color=WatermarkParsedColor();var flags=WatermarkStyleFlags();
  _main.ApplyStudioWatermark(WatermarkText.Text,Percent(WatermarkX,.82),Percent(WatermarkY,.08),D(WatermarkSize,42,8,240),ComboText(WatermarkFont,"Segoe UI Semibold"),flags.bold,flags.italic,
   color.R,color.G,color.B,D(WatermarkOpacity,45,0,100)/100.0,D(WatermarkScale,100,20,400)/100.0,D(WatermarkRotation,0,-360,360),D(WatermarkOutline,1,0,12),WatermarkShadow.IsChecked==true,I(WatermarkLayer,180,-500,500));
  _watermarkOnAir=true;CgStatus.Text="TEXT WATERMARK ON AIR";ModuleDiagnostics.WriteInfo("CG_WATERMARK","TAKE/UPDATE applied to Final Program compositor.");
 }
 void TakeWatermark_Click(object s,RoutedEventArgs e){ApplyWatermarkToAir();SaveProfileInternal(false);}
 void HideWatermark_Click(object s,RoutedEventArgs e){_main.ClearStudioWatermark();_watermarkOnAir=false;CgStatus.Text="TEXT WATERMARK HIDDEN • SETTINGS RETAINED";}
 void ClearWatermark_Click(object s,RoutedEventArgs e){_main.ClearStudioWatermark();_watermarkOnAir=false;WatermarkAlwaysOn.IsChecked=false;WatermarkText.Text="";SaveProfileInternal(false);CgStatus.Text="TEXT WATERMARK CLEARED • RESTART RESTORE DISABLED";RenderCanvasPreview();}
 void PreviewWatermark_Click(object s,RoutedEventArgs e){RefreshCanvas();CgStatus.Text="TEXT WATERMARK PREVIEW REFRESHED • NOT TAKEN TO AIR";}
 void ResetWatermark_Click(object s,RoutedEventArgs e)
 {
  WatermarkText.Text="SR MUSIC";WatermarkFont.SelectedIndex=0;WatermarkStyle.SelectedIndex=1;WatermarkColor.Text="#FFFFFF";WatermarkOpacity.Text="45";WatermarkSize.Text="42";WatermarkX.Text="82";WatermarkY.Text="8";WatermarkScale.Text="100";WatermarkRotation.Text="0";WatermarkOutline.Text="1";WatermarkShadow.IsChecked=true;WatermarkLockAspect.IsChecked=true;WatermarkSafeArea.IsChecked=true;WatermarkLayer.Text="180";RenderCanvasPreview();CgStatus.Text="WATERMARK DEFAULTS RESTORED";
 }
 void WatermarkLayerUp_Click(object s,RoutedEventArgs e){WatermarkLayer.Text=Math.Clamp(I(WatermarkLayer,180,-500,500)+10,-500,500).ToString(CultureInfo.InvariantCulture);if(_watermarkOnAir)ApplyWatermarkToAir();}
 void WatermarkLayerDown_Click(object s,RoutedEventArgs e){WatermarkLayer.Text=Math.Clamp(I(WatermarkLayer,180,-500,500)-10,-500,500).ToString(CultureInfo.InvariantCulture);if(_watermarkOnAir)ApplyWatermarkToAir();}
 void SaveWatermark_Click(object s,RoutedEventArgs e)=>SaveProfileInternal(true);
 void LoadWatermark_Click(object s,RoutedEventArgs e){LoadProfile(true);SelectCard(WatermarkCard);}
 void TakePrimary_Click(object s,RoutedEventArgs e){_main.ApplyStudioCgText(CgText.Text,D(CgX,.05,0,1),D(CgY,.82,0,1),D(CgSize,36,8,180));CgStatus.Text="PRIMARY TITLE ON AIR";RenderCanvasPreview();}
 void ClearPrimary_Click(object s,RoutedEventArgs e){_main.ClearStudioCg();CgStatus.Text="PRIMARY CLEARED";}
 void TakeSecondary_Click(object s,RoutedEventArgs e){_main.ApplyStudioSecondaryText(Cg2Text.Text,D(Cg2X,.05,0,1),D(Cg2Y,.9,0,1),D(Cg2Size,24,8,180));CgStatus.Text="SECONDARY ON AIR";}
 void ClearSecondary_Click(object s,RoutedEventArgs e)=>_main.ClearStudioSecondaryText();
 void TakeLower_Click(object s,RoutedEventArgs e)
 {
  var tc=Hex(LowerTextColor.Text);var bg=Hex(LowerBackgroundColor.Text);int z=70+Math.Max(0,_layers.IndexOf(_layers.First(x=>x.Id=="studio.cg.lowerthird")))*10;
  _main.ApplyStudioLowerThirdAdvanced(LowerText.Text,LowerSecondaryText.Text,D(LowerX,.05,0,1),D(LowerY,.75,0,1),D(LowerW,.60,.05,1),D(LowerH,.12,.03,.5),D(LowerSize,48,8,160),ComboText(LowerFont,"Segoe UI"),tc.r,tc.g,tc.b,bg.r,bg.g,bg.b,D(LowerBackgroundOpacity,90,0,100)/100.0,ComboText(LowerAnimationIn,"Slide In Left"),ComboText(LowerAnimationOut,"Slide Out Left"),D(LowerAnimationDuration,.6,.05,10),z);CgStatus.Text="LOWER THIRD ON AIR • ANIMATION IN ACTIVE";
 }
 void RemoveLowerThird_Click(object s,RoutedEventArgs e){_main.RemoveStudioLowerThirdAnimated();CgStatus.Text="LOWER THIRD • ANIMATION OUT ACTIVE";}
 void BrowseLogo_Click(object s,RoutedEventArgs e){var d=new Microsoft.Win32.OpenFileDialog{Filter="Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"};if(d.ShowDialog()==true){LogoPath.Text=d.FileName;RenderCanvasPreview();}}
 void TakeLogo_Click(object s,RoutedEventArgs e){try{_main.ApplyStudioLogo(LogoPath.Text,D(LogoX,.75,0,1),D(LogoY,.05,0,1),D(LogoW,.2,.01,1),D(LogoH,.15,.01,1),D(LogoOpacity,1,0,1),LogoStretchMode());CgStatus.Text="LOGO ON AIR • TRANSFORM APPLIED";}catch(Exception ex){CgStatus.Text="LOGO ERROR • "+ex.Message;}}
 void ClearLogo_Click(object s,RoutedEventArgs e)=>_main.ClearStudioLogo();
 void TakeTicker_Click(object s,RoutedEventArgs e){_main.ApplyStudioTicker(MoveText.Text,D(MoveAxis,.88,0,1),D(MoveSize,28,8,100),D(MoveSpeed,.12,.01,1));CgStatus.Text="TICKER ON AIR";}
 void TakeRoll_Click(object s,RoutedEventArgs e){_main.ApplyStudioRoll(MoveText.Text,D(MoveAxis,.10,0,1),D(MoveSize,28,8,100),D(MoveSpeed,.12,.01,1));CgStatus.Text="ROLL ON AIR";}
 void TakeClock_Click(object s,RoutedEventArgs e){_main.ApplyStudioClock(D(ClockX,.82,0,1),D(ClockY,.04,0,1),D(ClockSize,30,8,120));CgStatus.Text="CLOCK ON AIR";}
 void TakeTimer_Click(object s,RoutedEventArgs e){_main.ApplyStudioTimer(D(ClockX,.82,0,1),D(ClockY,.04,0,1),D(ClockSize,30,8,120),false);CgStatus.Text="TIMER ON AIR";}
 void TakeStopwatch_Click(object s,RoutedEventArgs e){_main.ApplyStudioTimer(D(ClockX,.82,0,1),D(ClockY,.04,0,1),D(ClockSize,30,8,120),true);CgStatus.Text="STOPWATCH ON AIR";}
 void TakeNowNext_Click(object s,RoutedEventArgs e){_main.ApplyStudioNowNext(NowText.Text,NextText.Text,.04,.66,28);CgStatus.Text="NOW/NEXT ON AIR";}
 static (byte r,byte g,byte b) Hex(string v){try{var c=(Color)ColorConverter.ConvertFromString(v);return(c.R,c.G,c.B);}catch{return(23,54,77);}}
 void TakeShape_Click(object s,RoutedEventArgs e){var c=Hex(ShapeHex.Text);_main.ApplyStudioShape(D(ShapeX,.02,0,1),D(ShapeY,.72,0,1),D(ShapeW,.7,.01,1),D(ShapeH,.16,.01,1),c.r,c.g,c.b,.85);CgStatus.Text="SHAPE ON AIR";}
 void LShapeOn_Click(object s,RoutedEventArgs e){_main.ApplyStudioLShape(true,.18,.08,.76,.84);CgStatus.Text="L-SHAPE ON AIR";}void LShapeOff_Click(object s,RoutedEventArgs e){_main.ApplyStudioLShape(false,0,0,1,1);CgStatus.Text="L-SHAPE OFF";}
 void BrowseFull_Click(object s,RoutedEventArgs e){var d=new Microsoft.Win32.OpenFileDialog{Filter="Image files|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"};if(d.ShowDialog()==true)FullPath.Text=d.FileName;}
 void TakeFull_Click(object s,RoutedEventArgs e){try{_main.ApplyStudioFullScreenGraphic(FullPath.Text,D(FullOpacity,1,0,1));CgStatus.Text="FULL SCREEN ON AIR";}catch(Exception ex){CgStatus.Text="FULL ERROR • "+ex.Message;}}void ClearFull_Click(object s,RoutedEventArgs e)=>_main.ClearStudioFullScreen();
 void BrowsePip_Click(object s,RoutedEventArgs e){var d=new Microsoft.Win32.OpenFileDialog{Filter="Video files|*.mp4;*.mkv;*.mov;*.avi;*.mxf;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.vob;*.dat;*.webm;*.wmv;*.flv;*.m4v|All files|*.*"};if(d.ShowDialog()==true)PipPath.Text=d.FileName;}
 async void TakePip_Click(object s,RoutedEventArgs e){try{await _main.StartStudioPipAsync(PipPath.Text,D(PipX,.7,0,1),D(PipY,.08,0,1),D(PipW,.26,.05,1),D(PipH,.26,.05,1),D(PipOpacity,1,0,1),PipLoop.IsChecked==true);CgStatus.Text="PIP ON AIR";}catch(Exception ex){CgStatus.Text="PIP ERROR • "+ex.Message;}}void ClearPip_Click(object s,RoutedEventArgs e)=>_main.StopStudioPip();
 void BrowseSequence_Click(object s,RoutedEventArgs e){var d=new Microsoft.Win32.OpenFileDialog{Multiselect=true,Filter="Image frames|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*"};if(d.ShowDialog()!=true)return;_sequenceFiles.Clear();_sequenceFiles.AddRange(d.FileNames.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));SequenceInfo.Text=$"{_sequenceFiles.Count} frame(s)";}
 void TakeSequence_Click(object s,RoutedEventArgs e){try{_main.ApplyStudioImageSequence(_sequenceFiles,D(SeqX,.05,0,1),D(SeqY,.05,0,1),D(SeqW,.3,.05,1),D(SeqH,.3,.05,1),D(SeqOpacity,1,0,1),D(SeqFps,12,.1,60),SeqLoop.IsChecked==true);CgStatus.Text="IMAGE SEQUENCE ON AIR";}catch(Exception ex){CgStatus.Text="SEQUENCE ERROR • "+ex.Message;}}void ClearSequence_Click(object s,RoutedEventArgs e)=>_main.ClearStudioImageSequence();
 void ClearAdvanced_Click(object s,RoutedEventArgs e){_main.ClearStudioAdvancedCg();CgStatus.Text="ADVANCED CG CLEARED";}
 void PreviewAll_Click(object s,RoutedEventArgs e){_previewRunning=true;_previewStarted=DateTime.UtcNow;CgCanvas.Visibility=Visibility.Visible;RefreshCanvas();CgStatus.Text="TEMPLATE PREVIEW RUNNING • NOT ON AIR";}
 void StopPreview_Click(object s,RoutedEventArgs e){_previewRunning=false;CgCanvas.Visibility=Visibility.Collapsed;CgStatus.Text="TEMPLATE PREVIEW STOPPED";}
 async void TakeSelectedToAir_Click(object s,RoutedEventArgs e)
 {
  if(LayerList.SelectedItem is not CgLayerItem layer)return;
  if(_layerSchedules.Remove(layer.Id,out var prior)){prior.Cancel();prior.Dispose();}var cts=new CancellationTokenSource();_layerSchedules[layer.Id]=cts;var start=ParseTimeline(layer.StartText,TimeSpan.Zero);var duration=ParseTimeline(layer.DurationText,TimeSpan.FromSeconds(30));
  try{if(start>TimeSpan.Zero){CgStatus.Text=$"{layer.Name} • QUEUED {start}";await Task.Delay(start,cts.Token);}await TakeLayerNowAsync(layer,s,e);layer.Visible=true;_main.SetStudioLayerVisible(layer.Id,true);LayerList.Items.Refresh();if(duration>TimeSpan.Zero){await Task.Delay(duration,cts.Token);RemoveLayerNow(layer);}}catch(OperationCanceledException){}finally{if(_layerSchedules.TryGetValue(layer.Id,out var active)&&ReferenceEquals(active,cts))_layerSchedules.Remove(layer.Id);cts.Dispose();}
 }
 static TimeSpan ParseTimeline(string value,TimeSpan fallback)=>TimeSpan.TryParse(value,CultureInfo.InvariantCulture,out var parsed)?parsed:fallback;
 async Task TakeLayerNowAsync(CgLayerItem layer,object s,RoutedEventArgs e){string baseId=layer.Id.Split(new[]{".copy."},StringSplitOptions.None)[0];switch(baseId){case "studio.cg.lowerthird":TakeLower_Click(s,e);break;case "studio.logo.primary":TakeLogo_Click(s,e);break;case "studio.cg.watermark":TakeWatermark_Click(s,e);break;case "studio.cg.clock":TakeClock_Click(s,e);break;case "studio.cg.ticker":TakeTicker_Click(s,e);break;case "studio.cg.roll":TakeRoll_Click(s,e);break;case "studio.cg.now":TakeNowNext_Click(s,e);break;case "studio.cg.shape":TakeShape_Click(s,e);break;case "studio.cg.fullscreen":TakeFull_Click(s,e);break;case "studio.image.sequence":TakeSequence_Click(s,e);break;case "studio.cg.lshape":LShapeOn_Click(s,e);break;case "studio.pip.video":await TakePipToAirAsync();break;}if(baseId!=layer.Id)_main.DuplicateStudioLayer(baseId,layer.Id,70+layer.Order*10);_main.SetStudioLayerZ(layer.Id,70+layer.Order*10);}
 void RemoveLayerNow(CgLayerItem layer){string baseId=layer.Id.Split(new[]{".copy."},StringSplitOptions.None)[0];if(baseId=="studio.cg.lowerthird")_main.RemoveStudioLowerThirdAnimated();else if(baseId=="studio.cg.lshape")_main.ApplyStudioLShape(false,0,0,1,1);else if(baseId=="studio.pip.video")_main.StopStudioPip();else _main.ClearStudioLayer(layer.Id);layer.Visible=false;LayerList.Items.Refresh();}
 async System.Threading.Tasks.Task TakePipToAirAsync(){try{await _main.StartStudioPipAsync(PipPath.Text,D(PipX,.7,0,1),D(PipY,.08,0,1),D(PipW,.26,.05,1),D(PipH,.26,.05,1),D(PipOpacity,1,0,1),PipLoop.IsChecked==true);CgStatus.Text="PIP ON AIR";}catch(Exception ex){CgStatus.Text="PIP ERROR • "+ex.Message;}}
 void RemoveSelectedFromAir_Click(object s,RoutedEventArgs e){if(LayerList.SelectedItem is not CgLayerItem layer)return;if(_layerSchedules.Remove(layer.Id,out var c)){c.Cancel();c.Dispose();}RemoveLayerNow(layer);CgStatus.Text=$"{layer.Name} • REMOVED FROM AIR";}
 void ClearSelected_Click(object s,RoutedEventArgs e){if(LayerList.SelectedItem is not CgLayerItem layer)return;RemoveLayerNow(layer);CgStatus.Text=$"{layer.Name} • CLEARED";}
 void NewTemplate_Click(object s,RoutedEventArgs e){_main.ClearAllStudioCg();foreach(var c in _layerSchedules.Values){c.Cancel();c.Dispose();}_layerSchedules.Clear();_templatePath=null;_layers.Clear();InitializeLayers();ApplyProfileToUi(new CgProfile());WatermarkAlwaysOn.IsChecked=false;RefreshCanvas();CgStatus.Text="NEW COMPLETE TEMPLATE READY";}
 void OpenTemplate_Click(object s,RoutedEventArgs e){DataStorage.EnsureCreated();var d=new Microsoft.Win32.OpenFileDialog{Filter="SMART CG template|*.json|All files|*.*",InitialDirectory=DataStorage.Settings};if(d.ShowDialog()!=true)return;try{var p=JsonSerializer.Deserialize<CgProfile>(File.ReadAllText(d.FileName));if(p==null)throw new InvalidDataException("Template data is empty.");ApplyProfileToUi(p);_templatePath=d.FileName;CgStatus.Text="TEMPLATE OPENED • "+Path.GetFileName(d.FileName);}catch(Exception ex){CgStatus.Text="OPEN ERROR • "+ex.Message;ModuleDiagnostics.Write("CG_TEMPLATE_OPEN",ex);}}
 void SaveAsTemplate_Click(object s,RoutedEventArgs e){var d=new Microsoft.Win32.SaveFileDialog{Filter="SMART CG template|*.json",DefaultExt=".json",AddExtension=true,InitialDirectory=DataStorage.Settings,FileName=string.IsNullOrWhiteSpace(LowerTemplateName.Text)?"CG_Template":LowerTemplateName.Text};if(d.ShowDialog()!=true)return;try{DataStorage.EnsureCreated();File.WriteAllText(d.FileName,JsonSerializer.Serialize(ReadProfile(),new JsonSerializerOptions{WriteIndented=true}));_templatePath=d.FileName;CgStatus.Text="TEMPLATE SAVED AS • "+Path.GetFileName(d.FileName);}catch(Exception ex){CgStatus.Text="SAVE AS ERROR • "+ex.Message;ModuleDiagnostics.Write("CG_TEMPLATE_SAVE_AS",ex);}}
 CgProfile ReadProfile()
 {
  var flags=WatermarkStyleFlags();return new(){Cg1=CgText.Text,X=D(CgX,.05),Y=D(CgY,.82),Size=D(CgSize,36),Logo=LogoPath.Text,LogoX=D(LogoX,.75),LogoY=D(LogoY,.05),LogoW=D(LogoW,.2),LogoH=D(LogoH,.15),LogoOpacity=D(LogoOpacity,1),LogoStretch=LogoStretch.SelectedIndex,LogoLockAspect=LogoLockAspect.IsChecked==true,
   WatermarkText=WatermarkText.Text,WatermarkFont=ComboText(WatermarkFont,"Segoe UI Semibold"),WatermarkBold=flags.bold,WatermarkItalic=flags.italic,WatermarkColor=WatermarkColor.Text,WatermarkOpacity=D(WatermarkOpacity,45,0,100)/100.0,WatermarkSize=D(WatermarkSize,42,8,240),WatermarkX=Percent(WatermarkX,.82),WatermarkY=Percent(WatermarkY,.08),WatermarkScale=D(WatermarkScale,100,20,400)/100.0,WatermarkRotation=D(WatermarkRotation,0,-360,360),WatermarkOutline=D(WatermarkOutline,1,0,12),WatermarkShadow=WatermarkShadow.IsChecked==true,WatermarkLockAspect=WatermarkLockAspect.IsChecked==true,WatermarkSafeArea=WatermarkSafeArea.IsChecked==true,WatermarkZ=I(WatermarkLayer,180,-500,500),WatermarkAlwaysOn=WatermarkAlwaysOn.IsChecked==true,
   LowerTemplateName=LowerTemplateName.Text,LowerText=LowerText.Text,LowerSecondary=LowerSecondaryText.Text,LowerFont=ComboText(LowerFont,"Segoe UI"),LowerFontSize=D(LowerSize,48,8,160),LowerTextColor=LowerTextColor.Text,LowerBackgroundColor=LowerBackgroundColor.Text,LowerBackgroundOpacity=D(LowerBackgroundOpacity,90,0,100)/100.0,LowerX=D(LowerX,.05,0,1),LowerY=D(LowerY,.75,0,1),LowerW=D(LowerW,.60,.05,1),LowerH=D(LowerH,.12,.03,.5),LowerAnimationIn=ComboText(LowerAnimationIn,"Slide In Left"),LowerAnimationOut=ComboText(LowerAnimationOut,"Slide Out Left"),LowerAnimationDuration=D(LowerAnimationDuration,.6,.05,10),
   Cg2=Cg2Text.Text,Cg2X=D(Cg2X,.05),Cg2Y=D(Cg2Y,.9),Cg2Size=D(Cg2Size,24),MoveText=MoveText.Text,MoveAxis=D(MoveAxis,.88),MoveSize=D(MoveSize,28),MoveSpeed=D(MoveSpeed,.12),ClockX=D(ClockX,.82),ClockY=D(ClockY,.04),ClockSize=D(ClockSize,30),NowText=NowText.Text,NextText=NextText.Text,ShapeX=D(ShapeX,.02),ShapeY=D(ShapeY,.72),ShapeW=D(ShapeW,.7),ShapeH=D(ShapeH,.16),ShapeHex=ShapeHex.Text,FullPath=FullPath.Text,FullOpacity=D(FullOpacity,1),PipPath=PipPath.Text,PipX=D(PipX,.7),PipY=D(PipY,.08),PipW=D(PipW,.26),PipH=D(PipH,.26),PipOpacity=D(PipOpacity,1),PipLoop=PipLoop.IsChecked==true,SequenceFiles=_sequenceFiles.ToList(),SeqX=D(SeqX,.05),SeqY=D(SeqY,.05),SeqW=D(SeqW,.3),SeqH=D(SeqH,.3),SeqOpacity=D(SeqOpacity,1),SeqFps=D(SeqFps,12),SeqLoop=SeqLoop.IsChecked==true,Layers=_layers.Select(x=>new CgLayerState{Order=x.Order,Name=x.Name,Id=x.Id,Card=x.Card,Visible=x.Visible,Locked=x.Locked,StartText=x.StartText,DurationText=x.DurationText}).ToList()};
 }
 void SaveProfileInternal(bool showStatus)
 {
  try
  {
   DataStorage.EnsureCreated();var p=ReadProfile();if(!string.IsNullOrWhiteSpace(p.Logo)&&File.Exists(p.Logo)){string dir=Path.Combine(DataStorage.DataRoot,"CG","Assets");Directory.CreateDirectory(dir);string target=Path.Combine(dir,"PrimaryLogo"+Path.GetExtension(p.Logo));if(!Path.GetFullPath(p.Logo).Equals(Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase))File.Copy(p.Logo,target,true);p.Logo=target;LogoPath.Text=target;}
   string json=JsonSerializer.Serialize(p,new JsonSerializerOptions{WriteIndented=true});DataStorage.WritePersistentText(ProfilePath,json,"Settings");if(!string.IsNullOrWhiteSpace(_templatePath))File.WriteAllText(_templatePath,json);if(showStatus)CgStatus.Text="CG PROFILE + COMPLETE TEMPLATE SAVED";ModuleDiagnostics.WriteInfo("CG_WATERMARK","Complete CG profile/template saved with restart policy.");
  }
  catch(Exception ex){CgStatus.Text="SAVE ERROR • "+ex.Message;ModuleDiagnostics.Write("CG_SAVE",ex);}
 }
 void SaveProfile_Click(object s,RoutedEventArgs e)=>SaveProfileInternal(true);
 void LoadProfile_Click(object s,RoutedEventArgs e)=>LoadProfile(true);
 static void SelectComboText(ComboBox combo,string value)
 {
  for(int i=0;i<combo.Items.Count;i++)if(combo.Items[i] is ComboBoxItem item&&string.Equals(item.Content?.ToString(),value,StringComparison.OrdinalIgnoreCase)){combo.SelectedIndex=i;return;}
 }
 void ApplyProfileToUi(CgProfile p)
 {
  CgText.Text=p.Cg1;CgX.Text=p.X.ToString("0.###",CultureInfo.InvariantCulture);CgY.Text=p.Y.ToString("0.###",CultureInfo.InvariantCulture);CgSize.Text=p.Size.ToString("0.###",CultureInfo.InvariantCulture);LogoPath.Text=File.Exists(p.Logo)?p.Logo:"";LogoX.Text=p.LogoX.ToString("0.###",CultureInfo.InvariantCulture);LogoY.Text=p.LogoY.ToString("0.###",CultureInfo.InvariantCulture);LogoW.Text=p.LogoW.ToString("0.###",CultureInfo.InvariantCulture);LogoH.Text=p.LogoH.ToString("0.###",CultureInfo.InvariantCulture);LogoOpacity.Text=p.LogoOpacity.ToString("0.###",CultureInfo.InvariantCulture);LogoStretch.SelectedIndex=Math.Clamp(p.LogoStretch,0,2);LogoLockAspect.IsChecked=p.LogoLockAspect;
  Cg2Text.Text=p.Cg2;Cg2X.Text=p.Cg2X.ToString("0.###",CultureInfo.InvariantCulture);Cg2Y.Text=p.Cg2Y.ToString("0.###",CultureInfo.InvariantCulture);Cg2Size.Text=p.Cg2Size.ToString("0.###",CultureInfo.InvariantCulture);MoveText.Text=p.MoveText;MoveAxis.Text=p.MoveAxis.ToString("0.###",CultureInfo.InvariantCulture);MoveSize.Text=p.MoveSize.ToString("0.###",CultureInfo.InvariantCulture);MoveSpeed.Text=p.MoveSpeed.ToString("0.###",CultureInfo.InvariantCulture);ClockX.Text=p.ClockX.ToString("0.###",CultureInfo.InvariantCulture);ClockY.Text=p.ClockY.ToString("0.###",CultureInfo.InvariantCulture);ClockSize.Text=p.ClockSize.ToString("0.###",CultureInfo.InvariantCulture);NowText.Text=p.NowText;NextText.Text=p.NextText;ShapeX.Text=p.ShapeX.ToString("0.###",CultureInfo.InvariantCulture);ShapeY.Text=p.ShapeY.ToString("0.###",CultureInfo.InvariantCulture);ShapeW.Text=p.ShapeW.ToString("0.###",CultureInfo.InvariantCulture);ShapeH.Text=p.ShapeH.ToString("0.###",CultureInfo.InvariantCulture);ShapeHex.Text=p.ShapeHex;
  FullPath.Text=File.Exists(p.FullPath)?p.FullPath:"";FullOpacity.Text=p.FullOpacity.ToString("0.###",CultureInfo.InvariantCulture);PipPath.Text=File.Exists(p.PipPath)?p.PipPath:"";PipX.Text=p.PipX.ToString("0.###",CultureInfo.InvariantCulture);PipY.Text=p.PipY.ToString("0.###",CultureInfo.InvariantCulture);PipW.Text=p.PipW.ToString("0.###",CultureInfo.InvariantCulture);PipH.Text=p.PipH.ToString("0.###",CultureInfo.InvariantCulture);PipOpacity.Text=p.PipOpacity.ToString("0.###",CultureInfo.InvariantCulture);PipLoop.IsChecked=p.PipLoop;_sequenceFiles.Clear();if(p.SequenceFiles!=null)_sequenceFiles.AddRange(p.SequenceFiles.Where(File.Exists));SequenceInfo.Text=$"{_sequenceFiles.Count} frame(s)";SeqX.Text=p.SeqX.ToString("0.###",CultureInfo.InvariantCulture);SeqY.Text=p.SeqY.ToString("0.###",CultureInfo.InvariantCulture);SeqW.Text=p.SeqW.ToString("0.###",CultureInfo.InvariantCulture);SeqH.Text=p.SeqH.ToString("0.###",CultureInfo.InvariantCulture);SeqOpacity.Text=p.SeqOpacity.ToString("0.###",CultureInfo.InvariantCulture);SeqFps.Text=p.SeqFps.ToString("0.###",CultureInfo.InvariantCulture);SeqLoop.IsChecked=p.SeqLoop;
  WatermarkText.Text=p.WatermarkText;SelectComboText(WatermarkFont,p.WatermarkFont);WatermarkStyle.SelectedIndex=p.WatermarkBold?(p.WatermarkItalic?3:1):(p.WatermarkItalic?2:0);WatermarkColor.Text=p.WatermarkColor;WatermarkOpacity.Text=(p.WatermarkOpacity*100).ToString("0.#",CultureInfo.InvariantCulture);WatermarkSize.Text=p.WatermarkSize.ToString("0.#",CultureInfo.InvariantCulture);WatermarkX.Text=(p.WatermarkX*100).ToString("0.#",CultureInfo.InvariantCulture);WatermarkY.Text=(p.WatermarkY*100).ToString("0.#",CultureInfo.InvariantCulture);WatermarkScale.Text=(p.WatermarkScale*100).ToString("0.#",CultureInfo.InvariantCulture);WatermarkRotation.Text=p.WatermarkRotation.ToString("0.#",CultureInfo.InvariantCulture);WatermarkOutline.Text=p.WatermarkOutline.ToString("0.#",CultureInfo.InvariantCulture);WatermarkShadow.IsChecked=p.WatermarkShadow;WatermarkLockAspect.IsChecked=p.WatermarkLockAspect;WatermarkSafeArea.IsChecked=p.WatermarkSafeArea;WatermarkLayer.Text=p.WatermarkZ.ToString(CultureInfo.InvariantCulture);WatermarkAlwaysOn.IsChecked=p.WatermarkAlwaysOn;
  LowerTemplateName.Text=p.LowerTemplateName;LowerText.Text=p.LowerText;LowerSecondaryText.Text=p.LowerSecondary;SelectComboText(LowerFont,p.LowerFont);LowerSize.Text=p.LowerFontSize.ToString("0.#",CultureInfo.InvariantCulture);LowerTextColor.Text=p.LowerTextColor;LowerBackgroundColor.Text=p.LowerBackgroundColor;LowerBackgroundOpacity.Text=(p.LowerBackgroundOpacity*100).ToString("0.#",CultureInfo.InvariantCulture);LowerX.Text=p.LowerX.ToString("0.###",CultureInfo.InvariantCulture);LowerY.Text=p.LowerY.ToString("0.###",CultureInfo.InvariantCulture);LowerW.Text=p.LowerW.ToString("0.###",CultureInfo.InvariantCulture);LowerH.Text=p.LowerH.ToString("0.###",CultureInfo.InvariantCulture);SelectComboText(LowerAnimationIn,p.LowerAnimationIn);SelectComboText(LowerAnimationOut,p.LowerAnimationOut);LowerAnimationDuration.Text=p.LowerAnimationDuration.ToString("0.##",CultureInfo.InvariantCulture);
  if(p.Layers?.Count>0){_layers.Clear();foreach(var state in p.Layers.OrderBy(x=>x.Order)){var color=Color.FromRgb((byte)(45+(state.Order*31)%180),(byte)(90+(state.Order*47)%145),(byte)(120+(state.Order*53)%125));_layers.Add(new CgLayerItem{Order=_layers.Count+1,Name=state.Name,Id=state.Id,Card=state.Card,Visible=state.Visible,Locked=state.Locked,StartText=state.StartText,DurationText=state.DurationText,BarBrush=new SolidColorBrush(color)});_main.SetStudioLayerVisible(state.Id,state.Visible);}}LayerList.Items.Refresh();RefreshLayerOrder();RefreshCanvas();
 }
 void LoadProfile(bool showStatus)
 {
  try
  {
   string path=File.Exists(ProfilePath)?ProfilePath:LegacyProfilePath;if(!File.Exists(path)){if(showStatus)CgStatus.Text="DEFAULT CG TEMPLATE READY";return;}var p=JsonSerializer.Deserialize<CgProfile>(File.ReadAllText(path));if(p==null)return;
   ApplyProfileToUi(p);if(p.WatermarkAlwaysOn&&!string.IsNullOrWhiteSpace(p.WatermarkText)){ApplyWatermarkToAir();_watermarkOnAir=true;}if(showStatus)CgStatus.Text=string.IsNullOrWhiteSpace(p.Logo)||File.Exists(p.Logo)?"CG PROFILE + TEMPLATE LOADED":"CG PROFILE LOADED • LOGO ASSET MISSING";
  }
  catch(Exception ex){CgStatus.Text="LOAD ERROR • "+ex.Message;ModuleDiagnostics.Write("CG_LOAD",ex);}
 }
 void Close_Click(object s,RoutedEventArgs e)=>Close();
}

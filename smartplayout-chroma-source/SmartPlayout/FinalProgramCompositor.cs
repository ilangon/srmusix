using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace FFmpegNativePlayer;

public sealed class FinalProgramCompositor
{
    sealed record ImageLayer(BitmapSource Image, Rect Rect, double Opacity, int Z, Stretch Stretch);
    sealed record SequenceLayer(BitmapSource[] Frames, Rect Rect, double Opacity, double Fps, bool Loop, int Z, long Started);
    sealed record TextLayer(string Text, double X, double Y, double Size, Brush Brush, int Z);
    sealed record WatermarkLayer(string Text, double X, double Y, double Size, string FontFamily, bool Bold, bool Italic,
        Color Fill, double Opacity, double Scale, double Rotation, double Outline, bool Shadow, int Z);
    sealed record LowerThirdLayer(string Text, string Secondary, Rect Rect, double Size, string FontFamily,
        Color TextColor, Color BackgroundColor, double BackgroundOpacity, string AnimationIn, string AnimationOut,
        double AnimationDuration, int Z, long Started, long Removing);
    sealed record MovingTextLayer(string Text, double Axis, double Size, double Speed, bool Vertical, int Z, long Started);
    sealed record ClockLayer(double X, double Y, double Size, int Z);
    sealed record TimerLayer(double X, double Y, double Size, bool Stopwatch, int Z, long Started);
    sealed record ShapeLayer(Rect Rect, Color Fill, double Opacity, int Z);
    readonly object _gate = new();
    readonly Dictionary<string, ImageLayer> _images = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SequenceLayer> _sequences = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TextLayer> _texts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, WatermarkLayer> _watermarks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, LowerThirdLayer> _lowerThirds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MovingTextLayer> _movingTexts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ClockLayer> _clocks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TimerLayer> _timers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ShapeLayer> _shapes = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    bool _lShapeEnabled;
    Rect _lShapeProgramRect = new(0,0,1,1);
    bool _keyEnabled;
    Color _keyColor = Colors.Lime;
    double _keyTolerance = .18;
    double _keySoftness = .08;
    double _keySpill = .65;
    double _keyDullness;
    double _keySpectralBalance = .5;
    double _keyEdgeSoftness = .18;
    double _keyErode;
    double _keyShadowRecovery = .20;
    Rect _keyTransform = new(0, 0, 1, 1);
    Thickness _keyCrop = new(0);
    double _keyRotation;
    Stretch _keyStretch = Stretch.Uniform;
    public bool HasLayers { get { lock (_gate) return _keyEnabled || _lShapeEnabled || _images.Count > 0 || _sequences.Count > 0 || _texts.Count > 0 || _watermarks.Count > 0 || _lowerThirds.Count > 0 || _movingTexts.Count > 0 || _clocks.Count > 0 || _timers.Count > 0 || _shapes.Count > 0; } }

    public void SetChromaKey(bool enabled, Color key, double tolerance, double softness)
    {
        lock (_gate) { _keyEnabled = enabled; _keyColor = key; _keyTolerance = Math.Clamp(tolerance, .01, 1); _keySoftness = Math.Clamp(softness, 0, .5); }
    }
    public void SetChromaKeyAdvanced(bool enabled, Color key, double tolerance, double softness,
        double spill, double dullness, double spectralBalance, double edgeSoftness, double erodeDilate, double shadowRecovery,
        Rect transform, Thickness crop, double rotation, Stretch stretch)
    {
        lock (_gate)
        {
            _keyEnabled = enabled; _keyColor = key;
            _keyTolerance = Math.Clamp(tolerance, .01, 1); _keySoftness = Math.Clamp(softness, 0, .5);
            _keySpill = Math.Clamp(spill, 0, 1); _keyDullness = Math.Clamp(dullness, 0, 1);
            _keySpectralBalance = Math.Clamp(spectralBalance, 0, 1); _keyEdgeSoftness = Math.Clamp(edgeSoftness, 0, 1);
            _keyErode = Math.Clamp(erodeDilate, -1, 1); _keyShadowRecovery = Math.Clamp(shadowRecovery, 0, 1);
            // Chroma transform deliberately supports moving partly off-screen and
            // enlarging up to 200%. Image-layer normalization is intentionally
            // stricter, so do not use Normalize() for the keyed foreground.
            _keyTransform = NormalizeKeyTransform(transform);
            _keyCrop = new Thickness(Math.Clamp(crop.Left, 0, .49), Math.Clamp(crop.Top, 0, .49),
                Math.Clamp(crop.Right, 0, .49), Math.Clamp(crop.Bottom, 0, .49));
            _keyRotation = Math.Clamp(rotation, -360, 360); _keyStretch = stretch;
        }
    }
    public void SetImage(string id, BitmapSource image, Rect rect, double opacity = 1, int z = 0, Stretch stretch = Stretch.Fill)
    {
        if (image.CanFreeze && !image.IsFrozen) image.Freeze();
        lock (_gate) { RemoveNoLock(id); _images[id] = new(image, Normalize(rect), Math.Clamp(opacity, 0, 1), z, stretch); }
    }
    public void SetImageSequence(string id, IReadOnlyList<BitmapSource> frames, Rect rect, double opacity, double fps, bool loop, int z = 0)
    {
        var safe=frames.Where(x=>x!=null).ToArray();
        foreach(var frame in safe)if(frame.CanFreeze&&!frame.IsFrozen)frame.Freeze();
        lock(_gate){RemoveNoLock(id);if(safe.Length>0)_sequences[id]=new(safe,Normalize(rect),Math.Clamp(opacity,0,1),Math.Clamp(fps,.1,60),loop,z,Stopwatch.GetTimestamp());}
    }
    public void SetText(string id, string text, double x, double y, double size, Brush brush, int z = 0)
    {
        var safeBrush=brush.CloneCurrentValue();if(safeBrush.CanFreeze)safeBrush.Freeze();
        lock(_gate){RemoveNoLock(id);_texts[id]=new(text??"",Math.Clamp(x,-1,1),Math.Clamp(y,-1,1),Math.Clamp(size,8,180),safeBrush,z);}
    }
    public void SetWatermark(string id, string text, double x, double y, double size, string fontFamily, bool bold, bool italic,
        Color fill, double opacity, double scale, double rotation, double outline, bool shadow, int z = 180)
    {
        lock (_gate)
        {
            RemoveNoLock(id);
            if (!string.IsNullOrWhiteSpace(text))
                _watermarks[id] = new(text, Math.Clamp(x, -1, 1), Math.Clamp(y, -1, 1), Math.Clamp(size, 8, 240),
                    string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI Semibold" : fontFamily, bold, italic, fill,
                    Math.Clamp(opacity, 0, 1), Math.Clamp(scale, .2, 4), Math.Clamp(rotation, -360, 360),
                    Math.Clamp(outline, 0, 12), shadow, Math.Clamp(z, -500, 500));
        }
    }
    public void SetLowerThird(string id, string text, Rect rect, double size, int z = 0)
    {SetLowerThirdAdvanced(id,text,"",rect,size,"Segoe UI",Colors.White,Color.FromRgb(10,62,215),.90,"None","None",.4,z);}
    public void SetLowerThirdAdvanced(string id,string text,string secondary,Rect rect,double size,string fontFamily,
        Color textColor,Color backgroundColor,double backgroundOpacity,string animationIn,string animationOut,double animationDuration,int z=100)
    {
        lock(_gate){RemoveNoLock(id);_lowerThirds[id]=new(text??"",secondary??"",Normalize(rect),Math.Clamp(size,8,160),
            string.IsNullOrWhiteSpace(fontFamily)?"Segoe UI":fontFamily,textColor,backgroundColor,Math.Clamp(backgroundOpacity,0,1),
            animationIn??"None",animationOut??"None",Math.Clamp(animationDuration,.05,10),z,Stopwatch.GetTimestamp(),0);}
    }
    public void BeginLowerThirdOut(string id)
    {lock(_gate){if(_lowerThirds.TryGetValue(id,out var layer))_lowerThirds[id]=layer with{Removing=Stopwatch.GetTimestamp()};}}
    public void SetLayerVisible(string id,bool visible){lock(_gate){if(visible)_hidden.Remove(id);else _hidden.Add(id);}}
    public void SetLayerZ(string id,int z)
    {
        lock(_gate)
        {
            z=Math.Clamp(z,-500,500);
            if(_images.TryGetValue(id,out var image))_images[id]=image with{Z=z};
            if(_sequences.TryGetValue(id,out var sequence))_sequences[id]=sequence with{Z=z};
            if(_texts.TryGetValue(id,out var text))_texts[id]=text with{Z=z};
            if(_watermarks.TryGetValue(id,out var watermark))_watermarks[id]=watermark with{Z=z};
            if(_lowerThirds.TryGetValue(id,out var lower))_lowerThirds[id]=lower with{Z=z};
            if(_movingTexts.TryGetValue(id,out var moving))_movingTexts[id]=moving with{Z=z};
            if(_clocks.TryGetValue(id,out var clock))_clocks[id]=clock with{Z=z};
            if(_timers.TryGetValue(id,out var timer))_timers[id]=timer with{Z=z};
            if(_shapes.TryGetValue(id,out var shape))_shapes[id]=shape with{Z=z};
        }
    }
    public bool DuplicateLayer(string sourceId,string destinationId,int z)
    {
        lock(_gate)
        {
            RemoveNoLock(destinationId);z=Math.Clamp(z,-500,500);
            if(_images.TryGetValue(sourceId,out var image)){_images[destinationId]=image with{Z=z};return true;}
            if(_sequences.TryGetValue(sourceId,out var sequence)){_sequences[destinationId]=sequence with{Z=z,Started=Stopwatch.GetTimestamp()};return true;}
            if(_texts.TryGetValue(sourceId,out var text)){_texts[destinationId]=text with{Z=z};return true;}
            if(_watermarks.TryGetValue(sourceId,out var watermark)){_watermarks[destinationId]=watermark with{Z=z};return true;}
            if(_lowerThirds.TryGetValue(sourceId,out var lower)){_lowerThirds[destinationId]=lower with{Z=z,Started=Stopwatch.GetTimestamp(),Removing=0};return true;}
            if(_movingTexts.TryGetValue(sourceId,out var moving)){_movingTexts[destinationId]=moving with{Z=z,Started=Stopwatch.GetTimestamp()};return true;}
            if(_clocks.TryGetValue(sourceId,out var clock)){_clocks[destinationId]=clock with{Z=z};return true;}
            if(_timers.TryGetValue(sourceId,out var timer)){_timers[destinationId]=timer with{Z=z,Started=Stopwatch.GetTimestamp()};return true;}
            if(_shapes.TryGetValue(sourceId,out var shape)){_shapes[destinationId]=shape with{Z=z};return true;}
            return false;
        }
    }
    public void SetMovingText(string id, string text, double axis, double size, double speed, bool vertical, int z = 0)
    {lock(_gate){RemoveNoLock(id);_movingTexts[id]=new(text??"",Math.Clamp(axis,0,1),Math.Clamp(size,8,100),Math.Clamp(speed,.01,1),vertical,z,Stopwatch.GetTimestamp());}}
    public void SetClock(string id, double x, double y, double size, int z = 0)
    {lock(_gate){RemoveNoLock(id);_clocks[id]=new(Math.Clamp(x,-1,1),Math.Clamp(y,-1,1),Math.Clamp(size,8,120),z);}}
    public void SetTimer(string id, double x, double y, double size, bool stopwatch, int z = 0)
    {lock(_gate){RemoveNoLock(id);_timers[id]=new(Math.Clamp(x,-1,1),Math.Clamp(y,-1,1),Math.Clamp(size,8,120),stopwatch,z,Stopwatch.GetTimestamp());}}
    public void SetShape(string id, Rect rect, Color fill, double opacity, int z = 0)
    {lock(_gate){RemoveNoLock(id);_shapes[id]=new(Normalize(rect),fill,Math.Clamp(opacity,0,1),z);}}
    public void SetLShape(bool enabled, Rect programRect)
    {lock(_gate){_lShapeEnabled=enabled;_lShapeProgramRect=Normalize(programRect);}}
    public void Remove(string id) { lock (_gate) RemoveNoLock(id); }
    void RemoveNoLock(string id){_images.Remove(id);_sequences.Remove(id);_texts.Remove(id);_watermarks.Remove(id);_lowerThirds.Remove(id);_movingTexts.Remove(id);_clocks.Remove(id);_timers.Remove(id);_shapes.Remove(id);_hidden.Remove(id);}
    public void Clear() { lock (_gate) { _images.Clear();_sequences.Clear();_texts.Clear();_watermarks.Clear();_lowerThirds.Clear();_movingTexts.Clear();_clocks.Clear();_timers.Clear();_shapes.Clear();_hidden.Clear();_keyEnabled=false;_lShapeEnabled=false; } }

    public BitmapSource Compose(BitmapSource frame)
    {
        bool enabled,lShapeEnabled; Color key; double tolerance, softness, spill, dullness, spectral, edge, erode, shadow, rotation; Rect transform,lShapeRect; Thickness crop; Stretch stretch;
        ImageLayer[] layers;SequenceLayer[] sequences;TextLayer[] texts;WatermarkLayer[] watermarks;LowerThirdLayer[] lowerThirds;MovingTextLayer[] movingTexts;ClockLayer[] clocks;TimerLayer[] timers;ShapeLayer[] shapes;
        lock (_gate) { enabled=_keyEnabled;key=_keyColor;tolerance=_keyTolerance;softness=_keySoftness;spill=_keySpill;dullness=_keyDullness;spectral=_keySpectralBalance;edge=_keyEdgeSoftness;erode=_keyErode;shadow=_keyShadowRecovery;transform=_keyTransform;crop=_keyCrop;rotation=_keyRotation;stretch=_keyStretch;lShapeEnabled=_lShapeEnabled;lShapeRect=_lShapeProgramRect;layers=_images.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();sequences=_sequences.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();texts=_texts.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();watermarks=_watermarks.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();lowerThirds=_lowerThirds.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();movingTexts=_movingTexts.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();clocks=_clocks.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();timers=_timers.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray();shapes=_shapes.Where(x=>!_hidden.Contains(x.Key)).Select(x=>x.Value).ToArray(); }
        if (!enabled&&!lShapeEnabled&&layers.Length==0&&sequences.Length==0&&texts.Length==0&&watermarks.Length==0&&lowerThirds.Length==0&&movingTexts.Length==0&&clocks.Length==0&&timers.Length==0&&shapes.Length==0) return frame;
        int width = frame.PixelWidth, height = frame.PixelHeight;
        BitmapSource foreground = enabled ? ApplyChroma(frame, key, tolerance, softness, spill, dullness, spectral, edge, erode, shadow) : frame;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual,BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(visual,EdgeMode.Unspecified);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
            // Negative-Z sources are true key backgrounds. Positive-Z graphics are
            // downstream overlays and must remain visible above the keyed foreground.
            foreach (var layer in layers.Where(x => x.Z < 0).OrderBy(x => x.Z))
            {
                dc.PushOpacity(layer.Opacity);DrawImageLayer(dc,layer,width,height);dc.Pop();
            }
            var sourceRect = new Rect(crop.Left * foreground.PixelWidth, crop.Top * foreground.PixelHeight,
                foreground.PixelWidth * (1 - crop.Left - crop.Right), foreground.PixelHeight * (1 - crop.Top - crop.Bottom));
            var cropped = new CroppedBitmap(foreground, new Int32Rect((int)sourceRect.X, (int)sourceRect.Y,
                Math.Max(1, (int)sourceRect.Width), Math.Max(1, (int)sourceRect.Height)));
            var target = Pixels(lShapeEnabled?Combine(lShapeRect,transform):transform, width, height);
            dc.PushTransform(new RotateTransform(rotation, target.X + target.Width / 2, target.Y + target.Height / 2));
            var brush = new ImageBrush(cropped) { Stretch = stretch };
            dc.DrawRectangle(brush, null, target);
            dc.Pop();
            var overlays=new List<(int Z,Action<DrawingContext> Draw)>();
            foreach(var layer in layers.Where(x=>x.Z>=0))overlays.Add((layer.Z,ctx=>{ctx.PushOpacity(layer.Opacity);DrawImageLayer(ctx,layer,width,height);ctx.Pop();}));
            foreach(var layer in sequences)
            {
                var selected=SelectSequenceFrame(layer);if(selected==null)continue;
                overlays.Add((layer.Z,ctx=>{ctx.PushOpacity(layer.Opacity);ctx.DrawImage(selected,Pixels(layer.Rect,width,height));ctx.Pop();}));
            }
            foreach(var layer in shapes)overlays.Add((layer.Z,ctx=>{var b=new SolidColorBrush(layer.Fill){Opacity=layer.Opacity};b.Freeze();ctx.DrawRectangle(b,null,Pixels(layer.Rect,width,height));}));
            foreach(var layer in texts)overlays.Add((layer.Z,ctx=>DrawText(ctx,layer.Text,layer.X*width,layer.Y*height,layer.Size,layer.Brush)));
            foreach(var layer in watermarks)overlays.Add((layer.Z,ctx=>DrawWatermark(ctx,layer,width,height)));
            foreach(var layer in lowerThirds)overlays.Add((layer.Z,ctx=>DrawLowerThird(ctx,layer,width,height)));
            foreach(var layer in movingTexts)overlays.Add((layer.Z,ctx=>DrawMovingText(ctx,layer,width,height)));
            foreach(var layer in clocks)overlays.Add((layer.Z,ctx=>DrawText(ctx,DateTime.Now.ToString("HH:mm:ss",CultureInfo.InvariantCulture),layer.X*width,layer.Y*height,layer.Size,Brushes.White)));
            foreach(var layer in timers)overlays.Add((layer.Z,ctx=>DrawTimer(ctx,layer,width,height)));
            foreach(var overlay in overlays.OrderBy(x=>x.Z))overlay.Draw(dc);
        }
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        output.Render(visual); output.Freeze(); return output;
    }

    static Rect Normalize(Rect r) => new(Math.Clamp(r.X, 0, 1), Math.Clamp(r.Y, 0, 1), Math.Clamp(r.Width, .001, 1), Math.Clamp(r.Height, .001, 1));
    static Rect NormalizeKeyTransform(Rect r) => new(Math.Clamp(r.X, -1, 1), Math.Clamp(r.Y, -1, 1), Math.Clamp(r.Width, .01, 2), Math.Clamp(r.Height, .01, 2));
    static Rect Combine(Rect outer,Rect inner)=>new(outer.X+inner.X*outer.Width,outer.Y+inner.Y*outer.Height,inner.Width*outer.Width,inner.Height*outer.Height);
    static Rect Pixels(Rect r, int w, int h) => new(r.X * w, r.Y * h, r.Width * w, r.Height * h);
    static void DrawImageLayer(DrawingContext dc,ImageLayer layer,int width,int height)
    {
        // Stable integer-pixel placement prevents a static transparent PNG/logo from
        // being sampled at a different sub-pixel phase on consecutive frames. That
        // phase shimmer is especially visible as edge flicker on SDI/interlaced TVs.
        var raw=Pixels(layer.Rect,width,height);
        var target=new Rect(Math.Round(raw.X),Math.Round(raw.Y),Math.Max(1,Math.Round(raw.Width)),Math.Max(1,Math.Round(raw.Height)));
        var imageBrush=new ImageBrush(layer.Image){Stretch=layer.Stretch,AlignmentX=AlignmentX.Center,AlignmentY=AlignmentY.Center};
        RenderOptions.SetBitmapScalingMode(imageBrush,BitmapScalingMode.HighQuality);
        if(imageBrush.CanFreeze)imageBrush.Freeze();dc.DrawRectangle(imageBrush,null,target);
    }
    static BitmapSource? SelectSequenceFrame(SequenceLayer layer)
    {
        if(layer.Frames.Length==0)return null;double elapsed=(Stopwatch.GetTimestamp()-layer.Started)/(double)Stopwatch.Frequency;
        long raw=(long)Math.Floor(elapsed*layer.Fps);int index=layer.Loop?(int)(raw%layer.Frames.Length):(int)Math.Min(raw,layer.Frames.Length-1);return layer.Frames[Math.Max(0,index)];
    }
    static void DrawText(DrawingContext dc,string text,double x,double y,double size,Brush brush)
    {
        if(string.IsNullOrWhiteSpace(text))return;var formatted=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI Semibold"),size,brush,1.0){MaxTextWidth=4096,Trimming=TextTrimming.CharacterEllipsis};
        dc.DrawText(formatted,new Point(x,y));
    }
    static void DrawWatermark(DrawingContext dc,WatermarkLayer layer,int width,int height)
    {
        if(string.IsNullOrWhiteSpace(layer.Text))return;
        double outputScale=Math.Max(.25,height/1080.0);
        double fontSize=layer.Size*layer.Scale*outputScale;
        var family=new FontFamily(layer.FontFamily);
        var typeface=new Typeface(family,layer.Italic?FontStyles.Italic:FontStyles.Normal,
            layer.Bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal);
        var fill=new SolidColorBrush(layer.Fill);fill.Freeze();
        var formatted=new FormattedText(layer.Text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,typeface,fontSize,fill,1.0)
        {MaxTextWidth=Math.Max(1,width*1.5),Trimming=TextTrimming.CharacterEllipsis};
        var geometry=formatted.BuildGeometry(new Point(layer.X*width,layer.Y*height));
        var bounds=geometry.Bounds;
        var center=new Point(bounds.X+bounds.Width/2,bounds.Y+bounds.Height/2);
        dc.PushOpacity(layer.Opacity);
        dc.PushTransform(new RotateTransform(layer.Rotation,center.X,center.Y));
        if(layer.Shadow)
        {
            var shadow=new SolidColorBrush(Color.FromArgb(170,0,0,0));shadow.Freeze();
            dc.PushTransform(new TranslateTransform(Math.Max(1,2*outputScale),Math.Max(1,2*outputScale)));
            dc.DrawGeometry(shadow,null,geometry);dc.Pop();
        }
        Pen? outline=null;
        if(layer.Outline>0)
        {
            var outlineBrush=new SolidColorBrush(Color.FromArgb(230,0,0,0));outlineBrush.Freeze();
            outline=new Pen(outlineBrush,Math.Max(.5,layer.Outline*outputScale));outline.Freeze();
        }
        dc.DrawGeometry(fill,outline,geometry);
        dc.Pop();dc.Pop();
    }
    static void DrawLowerThird(DrawingContext dc,LowerThirdLayer layer,int width,int height)
    {
        var rect=Pixels(layer.Rect,width,height);double seconds=(Stopwatch.GetTimestamp()-layer.Started)/(double)Stopwatch.Frequency;
        double progress=Math.Clamp(seconds/layer.AnimationDuration,0,1),opacity=1,dx=0,dy=0;
        string animation=layer.AnimationIn??"None";
        if(animation.Contains("Left",StringComparison.OrdinalIgnoreCase))dx=-rect.Width*(1-progress);
        else if(animation.Contains("Right",StringComparison.OrdinalIgnoreCase))dx=rect.Width*(1-progress);
        else if(animation.Contains("Up",StringComparison.OrdinalIgnoreCase))dy=-rect.Height*(1-progress);
        else if(animation.Contains("Down",StringComparison.OrdinalIgnoreCase))dy=rect.Height*(1-progress);
        else if(animation.Contains("Fade",StringComparison.OrdinalIgnoreCase))opacity=progress;
        if(layer.Removing>0)
        {
            double outSeconds=(Stopwatch.GetTimestamp()-layer.Removing)/(double)Stopwatch.Frequency;double outProgress=Math.Clamp(outSeconds/layer.AnimationDuration,0,1);string outAnimation=layer.AnimationOut??"None";
            if(outAnimation.Contains("Left",StringComparison.OrdinalIgnoreCase))dx=-rect.Width*outProgress;
            else if(outAnimation.Contains("Right",StringComparison.OrdinalIgnoreCase))dx=rect.Width*outProgress;
            else if(outAnimation.Contains("Up",StringComparison.OrdinalIgnoreCase))dy=-rect.Height*outProgress;
            else if(outAnimation.Contains("Down",StringComparison.OrdinalIgnoreCase))dy=rect.Height*outProgress;
            else opacity=1-outProgress;
        }
        dc.PushOpacity(opacity);dc.PushTransform(new TranslateTransform(dx,dy));
        var background=new SolidColorBrush(layer.BackgroundColor){Opacity=layer.BackgroundOpacity};background.Freeze();var borderBrush=new SolidColorBrush(Color.FromArgb(230,88,215,255));borderBrush.Freeze();var border=new Pen(borderBrush,Math.Max(1,width/960.0));border.Freeze();
        dc.DrawRoundedRectangle(background,border,rect,Math.Max(4,height*.012),Math.Max(4,height*.012));
        var textBrush=new SolidColorBrush(layer.TextColor);textBrush.Freeze();double titleY=rect.Y+Math.Max(3,rect.Height*.10);DrawStyledText(dc,layer.Text,rect.X+Math.Max(10,width*.012),titleY,layer.Size,layer.FontFamily,textBrush,true);
        if(!string.IsNullOrWhiteSpace(layer.Secondary))DrawStyledText(dc,layer.Secondary,rect.X+Math.Max(10,width*.012),titleY+layer.Size*1.05,Math.Max(8,layer.Size*.55),layer.FontFamily,textBrush,false);
        dc.Pop();dc.Pop();
    }
    static void DrawStyledText(DrawingContext dc,string text,double x,double y,double size,string fontFamily,Brush brush,bool bold)
    {
        if(string.IsNullOrWhiteSpace(text))return;var formatted=new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,
            new Typeface(new FontFamily(fontFamily),FontStyles.Normal,bold?FontWeights.Bold:FontWeights.Normal,FontStretches.Normal),size,brush,1.0)
        {MaxTextWidth=4096,Trimming=TextTrimming.CharacterEllipsis};dc.DrawText(formatted,new Point(x,y));
    }
    static void DrawMovingText(DrawingContext dc,MovingTextLayer layer,int width,int height)
    {
        double elapsed=(Stopwatch.GetTimestamp()-layer.Started)/(double)Stopwatch.Frequency;
        if(layer.Vertical){double y=(1-(elapsed*layer.Speed%1.35))*height;DrawText(dc,layer.Text,layer.Axis*width,y,layer.Size,Brushes.White);}
        else{double x=(1-(elapsed*layer.Speed%1.35))*width;DrawText(dc,layer.Text,x,layer.Axis*height,layer.Size,Brushes.White);}
    }
    static void DrawTimer(DrawingContext dc,TimerLayer layer,int width,int height)
    {
        double elapsed=Math.Max(0,(Stopwatch.GetTimestamp()-layer.Started)/(double)Stopwatch.Frequency);var span=TimeSpan.FromSeconds(elapsed);
        string prefix=layer.Stopwatch?"":"T ";string value=prefix+(span.TotalHours>=1?$"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}":$"{span.Minutes:00}:{span.Seconds:00}");DrawText(dc,value,layer.X*width,layer.Y*height,layer.Size,Brushes.White);
    }
    static BitmapSource ApplyChroma(BitmapSource source, Color key, double tolerance, double softness,
        double spill, double dullness, double spectralBalance, double edgeSoftness, double erodeDilate, double shadowRecovery)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4; var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
          for (int i = y * stride; i < (y + 1) * stride; i += 4)
          {
            double db = pixels[i] - key.B, dg = pixels[i + 1] - key.G, dr = pixels[i + 2] - key.R;
            double dominant = key.G >= key.B ? Math.Abs(dg) : Math.Abs(db);
            double chromaDistance = Math.Sqrt(db * db + dg * dg + dr * dr) / (Math.Sqrt(3) * 255);
            double spectralDistance = dominant / 255.0;
            double distance = chromaDistance * (1 - spectralBalance) + spectralDistance * spectralBalance;
            double saturation = (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]))) / 255.0;
            double adjustedTolerance = Math.Clamp(tolerance + erodeDilate * .08 + (1 - saturation) * dullness * .10, .001, 1);
            double feather = Math.Clamp(softness + edgeSoftness * .15, 0, .65);
            double alpha = distance <= adjustedTolerance ? 0 : distance >= adjustedTolerance + feather ? 1 : feather <= 0 ? 1 : (distance - adjustedTolerance) / feather;
            if (shadowRecovery > 0 && alpha < 1)
            {
                double luminance = (pixels[i + 2] * .2126 + pixels[i + 1] * .7152 + pixels[i] * .0722) / 255.0;
                alpha = Math.Clamp(alpha + (1 - luminance) * shadowRecovery * .25, 0, 1);
            }
            if (alpha < 1 && spill > 0)
            {
                double neutral = pixels[i + 2] * .2126 + pixels[i + 1] * .7152 + pixels[i] * .0722;
                double amount = spill * (1 - alpha);
                pixels[i] = (byte)Math.Clamp((int)Math.Round(pixels[i] + (neutral - pixels[i]) * amount), 0, 255);
                pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(pixels[i + 1] + (neutral - pixels[i + 1]) * amount), 0, 255);
                pixels[i + 2] = (byte)Math.Clamp((int)Math.Round(pixels[i + 2] + (neutral - pixels[i + 2]) * amount), 0, 255);
            }
            pixels[i + 3] = (byte)Math.Clamp((int)(pixels[i + 3] * alpha), 0, 255);
          }
        });
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride); result.Freeze(); return result;
    }
}

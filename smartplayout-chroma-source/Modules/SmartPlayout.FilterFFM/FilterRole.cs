namespace SmartPlayout.FilterFFM;

public enum SmartScaleMode { Smart=0, Force16x9=1, Force4x3=2, FullStretch=3 }

public sealed record FilterRole(
    SmartScaleMode ScaleMode,
    double Brightness,
    double Contrast,
    double Saturation,
    double Gamma)
{
    public static FilterRole Neutral => new(SmartScaleMode.Smart,0,0,0,1);
}

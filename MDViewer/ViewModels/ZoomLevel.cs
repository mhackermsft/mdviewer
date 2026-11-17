namespace MDViewer.ViewModels;

/// <summary>
/// Document zoom: 25% to 500% in 10% steps. The factor is kept on whole
/// percents, so repeated steps never drift (0.1 is not exact in binary:
/// 1.0 - 7 × 0.1 would otherwise show "29%").
/// </summary>
public sealed class ZoomLevel
{
    public const int MinPercent = 25;
    public const int MaxPercent = 500;
    public const int StepPercent = 10;

    public int Percent { get; private set; } = 100;

    public double Factor => Percent / 100.0;

    public string Label => $"{Percent}%";

    public bool CanZoomIn => Percent < MaxPercent;
    public bool CanZoomOut => Percent > MinPercent;

    /// <returns>True when the level changed.</returns>
    public bool ZoomIn() => Set(Percent + StepPercent);

    public bool ZoomOut() => Set(Percent - StepPercent);

    public bool Reset() => Set(100);

    private bool Set(int percent)
    {
        percent = Math.Clamp(percent, MinPercent, MaxPercent);
        if (percent == Percent) return false;
        Percent = percent;
        return true;
    }
}

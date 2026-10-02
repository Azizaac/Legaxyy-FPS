namespace OverlayDataBridge.Models;

public class PowerData
{
    public double? TotalW { get; set; }
    public bool IsEstimate { get; set; }

    // ─── Accumulative energy tracking ────────────────────────────────────────
    public double? TodayKwh { get; set; }
    public double? MonthKwh { get; set; }
}

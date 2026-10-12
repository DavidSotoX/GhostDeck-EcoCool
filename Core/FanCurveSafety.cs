namespace GhostDeck;

public static class FanCurveSafety
{
    public static void ValidateOrThrow(FanCurveSpec fc, int[] ct, int[] cs, int[] gt, int[] gs)
    {
        ArgumentNullException.ThrowIfNull(ct);
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(gt);
        ArgumentNullException.ThrowIfNull(gs);
        static bool NonDecreasing(int[] a)
        {
            for (int i = 1; i < a.Length; i++) if (a[i] < a[i - 1]) return false;
            return true;
        }
        bool shape = ct.Length == fc.Points && cs.Length == fc.Points &&
                     gt.Length == fc.Points && gs.Length == fc.Points;
        if (!shape) throw new ArgumentException($"Fan curve must contain exactly {fc.Points} points per table.");
        if (ct.Any(v => v is < 0 or > 115) || gt.Any(v => v is < 0 or > 115))
            throw new ArgumentOutOfRangeException(nameof(ct), "Fan-curve temperatures must be in 0..115 C.");
        if (cs.Any(v => v < 0 || v > fc.MaxFanPct) || gs.Any(v => v < 0 || v > fc.MaxFanPct))
            throw new ArgumentOutOfRangeException(nameof(cs), $"Fan speeds must be in 0..{fc.MaxFanPct}.");
        if (!NonDecreasing(ct) || !NonDecreasing(gt))
            throw new ArgumentException("Fan-curve temperatures must be non-decreasing.");
        if (!NonDecreasing(cs) || !NonDecreasing(gs))
            throw new ArgumentException("Fan-curve speeds must be non-decreasing.");
    }

    /// <summary>New curves must preserve the known factory cooling envelope. Restore uses
    /// shape validation separately so it can recover the exact pre-transaction state.</summary>
    public static void ValidateForApply(FanCurveSpec fc, int[] ct, int[] cs, int[] gt, int[] gs)
    {
        ValidateOrThrow(fc, ct, cs, gt, gs);
        static void Floor(int[] t, int[] s, int[]? stockT, int[]? stockS)
        {
            if (stockT == null || stockS == null)
                throw new InvalidOperationException("New fan curves require a verified factory cooling envelope.");
            if (stockT.Length != t.Length || stockS.Length != s.Length)
                throw new InvalidOperationException("Factory cooling envelope has an invalid shape.");
            for (int i = 0; i < t.Length; i++)
                if (t[i] > stockT[i] || s[i] < stockS[i])
                    throw new ArgumentException("Curve would delay or reduce known factory cooling. Restore the factory baseline or increase cooling.");
        }
        Floor(ct, cs, fc.FactoryCpuTemp, fc.FactoryCpuSpeed);
        if (!fc.SingleFan) Floor(gt, gs, fc.FactoryGpuTemp, fc.FactoryGpuSpeed);
    }
}

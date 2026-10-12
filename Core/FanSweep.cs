namespace GhostDeck;

/// <summary>
/// "Curve in action" fan calibration. Each EC order is written as a flat curve, then the
/// tachometers are sampled until the mechanical RPM has actually stabilised (or a bounded
/// timeout expires). This matters on the verified 1583EMS1.110 machine: at EC order 150 the
/// fans keep accelerating well after the old fixed 6-second window.
///
/// The caller owns restore in a finally block. This class never touches profile registers.
/// </summary>
public static class FanSweep
{
    // 125 fills the previously unmeasured gap between 100 and the model's 150 ceiling.
    public static readonly int[] DefaultSteps = { 30, 45, 60, 80, 100, 125 };

    public static int[] StepsFor(DeviceProfile dev)
    {
        int max = Math.Max(1, dev.FanCurve?.MaxFanPct ?? 100);
        // A flat calibration table must never reduce the known hot-end factory cooling.
        // Unknown factory envelopes do not authorize a physical calibration.
        var fc = dev.FanCurve;
        if (fc?.FactoryCpuSpeed is not { Length: > 0 } cpu ||
            (!fc.SingleFan && fc.FactoryGpuSpeed is not { Length: > 0 })) return Array.Empty<int>();
        int floor = Math.Max(cpu.Max(), fc.SingleFan ? 0 : fc.FactoryGpuSpeed!.Max());
        return DefaultSteps.Where(v => v <= max && v >= floor)
            .Append(max)
            .Where(v => v >= floor)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
    }

    public const int PollMilliseconds = 1000;
    public const int MinHoldSeconds = 4;
    public const int MaxHoldSeconds = 30;
    public const int StabilitySamples = 5;
    public const int StableRpmAbs = 120;
    public const double StableRpmFraction = 0.025;   // 2.5 % peak-to-peak over the final window

    public readonly record struct StepResult(int DutyPct, int CpuDuty, int GpuDuty, int CpuRpm, int GpuRpm,
                                             int CpuTemp, int GpuTemp, double SecondsToSettle);

    public sealed class Result
    {
        public List<StepResult> Steps { get; } = new();
        public bool HasTach { get; init; }
        public bool Aborted { get; set; }
        public bool RestoreVerified { get; set; }
        public string? Error { get; set; }
        public DateTime Started { get; init; } = DateTime.Now;
        public TimeSpan Duration { get; set; }
    }

    public static Result Run(DeviceProfile dev, int[] steps, Action<int, int, string> progress, CancellationToken ct)
    {
        var fc = dev.FanCurve ?? throw new InvalidOperationException("no fan-curve tables on this model");
        var res = new Result { HasTach = (dev.CpuRpmAddr != 0 || dev.CpuRpmAddr16 != 0) &&
            (fc.SingleFan || dev.GpuRpmAddr != 0 || dev.GpuRpmAddr16 != 0) };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (steps.Length == 0) throw new InvalidOperationException("No safe calibration steps are authorized by the factory contract.");
            if (!res.HasTach) throw new InvalidOperationException("Calibration requires a known tachometer for each controlled fan.");
            var cur = Ec.ReadFanCurve(dev) ?? throw new IOException("Cannot snapshot fan tables for calibration.");
            int[] tC = cur.cpuTemp, tG = cur.gpuTemp;

            for (int si = 0; si < steps.Length; si++)
            {
                if (ct.IsCancellationRequested) { res.Aborted = true; break; }
                int order = Math.Clamp(steps[si], 0, fc.MaxFanPct);
                var flat = Enumerable.Repeat(order, fc.Points).ToArray();
                progress(si, steps.Length, $"{order}");
                EcTransaction.ApplyFanCurveVerified(dev, tC, flat, tG, flat);

                var samples = new List<HwSnapshot>();
                double settledAt = -1;
                for (int sec = 1; sec <= MaxHoldSeconds; sec++)
                {
                    if (ct.WaitHandle.WaitOne(PollMilliseconds)) { res.Aborted = true; break; }
                    if (!Ec.TryReadHw(dev, out var hw))
                        throw new IOException("Calibration telemetry was lost; stop and restore the original state.");
                    samples.Add(hw);
                    progress(si, steps.Length, $"{order} · {sec}s");

                    if (sec >= MinHoldSeconds && IsStable(samples, res.HasTach, fc.SingleFan))
                    {
                        settledAt = sec;
                        break;
                    }
                }
                if (res.Aborted) break;
                if (samples.Count == 0) continue;

                var window = samples.Skip(Math.Max(0, samples.Count - StabilitySamples)).ToArray();
                res.Steps.Add(new StepResult(order,
                    Avg(window, w => w.CpuFan), Avg(window, w => w.GpuFan),
                    Avg(window, w => w.CpuRpm), Avg(window, w => w.GpuRpm),
                    Avg(window, w => w.CpuTemp), Avg(window, w => w.GpuTemp),
                    settledAt));
            }
        }
        catch (Exception ex) { res.Error = ex.Message; }
        res.Duration = elapsed.Elapsed;
        return res;
    }

    public static Result RunRestoring(DeviceProfile dev, int[] steps, Action<int, int, string> progress, CancellationToken ct)
    {
        Result? result = null;
        // Snapshot real EC state, never editor values which may not have been applied.
        var before = Ec.Exclusive(() =>
            (mode: Ec.ReadByte(dev.FanMode), curve: Ec.ReadFanCurve(dev)
                ?? throw new IOException("Cannot capture the real pre-calibration fan state.")));
        // Fail closed before any calibration mutation if the captured state cannot be restored.
        // A successful read is not enough: a mode outside the contract must never be
        // overwritten by a calibration that cannot safely restore it.
        EcTransaction.ValidateFanMode(dev, before.mode);
        if (dev.FanCurve is not { Verified: true } contract)
            throw new InvalidOperationException("Calibration requires a verified factory curve contract.");
        FanCurveSafety.ValidateOrThrow(contract, before.curve.cpuTemp, before.curve.cpuSpeed,
            before.curve.gpuTemp, before.curve.gpuSpeed);
        try { result = Run(dev, steps, progress, ct); }
        finally
        {
            try
            {
                EcTransaction.RestoreFanStateVerified(dev, before.mode, before.curve.cpuTemp,
                    before.curve.cpuSpeed, before.curve.gpuTemp, before.curve.gpuSpeed);
                if (result != null) result.RestoreVerified = true;
            }
            catch (Exception ex)
            {
                result ??= new Result();
                result.Error = (result.Error == null ? "" : result.Error + " | ") + "RESTORE FAILED: " + ex.Message;
            }
        }
        return result ?? new Result { Error = "Calibration did not produce a result." };
    }

    private static int Avg(IEnumerable<HwSnapshot> rows, Func<HwSnapshot, int> pick)
    {
        var vals = rows.Select(pick).ToArray();
        return vals.Length == 0 ? 0 : (int)Math.Round(vals.Average());
    }

    private static bool IsStable(List<HwSnapshot> samples, bool hasTach, bool singleFan = false)
    {
        if (samples.Count < StabilitySamples) return false;
        var w = samples.Skip(samples.Count - StabilitySamples).ToArray();
        if (!hasTach)
            return StableInts(w.Select(x => x.CpuFan), absTolerance: 2, fraction: 0) &&
                   StableInts(w.Select(x => x.GpuFan), absTolerance: 2, fraction: 0);

        var cpu = w.Select(x => x.CpuRpm).Where(v => v > 0).ToArray();
        var gpu = w.Select(x => x.GpuRpm).Where(v => v > 0).ToArray();
        bool cpuOk = cpu.Length == StabilitySamples && StableInts(cpu, StableRpmAbs, StableRpmFraction);
        bool gpuOk = singleFan || gpu.Length == StabilitySamples && StableInts(gpu, StableRpmAbs, StableRpmFraction);
        return cpuOk && gpuOk;
    }

    private static bool StableInts(IEnumerable<int> source, int absTolerance, double fraction)
    {
        var a = source.ToArray();
        if (a.Length == 0) return false;
        double avg = a.Average();
        double tolerance = Math.Max(absTolerance, avg * fraction);
        return a.Max() - a.Min() <= tolerance;
    }

    /// <summary>Facts derived from the measured stable/timeout RPM table only.</summary>
    public static List<(string key, object[] args)> Findings(Result r)
    {
        var f = new List<(string, object[])>();
        if (r.Aborted || r.Error != null || r.Steps.Count < 2 ||
            r.Steps.Any(s => s.SecondsToSettle < 0)) return f;
        for (int fan = 0; fan < 2; fan++)
        {
            var pts = r.Steps.Select(s => (d: s.DutyPct, v: r.HasTach ? (fan == 0 ? s.CpuRpm : s.GpuRpm) : (fan == 0 ? s.CpuDuty : s.GpuDuty)))
                .Where(p => p.v > 0).ToList();
            if (pts.Count < 2) { f.Add(("fc_find_unverified", new object[] { fan + 1 })); continue; }
            int drops = 0; string dropAt = "";
            for (int i = 1; i < pts.Count; i++)
                if (pts[i].v < pts[i - 1].v * 0.95) { drops++; dropAt += (dropAt.Length > 0 ? ", " : "") + pts[i].d; }
            if (drops > 0) f.Add(("fc_find_drop", new object[] { fan + 1, dropAt }));
            // A stable tachometer reading proves stability, not acceptance of an EC order.
            // Never infer physical command compliance from monotonicity alone.
            // The command readback and OEM policy ownership are not captured by this result.
            // Both a flat historical trace and a rising trace remain unverified.
            f.Add(("fc_find_unverified", new object[] { fan + 1 }));
            // Even an increasing RPM trace is observational, not proof that the EC obeyed.
        }
        if (r.HasTach)
        {
            var both = r.Steps.Where(s => s.CpuRpm > 0 && s.GpuRpm > 0).ToList();
            if (both.Count >= 2)
            {
                var worst = both.OrderByDescending(s => Math.Abs(s.CpuRpm - s.GpuRpm) / (double)Math.Max(s.CpuRpm, s.GpuRpm)).First();
                double gap = Math.Abs(worst.CpuRpm - worst.GpuRpm) / (double)Math.Max(worst.CpuRpm, worst.GpuRpm);
                if (gap > 0.35) f.Add(("fc_find_gap", new object[] { worst.DutyPct, worst.CpuRpm, worst.GpuRpm }));
            }
        }
        int tMin = r.Steps.Min(s => s.CpuTemp), tMax = r.Steps.Max(s => s.CpuTemp);
        f.Add(("fc_find_temps", new object[] { tMin, tMax }));
        // Do not declare a clean sweep: no EC effective-order readback is captured.
        return f;
    }

    public static string Report(Result r, DeviceProfile dev, string firmware, string appVersion,
                                IEnumerable<string>? findings = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== GhostDeck EcoCool - stabilized fan sweep ===");
        sb.AppendLine($"Generated: {r.Started:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"App version: {appVersion}");
        sb.AppendLine($"EC firmware: {firmware}");
        sb.AppendLine($"Model contract: {dev.Name}  ({dev.Tier})");
        sb.AppendLine($"RestoreVerified={r.RestoreVerified}");
        string cpuTach = dev.CpuRpmAddr16 != 0 ? $"0x{dev.CpuRpmAddr16:X2}:{dev.CpuRpmAddr16 + 1:X2} (16-bit)" : $"0x{dev.CpuRpmAddr:X2}";
        string gpuTach = dev.GpuRpmAddr16 != 0 ? $"0x{dev.GpuRpmAddr16:X2}:{dev.GpuRpmAddr16 + 1:X2} (16-bit)" : $"0x{dev.GpuRpmAddr:X2}";
        sb.AppendLine($"Tachometers: {(r.HasTach ? $"CPU {cpuTach} / GPU {gpuTach}" : "none - duty readback only")}");
        sb.AppendLine($"Adaptive hold: min {MinHoldSeconds}s, max {MaxHoldSeconds}s, stable window {StabilitySamples}s");
        sb.AppendLine($"Duration: {r.Duration.TotalSeconds:0} s{(r.Aborted ? "  (ABORTED)" : "")}{(r.Error != null ? "  ERROR: " + r.Error : "")}");
        sb.AppendLine();
        sb.AppendLine("--- Steps (EC order -> stabilized RPM) ---");
        sb.AppendLine("order  CPU duty  GPU duty  CPU rpm  GPU rpm  CPU C  GPU C  stable s");
        foreach (var s2 in r.Steps)
            sb.AppendLine($"{s2.DutyPct,5}  {s2.CpuDuty,8}  {s2.GpuDuty,8}  {(s2.CpuRpm > 0 ? s2.CpuRpm.ToString() : "--"),7}  {(s2.GpuRpm > 0 ? s2.GpuRpm.ToString() : "--"),7}  {s2.CpuTemp,5}  {s2.GpuTemp,5}  {(s2.SecondsToSettle < 0 ? "timeout" : s2.SecondsToSettle.ToString("0")),8}");
        sb.AppendLine();
        if (findings is { } lines)
        {
            var list = lines.ToList();
            if (list.Count > 0)
            {
                sb.AppendLine("--- What this says (shown in the app's language) ---");
                foreach (var line in list) sb.AppendLine("- " + line);
                sb.AppendLine();
            }
        }
        sb.AppendLine("order = EC/MSI fan command scale, not a physical percent of rated motor speed.");
        sb.AppendLine("stable s = seconds until the final tachometer window stopped changing materially; timeout means the 30 s ceiling was reached.");
        return sb.ToString();
    }
}

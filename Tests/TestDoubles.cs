namespace GhostDeck;
// Deliberately in-memory transport and contracts. This harness cannot access WMI or hardware.
public enum Tier { Tested, Experimental }
public enum ProfileId { Silent, Balanced, Extreme, SuperBattery }
public record FourthModeSpec(string Name, byte ShiftValue);
public record FanCurveSpec(byte AdvancedModeValue, byte CpuTempBase, byte CpuSpeedBase,
    byte GpuTempBase, byte GpuSpeedBase, int Points, bool Verified = false,
    bool SingleFan = false, int MaxFanPct = 150, int[]? FactoryCpuTemp = null,
    int[]? FactoryCpuSpeed = null, int[]? FactoryGpuTemp = null, int[]? FactoryGpuSpeed = null);
public sealed class DeviceProfile
{
    public string Name { get; set; } = "In-memory fixture";
    public string? MatchedPrefix(string firmware) => firmware == EcoCoolController.ExactOemEcoFirmware ? firmware : null;
    public Tier Tier { get; set; } = Tier.Tested;
    public FanCurveSpec? FanCurve { get; set; }
    public byte ShiftMode { get; set; } = 0xD2;
    public byte FanMode { get; set; } = 0xD4;
    public byte FanSilentValue { get; set; } = 0x1D;
    public byte ShiftTurboValue { get; set; } = 0xC4;
    public byte ShiftEcoValue { get; set; } = 0xC2;
    public FourthModeSpec? FourthMode { get; set; }
    public byte CpuRpmAddr { get; set; } = 0xC9;
    public byte GpuRpmAddr { get; set; } = 0xCB;
    public byte CpuRpmAddr16 { get; set; }
    public byte GpuRpmAddr16 { get; set; }
    public Dictionary<ProfileId, (byte addr, byte val)[]> Recipes { get; set; } = new();
}
public readonly record struct HwSnapshot(int CpuTemp, int GpuTemp, int CpuFan, int GpuFan,
    int ChargeLimit, string Firmware, int CpuRpm = 0, int GpuRpm = 0);
public static class Ec
{
    public static readonly byte[] Bytes = new byte[256];
    public static int Writes;
    public static readonly List<(byte addr, byte val)> WriteTrace = new();
    public static int? FailWriteAt;
    public static bool FailEveryWrite;
    public static bool CorruptNextCurveRead;
    private static void Write(byte a, byte v)
    {
        Writes++; WriteTrace.Add((a,v));
        if (FailEveryWrite || Writes == FailWriteAt) throw new IOException("injected write failure");
        Bytes[a] = v;
    }
    public static void Reset() { Array.Clear(Bytes); WriteTrace.Clear(); Writes = 0; FailWriteAt = null; FailEveryWrite = false; CorruptNextCurveRead = false; }
    public static T Exclusive<T>(Func<T> body) => body();
    public static byte ReadByte(byte a) => Bytes[a];
    public static byte[] ReadMany(byte[] a) => a.Select(ReadByte).ToArray();
    public static bool TryGetCurrent(DeviceProfile d, out ProfileId p) => EcProfileState.TryDecode(d, Bytes[d.ShiftMode], Bytes[d.FanMode], out p);
    public static void SetFanMode(DeviceProfile d, byte v) => Write(d.FanMode, v);
    public static void Apply(IEnumerable<(byte addr, byte val)> rows)
    {
        foreach (var (a,v) in rows) Write(a, a == 0xEB ? (byte)((Bytes[a] & 0xF0) | (v & 0x0F)) : v);
    }
    public static (int[] cpuTemp, int[] cpuSpeed, int[] gpuTemp, int[] gpuSpeed)? ReadFanCurve(DeviceProfile d)
    {
        var f = d.FanCurve!;
        int[] Read(byte a) => Enumerable.Range(a, f.Points).Select(i => (int)Bytes[i]).ToArray();
        var ct = Read(f.CpuTempBase);
        if (CorruptNextCurveRead && Writes > 0) { ct[0]++; CorruptNextCurveRead = false; }
        return (ct,Read(f.CpuSpeedBase),Read(f.GpuTempBase),Read(f.GpuSpeedBase));
    }
    public static void WriteFanCurve(DeviceProfile d,int[] ct,int[] cs,int[] gt,int[] gs)
    {
        var f=d.FanCurve!;
        void Put(byte a,int[] values) { for(int i=0;i<f.Points;i++) Write((byte)(a+i),(byte)values[i]); }
        Put(f.CpuTempBase,ct); Put(f.CpuSpeedBase,cs);
        if (!f.SingleFan) { Put(f.GpuTempBase,gt); Put(f.GpuSpeedBase,gs); }
    }
    public static bool TryReadHw(DeviceProfile d,out HwSnapshot hw) { hw=default; return false; }
}

public static class SafetyEdition { public static bool HardwareWritesAuthorized { get; set; } = true; }

public partial class AppSettings
{
    public Action<AppSettings>? OnSave;
    public void SaveCritical() => OnSave?.Invoke(this);
}
public static class AppLifecycle { public static void Report(Exception e, string context) { } }
public static class CurveModel { public static int[] Shift(int[] a, int n) => a.Select(v => v+n).ToArray(); }
public static class PowerPlan
{
    public static readonly Guid ModeBestEfficiency = Guid.Empty;
    public static bool TryGetUserPowerMode(out Guid ac, out Guid dc) => throw new Exception("Unexpected Windows ownership");
    public static bool TryGetActiveScheme(out Guid plan) => throw new Exception("Unexpected Windows ownership");
    public static bool TryReadBoost(Guid plan, out uint ac, out uint dc) => throw new Exception("Unexpected Windows ownership");
    public static bool TrySetPowerMode(Guid mode) => throw new Exception("Unexpected Windows ownership");
    public static bool TrySetPowerModes(Guid ac, Guid dc) => throw new Exception("Unexpected Windows ownership");
    public static bool WriteBoost(Guid plan, uint ac, uint dc) => throw new Exception("Unexpected Windows ownership");
}

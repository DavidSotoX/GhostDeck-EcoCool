using System.Text;

namespace GhostDeck;

/// <summary>
/// Controlled first-use qualification for the exact 1583EMS1.110 OEM-ECO policy.
/// It exercises the SAME EcoCoolController path used by the UI, observes it briefly,
/// then restores the exact pre-test state. Persistent UI availability is granted only
/// after apply + repeated verification + exact restore all pass.
/// </summary>
public static class EcoCoolQualificationTest
{
    private static readonly int[] StockCpuTemp  = { 0, 55, 62, 69, 76, 82 };
    private static readonly int[] StockCpuSpeed = { 38, 43, 48, 54, 60, 70 };
    private static readonly int[] StockGpuTemp  = { 0, 55, 61, 67, 72, 77 };
    private static readonly int[] StockGpuSpeed = { 0, 43, 48, 54, 60, 70 };

    private static void RequireExactStockEco(DeviceProfile dev)
    {
        if (!Ec.TryGetCurrent(dev, out var p) || p != ProfileId.SuperBattery)
            throw new InvalidOperationException("Qualification must start in MSI OEM ECO/SuperBattery.");
        if (Ec.ReadByte(dev.FanMode) != 0x0D)
            throw new InvalidOperationException("Qualification must start with OEM ECO fan mode Auto (D4=0D).");
        foreach (var (addr, val) in dev.Recipes[ProfileId.SuperBattery])
        {
            byte got = Ec.ReadByte(addr);
            bool ok = addr == 0xEB ? (got & 0x0F) == (val & 0x0F) : got == val;
            if (!ok)
                throw new InvalidOperationException($"OEM ECO prerequisite mismatch at 0x{addr:X2}.");
        }

        var c = Ec.ReadFanCurve(dev) ?? throw new InvalidOperationException("Cannot read the stock fan curve.");
        if (!c.cpuTemp.SequenceEqual(StockCpuTemp) || !c.cpuSpeed.SequenceEqual(StockCpuSpeed) ||
            !c.gpuTemp.SequenceEqual(StockGpuTemp) || !c.gpuSpeed.SequenceEqual(StockGpuSpeed))
            throw new InvalidOperationException("Qualification requires the exact observed MSI stock curve.");
    }

    private static void RequireExactRestored(DeviceProfile dev)
    {
        RequireExactStockEco(dev);
    }

    public static int Run(string reportPath)
    {
        string outPath = string.IsNullOrWhiteSpace(reportPath)
            ? Path.GetFullPath("ECOCOOL_QUALIFICATION.txt")
            : Path.GetFullPath(reportPath);
        var log = new StringBuilder();
        AppSettings? settings = null;
        DeviceProfile? dev = null;
        string fw = "";
        bool applied = false;
        bool restored = false;
        bool pass = false;

        try
        {
            if (!SafetyEdition.HardwareWritesAuthorized)
                throw new InvalidOperationException("Hardware qualification is locked pending review of the exact laptop model, BIOS and EC contract.");
            settings = AppSettings.Load();
            if (settings.EcoCoolPhase.Length > 0)
                throw new InvalidOperationException("An EcoCool transaction is already pending/active. Restore it before qualification.");

            fw = Ec.ReadFirmware();
            dev = Devices.Detect(fw) ?? throw new InvalidOperationException("Firmware is not in the compiled model allowlist.");
            if (!EcoCoolController.CanQualifyOemEco(dev, fw))
                throw new InvalidOperationException("Qualification is restricted to exact firmware 1583EMS1.110 with the verified fan contract.");

            RequireExactStockEco(dev);
            log.AppendLine("=== GhostDeck EcoCool OEM-ECO qualification ===");
            log.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            log.AppendLine($"Safety version: {SafetyEdition.Version}");
            log.AppendLine($"Firmware: {fw}");
            log.AppendLine($"Contract: {dev.Name}");
            log.AppendLine("BEFORE: exact OEM ECO + Auto + stock fan tables verified.");

            EcoCoolController.ApplyForQualification(settings, dev, fw);
            applied = true;

            for (int i = 1; i <= 8; i++)
            {
                if (!EcoCoolController.VerifyPersistedActive(settings, dev, fw, out string err))
                    throw new IOException($"Active verification failed at sample {i}: {err}");
                if (!Ec.TryReadHw(dev, out var hw))
                    throw new IOException($"Hardware telemetry read failed at sample {i}.");
                log.AppendLine($"ACTIVE[{i}]: CPU={hw.CpuTemp}C GPU={hw.GpuTemp}C CPU_RPM={hw.CpuRpm} GPU_RPM={hw.GpuRpm} CPU_DUTY={hw.CpuFan} GPU_DUTY={hw.GpuFan}");
                Thread.Sleep(500);
            }

            EcoCoolController.Restore(settings, dev, fw);
            applied = false;
            RequireExactRestored(dev);
            restored = true;

            EcoCoolController.MarkOemEcoQualified(settings, fw);
            pass = true;
            log.AppendLine("RESTORED: exact OEM ECO + Auto + stock fan tables verified.");
            log.AppendLine("QUALIFICATION: persistent ECO COOL unlocked for this exact firmware/policy schema.");
        }
        catch (Exception ex)
        {
            log.AppendLine("ERROR: " + ex.Message);
            if (applied && settings != null && dev != null && fw.Length > 0)
            {
                try
                {
                    EcoCoolController.Restore(settings, dev, fw);
                    applied = false;
                    RequireExactRestored(dev);
                    restored = true;
                    log.AppendLine("FAIL-ROLLBACK: exact OEM ECO baseline restored.");
                }
                catch (Exception rollbackEx)
                {
                    log.AppendLine("ROLLBACK_ERROR: " + rollbackEx.Message);
                }
            }
        }

        log.AppendLine($"RESULT: {(pass ? "PASS" : "FAIL")}");
        log.AppendLine($"RestoreVerified={restored}");
        log.AppendLine($"QualificationRecorded={(pass && settings != null && EcoCoolController.IsOemEcoQualified(settings, fw))}");
        SafeFile.WriteAllTextAtomic(outPath, log.ToString());
        Console.WriteLine(log.ToString());
        Console.WriteLine("qualification report: " + outPath);
        return pass ? 0 : 1;
    }
}

using System.Text;
using System.Text.Json;

namespace GhostDeck;

/// <summary>
/// One-shot, exact-firmware probe for the user's 1583EMS1.110 machine.
/// Purpose: prove whether the OEM ECO recipe (D2=C2, EB low nibble=0F) can coexist
/// with Advanced fan mode (D4=8D) while leaving the complete fan tables untouched.
///
/// The probe changes ONLY the fan-mode byte for ~3 seconds, then restores the exact
/// previous byte in a finally block. A durable journal is written before mutation so
/// an interrupted run can be recovered with --recover-eco-probe.
///
/// This does NOT enable persistent EcoCool on 1583EMS1.110. It is evidence collection only.
/// </summary>
public static class EcoAdvancedProbe
{
    public const string ExactFirmware = "1583EMS1.110";
    private const byte ExpectedEcoShift = 0xC2;
    private const byte ExpectedAutoMode = 0x0D;
    private const byte ExpectedAdvancedMode = 0x8D;
    private const byte SuperBattAddr = 0xEB;
    private const byte ExpectedSuperBattLowNibble = 0x0F;
    private const int HoldMilliseconds = 3000;

    private static readonly int[] ExpectedCpuTemp  = { 0, 55, 62, 69, 76, 82 };
    private static readonly int[] ExpectedCpuSpeed = { 38, 43, 48, 54, 60, 70 };
    private static readonly int[] ExpectedGpuTemp  = { 0, 55, 61, 67, 72, 77 };
    private static readonly int[] ExpectedGpuSpeed = { 0, 43, 48, 54, 60, 70 };

    private static string JournalPath => Path.Combine(AppSettings.Dir, "eco-advanced-probe.json");

    private sealed class ProbeJournal
    {
        public string Firmware { get; set; } = "";
        public string Model { get; set; } = "";
        public byte ShiftMode { get; set; }
        public byte FanMode { get; set; }
        public byte SuperBatt { get; set; }
        public int[] CpuTemp { get; set; } = Array.Empty<int>();
        public int[] CpuSpeed { get; set; } = Array.Empty<int>();
        public int[] GpuTemp { get; set; } = Array.Empty<int>();
        public int[] GpuSpeed { get; set; } = Array.Empty<int>();
        public DateTime CreatedUtc { get; set; }
    }

    public static bool HasPendingJournal => File.Exists(JournalPath);

    private static void RequireExactTarget(DeviceProfile? dev, string firmware)
    {
        if (!string.Equals(firmware, ExactFirmware, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Probe is allowlisted only for exact firmware {ExactFirmware}; current firmware is '{firmware}'.");
        if (dev is not { Tier: Tier.Tested })
            throw new InvalidOperationException("Probe requires a Tested model contract.");
        if (!string.Equals(dev.MatchedPrefix(firmware), ExactFirmware, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Probe requires the exact 1583EMS1.110 runtime contract.");
        if (!dev.Name.Contains("Crosshair 15 B12UEZ / B12UGSZ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Probe target model contract does not match the verified 1583EMS1 entry.");
        if (dev.ShiftMode != 0xD2 || dev.FanMode != 0xD4)
            throw new InvalidOperationException("Probe refuses an unexpected shift/fan register map.");
        if (dev.FanCurve is not { Points: 6 } fc ||
            fc.CpuTempBase != 0x69 || fc.CpuSpeedBase != 0x72 ||
            fc.GpuTempBase != 0x81 || fc.GpuSpeedBase != 0x8A ||
            fc.AdvancedModeValue != ExpectedAdvancedMode)
            throw new InvalidOperationException("Probe refuses an unexpected fan-curve register map.");
    }

    private static void RequireFactoryCurve((int[] cpuTemp, int[] cpuSpeed, int[] gpuTemp, int[] gpuSpeed) c)
    {
        if (!c.cpuTemp.SequenceEqual(ExpectedCpuTemp) ||
            !c.cpuSpeed.SequenceEqual(ExpectedCpuSpeed) ||
            !c.gpuTemp.SequenceEqual(ExpectedGpuTemp) ||
            !c.gpuSpeed.SequenceEqual(ExpectedGpuSpeed))
            throw new InvalidOperationException(
                "Probe requires the exact OEM stock curve observed in 06_EXTREME_AUTO_RESTORED / 07_ECO_RESTORED. " +
                "Current curve differs; restore stock fans in MSI Center and rerun.");
    }

    private static ProbeJournal CaptureAndValidate(DeviceProfile dev, string firmware)
    {
        return Ec.Exclusive(() =>
        {
            byte shift = Ec.ReadByte(dev.ShiftMode);
            byte fan = Ec.ReadByte(dev.FanMode);
            byte sb = Ec.ReadByte(SuperBattAddr);
            var curve = Ec.ReadFanCurve(dev) ?? throw new InvalidOperationException("Fan curve is unreadable.");

            if (shift != ExpectedEcoShift)
                throw new InvalidOperationException($"Probe requires OEM ECO shift 0x{ExpectedEcoShift:X2}; read 0x{shift:X2}.");
            if (fan != ExpectedAutoMode)
                throw new InvalidOperationException($"Probe requires fan Auto 0x{ExpectedAutoMode:X2}; read 0x{fan:X2}.");
            if ((sb & 0x0F) != ExpectedSuperBattLowNibble)
                throw new InvalidOperationException($"Probe requires ECO limiter low nibble 0x0F; read 0x{sb:X2}.");
            RequireFactoryCurve(curve);

            return new ProbeJournal
            {
                Firmware = firmware,
                Model = dev.Name,
                ShiftMode = shift,
                FanMode = fan,
                SuperBatt = sb,
                CpuTemp = (int[])curve.cpuTemp.Clone(),
                CpuSpeed = (int[])curve.cpuSpeed.Clone(),
                GpuTemp = (int[])curve.gpuTemp.Clone(),
                GpuSpeed = (int[])curve.gpuSpeed.Clone(),
                CreatedUtc = DateTime.UtcNow,
            };
        });
    }

    private static void PersistJournal(ProbeJournal j)
    {
        string json = JsonSerializer.Serialize(j, new JsonSerializerOptions { WriteIndented = true });
        SafeFile.WriteAllTextAtomic(JournalPath, json);
    }

    private static ProbeJournal LoadJournal()
    {
        string json = File.ReadAllText(JournalPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<ProbeJournal>(json)
            ?? throw new InvalidOperationException("Probe recovery journal is invalid.");
    }

    private static void DeleteJournalOrThrow()
    {
        if (File.Exists(JournalPath)) File.Delete(JournalPath);
        if (File.Exists(JournalPath))
            throw new IOException("Probe recovery journal could not be removed after verified restore.");
    }

    private static void VerifyCurveEqualsJournal(DeviceProfile dev, ProbeJournal j)
    {
        var c = Ec.ReadFanCurve(dev) ?? throw new IOException("Fan curve became unreadable.");
        if (!c.cpuTemp.SequenceEqual(j.CpuTemp) ||
            !c.cpuSpeed.SequenceEqual(j.CpuSpeed) ||
            !c.gpuTemp.SequenceEqual(j.GpuTemp) ||
            !c.gpuSpeed.SequenceEqual(j.GpuSpeed))
            throw new IOException("Fan tables changed during the probe; refusing to clear the recovery journal.");
    }

    private static void VerifyEcoBytes(DeviceProfile dev, ProbeJournal j, byte expectedFanMode)
    {
        byte shift = Ec.ReadByte(dev.ShiftMode);
        byte fan = Ec.ReadByte(dev.FanMode);
        byte sb = Ec.ReadByte(SuperBattAddr);
        if (shift != j.ShiftMode)
            throw new IOException($"ECO shift drifted during probe: expected 0x{j.ShiftMode:X2}, read 0x{shift:X2}.");
        if ((sb & 0x0F) != (j.SuperBatt & 0x0F))
            throw new IOException($"ECO limiter drifted during probe: expected low nibble 0x{(j.SuperBatt & 0x0F):X1}, read 0x{(sb & 0x0F):X1}.");
        if (fan != expectedFanMode)
            throw new IOException($"Fan mode verify failed: expected 0x{expectedFanMode:X2}, read 0x{fan:X2}.");
    }

    private static void RestoreFromJournal(DeviceProfile dev, ProbeJournal j)
    {
        Ec.Exclusive(() =>
        {
            byte currentFan = Ec.ReadByte(dev.FanMode);
            if (currentFan != j.FanMode && currentFan != ExpectedAdvancedMode)
                throw new IOException($"Recovery refuses unexpected fan mode 0x{currentFan:X2}.");

            // Never overwrite D2 or EB in this probe. If either changed externally, fail closed.
            byte shift = Ec.ReadByte(dev.ShiftMode);
            byte sb = Ec.ReadByte(SuperBattAddr);
            if (shift != j.ShiftMode || (sb & 0x0F) != (j.SuperBatt & 0x0F))
                throw new IOException("Recovery refuses because the OEM ECO state changed externally.");

            VerifyCurveEqualsJournal(dev, j);

            if (currentFan != j.FanMode)
                Ec.SetFanMode(dev, j.FanMode);

            VerifyEcoBytes(dev, j, j.FanMode);
            VerifyCurveEqualsJournal(dev, j);
            return 0;
        });
    }

    public static int RecoverPending()
    {
        try
        {
            if (!File.Exists(JournalPath))
            {
                Console.WriteLine("eco probe recovery: no pending journal");
                return 0;
            }

            var j = LoadJournal();
            string fw = Ec.ReadFirmware();
            var dev = Devices.Detect(fw);
            RequireExactTarget(dev, fw);
            if (!string.Equals(j.Firmware, fw, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recovery journal firmware does not match current firmware.");

            RestoreFromJournal(dev!, j);
            DeleteJournalOrThrow();
            Console.WriteLine("eco probe recovery: PASS - original fan Auto mode restored and verified");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("eco probe recovery: FAIL - " + ex.Message);
            return 1;
        }
    }

    public static int Run(string? outputPath)
    {
        if (!SafetyEdition.HardwareWritesAuthorized)
        {
            Console.WriteLine("Physical probing is locked pending verification of the exact model, BIOS and EC contract.");
            return 1;
        }
        string outPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Environment.CurrentDirectory, $"ECO_ADVANCED_PROBE_{DateTime.Now:yyyyMMdd_HHmmss}.txt")
            : Path.GetFullPath(outputPath);

        var log = new StringBuilder();
        log.AppendLine("=== GhostDeck EcoCool controlled ECO+Advanced probe ===");
        log.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"Safety version: {SafetyEdition.Version}");
        log.AppendLine("Mutation scope: ONLY fan-mode byte D4, temporary 0D -> 8D -> 0D.");
        log.AppendLine("Fan tables, D2 and EB are read/verified but never written by this probe.");
        log.AppendLine();

        bool journalWritten = false;
        bool activeVerified = false;
        bool restoreVerified = false;
        Exception? failure = null;

        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("GhostDeckEcoCool")
                .Any(p => p.Id != Environment.ProcessId))
                throw new InvalidOperationException("Close the normal GhostDeck EcoCool app before running the hardware probe.");

            if (File.Exists(JournalPath))
                throw new InvalidOperationException(
                    $"A previous probe journal exists at {JournalPath}. Run --recover-eco-probe first.");

            string fw = Ec.ReadFirmware();
            var dev = Devices.Detect(fw);
            RequireExactTarget(dev, fw);

            var j = CaptureAndValidate(dev!, fw);
            log.AppendLine($"Firmware: {fw}");
            log.AppendLine($"Model contract: {dev!.Name}");
            log.AppendLine($"BEFORE: D2=0x{j.ShiftMode:X2} D4=0x{j.FanMode:X2} EB=0x{j.SuperBatt:X2}");
            log.AppendLine($"CPU curve: T=[{string.Join(",", j.CpuTemp)}] S=[{string.Join(",", j.CpuSpeed)}]");
            log.AppendLine($"GPU curve: T=[{string.Join(",", j.GpuTemp)}] S=[{string.Join(",", j.GpuSpeed)}]");

            PersistJournal(j);   // durable before the first write
            journalWritten = true;

            Ec.Exclusive(() =>
            {
                // Revalidate under the exclusive lock immediately before mutation.
                VerifyEcoBytes(dev, j, j.FanMode);
                VerifyCurveEqualsJournal(dev, j);

                Ec.SetFanMode(dev, ExpectedAdvancedMode);   // the ONLY forward mutation
                VerifyEcoBytes(dev, j, ExpectedAdvancedMode);
                VerifyCurveEqualsJournal(dev, j);
                activeVerified = true;

                log.AppendLine($"ACTIVE: D2=0x{Ec.ReadByte(dev.ShiftMode):X2} D4=0x{Ec.ReadByte(dev.FanMode):X2} EB=0x{Ec.ReadByte(SuperBattAddr):X2}");

                for (int i = 0; i < 6; i++)
                {
                    if (Ec.TryReadHw(dev, out var hw))
                        log.AppendLine($"sample[{i+1}]: CPU={hw.CpuTemp}C GPU={hw.GpuTemp}C CPU_RPM={hw.CpuRpm} GPU_RPM={hw.GpuRpm} CPU_DUTY={hw.CpuFan} GPU_DUTY={hw.GpuFan}");
                    else
                        log.AppendLine($"sample[{i+1}]: READ_MISSING");
                    if (i < 5) Thread.Sleep(HoldMilliseconds / 5);
                }

                // The combination must survive the whole observation window, not just the
                // immediate write/readback. MSI Center or firmware resetting D4 is a FAIL.
                VerifyEcoBytes(dev, j, ExpectedAdvancedMode);
                VerifyCurveEqualsJournal(dev, j);
                log.AppendLine("ACTIVE_WINDOW_END: protected ECO bytes and full fan tables still verified.");
                return 0;
            });
        }
        catch (Exception ex)
        {
            failure = ex;
            log.AppendLine("PROBE_ERROR: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (journalWritten)
            {
                try
                {
                    var j = LoadJournal();
                    string fw = Ec.ReadFirmware();
                    var dev = Devices.Detect(fw);
                    RequireExactTarget(dev, fw);
                    RestoreFromJournal(dev!, j);
                    restoreVerified = true;
                    DeleteJournalOrThrow();
                    log.AppendLine($"RESTORED: D2=0x{Ec.ReadByte(dev!.ShiftMode):X2} D4=0x{Ec.ReadByte(dev.FanMode):X2} EB=0x{Ec.ReadByte(SuperBattAddr):X2}");
                }
                catch (Exception restoreEx)
                {
                    log.AppendLine("RESTORE_ERROR: " + restoreEx.GetType().Name + ": " + restoreEx.Message);
                    if (failure == null) failure = restoreEx;
                }
            }

            bool pass = failure == null && activeVerified && restoreVerified;
            log.AppendLine();
            log.AppendLine("RESULT: " + (pass ? "PASS" : "FAIL"));
            log.AppendLine($"AdvancedVerified={activeVerified}");
            log.AppendLine($"RestoreVerified={restoreVerified}");
            log.AppendLine($"RecoveryJournalRemaining={File.Exists(JournalPath)}");
            try { SafeFile.WriteAllTextAtomic(outPath, log.ToString()); }
            catch (Exception saveEx) { Console.WriteLine("probe report save failed: " + saveEx.Message); }
            Console.WriteLine((pass ? "eco advanced probe: PASS" : "eco advanced probe: FAIL"));
            Console.WriteLine("report: " + outPath);
            if (File.Exists(JournalPath))
                Console.WriteLine("WARNING: recovery journal remains. Run GhostDeckEcoCool.exe --recover-eco-probe");
        }

        return failure == null && activeVerified && restoreVerified ? 0 : 1;
    }
}

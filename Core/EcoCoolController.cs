namespace GhostDeck;

/// <summary>
/// High-level cooling policy with two explicitly separated hardware contracts.
///
/// 1583EMS1.110 (OEM-ECO V1):
///   Preserve the vendor ECO/SuperBattery recipe (D2=C2, EB low nibble=0F), engage Advanced
///   fan mode (D4=8D), and own only the verified six-point CPU/GPU fan tables. Windows power
///   mode and CPU boost are intentionally untouched.
///
/// 1584EMS1 legacy policy:
///   Preserves the previously shipped Safety-edition behavior: Balanced EC + Windows Best
///   Efficiency + CPU boost disabled + verified fan curve.
///
/// Every mutable value needed for exact rollback is journaled before the first mutation.
/// </summary>
public static class EcoCoolController
{
    public const string ExactOemEcoFirmware = "1583EMS1.110";
    public const int OemEcoPolicySchema = 2;
    private const string OemEcoPolicy = "OemEco1583V1";
    private const string Legacy1584Policy = "Legacy1584V1";

    private static bool IsOemEcoContract(DeviceProfile? dev, string firmware) =>
        dev is { Tier: Tier.Tested, FanCurve: { Verified: true } fc } &&
        string.Equals(firmware, ExactOemEcoFirmware, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(dev.MatchedPrefix(firmware), ExactOemEcoFirmware, StringComparison.OrdinalIgnoreCase) &&
        fc.FactoryCpuTemp != null && fc.FactoryCpuSpeed != null &&
        fc.FactoryGpuTemp != null && fc.FactoryGpuSpeed != null;

    private static bool IsLegacy1584Contract(DeviceProfile? dev, string firmware) =>
        dev is { Tier: Tier.Tested, FanCurve: { Verified: true } fc } &&
        string.Equals(dev.MatchedPrefix(firmware), "1584EMS1", StringComparison.OrdinalIgnoreCase) &&
        dev.Name.Contains("Sword 15 A12UC", StringComparison.OrdinalIgnoreCase) &&
        fc.FactoryCpuTemp != null && fc.FactoryCpuSpeed != null &&
        fc.FactoryGpuTemp != null && fc.FactoryGpuSpeed != null;

    public static bool CanQualifyOemEco(DeviceProfile? dev, string firmware) =>
        IsOemEcoContract(dev, firmware);

    public static bool IsOemEcoQualified(AppSettings s, string firmware) =>
        string.Equals(firmware, ExactOemEcoFirmware, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(s.EcoCoolQualifiedFirmware, ExactOemEcoFirmware, StringComparison.OrdinalIgnoreCase) &&
        s.EcoCoolQualifiedPolicySchema == OemEcoPolicySchema;

    /// <summary>
    /// User-facing availability. Exact 1583.110 must first pass the controlled qualification cycle.
    /// The legacy 1584 policy keeps its previous availability.
    /// </summary>
    public static bool Available(AppSettings s, DeviceProfile? dev, string firmware) =>
        IsLegacy1584Contract(dev, firmware) ||
        (IsOemEcoContract(dev, firmware) && IsOemEcoQualified(s, firmware));

    private static string PolicyFor(DeviceProfile dev, string firmware)
    {
        if (IsOemEcoContract(dev, firmware)) return OemEcoPolicy;
        if (IsLegacy1584Contract(dev, firmware)) return Legacy1584Policy;
        throw new InvalidOperationException("No verified EcoCool hardware contract matches this firmware.");
    }

    private static string JournalPolicy(AppSettings s, DeviceProfile dev, string firmware)
    {
        string actual = PolicyFor(dev, firmware);
        if (s.EcoCoolPolicy.Length == 0)
        {
            // Compatibility only for a pre-1.38.5 1584 journal. 1583 was not persistently enabled
            // before 1.38.5, so an empty 1583 policy is never inferred.
            if (actual == Legacy1584Policy) return actual;
            throw new InvalidOperationException("EcoCool journal has no policy identity.");
        }
        if (!string.Equals(s.EcoCoolPolicy, actual, StringComparison.Ordinal))
            throw new InvalidOperationException("EcoCool journal policy does not match the detected hardware contract.");
        return actual;
    }

    private static void RequireHardwareContract(DeviceProfile dev, string firmware) =>
        _ = PolicyFor(dev, firmware);

    private static void RequireUserAvailable(AppSettings s, DeviceProfile dev, string firmware)
    {
        if (!Available(s, dev, firmware))
        {
            if (IsOemEcoContract(dev, firmware))
                throw new InvalidOperationException(
                    "1583EMS1.110 is verified but not yet qualified for persistent ECO COOL. Run --qualify-ecocool first.");
            throw new InvalidOperationException("EcoCool Safety is not available for this hardware contract.");
        }
    }

    private static ProfileId TargetProfile(string policy) =>
        policy == OemEcoPolicy ? ProfileId.SuperBattery : ProfileId.Balanced;

    private static bool OwnsWindowsPower(string policy) => policy == Legacy1584Policy;

    private static (int[] addrs, int[] vals) SnapshotRecipeAddresses(DeviceProfile dev, ProfileId target)
    {
        var addrs = dev.Recipes[target].Select(x => x.addr).Distinct().ToArray();
        var vals = Ec.ReadMany(addrs);
        return (addrs.Select(x => (int)x).ToArray(), vals.Select(x => (int)x).ToArray());
    }

    private static (byte addr, byte val)[] SavedRecipe(AppSettings s)
    {
        if (s.EcoCoolPreviousRecipeAddrs.Length == 0 ||
            s.EcoCoolPreviousRecipeAddrs.Length != s.EcoCoolPreviousRecipeValues.Length)
            throw new InvalidOperationException("EcoCool restore snapshot has no valid EC recipe state.");
        var rows = new (byte addr, byte val)[s.EcoCoolPreviousRecipeAddrs.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            int a = s.EcoCoolPreviousRecipeAddrs[i], v = s.EcoCoolPreviousRecipeValues[i];
            if (a is < 0 or > 255 || v is < 0 or > 255)
                throw new InvalidOperationException("EcoCool restore snapshot contains an invalid EC byte.");
            rows[i] = ((byte)a, (byte)v);
        }
        return rows;
    }

    private static (int[] ct, int[] cs, int[] gt, int[] gs) DefaultCurve(DeviceProfile dev, string policy)
    {
        var fc = dev.FanCurve ?? throw new InvalidOperationException("No verified fan-curve contract.");
        int[] ct = (int[])fc.FactoryCpuTemp!.Clone();
        int[] gt = (int[])fc.FactoryGpuTemp!.Clone();
        int[] cs;
        int[] gs;

        if (policy == OemEcoPolicy)
        {
            // 1.39 Focus: the verified 1583EMS1.110 sweep proved that the EC's 0..150 command
            // range remains effective well above 100. Keep the two low-temperature points quiet,
            // then use the full validated range as the machine gets hot. These are EC orders,
            // not a claim that the motor is physically running at "150 percent".
            cs = new[] { 38, 43, 55, 80, 110, 150 };
            gs = new[] { 0, 43, 55, 80, 110, 150 };
        }
        else
        {
            cs = CurveModel.Shift(fc.FactoryCpuSpeed!, +10);
            gs = CurveModel.Shift(fc.FactoryGpuSpeed!, +10);
        }

        FanCurveSafety.ValidateOrThrow(fc, ct, cs, gt, gs);
        return (ct, cs, gt, gs);
    }

    private static (int[] ct, int[] cs, int[] gt, int[] gs) ActiveCurve(AppSettings s, DeviceProfile dev, string policy)
    {
        var fc = dev.FanCurve ?? throw new InvalidOperationException("No verified fan-curve contract.");
        if (s.EcoCoolCurrentCpuTemp.Length == fc.Points && s.EcoCoolCurrentCpuSpeed.Length == fc.Points &&
            s.EcoCoolCurrentGpuTemp.Length == fc.Points && s.EcoCoolCurrentGpuSpeed.Length == fc.Points)
        {
            FanCurveSafety.ValidateOrThrow(fc, s.EcoCoolCurrentCpuTemp, s.EcoCoolCurrentCpuSpeed,
                                           s.EcoCoolCurrentGpuTemp, s.EcoCoolCurrentGpuSpeed);
            return ((int[])s.EcoCoolCurrentCpuTemp.Clone(), (int[])s.EcoCoolCurrentCpuSpeed.Clone(),
                    (int[])s.EcoCoolCurrentGpuTemp.Clone(), (int[])s.EcoCoolCurrentGpuSpeed.Clone());
        }
        return DefaultCurve(dev, policy);
    }

    public static void Apply(AppSettings s, DeviceProfile dev, string firmware) =>
        ApplyCore(s, dev, firmware, allowUnqualifiedOem: false);

    internal static void ApplyForQualification(AppSettings s, DeviceProfile dev, string firmware) =>
        ApplyCore(s, dev, firmware, allowUnqualifiedOem: true);

    private static void ApplyCore(AppSettings s, DeviceProfile dev, string firmware, bool allowUnqualifiedOem)
    {
        if (!SafetyEdition.HardwareWritesAuthorized)
            throw new InvalidOperationException("Manual EC control is locked pending verification of the exact laptop model, BIOS and EC contract.");
        RequireHardwareContract(dev, firmware);
        string policy = PolicyFor(dev, firmware);
        if (!allowUnqualifiedOem) RequireUserAvailable(s, dev, firmware);
        else if (policy != OemEcoPolicy)
            throw new InvalidOperationException("Qualification bypass is valid only for exact 1583EMS1.110 OEM-ECO.");

        if (s.EcoCoolActive) return;
        if (s.EcoCoolPhase.Length > 0)
            throw new InvalidOperationException("An unfinished EcoCool transaction exists. Restore it before applying again.");
        if (!Ec.TryGetCurrent(dev, out var previous))
            throw new InvalidOperationException("EC profile state is UNKNOWN; EcoCool fails closed.");

        var curve = Ec.ReadFanCurve(dev) ?? throw new InvalidOperationException("Cannot snapshot the current fan curve.");
        FanCurveSafety.ValidateOrThrow(dev.FanCurve!, curve.cpuTemp, curve.cpuSpeed, curve.gpuTemp, curve.gpuSpeed);
        byte fanMode = Ec.ReadByte(dev.FanMode);
        EcTransaction.ValidateFanMode(dev, fanMode);
        ProfileId target = TargetProfile(policy);
        var recipeState = SnapshotRecipeAddresses(dev, target);
        var snapshotRows = recipeState.addrs.Select((addr, i) =>
            (addr: (byte)addr, val: (byte)recipeState.vals[i])).ToArray();
        EcTransaction.ValidateRecipeSnapshot(dev, snapshotRows);
        if (snapshotRows.Any(r => r.addr == dev.FanMode && r.val != fanMode))
            throw new IOException("EC fan mode changed while capturing the recovery snapshot; apply blocked.");

        Guid pmAc = Guid.Empty, pmDc = Guid.Empty, plan = Guid.Empty;
        uint turboAc = 0, turboDc = 0;
        if (OwnsWindowsPower(policy))
        {
            if (!PowerPlan.TryGetUserPowerMode(out pmAc, out pmDc))
                throw new InvalidOperationException("Cannot snapshot Windows AC/DC power modes; EcoCool write blocked.");
            if (!PowerPlan.TryGetActiveScheme(out plan) || !PowerPlan.TryReadBoost(plan, out turboAc, out turboDc))
                throw new InvalidOperationException("Cannot snapshot the active Windows power plan / boost mode; EcoCool write blocked.");
        }

        s.EcoCoolFirmware = firmware;
        s.EcoCoolPolicy = policy;
        s.EcoCoolPreviousProfile = previous.ToString();
        s.EcoCoolPreviousRecipeAddrs = recipeState.addrs;
        s.EcoCoolPreviousRecipeValues = recipeState.vals;
        s.EcoCoolPreviousFanMode = fanMode;
        s.EcoCoolPreviousCpuTemp = (int[])curve.cpuTemp.Clone();
        s.EcoCoolPreviousCpuSpeed = (int[])curve.cpuSpeed.Clone();
        s.EcoCoolPreviousGpuTemp = (int[])curve.gpuTemp.Clone();
        s.EcoCoolPreviousGpuSpeed = (int[])curve.gpuSpeed.Clone();
        if (OwnsWindowsPower(policy))
        {
            s.EcoCoolPreviousPowerModeAc = pmAc.ToString("D");
            s.EcoCoolPreviousPowerModeDc = pmDc.ToString("D");
            s.EcoCoolPreviousPlan = plan.ToString("D");
            s.EcoCoolPreviousTurboAc = checked((int)turboAc);
            s.EcoCoolPreviousTurboDc = checked((int)turboDc);
        }
        else
        {
            s.EcoCoolPreviousPowerModeAc = s.EcoCoolPreviousPowerModeDc = s.EcoCoolPreviousPlan = "";
            s.EcoCoolPreviousTurboAc = s.EcoCoolPreviousTurboDc = -1;
        }

        s.EcoCoolPhase = "Prepared";
        s.SaveCritical();

        try
        {
            // OEM-ECO path writes the vendor's proven SuperBattery/ECO recipe first
            // (D2=C2, D4=0D, EB low nibble=0F), then the fan transaction writes all tables
            // while Auto is active and engages D4=8D only after successful readback.
            EcTransaction.ApplyRecipeVerified(dev, dev.Recipes[target]);

            var desired = DefaultCurve(dev, policy);
            int[] ct = desired.ct, cs = desired.cs, gt = desired.gt, gs = desired.gs;
            s.EcoCoolCurrentCpuTemp = (int[])ct.Clone();
            s.EcoCoolCurrentCpuSpeed = (int[])cs.Clone();
            s.EcoCoolCurrentGpuTemp = (int[])gt.Clone();
            s.EcoCoolCurrentGpuSpeed = (int[])gs.Clone();
            EcTransaction.ApplyFanCurveVerified(dev, ct, cs, gt, gs);

            if (OwnsWindowsPower(policy))
            {
                if (!PowerPlan.TrySetPowerMode(PowerPlan.ModeBestEfficiency))
                    throw new InvalidOperationException("Windows refused Best Efficiency power mode.");
                if (!PowerPlan.WriteBoost(plan, 0, 0))
                    throw new InvalidOperationException("Windows refused the CPU boost-off write.");
            }

            VerifyActiveOrThrow(dev, ct, cs, gt, gs, policy, plan);
            s.EcoCoolPhase = "Active";
            s.SaveCritical();
        }
        catch (Exception applyError)
        {
            try { Restore(s, dev, firmware); }
            catch (Exception rollbackEx)
            {
                AppLifecycle.Report(rollbackEx, "ecocool-rollback");
                throw new AggregateException("ECO COOL apply failed and recovery could not be verified. Recovery journal retained.",
                    applyError, rollbackEx);
            }
            throw;
        }
    }

    private static void VerifyTargetRecipe(DeviceProfile dev, ProfileId target)
    {
        var recipe = dev.Recipes[target];
        foreach (var (addr, val) in recipe)
        {
            byte expected = addr == dev.FanMode ? dev.FanCurve!.AdvancedModeValue : val;
            byte got = Ec.ReadByte(addr);
            bool ok = addr == 0xEB ? (got & 0x0F) == (expected & 0x0F) : got == expected;
            if (!ok)
                throw new IOException($"EcoCool verify failed at 0x{addr:X2}: expected 0x{expected:X2}, read 0x{got:X2}.");
        }
    }

    private static void VerifyActiveOrThrow(DeviceProfile dev, int[] ct, int[] cs, int[] gt, int[] gs,
                                            string policy, Guid plan)
    {
        ProfileId target = TargetProfile(policy);
        if (!Ec.TryGetCurrent(dev, out var profile) || profile != target)
            throw new IOException($"EcoCool verify failed: EC profile is not proven {target}.");

        VerifyTargetRecipe(dev, target);

        var got = Ec.ReadFanCurve(dev) ?? throw new IOException("EcoCool verify failed: fan curve unreadable.");
        if (!got.cpuTemp.SequenceEqual(ct) || !got.cpuSpeed.SequenceEqual(cs) ||
            !got.gpuTemp.SequenceEqual(gt) || !got.gpuSpeed.SequenceEqual(gs))
            throw new IOException("EcoCool verify failed: fan curve readback differs.");
        if (Ec.ReadByte(dev.FanMode) != dev.FanCurve!.AdvancedModeValue)
            throw new IOException("EcoCool verify failed: Advanced fan mode is not active.");

        if (OwnsWindowsPower(policy))
        {
            if (!PowerPlan.TryGetUserPowerMode(out var ac, out var dc) ||
                ac != PowerPlan.ModeBestEfficiency || dc != PowerPlan.ModeBestEfficiency)
                throw new IOException("EcoCool verify failed: Windows Best Efficiency was not read back.");
            if (!PowerPlan.TryReadBoost(plan, out uint tac, out uint tdc) || tac != 0 || tdc != 0)
                throw new IOException("EcoCool verify failed: CPU boost is not disabled on the snapshotted plan.");
        }
    }

    public static bool VerifyPersistedActive(AppSettings s, DeviceProfile dev, string firmware, out string error)
    {
        error = "";
        if (s.EcoCoolPhase != "Active") { error = "EcoCool is not marked Active."; return false; }
        try
        {
            RequireHardwareContract(dev, firmware);
            if (!string.Equals(s.EcoCoolFirmware, firmware, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Firmware differs from the EcoCool journal.");
            string policy = JournalPolicy(s, dev, firmware);
            Guid plan = Guid.Empty;
            if (OwnsWindowsPower(policy) && !Guid.TryParse(s.EcoCoolPreviousPlan, out plan))
                throw new InvalidOperationException("EcoCool journal has no valid power-plan GUID.");
            var desired = ActiveCurve(s, dev, policy);
            VerifyActiveOrThrow(dev, desired.ct, desired.cs, desired.gt, desired.gs, policy, plan);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    /// <summary>
    /// Change only the ECO COOL fan curve while preserving its policy and original rollback
    /// snapshot. Persistence failure rolls the active curve back to the previously committed one.
    /// </summary>
    public static void ApplyCustomCurve(AppSettings s, DeviceProfile dev, string firmware,
        int[] ct, int[] cs, int[] gt, int[] gs)
    {
        RequireHardwareContract(dev, firmware);
        if (s.EcoCoolPhase != "Active")
            throw new InvalidOperationException("ECO COOL is not Active; custom ECO COOL curve write blocked.");
        if (!string.Equals(s.EcoCoolFirmware, firmware, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Firmware differs from the ECO COOL journal.");
        string policy = JournalPolicy(s, dev, firmware);
        var fc = dev.FanCurve!;
        FanCurveSafety.ValidateOrThrow(fc, ct, cs, gt, gs);
        var previous = ActiveCurve(s, dev, policy);

        EcTransaction.ApplyFanCurveVerified(dev, ct, cs, gt, gs);
        try
        {
            VerifyTargetRecipe(dev, TargetProfile(policy));
            s.EcoCoolCurrentCpuTemp = (int[])ct.Clone();
            s.EcoCoolCurrentCpuSpeed = (int[])cs.Clone();
            s.EcoCoolCurrentGpuTemp = (int[])gt.Clone();
            s.EcoCoolCurrentGpuSpeed = (int[])gs.Clone();
            s.SaveCritical();
        }
        catch (Exception persistError)
        {
            try
            {
                EcTransaction.RestoreFanStateVerified(dev, fc.AdvancedModeValue,
                    previous.ct, previous.cs, previous.gt, previous.gs);
                s.EcoCoolCurrentCpuTemp = previous.ct; s.EcoCoolCurrentCpuSpeed = previous.cs;
                s.EcoCoolCurrentGpuTemp = previous.gt; s.EcoCoolCurrentGpuSpeed = previous.gs;
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("ECO COOL curve changed but persistence failed, and curve rollback could not be verified.",
                                             persistError, rollbackError);
            }
            throw;
        }
    }

    public static void Restore(AppSettings s, DeviceProfile dev, string firmware)
    {
        if (s.EcoCoolPhase.Length == 0) return;
        RequireHardwareContract(dev, firmware);
        if (!string.Equals(s.EcoCoolFirmware, firmware, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Firmware changed since the EcoCool snapshot; automatic restore is blocked.");
        string policy = JournalPolicy(s, dev, firmware);

        // Validate the entire recovery record BEFORE the first mutation. A corrupt tail
        // must never leave a valid recipe restored with invalid or partial fan state.
        var recipe = SavedRecipe(s);
        var expectedAddresses = dev.Recipes[TargetProfile(policy)].Select(r => r.addr).ToHashSet();
        if (recipe.Length != expectedAddresses.Count ||
            !expectedAddresses.SetEquals(recipe.Select(r => r.addr)))
            throw new InvalidOperationException("EcoCool restore snapshot does not match the owned recipe addresses.");
        if (s.EcoCoolPreviousFanMode is < 0 or > 255)
            throw new InvalidOperationException("EcoCool restore snapshot has no valid fan mode.");
        EcTransaction.ValidateFanMode(dev, (byte)s.EcoCoolPreviousFanMode);
        EcTransaction.ValidateRecipeSnapshot(dev, recipe);
        if (recipe.Any(r => r.addr == dev.FanMode && r.val != s.EcoCoolPreviousFanMode))
            throw new InvalidOperationException("Recovery journal contains contradictory fan-mode snapshots.");
        FanCurveSafety.ValidateOrThrow(dev.FanCurve!, s.EcoCoolPreviousCpuTemp,
            s.EcoCoolPreviousCpuSpeed, s.EcoCoolPreviousGpuTemp, s.EcoCoolPreviousGpuSpeed);
        if (OwnsWindowsPower(policy) &&
            (!Guid.TryParse(s.EcoCoolPreviousPowerModeAc, out _) ||
             !Guid.TryParse(s.EcoCoolPreviousPowerModeDc, out _) ||
             !Guid.TryParse(s.EcoCoolPreviousPlan, out _) ||
             s.EcoCoolPreviousTurboAc < 0 || s.EcoCoolPreviousTurboDc < 0))
            throw new InvalidOperationException("EcoCool restore snapshot has invalid Windows power state.");

        EcTransaction.RestoreFanStateVerified(dev, (byte)s.EcoCoolPreviousFanMode,
            s.EcoCoolPreviousCpuTemp, s.EcoCoolPreviousCpuSpeed,
            s.EcoCoolPreviousGpuTemp, s.EcoCoolPreviousGpuSpeed);
        // A saved Advanced recipe must never activate crash-interrupted tables.
        // Restore complete tables under Auto first; only then restore the recipe.
        EcTransaction.RestoreRecipeVerified(dev, recipe);

        if (OwnsWindowsPower(policy))
        {
            if (!Guid.TryParse(s.EcoCoolPreviousPowerModeAc, out var pmAc) ||
                !Guid.TryParse(s.EcoCoolPreviousPowerModeDc, out var pmDc) ||
                !PowerPlan.TrySetPowerModes(pmAc, pmDc))
                throw new InvalidOperationException("Windows power-mode restore failed.");

            if (!Guid.TryParse(s.EcoCoolPreviousPlan, out var plan) ||
                s.EcoCoolPreviousTurboAc < 0 || s.EcoCoolPreviousTurboDc < 0 ||
                !PowerPlan.WriteBoost(plan, (uint)s.EcoCoolPreviousTurboAc, (uint)s.EcoCoolPreviousTurboDc))
                throw new InvalidOperationException("CPU boost restore failed.");
        }

        s.ClearEcoCoolState();
    }

    /// <summary>A Prepared journal means the process died before commit. Restore conservatively.</summary>
    public static bool RecoverIfNeeded(AppSettings s, DeviceProfile dev, string firmware, out string error)
    {
        error = "";
        if (s.EcoCoolPhase != "Prepared") return true;
        try { Restore(s, dev, firmware); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    internal static void MarkOemEcoQualified(AppSettings s, string firmware)
    {
        if (!string.Equals(firmware, ExactOemEcoFirmware, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Qualification can only be recorded for exact firmware 1583EMS1.110.");
        s.EcoCoolQualifiedFirmware = ExactOemEcoFirmware;
        s.EcoCoolQualifiedPolicySchema = OemEcoPolicySchema;
        s.SaveCritical();
    }
}

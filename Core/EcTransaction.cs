namespace GhostDeck;

/// <summary>
/// Verified, rollback-capable EC writes for profile recipes and complete fan curves.
/// No transaction accepts arbitrary addresses; every address comes from the detected model contract.
/// </summary>
public static class EcTransaction
{
    public static void ApplyRecipeVerified(DeviceProfile dev, IEnumerable<(byte addr, byte val)> recipe)
    {
        RequireManualAuthority();
        ApplyRecipeCore(dev, recipe);
    }

    // Recovery is deliberately separate from authorizing a new manual recipe.
    internal static void RestoreRecipeVerified(DeviceProfile dev, IEnumerable<(byte addr, byte val)> recipe) =>
        ApplyRecipeCore(dev, recipe);

    private static void ApplyRecipeCore(DeviceProfile dev, IEnumerable<(byte addr, byte val)> recipe)
    {
        RequireTested(dev);
        var rows = recipe.ToArray();
        if (rows.Length == 0) return;
        var allowed = dev.Recipes.Values.SelectMany(x => x).Select(x => x.addr).ToHashSet();
        if (rows.Any(r => !allowed.Contains(r.addr)))
            throw new InvalidOperationException("Recipe contains an address outside the device allowlist.");
        if (rows.Select(r => r.addr).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Recipe contains duplicate EC addresses.");

        Ec.Exclusive(() =>
        {
            var before = rows.Select(r => (r.addr, val: Ec.ReadByte(r.addr))).ToArray();
            try
            {
                Ec.Apply(rows);
                VerifyRecipe(rows, before);
                return 0;
            }
            catch (Exception writeError)
            {
                try
                {
                    Ec.Apply(before);
                    VerifyExactSnapshot(before);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("EC recipe write failed and rollback could not be verified.", writeError, rollbackError);
                }
                throw;
            }
        });
    }

    private static void VerifyRecipe((byte addr, byte val)[] rows, (byte addr, byte val)[] before)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            byte got = Ec.ReadByte(rows[i].addr);
            byte expected = rows[i].addr == 0xEB
                ? (byte)((before[i].val & 0xF0) | (rows[i].val & 0x0F))
                : rows[i].val;
            if (got != expected)
                throw new IOException($"EC verify failed at 0x{rows[i].addr:X2}: expected 0x{expected:X2}, read 0x{got:X2}.");
        }
    }

    private static void VerifyExactSnapshot((byte addr, byte val)[] snapshot)
    {
        foreach (var (addr, val) in snapshot)
        {
            byte got = Ec.ReadByte(addr);
            // 0xEB is contractually low-nibble owned; a high-nibble firmware flag is not ours.
            bool ok = addr == 0xEB ? (got & 0x0F) == (val & 0x0F) : got == val;
            if (!ok) throw new IOException($"EC rollback verify failed at 0x{addr:X2}.");
        }
    }

    public static void ApplyFanCurveVerified(DeviceProfile dev, int[] ct, int[] cs, int[] gt, int[] gs)
    {
        RequireManualAuthority();
        RequireTested(dev);
        if (dev.FanCurve is not { } fc || !fc.Verified)
            throw new InvalidOperationException("Fan-curve writes require a verified device curve contract.");
        FanCurveSafety.ValidateForApply(fc, ct, cs, gt, gs);

        Ec.Exclusive(() =>
        {
            byte oldMode = Ec.ReadByte(dev.FanMode);
            ValidateFanMode(dev, oldMode);
            var old = Ec.ReadFanCurve(dev) ?? throw new InvalidOperationException("Cannot snapshot current fan curve.");
            FanCurveSafety.ValidateOrThrow(fc, old.cpuTemp, old.cpuSpeed, old.gpuTemp, old.gpuSpeed);
            try
            {
                // Never let a half-written table control the fans. Hand control to firmware while
                // replacing all tables, then engage Advanced only after complete readback.
                byte safeMode = oldMode == dev.FanSilentValue ? dev.FanSilentValue : (byte)0x0D;
                Ec.SetFanMode(dev, safeMode);
                Ec.WriteFanCurve(dev, ct, cs, gt, gs);
                VerifyFanCurve(dev, fc, ct, cs, gt, gs);
                Ec.SetFanMode(dev, fc.AdvancedModeValue);
                byte mode = Ec.ReadByte(dev.FanMode);
                if (mode != fc.AdvancedModeValue)
                    throw new IOException($"Advanced fan-mode verify failed: expected 0x{fc.AdvancedModeValue:X2}, read 0x{mode:X2}.");
                return 0;
            }
            catch (Exception writeError)
            {
                try
                {
                    // Rollback also keeps partial tables inactive until the full old table is back.
                    Ec.SetFanMode(dev, (byte)0x0D);
                    Ec.WriteFanCurve(dev, old.cpuTemp, old.cpuSpeed, old.gpuTemp, old.gpuSpeed);
                    VerifyFanCurve(dev, fc, old.cpuTemp, old.cpuSpeed, old.gpuTemp, old.gpuSpeed);
                    Ec.SetFanMode(dev, oldMode);
                    if (Ec.ReadByte(dev.FanMode) != oldMode)
                        throw new IOException("Fan-mode rollback verification failed.");
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Fan-curve write failed and rollback could not be verified.", writeError, rollbackError);
                }
                throw;
            }
        });
    }

    private static void VerifyFanCurve(DeviceProfile dev, FanCurveSpec fc,
        int[] ct, int[] cs, int[] gt, int[] gs)
    {
        var got = Ec.ReadFanCurve(dev) ?? throw new IOException("Fan-curve readback unavailable.");
        bool ok = got.cpuTemp.SequenceEqual(ct) && got.cpuSpeed.SequenceEqual(cs) &&
                  (fc.SingleFan || (got.gpuTemp.SequenceEqual(gt) && got.gpuSpeed.SequenceEqual(gs)));
        if (!ok) throw new IOException("Fan-curve readback does not match the requested tables.");
    }

    public static void RestoreFanStateVerified(DeviceProfile dev, byte fanMode,
        int[] ct, int[] cs, int[] gt, int[] gs)
    {
        RequireTested(dev);
        if (dev.FanCurve is not { } fc || !fc.Verified)
            throw new InvalidOperationException("Fan-state restore requires a verified curve contract.");
        FanCurveSafety.ValidateOrThrow(fc, ct, cs, gt, gs);
        ValidateFanMode(dev, fanMode);
        Ec.Exclusive(() =>
        {
            // If restore is interrupted, stock/auto remains in charge instead of a partial curve.
            Ec.SetFanMode(dev, (byte)0x0D);
            Ec.WriteFanCurve(dev, ct, cs, gt, gs);
            VerifyFanCurve(dev, fc, ct, cs, gt, gs);
            Ec.SetFanMode(dev, fanMode);
            if (Ec.ReadByte(dev.FanMode) != fanMode)
                throw new IOException("Fan-mode restore verification failed.");
            return 0;
        });
    }

    private static void RequireTested(DeviceProfile dev)
    {
        if (dev.Tier != Tier.Tested)
            throw new InvalidOperationException("EC transactions require a Tested hardware contract.");
    }

    private static void RequireManualAuthority()
    {
        if (!SafetyEdition.HardwareWritesAuthorized)
            throw new InvalidOperationException("New EC writes are locked pending exact model, BIOS and EC contract verification.");
    }

    internal static void ValidateFanMode(DeviceProfile dev, byte mode)
    {
        bool known = dev.Recipes.Values.SelectMany(r => r)
            .Any(r => r.addr == dev.FanMode && r.val == mode) ||
            dev.FanCurve is { Verified: true } fc && mode == fc.AdvancedModeValue;
        if (!known) throw new InvalidOperationException("Fan mode is outside the verified device contract.");
    }

    internal static void ValidateRecipeSnapshot(DeviceProfile dev, IEnumerable<(byte addr, byte val)> rows)
    {
        foreach (var row in rows)
        {
            bool known = dev.Recipes.Values.SelectMany(r => r).Any(r => r.addr == row.addr &&
                (row.addr == 0xEB ? (r.val & 0x0F) == (row.val & 0x0F) : r.val == row.val));
            if (row.addr == dev.ShiftMode && dev.FourthMode is { } fourth && row.val == fourth.ShiftValue)
                known = true;
            if (row.addr == dev.FanMode && dev.FanCurve is { Verified: true } fc && row.val == fc.AdvancedModeValue)
                known = true;
            if (!known) throw new InvalidOperationException($"Recovery byte at 0x{row.addr:X2} is outside the verified recipe contract.");
        }
    }
}

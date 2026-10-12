namespace GhostDeck;

/// <summary>Decode only values present in the model contract; unknown bytes grant no authority.</summary>
public static class EcProfileState
{
    public static bool TryDecode(DeviceProfile dev, byte shift, byte fan, out ProfileId profile)
    {
        profile = default;
        if (shift == dev.ShiftTurboValue || dev.FourthMode is { } fm && shift == fm.ShiftValue)
        { profile = ProfileId.Extreme; return true; }
        if (shift == dev.ShiftEcoValue)
        { profile = ProfileId.SuperBattery; return true; }

        foreach (var id in new[] { ProfileId.Silent, ProfileId.Balanced })
        {
            if (!dev.Recipes.TryGetValue(id, out var recipe) ||
                !recipe.Any(r => r.addr == dev.ShiftMode && r.val == shift)) continue;
            if (fan == dev.FanSilentValue) { profile = ProfileId.Silent; return true; }
            if (dev.FanCurve is { Verified: true } fc && fan == fc.AdvancedModeValue)
            { profile = ProfileId.Balanced; return true; }
            if (dev.Recipes.TryGetValue(ProfileId.Balanced, out var balanced) &&
                balanced.Any(r => r.addr == dev.FanMode && r.val == fan))
            { profile = ProfileId.Balanced; return true; }
        }
        return false;
    }
}

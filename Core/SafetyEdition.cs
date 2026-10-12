namespace GhostDeck;

public static class SafetyEdition
{
    public const string Edition = "EcoCool Focus";
    public const string Version = "1.39.2";
    // Release gate: the observed Sword 15 A12UE / E1583IMS.110 identity is not
    // established by the historical Crosshair-labelled EC evidence. New manual
    // control stays locked until that exact model + BIOS + EC contract is reviewed.
    // Recovery of an existing journal remains a separate, validated path.
    public static bool HardwareWritesAuthorized => false;
    // Focus build: keep the proven EC/rollback core, but present only the user's
    // actual job-to-be-done (OEM ECO, ECO COOL, fan curve, diagnostics).
    public static bool FocusEditionEnabled => true;
    // A customized hardware controller must not silently replace itself with upstream GhostDeck.
    public static bool InAppUpdaterEnabled => false;
    // Keep the compiled allowlist canonical for this build. Remote model-data changes require a
    // reviewed rebuild rather than changing EC contracts under a running installation.
    public static bool RemoteModelDbEnabled => false;
    public static bool PowerTestEnabled => false;
}

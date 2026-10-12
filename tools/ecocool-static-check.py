#!/usr/bin/env python3
from pathlib import Path
import re, sys, json

ROOT = Path(__file__).resolve().parents[1]
checks = []

def must(desc, cond, detail=''):
    checks.append((desc, bool(cond), detail))

def txt(rel):
    return (ROOT / rel).read_text(encoding='utf-8')

safety = txt('Core/SafetyEdition.cs')
ec = txt('Core/Ec.cs')
ecot = txt('Core/EcoCoolController.cs')
txn = txt('Core/EcTransaction.cs')
tray = txt('TrayContext.cs')
cli = txt('Core/Cli.cs')
updater = txt('Core/Updater.cs')
modeldb = txt('Core/ModelDb.cs')
settings = txt('Core/AppSettings.cs')
devices = txt('Core/Devices.cs')
fanpage = txt('UI/FanCurvePage.cs')
ptest = txt('Core/PowerTest.cs')
autostart = txt('Core/Autostart.cs')
program = txt('Program.cs')
probe = txt('Core/EcoAdvancedProbe.cs')
qual = txt('Core/EcoCoolQualificationTest.cs')
lang = txt('Core/Lang.cs')
mainform = txt('UI/MainForm.cs')
maindeps = txt('UI/MainDeps.cs')
focus = txt('UI/FocusHomePage.cs')
sweep = txt('Core/FanSweep.cs')

must('Safety edition version is 1.39.2', 'Version = "1.39.2"' in safety)

global_json = txt('global.json')
build_ps1 = txt('BUILD_ECOCOOL.ps1')
csproj = txt('GhostDeck.csproj')
must('Build SDK pinned to 8.0.425', '"version": "8.0.425"' in global_json and "$requiredSdk = '8.0.425'" in build_ps1)
must('Build rejects SDK selection drift', 'SDK selection mismatch' in build_ps1 and 'dotnet --version' in build_ps1)
must('Restore RID aligned with publish RID', 'dotnet restore .\\GhostDeck.csproj -r win-x64' in build_ps1 and "'-r', 'win-x64'" in build_ps1)
must('Restore verifies win-x64 assets target', 'project.assets.json' in build_ps1 and "-match '/win-x64$'" in build_ps1 and 'Restore verification failed' in build_ps1)
must('Project package version is 1.39.2', '<Version>1.39.2</Version>' in csproj)
must('Build artifact labels Focus version', '1.39.2 EcoCool Focus' in build_ps1)

must('Focus edition gate enabled', 'FocusEditionEnabled => true' in safety)
must('Focus top-level navigation is limited to Start / Fan curve / Diagnostics',
     'SafetyEdition.FocusEditionEnabled' in mainform and
     '(MainTab.Scenarios, "subtab_start"' in mainform and
     '(MainTab.FanCurve,  "tab_fancurve"' in mainform and
     '(MainTab.Status,    "status_title"' in mainform and
     'MainTab.Settings,  "menu_settings"' in mainform)
must('Focus home exposes OEM ECO without exposing Silent as a normal mode',
     'class FocusHomePage' in focus and
     '"ECO ORIGINAL MSI"' in focus and
     'ProfileId.SuperBattery' in focus and
     '"SILENT (no es ECO MSI)"' in focus)
must('Focus closes the resident app instead of hiding to tray',
     'RequestFocusExit' in mainform and '_d.ExitApp();' in mainform and
     'public required Action ExitApp' in maindeps and 'ExitApp = ExitApp' in tray)
must('Focus hides update/report chrome',
     '_updatesBtn.Visible = false' in mainform and '_reportBtn.Visible = false' in mainform)
must('CLI version command exists', '--version' in cli and 'CliKind.Version' in cli and 'SafetyEdition.Version' in cli)

must('Fan sweep includes EC order 125 before the model ceiling',
     'DefaultSteps = { 30, 45, 60, 80, 100, 125 }' in sweep)
must('Fan sweep waits for mechanical RPM stabilization, not a fixed 6 second snapshot',
     'MaxHoldSeconds = 30' in sweep and 'StabilitySamples = 5' in sweep and
     'StableRpmFraction = 0.025' in sweep and 'IsStable(samples, res.HasTach, fc.SingleFan)' in sweep and
     'SettleSeconds' not in sweep)
must('Fan sweep report calls 0..150 an EC order, not physical motor percent',
     'order = EC/MSI fan command scale, not a physical percent' in sweep)
must('Focus fan editor uses exact model factory arrays instead of generic UI defaults',
     'FactoryCpuSpeed => _fc?.FactoryCpuSpeed' in fanpage and
     '_cpuT = (int[])FactoryCpuTemp.Clone(); _cpuS = (int[])FactoryCpuSpeed.Clone();' in fanpage and
     '_gpuT = (int[])FactoryGpuTemp.Clone(); _gpuS = (int[])FactoryGpuSpeed.Clone();' in fanpage)
must('Focus fan editor labels 0..150 as EC order rather than physical percent',
     'private static string OrderValue' in fanpage and
     'SafetyEdition.FocusEditionEnabled ? value.ToString() : value + "%"' in fanpage and
     'OrderAxisTitle => SafetyEdition.FocusEditionEnabled ? Lang.T("fc_col_set")' in fanpage)
must('Spanish sweep UI labels the command as EC order and explains stabilization',
     '"orden EC"' in lang and 'RPM dejan de cambiar de forma material' in lang and 'máx. 30 s' in lang)

must('Startup profile is definitely assigned before EC read',
     'ProfileId startupProfile = ProfileId.Balanced;' in tray and
     '_profileReadProven = false;' in tray and
     'Ec.TryGetCurrent(_device!, out startupProfile)' in tray and
     'out var startupProfile' not in tray)
must('Safety gates avoid compile-time unreachable-code folding',
     'const bool InAppUpdaterEnabled' not in safety and
     'const bool RemoteModelDbEnabled' not in safety and
     'const bool PowerTestEnabled' not in safety and
     '-p:TreatWarningsAsErrors=true' in build_ps1)

must('In-app updater hard-disabled', 'InAppUpdaterEnabled => false' in safety and 'if (!SafetyEdition.InAppUpdaterEnabled)' in updater)
must('Remote model DB hard-disabled', 'RemoteModelDbEnabled => false' in safety and 'if (!SafetyEdition.RemoteModelDbEnabled)' in modeldb)
must('Power write-test hard-disabled', 'PowerTestEnabled => false' in safety and 'if (!SafetyEdition.PowerTestEnabled)' in ptest)

must('UNKNOWN profile is explicit', 'TryGetCurrent' in ec and 'throw new InvalidOperationException("EC profile state is UNKNOWN' in ec)
must('No fake Balanced fallback in Ec.GetCurrent', not re.search(r'catch\s*\{\s*return\s+ProfileId\.Balanced', ec, re.S))
must('Tray writes limited to Tested contracts', 'private bool Writable => SafetyEdition.HardwareWritesAuthorized' in tray and '_device!.Tier == Tier.Tested' in tray)
must('One-shot CLI writes limited to Tested contracts', 'bool writable = SafetyEdition.HardwareWritesAuthorized && dev != null && dev.Tier == Tier.Tested;' in cli)
must('CLI no direct profile-recipe writes', 'Ec.Apply(dev.Recipes' not in cli)
must('CLI no direct fan-table writes', 'Ec.WriteFanCurve' not in cli)
must('UI fan writes require verified curve', 'private bool Editable => _fc is { Verified: true } && D.Writable()' in fanpage)
must('No 0x9D experiment in persistent paths', '0x9D' not in ecot and '0x9D' not in txn and '0x9D' not in tray)

must('Exact 1583.110 fan contract exists',
     'Crosshair1583_110CurveVerified' in devices and
     'private static readonly DeviceProfile Exact1583_110' in devices and
     'FirmwarePrefixes = new[] { "1583EMS1.110" }' in devices and
     'FanCurve = Crosshair1583_110CurveVerified' in devices)
must('Exact 1583.110 runtime overlay is exact-match only',
     'string.Equals(firmware, "1583EMS1.110", StringComparison.OrdinalIgnoreCase)' in devices and
     'return Exact1583_110;' in devices)
must('Exact 1583.110 factory arrays match observed stock',
     'FactoryCpuTemp: new[] { 0, 55, 62, 69, 76, 82 }' in devices and
     'FactoryCpuSpeed: new[] { 38, 43, 48, 54, 60, 70 }' in devices and
     'FactoryGpuTemp: new[] { 0, 55, 61, 67, 72, 77 }' in devices and
     'FactoryGpuSpeed: new[] { 0, 43, 48, 54, 60, 70 }' in devices)
must('Generic 1583 remains curve read-only',
     re.search(r'FirmwarePrefixes = new\[\] \{ "1583EMS1" \}.*?FanCurve = ModernCurve,', devices, re.S) is not None)
must('Signed upstream model assets remain canonical and untouched',
     'DataVersion = 20261010' in devices and
     'Exact1583_110' not in txt('data/models.json') and
     __import__('hashlib').sha256((ROOT/'data/models.json').read_bytes()).hexdigest() == 'f2356bf52c4628ec56d6b5c0e7ec5cff5abc3908427dd932ff7c468de98e8cf2' and
     __import__('hashlib').sha256((ROOT/'data/models.json.sig').read_bytes()).hexdigest() == '8e5fcd1655f60051da86ab56b0c8c7ed2f6dfa672f7f3da5fa1e7c7b365a4651')
must('Remote DB disabled so local exact overlay cannot be replaced at runtime',
     'RemoteModelDbEnabled => false' in safety and 'return Exact1583_110;' in devices)

must('OEM-ECO exact firmware allowlist', 'ExactOemEcoFirmware = "1583EMS1.110"' in ecot)
must('OEM-ECO policy targets SuperBattery', 'policy == OemEcoPolicy ? ProfileId.SuperBattery : ProfileId.Balanced' in ecot)
must('OEM-ECO UI requires hardware qualification',
     'IsOemEcoQualified(s, firmware)' in ecot and
     'Run --qualify-ecocool first.' in ecot)
must('UI can always restore an existing EcoCool journal even if qualification is unavailable',
     '(on && !EcoCoolController.Available(_settings, _device, _firmware))' in tray and
     '_settings.EcoCoolPhase.Length > 0 ||' in tray)
must('OEM-ECO leaves Windows power/boost unowned',
     'OwnsWindowsPower(string policy) => policy == Legacy1584Policy' in ecot and
     'if (OwnsWindowsPower(policy))' in ecot)
must('OEM-ECO default preserves low-speed points and uses verified 150 range when hot',
     'cs = new[] { 38, 43, 55, 80, 110, 150 };' in ecot and
     'gs = new[] { 0, 43, 55, 80, 110, 150 };' in ecot)
must('OEM-ECO recipe is applied before full fan transaction',
     ecot.find('EcTransaction.ApplyRecipeVerified(dev, dev.Recipes[target])') <
     ecot.find('EcTransaction.ApplyFanCurveVerified(dev, ct, cs, gt, gs)'))
must('OEM-ECO verifies recipe bytes including EB nibble',
     'VerifyTargetRecipe' in ecot and 'addr == 0xEB ? (got & 0x0F)' in ecot)
must('EcoCool policy identity is journaled',
     'public string EcoCoolPolicy' in settings and
     's.EcoCoolPolicy = policy;' in ecot and
     'JournalPolicy' in ecot)
must('EcoCool journal is durable before mutation',
     ecot.find('s.EcoCoolPhase = "Prepared"') <
     ecot.find('EcTransaction.ApplyRecipeVerified(dev, dev.Recipes[target])'))
must('OEM restore is EC-only while legacy 1584 restores Windows state',
     'if (OwnsWindowsPower(policy))' in ecot and
     ecot.find('if (OwnsWindowsPower(policy))', ecot.find('public static void Restore')) > 0)
must('EcoCool exact restore state clears only after restore work',
     ecot.find('s.ClearEcoCoolState()', ecot.find('public static void Restore')) >
     ecot.find('EcTransaction.RestoreFanStateVerified', ecot.find('public static void Restore')))
must('Atomic critical settings persistence present', 'SafeFile.WriteAllTextAtomic' in settings and 'SaveCritical()' in settings)
must('EcoCool uses isolated app-data directory', 'GhostDeckEcoCool' in settings)
must('Autostart task isolated', 'GhostDeck EcoCool Safety' in autostart)
must('Process/single-instance identity isolated', 'GhostDeckEcoCool_' in program and 'GhostDeckEcoCool_' in tray)
must('Fan transaction has rollback verification', 'rollback could not be verified' in txn and 'VerifyFanCurve' in txn)

must('Qualification command exists',
     '--qualify-ecocool' in cli and 'CliKind.EcoCoolQualify' in cli and
     'EcoCoolQualificationTest.Run(cmd.Arg)' in cli)
must('Qualification runs locally and skips unrelated travel writes',
     'EcoAdvancedProbe or CliKind.RecoverEcoProbe or CliKind.EcoCoolQualify' in cli)
must('Qualification requires exact OEM stock baseline',
     'RequireExactStockEco' in qual and
     'ProfileId.SuperBattery' in qual and 'D4=0D' in qual and
     'StockCpuSpeed = { 38, 43, 48, 54, 60, 70 }' in qual and
     'StockGpuSpeed = { 0, 43, 48, 54, 60, 70 }' in qual)
must('Qualification exercises the same EcoCool controller path',
     'EcoCoolController.ApplyForQualification(settings, dev, fw)' in qual and
     'EcoCoolController.VerifyPersistedActive(settings, dev, fw' in qual)
must('Qualification restores before recording PASS authority',
     qual.find('EcoCoolController.Restore(settings, dev, fw)') <
     qual.find('EcoCoolController.MarkOemEcoQualified(settings, fw)'))
must('Qualification marker has firmware + policy schema',
     'EcoCoolQualifiedFirmware' in settings and 'EcoCoolQualifiedPolicySchema' in settings and
     'OemEcoPolicySchema = 2' in ecot)
must('Qualification report records result and restore evidence',
     'RESULT: ' in qual and 'RestoreVerified=' in qual and 'QualificationRecorded=' in qual)

# Preserve the earlier D4-only evidence collector/recovery path.
must('1583 exact-firmware probe allowlist', 'ExactFirmware = "1583EMS1.110"' in probe and 'MatchedPrefix(firmware), ExactFirmware' in probe)
must('Probe forward mutation is D4 advanced only',
     probe.count('Ec.SetFanMode(dev, ExpectedAdvancedMode)') == 1 and
     'Ec.WriteFanCurve' not in probe and 'Ec.Apply(' not in probe and 'PowerPlan.' not in probe)
must('Probe has durable journal before mutation',
     probe.find('PersistJournal(j)') < probe.find('Ec.SetFanMode(dev, ExpectedAdvancedMode)') and
     'SafeFile.WriteAllTextAtomic(JournalPath' in probe)
must('Probe recovery command exists', '--recover-eco-probe' in cli and 'EcoAdvancedProbe.RecoverPending()' in cli)

# Direct full fan-table writes stay confined to the low-level transaction layer.
direct_curve = []
for p in (p for p in ROOT.rglob('*.cs') if not {'bin','obj','releases'}.intersection(p.relative_to(ROOT).parts)):
    rel = p.relative_to(ROOT).as_posix()
    if rel in {'Core/Ec.cs', 'Core/EcTransaction.cs'} or rel.startswith('Tests/'):
        continue
    t = p.read_text(encoding='utf-8', errors='replace')
    if 'Ec.WriteFanCurve' in t:
        direct_curve.append(rel)
must('No fan-table write bypasses EcTransaction', not direct_curve, ', '.join(direct_curve))

# Approximate source delimiter smoke test.
def strip_csharp(src):
    src = re.sub(r'@?"(?:""|\\.|[^"\\])*"', '""', src, flags=re.S)
    src = re.sub(r"'(?:\\.|[^'\\])'", "''", src)
    src = re.sub(r'/\*.*?\*/', '', src, flags=re.S)
    src = re.sub(r'//.*', '', src)
    return src

bad_balance=[]
for p in (p for p in ROOT.rglob('*.cs') if not {'bin','obj','releases'}.intersection(p.relative_to(ROOT).parts)):
    t=strip_csharp(p.read_text(encoding='utf-8', errors='replace'))
    for a,b,name in [('{','}','{}'),('(',')','()'),('[',']','[]')]:
        bal=0; ok=True
        for ch in t:
            if ch==a: bal+=1
            elif ch==b:
                bal-=1
                if bal<0: ok=False; break
        if not ok or bal!=0:
            bad_balance.append(f'{p.relative_to(ROOT)}:{name}:{bal}')
must('C# delimiter smoke check', not bad_balance, ', '.join(bad_balance[:20]))


# Lexical smoke test for ordinary C# string literals. A regular/interpolated string
# may not contain a physical newline; verbatim (@"...") and raw ("""...""") strings may.
def ordinary_string_newline_issues(src, rel):
    issues=[]
    i=0; line=1; n=len(src); state='code'; raw_q=0
    while i<n:
        c=src[i]
        if state=='code':
            if c=='\n': line+=1; i+=1; continue
            if src.startswith('//',i): state='line_comment'; i+=2; continue
            if src.startswith('/*',i): state='block_comment'; i+=2; continue
            # raw string: optional $ prefix followed by 3+ quotes
            j=i
            while j<n and src[j]=='$': j+=1
            if j<n and src[j]=='"':
                k=j
                while k<n and src[k]=='"': k+=1
                if k-j>=3:
                    state='raw'; raw_q=k-j; i=k; continue
            # verbatim string can be @" or $@" or @$"
            if src.startswith('@"',i) or src.startswith('$@"',i) or src.startswith('@$"',i):
                state='verbatim'; i += 2 if src.startswith('@"',i) else 3; continue
            if c=='"' or (c=='$' and i+1<n and src[i+1]=='"'):
                state='normal'; start_line=line; i += 2 if c=='$' else 1; continue
            if c=="'": state='char'; i+=1; continue
            i+=1; continue
        if state=='line_comment':
            if c=='\n': state='code'; line+=1
            i+=1; continue
        if state=='block_comment':
            if src.startswith('*/',i): state='code'; i+=2; continue
            if c=='\n': line+=1
            i+=1; continue
        if state=='char':
            if c=='\\': i+=2; continue
            if c=="'": state='code'; i+=1; continue
            if c=='\n': issues.append(f'{rel}:{line}:newline in char literal'); state='code'; line+=1
            i+=1; continue
        if state=='normal':
            if c=='\\': i+=2; continue
            if c=='"': state='code'; i+=1; continue
            if c=='\n':
                issues.append(f'{rel}:{start_line}:physical newline in regular string literal')
                state='code'; line+=1; i+=1; continue
            i+=1; continue
        if state=='verbatim':
            if c=='\n': line+=1; i+=1; continue
            if c=='"':
                if i+1<n and src[i+1]=='"': i+=2; continue
                state='code'; i+=1; continue
            i+=1; continue
        if state=='raw':
            if c=='\n': line+=1; i+=1; continue
            if c=='"':
                k=i
                while k<n and src[k]=='"': k+=1
                if k-i>=raw_q: state='code'; i=k; continue
            i+=1; continue
    if state=='normal': issues.append(f'{rel}:{start_line}:unterminated regular string literal at EOF')
    return issues

string_issues=[]
for p in (p for p in ROOT.rglob('*.cs') if not {'bin','obj','releases'}.intersection(p.relative_to(ROOT).parts)):
    rel=p.relative_to(ROOT).as_posix()
    string_issues.extend(ordinary_string_newline_issues(p.read_text(encoding='utf-8', errors='replace'), rel))
must('C# regular-string newline smoke check', not string_issues, ', '.join(string_issues[:20]))


must('Manual hardware control fails closed pending exact model BIOS EC authority',
     'HardwareWritesAuthorized => false' in safety and 'if (!SafetyEdition.HardwareWritesAuthorized)' in ecot and
     'if (!SafetyEdition.HardwareWritesAuthorized)' in qual and 'if (!SafetyEdition.HardwareWritesAuthorized)' in probe)
must('Preview cannot enable real UI writers', '!_simulate && !(_main?.FanSweepRunning' in tray)
must('Unknown profile bytes require contract decoding', 'EcProfileState.TryDecode' in ec)
must('New curves retain the verified factory cooling envelope',
     'FanCurveSafety.ValidateForApply' in txn and 't[i] > stockT[i] || s[i] < stockS[i]' in txt('Core/FanCurveSafety.cs'))
must('Restoration validates fan state before any recipe write',
     ecot.find('FanCurveSafety.ValidateOrThrow(dev.FanCurve!, s.EcoCoolPreviousCpuTemp') < ecot.find('EcTransaction.RestoreRecipeVerified(dev, recipe)'))
must('Critical clear commits before dropping the in-memory journal',
     'cleared.SaveCritical();' in settings and 'var cleared = (AppSettings)MemberwiseClone();' in settings)
must('Status CLI cannot expire travel or write battery state', 'CliKind.Status or CliKind.Diag' in cli)
must('Lost CLI replies never trigger duplicate one-shot writes', 'if (!connected) return false;' in cli and 'no one-shot retry' in cli)
must('CLI response waits are bounded', 'ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))' in cli)
must('Calibration snapshots real EC and records restore outcome', 'RunRestoring' in sweep and 'RestoreVerified=' in sweep and 'FanSweep.RunRestoring' in fanpage)
must('Focus cannot terminate while calibration is restoring', 'FanSweepRunning: true' in tray and '_main.CancelFanSweep();' in tray)

must('Dependencies are locked and audit includes transitive packages',
     '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>' in csproj and
     '<NuGetAuditMode>all</NuGetAuditMode>' in csproj and '--locked-mode' in build_ps1)
must('SDK roll-forward is disabled', '"rollForward": "disable"' in global_json)
must('Failed builds preserve the prior distribution',
     '$targetDist' in build_ps1 and 'previous-dist.zip' in build_ps1 and 'Staging cleanup path mismatch' in build_ps1)
must('Test transport is excluded from production assembly', '<Compile Remove="Tests/**/*.cs" />' in csproj)
failed = [x for x in checks if not x[1]]
for desc, ok, detail in checks:
    line = f"{'PASS' if ok else 'FAIL'}  {desc}"
    if detail and not ok: line += f" -> {detail}"
    print(line)
print(f'\n{len(checks)-len(failed)}/{len(checks)} checks PASS')
if failed:
    sys.exit(1)

from pathlib import Path
root=Path(__file__).resolve().parents[1]
s=(root/'Core/AppSettings.cs').read_text(encoding='utf-8')
start=s.index('    public string EcoCoolPhase ')
end=s.index('    /// <summary>Remember the curve',start)
p=root/'Tests/obj/generated/SettingsState.cs';p.parent.mkdir(parents=True,exist_ok=True)
p.write_text('// Generated from canonical AppSettings fields and ClearEcoCoolState; never edit.\nnamespace GhostDeck;\npublic partial class AppSettings {\n'+s[start:end]+'\n}\n',encoding='utf-8')

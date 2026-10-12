using GhostDeck;
internal static class ControllerChecks
{
 public static void Run(DeviceProfile d,int[] t,int[] s,int[] gt,int[] gs,Action<bool,string> check,Action<Action,string> throws,Action seed)
 {
  string fw=EcoCoolController.ExactOemEcoFirmware;
  AppSettings Journal() => new() { EcoCoolPhase="Prepared",EcoCoolFirmware=fw,EcoCoolPolicy="OemEco1583V1",EcoCoolPreviousRecipeAddrs=new[]{0xD2,0xD4,0xEB},EcoCoolPreviousRecipeValues=new[]{0xC1,0x0D,0xA0},EcoCoolPreviousFanMode=0x0D,EcoCoolPreviousCpuTemp=(int[])t.Clone(),EcoCoolPreviousCpuSpeed=(int[])s.Clone(),EcoCoolPreviousGpuTemp=(int[])gt.Clone(),EcoCoolPreviousGpuSpeed=(int[])gs.Clone() };
  foreach(string kind in new[]{"mode","unsupported-mode","recipe-shift","recipe-fan","contradictory-mode","table","addresses","firmware","policy"}) {
   seed();var j=Journal();
   switch(kind) {case "mode":j.EcoCoolPreviousFanMode=-1;break;case "unsupported-mode":j.EcoCoolPreviousFanMode=0xFF;break;case "recipe-shift":j.EcoCoolPreviousRecipeValues[0]=0xFF;break;case "recipe-fan":j.EcoCoolPreviousRecipeValues[1]=0xFF;break;case "contradictory-mode":j.EcoCoolPreviousRecipeValues[1]=0x8D;break;case "table":j.EcoCoolPreviousCpuSpeed=new int[5];break;case "addresses":j.EcoCoolPreviousRecipeAddrs=new[]{0xD2,0xD4,0x34};break;case "firmware":j.EcoCoolFirmware="other";break;case "policy":j.EcoCoolPolicy="other";break;}
   throws(()=>EcoCoolController.Restore(j,d,fw),"invalid recovery "+kind+" rejected");check(Ec.Writes==0,"invalid recovery "+kind+" performs zero writes");
  }
  seed();var interrupted=Journal();interrupted.EcoCoolPreviousFanMode=0x8D;interrupted.EcoCoolPreviousRecipeValues[1]=0x8D;
  Ec.Bytes[d.FanCurve!.CpuSpeedBase]=0;Ec.WriteTrace.Clear();
  EcoCoolController.Restore(interrupted,d,fw);
  int advancedAt=Ec.WriteTrace.FindIndex(r=>r.addr==d.FanMode&&r.val==0x8D);
  check(Ec.WriteTrace[0]==(d.FanMode,(byte)0x0D)&&advancedAt>=25,"crash recovery completes tables under Auto before restoring Advanced recipe");
  seed();var state=Journal();state.OnSave=_=>throw new IOException("injected journal clear failure");
  throws(()=>EcoCoolController.Restore(state,d,fw),"recovery commit failure surfaced");
  check(state.EcoCoolPhase=="Prepared"&&state.EcoCoolPreviousCpuSpeed.SequenceEqual(s),"real ClearEcoCoolState retains in-memory snapshot on IO failure");
  state.OnSave=null;EcoCoolController.Restore(state,d,fw);check(state.EcoCoolPhase=="","recovery can resume after failed commit");
  seed();state=Journal();Ec.FailEveryWrite=true;
  throws(()=>EcoCoolController.Restore(state,d,fw),"recovery hardware failure surfaced");check(state.EcoCoolPhase=="Prepared","hardware restore failure retains journal");
  seed();state=new(){EcoCoolQualifiedFirmware=fw,EcoCoolQualifiedPolicySchema=EcoCoolController.OemEcoPolicySchema};
  SafetyEdition.HardwareWritesAuthorized=false;
  throws(()=>EcoCoolController.Apply(state,d,fw),"unapproved manual hardware control rejected");check(Ec.Writes==0,"unapproved control cannot mutate EC");
  SafetyEdition.HardwareWritesAuthorized=true;
  Ec.Bytes[d.FanMode]=0xFF;
  Ec.Bytes[d.ShiftMode]=d.ShiftTurboValue;
  throws(()=>EcoCoolController.Apply(state,d,fw),"unknown original fan state blocks apply");
  check(Ec.Writes==0 && state.EcoCoolPhase.Length==0,"unknown original fan state cannot create an unrestorable transaction");
  seed();Ec.Bytes[0xEB]=0xA7;
  throws(()=>EcoCoolController.Apply(state,d,fw),"unknown original recipe nibble blocks apply");
  check(Ec.Writes==0 && state.EcoCoolPhase.Length==0,"unknown recipe snapshot cannot authorize mutation");
  seed();
  var phases=new List<string>();state.OnSave=j=>phases.Add(j.EcoCoolPhase);
  EcoCoolController.Apply(state,d,fw);
  check(phases.SequenceEqual(new[]{"Prepared","Active"}),"controller persists Prepared before Active");
  check(EcoCoolController.VerifyPersistedActive(state,d,fw,out _),"controller verifies active policy against actual fake EC");
  EcoCoolController.Restore(state,d,fw);check(state.EcoCoolPhase==""&&Ec.Bytes[0xD2]==0xC1&&Ec.Bytes[0xD4]==0x0D&&Ec.Bytes[0xEB]==0xA0,"controller restores exact original recipe and clears only after commit");
  seed();var original=(byte[])Ec.Bytes.Clone();state=new(){EcoCoolQualifiedFirmware=fw,EcoCoolQualifiedPolicySchema=EcoCoolController.OemEcoPolicySchema};
  state.OnSave=j=>{if(j.EcoCoolPhase=="Active")throw new IOException("injected Active commit failure");};
  throws(()=>EcoCoolController.Apply(state,d,fw),"Active commit failure surfaced");check(Ec.Bytes.SequenceEqual(original)&&state.EcoCoolPhase=="","Active commit failure rolls back exact EC state");
  seed();state=new(){EcoCoolQualifiedFirmware=fw,EcoCoolQualifiedPolicySchema=EcoCoolController.OemEcoPolicySchema};
  state.OnSave=j=>throw new IOException("injected Prepared commit failure");
  throws(()=>EcoCoolController.Apply(state,d,fw),"Prepared persistence failure surfaced");check(Ec.Writes==0,"Prepared persistence failure precedes every write");
 }
}

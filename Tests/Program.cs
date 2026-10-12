using GhostDeck;
int passed=0;
void Check(bool ok,string name) { if(!ok) throw new Exception("FAIL "+name); Console.WriteLine("PASS "+name); passed++; }
void Throws(Action action,string name) { try { action(); } catch(Exception e) when(e is ArgumentException or InvalidOperationException or IOException or AggregateException) { Check(true,name); return; } throw new Exception("FAIL expected rejection: "+name); }
int[] t={0,55,62,69,76,82}, s={38,43,48,54,60,70}, gt={0,55,61,67,72,77}, gs={0,43,48,54,60,70};
var fc=new FanCurveSpec(0x8D,0x69,0x72,0x81,0x8A,6,true,false,150,t,s,gt,gs);
var d=new DeviceProfile { FanCurve=fc, Recipes=new() {
 [ProfileId.Balanced]=new (byte,byte)[]{(0xD2,0xC1),(0xD4,0x0D),(0xEB,0)},
 [ProfileId.Silent]=new (byte,byte)[]{(0xD2,0xC1),(0xD4,0x1D),(0xEB,0)},
 [ProfileId.Extreme]=new (byte,byte)[]{(0xD2,0xC4),(0xD4,0x0D),(0xEB,0)},
 [ProfileId.SuperBattery]=new (byte,byte)[]{(0xD2,0xC2),(0xD4,0x0D),(0xEB,15)} } };
void Seed() {
 Ec.Reset(); Ec.WriteFanCurve(d,t,s,gt,gs); Ec.Bytes[0xD4]=0x0D; Ec.Bytes[0xD2]=0xC1; Ec.Bytes[0xEB]=0xA0; Ec.Writes=0;
}
foreach(var (shift,fan,expected) in new[]{(0xC1,0x0D,ProfileId.Balanced),(0xC1,0x1D,ProfileId.Silent),(0xC1,0x8D,ProfileId.Balanced),(0xC2,0x0D,ProfileId.SuperBattery),(0xC4,0x0D,ProfileId.Extreme)})
 Check(EcProfileState.TryDecode(d,(byte)shift,(byte)fan,out var p)&&p==expected,$"profile {shift:X2}/{fan:X2}");
Check(!EcProfileState.TryDecode(d,0xFF,0x0D,out _),"unknown shift fails closed");
Check(!EcProfileState.TryDecode(d,0xC1,0xFF,out _),"unknown fan fails closed");
FanCurveSafety.ValidateForApply(fc,t,s,gt,gs); Check(true,"factory curve accepted");
int[] hot={38,43,55,80,110,150}, ghot={0,43,55,80,110,150};
FanCurveSafety.ValidateForApply(fc,t,hot,gt,ghot); Check(true,"EcoCool curve accepted");
Throws(()=>FanCurveSafety.ValidateForApply(fc,t,new int[6],gt,new int[6]),"all-zero hot curve rejected");
var late=(int[])t.Clone(); late[5]++;
Throws(()=>FanCurveSafety.ValidateForApply(fc,late,s,gt,gs),"delayed factory cooling rejected");
Throws(()=>FanCurveSafety.ValidateForApply(fc with {FactoryCpuSpeed=null},t,s,gt,gs),"unknown factory envelope rejected");
Throws(()=>FanCurveSafety.ValidateOrThrow(fc,t,new[]{38,43,55,80,110,151},gt,gs),"out-of-range order rejected");
Throws(()=>FanCurveSafety.ValidateOrThrow(fc,t,new[]{38,43,42,54,60,70},gt,gs),"descending curve rejected");
Throws(()=>FanCurveSafety.ValidateOrThrow(fc,t,new int[5],gt,gs),"partial table rejected");
Seed(); var baseline=(byte[])Ec.Bytes.Clone();
EcTransaction.ApplyRecipeVerified(d,d.Recipes[ProfileId.SuperBattery]);
Check(Ec.Bytes[0xEB]==0xAF,"recipe preserves firmware-owned high nibble");
Seed(); Ec.FailWriteAt=2;
Throws(()=>EcTransaction.ApplyRecipeVerified(d,d.Recipes[ProfileId.SuperBattery]),"partial recipe fails");
Check(Ec.Bytes.SequenceEqual(baseline),"partial recipe rollback exact");
Seed(); Throws(()=>EcTransaction.ApplyRecipeVerified(d,new (byte,byte)[]{(0x9D,1)}),"out-of-contract address rejected"); Check(Ec.Writes==0,"invalid address writes nothing");
Throws(()=>EcTransaction.ApplyRecipeVerified(d,new (byte,byte)[]{(0xD2,1),(0xD2,2)}),"duplicate address rejected");
Seed(); d.Tier=Tier.Experimental;
Throws(()=>EcTransaction.ApplyFanCurveVerified(d,t,hot,gt,ghot),"experimental transaction rejected"); Check(Ec.Writes==0,"experimental writes nothing"); d.Tier=Tier.Tested;
Seed(); EcTransaction.ApplyFanCurveVerified(d,t,hot,gt,ghot);
Check(Ec.Bytes[0xD4]==0x8D&&Ec.ReadFanCurve(d)!.Value.cpuSpeed.SequenceEqual(hot),"complete curve readback and advanced mode");
for(int fail=1;fail<=26;fail++) {
 Seed(); Ec.FailWriteAt=fail;
 Throws(()=>EcTransaction.ApplyFanCurveVerified(d,t,hot,gt,ghot),$"curve write failure {fail} detected");
 Check(Ec.Bytes.SequenceEqual(baseline),$"curve write failure {fail} restores exact state");
}
Seed(); Ec.CorruptNextCurveRead=true;
Throws(()=>EcTransaction.ApplyFanCurveVerified(d,t,hot,gt,ghot),"readback mismatch detected"); Check(Ec.Bytes.SequenceEqual(baseline),"readback mismatch rollback exact");
Seed(); Ec.FailEveryWrite=true;
try { EcTransaction.ApplyFanCurveVerified(d,t,hot,gt,ghot); throw new Exception("missing failure"); }
catch(AggregateException e) { Check(e.InnerExceptions.Count==2,"write and rollback failure both surfaced"); }
Seed(); EcTransaction.RestoreFanStateVerified(d,0x1D,t,s,gt,gs); Check(Ec.Bytes[0xD4]==0x1D,"exact prior fan mode restored");
Check(FanSweep.StepsFor(d).SequenceEqual(new[]{80,100,125,150}),"sweep never undercuts hot factory cooling");
var timeout=new FanSweep.Result {HasTach=true};
timeout.Steps.Add(new(80,80,80,3000,3000,65,60,-1)); timeout.Steps.Add(new(100,100,100,4000,4000,65,60,5));
Check(FanSweep.Findings(timeout).Count==0,"timeout cannot produce clean findings");
// Regression fixture: 2026-10-08 stabilized sweep had a physical RPM plateau despite
// increasing EC requests. Neither stability nor monotonicity proves command execution.
var plateau = new FanSweep.Result { HasTach = true, RestoreVerified = true };
int[] orders = {30,45,60,80,100,125,150};
int[] cpuRpms = {2202,2697,2688,2682,2688,2700,2694};
int[] gpuRpms = {1616,2685,2691,2679,2682,2697,2694};
for (int i=0;i<orders.Length;i++) plateau.Steps.Add(new(orders[i],orders[i],orders[i],cpuRpms[i],gpuRpms[i],74,68,5));
var plateauFindings = FanSweep.Findings(plateau);
Check(plateauFindings.Count(x=>x.key=="fc_find_unverified")==2,"historical plateau never proves EC command execution");
Check(!plateauFindings.Any(x=>x.key=="fc_find_ok"),"historical plateau never reports fan-response PASS");
var rising = new FanSweep.Result { HasTach=true, RestoreVerified=true };
rising.Steps.Add(new(80,80,80,3000,3000,74,68,5)); rising.Steps.Add(new(100,100,100,4100,4200,74,68,5));
Check(FanSweep.Findings(rising).Count(x=>x.key=="fc_find_unverified")==2,"rising RPM alone never proves EC command execution");

Seed(); Ec.Bytes[0xD4]=0xFE; Ec.Writes=0;
Throws(()=>FanSweep.RunRestoring(d,new[]{80},(_,_,_)=>{},CancellationToken.None),"unknown prior fan mode blocks calibration before write");
Check(Ec.Writes==0 && Ec.Bytes[0xD4]==0xFE,"unknown prior fan mode cannot be overwritten by calibration");
Seed(); var canceled=FanSweep.RunRestoring(d,new[]{80},(_,_,_)=>{},new CancellationToken(true));
Check(canceled.Aborted&&canceled.RestoreVerified&&Ec.Bytes.SequenceEqual(baseline),"canceled sweep restores real EC snapshot");
Seed(); var lost=FanSweep.RunRestoring(d,new[]{80},(_,_,_)=>{},CancellationToken.None);
Check(lost.Error!=null&&lost.RestoreVerified&&Ec.Bytes.SequenceEqual(baseline),"lost telemetry aborts and restores");
string dir=Path.Combine(AppContext.BaseDirectory,"io-fixture"); Directory.CreateDirectory(dir); string path=Path.Combine(dir,"state.json");
SafeFile.WriteAllTextAtomic(path,"old"); SafeFile.WriteAllTextAtomic(path,"new");
Check(File.ReadAllText(path)=="new"&&File.ReadAllText(path+".bak")=="old","atomic commit preserves previous copy");
using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
 Throws(()=>SafeFile.WriteAllTextAtomic(path,"must-not-commit"),"locked destination fails closed");
Check(File.ReadAllText(path)=="new"&&File.ReadAllText(path+".bak")=="old","failed commit preserves current and backup");
Check(!Directory.EnumerateFiles(dir,"*.tmp.*").Any(),"temporary IO resources cleaned");
ControllerChecks.Run(d,t,s,gt,gs,Check,Throws,Seed);
Console.WriteLine($"RESULT: {passed}/{passed} PASS (simulation and filesystem only; no hardware qualification)");

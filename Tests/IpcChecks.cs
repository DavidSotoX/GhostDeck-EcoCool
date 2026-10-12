using System.IO.Pipes;
using System.Reflection;
using GhostDeck;
if(args.Length==2 && args[0]=="--render") { UiChecks.Run(Path.GetFullPath(args[1])); return; }
if(args.Length==2 && args[0]=="--models") {
 string root=Path.GetFullPath(args[1]);byte[] data=File.ReadAllBytes(Path.Combine(root,"data/models.json"));
 if(!ModelDb.VerifySignature(data,File.ReadAllText(Path.Combine(root,"data/models.json.sig"))))throw new Exception("Invalid ECDSA signature");
 if(!ModelDb.Dump().AsSpan().SequenceEqual(data))throw new Exception("Model database drift");
 var tampered=(byte[])data.Clone();tampered[0]^=1;if(ModelDb.VerifySignature(tampered,File.ReadAllText(Path.Combine(root,"data/models.json.sig"))))throw new Exception("Tamper accepted");
 Console.WriteLine("PASS canonical model bytes, ECDSA signature and tamper rejection (no hardware access)");return;
}
var send=typeof(Cli).GetMethod("TrySendToRunning",BindingFlags.NonPublic|BindingFlags.Static)!;
int passed=0;
void Check(bool ok,string name) { if(!ok) throw new Exception("FAIL "+name); Console.WriteLine("PASS "+name); passed++; }
(bool sent,int code,string response) Invoke(string name) {
 object?[] args={new[]{"--cycle"},"",1,name}; bool sent=(bool)send.Invoke(null,args)!; return(sent,(int)args[2]!, (string)args[1]!);
}
string unique="GhostDeckSafetyTest_"+Guid.NewGuid().ToString("N");
Check(!Invoke(unique).sent,"absent server permits one-shot decision");
async Task Scenario(string suffix,Func<StreamReader,StreamWriter,Task> server,Func<(bool sent,int code,string response),bool> expected) {
 string name=unique+suffix;
 using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
 using var srv=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
 var serve=Task.Run(async()=> { await srv.WaitForConnectionAsync(timeout.Token); using var r=new StreamReader(srv); using var w=new StreamWriter(srv){AutoFlush=true}; await server(r,w); });
 var answer=Invoke(name); Console.WriteLine($"TRACE {suffix}: sent={answer.sent} code={answer.code} response={answer.response}"); await serve.WaitAsync(TimeSpan.FromSeconds(16));
 Check(expected(answer),suffix);
}
await Scenario("success",async(r,w)=>{await r.ReadLineAsync();await w.WriteLineAsync("0|verified");},a=>a.sent&&a.code==0&&a.response=="verified");
await Scenario("lost-reply",async(r,w)=>{await r.ReadLineAsync();},a=>a.sent&&a.code==1&&a.response.Contains("UNKNOWN"));
await Scenario("malformed-reply",async(r,w)=>{await r.ReadLineAsync();await w.WriteLineAsync("not-a-result");},a=>a.sent&&a.code==1);
var watch=System.Diagnostics.Stopwatch.StartNew();
await Scenario("stalled-reply",async(r,w)=>{await r.ReadLineAsync();await Task.Delay(11000);},a=>a.sent&&a.code==1&&a.response.Contains("UNKNOWN"));
Check(watch.Elapsed<TimeSpan.FromSeconds(15),"stalled response wait is bounded");
Console.WriteLine($"RESULT: {passed}/{passed} PASS (isolated random pipe; no hardware access)");

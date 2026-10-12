using System.Drawing;
using System.Linq.Expressions;
using GhostDeck;
internal static class UiChecks
{
 public static void Run(string destination)
 {
  Exception? failure=null;
  var thread=new Thread(()=> {
   try {
    Lang.Set("es"); Theme.Set(true);
    var deps=Activator.CreateInstance<MainDeps>();
    typeof(MainDeps).GetProperty("Settings")!.SetValue(deps,new AppSettings());
    foreach(var p in typeof(MainDeps).GetProperties().Where(p=>typeof(Delegate).IsAssignableFrom(p.PropertyType))) {
     var invoke=p.PropertyType.GetMethod("Invoke")!;
     var parameters=invoke.GetParameters().Select(p=>Expression.Parameter(p.ParameterType)).ToArray();
     Expression body=invoke.ReturnType==typeof(void)?Expression.Empty():Expression.Default(invoke.ReturnType);
     if(invoke.ReturnType==typeof(string)) body=Expression.Constant(p.Name=="Firmware"?"SIMULATED (no EC access)":p.Name=="AppVersion"?SafetyEdition.Version:"");
     if(p.Name=="Simulating")body=Expression.Constant(true);
     p.SetValue(deps,Expression.Lambda(p.PropertyType,body,parameters).Compile());
    }
    using var form=new Form{ClientSize=new Size(1200,760),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-32000,-32000),Opacity=0};
    using var page=new FocusHomePage(deps){Dock=DockStyle.Fill}; form.Controls.Add(page); form.PerformLayout(); form.CreateControl(); page.CreateControl(); form.Show(); Application.DoEvents();
    using var bitmap=new Bitmap(form.ClientSize.Width,form.ClientSize.Height); page.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.ClientSize));
    var colors=new HashSet<int>();for(int y=0;y<bitmap.Height;y+=5)for(int x=0;x<bitmap.Width;x+=5)colors.Add(bitmap.GetPixel(x,y).ToArgb());
    if(colors.Count<20)throw new Exception("Blank rendering detected");form.Hide();
    if(!page.Controls.OfType<Button>().Any(b=>b.Text=="Cerrar"))throw new Exception("Close button misrepresents apply");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);bitmap.Save(destination,System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine("PASS isolated Focus rendering and truthful close action; no hardware delegates");
   } catch(Exception ex){failure=ex;}
  });
  thread.SetApartmentState(ApartmentState.STA);thread.Start();if(!thread.Join(TimeSpan.FromSeconds(20)))throw new TimeoutException("UI rendering stalled");if(failure!=null)throw failure;
 }
}

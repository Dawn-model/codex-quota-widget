using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Xml;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

// Shares the existing quota-widget process; no new polling process or model monitor.
static class NativeQuotaWake {
 static readonly string Root=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","quota-timer"));
 static readonly object Gate=new object(),LogGate=new object();
 static Registration current;
 static bool closed,roundActive;
 static readonly bool AutoReturnEnabled=false; // Isolate hardware wake: no automatic sleep/hibernate in this diagnostic version.
 static long lastReset;
 [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] static extern IntPtr CreateWaitableTimer(IntPtr a,bool manual,string name);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetWaitableTimerEx(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr context,ref Reason wakeContext,uint tolerableDelay);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetWaitableTimer(IntPtr timer,ref long due,int period,IntPtr callback,IntPtr context,bool resume);
 [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
 [DllImport("kernel32.dll")] static extern bool IsSystemResumeAutomatic();
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr PowerCreateRequest(ref Reason reason);
 [DllImport("kernel32.dll",SetLastError=true)] static extern bool PowerSetRequest(IntPtr handle,int type);
 [DllImport("kernel32.dll")] static extern bool PowerClearRequest(IntPtr handle,int type);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [StructLayout(LayoutKind.Explicit,Size=32)] struct Reason {[FieldOffset(0)] public uint Version;[FieldOffset(4)] public uint Flags;[FieldOffset(8)] public IntPtr Text;}
 [StructLayout(LayoutKind.Sequential)] struct InputInfo {public uint Size,Tick;}
 [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref InputInfo info);
 [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool WTSQuerySessionInformation(IntPtr server,int session,int info,out IntPtr buffer,out uint bytes);
 [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr buffer);
 sealed class TimerHandle:WaitHandle {
  public IntPtr Text;public TimerHandle(IntPtr handle){SafeWaitHandle=new SafeWaitHandle(handle,true);}
  protected override void Dispose(bool disposing){base.Dispose(disposing);if(Text!=IntPtr.Zero){Marshal.FreeHGlobal(Text);Text=IntPtr.Zero;}}
 }
 sealed class Registration {
  public long Reset;public DateTime Due;public TimerHandle Timer;public readonly ManualResetEvent Cancel=new ManualResetEvent(false);
  public void Stop(){try{Cancel.Set();}catch(ObjectDisposedException){}}
 }
 static JavaScriptSerializer Serializer(){return new JavaScriptSerializer();}
 static long Now(){return DateTimeOffset.UtcNow.ToUnixTimeSeconds();}
 static string FilePath(string name){return Path.Combine(Root,name);}
 static void Log(string phase,object data){try{lock(LogGate){File.AppendAllText(FilePath("native-wake-history.jsonl"),Serializer().Serialize(new {at=DateTimeOffset.Now.ToString("o"),phase=phase,data=data})+Environment.NewLine);}}catch{}}
 static Dictionary<string,object> Read(string name){return Serializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(FilePath(name)));}
 static long Number(Dictionary<string,object> d,string key){object v;return d!=null&&d.TryGetValue(key,out v)&&v!=null?Convert.ToInt64(v):0;}
 static Dictionary<string,object> Object(Dictionary<string,object> d,string key){object v;return d!=null&&d.TryGetValue(key,out v)?v as Dictionary<string,object>:null;}
 static uint InputTick(){var info=new InputInfo{Size=(uint)Marshal.SizeOf(typeof(InputInfo))};return GetLastInputInfo(ref info)?info.Tick:UInt32.MaxValue;}
 static bool Locked(){
  IntPtr p;uint bytes;int session=Process.GetCurrentProcess().SessionId;
  if(!WTSQuerySessionInformation(IntPtr.Zero,session,25,out p,out bytes))return false;
  try{
   // WTSINFOEX x64: Level at 0, aligned Level1 union at 8; flags at 16.
   if(bytes<20||Marshal.ReadInt32(p,0)!=1||Marshal.ReadInt32(p,8)!=session)return false;
   return Marshal.ReadInt32(p,16)==0;
  }finally{WTSFreeMemory(p);}
 }
 static bool CanReturn(bool automatic,bool locked,uint initial,uint latest){return automatic&&locked&&initial!=UInt32.MaxValue&&initial==latest;}
 static bool ValidWakeEvidence(DateTime sleep,DateTime wake,DateTime due,DateTime now,string state,string source,string owner,string context,string sourceText){
  return state=="5" && source=="8" && sleep<due && wake>=due.AddSeconds(-5) && wake<=due.AddSeconds(120)
   && wake<=now.AddSeconds(3) && wake>=now.AddSeconds(-60)
   && ((owner??"")+" "+(context??"")+" "+(sourceText??"")).IndexOf("CodexQuotaResetWake",StringComparison.OrdinalIgnoreCase)>=0;
 }
 static string Field(XmlDocument doc,string name){var n=doc.SelectSingleNode("//*[local-name()='EventData']/*[local-name()='Data' and @Name='"+name+"']");return n==null?"":n.InnerText;}
 static bool ReadWakeEvidence(DateTime due,out string detail){
  detail="Windows wake-source event unavailable";
  try{
   var query=new EventLogQuery("System",PathType.LogName,"*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]"){ReverseDirection=true};
   using(var reader=new EventLogReader(query))using(var e=reader.ReadEvent()){
    if(e==null)return false;
    var doc=new XmlDocument();doc.LoadXml(e.ToXml());DateTime sleep,wake;
    if(!DateTime.TryParse(Field(doc,"SleepTime"),null,System.Globalization.DateTimeStyles.AdjustToUniversal|System.Globalization.DateTimeStyles.AssumeUniversal,out sleep)||!DateTime.TryParse(Field(doc,"WakeTime"),null,System.Globalization.DateTimeStyles.AdjustToUniversal|System.Globalization.DateTimeStyles.AssumeUniversal,out wake))return false;
    string source=Field(doc,"WakeSourceType"),owner=Field(doc,"WakeTimerOwner"),context=Field(doc,"WakeTimerContext");
    bool valid=ValidWakeEvidence(sleep,wake,due,DateTime.UtcNow,Field(doc,"EffectiveState"),source,owner,context,Field(doc,"WakeSourceText"));
    detail="record="+e.RecordId+"; source="+source+"; wake="+wake.ToString("o")+"; owner="+owner+"; context="+context;
    return valid;
   }
  }catch(Exception e){detail=e.Message;return false;}
 }
 static TimerHandle Arm(DateTime due,bool resume=false){
  IntPtr handle=CreateWaitableTimer(IntPtr.Zero,true,null);
  if(handle==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  var wait=new TimerHandle(handle);long ft=due.ToFileTimeUtc();SetLastError(0);
  wait.Text=Marshal.StringToHGlobalUni("Quota reset wake: one absolute alarm, no coalescing delay");
  var context=new Reason{Version=0,Flags=1,Text=wait.Text};
  bool ok=resume ? SetWaitableTimerEx(handle,ref ft,0,IntPtr.Zero,IntPtr.Zero,ref context,0) : SetWaitableTimer(handle,ref ft,0,IntPtr.Zero,IntPtr.Zero,false);int error=Marshal.GetLastWin32Error();
  if(!ok||error==50){wait.Dispose();throw new System.ComponentModel.Win32Exception(error);}
  return wait;
 }
 public static void Sync(Dictionary<string,object> window){
  try{
   if(window==null||Number(window,"windowDurationMins")!=300)return;
   lock(Gate){
    if(closed)return;
    if(File.Exists(FilePath("wake-round.json"))){
     var saved=Read("wake-round.json");
     if(saved.ContainsKey("suppressAutomaticWake")&&Convert.ToBoolean(saved["suppressAutomaticWake"])){
      if(Locked()&&InputTick()==(uint)Number(saved,"lastInputTick"))return;
      File.Delete(FilePath("wake-round.json"));File.Delete(FilePath("wake-schedule.json"));Log("MANUAL_RETURN_REARM",new {reason="user input or unlock"});
     }
    }
    if(roundActive)return;
    long reset=Number(window,"resetsAt");
    if(reset<=Now()||reset>Now()+604800)return;
    if(current!=null&&Math.Abs(reset-lastReset)<=2)return;
    DateTime due=DateTimeOffset.FromUnixTimeSeconds(Math.Max(reset-60,Now()+1)).UtcDateTime;
    var next=new Registration{Reset=reset,Due=due};
    var old=current;current=next;lastReset=reset;
    var thread=new Thread(()=>Run(next)){IsBackground=true,Name="Quota native wake wait"};thread.Start();
    if(old!=null)old.Stop();

   }
  }catch(Exception e){Log("REGISTER_FAILED",new {error=e.Message});}
 }
 static void Dispatch(){
  var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"schtasks.exe"),"/Run /TN CodexQuotaWindowTimer"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  using(var p=Process.Start(info)){if(!p.WaitForExit(5000)){p.Kill();throw new Exception("Quota task dispatch timed out");}if(p.ExitCode!=0)throw new Exception("Quota task dispatch failed "+p.ExitCode);}
 }
 static bool Confirmed(long expected,out long next){
  next=0;
  try{
   var state=Read("state.json");long sent=Number(state,"lastPingAt");
   var cache=Read("latest-quota.json");var primary=Object(Object(cache,"limits"),"primary");
   next=Number(primary,"resetsAt");
   return ValidConfirmation(expected,sent,Number(cache,"capturedAt"),Number(primary,"windowDurationMins"),next,Now());
  }catch{return false;}
 }
 static bool ValidConfirmation(long expected,long sent,long captured,long duration,long next,long now){return sent>=expected&&sent<=now+3&&captured>=sent&&captured<=now+3&&duration==300&&next>now+600&&next>expected+60&&next<=now+21600;}
 static void DisableFallback(){
  Type type=Type.GetTypeFromProgID("Schedule.Service");dynamic service=Activator.CreateInstance(type);service.Connect();
  try{service.GetFolder("\\").GetTask("CodexQuotaResetWake").Enabled=false;}finally{Marshal.FinalReleaseComObject(service);}
 }
 static void Run(Registration r){
  IntPtr request=IntPtr.Zero,text=IntPtr.Zero;bool powerHeld=false,ownsRound=false;
  try{
   // The same long-lived thread both registers and waits for the native timer.
   r.Timer=Arm(r.Due);
   lock(Gate){if(closed||current!=r)return;
    File.WriteAllText(FilePath("native-wake-state.json"),Serializer().Serialize(new {resetAt=r.Reset,wakeAt=new DateTimeOffset(r.Due).ToUnixTimeSeconds(),registeredAt=Now(),host="Windows Task Scheduler: CodexQuotaResetWake",resume=false,api="SetWaitableTimer(non-waking)",wakeOwner="CodexQuotaResetWake",leadSeconds=60,tolerableDelayMs=0}));
    Log("ARMED",new {resetAt=r.Reset,wakeAt=r.Due.ToLocalTime().ToString("o"),api="SetWaitableTimer(non-waking)",wakeOwner="CodexQuotaResetWake",ownerThread=Thread.CurrentThread.ManagedThreadId});
   }
   if(WaitHandle.WaitAny(new WaitHandle[]{r.Cancel,r.Timer})!=1)return;
   lock(Gate){if(closed||current!=r)return;roundActive=true;ownsRound=true;}
   bool resumeHint=IsSystemResumeAutomatic();uint first=InputTick();bool locked=Locked();
   bool automatic=false;string evidence="no confirmed scheduled-task wake";
   // The API hint is not wake-source evidence: a manual power-button restore
   // on this machine returned true. Only fresh Windows timer attribution counts.
   if(AutoReturnEnabled && resumeHint && locked && (DateTime.UtcNow-r.Due).TotalSeconds<=120){
    for(int i=0;i<15;i++){
     automatic=ReadWakeEvidence(r.Due,out evidence);
     if(automatic || InputTick()!=first || !Locked())break;
     if(r.Cancel.WaitOne(1000))return;
    }
   }
   Log("TIMER_CALLBACK",new {resetAt=r.Reset,latenessSeconds=(DateTime.UtcNow-r.Due).TotalSeconds,resumeApiHint=resumeHint,automaticWakeConfirmed=automatic,wakeEvidence=evidence,locked=locked});
   Dispatch();
   if(!AutoReturnEnabled){Log("AUTO_RETURN_DISABLED",new {reason="hardware-wake isolation; remain running after quota trigger"});return;}
   if(!CanReturn(automatic,locked,first,InputTick())){Log("NO_AUTO_SLEEP",new {reason="not an unattended locked resume"});return;}
   // Only the actual post-resume work interval, never while waiting asleep.
   text=Marshal.StringToHGlobalUni("Complete the quota reset request after automatic resume");var reason=new Reason{Version=0,Flags=1,Text=text};
   request=PowerCreateRequest(ref reason);
   if(request!=IntPtr.Zero&&request!=new IntPtr(-1))powerHeld=PowerSetRequest(request,3);
   bool retried=false;
   while(Now()<=r.Reset+180){
    if(r.Cancel.WaitOne(5000))return;
    if(!CanReturn(automatic,Locked(),first,InputTick())){Log("USER_RETURNED",new {resetAt=r.Reset});return;}
    if(!retried&&Now()>=r.Reset+35){Dispatch();retried=true;}
    long next;
    if(!Confirmed(r.Reset,out next))continue;
    // Cancel further automatic wakes for this overnight round until user input/unlock.
    File.WriteAllText(FilePath("wake-round.json"),Serializer().Serialize(new {suppressAutomaticWake=true,completedAt=Now(),resetAt=r.Reset,nextResetAt=next,lastInputTick=first}));
    try{DisableFallback();}catch(Exception e){File.Delete(FilePath("wake-round.json"));Log("FALLBACK_DISABLE_FAILED",new {error=e.Message});return;}
    Log("RESET_CONFIRMED_SLEEP_REQUEST",new {oldResetAt=r.Reset,nextResetAt=next});
    if(powerHeld){PowerClearRequest(request,3);powerHeld=false;}
    if(!CanReturn(automatic,Locked(),first,InputTick())){File.Delete(FilePath("wake-round.json"));File.Delete(FilePath("wake-schedule.json"));return;}
    bool slept=false;
    try{slept=Application.SetSuspendState(PowerState.Suspend,false,false);}catch(Exception e){Log("SLEEP_REQUEST_FAILED",new {error=e.Message});}
    if(!slept){
     Log("HIBERNATE_FALLBACK",new {reason="Windows rejected ordinary sleep"});
     Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"shutdown.exe"),"/h"){UseShellExecute=false,CreateNoWindow=true});
    }
    return;
   }
   Log("NO_AUTO_SLEEP",new {reason="quota success not confirmed before deadline; leave state unchanged"});
  }catch(Exception e){Log("WAKE_WORK_FAILED",new {error=e.Message});}
  finally{
   if(powerHeld)PowerClearRequest(request,3);
   if(request!=IntPtr.Zero&&request!=new IntPtr(-1))CloseHandle(request);
   if(text!=IntPtr.Zero)Marshal.FreeHGlobal(text);
   lock(Gate){if(ownsRound&&current==r)roundActive=false;}
   if(r.Timer==null){lock(Gate){if(current==r)current=null;}}else{r.Timer.Dispose();}r.Cancel.Dispose();
  }
 }
 public static void Dispose(){lock(Gate){closed=true;if(current!=null)current.Stop();}}
 public static void DurationTest(int minutes,string output){
  if(minutes<2||minutes>90)throw new ArgumentOutOfRangeException("minutes");
  var due=DateTime.UtcNow.AddMinutes(minutes);
  using(var timer=Arm(due,true)){
   File.WriteAllText(output,Serializer().Serialize(new {phase="ARMED",api="SetWaitableTimerEx",armedAt=DateTimeOffset.Now.ToString("o"),dueAt=new DateTimeOffset(due).ToLocalTime().ToString("o"),tolerableDelayMs=0,holdsSystemAwake=false,generatesModelRequest=false}));
   bool fired=timer.WaitOne(TimeSpan.FromMinutes(minutes+15));
   File.WriteAllText(output,Serializer().Serialize(new {phase=fired?"CALLBACK_OBSERVED":"WAIT_TIMEOUT",api="SetWaitableTimerEx",observedAt=DateTimeOffset.Now.ToString("o"),dueAt=new DateTimeOffset(due).ToLocalTime().ToString("o"),latenessSeconds=(DateTime.UtcNow-due).TotalSeconds,automaticResume=IsSystemResumeAutomatic(),locked=Locked(),generatesModelRequest=false}));
  }
 }
 public static void SelfTest(string output){
  var due=DateTime.UtcNow;var before=due.AddHours(-1);var wake=due.AddSeconds(20);var now=due.AddSeconds(25);
  if(!ValidWakeEvidence(before,wake,due,now,"5","8","svchost","Task \\CodexQuotaResetWake","")
   ||ValidWakeEvidence(before,wake,due,now,"5","0","","","")
   ||ValidWakeEvidence(before,due.AddMinutes(19),due,due.AddMinutes(19),"5","8","svchost","CodexQuotaResetWake","")
   ||ValidWakeEvidence(before,wake,due,now,"5","8","OtherTimer","","")
   ||ValidWakeEvidence(before,wake,due,now.AddMinutes(5),"5","8","svchost","CodexQuotaResetWake","")
   ||ValidWakeEvidence(before,wake,due,now,"4","8","svchost","CodexQuotaResetWake",""))throw new Exception("Wake attribution guard failed");
  if(CanReturn(false,true,1,1)||CanReturn(true,false,1,1)||CanReturn(true,true,1,2)||CanReturn(true,true,UInt32.MaxValue,UInt32.MaxValue)||!CanReturn(true,true,1,1))throw new Exception("Unsafe return-to-sleep decision");
  if(!ValidConfirmation(1000,1001,1002,300,19001,1003)||ValidConfirmation(1000,999,1002,300,19001,1003)||ValidConfirmation(1000,1001,999,300,19001,1003)||ValidConfirmation(1000,1001,1002,300,1000,1003))throw new Exception("Quota confirmation guards failed");
  using(var timer=Arm(DateTime.UtcNow.AddMilliseconds(300))){if(!timer.WaitOne(3000))throw new Exception("Absolute kernel timer did not fire");}
  IntPtr p;uint bytes;int session=Process.GetCurrentProcess().SessionId;
  if(!WTSQuerySessionInformation(IntPtr.Zero,session,25,out p,out bytes))throw new Exception("Session lock query unavailable");
  try{if(bytes<20||Marshal.ReadInt32(p,0)!=1||Marshal.ReadInt32(p,8)!=session||(Marshal.ReadInt32(p,16)!=0&&Marshal.ReadInt32(p,16)!=1))throw new Exception("Session lock query layout mismatch: bytes="+bytes+", session="+session+", fields="+Marshal.ReadInt32(p,0)+","+Marshal.ReadInt32(p,4)+","+Marshal.ReadInt32(p,8)+","+Marshal.ReadInt32(p,12)+","+Marshal.ReadInt32(p,16)+","+Marshal.ReadInt32(p,20));}finally{WTSFreeMemory(p);}
  File.WriteAllText(output,"PASS: non-waking timer fired; fresh S4 task-attributed timer evidence accepted; manual, stale, late, unrelated and S0/S3 evidence rejected; lock and quota guards passed. No model request or power transition performed. Automatic return is disabled. Hardware wake is NOT verified.");
 }
}

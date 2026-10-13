using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public sealed class TightLabel : Label {
    protected override void OnPaint(PaintEventArgs e) {
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,
            TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter);
    }
}

public sealed class QuotaWidget : Form {
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string RestoreMarker = Path.Combine(Root,"restore.request");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly Color BlushBackground = Color.FromArgb(255,242,247);
    static readonly Color BlushBorder = Color.FromArgb(231,164,188);
    static readonly Color PrimaryTextColor = Color.FromArgb(42,31,36);
    static readonly Color WeeklyTextColor = Color.FromArgb(62,46,53);
    static readonly Color PrimaryAccent = Color.FromArgb(180,35,90);
    static readonly Color WeeklyAccent = Color.FromArgb(134,64,89);
    static readonly Color HideColor = Color.FromArgb(132,82,100);
    static readonly EventWaitHandle RestoreRequest = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\CodexQuotaWidgetRestoreOyzy2");
    readonly Label primary = new TightLabel(), weekly = new TightLabel();
    readonly Label primaryTitle = new TightLabel(), weeklyTitle = new TightLabel();
    readonly Label hide;
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly ToolTip tips = new ToolTip();
    readonly ToolStripMenuItem pin = new ToolStripMenuItem("置顶");
    sealed class PendingQuery {
        public string Primary, Weekly;
    }
    Task<PendingQuery> startingQuery;
    uint cachedForegroundPid;
    string cachedForegroundName="";
    double maxTickMs;
    double maxSyncMs,maxRenderMs,maxMetadataMs,maxPositionMs,maxShowMs;
    DateTime nextQuery = DateTime.MinValue, lastSuccess = DateTime.MinValue;
    string primaryText = "5小时：—", weeklyText = "一周：—";
    bool failed;
    bool userHidden;
    bool dragging;
    bool appActiveNow;
    bool lastContentWasApp;
    string lastLayoutKey="";
    string lastTooltip="";
    IntPtr appWindow=IntPtr.Zero;
    readonly bool previewMode;
    int offsetX=-1, offsetY=100;
    delegate void WinEventProc(IntPtr hook,uint evt,IntPtr hwnd,int objectId,int childId,uint thread,uint time);
    WinEventProc focusCallback;
    IntPtr focusHook,minimizeHook,moveHook;
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint start,uint end,IntPtr module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr handle,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd,uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd,uint command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out RECT rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool SetProp(IntPtr hwnd,string name,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr RemoveProp(IntPtr hwnd,string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(int extended,string className,string title,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int command);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder name,int capacity);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd,int index);
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get {
            var cp=base.CreateParams;
            // Keep this auxiliary window out of Alt+Tab, including after a
            // handle recreation caused by hiding, opacity or shell restarts.
            // Native nonactivation must also survive the layered HWND
            // recreation when Opacity changes from hidden to visible.
            cp.ExStyle=(cp.ExStyle | 0x00000080 | 0x08000000) & ~0x00040000;
            return cp;
        }
    }

    [STAThread] public static void Main(string[] args) {
        if(args.Length>0&&args[0]=="--tray-test"){
            try{TrayBindingTest();}catch(Exception e){File.WriteAllText(Path.Combine(Root,"tray-binding-test.json"),Json.Serialize(new{passed=false,error=e.ToString()}));Environment.ExitCode=1;}return;
        }
        if(args.Length > 0 && args[0] == "--self-test") {
            try { SelfTest(); } catch(Exception e) { File.WriteAllText(Path.Combine(Root,"test-result.txt"),"FAIL: "+e.Message); Environment.ExitCode=1; }
            return;
        }
        bool created;
        using(var single = new Mutex(true, "Local\\CodexQuotaWidgetOyzy2", out created)) {
            if(!created) {
                // A named event is the fast path. The marker is a durable fallback
                // for environments where the second process cannot signal it.
                try { File.WriteAllText(RestoreMarker,DateTime.UtcNow.ToString("o")); } catch { }
                RestoreRequest.Set();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new QuotaWidget());
            single.ReleaseMutex();
        }
    }
    public QuotaWidget(bool preview=false) {
        previewMode=preview;
        Text="额度小浮窗"; FormBorderStyle=FormBorderStyle.None;
        AutoScaleMode=AutoScaleMode.None;
        ClientSize=new Size(295,76); BackColor=BlushBackground;
        DoubleBuffered=true;
        ForeColor=PrimaryTextColor; TopMost=true; ShowInTaskbar=false;
        StartPosition=FormStartPosition.Manual;
        Rectangle area=Screen.PrimaryScreen.WorkingArea;
        Location=new Point(area.Right-Width-28,area.Top+100);
        LoadCachedDisplay();
        var menu=new ContextMenuStrip();
        menu.Items.Add("立即刷新",null,delegate { nextQuery=DateTime.MinValue; });
        pin.Checked=true; pin.CheckOnClick=true;
        pin.CheckedChanged+=delegate { TopMost=pin.Checked; };
        menu.Items.Add(pin);
        menu.Items.Add("恢复显示（仅GPT内）",null,delegate { userHidden=false; nextQuery=DateTime.MinValue; SyncToApp(); });
        menu.Items.Add("收起",null,delegate { userHidden=true; Hide(); });
        menu.Items.Add("退出",null,delegate { Close(); });
        ContextMenuStrip=menu;
        primaryTitle.Text="5小时"; weeklyTitle.Text="一  周";
        SetupLabel(primaryTitle,11,8,38); SetupLabel(weeklyTitle,11,40,38);
        SetupLabel(primary,49,8,237); SetupLabel(weekly,49,40,237);
        hide=new Label { Text="−", Location=new Point(276,-2),Size=new Size(18,20),Cursor=Cursors.Hand,BackColor=Color.Transparent,
            ForeColor=HideColor,Font=new Font("Segoe UI",9),TextAlign=ContentAlignment.MiddleCenter };
        hide.Click+=delegate { userHidden=true; Hide(); }; tips.SetToolTip(hide,"收起；在聊天中说“打开额度状态栏”即可请求恢复");
        Controls.Add(hide);
        MouseDown+=DragWindow;
        Paint+=delegate(object s,PaintEventArgs e) {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float borderWidth=Math.Max(1f,GetDpiForSystem()/96f);
            using(var p=new Pen(BlushBorder,borderWidth))
            using(var path=RoundedBorder(ClientSize.Width,ClientSize.Height,borderWidth/2f+0.5f)) e.Graphics.DrawPath(p,path);
        };
        ResizeEnd+=delegate { SavePosition(); };
        poll.Interval=50; poll.Tick+=Tick;
        if(!preview) {
            poll.Start(); focusCallback=OnWindowEvent;
            focusHook=SetWinEventHook(3,3,IntPtr.Zero,focusCallback,0,0,0);
            minimizeHook=SetWinEventHook(0x16,0x17,IntPtr.Zero,focusCallback,0,0,0);
            moveHook=SetWinEventHook(0x800B,0x800B,IntPtr.Zero,focusCallback,0,0,0);
        }
        float dpiScale=GetDpiForSystem()/96f;
        if(dpiScale!=1f) Scale(new SizeF(dpiScale,dpiScale));
        UpdateShape();
        LoadPosition();
        Opacity=0; Render();
        FormClosed+=delegate { poll.Stop(); poll.Dispose(); SavePosition(); tips.Dispose();
            if(focusHook!=IntPtr.Zero) UnhookWinEvent(focusHook);
            if(minimizeHook!=IntPtr.Zero) UnhookWinEvent(minimizeHook);
            if(moveHook!=IntPtr.Zero) UnhookWinEvent(moveHook);
            // An in-flight read-only query finishes normally and cleans up its app-server.
            if(startingQuery!=null) startingQuery.ContinueWith(delegate(Task<PendingQuery> t) {
                if(t.IsFaulted) { var ignored=t.Exception; }
            }); };
    }
    void SetupLabel(Label label,int x,int y,int width) {
        label.SetBounds(x,y,width,27); label.Font=new Font("Microsoft YaHei UI",10f,FontStyle.Regular);
        label.BackColor=Color.Transparent;
        label.TextAlign=ContentAlignment.MiddleLeft; label.AutoEllipsis=false;
        label.MouseDown+=DragWindow; label.ContextMenuStrip=ContextMenuStrip; Controls.Add(label);
    }
    void DragWindow(object s,MouseEventArgs e) {
        if(e.Button!=MouseButtons.Left) return;
        dragging=true;
        try {
            ReleaseCapture(); SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);
        } finally {
            // Save the final mouse-selected offset before resuming app-window tracking.
            SavePosition(); dragging=false;
        }
    }
    void Toggle() { userHidden=!userHidden; if(userHidden) Hide(); else { nextQuery=DateTime.MinValue; SyncToApp(); } }
    void OnWindowEvent(IntPtr hook,uint evt,IntPtr hwnd,int objectId,int childId,uint thread,uint time) {
        if(IsDisposed || Disposing) return;
        if(evt==0x800B && (hwnd!=appWindow || objectId!=0 || childId!=0)) return;
        if(evt==0x16 && hwnd==appWindow) { appActiveNow=false; Hide(); return; }
        SyncToApp();
    }
    static GraphicsPath RoundedBorder(int width,int height,float inset=0f) {
        var path=new GraphicsPath(); float diameter=Math.Max(12f,12*GetDpiForSystem()/96f)-2*inset;
        float right=width-1-inset, bottom=height-1-inset;
        path.AddArc(inset,inset,diameter,diameter,180,90);
        path.AddArc(right-diameter,inset,diameter,diameter,270,90);
        path.AddArc(right-diameter,bottom-diameter,diameter,diameter,0,90);
        path.AddArc(inset,bottom-diameter,diameter,diameter,90,90); path.CloseFigure(); return path;
    }
    void UpdateShape() { using(var path=RoundedBorder(ClientSize.Width,ClientSize.Height)) { var old=Region; Region=new Region(path); if(old!=null) old.Dispose(); } }
    void LoadCachedDisplay() {
        try { var cached=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"display-status.json")));
            primaryText=NormalizeDisplay(Convert.ToString(cached["primary"])); weeklyText=NormalizeDisplay(Convert.ToString(cached["weekly"]));
            lastSuccess=DateTime.Parse(Convert.ToString(cached["lastSuccessUtc"]),null,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        } catch { }
    }
    void LoadPosition() {
        try { var saved=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"position.json")));
            if(saved.ContainsKey("offsetX")) { offsetX=Convert.ToInt32(saved["offsetX"]); offsetY=Convert.ToInt32(saved["offsetY"]); }
            var p=new Point(Convert.ToInt32(saved["x"]),Convert.ToInt32(saved["y"]));
            foreach(var screen in Screen.AllScreens) if(screen.WorkingArea.Contains(new Rectangle(p,Size))) { Location=p; break; }
        } catch { }
    }
    bool TryGetSavedOffset(out Point savedOffset) {
        savedOffset=new Point(offsetX,offsetY);
        RECT rect;
        if(appWindow!=IntPtr.Zero) {
            // Minimized windows have an offscreen sentinel rectangle. Preserve
            // the saved offset when closing while the app is hidden/minimized.
            if(!IsMainAppFrame(appWindow)||!IsWindowVisible(appWindow)||IsIconic(appWindow)||!GetWindowRect(appWindow,out rect))return false;
            savedOffset=new Point(Left-rect.Left,Top-rect.Top);
        }
        return true;
    }
    void SavePosition() { if(previewMode)return;try {
        Point savedOffset;
        if(!TryGetSavedOffset(out savedOffset))return;
        offsetX=savedOffset.X;offsetY=savedOffset.Y;
        File.WriteAllText(Path.Combine(Root,"position.json"),Json.Serialize(new { x=Left,y=Top,offsetX=offsetX,offsetY=offsetY }));
    } catch { } }
    void SyncToApp() {
        var syncWatch=Stopwatch.StartNew();
        try { SyncToAppCore(); } finally { maxSyncMs=Math.Max(maxSyncMs,syncWatch.Elapsed.TotalMilliseconds); }
    }
    static string ReadProcessName(uint pid) {
        var handle=OpenProcess(0x1000,false,pid);
        if(handle==IntPtr.Zero) return "";
        try {
            var path=new StringBuilder(2048); uint size=(uint)path.Capacity;
            return QueryFullProcessImageName(handle,0,path,ref size) ? Path.GetFileNameWithoutExtension(path.ToString()) : "";
        } finally { CloseHandle(handle); }
    }
    static bool IsMainAppFrame(IntPtr hwnd){
        if(hwnd==IntPtr.Zero||!IsWindow(hwnd)||GetAncestor(hwnd,2)!=hwnd||GetWindow(hwnd,4)!=IntPtr.Zero)return false;
        int style=GetWindowLong(hwnd,-16),extended=GetWindowLong(hwnd,-20);
        return (style&unchecked((int)0xC0000000))==0&&(style&0x00040000)!=0&&((style&0x00C00000)!=0||(style&0x00030000)==0x00030000)&&(extended&(0x80|0x08000000))==0;
    }
    void SyncToAppCore(){SyncToAppCore(GetForegroundWindow());}
    void SyncToAppCore(IntPtr foreground) {
        // Windows' native move loop still dispatches timer/window events. Do not
        // restore the previous saved position while the mouse is moving us.
        if(dragging) return;
        uint pid;
        GetWindowThreadProcessId(foreground,out pid);
        bool appActive=false;
        try {
            // Foreground classification must not query process metadata on every
            // focus/layout event. Refresh the cache when the foreground PID changes.
            if(pid!=cachedForegroundPid || cachedForegroundName.Length==0) {
                var metadataWatch=Stopwatch.StartNew();
                cachedForegroundName=ReadProcessName(pid);
                maxMetadataMs=Math.Max(maxMetadataMs,metadataWatch.Elapsed.TotalMilliseconds);
                cachedForegroundPid=pid;
            }
            string name=cachedForegroundName;
            bool appProcess=string.Equals(name,"Codex",StringComparison.OrdinalIgnoreCase) || string.Equals(name,"ChatGPT",StringComparison.OrdinalIgnoreCase);
            appActive=appProcess&&IsMainAppFrame(foreground);
            if(appActive) { appWindow=foreground; lastContentWasApp=true; }
            else if(appProcess) {
                // GPT's tray menu is a separate foreground window in the same
                // process. Keep showing at the existing main-window anchor;
                // the menu must never become the position reference.
                appActive=lastContentWasApp;
            }
            else if(pid==GetCurrentProcessId() && appWindow!=IntPtr.Zero) appActive=lastContentWasApp;
            else {
                var className=new StringBuilder(256); GetClassName(foreground,className,className.Capacity);
                bool shellOverlay=IsTransientShell(name,className.ToString());
                appActive=KeepAppVisible(lastContentWasApp,shellOverlay);
                if(!shellOverlay) lastContentWasApp=false;
            }
        } catch { }
        RECT rect;
        appActiveNow=appActive;
        var startupSurface=appWindow==IntPtr.Zero?IntPtr.Zero:GetProp(appWindow,"CodexStartupAnimationSurfaceOyzy2");
        // Suppress display only, preserving position, userHidden and refresh.
        // A crashed animation loses its HWND and cannot leave us hidden.
        bool startupPlaying=startupSurface!=IntPtr.Zero&&IsWindow(startupSurface);
        if(userHidden || startupPlaying || !appActive || !IsMainAppFrame(appWindow) || !IsWindowVisible(appWindow) || IsIconic(appWindow) || !GetWindowRect(appWindow,out rect)) { if(Visible) Hide(); return; }
        int x=offsetX<0 ? rect.Right-rect.Left-Width-24 : offsetX;
        x=Math.Max(0,Math.Min(x,Math.Max(0,rect.Right-rect.Left-Width)));
        int y=Math.Max(0,Math.Min(offsetY,Math.Max(0,rect.Bottom-rect.Top-Height)));
        var positionWatch=Stopwatch.StartNew();
        var targetPosition=new Point(rect.Left+x,rect.Top+y);
        if(Location!=targetPosition) Location=targetPosition;
        // The renderer publishes its actual background reveal alpha, so the
        // quota fades on the same frames instead of using a separate clock.
        var activeSurface=GetProp(appWindow,"CodexStartupAnimationActiveSurfaceOyzy2");
        long reveal=GetProp(appWindow,"CodexStartupAnimationRevealOpacityOyzy2").ToInt64();
        double targetOpacity=activeSurface!=IntPtr.Zero&&IsWindow(activeSurface)&&reveal>=1&&reveal<=10001?(reveal-1)/10000.0:1;
        if(Math.Abs(Opacity-targetOpacity)>.00001) Opacity=targetOpacity;
        maxPositionMs=Math.Max(maxPositionMs,positionWatch.Elapsed.TotalMilliseconds);
        if(!Visible) {
            var showWatch=Stopwatch.StartNew();
            Show();
            maxShowMs=Math.Max(maxShowMs,showWatch.Elapsed.TotalMilliseconds);
        }
    }
    static bool KeepAppVisible(bool previousApp,bool shellOverlay) { return previousApp && shellOverlay; }
    protected override void WndProc(ref Message m) {
        if(m.Msg==0x8031){SyncToApp();return;}
        base.WndProc(ref m);
    }
    static bool IsTransientShell(string processName,string className) {
        if(string.Equals(processName,"StartMenuExperienceHost",StringComparison.OrdinalIgnoreCase) ||
           string.Equals(processName,"SearchHost",StringComparison.OrdinalIgnoreCase) ||
           string.Equals(processName,"ShellExperienceHost",StringComparison.OrdinalIgnoreCase)) return true;
        if(!string.Equals(processName,"explorer",StringComparison.OrdinalIgnoreCase)) return false;
        return className=="Shell_TrayWnd" || className=="Shell_SecondaryTrayWnd" ||
            className=="NotifyIconOverflowWindow" || className=="TopLevelWindowForOverflowXamlIsland" ||
            className=="XamlExplorerHostIslandWindow";
    }
    void Tick(object s,EventArgs e) {
        var tickWatch=Stopwatch.StartNew();
        bool restoreRequested=RestoreRequest.WaitOne(0);
        try {
            if(File.Exists(RestoreMarker)) {
                File.Delete(RestoreMarker);
                restoreRequested=true;
            }
        } catch { }
        if(restoreRequested) { userHidden=false; nextQuery=DateTime.MinValue; }
        SyncToApp();
        if(startingQuery!=null && startingQuery.IsCompleted) {
            try {
                var ready=startingQuery.Result;
                primaryText=ready.Primary; weeklyText=ready.Weekly;
                lastSuccess=DateTime.UtcNow; failed=false;
            } catch { failed=true; nextQuery=DateTime.UtcNow.AddSeconds(5); }
            startingQuery=null;
            WriteStatus();
        }
        // Reading limits is independent of whether the companion window is visible.
        // Keep a single in-flight query, including while GPT is in the background.
        if(startingQuery==null && DateTime.UtcNow>=nextQuery) StartQuery();
        var renderWatch=Stopwatch.StartNew();
        Render();
        maxRenderMs=Math.Max(maxRenderMs,renderWatch.Elapsed.TotalMilliseconds);
        maxTickMs=Math.Max(maxTickMs,tickWatch.Elapsed.TotalMilliseconds);
    }
    static Dictionary<string,object> GetWindow(Dictionary<string,object> data,string key) {
        object value; return data!=null && data.TryGetValue(key,out value) ? value as Dictionary<string,object> : null;
    }
    public static string FormatWindow(string name,Dictionary<string,object> w,int duration,bool showDate) {
        if(w==null || !w.ContainsKey("usedPercent") || w["usedPercent"]==null || !w.ContainsKey("resetsAt") || w["resetsAt"]==null)
            return name+"：数据不可用";
        if(w.ContainsKey("windowDurationMins") && Convert.ToInt32(w["windowDurationMins"])!=duration)
            return name+"：窗口类型已变化";
        double used=Convert.ToDouble(w["usedPercent"]);
        if(double.IsNaN(used) || double.IsInfinity(used)) return name+"：数据不可用";
        double remaining=Math.Max(0,Math.Min(100,100-used));
        var utc=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(Convert.ToInt64(w["resetsAt"]));
        var reset=TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc,"China Standard Time");
        return name+"：剩余 "+remaining.ToString("0.#")+"%（"+reset.ToString(showDate?"M月d日 HH:mm":"HH:mm")+" 重置）";
    }
    void StartQuery() {
        nextQuery=DateTime.UtcNow.AddSeconds(5);
        try {
            var info=new ProcessStartInfo {
                FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments="-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \""+Path.GetFullPath(Path.Combine(Root,"..","quota-timer","quota-timer.ps1"))+"\" -ProbeOnly",
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                WorkingDirectory=Root };
            // Process.Start can block during antivirus checks or heavy system load.
            // Do not let that pause WinEvent delivery or hide/show on the UI thread.
            startingQuery=LaunchProbeAsync(delegate {
                using(var p=Process.Start(info)) {
                    var output=p.StandardOutput.ReadToEndAsync();
                    var error=p.StandardError.ReadToEndAsync();
                    if(!p.WaitForExit(90000)) { p.Kill(); throw new Exception("Probe timed out"); }
                    if(!Task.WaitAll(new Task[] { output,error },5000) || p.ExitCode!=0) throw new Exception("Probe failed");
                    var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(output.Result.Trim());
                    string newPrimary=FormatWindow("5小时",GetWindow(data,"primary"),300,false);
                    string newWeekly=FormatWindow("一周",GetWindow(data,"secondary"),10080,true);
                    if(!newPrimary.Contains("剩余") || !newWeekly.Contains("剩余")) throw new Exception("Incomplete limits");
                    return new PendingQuery { Primary=newPrimary,Weekly=newWeekly };
                }
            });
        } catch { startingQuery=null; failed=true; WriteStatus(); }
    }
    static Task<PendingQuery> LaunchProbeAsync(Func<PendingQuery> factory) { return Task.Run(factory); }
    void Render() {
        // Keep the previous reading throughout refresh and transient failures.
        SetIfChanged(primary,BodyDisplay(primaryText)); SetIfChanged(weekly,BodyDisplay(weeklyText));
        primary.ForeColor=PrimaryTextColor;
        weekly.ForeColor=WeeklyTextColor;
        primaryTitle.ForeColor=PrimaryAccent;
        weeklyTitle.ForeColor=WeeklyAccent;
        FitTextWidth();
        string detail="每5秒刷新；拖动文字调整位置；右键打开菜单。";
        if(lastTooltip!=detail) {
            lastTooltip=detail;
            tips.SetToolTip(primary,detail); tips.SetToolTip(weekly,detail);
            tips.SetToolTip(primaryTitle,detail); tips.SetToolTip(weeklyTitle,detail);
        }
    }
    static Rectangle MeasureInk(string text,Font font,int height) {
        using(var bitmap=new Bitmap(1024,height)) {
            bitmap.SetResolution(GetDpiForSystem(),GetDpiForSystem());
            using(var graphics=Graphics.FromImage(bitmap)) {
                graphics.Clear(Color.Black);
                TextRenderer.DrawText(graphics,text,font,new Rectangle(0,0,bitmap.Width,bitmap.Height),Color.White,
                    TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter);
            }
            // Scan one copied pixel buffer, not tens of thousands of synchronous
            // GDI+ GetPixel calls which used to block foreground event handling.
            int left=bitmap.Width,right=-1;
            var bits=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try {
                int stride=Math.Abs(bits.Stride);
                var pixels=new byte[stride*bitmap.Height];
                Marshal.Copy(bits.Scan0,pixels,0,pixels.Length);
                for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++) {
                    int at=y*stride+x*4;
                    if(pixels[at]>8 || pixels[at+1]>8 || pixels[at+2]>8) { left=Math.Min(left,x);right=Math.Max(right,x); }
                }
            } finally { bitmap.UnlockBits(bits); }
            return right<0 ? Rectangle.Empty : new Rectangle(left,0,right-left+1,height);
        }
    }
    void FitTextWidth() {
        if(dragging) return;
        string key=primary.Text+"\n"+weekly.Text;
        if(lastLayoutKey==key) return;
        lastLayoutKey=key;
        var titleInk=MeasureInk(primaryTitle.Text,primaryTitle.Font,primaryTitle.Height);
        int margin=primaryTitle.Left+titleInk.Left;
        int primaryRight=MeasureInk(primary.Text,primary.Font,primary.Height).Right;
        int weeklyRight=MeasureInk(weekly.Text,weekly.Font,weekly.Height).Right;
        int textRight=primary.Left+Math.Max(primaryRight,weeklyRight);
        // Leave the same visible margin after the last glyph as before the title.
        int width=Math.Max(textRight+margin,(int)Math.Round(140*GetDpiForSystem()/96.0));
        ClientSize=new Size(width,ClientSize.Height);
        primary.Width=primaryRight+4; weekly.Width=weeklyRight+4;
        hide.Left=ClientSize.Width-hide.Width-(int)Math.Round(GetDpiForSystem()/96.0);
        hide.BringToFront(); UpdateShape(); Invalidate();
    }
    static void SetIfChanged(Label label,string text) { if(label.Text!=text) label.Text=text; }
    public static string BodyDisplay(string text) { int i=(text??"").IndexOf('：'); return i>=0?text.Substring(i):"：—"; }
    static string NormalizeDisplay(string text) { int i=(text??"").IndexOf('·'); return i>=0?text.Substring(0,i).TrimEnd()+"（"+text.Substring(i+1).Trim()+"）":text; }
    public static string PercentDisplay(string text) { var m=Regex.Match(text??"","剩余\\s+([0-9.]+%)"); return m.Success?"剩余 "+m.Groups[1].Value:"—"; }
    public static string ResetDisplay(string text) { int i=(text??"").IndexOf('·'); return i>=0?text.Substring(i+1).Trim():"暂无数据"; }
    void WriteStatus() {
        string snapshot=Json.Serialize(new { primary=primaryText,weekly=weeklyText,failed=failed,lastSuccessUtc=lastSuccess.ToString("o"),refreshSeconds=5,offsetX=offsetX,offsetY=offsetY,focusHookActive=focusHook!=IntPtr.Zero,focusPollMs=poll.Interval,mainWindowAnchorOnly=true,trayKeepsWidgetVisible=true,probeStartupOffUiThread=true,fullProbeOffUiThread=true,appActive=appActiveNow,visible=Visible,userHidden=userHidden,foregroundProcess=cachedForegroundName,foregroundPid=cachedForegroundPid,appWindow=appWindow.ToInt64(),maxTickMs=Math.Round(maxTickMs,2),maxSyncMs=Math.Round(maxSyncMs,2),maxRenderMs=Math.Round(maxRenderMs,2),maxMetadataMs=Math.Round(maxMetadataMs,2),maxPositionMs=Math.Round(maxPositionMs,2),maxShowMs=Math.Round(maxShowMs,2) });
        Task.Run(delegate { try { File.WriteAllText(Path.Combine(Root,"display-status.json"),snapshot); } catch { } });
    }
    static void AssertRenderedMargins(Bitmap image,int top,int bottom) {
        int first=image.Width,last=-1;
        for(int x=0;x<image.Width;x++) for(int y=top;y<bottom;y++) {
            var pixel=image.GetPixel(x,y);
            if(pixel.R>110 && pixel.G>110 && pixel.B>110) { first=Math.Min(first,x);last=Math.Max(last,x); }
        }
        if(last<0 || Math.Abs(first-(image.Width-1-last))>2) throw new Exception("Unequal rendered margins: left="+first+" right="+(image.Width-1-last));
    }
    static void TrayBindingTest() {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string positionPath=Path.Combine(Root,"position.json"), savedPosition=File.ReadAllText(positionPath);
        var foregroundBefore=GetForegroundWindow();
        var foregroundStages=new List<string>();
        Action<string> recordForeground=delegate(string stage){foregroundStages.Add(stage+"="+GetForegroundWindow());};
        IntPtr main=IntPtr.Zero,second=IntPtr.Zero,tray=IntPtr.Zero,owned=IntPtr.Zero;
        Action<bool,string> check=delegate(bool passed,string message){if(!passed)throw new Exception(message);};
        try {
            // Match the actual GPT frame/menu styles with native, offscreen
            // windows. No foreground switch or real tray interaction is needed.
            main=CreateWindowEx(0,"STATIC","Main probe",0x04C70000,-20000,-20000,1600,1900,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            second=CreateWindowEx(0,"STATIC","Second main probe",0x04070000,-23000,-20000,1600,1900,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            tray=CreateWindowEx(0x00200088,"STATIC","Tray probe",0x04000000,-26000,-20000,820,1000,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            owned=CreateWindowEx(0,"STATIC","Owned dialog probe",0x04C70000,-28000,-20000,820,1000,main,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            check(main!=IntPtr.Zero&&second!=IntPtr.Zero&&tray!=IntPtr.Zero&&owned!=IntPtr.Zero,"Cannot create native probes");
            ShowWindow(main,4);ShowWindow(second,4);ShowWindow(tray,4);ShowWindow(owned,4);
            recordForeground("native-probes");
            check(IsMainAppFrame(main)&&IsMainAppFrame(second)&&!IsMainAppFrame(tray)&&!IsMainAppFrame(owned),"Frame/menu classification failed");
            using(var widget=new QuotaWidget(true)) {
                widget.cachedForegroundPid=GetCurrentProcessId();widget.cachedForegroundName="ChatGPT";
                widget.offsetX=107;widget.offsetY=100;
                widget.SyncToAppCore(tray);
                check(widget.appWindow==IntPtr.Zero&&!widget.Visible,"Tray established an initial anchor");
                widget.SyncToAppCore(main);
                recordForeground("widget-show");
                check(widget.appWindow==main&&widget.Visible,"Main did not establish an anchor");
                Point anchorPosition=widget.Location;
                for(int i=0;i<50;i++){
                    widget.SyncToAppCore(tray);
                    check(widget.Visible&&widget.appWindow==main&&widget.Location==anchorPosition&&widget.offsetX==107&&widget.offsetY==100,"Tray hid or moved widget");
                    widget.SyncToAppCore(main);
                    check(widget.Visible&&widget.appWindow==main&&widget.Location==anchorPosition,"Main restore changed position");
                }
                widget.SyncToAppCore(second);
                recordForeground("second-anchor");
                Point secondPosition=widget.Location;
                check(widget.Visible&&widget.appWindow==second&&secondPosition!=anchorPosition,"Second main anchor failed");
                widget.SyncToAppCore(owned);
                check(widget.Visible&&widget.appWindow==second&&widget.Location==secondPosition,"Owned dialog hid widget or stole the anchor");
                widget.SyncToAppCore(second);
                widget.cachedForegroundName=Path.GetFileNameWithoutExtension(Application.ExecutablePath);
                widget.SyncToAppCore(widget.Handle);
                check(widget.Visible&&widget.appWindow==second,"Widget interaction lost the anchor");
                widget.cachedForegroundName="ChatGPT";
                check(SetProp(second,"CodexStartupAnimationSurfaceOyzy2",tray),"Animation property failed");
                widget.SyncToAppCore(second);
                check(!widget.Visible&&widget.appWindow==second,"Animation suppression failed");
                RemoveProp(second,"CodexStartupAnimationSurfaceOyzy2");
                widget.SyncToAppCore(second);
                check(widget.Visible&&widget.Location==secondPosition,"Animation restore moved widget");
                SetProp(second,"CodexStartupAnimationActiveSurfaceOyzy2",tray);
                foreach(int alpha in new int[]{0,1250,5000,8750,10000}){
                    SetProp(second,"CodexStartupAnimationRevealOpacityOyzy2",new IntPtr(alpha+1));
                    widget.SyncToAppCore(second);
                    check(widget.Visible&&Math.Abs(widget.Opacity-alpha/10000.0)<.0001&&widget.Location==secondPosition,"Synchronized reveal opacity failed");
                    check(GetForegroundWindow()==foregroundBefore,"Reveal opacity stole focus");
                }
                SetProp(second,"CodexStartupAnimationActiveSurfaceOyzy2",new IntPtr(-1));
                SetProp(second,"CodexStartupAnimationRevealOpacityOyzy2",new IntPtr(1));
                widget.SyncToAppCore(second);check(widget.Opacity==1,"Stale reveal marker could leave widget transparent");
                RemoveProp(second,"CodexStartupAnimationActiveSurfaceOyzy2");RemoveProp(second,"CodexStartupAnimationRevealOpacityOyzy2");
                ShowWindow(second,0);widget.SyncToAppCore(second);
                check(!widget.Visible&&widget.appWindow==second,"Hidden main handling failed");
                Point offsetToSave;
                check(!widget.TryGetSavedOffset(out offsetToSave),"Hidden main could corrupt saved offset");
                ShowWindow(second,4);widget.SyncToAppCore(second);
                check(widget.Visible&&widget.Location==secondPosition,"Hidden main restore failed");
                ShowWindow(second,7);widget.SyncToAppCore(second);
                recordForeground("minimized-probe");
                check(IsIconic(second)&&!widget.Visible&&!widget.TryGetSavedOffset(out offsetToSave),"Minimized main could corrupt saved offset");
                widget.SyncToAppCore(tray);
                check(!widget.Visible&&widget.appWindow==second,"Tray resurrected widget for minimized main");
                ShowWindow(second,4);widget.SyncToAppCore(second);
                check(widget.Visible&&widget.Location==secondPosition&&widget.TryGetSavedOffset(out offsetToSave)&&offsetToSave==new Point(107,100),"Minimized main restore changed offset");
                widget.cachedForegroundName="UnrelatedApp";widget.cachedForegroundPid=0;
                widget.SyncToAppCore(IntPtr.Zero);
                check(!widget.Visible&&!widget.lastContentWasApp,"Leaving GPT did not hide widget");
                widget.cachedForegroundName="ChatGPT";widget.cachedForegroundPid=GetCurrentProcessId();
                widget.SyncToAppCore(tray);
                check(!widget.Visible&&widget.appWindow==second,"Tray revived an inactive GPT widget");
                widget.SyncToAppCore(second);
                check(widget.Visible&&widget.Location==secondPosition,"Returning to GPT did not restore widget");
                widget.Close();
                recordForeground("widget-close");
            }
            check(File.ReadAllText(positionPath)==savedPosition,"Test changed saved position");
            File.WriteAllText(Path.Combine(Root,"tray-binding-test.json"),Json.Serialize(new{passed=true,synchronizedRevealOpacityPassed=true,staleRevealFailOpen=true,captionlessMaximizedFrameSupported=true,trayCycles=50,trayKeepsWidgetVisible=true,initialTrayIgnored=true,mainRestoreStable=true,secondMainSupported=true,ownedDialogIgnored=true,animationSuppressionPreserved=true,minimizedPositionProtected=true,inactiveAppRemainsHidden=true,savedPositionUnchanged=true,foregroundBefore=foregroundBefore.ToInt64(),foregroundAfter=GetForegroundWindow().ToInt64(),foregroundStages,foregroundUnchanged=GetForegroundWindow()==foregroundBefore}));
        } finally {
            if(owned!=IntPtr.Zero)DestroyWindow(owned);
            if(tray!=IntPtr.Zero)DestroyWindow(tray);
            if(second!=IntPtr.Zero){RemoveProp(second,"CodexStartupAnimationSurfaceOyzy2");DestroyWindow(second);}
            if(main!=IntPtr.Zero)DestroyWindow(main);
        }
    }
    static void SelfTest() {
        if(!string.Equals(ReadProcessName(GetCurrentProcessId()),Path.GetFileNameWithoutExtension(Application.ExecutablePath),StringComparison.OrdinalIgnoreCase)) throw new Exception("Native foreground process lookup failed");
        int callerThread=Thread.CurrentThread.ManagedThreadId;
        var launch=LaunchProbeAsync(delegate {
            if(Thread.CurrentThread.ManagedThreadId==callerThread) throw new Exception("Probe startup still runs on UI thread");
            Thread.Sleep(350); return new PendingQuery();
        });
        if(launch.IsCompleted) throw new Exception("Delayed startup did not remain asynchronous");
        int responsiveTicks=0;
        using(var responsiveTimer=new System.Windows.Forms.Timer { Interval=50 }) {
            responsiveTimer.Tick+=delegate { responsiveTicks++; };
            responsiveTimer.Start();
            while(!launch.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
            responsiveTimer.Stop();
        }
        if(launch.IsFaulted || responsiveTicks<3) throw new Exception("UI stopped responding during slow probe startup");
        if(!KeepAppVisible(true,IsTransientShell("explorer","Shell_TrayWnd")) ||
           !KeepAppVisible(true,IsTransientShell("StartMenuExperienceHost","Windows.UI.Core.CoreWindow")) ||
           KeepAppVisible(false,IsTransientShell("explorer","Shell_TrayWnd")) ||
           KeepAppVisible(true,IsTransientShell("explorer","CabinetWClass")) ||
           KeepAppVisible(true,IsTransientShell("explorer","Progman")) ||
           KeepAppVisible(true,IsTransientShell("msedge","Chrome_WidgetWin_1"))) throw new Exception("Taskbar/start-menu/content focus classification failed");
        var w=new Dictionary<string,object> { {"usedPercent",20},{"resetsAt",1791283294L},{"windowDurationMins",300} };
        if(!FormatWindow("5小时",w,300,false).Contains("80%")) throw new Exception("Remaining percentage failed");
        if(!FormatWindow("一周",null,10080,true).Contains("不可用")) throw new Exception("Unknown window failed");
        w["usedPercent"]=null;
        if(!FormatWindow("5小时",w,300,false).Contains("不可用")) throw new Exception("Null incorrectly treated as zero");
        w["usedPercent"]=20; w["windowDurationMins"]=60;
        if(!FormatWindow("5小时",w,300,false).Contains("变化")) throw new Exception("Window duration mismatch failed");
        if(PercentDisplay("5小时：剩余 73%  ·  18:41 重置")!="剩余 73%") throw new Exception("Value layout failed");
        if(ResetDisplay("5小时：剩余 73%  ·  18:41 重置")!="18:41 重置") throw new Exception("Reset layout failed");
        using(var font=new Font("Microsoft YaHei UI",10f,FontStyle.Regular))
        using(var bitmap=new Bitmap(410,76)) {
            bitmap.SetResolution(96,96);
            using(var graphics=Graphics.FromImage(bitmap)) {
            var measured=TextRenderer.MeasureText(graphics,"：剩余 100%（12月31日 23:59 重置）",font,new Size(int.MaxValue,int.MaxValue),TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
            double scale=GetDpiForSystem()/96.0;
            if(measured.Width>237*scale || measured.Height>27*scale) throw new Exception("Full text exceeds scaled label bounds: "+measured+" scale="+scale);
            }
        }
        using(var preview=new QuotaWidget(true)) {
            preview.Opacity=0; preview.Show();
            var handle=preview.Handle;
            if((GetWindowLong(handle,-20)&0x80)==0 || (GetWindowLong(handle,-20)&0x40000)!=0) throw new Exception("Tool-window style missing");
            preview.RecreateHandle();
            if((GetWindowLong(preview.Handle,-20)&0x80)==0 || (GetWindowLong(preview.Handle,-20)&0x40000)!=0) throw new Exception("Tool-window style lost after handle recreation");
            var originalPosition=preview.Location;
            var dragPosition=new Point(123,234);
            preview.Location=dragPosition; preview.dragging=true; preview.SyncToApp();
            if(preview.Location!=dragPosition) throw new Exception("App tracking moved the widget during dragging");
            preview.dragging=false;
            preview.Location=originalPosition;
            if(preview.Region==null || preview.Region.IsVisible(0,0) || !preview.Region.IsVisible(preview.ClientSize.Width/2,preview.ClientSize.Height/2)) throw new Exception("Rounded window shape failed");
            using(var currentImage=new Bitmap(preview.Width,preview.Height)) {
                preview.DrawToBitmap(currentImage,new Rectangle(Point.Empty,currentImage.Size));
                if(preview.weekly.Text.Contains("剩余")) AssertRenderedMargins(currentImage,preview.weekly.Top,preview.weekly.Bottom);
                currentImage.Save(Path.Combine(Root,"layout-current-preview.png"));
            }
            preview.primaryText="5小时：剩余 100%（23:59 重置）";
            preview.weeklyText="一周：剩余 100%（12月31日 23:59 重置）";
            preview.lastSuccess=DateTime.UtcNow; preview.Render();
            File.WriteAllText(Path.Combine(Root,"layout-metrics.txt"),"client="+preview.ClientSize+" hide="+preview.hide.Bounds+" font="+preview.hide.Font+" primary="+preview.primary.Bounds+" weekly="+preview.weekly.Bounds);
            Size measured;
            using(var graphics=preview.CreateGraphics()) measured=TextRenderer.MeasureText(graphics,preview.weekly.Text,preview.weekly.Font,new Size(int.MaxValue,int.MaxValue),TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding);
            if(MeasureInk(preview.weekly.Text,preview.weekly.Font,preview.weekly.Height).Right>preview.weekly.Width || measured.Height>preview.weekly.Height) throw new Exception("Rendered text clipped: "+measured+" label="+preview.weekly.Size);
            if(preview.primary.Left!=preview.weekly.Left || preview.primary.Font.Size!=preview.weekly.Font.Size || preview.primary.Font.Size!=preview.primaryTitle.Font.Size) throw new Exception("Colon alignment or font size mismatch");
            if(preview.primary.Right>preview.hide.Left) throw new Exception("First-row label overlaps minimize button");
            string oldReading=preview.primary.Text;
            preview.lastSuccess=DateTime.UtcNow.AddHours(-1); preview.failed=true; preview.Render();
            if(preview.primary.ForeColor!=PrimaryTextColor || preview.primaryTitle.ForeColor!=PrimaryAccent || preview.primary.Text!=oldReading) throw new Exception("Stale reading changed color or text");
            using(var image=new Bitmap(preview.Width,preview.Height)) {
                preview.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                AssertRenderedMargins(image,preview.weekly.Top,preview.weekly.Bottom);
                image.Save(Path.Combine(Root,"layout-preview.png"));
            }
            preview.poll.Dispose(); preview.tips.Dispose();
        }
        File.WriteAllText(Path.Combine(Root,"test-result.txt"),"PASS: asynchronous probe startup with responsive UI timer, tool-window style and handle recreation, percentage, missing/null values, duration, full text, equal rendered margins, drag tracking guard, taskbar/start-menu focus classification");
    }
}

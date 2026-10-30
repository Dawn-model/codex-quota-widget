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
using System.Windows.Automation;

public sealed class TightLabel : Label {
    protected override void OnPaint(PaintEventArgs e) {
        TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,ForeColor,
            TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|TextFormatFlags.SingleLine|TextFormatFlags.VerticalCenter);
    }
}

public sealed class CardTheme {
    public readonly bool Light;
    public readonly Color Surface,CardBackground,DetailBackground,Text,Muted,Track,Line,CardBorder;
    CardTheme(bool light,Color surface) {
        Light=light;Surface=surface;
        CardBackground=light?Color.FromArgb(255, 239, 245):Color.FromArgb(85, 42, 60);
        DetailBackground=light?Color.FromArgb(255, 239, 245):Color.FromArgb(70, 36, 51);
        Text=light?Color.FromArgb(29,31,35):Color.FromArgb(244,245,247);
        Muted=light?Color.FromArgb(45,49,55):Color.FromArgb(173,180,189);
        Track=light?Color.FromArgb(210,214,219):Color.FromArgb(72,78,85);
        Line=light?Color.FromArgb(215,218,224):Color.FromArgb(70,74,81);
        CardBorder=light?Color.FromArgb(167,174,185):Color.FromArgb(67,71,81);
    }
    static Color Mix(Color a,Color b,double amount) {return Color.FromArgb((int)Math.Round(a.R*(1-amount)+b.R*amount),(int)Math.Round(a.G*(1-amount)+b.G*amount),(int)Math.Round(a.B*(1-amount)+b.B*amount));}
    public Color Ring(Color c) {return Light?Mix(c,Color.Black,0.25):c;}
    public bool Same(CardTheme other) {return other!=null&&Light==other.Light&&Surface.ToArgb()==other.Surface.ToArgb();}
    public static CardTheme Parse(string config,bool systemLight) {
        string section="", mode=null,lightSurface=null,darkSurface=null;
        foreach(string raw in (config??"").Split('\n')) {
            string line=raw.Trim();if(line.Length==0||line.StartsWith("#"))continue;
            if(line.StartsWith("[")&&line.EndsWith("]")){section=line.Substring(1,line.Length-2).Trim();continue;}
            var match=Regex.Match(line,@"^([A-Za-z][A-Za-z0-9]*)\s*=\s*[""\x27]([^""\x27]*)[""\x27]");
            if(!match.Success)continue;string key=match.Groups[1].Value,val=match.Groups[2].Value;
            if(section=="desktop"&&key=="appearanceTheme")mode=val.ToLowerInvariant();
            if(section=="desktop.appearanceLightChromeTheme"&&key=="surface")lightSurface=val;
            if(section=="desktop.appearanceDarkChromeTheme"&&key=="surface")darkSurface=val;
        }
        bool light=mode=="light"?true:mode=="dark"?false:systemLight;
        Color surface; string hex=light?lightSurface:darkSurface;
        if(hex==null||!Regex.IsMatch(hex,@"^#[0-9a-fA-F]{6}$"))surface=light?Color.White:Color.FromArgb(17,17,17);
        else surface=Color.FromArgb(Convert.ToInt32(hex.Substring(1,2),16),Convert.ToInt32(hex.Substring(3,2),16),Convert.ToInt32(hex.Substring(5,2),16));
        // A custom surface can be opposite to its theme name; choose readable text from its brightness.
        bool bright=(surface.R*0.2126+surface.G*0.7152+surface.B*0.0722)>150;
        return new CardTheme(bright,surface);
    }
    public static CardTheme Read() {
        bool systemLight=false;
        try {using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")){if(key!=null)systemLight=Convert.ToInt32(key.GetValue("AppsUseLightTheme",0))!=0;}}catch{}
        string home=Environment.GetEnvironmentVariable("CODEX_HOME");
        if(String.IsNullOrWhiteSpace(home))home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        string file=Path.Combine(home,"config.toml");
        return Parse(File.Exists(file)?File.ReadAllText(file):"",systemLight);
    }
}

public static class ResidentBackdrop {
    static readonly string Folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"optional-resident-background");
    sealed class Data { public Bitmap Image; public double[][] Stops; }
    static readonly Task<Data> Load=Task.Run((Func<Data>)LoadFiles);
    [StructLayout(LayoutKind.Sequential)] struct R { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetProp(IntPtr hwnd,string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out R rect);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    static Data LoadFiles() {
        try {
            string image=Path.Combine(Folder,"resident-panorama.png"), config=Path.Combine(Folder,"resident-panorama-config.json");
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(config));
            var rows=data["opacityStops"] as System.Collections.IEnumerable;var stops=new List<double[]>();
            foreach(var row in rows){var a=row as System.Collections.IList;if(a!=null&&a.Count>=2)stops.Add(new[]{Convert.ToDouble(a[0]),Convert.ToDouble(a[1])});}
            if(stops.Count<2)return null;
            using(var stream=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var input=Image.FromStream(stream))return new Data{Image=new Bitmap(input),Stops=stops.ToArray()};
        }catch{return null;}
    }
    public static bool Ready {get{return Load.IsCompleted&&!Load.IsFaulted&&Load.Result!=null;}}
    static double Alpha(Data data,double x) {
        var stops=data.Stops;if(x<=stops[0][0])return stops[0][1];
        for(int i=1;i<stops.Length;i++)if(x<=stops[i][0]){double w=(x-stops[i-1][0])/(stops[i][0]-stops[i-1][0]);return stops[i-1][1]+w*(stops[i][1]-stops[i-1][1]);}
        return stops[stops.Length-1][1];
    }
    public static void Draw(Graphics graphics,IntPtr owner,Rectangle area,double strength=0.20) {
        if(owner==IntPtr.Zero||!Ready||area.Width<=0||area.Height<=0)return;
        try {
            IntPtr surface=GetProp(owner,"CodexStartupAnimationActiveSurfaceOyzy2");R raw;
            if(surface==IntPtr.Zero||!IsWindowVisible(surface)||!GetWindowRect(surface,out raw))return;
            var frame=Rectangle.FromLTRB(raw.Left,raw.Top,raw.Right,raw.Bottom);
            if(frame.Width<=0||frame.Height<=0||!frame.IntersectsWith(area))return;
            DrawFrame(graphics,frame,area,strength);
        }catch{}
    }
    public static void DrawFrame(Graphics graphics,Rectangle frame,Rectangle area,double strength=0.20) {
        if(!Ready||frame.Width<=0||frame.Height<=0)return;
        Data data=Load.Result;float scale=Math.Max((float)frame.Width/data.Image.Width,(float)frame.Height/data.Image.Height);
        int width=(int)Math.Ceiling(data.Image.Width*scale),height=(int)Math.Ceiling(data.Image.Height*scale);
        int x=frame.Right-width-area.Left,y=frame.Top+(frame.Height-height)/2-area.Top;
        double relative=(area.Left+area.Width/2.0-frame.Left)/frame.Width;
        float alpha=(float)Math.Max(0,Math.Min(1,Alpha(data,relative)*strength));
        using(var attributes=new ImageAttributes()) {
            var matrix=new ColorMatrix();matrix.Matrix33=alpha;attributes.SetColorMatrix(matrix);
            graphics.DrawImage(data.Image,new Rectangle(x,y,width,height),0,0,data.Image.Width,data.Image.Height,GraphicsUnit.Pixel,attributes);
        }
    }
}

public sealed class QuotaDetails : Form {
    public string Plan="", Primary="", Weekly="", CreditCount="当前数据未提供", CreditExpiry="当前数据未提供", SubscriptionExpiry="当前数据未提供";
    readonly float scale;
    public IntPtr BackgroundOwner=IntPtr.Zero;
    CardTheme theme=CardTheme.Parse("",false);
    public void ApplyTheme(CardTheme value) {if(value==null)return;theme=value;BackColor=theme.DetailBackground;Invalidate();}
    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color,out bool opaqueBlend);
    static Color SystemAccent() { try {uint color;bool opaque;if(DwmGetColorizationColor(out color,out opaque)==0)return Color.FromArgb(255,(int)((color>>16)&255),(int)((color>>8)&255),(int)(color&255));} catch{} return Color.FromArgb(108,143,189); }
    protected override void WndProc(ref Message m) {base.WndProc(ref m);if(m.Msg==0x0320||m.Msg==0x001A||m.Msg==0x031A)Invalidate();}
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var cp=base.CreateParams; cp.ExStyle|=0x08000080; return cp; } }
    public QuotaDetails(float scale) {
        this.scale=scale; AutoScaleMode=AutoScaleMode.None; FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; StartPosition=FormStartPosition.Manual;
        ClientSize=new Size((int)(350*scale),(int)(215*scale)); BackColor=Color.FromArgb(43,46,54); DoubleBuffered=true;
        AutoScroll=true; var initializedHandle=Handle; UpdatePanelSize();
    }
    public void UpdatePanelSize() {
        int lines=Math.Max(1,(CreditExpiry??"").Split('\n').Length);
        int fullHeight=(int)((238+Math.Max(0,lines-1)*19)*scale);
        int maxHeight=Screen.PrimaryScreen.WorkingArea.Height-(int)(24*scale);
        ClientSize=new Size((int)(350*scale),Math.Min(fullHeight,maxHeight));
        AutoScrollMinSize=new Size(0,fullHeight);
        using(var path=new GraphicsPath()) {float d=14*scale;path.AddArc(0,0,d,d,180,90);path.AddArc(Width-d-1,0,d,d,270,90);path.AddArc(Width-d-1,Height-d-1,d,d,0,90);path.AddArc(0,Height-d-1,d,d,90,90);path.CloseFigure();var old=Region;Region=new Region(path);if(old!=null)old.Dispose();}
        Invalidate();
    }
    void DrawSubtleText(Graphics g,string text,Font font,Brush brush,float x,float y) {
        g.DrawString(text,font,brush,x,y);
        if(!theme.Light) g.DrawString(text,font,brush,x+0.2f*scale,y);
    }
    static string Percent(string text) {var m=Regex.Match(text??"",@"剩余\s+([0-9.]+%)");return m.Success?m.Groups[1].Value:"—";}
    static string Reset(string text) {var m=Regex.Match(text??"",@"（([^）]+)）");return m.Success?m.Groups[1].Value:"重置时间不可用";}
    static Color NumberColor(string text) {var m=Regex.Match(text??"",@"剩余\s+([0-9.]+)%");double n; if(!m.Success||!double.TryParse(m.Groups[1].Value,out n))return Color.LightGray;return n<=25?Color.FromArgb(232,85,95):n<=50?Color.FromArgb(224,181,105):n<=75?Color.FromArgb(105,177,206):Color.FromArgb(102,205,172);}
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;if(theme.Light)g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAlias;
        ResidentBackdrop.Draw(g,BackgroundOwner,Bounds,0.45);
        using(var border=new Pen(SystemAccent(),Math.Max(1,(float)Math.Round(scale))))
        using(var outline=new GraphicsPath()) {
            float inset=Math.Max(2f,(float)Math.Round(2*scale)),diameter=(float)Math.Round(14*scale);
            float right=Width-1-inset,bottom=Height-1-inset;
            outline.AddArc(inset,inset,diameter,diameter,180,90);outline.AddArc(right-diameter,inset,diameter,diameter,270,90);
            outline.AddArc(right-diameter,bottom-diameter,diameter,diameter,0,90);outline.AddArc(inset,bottom-diameter,diameter,diameter,90,90);
            outline.CloseFigure();g.DrawPath(border,outline);
        }
        g.TranslateTransform(AutoScrollPosition.X,AutoScrollPosition.Y-6*scale);
        using(var title=new Font("Microsoft YaHei UI",17*scale,FontStyle.Bold,GraphicsUnit.Pixel))
        using(var label=new Font("Microsoft YaHei UI",15*scale,GraphicsUnit.Pixel))
        using(var body=new Font("Microsoft YaHei UI",14*scale,GraphicsUnit.Pixel))
        using(var big=new Font("Microsoft YaHei UI",30*scale,FontStyle.Bold,GraphicsUnit.Pixel))
        using(var white=new SolidBrush(theme.Text))
        using(var muted=new SolidBrush(theme.Muted))
        using(var pcolor=new SolidBrush(theme.Ring(NumberColor(Primary))))
        using(var wcolor=new SolidBrush(theme.Ring(NumberColor(Weekly)))) {
            g.DrawString((String.IsNullOrEmpty(Plan)?"":Plan+" ")+"使用限额",title,white,20*scale,19*scale);
            if(Plan!="Pro") { DrawSubtleText(g,"5小时",label,muted,20*scale,58*scale); g.DrawString(Percent(Primary),big,pcolor,18*scale,80*scale); DrawSubtleText(g,Reset(Primary),body,muted,20*scale,120*scale); }
            float wx=Plan=="Pro"?20:183; DrawSubtleText(g,"一周",label,muted,wx*scale,58*scale); g.DrawString(Percent(Weekly),big,wcolor,(wx-2)*scale,80*scale); DrawSubtleText(g,Reset(Weekly),body,muted,wx*scale,120*scale);
            using(var line=new Pen(theme.Line,scale))g.DrawLine(line,20*scale,150*scale,330*scale,150*scale);
            DrawSubtleText(g,"重置卡："+CreditCount,body,muted,20*scale,164*scale);
            DrawSubtleText(g,"重置卡到期：",body,muted,20*scale,185*scale);
            DrawSubtleText(g,CreditExpiry,body,muted,112*scale,185*scale);
            int expiryLines=Math.Max(1,(CreditExpiry??"").Split('\n').Length);
            DrawSubtleText(g,"订阅到期："+SubscriptionExpiry,body,muted,20*scale,(185+expiryLines*19)*scale);
        }
    }
}

public sealed class QuotaWidget : Form {
    static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    static readonly string TimerRoot = Path.GetFullPath(Path.Combine(Root,"..","quota-timer"));
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static readonly EventWaitHandle RestoreRequest = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\CodexQuotaWidgetRestoreOyzy2");
    readonly Label primary = new TightLabel(), weekly = new TightLabel();
    readonly Label primaryTitle = new TightLabel(), weeklyTitle = new TightLabel();
    readonly Label hide;
    readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
    readonly ToolTip tips = new ToolTip();
    QuotaDetails details;
    CardTheme cardTheme=CardTheme.Parse("",false);
    bool backgroundLoaded;
    Task<CardTheme> pendingTheme;
    DateTime nextThemeCheck=DateTime.MinValue;
    void ApplyTheme(CardTheme value) {
        if(value==null||value.Same(cardTheme))return;
        cardTheme=value;BackColor=value.CardBackground;cardNeedsPaint=true;Invalidate();
        if(details!=null&&!details.IsDisposed)details.ApplyTheme(value);
    }
    void CheckTheme() {
        if(pendingTheme!=null&&pendingTheme.IsCompleted){try{ApplyTheme(pendingTheme.Result);}catch{}pendingTheme=null;}
        if(pendingTheme==null&&DateTime.UtcNow>=nextThemeCheck){nextThemeCheck=DateTime.UtcNow.AddSeconds(2);pendingTheme=Task.Run((Func<CardTheme>)CardTheme.Read);}
    }
    delegate IntPtr MouseHookProc(int code,IntPtr message,IntPtr data);
    MouseHookProc mouseHookCallback;
    IntPtr mouseHook=IntPtr.Zero;
    [StructLayout(LayoutKind.Sequential)] struct MouseHookData { public int X,Y; public uint MouseData,Flags,Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int kind,MouseHookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    IntPtr OnMouseHook(int code,IntPtr message,IntPtr data) {
        if(code>=0&&(message.ToInt64()==0x201||message.ToInt64()==0x204||message.ToInt64()==0x207)&&details!=null&&!details.IsDisposed) {
            var point=(MouseHookData)Marshal.PtrToStructure(data,typeof(MouseHookData)); RECT card,panel;
            bool onCard=GetWindowRect(Handle,out card)&&point.X>=card.Left&&point.X<card.Right&&point.Y>=card.Top&&point.Y<card.Bottom;
            bool onPanel=GetWindowRect(details.Handle,out panel)&&point.X>=panel.Left&&point.X<panel.Right&&point.Y>=panel.Top&&point.Y<panel.Bottom;
            if(!onCard&&!onPanel) {try {BeginInvoke((Action)delegate {CloseDetails();});}catch{}}
        }
        return CallNextHookEx(mouseHook,code,message,data);
    }

    readonly ToolStripMenuItem pin = new ToolStripMenuItem("置顶");
    sealed class PendingQuery {
        public string Primary, Weekly, CreditCount, CreditExpiry, Plan;
    }
    Task<PendingQuery> startingQuery;
    uint cachedForegroundPid;
    string cachedForegroundName="";
    double maxTickMs;
    double maxSyncMs,maxRenderMs,maxMetadataMs,maxPositionMs,maxShowMs;
    DateTime nextQuery = DateTime.MinValue, lastSuccess = DateTime.MinValue;
    string primaryText = "5小时：—", weeklyText = "一周：—", creditCount="当前数据未提供", creditExpiry="当前数据未提供", subscriptionExpiry="当前数据未提供";
    string plan=""; Task<string[]> creditTask; DateTime nextCredit=DateTime.MinValue; string creditCountForDetails="";
    bool failed;
    bool userHidden;
    bool dragging; bool pointerDown; Point pointerOrigin, windowOrigin;
    bool appActiveNow;
    bool lastContentWasApp;
    string lastLayoutKey="";
    string lastTooltip="";
    bool cardNeedsPaint=true;
    double cardReveal=1;
    [StructLayout(LayoutKind.Sequential)] struct LayerPoint { public int X,Y; public LayerPoint(int x,int y){X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential)] struct LayerSize { public int Width,Height; public LayerSize(int w,int h){Width=w;Height=h;} }
    [StructLayout(LayoutKind.Sequential,Pack=1)] struct LayerBlend { public byte Operation,Flags,Opacity,AlphaFormat; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UpdateLayeredWindow(IntPtr window,IntPtr destination,ref LayerPoint point,ref LayerSize size,IntPtr source,ref LayerPoint sourcePoint,int colorKey,ref LayerBlend blend,int flags);
    void PaintLayer() {
        if(previewMode||!Visible||!IsHandleCreated||Width<=0||Height<=0)return;
        try {
            using(var bitmap=new Bitmap(Width,Height,PixelFormat.Format32bppPArgb)) {
                using(var g=Graphics.FromImage(bitmap)) {
                    g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;if(cardTheme.Light)g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using(var shape=RoundedBorder(Width,Height)) {
                        g.SetClip(shape);
                        using(var backdrop=new SolidBrush(Color.FromArgb(110,cardTheme.CardBackground)))g.FillRectangle(backdrop,0,0,Width,Height);
                        if(buttonBounds.HasValue) {
                            Rectangle button=buttonBounds.Value;button.Offset(-Left,-Top);button=Rectangle.Intersect(new Rectangle(0,0,Width,Height),button);
                            if(button.Width>0&&button.Height>0){
                                var state=g.Save();g.SetClip(button,CombineMode.Intersect);
                                using(var mask=new SolidBrush(cardTheme.CardBackground))g.FillRectangle(mask,button);
                                ResidentBackdrop.Draw(g,appWindow,Bounds);g.Restore(state);
                            }
                        }
                        g.ResetClip();DrawCard(g);
                        using(var pen=new Pen(Color.FromArgb(100,cardTheme.CardBorder),Math.Max(1f,GetDpiForSystem()/96f)))
                        using(var outline=RoundedBorder(Width,Height,Math.Max(1f,GetDpiForSystem()/192f)))g.DrawPath(pen,outline);
                    }
                }
                IntPtr screen=GetDC(IntPtr.Zero),memory=CreateCompatibleDC(screen),hbitmap=IntPtr.Zero,previous=IntPtr.Zero;
                try {
                    hbitmap=bitmap.GetHbitmap(Color.FromArgb(0));previous=SelectObject(memory,hbitmap);
                    var where=new LayerPoint(Left,Top);var size=new LayerSize(Width,Height);var origin=new LayerPoint(0,0);
                    var blend=new LayerBlend{Operation=0,Flags=0,Opacity=(byte)Math.Max(0,Math.Min(255,(int)Math.Round(cardReveal*255))),AlphaFormat=1};
                    if(!UpdateLayeredWindow(Handle,screen,ref where,ref size,memory,ref origin,0,ref blend,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    cardNeedsPaint=false;
                } finally {if(previous!=IntPtr.Zero)SelectObject(memory,previous);if(hbitmap!=IntPtr.Zero)DeleteObject(hbitmap);if(memory!=IntPtr.Zero)DeleteDC(memory);if(screen!=IntPtr.Zero)ReleaseDC(IntPtr.Zero,screen);}
            }
        }catch(Exception ex){try{File.WriteAllText(Path.Combine(Root,"card-layer-error.txt"),ex.Message);}catch{}}
    }
    IntPtr appWindow=IntPtr.Zero;
    Task<Tuple<IntPtr,Rectangle,Rectangle?>> locateButton;
    DateTime nextButtonSearch=DateTime.MinValue;
    Rectangle? buttonBounds;
    IntPtr buttonWindow=IntPtr.Zero;
    RECT buttonFrame;
    static Rectangle? FindUsageButton(IntPtr window) {
        try {
            var root=AutomationElement.FromHandle(window);
            if(root==null)return null;
            var condition=new AndCondition(new PropertyCondition(AutomationElement.NameProperty,"用量"),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
            var match=root.FindFirst(TreeScope.Descendants,condition);
            if(match==null||match.Current.IsOffscreen)return null;
            var r=match.Current.BoundingRectangle;
            if(r.IsEmpty||r.Width<4||r.Height<4)return null;
            return Rectangle.FromLTRB((int)Math.Round(r.Left),(int)Math.Round(r.Top),(int)Math.Round(r.Right),(int)Math.Round(r.Bottom));
        }catch{return null;}
    }
    bool TryButtonBounds(RECT frame,out Rectangle bounds) {
        bool moved=buttonWindow!=appWindow||buttonFrame.Left!=frame.Left||buttonFrame.Top!=frame.Top||buttonFrame.Right!=frame.Right||buttonFrame.Bottom!=frame.Bottom;
        if(moved){buttonBounds=null; buttonWindow=appWindow;buttonFrame=frame;nextButtonSearch=DateTime.MinValue;}
        if(locateButton!=null&&locateButton.IsCompleted){
            try {var result=locateButton.Result; var current=Rectangle.FromLTRB(frame.Left,frame.Top,frame.Right,frame.Bottom); if(result.Item1==appWindow && result.Item2==current) { buttonBounds=result.Item3; try {File.WriteAllText(Path.Combine(Root,"button-probe.txt"),result.Item3.HasValue?result.Item3.Value.ToString():"button not found");}catch{} }}catch{buttonBounds=null;}
            locateButton=null;
        }
        if(locateButton==null&&DateTime.UtcNow>=nextButtonSearch){
            IntPtr handle=appWindow; var captured=Rectangle.FromLTRB(frame.Left,frame.Top,frame.Right,frame.Bottom);
            locateButton=Task.Run(delegate {return Tuple.Create(handle,captured,FindUsageButton(handle));});
            nextButtonSearch=DateTime.UtcNow.AddSeconds(1);
        }
        bounds=buttonBounds.GetValueOrDefault();
        return buttonBounds.HasValue&&bounds.Left>=frame.Left&&bounds.Right<=frame.Right&&bounds.Top>=frame.Top&&bounds.Bottom<=frame.Bottom;
    }

    readonly bool previewMode;
    int offsetX=-1, offsetY=100;
    int magnetX=106,magnetY=1751,magnetRadiusDip=28;
    bool magnetSnapped,magnetBottom;
    Point MagnetOffset(Size frame) {return FitOffset(magnetX,magnetBottom?Math.Max(0,frame.Height-Height):magnetY,frame,Size);}
    static Point FitOffset(int x,int y,Size frame,Size widget) {
        if(x<0)x=frame.Width-widget.Width-24;
        return new Point(Math.Max(0,Math.Min(x,Math.Max(0,frame.Width-widget.Width))),Math.Max(0,Math.Min(y,Math.Max(0,frame.Height-widget.Height))));
    }
    static bool NearMagnet(Point dropped,Point target,int radius) {
        long dx=(long)dropped.X-target.X,dy=(long)dropped.Y-target.Y;
        return dx*dx+dy*dy<=(long)radius*radius;
    }
    void FinishDrag() {
        RECT rect;
        if(appWindow!=IntPtr.Zero&&IsMainAppFrame(appWindow)&&IsWindowVisible(appWindow)&&!IsIconic(appWindow)&&GetWindowRect(appWindow,out rect)) {
            var frame=new Size(rect.Right-rect.Left,rect.Bottom-rect.Top);
            var dropped=FitOffset(Left-rect.Left,Top-rect.Top,frame,Size);
            magnetSnapped=false;offsetX=dropped.X;offsetY=dropped.Y;
            Location=new Point(rect.Left+dropped.X,rect.Top+dropped.Y);
        }
        SavePosition();
    }
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
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr hwnd,out uint key,out byte alpha,out uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out RECT value,int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd,int attribute,out int value,int size);
    delegate bool EnumProc(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback,IntPtr data);
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
            cp.ExStyle=(cp.ExStyle | 0x00000080 | 0x08000000 | 0x00080000) & ~0x00040000;
            return cp;
        }
    }

    [STAThread] public static void Main(string[] args) {
        if(args.Length==2&&args[0]=="--alt-switcher-test") {
            try {
                if(!IgnoreAltSwitcher(true,true,"explorer","XamlExplorerHostIslandWindow"))throw new Exception("switcher was not ignored");
                if(IgnoreAltSwitcher(false,true,"explorer","XamlExplorerHostIslandWindow")||IgnoreAltSwitcher(true,false,"explorer","XamlExplorerHostIslandWindow")||IgnoreAltSwitcher(true,true,"other","XamlExplorerHostIslandWindow")||IgnoreAltSwitcher(true,true,"explorer","CabinetWClass"))throw new Exception("ordinary window was ignored");
                File.WriteAllText(args[1],"PASS: active Alt+Tab ignored; unrelated windows preserved");
            }catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;
        }
        if(args.Length==2&&args[0]=="--underlay-test") {
            try {
                for(int i=0;i<300&&!ResidentBackdrop.Ready;i++)Thread.Sleep(20);
                if(!ResidentBackdrop.Ready)throw new Exception("resident image or configuration unavailable");
                using(var image=new Bitmap(100,390))using(var graphics=Graphics.FromImage(image)) {
                    graphics.Clear(Color.FromArgb(30,30,30));
                    ResidentBackdrop.DrawFrame(graphics,new Rectangle(0,0,2808,1790),new Rectangle(0,1300,100,390));
                    bool changed=false;for(int y=0;y<390&&!changed;y+=10)for(int x=0;x<100;x+=10)if(image.GetPixel(x,y).ToArgb()!=Color.FromArgb(30,30,30).ToArgb()){changed=true;break;}
                    if(!changed)throw new Exception("resident texture did not render");
                }
                File.WriteAllText(args[1],"PASS: resident image loaded and aligned texture rendered");
            }catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;
        }
        if(args.Length==2&&args[0]=="--theme-test") {
            try {
                var l=CardTheme.Parse("[desktop]\nappearanceTheme = \"light\"\n[desktop.appearanceLightChromeTheme]\nsurface = \"#faf7f3\"",false);
                var d=CardTheme.Parse("[desktop]\nappearanceTheme = \"dark\"\n[desktop.appearanceDarkChromeTheme]\nsurface = \"#121822\"",true);
                var sys=CardTheme.Parse("[desktop]\nappearanceTheme = \"system\"",true);
                if(!l.Light||l.Surface.R!=250||l.Surface.G!=247||d.Light||d.Surface.B!=34||!sys.Light)throw new Exception("theme parsing failed");
                if(l.Text.R<=200&&d.Text.R>=200&&l.CardBackground.R>200&&d.CardBackground.R<100)File.WriteAllText(args[1],"PASS: explicit light/dark, custom surfaces and system following");
                else throw new Exception("theme contrast failed");
            }catch(Exception e){File.WriteAllText(args[1],"FAIL: "+e);Environment.ExitCode=1;}return;
        }
        if(args.Length==3&&args[0]=="--theme-preview") {
            bool light=args[1]=="light";var value=CardTheme.Parse("[desktop]\nappearanceTheme = \""+args[1]+"\"",false);
            using(var preview=new QuotaWidget(true)) {
                preview.ApplyTheme(value);preview.primaryText="5小时：剩余 84%（07:00 重置）";preview.weeklyText="一周：剩余 33%（11月4日 18:00 重置）";preview.Render();
                using(var bitmap=new Bitmap(preview.Width,preview.Height)){preview.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[2],ImageFormat.Png);}
            }return;
        }
        if(args.Length==3&&args[0]=="--theme-detail-preview") {
            var value=CardTheme.Parse("[desktop]\nappearanceTheme = \""+args[1]+"\"",false);
            using(var preview=new QuotaDetails(2f)) {
                preview.Primary="5小时：剩余 84%（07:00 重置）";preview.Weekly="一周：剩余 33%（11月4日 18:00 重置）";
                preview.CreditCount="2张可用";preview.CreditExpiry="11月7日 06:33\n11月8日 06:33";preview.SubscriptionExpiry="2026年11月14日 11:17";preview.ApplyTheme(value);preview.UpdatePanelSize();
                using(var bitmap=new Bitmap(preview.Width,preview.Height)){preview.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[2],ImageFormat.Png);}
            }return;
        }
        if(args.Length==2&&args[0]=="--render-details") {
            using(var preview=new QuotaDetails(2f)) {
                preview.Primary="5小时：剩余 85%（15:18 重置）";preview.Weekly="一周：剩余 30%（11月4日 14:36 重置）";
                preview.CreditCount="2张可用";preview.CreditExpiry="11月7日 06:33\n11月8日 06:33";preview.UpdatePanelSize();
                using(var bitmap=new Bitmap(preview.Width,preview.Height)) {preview.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[1],ImageFormat.Png);}
            } return;
        }
        if(args.Length==2&&args[0]=="--credit-test") {
            try {
                var sample=Json.Deserialize<Dictionary<string,object>>("{\"availableCount\":2,\"credits\":[{\"expiresAt\":1794090813},{\"expiresAt\":1794004413}]}");
                var values=FormatCredits(sample); if(values[0]!="2张可用"||values[1]!="11月7日 06:33\n11月8日 06:33")throw new Exception("expiry ordering failed: "+values[1]);
                var missing=Json.Deserialize<Dictionary<string,object>>("{\"availableCount\":1,\"credits\":null}");
                if(!FormatCredits(missing)[1].Contains("未提供"))throw new Exception("missing expiry failed");
                File.WriteAllText(args[1],"PASS: sorted dates and missing details");
            }catch(Exception ex){File.WriteAllText(args[1],"FAIL: "+ex);Environment.ExitCode=1;}return;
        }
        if(args.Length==2&&(args[0]=="--render-card"||args[0]=="--render-long-card")) {
            using(var preview=new QuotaWidget(true)) {
                preview.primaryText="5小时：剩余 84%（07:00 重置）";
                preview.weeklyText=args[0]=="--render-long-card"?"一周：剩余 33%（12月31日 23:59 重置）":"一周：剩余 33%（11月4日 18:00 重置）";
                preview.Render();
                using(var bitmap=new Bitmap(preview.Width,preview.Height)) { preview.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(args[1],ImageFormat.Png); }
            } return;
        }
        if(args.Length==3&&args[0]=="--wake-duration-test"){try{NativeQuotaWake.DurationTest(Int32.Parse(args[1]),args[2]);}catch(Exception e){File.WriteAllText(args[2],"FAIL: "+e.ToString());Environment.ExitCode=1;}return;}
        if(args.Length>0&&args[0]=="--native-wake-self-test"){try{NativeQuotaWake.SelfTest(Path.Combine(Root,"native-wake-test.txt"));}catch(Exception e){File.WriteAllText(Path.Combine(Root,"native-wake-test.txt"),"FAIL: "+e.ToString());Environment.ExitCode=1;}return;}
        if(args.Length>0&&args[0]=="--tray-test"){
            try{TrayBindingTest();}catch(Exception e){File.WriteAllText(Path.Combine(Root,"tray-binding-test.json"),Json.Serialize(new{passed=false,error=e.ToString()}));Environment.ExitCode=1;}return;
        }
        if(args.Length > 0 && args[0] == "--self-test") {
            try { SelfTest(); } catch(Exception e) { File.WriteAllText(Path.Combine(Root,"test-result.txt"),"FAIL: "+e.Message); Environment.ExitCode=1; }
            return;
        }
        bool created;
        using(var single = new Mutex(true, "Local\\CodexQuotaWidgetOyzy2", out created)) {
            if(!created) { RestoreRequest.Set(); return; }
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
        ClientSize=new Size(50,196); BackColor=Color.FromArgb(31,33,39);
        DoubleBuffered=true;
        ForeColor=Color.WhiteSmoke; TopMost=false; ShowInTaskbar=false;
        StartPosition=FormStartPosition.Manual;
        Rectangle area=Screen.PrimaryScreen.WorkingArea;
        Location=new Point(area.Right-Width-28,area.Top+100);
        LoadCachedDisplay();
        var menu=new ContextMenuStrip(); menu.Font=new Font("Microsoft YaHei UI",9f,FontStyle.Regular);
        menu.Items.Add("立即刷新",null,delegate { nextQuery=DateTime.MinValue; });
        pin.Text="随GPT窗口显示";pin.Checked=true;pin.Enabled=false;
        menu.Items.Add(pin);
        menu.Items.Add("恢复显示（仅GPT内）",null,delegate { userHidden=false; nextQuery=DateTime.MinValue; SyncToApp(); });
        menu.Items.Add("收起",null,delegate { userHidden=true; Hide(); });
        menu.Items.Add("退出",null,delegate { Close(); });
        ContextMenuStrip=menu;
        primaryTitle.Text="5小时"; weeklyTitle.Text="一  周";
        SetupLabel(primaryTitle,11,8,38); SetupLabel(weeklyTitle,11,40,38);
        SetupLabel(primary,49,8,237); SetupLabel(weekly,49,40,237);
        hide=new Label { Text="−", Location=new Point(276,-2),Size=new Size(18,20),Cursor=Cursors.Hand,BackColor=Color.Transparent,
            ForeColor=Color.FromArgb(133,139,151),Font=new Font("Segoe UI",8.64f),TextAlign=ContentAlignment.MiddleCenter };
        hide.Click+=delegate { userHidden=true; Hide(); }; tips.SetToolTip(hide,"收起；在聊天中说“打开额度状态栏”即可请求恢复");
        Controls.Add(hide);
        primaryTitle.Visible=false; weeklyTitle.Visible=false; primary.Visible=false; weekly.Visible=false; hide.Visible=false;
        // A short click opens details; a movement beyond the system threshold drags.
        MouseDown+=delegate(object sender,MouseEventArgs e) {if(e.Button==MouseButtons.Left){pointerDown=true;dragging=false;pointerOrigin=Cursor.Position;windowOrigin=Location;Capture=true;}};
        MouseMove+=delegate(object sender,MouseEventArgs e) {if(!pointerDown)return;Point current=Cursor.Position;int dx=current.X-pointerOrigin.X,dy=current.Y-pointerOrigin.Y;if(!dragging && (Math.Abs(dx)>=SystemInformation.DragSize.Width/2 || Math.Abs(dy)>=SystemInformation.DragSize.Height/2)){dragging=true;CloseDetails();}if(dragging)Location=new Point(windowOrigin.X+dx,windowOrigin.Y+dy);};
        MouseUp+=delegate(object sender,MouseEventArgs e) {if(e.Button!=MouseButtons.Left||!pointerDown)return;pointerDown=false;Capture=false;if(dragging){FinishDrag();dragging=false;SyncToApp();}else ToggleDetails();};
        Paint+=delegate(object s,PaintEventArgs e) {
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            ResidentBackdrop.Draw(e.Graphics,appWindow,Bounds);
            DrawCard(e.Graphics);
            float borderWidth=Math.Max(1f,GetDpiForSystem()/96f);
            using(var p=new Pen(cardTheme.CardBorder,borderWidth))
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
        float dpiScale=GetDpiForSystem()/96f*0.96f;
        if(dpiScale!=1f) Scale(new SizeF(dpiScale,dpiScale));
        UpdateShape();
        LoadPosition();
        Render();
        FormClosed+=delegate { CloseDetails(); if(!previewMode) NativeQuotaWake.Dispose(); poll.Stop(); poll.Dispose(); SavePosition(); tips.Dispose();
            if(focusHook!=IntPtr.Zero) UnhookWinEvent(focusHook);
            if(minimizeHook!=IntPtr.Zero) UnhookWinEvent(minimizeHook);
            if(moveHook!=IntPtr.Zero) UnhookWinEvent(moveHook);
            // An in-flight read-only query finishes normally and cleans up its app-server.
            if(startingQuery!=null) startingQuery.ContinueWith(delegate(Task<PendingQuery> t) {
                if(t.IsFaulted) { var ignored=t.Exception; }
            }); };
    }
    void SetupLabel(Label label,int x,int y,int width) {
        label.SetBounds(x,y,width,27); label.Font=new Font("Microsoft YaHei UI",9.6f,FontStyle.Regular);
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
            FinishDrag(); dragging=false;
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
        try {var anchor=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"magnet.json")));
            magnetX=Convert.ToInt32(anchor["offsetX"]);magnetY=Convert.ToInt32(anchor["offsetY"]);
            if(anchor.ContainsKey("bottomAligned"))magnetBottom=Convert.ToBoolean(anchor["bottomAligned"]);
            if(anchor.ContainsKey("radiusDip"))magnetRadiusDip=Math.Max(4,Math.Min(100,Convert.ToInt32(anchor["radiusDip"])));
        }catch{}

        try { var saved=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Root,"sidebar-position.json")));
            if(saved.ContainsKey("magnetSnapped"))magnetSnapped=Convert.ToBoolean(saved["magnetSnapped"]);
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
            savedOffset=magnetSnapped?new Point(magnetX,magnetY):new Point(Left-rect.Left,Top-rect.Top);
        }
        return true;
    }
    void SavePosition() { if(previewMode)return;try {
        Point savedOffset;
        if(!TryGetSavedOffset(out savedOffset))return;
        offsetX=savedOffset.X;offsetY=savedOffset.Y;
        File.WriteAllText(Path.Combine(Root,"sidebar-position.json"),Json.Serialize(new { x=Left,y=Top,offsetX=offsetX,offsetY=offsetY,magnetSnapped=magnetSnapped }));
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
    DateTime nextAnchorSearch=DateTime.MinValue;
    void DiscoverVisibleAnchor(){
        if(previewMode||IsMainAppFrame(appWindow)||DateTime.UtcNow<nextAnchorSearch)return;
        nextAnchorSearch=DateTime.UtcNow.AddSeconds(1);
        EnumWindows(delegate(IntPtr window,IntPtr ignored){
            if(!IsMainAppFrame(window)||!IsWindowVisible(window)||IsIconic(window))return true;
            uint pid;GetWindowThreadProcessId(window,out pid);string name=ReadProcessName(pid);
            if(!string.Equals(name,"Codex",StringComparison.OrdinalIgnoreCase)&&!string.Equals(name,"ChatGPT",StringComparison.OrdinalIgnoreCase))return true;
            appWindow=window;return false;
        },IntPtr.Zero);
    }
    static Rectangle NativeRect(IntPtr w){RECT r;GetWindowRect(w,out r);return Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);}
    static Rectangle VisualBounds(IntPtr window){
        RECT rect;if(DwmGetWindowAttribute(window,9,out rect,16)!=0||rect.Right<=rect.Left||rect.Bottom<=rect.Top)return NativeRect(window);
        return Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
    }
    static Rectangle OpaqueBounds(IntPtr window){
        if(!IsWindowVisible(window)||IsIconic(window))return Rectangle.Empty;
        int cloak;if(DwmGetWindowAttribute(window,14,out cloak,4)==0&&cloak!=0)return Rectangle.Empty;
        int style=GetWindowLong(window,-20);if((style&0x20)!=0)return Rectangle.Empty;
        if((style&0x80000)!=0){uint key,flags;byte alpha;if(!GetLayeredWindowAttributes(window,out key,out alpha,out flags)||(flags&1)!=0||(flags&2)!=0&&alpha<255)return Rectangle.Empty;}
        return VisualBounds(window);
    }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    static bool IgnoreAltSwitcher(bool altDown,bool isForeground,string process,string cls) {
        return altDown&&isForeground&&String.Equals(process,"explorer",StringComparison.OrdinalIgnoreCase)&&cls=="XamlExplorerHostIslandWindow";
    }
    static bool IsAltSwitcherOccluder(IntPtr window) {
        if(window!=GetForegroundWindow()||(GetAsyncKeyState(0x12)&0x8000)==0)return false;
        var name=new StringBuilder(256);GetClassName(window,name,name.Capacity);uint pid;GetWindowThreadProcessId(window,out pid);
        return IgnoreAltSwitcher(true,true,ReadProcessName(pid),name.ToString());
    }
    bool AppFullyOccluded(){
        // Use the compositor's coordinate space for both target and occluders;
        // mixing it with virtualized GetWindowRect/client coordinates breaks at 200% DPI.
        var bounds=VisualBounds(appWindow);
        if(bounds.Width<=0||bounds.Height<=0)return true;
        var visible=new List<Rectangle>{bounds};
        var surface=GetProp(appWindow,"CodexStartupAnimationActiveSurfaceOyzy2");
        for(IntPtr w=GetWindow(appWindow,3);w!=IntPtr.Zero;w=GetWindow(w,3)){
            if(w==Handle||w==surface||IsAltSwitcherOccluder(w))continue;
            var covered=Rectangle.Intersect(bounds,OpaqueBounds(w));if(covered.Width<=0||covered.Height<=0)continue;
            var next=new List<Rectangle>();foreach(var area in visible){var overlap=Rectangle.Intersect(area,covered);if(overlap.Width<=0||overlap.Height<=0){next.Add(area);continue;}
                if(overlap.Top>area.Top)next.Add(Rectangle.FromLTRB(area.Left,area.Top,area.Right,overlap.Top));
                if(overlap.Bottom<area.Bottom)next.Add(Rectangle.FromLTRB(area.Left,overlap.Bottom,area.Right,area.Bottom));
                if(overlap.Left>area.Left)next.Add(Rectangle.FromLTRB(area.Left,overlap.Top,overlap.Left,overlap.Bottom));
                if(overlap.Right<area.Right)next.Add(Rectangle.FromLTRB(overlap.Right,overlap.Top,area.Right,overlap.Bottom));
            }visible=next;if(visible.Count==0)return true;
        }return false;
    }
    void StackWithApp(){
        // Above the resident portrait, below every unrelated window above GPT.
        IntPtr anchor=GetProp(appWindow,"CodexStartupAnimationActiveSurfaceOyzy2");
        if(anchor==IntPtr.Zero||!IsWindowVisible(anchor))anchor=appWindow;
        IntPtr previous=GetWindow(anchor,3);if(previous==Handle)return;
        if(previous!=IntPtr.Zero&&(GetWindowLong(previous,-20)&8)!=0)previous=IntPtr.Zero;
        SetWindowPos(Handle,previous,0,0,0,0,0x1|0x2|0x10|0x200);
    }
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
        DiscoverVisibleAnchor();
        RECT rect;
        appActive=IsMainAppFrame(appWindow)&&IsWindowVisible(appWindow)&&!IsIconic(appWindow);
        if(appActive)appActive=!AppFullyOccluded();
        appActiveNow=appActive;
        var startupSurface=appWindow==IntPtr.Zero?IntPtr.Zero:GetProp(appWindow,"CodexStartupAnimationSurfaceOyzy2");
        // Suppress display only, preserving position, userHidden and refresh.
        // A crashed animation loses its HWND and cannot leave us hidden.
        bool startupPlaying=startupSurface!=IntPtr.Zero&&IsWindow(startupSurface);
        if(userHidden || startupPlaying || !appActive || !IsMainAppFrame(appWindow) || !IsWindowVisible(appWindow) || IsIconic(appWindow) || !GetWindowRect(appWindow,out rect)) { CloseDetails(); if(Visible) Hide(); return; }
        Rectangle button;
        if(!TryButtonBounds(rect,out button)){ CloseDetails(); if(Visible)Hide(); return; }
        var positionWatch=Stopwatch.StartNew();
        Point relative;
        if(offsetX>=0) relative=FitOffset(offsetX,offsetY,new Size(rect.Right-rect.Left,rect.Bottom-rect.Top),Size);
        else relative=FitOffset(button.Right-rect.Left+(int)Math.Round(12*GetDpiForSystem()/96.0),button.Bottom-rect.Top-Height,new Size(rect.Right-rect.Left,rect.Bottom-rect.Top),Size);
        var targetPosition=new Point(rect.Left+relative.X,rect.Top+relative.Y);
        if(Location!=targetPosition){ Location=targetPosition;cardNeedsPaint=true;Invalidate(); }
        if(details!=null&&!details.IsDisposed) {var pos=new Point(Right+(int)(6*ClientSize.Width/50f), Math.Max(rect.Top,Bottom-details.Height));if(details.Location!=pos){details.Location=pos;details.Invalidate();}}
        // The renderer publishes its actual background reveal alpha, so the
        // quota fades on the same frames instead of using a separate clock.
        var activeSurface=GetProp(appWindow,"CodexStartupAnimationActiveSurfaceOyzy2");
        long reveal=GetProp(appWindow,"CodexStartupAnimationRevealOpacityOyzy2").ToInt64();
        double targetOpacity=activeSurface!=IntPtr.Zero&&IsWindow(activeSurface)&&reveal>=1&&reveal<=10001?(reveal-1)/10000.0:1;
        if(Math.Abs(cardReveal-targetOpacity)>.00001){cardReveal=targetOpacity;cardNeedsPaint=true;}
        maxPositionMs=Math.Max(maxPositionMs,positionWatch.Elapsed.TotalMilliseconds);
        if(!Visible) {
            var showWatch=Stopwatch.StartNew();
            Show();cardNeedsPaint=true;
            maxShowMs=Math.Max(maxShowMs,showWatch.Elapsed.TotalMilliseconds);
        }
        StackWithApp();
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
        if(RestoreRequest.WaitOne(0)) { userHidden=false; nextQuery=DateTime.MinValue; }
        CheckTheme();
        if(!backgroundLoaded&&ResidentBackdrop.Ready){backgroundLoaded=true;cardNeedsPaint=true;Invalidate();if(details!=null&&!details.IsDisposed)details.Invalidate();}
        SyncToApp();
        if(startingQuery!=null && startingQuery.IsCompleted) {
            try {
                var ready=startingQuery.Result;
                primaryText=ready.Primary; weeklyText=ready.Weekly; if(plan!=ready.Plan) { plan=ready.Plan; int height=(int)Math.Round((plan=="Pro"?104:196)*ClientSize.Width/50.0); ClientSize=new Size(ClientSize.Width,height); UpdateShape(); cardNeedsPaint=true; } if(ready.CreditCount!="当前数据未提供" && creditCountForDetails==""){creditCount=ready.CreditCount;creditExpiry=ready.CreditExpiry;}
                lastSuccess=DateTime.UtcNow; failed=false;
            } catch { failed=true; nextQuery=DateTime.UtcNow.AddSeconds(5); }
            startingQuery=null;
            WriteStatus();
        }
        if(creditTask!=null && creditTask.IsCompleted) { try {var c=creditTask.Result; if(c!=null){creditCount=c[0];creditExpiry=c[1];subscriptionExpiry=c[2];creditCountForDetails=c[0];}} catch {} creditTask=null; }
        if(creditTask==null && DateTime.UtcNow>=nextCredit) { nextCredit=DateTime.UtcNow.AddMinutes(5); creditTask=Task.Run((Func<string[]>)ReadCreditDetails); }
        // Reading limits is independent of whether the companion window is visible.
        // Keep a single in-flight query, including while GPT is in the background.
        if(startingQuery==null && DateTime.UtcNow>=nextQuery) StartQuery();
        var renderWatch=Stopwatch.StartNew();
        Render();
        if(cardNeedsPaint&&Visible)PaintLayer();
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
    // Use the timer's existing 15-second cache without starting PowerShell.
    // A cache read must never advance capturedAt or hide a failed fresh query.
    static PendingQuery ParseRecentQuery(string json,long now) {
        try {
            var snapshot=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            if(snapshot==null || !snapshot.ContainsKey("capturedAt") || snapshot["capturedAt"]==null) return null;
            long captured=Convert.ToInt64(snapshot["capturedAt"]);
            if(now-captured>15 || captured-now>3) return null;
            var limits=GetWindow(snapshot,"limits");
            foreach(string key in (PlanName(limits)=="Pro" ? new[] { "secondary" } : new[] { "primary","secondary" })) {
                var window=GetWindow(limits,key);
                if(window==null || !window.ContainsKey("usedPercent") || window["usedPercent"]==null ||
                   !window.ContainsKey("resetsAt") || window["resetsAt"]==null) return null;
                double used=Convert.ToDouble(window["usedPercent"]);
                if(double.IsNaN(used) || double.IsInfinity(used) || used<0 || used>100) return null;
            }
            string p=FormatWindow("5小时",GetWindow(limits,"primary"),300,false);
            string w=FormatWindow("一周",GetWindow(limits,"secondary"),10080,true);
            if((PlanName(limits)!="Pro" && !p.Contains("剩余")) || !w.Contains("剩余")) return null;
            var credits=FormatCredits(GetWindow(limits,"rateLimitResetCredits"));
            return new PendingQuery { Primary=p,Weekly=w,CreditCount=credits[0],CreditExpiry=credits[1],Plan=PlanName(limits) };
        } catch { return null; }
    }
    static string PlanName(Dictionary<string,object> data) {object v; if(data==null||!data.TryGetValue("planType",out v)||v==null)return ""; string p=Convert.ToString(v); if(p.Equals("plus",StringComparison.OrdinalIgnoreCase))return "Plus";if(p.Equals("pro",StringComparison.OrdinalIgnoreCase))return "Pro";return String.IsNullOrWhiteSpace(p)?"":System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p.ToLowerInvariant());}
    static string[] FormatCredits(Dictionary<string,object> summary) {
        try {
            if(summary==null||!summary.ContainsKey("availableCount")||summary["availableCount"]==null)return new[]{"当前数据未提供","当前数据未提供"};
            int count=Convert.ToInt32(summary["availableCount"]); if(count<0)return new[]{"当前数据未提供","当前数据未提供"};
            string countText=count+"张可用"; if(count==0)return new[]{countText,"暂无可用重置卡"};
            object raw; if(!summary.TryGetValue("credits",out raw)||raw==null)return new[]{countText,"到期时间未提供"};
            var items=raw as System.Collections.IEnumerable; if(items==null)return new[]{countText,"到期时间未提供"};
            var expiryTimes=new List<long>();int entries=0, unknown=0;
            foreach(var item in items) {var row=item as Dictionary<string,object>;if(row==null)continue;entries++;object v;if(row.TryGetValue("expiresAt",out v)&&v!=null)expiryTimes.Add(Convert.ToInt64(v));else unknown++;}
            expiryTimes.Sort();var dates=new List<string>();
            foreach(long epoch in expiryTimes) {
                var utc=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(epoch);
                dates.Add(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc,"China Standard Time").ToString("M月d日 HH:mm"));
            }
            for(int i=0;i<unknown;i++)dates.Add("未提供到期时间");
            if(entries<count)dates.Add("其余到期时间未提供");
            if(dates.Count==0)dates.Add("未提供到期时间");
            return new[]{countText,String.Join("\n",dates.ToArray())};
        } catch {return new[]{"当前数据未提供","当前数据未提供"};}
    }
    static string[] ReadCreditDetails() {
        try {var info=new ProcessStartInfo {FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),Arguments="-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \""+Path.Combine(Root,"credit-details.ps1")+"\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var p=Process.Start(info)){var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(40000)){p.Kill();return null;}Task.WaitAll(new Task[]{output,error},3000);if(p.ExitCode!=0)return null;var data=Json.Deserialize<Dictionary<string,object>>(output.Result.Trim());var credit=FormatCredits(data);
                string expiry="当前数据未提供";object raw;if(data!=null&&data.TryGetValue("subscriptionExpiresAt",out raw)&&raw!=null) {var utc=new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(Convert.ToInt64(raw));expiry=TimeZoneInfo.ConvertTime(utc,TimeZoneInfo.Local).ToString("yyyy年M月d日 HH:mm");}
                return new[]{credit[0],credit[1],expiry};}}
        catch{return null;}
    }
    void StartQuery() {
        nextQuery=DateTime.UtcNow.AddSeconds(5);
        try {
            var info=new ProcessStartInfo {
                FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),
                Arguments="-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File \""+Path.Combine(TimerRoot,"quota-timer.ps1")+"\" -ProbeOnly",
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                WorkingDirectory=Root };
            // Process.Start can block during antivirus checks or heavy system load.
            // Do not let that pause WinEvent delivery or hide/show on the UI thread.
            startingQuery=LaunchProbeAsync(delegate {
                try {
                    var cached=ParseRecentQuery(File.ReadAllText(Path.Combine(TimerRoot,"latest-quota.json")),
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if(cached!=null) return cached;
                } catch { /* Missing or concurrently replaced cache: use the normal probe. */ }
                using(var p=Process.Start(info)) {
                    var output=p.StandardOutput.ReadToEndAsync();
                    var error=p.StandardError.ReadToEndAsync();
                    if(!p.WaitForExit(90000)) { p.Kill(); throw new Exception("Probe timed out"); }
                    if(!Task.WaitAll(new Task[] { output,error },5000) || p.ExitCode!=0) throw new Exception("Probe failed");
                    var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(output.Result.Trim());
                    string newPrimary=FormatWindow("5小时",GetWindow(data,"primary"),300,false);
                    string newWeekly=FormatWindow("一周",GetWindow(data,"secondary"),10080,true);
                    if((PlanName(data)!="Pro" && !newPrimary.Contains("剩余")) || !newWeekly.Contains("剩余")) throw new Exception("Incomplete limits");
                    // Automatic wake observer removed at the user's request; quota display continues normally.
                    var credits=FormatCredits(GetWindow(data,"rateLimitResetCredits"));
                    return new PendingQuery { Primary=newPrimary,Weekly=newWeekly,CreditCount=credits[0],CreditExpiry=credits[1],Plan=PlanName(data) };
                }
            });
        } catch { startingQuery=null; failed=true; WriteStatus(); }
    }
    static Task<PendingQuery> LaunchProbeAsync(Func<PendingQuery> factory) { return Task.Run(factory); }
    void Render() {
        // Keep the previous reading throughout refresh and transient failures.
        SetIfChanged(primary,BodyDisplay(primaryText)); SetIfChanged(weekly,BodyDisplay(weeklyText));
        primary.ForeColor=Color.FromArgb(126,216,183);
        weekly.ForeColor=Color.FromArgb(200,205,218);
        primaryTitle.ForeColor=primary.ForeColor; weeklyTitle.ForeColor=weekly.ForeColor;
        string key=plan+"\n"+primaryText+"\n"+weeklyText+"\n"+creditCount+"\n"+creditExpiry+"\n"+subscriptionExpiry;
        if(lastLayoutKey!=key){lastLayoutKey=key;cardNeedsPaint=true;Invalidate(); if(details!=null&&!details.IsDisposed){details.Plan=plan;details.Primary=primaryText;details.Weekly=weeklyText;details.CreditCount=creditCount;details.CreditExpiry=creditExpiry;details.SubscriptionExpiry=subscriptionExpiry;details.UpdatePanelSize();}}
        string detail=primaryText+"\n"+weeklyText+"\n右键打开菜单";
        if(lastTooltip!=detail) {
            lastTooltip=detail;
            tips.SetToolTip(this,detail);
            tips.SetToolTip(primary,detail); tips.SetToolTip(weekly,detail);
            tips.SetToolTip(primaryTitle,detail); tips.SetToolTip(weeklyTitle,detail);
        }
    }
    void DrawCard(Graphics graphics) {
        float scale=ClientSize.Width/50f;
        if(plan!="Pro") DrawRing(graphics,primaryText, "5h额度", 7f*scale,scale);
        DrawRing(graphics,weeklyText, "周额度", (plan=="Pro"?7f:99f)*scale,scale);
    }
    void DrawRing(Graphics graphics,string value,string label,float top,float scale) {
        var m=Regex.Match(value??"", @"剩余\s+([0-9.]+)%");
        double remaining=0; bool valid=m.Success&&Double.TryParse(m.Groups[1].Value,out remaining);
        Color color=remaining<=25?Color.FromArgb(232,85,95):remaining<=50?Color.FromArgb(224,181,105):remaining<=75?Color.FromArgb(105,177,206):Color.FromArgb(102,205,172);
        color=cardTheme.Ring(color);
        float diameter=30*scale, left=10*scale;
        var rect=new RectangleF(left,top,diameter,diameter);
        using(var track=new Pen(cardTheme.Track,3.7f*scale)) graphics.DrawEllipse(track,rect);
        using(var arc=new Pen(color,3.7f*scale)) {arc.StartCap=LineCap.Round;arc.EndCap=LineCap.Round;if(valid&&remaining>0)graphics.DrawArc(arc,rect,-90,(float)(remaining*3.6));}
        using(var fmt=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center })
        using(var numberFont=new Font("Microsoft YaHei UI",13f*scale,FontStyle.Bold,GraphicsUnit.Pixel))
        using(var labelFont=new Font("Microsoft YaHei UI",11.2f*scale,FontStyle.Regular,GraphicsUnit.Pixel))
        using(var numberBrush=new SolidBrush(cardTheme.Text))
        using(var labelBrush=new SolidBrush(cardTheme.Muted)) {
            graphics.DrawString(valid?m.Groups[1].Value+"%":"—",numberFont,numberBrush,new RectangleF(0,top+34*scale,50*scale,17*scale),fmt);
            var labelBounds=new RectangleF(0,top+51*scale,50*scale,13*scale);
            graphics.DrawString(label,labelFont,labelBrush,labelBounds,fmt);
            if(cardTheme.Light) graphics.DrawString(label,labelFont,labelBrush,new RectangleF(0.12f*scale,top+51*scale,50*scale,13*scale),fmt);
            var reset=Regex.Match(value??"", @"（([^）]+)）");
            string text=reset.Success?reset.Groups[1].Value.Replace(" 重置",""):"—";
            using(var resetFont=new Font("Microsoft YaHei UI",11.2f*scale,FontStyle.Regular,GraphicsUnit.Pixel)) {
                var pieces=text.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
                if(pieces.Length>1) {
                    DrawFitted(graphics,pieces[0],resetFont,labelBrush,new RectangleF(1*scale,top+65*scale,48*scale,14*scale),fmt,cardTheme.Light);
                    DrawFitted(graphics,pieces[1],resetFont,labelBrush,new RectangleF(1*scale,top+79*scale,48*scale,14*scale),fmt,cardTheme.Light);
                } else DrawFitted(graphics,text,resetFont,labelBrush,new RectangleF(1*scale,top+65*scale,48*scale,15*scale),fmt,cardTheme.Light);
            }
        }
    }
    static void DrawFitted(Graphics graphics,string text,Font font,Brush brush,RectangleF bounds,StringFormat center,bool light) {
        var measured=graphics.MeasureString(text,font,Int32.MaxValue,StringFormat.GenericTypographic);
        float ratio=Math.Min(1f,bounds.Width/(measured.Width*(light?1.05f:1.2f)));
        using(var sized=new Font(font.FontFamily,font.Size*ratio,font.Style,GraphicsUnit.Pixel))
        using(var format=(StringFormat)center.Clone()) {format.FormatFlags|=StringFormatFlags.NoWrap;graphics.DrawString(text,sized,brush,bounds,format); if(light) { var shifted=bounds; shifted.X+=0.12f; graphics.DrawString(text,sized,brush,shifted,format); }}
    }
    void CloseDetails() { if(mouseHook!=IntPtr.Zero){UnhookWindowsHookEx(mouseHook);mouseHook=IntPtr.Zero;mouseHookCallback=null;} if(details!=null){details.Close(); details.Dispose(); details=null;} }
    void ToggleDetails() {
        if(details!=null){CloseDetails(); return;}
        details=new QuotaDetails(ClientSize.Width/50f) {Plan=plan,Primary=primaryText,Weekly=weeklyText,CreditCount=creditCount,CreditExpiry=creditExpiry,SubscriptionExpiry=subscriptionExpiry};
        details.BackgroundOwner=appWindow;
        details.ApplyTheme(cardTheme);
        details.UpdatePanelSize();
        RECT frame; int top=GetWindowRect(appWindow,out frame)?frame.Top:0;
        details.Location=new Point(Right+(int)(6*ClientSize.Width/50f),Math.Max(top,Bottom-details.Height));
        details.Show(this);
        mouseHookCallback=OnMouseHook;
        mouseHook=SetWindowsHookEx(14,mouseHookCallback,GetModuleHandle(null),0);
        if(mouseHook==IntPtr.Zero){CloseDetails();}
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
        string snapshot=Json.Serialize(new { primary=primaryText,weekly=weeklyText,subscriptionExpiry=subscriptionExpiry,creditCount=creditCount,failed=failed,lastSuccessUtc=lastSuccess.ToString("o"),refreshSeconds=5,offsetX=offsetX,offsetY=offsetY,magnetEnabled=true,magnetSnapped=magnetSnapped,magnetX=magnetX,magnetY=magnetY,magnetRadiusDip=magnetRadiusDip,magnetBottomAligned=magnetBottom,focusHookActive=focusHook!=IntPtr.Zero,focusPollMs=poll.Interval,mainWindowAnchorOnly=true,trayKeepsWidgetVisible=true,visibilityIndependentOfFocus=true,fullOcclusionHides=true,stackedWithGpt=true,probeStartupOffUiThread=true,fullProbeOffUiThread=true,maxTickMs=Math.Round(maxTickMs,2),maxSyncMs=Math.Round(maxSyncMs,2),maxRenderMs=Math.Round(maxRenderMs,2),maxMetadataMs=Math.Round(maxMetadataMs,2),maxPositionMs=Math.Round(maxPositionMs,2),maxShowMs=Math.Round(maxShowMs,2) });
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
        string positionPath=Path.Combine(Root,"sidebar-position.json"), savedPosition=File.ReadAllText(positionPath);
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
                widget.magnetSnapped=false;widget.offsetX=107;widget.offsetY=100;
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
                check(widget.Visible&&widget.Location==secondPosition&&widget.TryGetSavedOffset(out offsetToSave)&&offsetToSave==new Point(107,100),"Minimized main restore changed offset visible="+widget.Visible+" location="+widget.Location+" expected="+secondPosition+" offset="+offsetToSave+" occluded="+widget.AppFullyOccluded()+" iconic="+IsIconic(second));
                widget.cachedForegroundName="UnrelatedApp";widget.cachedForegroundPid=0;
                widget.SyncToAppCore(IntPtr.Zero);
                check(widget.Visible,"Focus loss hid a visible GPT widget");
                widget.cachedForegroundName="ChatGPT";widget.cachedForegroundPid=GetCurrentProcessId();
                widget.SyncToAppCore(tray);
                check(widget.Visible&&widget.appWindow==second,"Tray focus hid or moved a visible GPT widget");
                widget.SyncToAppCore(second);
                check(widget.Visible&&widget.Location==secondPosition,"Returning to GPT did not restore widget");
                foreach(var target in new[]{main,second}){
                    widget.cachedForegroundName="ChatGPT";widget.cachedForegroundPid=GetCurrentProcessId();widget.SyncToAppCore(target);
                    RECT frame;GetWindowRect(target,out frame);SetWindowPos(target,IntPtr.Zero,0,0,0,0,0x1|0x2|0x10|0x40);
                    IntPtr partial=IntPtr.Zero,remaining=IntPtr.Zero;
                    try{
                        int half=(frame.Right-frame.Left)/2;
                        partial=CreateWindowEx(0,"STATIC","Partial cover",0x04000000,frame.Left-2,frame.Top-2,half+24,frame.Bottom-frame.Top+4,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                        remaining=CreateWindowEx(0,"STATIC","Remaining cover",0x04000000,frame.Left+half-24,frame.Top-2,frame.Right-frame.Left-half+264,frame.Bottom-frame.Top+4,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                        ShowWindow(partial,4);SetWindowPos(partial,IntPtr.Zero,0,0,0,0,0x1|0x2|0x10|0x40);
                        widget.cachedForegroundName="UnrelatedApp";widget.SyncToAppCore(partial);
                        check(widget.Visible&&!widget.AppFullyOccluded()&&!widget.TopMost,"Partial unrelated window hid widget or made it globally topmost");
                        bool above=false;for(var w=GetWindow(widget.Handle,3);w!=IntPtr.Zero;w=GetWindow(w,3))if(w==partial){above=true;break;}
                        check(above,"Widget covered unrelated window");
                        ShowWindow(remaining,4);SetWindowPos(remaining,IntPtr.Zero,0,0,0,0,0x1|0x2|0x10|0x40);Application.DoEvents();System.Threading.Thread.Sleep(80);widget.SyncToAppCore(remaining);
                        check(!widget.Visible&&widget.AppFullyOccluded(),"Two opaque windows fully covered GPT without hiding widget target="+target+" partial="+OpaqueBounds(partial)+" remaining="+OpaqueBounds(remaining)+" full="+widget.AppFullyOccluded()+" targetFrame="+Rectangle.FromLTRB(frame.Left,frame.Top,frame.Right,frame.Bottom)+" targetDwm="+OpaqueBounds(target)+" coverFrame="+NativeRect(partial));
                        ShowWindow(partial,0);widget.SyncToAppCore(remaining);
                        check(widget.Visible&&!widget.AppFullyOccluded(),"Uncover without focus did not restore widget");
                        widget.userHidden=true;widget.SyncToAppCore(remaining);check(!widget.Visible,"Explicit user hide ignored");widget.userHidden=false;
                        ShowWindow(remaining,0);widget.SyncToAppCore(IntPtr.Zero);check(widget.Visible,"Unfocused visible GPT did not remain visible");
                    }finally{if(partial!=IntPtr.Zero)DestroyWindow(partial);if(remaining!=IntPtr.Zero)DestroyWindow(remaining);}
                }
                widget.Close();
                recordForeground("widget-close");
            }
            check(File.ReadAllText(positionPath)==savedPosition,"Test changed saved position");
            File.WriteAllText(Path.Combine(Root,"tray-binding-test.json"),Json.Serialize(new{passed=true,synchronizedRevealOpacityPassed=true,staleRevealFailOpen=true,captionlessMaximizedFrameSupported=true,trayCycles=50,trayKeepsWidgetVisible=true,initialTrayIgnored=true,mainRestoreStable=true,secondMainSupported=true,ownedDialogIgnored=true,animationSuppressionPreserved=true,minimizedPositionProtected=true,visibilityIndependentOfFocus=true,unionOcclusionTested=true,partialOverlapKeepsWidget=true,uncoverWithoutFocusRestores=true,belowUnrelatedWindows=true,savedPositionUnchanged=true,foregroundBefore=foregroundBefore.ToInt64(),foregroundAfter=GetForegroundWindow().ToInt64(),foregroundStages,foregroundUnchanged=GetForegroundWindow()==foregroundBefore}));
        } finally {
            if(owned!=IntPtr.Zero)DestroyWindow(owned);
            if(tray!=IntPtr.Zero)DestroyWindow(tray);
            if(second!=IntPtr.Zero){RemoveProp(second,"CodexStartupAnimationSurfaceOyzy2");DestroyWindow(second);}
            if(main!=IntPtr.Zero)DestroyWindow(main);
        }
    }
        static void MagnetSelfTest() {
        var target=FitOffset(106,1751,new Size(1600,1100),new Size(580,152));
        if(target!=new Point(106,948)||!NearMagnet(new Point(126,968),target,40)||NearMagnet(new Point(147,948),target,40))throw new Exception("Magnet radius or normal target failed");
        IntPtr frame=CreateWindowEx(0,"STATIC","Magnet probe",0x04C70000,-20000,-20000,1600,1100,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(frame==IntPtr.Zero)throw new Exception("Magnet probe creation failed");
        try {
            ShowWindow(frame,4);
            using(var widget=new QuotaWidget(true)) {
                widget.appWindow=frame;widget.magnetX=106;widget.magnetY=1751;widget.magnetRadiusDip=28;
                RECT rect;GetWindowRect(frame,out rect);
                var normal=FitOffset(106,1751,new Size(rect.Right-rect.Left,rect.Bottom-rect.Top),widget.Size);
                widget.Location=new Point(rect.Left+normal.X+5,rect.Top+normal.Y+5);widget.FinishDrag();
                if(!widget.magnetSnapped||widget.offsetX!=106||widget.offsetY!=1751||widget.Location!=new Point(rect.Left+normal.X,rect.Top+normal.Y))throw new Exception("Normal release did not snap to canonical anchor");
                Point saved;if(!widget.TryGetSavedOffset(out saved)||saved!=new Point(106,1751))throw new Exception("Normal clamp overwrote canonical anchor");
                var maximized=FitOffset(widget.offsetX,widget.offsetY,new Size(1800,2000),widget.Size);
                if(maximized!=FitOffset(106,1751,new Size(1800,2000),widget.Size)||FitOffset(widget.offsetX,widget.offsetY,new Size(rect.Right-rect.Left,rect.Bottom-rect.Top),widget.Size)!=normal)throw new Exception("Mode round trip lost mapped positions");
                widget.Location=new Point(rect.Left+normal.X+200,rect.Top+normal.Y);widget.FinishDrag();
                if(widget.magnetSnapped||widget.offsetX!=normal.X+200)throw new Exception("Far release could not detach");
                widget.Location=new Point(rect.Left+normal.X,rect.Top+normal.Y);widget.FinishDrag();
                if(!widget.magnetSnapped||widget.offsetY!=1751)throw new Exception("Reattach failed");
                widget.magnetBottom=true;widget.magnetX=102;
                foreach(var dimensions in new[]{new Size(1600,1100),new Size(2800,1800),new Size(3200,2200)}) {
                    var bottom=widget.MagnetOffset(dimensions);
                    if(bottom.X!=102||bottom.Y+widget.Height!=dimensions.Height)throw new Exception("Bottom alignment changed across window modes");
                }
                var bottomTarget=widget.MagnetOffset(new Size(rect.Right-rect.Left,rect.Bottom-rect.Top));
                widget.Location=new Point(rect.Left+bottomTarget.X+4,rect.Top+bottomTarget.Y-4);widget.FinishDrag();
                if(!widget.magnetSnapped||widget.Location.Y+widget.Height!=rect.Bottom||widget.Location.X-rect.Left!=102)throw new Exception("Bottom magnet release failed");
            }
        }finally{DestroyWindow(frame);}
    }
    static void SelfTest() {
        MagnetSelfTest();
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
            if(preview.primary.ForeColor!=Color.FromArgb(126,216,183) || preview.primaryTitle.ForeColor!=preview.primary.ForeColor || preview.primary.Text!=oldReading) throw new Exception("Stale reading changed color or text");
            using(var image=new Bitmap(preview.Width,preview.Height)) {
                preview.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));
                AssertRenderedMargins(image,preview.weekly.Top,preview.weekly.Bottom);
                image.Save(Path.Combine(Root,"layout-preview.png"));
            }
            preview.poll.Dispose(); preview.tips.Dispose();
        }
        File.WriteAllText(Path.Combine(Root,"test-result.txt"),"PASS: magnet release radius, detach/reattach, canonical anchor persistence and mode mapping; asynchronous probe startup with responsive UI timer, tool-window style and handle recreation, percentage, missing/null values, duration, full text, equal rendered margins, drag tracking guard, taskbar/start-menu focus classification");
    }
}

using System.Text.Json;
using System.Text.Encodings.Web;

namespace VgnBattery;

internal static class Program
{
#if UI_QA
    internal static bool QaSingleDevice { get; private set; }
#endif
    private static string diagnosticDirectory = AppContext.BaseDirectory;

    [STAThread]
    static void Main(string[] args)
    {
        bool diagnostic = args.Any(a=>a is "--enumerate" or "--diagnose" or "--query" or "--query-all" or "--self-test" or "--icon-preview" or "--menu-preview" or "--ui-preview");
        if (!diagnostic) { Run(args); return; }
        try
        {
            int outputIndex = Array.IndexOf(args,"--output-dir");
            if (outputIndex >= 0 && (outputIndex+1 >= args.Length || args[outputIndex+1].StartsWith("--")))
                throw new ArgumentException("--output-dir requires a folder path.");
            string? requested = outputIndex >= 0 ? args[outputIndex+1] : null;
            try { diagnosticDirectory = WritableDirectory(requested ?? AppContext.BaseDirectory); }
            catch (Exception ex) when (requested is null && ex is IOException or UnauthorizedAccessException)
            {
                diagnosticDirectory = WritableDirectory(Path.Combine(BatteryReader.DataDirectory,"diagnostics"));
            }
            Run(args);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            try
            {
                Directory.CreateDirectory(BatteryReader.DataDirectory);
                File.WriteAllText(Path.Combine(BatteryReader.DataDirectory,"diagnostic-error.txt"),ex.ToString());
            }
            catch { }
        }
    }

    private static string WritableDirectory(string directory)
    {
        directory=Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using var probe=new FileStream(Path.Combine(directory,".vgn-write-check-"+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose);
        return directory;
    }

    private static string DiagnosticPath(string name) => Path.Combine(diagnosticDirectory,name);

    static void Run(string[] args)
    {
        if (args.Contains("--enumerate"))
        {
            File.WriteAllText(DiagnosticPath("devices.json"), JsonSerializer.Serialize(NativeHid.Enumerate(), new JsonSerializerOptions { WriteIndented=true }));
            return;
        }
        if (args.Contains("--diagnose"))
        {
            File.WriteAllText(DiagnosticPath("device-report.json"),JsonSerializer.Serialize(DeviceDiagnostics.Collect(),new JsonSerializerOptions { WriteIndented=true, Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return;
        }
        if (args.Contains("--query"))
        {
            new BatteryReader().Read();
            return;
        }
        if (args.Contains("--query-all"))
        {
            File.WriteAllText(DiagnosticPath("devices-status.json"),JsonSerializer.Serialize(new BatteryReader().ReadAll(),new JsonSerializerOptions { WriteIndented=true, Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return;
        }
        if (args.Contains("--self-test"))
        {
            RunParserChecks();
            return;
        }
        if (args.Contains("--icon-preview"))
        {
            RenderIconPreview();
            return;
        }
        if (args.Contains("--menu-preview"))
        {
            ApplicationConfiguration.Initialize();
            RenderMenuPreview();
            return;
        }
        if (args.Contains("--ui-preview"))
        {
            ApplicationConfiguration.Initialize();
            using var popup=new BatteryPopup();
            popup.SetDisplayOptions(MouseDisplayMode.Fixed, "demo-vgn",
                [new("demo-vgn", "VGN F2 Pro Max · 2.4G"), new("demo-keychron", "Keychron M5 · 2.4G")], 30, 5);
            var previewTime=new DateTimeOffset(2026,11,17,12,34,56,TimeSpan.FromHours(8));
            popup.SetState(new TrayApp.BatteryView(100,false,"无线使用中",previewTime,"2.4 GHz USB 接收器","VGN DragonFly F2 Pro Max"),false,false,true,true,20);
            var previewDevices=new List<MouseBattery>
            {
                new("demo-vgn",new(100,false,"",previewTime,"2.4 GHz USB 接收器","VGN DragonFly F2 Pro Max"))
            };
            if (!args.Contains("--preview-one"))
                previewDevices.Add(new("demo-keychron",new(95,false,"",previewTime,"2.4 GHz USB 接收器","Keychron M5")));
            if (args.Contains("--preview-many"))
            {
                previewDevices.Add(new("demo-history",new(42,false,"",previewTime,"USB 有线","VXE R1 Pro"),true));
                previewDevices.Add(new("demo-unknown",new(null,false,"",previewTime,"2.4 GHz USB 接收器","VGN Mouse 2.4G Receiver · 长名称示例"),true));
            }
            popup.SetOtherDevices("demo-vgn",previewDevices);
            popup.SetSettingsExpanded(args.Contains("--preview-settings"));
            popup.PerformLayout();
            using var surface=new Panel { ClientSize=popup.ClientSize, BackColor=popup.BackColor, ForeColor=popup.ForeColor, Font=popup.Font, Padding=popup.Padding };
            surface.CreateControl();
            foreach(Control control in popup.Controls.Cast<Control>().ToArray()) surface.Controls.Add(control);
            void LayoutPreview(Control control)
            {
                control.PerformLayout();
                foreach(Control child in control.Controls) LayoutPreview(child);
            }
            LayoutPreview(surface);
            using var bitmap=new Bitmap(popup.ClientSize.Width,popup.ClientSize.Height);
            surface.DrawToBitmap(bitmap,new Rectangle(Point.Empty,surface.ClientSize));
            bitmap.Save(DiagnosticPath("popup-demo.png"),System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
#if !HID_PROBE
#if UI_QA
        const string instanceName=@"Local\VgnBattery.QA.Instance";
        const string showName=@"Local\VgnBattery.QA.Show";
#else
        const string instanceName=@"Local\VgnBattery.Instance";
        const string showName=@"Local\VgnBattery.Show";
#endif
        using var mutex = new Mutex(true, instanceName, out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, showName);
        if (!first) { showEvent.Set(); return; }
        ApplicationConfiguration.Initialize();
#if UI_QA
        QaSingleDevice=args.Contains("--qa-single");
#endif
        using var context = new TrayApp();
#if UI_QA
        using var qaTimer = new System.Windows.Forms.Timer { Interval=100 };
        if (args.Contains("--qa-smoke"))
        {
            qaTimer.Tick += async (_,_) =>
            {
                qaTimer.Stop();
                string qaDirectory=Path.Combine(AppContext.BaseDirectory,"qa-data");
                Directory.CreateDirectory(qaDirectory);
                try
                {
                    int menuChecks=0;
                    await DropdownChecks.RunAsync((success,label) =>
                    {
                        if (!success) throw new InvalidDataException(label);
                        menuChecks++;
                    });
                    File.WriteAllText(Path.Combine(qaDirectory,"dropdown.txt"),$"Passed {menuChecks} menu lifecycle checks using actual WinForms click handling.");
                    int layoutChecks=0;
                    await PopupLayoutChecks.RunAsync((success,label) =>
                    {
                        if (!success) throw new InvalidDataException(label);
                        layoutChecks++;
                    });
                    File.WriteAllText(Path.Combine(qaDirectory,"layout-checks.txt"),$"Passed {layoutChecks} popup layout checks with a real window and message loop.");
                    int paintChecks=0;
                    await PaintChecks.RunAsync((success,label) =>
                    {
                        if (!success) throw new InvalidDataException(label);
                        paintChecks++;
                    });
                    File.WriteAllText(Path.Combine(qaDirectory,"paint-checks.txt"),$"Passed {paintChecks} paint invalidation checks using real WM_PAINT clips.");
                    await context.RunQaSmokeAsync();
                }
                catch (Exception ex)
                {
                    Environment.ExitCode=1;
                    File.WriteAllText(Path.Combine(qaDirectory,"dropdown.txt"),ex.ToString());
                    context.ExitThread();
                }
            };
            qaTimer.Start();
        }
#endif
        // A second instance signals this event. Wait for it instead of waking
        // the UI thread every 300ms while the tray is idle.
        var showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_,_) => context.ShowPopup(), null, Timeout.Infinite, executeOnlyOnce:false);
        try
        {
            if (args.Contains("--show")) showEvent.Set();
            Application.Run(context);
        }
        finally
        {
            // ShowPopup safely posts to the UI thread, including during exit.
            // Drain callbacks before disposing the context and event handles.
            using var callbacksStopped = new ManualResetEvent(false);
            if (showWait.Unregister(callbacksStopped)) callbacksStopped.WaitOne();
        }
        mutex.ReleaseMutex();
#endif
    }

    private static void RunParserChecks()
    {
        byte[] Frame(byte percent, bool cable, ushort millivolts=3900)
        {
            byte[] frame = [8,4,0,0,0,2,percent,(byte)(cable?1:0),(byte)(millivolts>>8),(byte)millivolts,0,0,0,0,0,0,0];
            frame[16] = unchecked((byte)(0x55-frame.Sum(b=>(int)b)));
            return frame;
        }
        byte[] KeychronFrame(byte level, bool charging)
        {
            var frame = new byte[64];
            frame[0] = 0xb4;
            frame[1] = 0x06;
            frame[20] = (byte)(level | (charging ? 0x80 : 0));
            return frame;
        }
        int passedChecks=0;
        void Check(bool success, string label) { if (!success) throw new InvalidDataException(label); passedChecks++; }
        Check(BatteryReader.Parse(Frame(67,false),0xfb3e).Percent==67,"wireless percent");
        Check(BatteryReader.Parse(Frame(0,false,3300),0xfb3e).Percent==0,"real zero percent");
        Check(BatteryReader.Parse(Frame(38,true),0xfb3d).Charging,"cable charging");
        Check(!BatteryReader.Parse(Frame(100,true),0xfb3d).Charging,"fully charged");
        var corrupt=Frame(60,false); corrupt[6]=59;
        Check(BatteryReader.Parse(corrupt,0xfb3e).Percent is null,"bad checksum rejected");
        Check(BatteryReader.Parse(Frame(101,false),0xfb3e).Percent is null,"out of range rejected");
        Check(BatteryReader.Parse([8,4],0xfb3e).Percent is null,"short report rejected");
        Check(BatteryReader.Parse([8,4,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0x49],0xfb3e).Percent is null,"echo rejected");
        var actualName = new HidDevice("",0x3554,0xfb3e,"VGN DragonFly F2 Pro Max",0xff02,2,17,17,0);
        Check(DeviceCatalog.Model(actualName,DeviceCatalog.Find(0x3554,0xfb3e))=="VGN DragonFly F2 Pro Max","firmware model name");
        Check(DeviceCatalog.Model(actualName with { Product="Compx" },DeviceCatalog.Find(0x3554,0xf503))=="VGN F1 Pro","generic receiver name fallback");
        Check(DeviceCatalog.Find(0x3554,0xffff) is null,"unknown device never queried");
        using var onlineIcon=TrayIconRenderer.Create(100,false,true,false);
        using var lowIcon=TrayIconRenderer.Create(5,false,true,false);
        using var unknownIcon=TrayIconRenderer.Create(null,false,true,false);
        using var asleepIcon=TrayIconRenderer.Create(100,false,true,false,true);
        using var withoutDigits=TrayIconRenderer.Create(100,false,false,false);
        using var online=onlineIcon.ToBitmap();
        using var low=lowIcon.ToBitmap();
        using var unknown=unknownIcon.ToBitmap();
        using var asleep=asleepIcon.ToBitmap();
        using var plain=withoutDigits.ToBitmap();
        bool HasGreen(Bitmap bitmap)
        {
            for(int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++)
            { var c=bitmap.GetPixel(x,y); if(c.A>100 && c.G>c.R+40 && c.G>c.B+15) return true; }
            return false;
        }
        Check(HasGreen(online),"connected battery icon is green");
        Check(HasGreen(low),"connected low battery icon stays green");
        Check(!HasGreen(unknown),"disconnected icon is gray");
        Check(!HasGreen(asleep),"sleeping icon is gray");
        bool differs=false;
        for(int y=0;y<online.Height;y++) for(int x=0;x<online.Width;x++) if(online.GetPixel(x,y)!=plain.GetPixel(x,y)) differs=true;
        Check(differs,"percentage toggle changes the battery icon");
        var policy = new LowBatteryPolicy();
        BatteryReading Reading(int? p,bool charging=false,string name="test mouse") => new(p,charging,"",DateTimeOffset.Now,"USB",name);
        Check(!policy.ShouldNotify(Reading(20),true,20),"threshold is strictly below");
        Check(policy.ShouldNotify(Reading(19),true,20),"first low reading notifies");
        Check(!policy.ShouldNotify(Reading(18),true,20),"low reading does not spam");
        Check(!policy.ShouldNotify(Reading(null,name:"unknown"),true,20),"missing reading never notifies");
        Check(!policy.ShouldNotify(Reading(18),true,20),"missing reading does not rearm");
        Check(!policy.ShouldNotify(Reading(22),true,20),"small recovery does not rearm");
        Check(!policy.ShouldNotify(Reading(19),true,20),"oscillating reading does not spam");
        Check(!policy.ShouldNotify(Reading(25),true,20),"recovery rearms silently");
        Check(policy.ShouldNotify(Reading(19),true,20),"new discharge cycle notifies");
        Check(!policy.ShouldNotify(Reading(19,true),true,20),"charging rearms silently");
        Check(policy.ShouldNotify(Reading(19),true,20),"unplugging below threshold notifies");
        Check(!policy.ShouldNotify(Reading(5),false,20),"disabled notifications suppressed");
        Check(policy.ShouldNotify(Reading(5),true,20),"reenabling notifications works");
        Check(policy.ShouldNotify(Reading(5,name:"second mouse"),true,20),"new mouse gets own warning");
        Check(policy.ShouldNotify(Reading(29,name:"second mouse"),true,30),"changed threshold applies");
        using var dragLabel=new Label(); using var dragPanel=new Panel(); using var dragGauge=new BatteryCanvas();
        using var clickButton=new Button(); using var clickCheck=new CheckBox(); using var clickNumber=new NumericUpDown();
        Check(BatteryPopup.CanDragFrom(dragLabel) && BatteryPopup.CanDragFrom(dragPanel) && BatteryPopup.CanDragFrom(dragGauge),"text, background and battery allow dragging");
        Check(!BatteryPopup.CanDragFrom(clickButton) && !BatteryPopup.CanDragFrom(clickCheck) && !BatteryPopup.CanDragFrom(clickNumber),"interactive controls keep their actions");
        using var styledNumber=new ThresholdStepper(); using var styledCheck=new StyledCheckBox();
        Check(!BatteryPopup.CanDragFrom(styledNumber) && !BatteryPopup.CanDragFrom(styledCheck),"styled settings controls keep their actions");
        styledNumber.Value=0; Check(styledNumber.Value==1,"threshold input lower bound");
        styledNumber.Value=100; Check(styledNumber.Value==99,"threshold input upper bound");
        string startup=StartupRegistration.BuildCommand(@"C:\Apps\Mouse Battery\VgnBattery.exe",@"C:\Apps\Mouse Battery");
        Check(startup=="\"C:\\Apps\\Mouse Battery\\VgnBattery.exe\"","startup path with spaces quoted");
        Check(StartupRegistration.BuildCommand(@"C:\dotnet\dotnet.exe",@"C:\MouseApp").EndsWith("\"C:\\MouseApp\\VgnBattery.dll\""),"dotnet host includes application DLL");
        Check(!StartupRegistration.NeedsMigration(null,startup) && !StartupRegistration.NeedsMigration("  ",startup),"disabled startup stays disabled");
        Check(StartupRegistration.NeedsMigration("\"C:\\OldApp\\VgnBattery.exe\"",startup),"existing startup path migrates");
        Check(!StartupRegistration.NeedsMigration(startup,startup),"current startup needs no migration");
        Check(StartupRegistration.MatchesCurrentCommand("  "+startup.ToUpperInvariant()+"  ",startup),"startup command comparison tolerates case and outer spaces");
        Check(!StartupRegistration.MatchesCurrentCommand("\"C:\\OldApp\\VgnBattery.exe\"",startup),"old startup path not reported as current");
        Check(DeviceCatalog.Find(0x391d,0x102d) is { Wired:false },"F2 Master GT receiver registered");
        Check(DeviceCatalog.Find(0x391d,0x1032) is { Wired:true },"F2 Master GT cable registered");
        Check(DeviceCatalog.Model(actualName with { VendorId=0x391d,ProductId=0x102d,Product="Compx" },DeviceCatalog.Find(0x391d,0x102d))=="VGN F2 Master GT","GT generic receiver model fallback");
        Check(DeviceCatalog.Find(0x391d,0xffff) is null,"unknown device from GT vendor not queried");
        Check(BatteryReader.ParseKeychron(KeychronFrame(67,false)).Percent==67,"keychron wireless percent");
        Check(BatteryReader.ParseKeychron(KeychronFrame(0,false)).Percent==0,"keychron real zero percent");
        Check(BatteryReader.ParseKeychron(KeychronFrame(38,true),true).Charging,"keychron charging flag");
        Check(!BatteryReader.ParseKeychron(KeychronFrame(100,true),true).Charging,"keychron full is not charging");
        Check(BatteryReader.ParseKeychron(KeychronFrame(101,false)).Percent is null,"keychron out of range rejected");
        var wrongReportId=KeychronFrame(50,false); wrongReportId[0]=0xb5;
        Check(BatteryReader.ParseKeychron(wrongReportId).Percent is null,"keychron wrong report id rejected");
        Check(BatteryReader.ParseKeychron(new byte[20]).Percent is null,"keychron short report rejected");
        Check(DeviceCatalog.Find(0x3434,0xd028) is { Wired:false, QueryModel:true },"keychron receiver registered");
        Check(DeviceCatalog.Find(0x3434,0xd048) is { Wired:true },"keychron wired M5 registered");
        Check(DeviceCatalog.Find(0x3434,0xd049) is { Wired:true },"keychron wired M6 registered");
        Check(DeviceCatalog.Find(0x3434,0xffff) is null,"unknown keychron device never queried");
        Check(DeviceCatalog.IsVendor(0x3434) && !DeviceCatalog.IsVendor(0x1234),"scan accepts registered vendors only");
        var receiverProfile=DeviceCatalog.Find(0x3434,0xd028)!;
        Check(receiverProfile.Matches(new HidDevice("",0x3434,0xd028,"Keychron Ultra-Link 8K",0xffc1,1,64,64,0)),"keychron receiver matches its 64-byte collection");
        Check(!receiverProfile.Matches(new HidDevice("",0x3434,0xd028,"Keychron Ultra-Link 8K",0xff60,0x61,33,33,0)),"keychron receiver never targets the raw HID collection");
        Check(DeviceCatalog.Model(new HidDevice("",0x3434,0xd028,"Keychron Ultra-Link 8K",0xffc1,1,64,64,0),receiverProfile)=="Keychron Ultra-Link 8K","keychron product name preserved");
        Check(TrayApp.NormalizePollSeconds(null)==30,"poll interval defaults to 30 seconds");
        Check(TrayApp.NormalizePollSeconds(5)==5,"poll interval accepts five seconds");
        Check(TrayApp.NormalizePollSeconds(600)==30,"poll interval rejects out of range values");
        Check(TrayApp.NormalizePollSeconds(30)==30 && TrayApp.NormalizePollSeconds(120)==120,"poll interval accepts new default and slow option");
        Check(TrayApp.NormalizePollSeconds(4)==30 && TrayApp.NormalizePollSeconds(7)==30,"poll interval accepts listed choices only");
        var failedResponse=Frame(55,false); failedResponse[2]=1; failedResponse[16]=0; failedResponse[16]=unchecked((byte)(0x55-failedResponse.Sum(b=>(int)b)));
        Check(BatteryReader.Parse(failedResponse,0xfb3e).Percent is null,"error response with valid checksum rejected");
        string shareReport=JsonSerializer.Serialize(DeviceDiagnostics.CreateReport([actualName with { Path="private-device-path-and-serial",VendorId=0x391d,ProductId=0x102d }],[]));
        Check(!shareReport.Contains("private-device-path-and-serial",StringComparison.Ordinal),"share report omits device path and serial");
        Check(shareReport.Contains("391D",StringComparison.Ordinal) && shareReport.Contains("102D",StringComparison.Ordinal),"share report retains model identifiers");
        MultiDeviceChecks.Run(Check);
        OtherDeviceChecks.Run(Check);
        SettingsVisibilityChecks.Run(Check);
        File.WriteAllText(DiagnosticPath("self-test.txt"), $"Passed {passedChecks} protocol, identity, icon, multi-device, settings, startup, diagnostics and low battery policy checks. These are offline checks, not hardware validation.");
    }

    private static void RenderIconPreview()
    {
        using var sheet = new Bitmap(780, 256);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.FromArgb(30,30,30));
        using var font = new Font("Segoe UI",12,FontStyle.Regular,GraphicsUnit.Pixel);
        (int? Percent,bool Stale,string Label)[] cases = [(100,false,"100"),(99,false,"99"),(35,false,"35"),(5,false,"5"),(0,false,"0"),(100,true,"Asleep 100")];
        int[] sizes=[16,24,32,24];
        for(int row=0;row<4;row++)
        for(int col=0;col<cases.Length;col++)
        {
            var item=cases[col];
            int size=sizes[row];
            using var icon=TrayIconRenderer.Create(item.Percent,false,row!=3,false,item.Stale,iconSize:size);
            graphics.DrawIcon(icon,new Rectangle(10+col*130,12+row*64,size,size));
            using var bitmap=icon.ToBitmap();
            graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.DrawImage(bitmap,new Rectangle(50+col*130,4+row*64,48,48));
            graphics.DrawString($"{size}px {item.Label}"+(row==3?" icon":""),font,Brushes.White,10+col*130,50+row*64);
        }
        sheet.Save(DiagnosticPath("icon-preview.png"),System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void RenderMenuPreview()
    {
        using var menu=new ContextMenuStrip
        {
            ShowImageMargin=false,ShowCheckMargin=true,Renderer=new DarkMenuRenderer(),
            Font=new Font("Microsoft YaHei UI",9.5F),BackColor=Color.FromArgb(27,30,36),ForeColor=Color.FromArgb(239,243,250),Padding=new Padding(4)
        };
        menu.Items.Add("查看电量");
        menu.Items.Add("立即刷新");
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("显示电量数字"){CheckOnClick=true,Checked=true});
        menu.Items.Add(new ToolStripMenuItem("低电通知（低于 20%）"){CheckOnClick=true,Checked=true});
        var displayMode=new ToolStripMenuItem("显示方式");
        displayMode.DropDownItems.Add(new ToolStripMenuItem("固定显示"){Checked=true});
        displayMode.DropDownItems.Add(new ToolStripMenuItem("轮换显示"));
        displayMode.DropDownItems.Add(new ToolStripMenuItem("平铺显示"));
        menu.Items.Add(displayMode);
        var interval = new ToolStripMenuItem("查询间隔");
        foreach (int seconds in new[]{5,15,30,45,60,120}) interval.DropDownItems.Add(new ToolStripMenuItem($"每 {seconds} 秒"){Checked=seconds==30});
        menu.Items.Add(interval);
        menu.Items.Add(new ToolStripMenuItem("开机自动运行"){CheckOnClick=true,Checked=false});
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出");
        menu.CreateControl();
        menu.PerformLayout();
        menu.Size=menu.GetPreferredSize(Size.Empty);
        using var bitmap=new Bitmap(menu.Width,menu.Height);
        menu.DrawToBitmap(bitmap,new Rectangle(Point.Empty,menu.Size));
        bitmap.Save(DiagnosticPath("menu-preview.png"),System.Drawing.Imaging.ImageFormat.Png);

        if (interval.DropDown is ToolStripDropDownMenu choices)
        {
            choices.ShowImageMargin=false;
            choices.ShowCheckMargin=true;
            choices.Font=new Font("Microsoft YaHei UI",9.5F);
            choices.BackColor=Color.FromArgb(27,30,36);
            choices.ForeColor=Color.FromArgb(239,243,250);
            choices.Padding=new Padding(4);
            choices.PerformLayout();
            choices.Size=choices.GetPreferredSize(Size.Empty);
            using var sheet=new Bitmap(choices.Width,choices.Height);
            choices.DrawToBitmap(sheet,new Rectangle(Point.Empty,choices.Size));
            sheet.Save(DiagnosticPath("menu-interval-preview.png"),System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace VgnBattery;

internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const int DefaultPollSeconds = 30;
    internal const int DefaultRotateSeconds = 5;
    internal static readonly int[] PollChoices = [5, 15, 30, 45, 60, 120];
    internal static readonly int[] RotateChoices = [3, 5, 10, 15];
#if UI_QA
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "qa-data", "settings.json");
#else
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgnBattery", "settings.json");
#endif
    private readonly BatteryReader reader = new();
    private readonly MouseDisplayState display = new();
    private readonly Dictionary<string, TrayIconHost> icons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LowBatteryPolicy> lowPolicies = new(StringComparer.Ordinal);
    private readonly BatteryPopup popup;
    private readonly ContextMenuStrip menu;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer rotationTimer;
    private readonly System.Windows.Forms.Timer deviceDebounce;
    private readonly ToolStripMenuItem refreshItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem percentageItem;
    private readonly ToolStripMenuItem lowBatteryItem;
    private readonly ToolStripMenuItem exportItem;
    private readonly ToolStripMenuItem pollIntervalItem;
    private readonly ToolStripMenuItem modeItem;
    private readonly ToolStripMenuItem fixedTargetItem;
    private readonly ToolStripMenuItem rotationItem;
    private readonly ToolStripMenuItem settingsItem;
    private readonly ToolStripSeparator multiSettingsSeparator;
    private bool refreshing;
    private bool refreshQueued;
    private volatile bool exiting;
#if !UI_QA
    private bool exporting;
#endif
    private bool showPercentage = true;
    private bool lightTaskbar;
    private bool startupEnabled;
    private bool lowBatteryEnabled = true;
    private int lowBatteryThreshold = 20;
    private int pollSeconds = DefaultPollSeconds;
    private int rotateSeconds = DefaultRotateSeconds;
    private string? detailsDeviceId;
    private string? contextDeviceId;
    private string targetsSignature = "";

    public TrayApp()
    {
        bool migrateSettings = false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;
            if (root.TryGetProperty("ShowPercentage", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                showPercentage = value.GetBoolean();
            else if (root.TryGetProperty("BatteryIcon", out var legacy) && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                showPercentage = !legacy.GetBoolean();
                migrateSettings = true;
            }
            if (root.TryGetProperty("LowBatteryEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                lowBatteryEnabled = enabled.GetBoolean();
            if (root.TryGetProperty("LowBatteryThreshold", out var threshold) && threshold.TryGetInt32(out int valueThreshold))
                lowBatteryThreshold = Math.Clamp(valueThreshold, 1, 99);
            if (root.TryGetProperty("PollSeconds", out var poll) && poll.TryGetInt32(out int pollValue))
                pollSeconds = NormalizePollSeconds(pollValue);
            if (root.TryGetProperty("RotateSeconds", out var rotate) && rotate.TryGetInt32(out int rotateValue))
                rotateSeconds = NormalizeRotateSeconds(rotateValue);
            if (root.TryGetProperty("Mode", out var mode) && mode.ValueKind == JsonValueKind.String
                && Enum.TryParse<MouseDisplayMode>(mode.GetString(), true, out var parsedMode) && Enum.IsDefined(parsedMode))
                display.Mode = parsedMode;
            if (root.TryGetProperty("FixedDeviceId", out var fixedId) && fixedId.ValueKind == JsonValueKind.String)
                display.FixedDeviceId = fixedId.GetString() ?? "";
        }
        catch { /* Missing preferences retain safe defaults: fixed display and 30-second polling. */ }

        MigrateExistingStartupRegistration();
        lightTaskbar = UsesLightTaskbar();
        popup = new BatteryPopup();
        popup.StartupRequested += (_, _) => SetStartup(popup.StartupChecked);
        popup.PercentageRequested += (_, _) => SetShowPercentage(popup.PercentageChecked);
        popup.LowBatteryOptionsRequested += (_, _) => SetLowBatteryOptions(popup.LowBatteryChecked, popup.NotificationThreshold);
        popup.PollRequested += (_, _) => SetPollSeconds(popup.SelectedPollSeconds);
        popup.ModeRequested += (_, _) => SetDisplayMode(popup.SelectedDisplayMode);
        popup.FixedTargetRequested += (_, _) => SetFixedTarget(popup.SelectedFixedDeviceId);
        popup.RotationRequested += (_, _) => SetRotateSeconds(popup.SelectedRotateSeconds);
        popup.TestNotificationRequested += OnTestNotificationRequested;
        popup.DevicesChanged += OnDevicesChanged;

        menu = NewMenu();
        menu.Items.Add("查看电量", null, (_, _) => ShowPopup(contextDeviceId));
        refreshItem = new ToolStripMenuItem("立即刷新", null, (_, _) => RequestImmediateRefresh());
        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        settingsItem = new ToolStripMenuItem("设置");
        ConfigureSubmenu(settingsItem);
        menu.Items.Add(settingsItem);
        modeItem = new ToolStripMenuItem("显示方式");
        ConfigureSubmenu(modeItem);
        foreach (var mode in Enum.GetValues<MouseDisplayMode>())
        {
            var choice = new ToolStripMenuItem(ModeLabel(mode)) { Tag = mode };
            choice.Click += (_, _) => SetDisplayMode(mode);
            modeItem.DropDownItems.Add(choice);
        }
        fixedTargetItem = new ToolStripMenuItem("固定目标");
        ConfigureSubmenu(fixedTargetItem);
        rotationItem = MakeIntervalMenu("轮换间隔", RotateChoices, SetRotateSeconds);
        pollIntervalItem = MakeIntervalMenu("查询间隔", PollChoices, SetPollSeconds);
        percentageItem = new ToolStripMenuItem("显示电量数字") { CheckOnClick = true, Checked = showPercentage };
        percentageItem.CheckedChanged += (_, _) => SetShowPercentage(percentageItem.Checked);
        lowBatteryItem = new ToolStripMenuItem($"低电通知（低于 {lowBatteryThreshold}%）") { CheckOnClick = true, Checked = lowBatteryEnabled };
        lowBatteryItem.CheckedChanged += (_, _) => SetLowBatteryOptions(lowBatteryItem.Checked, lowBatteryThreshold);
        startupItem = new ToolStripMenuItem("开机自动运行") { CheckOnClick = true };
        startupItem.Click += (_, _) => SetStartup(startupItem.Checked);
        settingsItem.DropDownItems.Add(startupItem);
        settingsItem.DropDownItems.Add(percentageItem);
        settingsItem.DropDownItems.Add(lowBatteryItem);
        settingsItem.DropDownItems.Add(pollIntervalItem);
        multiSettingsSeparator = new ToolStripSeparator();
        settingsItem.DropDownItems.Add(multiSettingsSeparator);
        settingsItem.DropDownItems.Add(modeItem);
        settingsItem.DropDownItems.Add(fixedTargetItem);
        settingsItem.DropDownItems.Add(rotationItem);
        settingsItem.DropDown.Opening += (_, _) => SyncPopupSettings(forceMenu: true, readStartup: true);
        menu.Items.Add(new ToolStripSeparator());
        exportItem = new ToolStripMenuItem("导出设备信息…", null, async (_, _) => await ExportDeviceInformationAsync());
        menu.Items.Add(exportItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => SyncPopupSettings(forceMenu: true, readStartup: true);

        timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (_, _) => { timer.Interval = pollSeconds * 1000; await RefreshAsync(); };
        rotationTimer = new System.Windows.Forms.Timer { Interval = rotateSeconds * 1000 };
        rotationTimer.Tick += (_, _) => { display.RotateNext(); UpdateDisplay(); };
        deviceDebounce = new System.Windows.Forms.Timer { Interval = 300 };
        deviceDebounce.Tick += (_, _) => { deviceDebounce.Stop(); RequestImmediateRefresh(); };
        UpdateDisplay();
        if (migrateSettings) SaveSettings();
        timer.Start();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private static ContextMenuStrip NewMenu() => new()
    {
        ShowImageMargin = false, ShowCheckMargin = true, Renderer = new DarkMenuRenderer(),
        Font = new Font("Microsoft YaHei UI", 9.5F), BackColor = Color.FromArgb(27, 30, 36),
        ForeColor = Color.FromArgb(239, 243, 250), Padding = new Padding(4)
    };

    private static void ConfigureSubmenu(ToolStripMenuItem parent)
    {
        if (parent.DropDown is not ToolStripDropDownMenu submenu) return;
        submenu.ShowImageMargin = false;
        submenu.ShowCheckMargin = true;
        submenu.Renderer = new DarkMenuRenderer();
        submenu.Font = new Font("Microsoft YaHei UI", 9.5F);
        submenu.BackColor = Color.FromArgb(27, 30, 36);
        submenu.ForeColor = Color.FromArgb(239, 243, 250);
        submenu.Padding = new Padding(4);
    }

    private static ToolStripMenuItem MakeIntervalMenu(string label, IEnumerable<int> values, Action<int> selected)
    {
        var parent = new ToolStripMenuItem(label);
        ConfigureSubmenu(parent);
        foreach (int seconds in values)
        {
            var choice = new ToolStripMenuItem($"每 {seconds} 秒") { Tag = seconds };
            choice.Click += (_, _) => selected(seconds);
            parent.DropDownItems.Add(choice);
        }
        return parent;
    }

    internal static string ModeLabel(MouseDisplayMode mode) => mode switch
    {
        MouseDisplayMode.Rotate => "轮流显示",
        MouseDisplayMode.Tiled => "全部平铺",
        _ => "固定一只"
    };

    public void ShowPopup()
    {
        if (exiting) return;
        // Event-wait callbacks run off the UI thread. Always post, because a
        // destroyed handle can make InvokeRequired return false during exit.
        try { popup.BeginInvoke(() => ShowPopup(null)); }
        catch (InvalidOperationException) { /* The popup may be disposed while posting. */ }
    }

    private void ShowPopup(string? deviceId)
    {
        if (exiting) return;
        if (popup.InvokeRequired) { popup.BeginInvoke(() => ShowPopup(deviceId)); return; }
        detailsDeviceId = deviceId ?? VisibleTrayDevices()[0].DeviceId;
        SyncPopupSettings(forcePopup: true, readStartup: true);
        popup.PositionNearTray();
        popup.TopMost = true;
        popup.Show();
        popup.Activate();
        RequestImmediateRefresh();
    }

    private void TogglePopup(string deviceId)
    {
        if (exiting) return;
        if (popup.Visible && detailsDeviceId == deviceId) { popup.Hide(); return; }
        ShowPopup(deviceId);
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (exiting) return;
#if !UI_QA
        reader.InvalidateDevices();
#endif
        deviceDebounce.Stop();
        deviceDebounce.Start();
    }

    private void RequestImmediateRefresh()
    {
        if (exiting) return;
        if (refreshing) { refreshQueued = true; return; }
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (refreshing || exiting) return;
        refreshing = true;
        SyncPopupSettings();
        try
        {
#if UI_QA
            await Task.Delay(10);
            IReadOnlyList<MouseBattery> snapshot =
            Program.QaSingleDevice ?
            [
                new("qa-a", new BatteryReading(80, false, "演示数据", DateTimeOffset.Now, "2.4G 接收器", "演示 · VGN F2"))
            ] : [
                new("qa-a", new BatteryReading(80, false, "演示数据", DateTimeOffset.Now, "2.4G 接收器", "演示 · VGN F2")),
                new("qa-b", new BatteryReading(35, false, "演示数据", DateTimeOffset.Now, "2.4G 接收器", "演示 · Keychron M5"))
            ];
#else
            var snapshot = await Task.Run(() => reader.ReadAll());
#endif
            if (exiting) return;
            string oldFixedId = display.FixedDeviceId;
            display.ApplySnapshot(snapshot);
            if (oldFixedId != display.FixedDeviceId) SaveSettings();
            if (string.IsNullOrEmpty(detailsDeviceId))
                detailsDeviceId = VisibleTrayDevices().FirstOrDefault(d => !string.IsNullOrEmpty(d.DeviceId))?.DeviceId;
            var warnings = new List<MouseBattery>();
            foreach (var device in snapshot)
            {
                if (string.IsNullOrEmpty(device.DeviceId) || device.Stale || device.Reading.Percent is null) continue;
                if (!lowPolicies.TryGetValue(device.DeviceId, out var policy))
                    lowPolicies[device.DeviceId] = policy = new LowBatteryPolicy();
                if (policy.ShouldNotify(device.Reading, lowBatteryEnabled, lowBatteryThreshold))
                    warnings.Add(device);
            }
            if (warnings.Count == 1)
                Notify("鼠标电量较低", $"{warnings[0].Reading.Model} 剩余 {warnings[0].Reading.Percent}%，已低于设定的 {lowBatteryThreshold}%，可以安排充电了。", ToolTipIcon.Warning, warnings[0].DeviceId);
            else if (warnings.Count > 1)
                Notify("多只鼠标电量较低", string.Join("\n", warnings.Select(d => $"{d.Reading.Model}：{d.Reading.Percent}%")), ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            if (!exiting) Notify("暂时无法更新电量", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            refreshing = false;
            if (!exiting)
            {
                UpdateDisplay();
                if (refreshQueued) { refreshQueued = false; RequestImmediateRefresh(); }
            }
        }
    }

    private void UpdateDisplay()
    {
        if (exiting) return;
        var wanted = VisibleTrayDevices();
        // Fixed and rotating modes reuse the same shell icon when its device changes.
        if (display.Mode != MouseDisplayMode.Tiled && wanted.Count == 1 && icons.Count == 1)
        {
            var existing = icons.Single();
            if (existing.Key != wanted[0].DeviceId)
            {
                icons.Clear();
                icons[wanted[0].DeviceId] = existing.Value;
            }
        }
        int size = Math.Max(16, (int)Math.Round(16 * popup.DeviceDpi / 96d));
        foreach (var device in wanted)
        {
            if (!icons.TryGetValue(device.DeviceId, out var host))
            {
                host = new TrayIconHost(menu);
                host.LeftClicked += (sender, _) => { if (sender is TrayIconHost clicked) TogglePopup(clicked.DeviceId); };
                host.ContextRequested += (sender, _) => { if (sender is TrayIconHost clicked) contextDeviceId = clicked.DeviceId; };
                icons.Add(device.DeviceId, host);
            }
            host.Update(device, showPercentage, lightTaskbar, size);
        }
        var wantedIds = wanted.Select(d => d.DeviceId).ToHashSet(StringComparer.Ordinal);
        foreach (string removed in icons.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
        {
            icons[removed].Dispose();
            icons.Remove(removed);
        }
        ConfigureRotation();
        SyncPopupSettings();
#if UI_QA
        WriteQaState();
#endif
    }

#if UI_QA
    internal async Task RunQaSmokeAsync()
    {
        string resultPath = Path.Combine(AppContext.BaseDirectory, "qa-data", "integration.txt");
        int checks = 0;
        void Check(bool success, string description)
        {
            if (!success) throw new InvalidDataException(description);
            checks++;
        }
        try
        {
            timer.Stop();
            // The normal startup poll may still be completing on a slow runner.
            while (refreshing) await Task.Delay(10);
            await RefreshAsync();
            SetDisplayMode(MouseDisplayMode.Fixed);
            SetFixedTarget("qa-b");
            Check(icons.Count == 1 && icons.ContainsKey("qa-b"), "fixed target owns one matching icon");
            Check(detailsDeviceId == "qa-b", "manually selecting a fixed target updates its details");
            SetDisplayMode(MouseDisplayMode.Tiled);
            Check(icons.Count == 2 && icons.ContainsKey("qa-a") && icons.ContainsKey("qa-b"), "tiled mode creates both icons");
            Check(!menu.IsDisposed, "removing an icon preserves the shared menu");
            SetDisplayMode(MouseDisplayMode.Rotate);
            Check(icons.Count == 1 && rotationTimer.Enabled, "rotation owns one icon and starts its timer");
            string before = icons.Keys.Single();
            var reusable = icons.Values.Single();
            detailsDeviceId = "qa-a";
            display.GetDetails(detailsDeviceId);
            display.RotateNext();
            UpdateDisplay();
            Check(icons.Keys.Single() != before && ReferenceEquals(reusable, icons.Values.Single()), "rotation reuses the shell icon");
            Check(display.GetDetails(detailsDeviceId).DeviceId == "qa-a", "rotation keeps details pinned");
            SetPollSeconds(30);
            SetRotateSeconds(5);
            SetPollSeconds(120);
            SetRotateSeconds(3);
            Check(timer.Interval == 120000 && rotationTimer.Interval == 3000, "query and rotation intervals are independent");
            SyncPopupSettings(forceMenu: true);
            Check(pollIntervalItem.DropDownItems.OfType<ToolStripMenuItem>().Single(i => i.Checked).Tag is 120, "query menu marks the selected interval");
            SetDisplayMode(MouseDisplayMode.Fixed);
            Check(!rotationTimer.Enabled && icons.Keys.Single() == "qa-b", "fixed mode stops rotation and restores its target");
            for (int i=0;i<25;i++)
            {
                SetDisplayMode(MouseDisplayMode.Tiled);
                SetDisplayMode(MouseDisplayMode.Fixed);
            }
            Check(icons.Count == 1 && !menu.IsDisposed, "repeated mode changes preserve one icon and the menu");
            Check(settingsItem.DropDownItems.Contains(modeItem) && !menu.Items.Contains(modeItem)
                && settingsItem.DropDownItems.Contains(pollIntervalItem),"tray settings are grouped under one submenu");
            SyncPopupSettings(forceMenu:true);
            Check(multiSettingsSeparator.Available && modeItem.Available && fixedTargetItem.Available && !rotationItem.Available,
                "multi-device fixed settings are prepared before the submenu opens");
            display.ApplySnapshot([
                new("qa-a",new(80,false,"",DateTimeOffset.Now,"USB 有线","Single usable mouse")),
                new("qa-idle",new(null,false,"",DateTimeOffset.Now,"2.4G 接收器","Idle receiver"))
            ]);
            UpdateDisplay();
            SyncPopupSettings(forceMenu:true);
            Check(!modeItem.Available && !rotationItem.Available && fixedTargetItem.Available,
                "single-device menus retain recovery for the unavailable fixed target");
            SetFixedTarget("qa-a");
            SyncPopupSettings(forceMenu:true);
            Check(!modeItem.Available && !rotationItem.Available && !fixedTargetItem.Available
                && pollIntervalItem.Available && startupItem.Available,
                "single-device menus hide multi-device controls and retain common settings");
            SetDisplayMode(MouseDisplayMode.Rotate);
            display.RotateNext();
            UpdateDisplay();
            Check(!rotationTimer.Enabled && icons.Count==1 && icons.Keys.Single()=="qa-a",
                "single usable mice pause rotation without cycling through an idle receiver");
            File.WriteAllText(resultPath, $"Passed {checks} simulated tray integration checks. No hardware was queried.");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(resultPath, ex.ToString());
        }
        finally { ExitThread(); }
    }

    private void WriteQaState()
    {
        try
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "qa-data");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "state.json"), JsonSerializer.Serialize(new
            {
                Mode = display.Mode.ToString(), FixedDeviceId = display.FixedDeviceId,
                DetailsDeviceId = detailsDeviceId, PopupVisible = popup.Visible, VisibleHostCount = icons.Count,
                HostIds = icons.Keys.ToArray(), VisibleDeviceIds = VisibleTrayDevices().Select(d => d.DeviceId).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
#endif

    private IReadOnlyList<MouseBattery> VisibleTrayDevices() => SettingsVisibility.ForTray(display.Mode, display.Devices, display.GetVisible());

    private void ConfigureRotation()
    {
        bool run = display.Mode == MouseDisplayMode.Rotate && SettingsVisibility.ForDevices(display.Devices);
        rotationTimer.Interval = rotateSeconds * 1000;
        if (run && !rotationTimer.Enabled) rotationTimer.Start();
        else if (!run && rotationTimer.Enabled) rotationTimer.Stop();
    }

    private void SetShowPercentage(bool enabled)
    {
        if (showPercentage == enabled) return;
        showPercentage = enabled;
        SaveSettings();
        UpdateDisplay();
    }

    private void OnTestNotificationRequested(object? sender, EventArgs e) => Notify("低电提醒测试",
        $"这是一条测试通知。鼠标电量低于 {lowBatteryThreshold}% 时会提醒。");

    private void Notify(string title, string text, ToolTipIcon kind = ToolTipIcon.Info, string? deviceId = null)
    {
        TrayIconHost? host = deviceId is not null && icons.TryGetValue(deviceId, out var exact) ? exact : icons.Values.FirstOrDefault();
        host?.Notify(title, text, kind);
    }

    private async Task ExportDeviceInformationAsync()
    {
#if UI_QA
        await Task.CompletedTask;
        Notify("演示模式", "演示模式不会读取或导出真实设备信息。");
#else
        if (exporting || exiting) return;
        exporting = true;
        exportItem.Enabled = false;
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出设备接口信息", FileName = "Device-report.json", Filter = "JSON 文件 (*.json)|*.json",
                DefaultExt = "json", AddExtension = true, OverwritePrompt = true, CheckPathExists = true
            };
            var choice = popup.Visible ? dialog.ShowDialog(popup) : dialog.ShowDialog();
            if (choice != DialogResult.OK) return;
            exportItem.Text = "正在导出设备信息…";
            string json = await Task.Run(() => JsonSerializer.Serialize(DeviceDiagnostics.Collect(), new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
            if (exiting) return;
            await File.WriteAllTextAsync(dialog.FileName, json);
            if (!exiting) Notify("设备信息已导出", "已导出设备接口信息，未包含设备路径或序列号");
        }
        catch (Exception ex) { if (!exiting) Notify("导出设备信息失败", ex.Message, ToolTipIcon.Warning); }
        finally
        {
            exporting = false;
            if (!exiting) { exportItem.Enabled = true; exportItem.Text = "导出设备信息…"; }
        }
#endif
    }

    private void SetLowBatteryOptions(bool enabled, int threshold)
    {
        threshold = Math.Clamp(threshold, 1, 99);
        if (lowBatteryEnabled == enabled && lowBatteryThreshold == threshold) return;
        lowBatteryEnabled = enabled;
        lowBatteryThreshold = threshold;
        foreach (var policy in lowPolicies.Values) policy.Reset();
        SaveSettings();
        UpdateDisplay();
    }

    private void SetPollSeconds(int seconds)
    {
        seconds = NormalizePollSeconds(seconds);
        if (pollSeconds == seconds) { SyncPopupSettings(); return; }
        pollSeconds = seconds;
        SaveSettings();
        timer.Interval = pollSeconds * 1000;
        UpdateDisplay();
    }

    private void SetRotateSeconds(int seconds)
    {
        seconds = NormalizeRotateSeconds(seconds);
        if (rotateSeconds == seconds) return;
        rotateSeconds = seconds;
        SaveSettings();
        ConfigureRotation();
        SyncPopupSettings();
    }

    private void SetDisplayMode(MouseDisplayMode mode)
    {
        if (!Enum.IsDefined(mode)) mode = MouseDisplayMode.Fixed;
        if (display.Mode == mode) return;
        display.Mode = mode;
        SaveSettings();
        UpdateDisplay();
    }

    private void SetFixedTarget(string deviceId)
    {
        if (display.FixedDeviceId == deviceId) return;
        display.FixedDeviceId = deviceId;
        detailsDeviceId = deviceId;
        SaveSettings();
        UpdateDisplay();
    }

    internal static int NormalizePollSeconds(int? seconds) => seconds.HasValue && PollChoices.Contains(seconds.Value) ? seconds.Value : DefaultPollSeconds;
    internal static int NormalizeRotateSeconds(int? seconds) => seconds.HasValue && RotateChoices.Contains(seconds.Value) ? seconds.Value : DefaultRotateSeconds;

    private List<ChoiceOption> DeviceChoices()
    {
        var devices = display.Devices;
        var labels = devices.GroupBy(d => $"{d.Reading.Model} · {d.Reading.Transport}", StringComparer.OrdinalIgnoreCase);
        var choices = new List<ChoiceOption>();
        foreach (var group in labels)
        {
            int count = group.Count(), index = 0;
            foreach (var device in group)
            {
                index++;
                string label = group.Key + (count > 1 ? $" · {index}" : "");
                choices.Add(new(device.DeviceId, label));
            }
        }
        if (!string.IsNullOrEmpty(display.FixedDeviceId) && choices.All(c => c.Value != display.FixedDeviceId))
        {
            var missing = display.PeekDevice(display.FixedDeviceId);
            choices.Insert(0, new(display.FixedDeviceId, missing.Reading.Model + " · 未连接"));
        }
        if (choices.Count == 0) choices.Add(new("", "等待鼠标连接", false));
        return choices;
    }

    private void SyncPopupSettings(bool forcePopup = false, bool forceMenu = false, bool readStartup = false)
    {
        bool syncPopup = forcePopup || popup.Visible;
        bool syncMenu = forceMenu || menu.Visible || settingsItem.DropDown.Visible;
        // Hidden controls are refreshed when opened; polling and rotation only
        // need to keep the shell icons and model up to date in the background.
        if (!syncPopup && !syncMenu) return;
        if (readStartup) startupEnabled = IsStartupEnabled();
        var choices = DeviceChoices();
        if (syncMenu)
        {
            refreshItem.Enabled = !refreshing;
            refreshItem.Text = refreshing ? "正在刷新…" : "立即刷新";
            startupItem.Checked = startupEnabled;
            if (percentageItem.Checked != showPercentage) percentageItem.Checked = showPercentage;
            if (lowBatteryItem.Checked != lowBatteryEnabled) lowBatteryItem.Checked = lowBatteryEnabled;
            lowBatteryItem.Text = $"低电通知（低于 {lowBatteryThreshold}%）";
            foreach (ToolStripMenuItem choice in pollIntervalItem.DropDownItems) choice.Checked = (int)choice.Tag! == pollSeconds;
            foreach (ToolStripMenuItem choice in rotationItem.DropDownItems) choice.Checked = (int)choice.Tag! == rotateSeconds;
            foreach (ToolStripMenuItem choice in modeItem.DropDownItems) choice.Checked = (MouseDisplayMode)choice.Tag! == display.Mode;
            bool multipleDevices = SettingsVisibility.ForDevices(display.Devices);
            bool recoverTarget = display.Mode == MouseDisplayMode.Fixed
                && SettingsVisibility.NeedsTargetRecovery(display.Devices, display.FixedDeviceId);
            modeItem.Visible = multipleDevices;
            fixedTargetItem.Visible = display.Mode == MouseDisplayMode.Fixed && (multipleDevices || recoverTarget);
            fixedTargetItem.Text = multipleDevices ? "固定目标" : "显示设备";
            fixedTargetItem.Enabled = choices.Any(c => c.Enabled);
            rotationItem.Visible = multipleDevices && display.Mode == MouseDisplayMode.Rotate;
            rotationItem.Enabled = display.Mode == MouseDisplayMode.Rotate;
            multiSettingsSeparator.Visible = modeItem.Available || fixedTargetItem.Available || rotationItem.Available;
            string signature = string.Join("\n", choices.Select(c => c.Value + "|" + c.Text + "|" + c.Enabled));
            if (signature != targetsSignature)
            {
                foreach (ToolStripItem old in fixedTargetItem.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
                fixedTargetItem.DropDownItems.Clear();
                foreach (var device in choices)
                {
                    var item = new ToolStripMenuItem(device.Text) { Tag = device.Value, Enabled = device.Enabled };
                    item.Click += (_, _) => SetFixedTarget(device.Value);
                    fixedTargetItem.DropDownItems.Add(item);
                }
                targetsSignature = signature;
            }
            foreach (ToolStripMenuItem choice in fixedTargetItem.DropDownItems) choice.Checked = (string)choice.Tag! == display.FixedDeviceId;
        }
        if (syncPopup)
        {
            popup.PollSeconds = pollSeconds;
            popup.SetDisplayOptions(display.Mode, display.FixedDeviceId, choices, pollSeconds, rotateSeconds);
            var details = display.GetDetails(detailsDeviceId);
            var reading = details.Reading;
            popup.SetState(new(reading.Percent, reading.Charging, reading.Message, reading.Timestamp, reading.Transport, reading.Model, details.Stale),
                refreshing, startupEnabled, showPercentage, lowBatteryEnabled, lowBatteryThreshold);
            popup.SetOtherDevices(details.DeviceId, display.Devices);
        }
    }

    private void SetStartup(bool enabled)
    {
#if UI_QA
        SyncPopupSettings(readStartup: true);
#else
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue("VgnBattery", CurrentStartupCommand(), RegistryValueKind.String);
            else key.DeleteValue("VgnBattery", false);
        }
        catch (Exception ex) { Notify("未能更改开机启动", ex.Message, ToolTipIcon.Warning); }
        SyncPopupSettings(readStartup: true);
#endif
    }

    private static bool IsStartupEnabled()
    {
#if UI_QA
        return false;
#else
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return StartupRegistration.MatchesCurrentCommand(key?.GetValue("VgnBattery") as string, CurrentStartupCommand());
        }
        catch { return false; }
#endif
    }

    private static string CurrentStartupCommand() => StartupRegistration.BuildCommand(Environment.ProcessPath, AppContext.BaseDirectory);
    private static void MigrateExistingStartupRegistration()
    {
#if !UI_QA
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable:true);
            string? registered = key?.GetValue("VgnBattery") as string;
            if (string.IsNullOrWhiteSpace(registered)) return;
            string command = CurrentStartupCommand();
            if (StartupRegistration.NeedsMigration(registered, command)) key!.SetValue("VgnBattery", command, RegistryValueKind.String);
        }
        catch { /* A stale item stays unchecked, and the user can explicitly retry. */ }
#endif
    }

    internal static string ChargeLabel(BatteryView view) => view.Percent is null ? "等待鼠标连接"
        : view.Charging ? "正在充电" : view.Message.Contains("已充满", StringComparison.Ordinal) ? "已充满" : "电池供电";

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new AppSettings
            {
                ShowPercentage = showPercentage, LowBatteryEnabled = lowBatteryEnabled, LowBatteryThreshold = lowBatteryThreshold,
                PollSeconds = pollSeconds, RotateSeconds = rotateSeconds, Mode = display.Mode.ToString(), FixedDeviceId = display.FixedDeviceId
            }));
            File.Move(temporary, SettingsPath, true);
        }
        catch (Exception ex) { Notify("设置暂未保存", ex.Message, ToolTipIcon.Warning); }
    }

    private static bool UsesLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch { return false; }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!exiting && popup.IsHandleCreated && !popup.IsDisposed)
            try { popup.BeginInvoke(() => { lightTaskbar = UsesLightTaskbar(); UpdateDisplay(); }); } catch (InvalidOperationException) { }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!exiting && popup.IsHandleCreated && !popup.IsDisposed)
            try { popup.BeginInvoke(() => { if (popup.Visible) popup.PositionNearTray(); UpdateDisplay(); }); } catch (InvalidOperationException) { }
    }

    protected override void ExitThreadCore()
    {
        if (exiting) return;
        exiting = true;
        timer.Stop(); timer.Dispose();
        rotationTimer.Stop(); rotationTimer.Dispose();
        deviceDebounce.Stop(); deviceDebounce.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        foreach (var host in icons.Values) host.Dispose();
        icons.Clear();
        menu.Dispose();
        popup.Dispose();
        base.ExitThreadCore();
    }

    private sealed class AppSettings
    {
        public bool ShowPercentage { get; set; } = true;
        public bool LowBatteryEnabled { get; set; } = true;
        public int LowBatteryThreshold { get; set; } = 20;
        public int PollSeconds { get; set; } = DefaultPollSeconds;
        public int RotateSeconds { get; set; } = DefaultRotateSeconds;
        public string Mode { get; set; } = nameof(MouseDisplayMode.Fixed);
        public string FixedDeviceId { get; set; } = "";
    }
    internal sealed record BatteryView(int? Percent, bool Charging, string Message, DateTimeOffset Timestamp, string Transport, string Model = "鼠标电量", bool Stale = false);
}

internal sealed class BatteryPopup : Form
{
    private static readonly Color Background = Color.FromArgb(27, 30, 36);
    private static readonly Color Secondary = Color.FromArgb(158, 168, 183);
    private readonly Label title;
    private readonly Label connection;
    private readonly Label percentage;
    private readonly Label chargeState;
    private readonly Label message;
    private readonly Label updated;
    private readonly Label hint;
    private readonly CheckBox startup;
    private readonly CheckBox showPercentage;
    private readonly CheckBox lowBattery;
    private readonly ThresholdStepper notificationThreshold;
    private readonly StyledChoiceButton modeChoice;
    private readonly StyledChoiceButton fixedChoice;
    private readonly StyledChoiceButton pollChoice;
    private readonly StyledChoiceButton rotationChoice;
    private readonly BatteryCanvas battery;
    private readonly TableLayoutPanel contentLayout;
    private readonly OtherDevicesPanel otherDevices;
    private readonly Panel viewport;
    private readonly SettingsDisclosureButton settingsDisclosure;
    private readonly TableLayoutPanel settingsContainer;
    private readonly TableLayoutPanel settingsContent;
    private readonly Panel modeRow;
    private readonly Panel fixedRow;
    private readonly Panel rotationRow;
    private readonly Label fixedRowLabel;
    private bool settingsExpanded;
    private bool hasMultipleDeviceSettings;
    private bool hasTargetRecovery;
    private int settingsBodyLogicalHeight = 184;
    private (bool Expanded, bool Multiple, bool Recovery, MouseDisplayMode Mode, int Dpi)? settingsLayoutKey;
    private bool settingState;
    private const int PagePadding = 20;
    private const int SettingsHeaderHeight = 32;
    private const int SettingsCardInset = 10;
    private const int SettingsCardTopGap = 8;
    private const int StaticRowsHeight = 32 + 26 + 86 + 36 + 22 + 18;
    internal bool SettingsExpanded => settingsExpanded;
    internal bool HasMultipleDeviceSettings => hasMultipleDeviceSettings;
    internal bool HasTargetRecovery => hasTargetRecovery;
    public event EventHandler? StartupRequested;
    public event EventHandler? PercentageRequested;
    public event EventHandler? LowBatteryOptionsRequested;
    public event EventHandler? TestNotificationRequested;
    public event EventHandler? DevicesChanged;
    public event EventHandler? PollRequested;
    public event EventHandler? ModeRequested;
    public event EventHandler? FixedTargetRequested;
    public event EventHandler? RotationRequested;
    public bool StartupChecked => startup.Checked;
    public bool PercentageChecked => showPercentage.Checked;
    public bool LowBatteryChecked => lowBattery.Checked;
    public int NotificationThreshold => (int)notificationThreshold.Value;
    public MouseDisplayMode SelectedDisplayMode => Enum.TryParse<MouseDisplayMode>(modeChoice.SelectedValue, out var mode) ? mode : MouseDisplayMode.Fixed;
    public string SelectedFixedDeviceId => fixedChoice.SelectedValue;
    public int SelectedPollSeconds => int.TryParse(pollChoice.SelectedValue, out int seconds) ? seconds : TrayApp.DefaultPollSeconds;
    public int SelectedRotateSeconds => int.TryParse(rotationChoice.SelectedValue, out int seconds) ? seconds : TrayApp.DefaultRotateSeconds;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int PollSeconds { get; set; } = TrayApp.DefaultPollSeconds;

    public BatteryPopup()
    {
        // AutoScaleDimensions causes an immediate layout unless suspended. Keep the
        // entire 96-DPI control tree intact until all bounds/fonts are established.
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(350, PagePadding * 2 + StaticRowsHeight + SettingsCardTopGap + SettingsCardInset * 2 + SettingsHeaderHeight);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
#if UI_QA
        ShowInTaskbar = true;
#endif
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Background;
        ForeColor = Color.FromArgb(239, 243, 250);
        Font = new Font("Microsoft YaHei UI", 9F);
        DoubleBuffered = true;
        Padding = new Padding(PagePadding);
        Text = "鼠标电量";

        contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = StaticRowsHeight + SettingsCardTopGap + SettingsCardInset * 2 + SettingsHeaderHeight, ColumnCount = 1, RowCount = 8,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        var layout = contentLayout;
        layout.SuspendLayout();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 32, 26, 86, 36, 22, 0, SettingsCardTopGap + SettingsCardInset * 2 + SettingsHeaderHeight, 18 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        title = MakeLabel("鼠标电量", 13F, FontStyle.Bold);
        title.TextAlign = ContentAlignment.MiddleLeft;
        heading.Controls.Add(title, 0, 0);
        var close = new Button
        {
            Text = "×", Dock = DockStyle.Fill, Margin = Padding.Empty, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 15F), ForeColor = Secondary, BackColor = Background,
            Cursor = Cursors.Hand, AccessibleName = "关闭电量窗口", UseVisualStyleBackColor = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(52, 60, 72);
        close.Click += (_, _) => Hide();
        heading.Controls.Add(close, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        connection = MakeLabel("正在查找鼠标…", 9F);
        connection.ForeColor = Secondary;
        connection.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(connection, 0, 1);

        var gauge = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        gauge.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        gauge.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gauge.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        battery = new BatteryCanvas
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 20, 18, 20),
            BackColor = Background, AccessibleName = "鼠标电池"
        };
        gauge.Controls.Add(battery, 0, 0);
        var amount = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Background
        };
        amount.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        amount.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        amount.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        percentage = MakeLabel("—", 32F, FontStyle.Bold);
        percentage.Font = new Font("Segoe UI", 32F, FontStyle.Bold);
        percentage.AutoEllipsis = false;
        percentage.TextAlign = ContentAlignment.MiddleLeft;
        amount.Controls.Add(percentage, 0, 0);
        chargeState = MakeLabel("等待电量数据", 9F);
        chargeState.ForeColor = Secondary;
        chargeState.TextAlign = ContentAlignment.MiddleLeft;
        amount.Controls.Add(chargeState, 0, 1);
        gauge.Controls.Add(amount, 1, 0);
        layout.Controls.Add(gauge, 0, 2);

        message = MakeLabel("", 9F);
        message.ForeColor = Secondary;
        message.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(message, 0, 3);
        updated = MakeLabel("", 8F);
        updated.ForeColor = Secondary;
        updated.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(updated, 0, 4);
        otherDevices = new OtherDevicesPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Background,
            ForeColor = ForeColor, Font = Font, Visible = false
        };
        layout.Controls.Add(otherDevices, 0, 5);
        var settingsCard = new SettingsCard
        {
            Dock = DockStyle.Fill, Padding = new Padding(SettingsCardInset), Margin = new Padding(0, SettingsCardTopGap, 0, 0)
        };
        settingsCard.SuspendLayout();
        settingsContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = SettingsPalette.Card
        };
        settingsContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, SettingsHeaderHeight));
        settingsContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        settingsDisclosure = new SettingsDisclosureButton
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        settingsDisclosure.Click += (_, _) => SetSettingsExpanded(!settingsExpanded);
        settingsContainer.Controls.Add(settingsDisclosure, 0, 0);
        settingsContent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = SettingsPalette.Card, Visible = false
        };
        var settings = settingsContent;
        settings.SuspendLayout();
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 34, 34, 34, 8, 40, 34, 34, 34, 34 })
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        startup = MakeCheckBox("开机自动运行", divider:true);
        startup.AccessibleDescription = "登录 Windows 后自动在任务栏显示鼠标电量";
        startup.CheckedChanged += (_, _) => { if (!settingState) StartupRequested?.Invoke(this, EventArgs.Empty); };
        settings.Controls.Add(startup, 0, 0);
        showPercentage = MakeCheckBox("托盘显示电量数字", divider:true);
        showPercentage.CheckedChanged += (_, _) => { if (!settingState) PercentageRequested?.Invoke(this, EventArgs.Empty); };
        settings.Controls.Add(showPercentage, 0, 1);

        lowBattery = MakeCheckBox("低电通知");
        settings.Controls.Add(lowBattery, 0, 2);
        var lowBatteryRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = SettingsPalette.Card
        };
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        lowBatteryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        lowBatteryRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var below = MakeLabel("低于", 9F);
        below.TextAlign = ContentAlignment.MiddleLeft;
        below.ForeColor = Secondary;
        below.BackColor = SettingsPalette.Card;
        below.Margin = new Padding(4, 0, 0, 0);
        lowBatteryRow.Controls.Add(below, 0, 0);
        notificationThreshold = new ThresholdStepper
        {
            Value = 20, Anchor = AnchorStyles.Left | AnchorStyles.Right, Size = new Size(130, 30),
            Font = new Font("Microsoft YaHei UI", 9.5F), Margin = Padding.Empty
        };
        notificationThreshold.ValueChanged += (_, _) => { if (!settingState) LowBatteryOptionsRequested?.Invoke(this, EventArgs.Empty); };
        lowBattery.CheckedChanged += (_, _) =>
        {
            notificationThreshold.Enabled = lowBattery.Checked;
            if (!settingState) LowBatteryOptionsRequested?.Invoke(this, EventArgs.Empty);
        };
        lowBatteryRow.Controls.Add(notificationThreshold, 1, 0);

        var testNotification = new StyledButton
        {
            Text = "测试通知", Anchor = AnchorStyles.Left | AnchorStyles.Right, Size = new Size(76, 30),
            Font = new Font("Microsoft YaHei UI", 9F), Margin = Padding.Empty,
            AccessibleDescription = "发送一条 Windows 测试通知，不影响低电提醒状态"
        };
        testNotification.Click += (_, _) => TestNotificationRequested?.Invoke(this, EventArgs.Empty);
        lowBatteryRow.Controls.Add(testNotification, 3, 0);
        settings.Controls.Add(lowBatteryRow, 0, 4);
        modeChoice = MakeChoice("托盘显示方式");
        modeChoice.SelectionChanged += (_, _) => { if (!settingState) ModeRequested?.Invoke(this, EventArgs.Empty); };
        modeRow = AddChoiceRow(settings, 5, "显示方式", modeChoice);
        fixedChoice = MakeChoice("固定显示的鼠标");
        fixedChoice.SelectionChanged += (_, _) => { if (!settingState) FixedTargetRequested?.Invoke(this, EventArgs.Empty); };
        fixedRow = AddChoiceRow(settings, 6, "固定目标", fixedChoice);
        fixedRowLabel = fixedRow.Controls.OfType<Label>().Single();
        pollChoice = MakeChoice("鼠标电量查询间隔");
        pollChoice.SelectionChanged += (_, _) => { if (!settingState) PollRequested?.Invoke(this, EventArgs.Empty); };
        AddChoiceRow(settings, 7, "查询间隔", pollChoice);
        rotationChoice = MakeChoice("托盘图标轮换间隔");
        rotationChoice.SelectionChanged += (_, _) => { if (!settingState) RotationRequested?.Invoke(this, EventArgs.Empty); };
        rotationRow = AddChoiceRow(settings, 8, "轮换间隔", rotationChoice);
        settingsContainer.Controls.Add(settings, 0, 1);
        settingsCard.Controls.Add(settingsContainer);
        layout.Controls.Add(settingsCard, 0, 6);
        hint = MakeLabel("自动查询 · 每 30 秒", 8F);
        hint.ForeColor = Color.FromArgb(113, 125, 143);
        hint.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(hint, 0, 7);
        viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Background, Margin = Padding.Empty };
        viewport.Controls.Add(layout);
        Controls.Add(viewport);

        EnableWindowDragging(this);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => WriteLayoutDiagnostics();
        DpiChanged += (_, _) => BeginInvoke(() => { UpdateSettingsPresentation(); WriteLayoutDiagnostics(); });

        // Establish a single scaling baseline only after every child has been added.
        AutoScaleDimensions = new SizeF(96, 96);
        _ = Handle;
        settings.ResumeLayout(false);
        settingsCard.ResumeLayout(false);
        layout.ResumeLayout(false);
        ResumeLayout(true);
        SetDisplayOptions(MouseDisplayMode.Fixed, "", [new ChoiceOption("", "等待鼠标连接", false)], TrayApp.DefaultPollSeconds, TrayApp.DefaultRotateSeconds);
        WriteLayoutDiagnostics();
    }

    protected override void SetVisibleCore(bool value)
    {
        // Every newly opened panel starts with only its setting heading. Hiding
        // also closes the disclosure so it does not keep a large hidden layout.
        if (settingsDisclosure is not null && ((value && !Visible) || (!value && Visible)))
            SetSettingsExpanded(false);
        base.SetVisibleCore(value);
    }

    public void SetOtherDevices(string currentDeviceId, IReadOnlyList<MouseBattery> devices)
    {
        if (IsDisposed) return;
        int previousCount = otherDevices.Rows.Count;
        bool listChanged = otherDevices.SetDevices(currentDeviceId, devices) && previousCount != otherDevices.Rows.Count;
        hasMultipleDeviceSettings = SettingsVisibility.ForDevices(devices);
        hasTargetRecovery = SelectedDisplayMode == MouseDisplayMode.Fixed
            && SettingsVisibility.NeedsTargetRecovery(devices, fixedChoice.SelectedValue);
        if (!UpdateSettingsPresentation() && listChanged) UpdatePopupLayout();
    }

    internal void SetSettingsExpanded(bool expanded)
    {
        if (IsDisposed || settingsDisclosure is null || settingsExpanded == expanded) return;
        settingsExpanded = expanded;
        UpdateSettingsPresentation();
        if (!expanded) viewport.AutoScrollPosition = Point.Empty;
    }

    private int SettingsCardLogicalHeight => SettingsCardTopGap + SettingsCardInset * 2 + SettingsHeaderHeight
        + (settingsExpanded ? settingsBodyLogicalHeight : 0);

    private int DesiredPopupHeight => ScaleLogical(PagePadding * 2 + StaticRowsHeight + SettingsCardLogicalHeight
        + OtherDevicesPanel.LogicalHeightForRows(otherDevices.Rows.Count));

    private int ScaleLogical(int value) => (int)Math.Round(value * DeviceDpi / 96F);

    private bool UpdateSettingsPresentation()
    {
        var mode = SelectedDisplayMode;
        var key = (settingsExpanded, hasMultipleDeviceSettings, hasTargetRecovery, mode, DeviceDpi);
        if (settingsLayoutKey == key) return false;
        settingsLayoutKey = key;
        bool showFixed = mode == MouseDisplayMode.Fixed && (hasMultipleDeviceSettings || hasTargetRecovery);
        bool showRotation = hasMultipleDeviceSettings && mode == MouseDisplayMode.Rotate;
        modeRow.Visible = hasMultipleDeviceSettings;
        fixedRow.Visible = showFixed;
        rotationRow.Visible = showRotation;
        fixedRowLabel.Text = hasMultipleDeviceSettings ? "固定目标" : "显示设备";
        settingsContent.SuspendLayout();
        int[] heights = [34, 34, 34, 8, 40, hasMultipleDeviceSettings ? 34 : 0,
            showFixed ? 34 : 0, 34, showRotation ? 34 : 0];
        for (int i=0;i<heights.Length;i++) settingsContent.RowStyles[i].Height = ScaleLogical(heights[i]);
        settingsBodyLogicalHeight = heights.Sum();
        settingsContent.Visible = settingsExpanded;
        settingsDisclosure.Expanded = settingsExpanded;
        settingsContent.ResumeLayout(true);
        UpdateHint();
        UpdatePopupLayout();
        return true;
    }

    private void UpdatePopupLayout()
    {
        if (IsDisposed) return;
        int listHeight = ScaleLogical(OtherDevicesPanel.LogicalHeightForRows(otherDevices.Rows.Count));
        contentLayout.SuspendLayout();
        settingsContainer.RowStyles[0].Height = ScaleLogical(SettingsHeaderHeight);
        settingsContainer.RowStyles[1].Height = settingsExpanded ? ScaleLogical(settingsBodyLogicalHeight) : 0;
        contentLayout.RowStyles[5].Height = listHeight;
        contentLayout.RowStyles[6].Height = ScaleLogical(SettingsCardLogicalHeight);
        contentLayout.Height = ScaleLogical(StaticRowsHeight + SettingsCardLogicalHeight) + listHeight;
        otherDevices.Visible = listHeight > 0;
        contentLayout.ResumeLayout(true);

        // Opening positions are handled separately. Refreshes retain a dragged
        // window's location and only clamp its growing bounds to the same screen.
        if (!Visible) { Height = DesiredPopupHeight; return; }
        FitToWorkingArea(Screen.FromRectangle(Bounds).WorkingArea);
    }

    private void FitToWorkingArea(Rectangle area)
    {
        int margin = (int)Math.Round(12 * DeviceDpi / 96F);
        Height = Math.Min(DesiredPopupHeight, Math.Max(200, area.Height - margin * 2));
        int left = Math.Clamp(Left, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        int top = Math.Clamp(Top, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - Height - margin));
        Location = new Point(left, top);
    }

    public void SetDisplayOptions(MouseDisplayMode mode, string fixedId, IReadOnlyList<ChoiceOption> devices, int pollSeconds, int rotateSeconds)
    {
        if (IsDisposed) return;
        settingState = true;
        try
        {
            modeChoice.SetChoices(Enum.GetValues<MouseDisplayMode>().Select(m => new ChoiceOption(m.ToString(), TrayApp.ModeLabel(m))).ToArray(), mode.ToString());
            fixedChoice.SetChoices(devices, fixedId);
            fixedChoice.Enabled = mode == MouseDisplayMode.Fixed && devices.Any(d => d.Enabled);
            pollChoice.SetChoices(TrayApp.PollChoices.Select(s => new ChoiceOption(s.ToString(), $"每 {s} 秒")).ToArray(), pollSeconds.ToString());
            rotationChoice.SetChoices(TrayApp.RotateChoices.Select(s => new ChoiceOption(s.ToString(), $"每 {s} 秒")).ToArray(), rotateSeconds.ToString());
            rotationChoice.Enabled = mode == MouseDisplayMode.Rotate;
            PollSeconds = pollSeconds;
        }
        finally { settingState = false; }
        UpdateSettingsPresentation();
    }

    public void SetState(TrayApp.BatteryView view, bool refreshing, bool startupEnabled, bool showTrayPercentage, bool lowBatteryEnabled, int lowBatteryThreshold)
    {
        if (IsDisposed) return;
        settingState = true;
        try
        {
            startup.Checked = startupEnabled;
            showPercentage.Checked = showTrayPercentage;
            lowBattery.Checked = lowBatteryEnabled;
            notificationThreshold.Value = Math.Clamp(lowBatteryThreshold, 1, 99);
            notificationThreshold.Enabled = lowBatteryEnabled;
        }
        finally { settingState = false; }
        title.Text = string.IsNullOrWhiteSpace(view.Model) ? "鼠标电量" : view.Model;
        Text = title.Text + " · 鼠标电量";
        percentage.Text = view.Percent is int p ? $"{p}%" : "—";
        percentage.ForeColor = view.Stale || view.Percent is null ? Secondary : Color.FromArgb(115, 227, 167);
        bool disconnected = view.Transport == "未连接";
        connection.Text = disconnected ? "设备未连接"
            : view.Stale ? "上次读数 · 鼠标暂未响应"
            : view.Percent is not null ? "已连接 · " + (string.IsNullOrWhiteSpace(view.Transport) ? "鼠标" : view.Transport)
            : refreshing ? "正在读取电量…" : "暂未读取到电量";
        connection.ForeColor = view.Percent is not null && !view.Stale ? Color.FromArgb(118, 215, 178) : Secondary;
        chargeState.Text = disconnected ? "等待重新连接" : view.Stale ? "等待鼠标唤醒"
            : view.Percent is int remaining && lowBatteryEnabled && remaining < lowBatteryThreshold && !view.Charging ? "电量较低，建议充电"
            : TrayApp.ChargeLabel(view);
        message.Text = disconnected ? view.Message : view.Stale ? "移动鼠标后会自动更新，当前显示最近一次读数。" : view.Message;
        UpdateHint();
        updated.Text = view.Timestamp == DateTimeOffset.MinValue ? "尚无读数"
            : (view.Stale ? "上次读数 · " : view.Percent is null ? "最近尝试 · " : "已更新 · ") + view.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        battery.Percent = view.Percent;
        battery.Charging = !view.Stale && view.Charging;
        battery.Stale = view.Stale;
        battery.Invalidate();
        AccessibleDescription = view.Percent is int value ? $"{title.Text} 剩余电量 {value}%，{connection.Text}，{chargeState.Text}" : view.Message;
    }

    private void UpdateHint() => hint.Text = $"自动查询 · 每 {PollSeconds} 秒"
        + (hasMultipleDeviceSettings && SelectedDisplayMode == MouseDisplayMode.Rotate ? $" · 每 {SelectedRotateSeconds} 秒轮换" : "");

    public void PositionNearTray()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        int margin = (int)Math.Round(12 * DeviceDpi / 96F);
        int desiredHeight = DesiredPopupHeight;
        Height = Math.Min(desiredHeight, Math.Max(200, area.Height - margin * 2));
        int x = Math.Clamp(cursor.X - Width + margin * 2, area.Left + margin, Math.Max(area.Left + margin, area.Right - Width - margin));
        int y = cursor.Y < area.Top + margin * 2 ? area.Top + margin : area.Bottom - Height - margin;
        Location = new Point(x, Math.Max(area.Top + margin, y));
    }

    internal static bool CanDragFrom(Control control) => control is not ButtonBase and not TextBoxBase and not ComboBox and not LinkLabel and not ScrollBar and not UpDownBase and not ThresholdStepper;

    private void EnableWindowDragging(Control control)
    {
        if (!CanDragFrom(control)) return;
        control.MouseDown += DragWindow;
        foreach (Control child in control.Controls) EnableWindowDragging(child);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    protected override void WndProc(ref Message m)
    {
        const int WmDeviceChange = 0x0219;
        if (m.Msg == WmDeviceChange && m.WParam.ToInt64() is 7 or 0x8000 or 0x8004)
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private void WriteLayoutDiagnostics()
    {
        try
        {
            var nodes = new List<object>();
            void Add(Control node)
            {
                nodes.Add(new { Type = node.GetType().Name, node.Text, Bounds = new { node.Left, node.Top, node.Width, node.Height }, Font = new { node.Font.Name, node.Font.SizeInPoints } });
                foreach (Control child in node.Controls) Add(child);
            }
            Add(this);
            var data = new { DeviceDpi, ClientSize = new { ClientSize.Width, ClientSize.Height }, AutoScaleDimensions, CurrentAutoScaleDimensions, Controls = nodes };
#if UI_QA
            string directory = Path.Combine(AppContext.BaseDirectory, "qa-data");
#else
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgnBattery");
#endif
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "layout.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Diagnostics never interfere with the tray. */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(65, 74, 88));
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ClassStyle |= 0x00020000; return value; }
    }

    private static Label MakeLabel(string text, float fontSize, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", fontSize, style),
        AutoEllipsis = true, UseMnemonic = false, BackColor = Background
    };

    private static CheckBox MakeCheckBox(string text, bool divider = false) => new StyledCheckBox(divider)
    {
        Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty,
        Font = new Font("Microsoft YaHei UI", 9.5F)
    };

    private static StyledChoiceButton MakeChoice(string name) => new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 28, Margin = Padding.Empty,
        Font = new Font("Microsoft YaHei UI", 9F), AccessibleName = name
    };

    private static Panel AddChoiceRow(TableLayoutPanel settings, int row, string text, StyledChoiceButton choice)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty,
            BackColor = SettingsPalette.Card
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = MakeLabel(text, 9F);
        label.BackColor = SettingsPalette.Card;
        label.ForeColor = Secondary;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Margin = new Padding(4, 0, 0, 0);
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(choice, 1, 0);
        settings.Controls.Add(panel, 0, row);
        return panel;
    }
}
internal sealed class BatteryCanvas : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int? Percent { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Charging { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Stale { get; set; }
    public BatteryCanvas() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var outline = Stale || Percent is null ? Color.FromArgb(146, 154, 166) : Color.FromArgb(115, 227, 167);
        TrayIconRenderer.DrawBattery(e.Graphics, new RectangleF(1, 2, Width - 3, Height - 5), Percent, Charging, outline, true, Stale);
    }
}

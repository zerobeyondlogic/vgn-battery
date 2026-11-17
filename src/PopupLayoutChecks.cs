#if UI_QA
namespace VgnBattery;

internal static class PopupLayoutChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        using var popup=new BatteryPopup { Opacity=0,ShowInTaskbar=false,Enabled=false };
        var at=DateTimeOffset.Now;
        MouseBattery Device(string id,int? percent=50) => new(id,new(percent,false,"",at,"USB 有线","Layout test mouse"));
        var pair=new[] { Device("current"),Device("other") };
        var area=Screen.PrimaryScreen!.WorkingArea;
        popup.Location=new Point(area.Left+24,area.Top+24);
        popup.Show();
        try
        {
            check(!popup.SettingsExpanded,"settings start collapsed");
            popup.SetOtherDevices("current",pair);
            await Task.Delay(30);
            check(area.Contains(popup.Bounds),"overview fits within the working area");
            Control Find(Control root,string name) => root.Controls.Cast<Control>()
                .SelectMany(c=>new[]{c}.Concat(Descendants(c))).Single(c=>c.AccessibleName==name);
            IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>()
                .SelectMany(c=>new[]{c}.Concat(Descendants(c)));
            var modes=Find(popup,"托盘显示方式");
            var targets=Find(popup,"固定显示的鼠标");
            var rotation=Find(popup,"托盘图标轮换间隔");
            var polling=Find(popup,"鼠标电量查询间隔");
            int collapsedHeight=popup.Height;
            popup.SetDisplayOptions(MouseDisplayMode.Fixed,"current",[new("current","Current mouse"),new("other","Other mouse")],30,5);
            popup.SetSettingsExpanded(true);
            check(popup.SettingsExpanded && popup.Height>collapsedHeight,"expanding settings adds usable space");
            check(popup.HasMultipleDeviceSettings && modes.Visible && targets.Visible && !rotation.Visible,"fixed mode only shows its relevant multi-device options");
            popup.SetDisplayOptions(MouseDisplayMode.Rotate,"current",[new("current","Current mouse"),new("other","Other mouse")],30,5);
            check(modes.Visible && rotation.Visible && !targets.Visible,"rotation only shows its relevant multi-device options");
            popup.SetDisplayOptions(MouseDisplayMode.Tiled,"current",[new("current","Current mouse"),new("other","Other mouse")],30,5);
            check(modes.Visible && !rotation.Visible && !targets.Visible,"tiled mode hides fixed-target and rotation selectors");
            popup.SetOtherDevices("current",[Device("current"),Device("unused",null)]);
            check(!popup.HasMultipleDeviceSettings && !modes.Visible && !rotation.Visible && !targets.Visible && polling.Visible,"one usable mouse hides all multi-device options but retains polling");
            check(popup.SelectedDisplayMode==MouseDisplayMode.Tiled,"hiding single-device controls preserves the saved display mode");
            popup.SetDisplayOptions(MouseDisplayMode.Fixed,"missing",[new("missing","Unavailable mouse"),new("current","Current mouse")],30,5);
            popup.SetOtherDevices("missing",[Device("current")]);
            check(popup.HasTargetRecovery && targets.Visible && !modes.Visible,"a missing fixed target retains a compact recovery selector");
            popup.SetOtherDevices("current",pair);
            var position=popup.Location;
            popup.SetOtherDevices("current",[Device("current",49),Device("other",48)]);
            check(popup.Location==position,"a reading refresh preserves the popup position");
            foreach(var mode in Enum.GetValues<MouseDisplayMode>())
            {
                popup.SetDisplayOptions(mode,"current",[new("current","Current mouse"),new("other","Other mouse")],30,5);
                popup.SetOtherDevices("current",pair);
            }
            var many=Enumerable.Range(0,30).Select(i=>Device(i==0?"current":"other-"+i)).ToArray();
            popup.SetOtherDevices("current",many);
            await Task.Delay(30);
            var viewport=popup.Controls.OfType<Panel>().Single();
            check(area.Contains(popup.Bounds) && viewport.AutoScroll && viewport.VerticalScroll.Visible,"a long device list stays on screen and scrolls");
            viewport.AutoScrollPosition=new Point(0,80);
            var scroll=viewport.AutoScrollPosition;
            popup.SetOtherDevices("current",many.Select(d=>d with { Reading=d.Reading with { Percent=51 } }).ToArray());
            check(viewport.AutoScrollPosition==scroll,"a reading refresh preserves the overview scroll position");
            int expandedHeight=popup.Height;
            popup.SetOtherDevices("current",[Device("current")]);
            await Task.Delay(30);
            check(popup.Height<=expandedHeight && !viewport.VerticalScroll.Visible,"removing other devices removes their extra space");
            popup.SetSettingsExpanded(true);
            popup.Hide();
            popup.Show();
            check(!popup.SettingsExpanded,"reopening the popup resets settings to collapsed");
        }
        finally { popup.Hide(); }
    }
}
#endif

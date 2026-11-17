namespace VgnBattery;

internal static class SettingsVisibilityChecks
{
    internal static void Run(Action<bool,string> check)
    {
        MouseBattery Device(string id,int? percent,bool stale=false) =>
            new(id,new(percent,false,"",DateTimeOffset.Now,"USB","Same mouse"),stale);
        check(!SettingsVisibility.ForDevices([]),"empty device lists hide multi-device settings");
        check(!SettingsVisibility.ForDevices([Device("one",100)]),"a single mouse hides multi-device settings");
        check(!SettingsVisibility.ForDevices([Device("wired",100),Device("receiver",null)]),"an unresponsive unused receiver does not enable multi-device settings");
        check(SettingsVisibility.ForDevices([Device("one",0),Device("two",42)]),"two known mice including zero percent enable multi-device settings");
        check(SettingsVisibility.ForDevices([Device("one",100),Device("two",42,true)]),"sleeping known mice keep their multi-device controls");
        check(!SettingsVisibility.ForDevices([Device("same",100),Device("same",100)]),"duplicate interfaces do not inflate settings device counts");
        check(!SettingsVisibility.ForDevices([Device("",80),Device("one",100)]),"placeholders do not enable multi-device settings");
        check(SettingsVisibility.NeedsTargetRecovery([Device("new",100)],"old"),"a missing saved fixed target retains a recovery selector");
        check(SettingsVisibility.NeedsTargetRecovery([Device("old",null),Device("new",100)],"old"),"a never-responsive fixed target can be replaced by the usable mouse");
        check(!SettingsVisibility.NeedsTargetRecovery([Device("one",100)],"one"),"a working single target needs no recovery selector");
        check(!SettingsVisibility.NeedsTargetRecovery([Device("one",100)],""),"automatic first selection needs no recovery selector");
        var interfaces=new[]{Device("wired",100),Device("receiver",null)};
        check(SettingsVisibility.ForTray(MouseDisplayMode.Rotate,interfaces,[interfaces[1]]).Single().DeviceId=="wired","single usable mice do not rotate into an idle receiver");
        check(SettingsVisibility.ForTray(MouseDisplayMode.Tiled,interfaces,interfaces).Count==1,"single usable mice do not gain extra tiled receiver icons");
        check(SettingsVisibility.ForTray(MouseDisplayMode.Fixed,interfaces,[interfaces[1]]).Single().DeviceId=="receiver","single-device presentation never overrides an explicitly fixed target");
    }
}

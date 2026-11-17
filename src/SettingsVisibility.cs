namespace VgnBattery;

internal static class SettingsVisibility
{
    // A receiver that has never returned a battery reading is not evidence of
    // another usable mouse. Cached readings still count while a mouse sleeps.
    internal static bool ForDevices(IReadOnlyList<MouseBattery> devices) =>
        KnownDevices(devices).Take(2).Count() > 1;

    // Keep a way out of a saved, unavailable fixed target without exposing the
    // full multi-device settings when only one usable device is present.
    internal static bool NeedsTargetRecovery(IReadOnlyList<MouseBattery> devices,string fixedId) =>
        !string.IsNullOrEmpty(fixedId) && KnownDevices(devices).Any(d=>d.DeviceId!=fixedId)
        && !KnownDevices(devices).Any(d=>d.DeviceId==fixedId);

    internal static IReadOnlyList<MouseBattery> ForTray(MouseDisplayMode mode,
        IReadOnlyList<MouseBattery> devices,IReadOnlyList<MouseBattery> requested)
    {
        if (mode==MouseDisplayMode.Fixed) return requested;
        var known=KnownDevices(devices).Take(2).ToArray();
        return known.Length==1 ? known : requested;
    }

    private static IEnumerable<MouseBattery> KnownDevices(IReadOnlyList<MouseBattery> devices) =>
        devices.Where(d=>!string.IsNullOrWhiteSpace(d.DeviceId) && d.Reading.Percent is >=0 and <=100)
            .DistinctBy(d=>d.DeviceId,StringComparer.Ordinal);
}

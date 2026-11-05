using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CuePlayer.Audio;

public sealed record DeviceItem(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Lists output devices and reports when they change (plugged, unplugged, new default).</summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    /// <summary>Raised on a COM thread: marshal before touching the UI.</summary>
    public event Action? DevicesChanged;
    public event Action? DefaultChanged;

    public DeviceWatcher()
    {
        try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { }
    }

    public List<DeviceItem> List()
    {
        var list = new List<DeviceItem> { new(null, "Predefinita di Windows") };
        try
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add(new DeviceItem(d.ID, d.FriendlyName)); } catch { }
            }
        }
        catch { }
        return list;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();
    public void OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();
    public void OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) DefaultChanged?.Invoke();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator.Dispose();
    }
}

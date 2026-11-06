using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using PracticeMate.Core.Devices;

namespace PracticeMate.Audio.Windows;

/// <summary>
/// WASAPI 裝置列舉與建立（只使用 NAudio 的裝置列舉、擷取與輸出功能）。
/// 註冊端點通知，讓擷取/輸出裝置在被拔除或預設裝置變更時能停止並回報原因。
/// </summary>
public sealed class WasapiAudioSystem : IAudioDeviceFactory, IAudioDeviceCatalog, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly EndpointNotifications _notifications = new();

    public WasapiAudioSystem() => _enumerator.RegisterEndpointNotificationCallback(_notifications);

    public IReadOnlyList<AudioDeviceInfo> GetDevices(AudioDeviceKind kind)
    {
        var flow = Flow(kind);
        var defaultId = _enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia)
            ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID
            : null;
        return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, kind, d.ID == defaultId))
            .ToList();
    }

    public IAudioCaptureDevice CreateCapture(string deviceId)
    {
        var (device, followsDefault) = Resolve(DataFlow.Capture, deviceId);
        return new WasapiCaptureDevice(device, followsDefault, _notifications);
    }

    public IAudioRenderDevice CreateRender(string deviceId)
    {
        var (device, followsDefault) = Resolve(DataFlow.Render, deviceId);
        return new WasapiRenderDevice(device, followsDefault, _notifications);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_notifications);
        _enumerator.Dispose();
    }

    private static DataFlow Flow(AudioDeviceKind kind) => kind == AudioDeviceKind.Capture ? DataFlow.Capture : DataFlow.Render;

    private (MMDevice Device, bool FollowsDefault) Resolve(DataFlow flow, string deviceId)
    {
        var what = flow == DataFlow.Capture ? "錄音" : "輸出";
        if (deviceId == AudioDeviceInfo.DefaultDeviceId)
        {
            if (!_enumerator.HasDefaultAudioEndpoint(flow, Role.Multimedia))
            {
                throw new InvalidOperationException($"找不到可用的{what}裝置。");
            }
            return (_enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia), true);
        }
        try
        {
            var device = _enumerator.GetDevice(deviceId);
            if (device.State != DeviceState.Active)
            {
                throw new InvalidOperationException($"指定的{what}裝置目前無法使用（可能已被拔除）。");
            }
            return (device, false);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"找不到指定的{what}裝置，可能已被拔除。", ex);
        }
    }
}

/// <summary>把 IMMNotificationClient 的回呼轉成 .NET 事件（回呼在 COM 執行緒上，處理端不得阻塞）。</summary>
internal sealed class EndpointNotifications : IMMNotificationClient
{
    public event Action<string>? DeviceLost;

    public event Action<DataFlow, string>? DefaultChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState != DeviceState.Active)
        {
            DeviceLost?.Invoke(deviceId);
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId)
    {
        // 新裝置不影響進行中的串流
    }

    public void OnDeviceRemoved(string deviceId) => DeviceLost?.Invoke(deviceId);

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (role == Role.Multimedia)
        {
            DefaultChanged?.Invoke(flow, defaultDeviceId);
        }
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // 屬性變更（例如名稱）不影響串流
    }
}

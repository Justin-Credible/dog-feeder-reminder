using Meadow;
using System;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public class DeviceTimeManager
{
    const string Tag = "DeviceTimeManager";
    static readonly DateTime MinimumValidDeviceTime = new DateTime(2021, 1, 1);

    bool deviceTimeLogged;
    bool monitoringStarted;
    readonly TaskCompletionSource<bool> validTimeSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public DateTime CurrentDeviceTime => DateTime.Now;

    public string CurrentDeviceTimeText => FormatDeviceTime(CurrentDeviceTime);

    public Task WaitForValidTimeAsync()
    {
        if (IsDeviceTimeValid())
        {
            validTimeSource.TrySetResult(true);
            return Task.CompletedTask;
        }

        return validTimeSource.Task;
    }

    public void StartMonitoring()
    {
        if (monitoringStarted)
        {
            return;
        }

        monitoringStarted = true;
        _ = Task.Run(MonitorDeviceTimeAsync);
    }

    async Task MonitorDeviceTimeAsync()
    {
        while (!deviceTimeLogged)
        {
            if (IsDeviceTimeValid())
            {
                deviceTimeLogged = true;
                validTimeSource.TrySetResult(true);
                Logger.Info(Tag, $"Device time set to {CurrentDeviceTimeText}");
                return;
            }

            await Task.Delay(1000);
        }
    }

    static bool IsDeviceTimeValid()
    {
        return DateTime.Now >= MinimumValidDeviceTime;
    }

    static string FormatDeviceTime(DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss");
    }
}
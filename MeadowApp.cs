using Meadow;
using Meadow.Devices;
using Meadow.Foundation.Leds;
using Meadow.Peripherals.Leds;
using System;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public class MeadowApp : App<F7FeatherV2>
{
    RgbPwmLed onboardLed;
    WiFiManager wifiManager;
    DeviceTimeManager deviceTimeManager;
    WebServerManager webServerManager;

    public override async Task Initialize()
    {
        Logger.Info("MeadowApp", "Initialize...");

        onboardLed = new RgbPwmLed(
            redPwmPin: Device.Pins.OnboardLedRed,
            greenPwmPin: Device.Pins.OnboardLedGreen,
            bluePwmPin: Device.Pins.OnboardLedBlue,
            CommonType.CommonAnode);

        wifiManager = new WiFiManager(
            Device.NetworkAdapters.Primary<Meadow.Hardware.IWiFiNetworkAdapter>()
        );

        deviceTimeManager = new DeviceTimeManager();
        deviceTimeManager.StartMonitoring();

        var deviceName = string.IsNullOrWhiteSpace(Device.Information.DeviceName)
            ? "(unknown device name)"
            : Device.Information.DeviceName;

        webServerManager = new WebServerManager(
            wifiManager,
            deviceTimeManager,
            DateTimeOffset.UtcNow,
            deviceName,
            "cycling");

        await wifiManager.InitializeAsync();

        await base.Initialize();
    }

    public override Task Run()
    {
        Logger.Info("MeadowApp", "Run...");

        _ = Task.Run(webServerManager.StartAsync);

        return CycleColors(TimeSpan.FromMilliseconds(1000));
    }

    async Task CycleColors(TimeSpan duration)
    {
        Resolver.Log.Info("Cycle colors...");

        while (true)
        {
            await ShowColorPulse(Color.Blue, duration);
            await ShowColorPulse(Color.Cyan, duration);
            await ShowColorPulse(Color.Green, duration);
            await ShowColorPulse(Color.GreenYellow, duration);
            await ShowColorPulse(Color.Yellow, duration);
            await ShowColorPulse(Color.Orange, duration);
            await ShowColorPulse(Color.OrangeRed, duration);
            await ShowColorPulse(Color.Red, duration);
            await ShowColorPulse(Color.MediumVioletRed, duration);
            await ShowColorPulse(Color.Purple, duration);
            await ShowColorPulse(Color.Magenta, duration);
            await ShowColorPulse(Color.Pink, duration);
        }
    }

    async Task ShowColorPulse(Color color, TimeSpan duration)
    {
        await onboardLed.StartPulse(color, duration / 2);
        await Task.Delay(duration);
    }
}

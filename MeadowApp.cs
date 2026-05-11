using Meadow;
using Meadow.Devices;
using Meadow.Foundation.Leds;
using Meadow.Hardware;
using Meadow.Peripherals.Leds;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public class MeadowApp : App<F7FeatherV2>
{
    const string Tag = "MeadowApp";

    static readonly TimeSpan PlatformBlinkInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan FeedButtonHardwareDebounce = TimeSpan.FromMilliseconds(30);
    static readonly TimeSpan FeedButtonHardwareGlitch = TimeSpan.FromMilliseconds(5);
    static readonly TimeSpan FeedButtonDebounceInterval = TimeSpan.FromMilliseconds(120);

    RgbPwmLed platformLed;
    IDigitalOutputPort dayFeedingLed;
    IDigitalOutputPort nightFeedingLed;
    IDigitalInterruptPort feedButtonPort;

    CancellationTokenSource appLifetimeSource;
    WiFiManager wifiManager;
    DeviceTimeManager deviceTimeManager;
    WebServerManager webServerManager;
    PushNotificationManager pushNotificationManager;
    FeedingManager feedingManager;
    PowerStatusManager powerStatusManager;
    readonly object feedButtonGate = new object();
    DateTimeOffset lastFeedButtonPressedAt = DateTimeOffset.MinValue;
    volatile bool isSystemReady;

    public override async Task Initialize()
    {
        Logger.Info(Tag, "Initialize...");

        // Initialize hardware interfaces

        platformLed = new RgbPwmLed(
            redPwmPin: Device.Pins.OnboardLedRed,
            greenPwmPin: Device.Pins.OnboardLedGreen,
            bluePwmPin: Device.Pins.OnboardLedBlue,
            CommonType.CommonAnode);

        dayFeedingLed = Device.CreateDigitalOutputPort(Device.Pins.D01, false);
        nightFeedingLed = Device.CreateDigitalOutputPort(Device.Pins.D02, false);

        feedButtonPort = Device.CreateDigitalInterruptPort(
            Device.Pins.D03,
            InterruptMode.EdgeRising,
            ResistorMode.InternalPullDown,
            FeedButtonHardwareDebounce,
            FeedButtonHardwareGlitch);

        feedButtonPort.Changed += OnFeedButtonChanged;

        appLifetimeSource = new CancellationTokenSource();

        // Initialize managers

        wifiManager = new WiFiManager(
            Device.NetworkAdapters.Primary<IWiFiNetworkAdapter>()
        );

        deviceTimeManager = new DeviceTimeManager();
        deviceTimeManager.StartMonitoring();

        pushNotificationManager = new PushNotificationManager();
        feedingManager = new FeedingManager(pushNotificationManager);
        feedingManager.IndicatorStateChanged += OnIndicatorStateChanged;
        powerStatusManager = new PowerStatusManager(Device);

        var deviceName = string.IsNullOrWhiteSpace(Device.Information.DeviceName)
            ? "(unknown device name)"
            : Device.Information.DeviceName;

        webServerManager = new WebServerManager(
            wifiManager,
            deviceTimeManager,
            feedingManager,
            powerStatusManager,
            DateTimeOffset.UtcNow,
            deviceName);

        await wifiManager.InitializeAsync();

        await base.Initialize();
    }

    public override async Task Run()
    {
        Logger.Info(Tag, "Run...");

        var startupBlinkSource = new CancellationTokenSource();
        var startupBlinkTask = Task.Run(() => BlinkPlatformLedUntilReadyAsync(startupBlinkSource.Token));

        _ = Task.Run(webServerManager.StartAsync, appLifetimeSource.Token);
        _ = Task.Run(() => feedingManager.StartMonitoringAsync(appLifetimeSource.Token), appLifetimeSource.Token);

        await wifiManager.WaitForNetworkReadyAsync();
        await deviceTimeManager.WaitForValidTimeAsync();
        await webServerManager.WaitForStartedAsync();

        startupBlinkSource.Cancel();

        try
        {
            await startupBlinkTask;
        }
        catch (TaskCanceledException)
        {
            // Expected once startup readiness is achieved.
        }

        platformLed.SetColor(Color.Green);
        isSystemReady = true;
        Logger.Info(Tag, "System ready. Platform LED set to solid on.");
    }

    async Task BlinkPlatformLedUntilReadyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await platformLed.StartPulse(Color.Blue, TimeSpan.FromMilliseconds(500));
            await Task.Delay(PlatformBlinkInterval, cancellationToken);
        }
    }

    void OnFeedButtonChanged(object sender, DigitalPortResult result)
    {
        if (!isSystemReady)
        {
            return;
        }

        // Hardware debounce/glitch filtering handles most bounce; this is a small safety guard.
        var now = DateTimeOffset.UtcNow;
        lock (feedButtonGate)
        {
            if (now - lastFeedButtonPressedAt < FeedButtonDebounceInterval)
            {
                return;
            }

            lastFeedButtonPressedAt = now;
        }

        feedingManager.OnFeedButtonPressed();
    }

    void OnIndicatorStateChanged(FeedingIndicatorState state)
    {
        dayFeedingLed.State = state.DayLedOn;
        nightFeedingLed.State = state.NightLedOn;
    }
}

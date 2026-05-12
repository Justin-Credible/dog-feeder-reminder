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
    static readonly TimeSpan PlatformRecoveryBlinkInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan ConnectivityCheckInterval = TimeSpan.FromSeconds(2);
    static readonly TimeSpan FeedButtonHardwareDebounce = TimeSpan.FromMilliseconds(30);
    static readonly TimeSpan FeedButtonHardwareGlitch = TimeSpan.FromMilliseconds(5);
    static readonly TimeSpan FeedButtonDebounceInterval = TimeSpan.FromMilliseconds(120);
    static readonly TimeSpan FeedButtonLongPressDuration = TimeSpan.FromSeconds(3);
    static readonly TimeSpan VacationPatternBlinkOnDuration = TimeSpan.FromMilliseconds(120);
    static readonly TimeSpan VacationPatternBlinkOffDuration = TimeSpan.FromMilliseconds(140);
    static readonly TimeSpan VacationPatternPauseDuration = TimeSpan.FromSeconds(5);
    static readonly TimeSpan ConnectivityActivePollingInterval = TimeSpan.FromMilliseconds(100);

    RgbPwmLed platformLed;
    IDigitalOutputPort externalPlatformLed;
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
    DateTimeOffset currentFeedButtonPressStartedAt = DateTimeOffset.MinValue;
    CancellationTokenSource feedButtonLongPressSource;
    bool longPressHandledForCurrentPress;
    bool vacationPatternInitialized;
    int vacationPatternStep;
    DateTimeOffset vacationPatternStepStartedAt;
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
        externalPlatformLed = Device.CreateDigitalOutputPort(Device.Pins.D04, false);

        feedButtonPort = Device.CreateDigitalInterruptPort(
            Device.Pins.D03,
            InterruptMode.EdgeBoth,
            ResistorMode.InternalPullUp,
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

        var configuration = ConfigurationManager.LoadDogFeederConfiguration(Settings);
        pushNotificationManager = new PushNotificationManager(configuration.Pushover);
        feedingManager = new FeedingManager(
            pushNotificationManager,
            configuration.FeedingSchedule,
            nowProvider: () => DateTime.UtcNow + configuration.FeedingScheduleUtcOffset);
        feedingManager.IndicatorStateChanged += OnIndicatorStateChanged;
        OnIndicatorStateChanged(feedingManager.CurrentIndicatorState);
        powerStatusManager = new PowerStatusManager(Device);

        var deviceName = string.IsNullOrWhiteSpace(Device.Information.DeviceName)
            ? "(unknown device name)"
            : Device.Information.DeviceName;

        webServerManager = new WebServerManager(
            wifiManager,
            deviceTimeManager,
            feedingManager,
            powerStatusManager,
            pushNotificationManager,
            configuration.Pushover,
            configuration.PushoverTestEnabled,
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

        await platformLed.StopAnimation();
        platformLed.SetColor(Color.Green);
        externalPlatformLed.State = true;
        isSystemReady = true;
        Logger.Info(Tag, "System ready. Platform LED set to solid on.");

        _ = Task.Run(() => MaintainConnectivityAndPlatformStateAsync(appLifetimeSource.Token), appLifetimeSource.Token);
    }

    async Task BlinkPlatformLedUntilReadyAsync(CancellationToken cancellationToken)
    {
        var externalLedOn = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            await platformLed.StartPulse(Color.Blue, TimeSpan.FromMilliseconds(500));

            externalLedOn = !externalLedOn;
            externalPlatformLed.State = externalLedOn;

            await Task.Delay(PlatformBlinkInterval, cancellationToken);
        }

        externalPlatformLed.State = false;
    }

    void OnFeedButtonChanged(object sender, DigitalPortResult result)
    {
        var isPressed = !feedButtonPort.State;
        if (isPressed)
        {
            BeginFeedButtonPressTracking();
            return;
        }

        EndFeedButtonPressTracking();
    }

    void BeginFeedButtonPressTracking()
    {
        lock (feedButtonGate)
        {
            feedButtonLongPressSource?.Cancel();
            feedButtonLongPressSource?.Dispose();

            currentFeedButtonPressStartedAt = DateTimeOffset.UtcNow;
            longPressHandledForCurrentPress = false;
            feedButtonLongPressSource = new CancellationTokenSource();
            var token = feedButtonLongPressSource.Token;

            _ = Task.Run(() => TryHandleLongPressAsync(token), token);
        }
    }

    void EndFeedButtonPressTracking()
    {
        DateTimeOffset pressStartedAt;
        bool longPressHandled;

        lock (feedButtonGate)
        {
            feedButtonLongPressSource?.Cancel();
            feedButtonLongPressSource?.Dispose();
            feedButtonLongPressSource = null;

            pressStartedAt = currentFeedButtonPressStartedAt;
            longPressHandled = longPressHandledForCurrentPress;
        }

        if (longPressHandled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - pressStartedAt >= FeedButtonLongPressDuration)
        {
            return;
        }

        lock (feedButtonGate)
        {
            if (now - lastFeedButtonPressedAt < FeedButtonDebounceInterval)
            {
                return;
            }

            lastFeedButtonPressedAt = now;
        }

        if (!isSystemReady)
        {
            Logger.Info(Tag, "Feed button ignored because system is not ready yet.");
            return;
        }

        feedingManager.OnFeedButtonPressed();
    }

    async Task TryHandleLongPressAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(FeedButtonLongPressDuration, token);

            if (token.IsCancellationRequested || feedButtonPort.State)
            {
                return;
            }

            lock (feedButtonGate)
            {
                longPressHandledForCurrentPress = true;
            }

            feedingManager.ToggleVacationMode();
            Logger.Info(Tag, $"Feed button long press toggled vacation mode {(feedingManager.IsVacationModeEnabled ? "ON" : "OFF")}." );
        }
        catch (OperationCanceledException)
        {
            // Expected when the button is released before long-press threshold.
        }
    }

    void OnIndicatorStateChanged(FeedingIndicatorState state)
    {
        Logger.Info(Tag, $"Updating feeding indicator LEDs: Day {(state.DayLedOn ? "ON" : "off")}, Night {(state.NightLedOn ? "ON" : "off")}");

        dayFeedingLed.State = state.DayLedOn;
        nightFeedingLed.State = state.NightLedOn;
    }

    async Task MaintainConnectivityAndPlatformStateAsync(CancellationToken cancellationToken)
    {
        var blinkOn = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await wifiManager.EnsureConnectedAsync();

                var hasUsableNetwork = wifiManager.IsConnected && wifiManager.CurrentIpAddress != null;
                if (!hasUsableNetwork)
                {
                    if (isSystemReady)
                    {
                        isSystemReady = false;
                        ResetVacationPattern();
                        Logger.Warn(Tag, "Network connectivity lost. Returning platform LED to blinking state.");
                    }

                    blinkOn = !blinkOn;
                    platformLed.SetColor(blinkOn ? Color.Blue : Color.Black);
                    externalPlatformLed.State = blinkOn;
                    await Task.Delay(PlatformRecoveryBlinkInterval, cancellationToken);
                    continue;
                }

                if (!webServerManager.IsStarted)
                {
                    Logger.Warn(Tag, "Web server is not running while network is available. Attempting restart.");
                    _ = Task.Run(webServerManager.StartAsync, cancellationToken);
                }

                if (!isSystemReady)
                {
                    await platformLed.StopAnimation();
                    platformLed.SetColor(Color.Green);
                    externalPlatformLed.State = true;
                    ResetVacationPattern();
                    isSystemReady = true;
                    Logger.Info(Tag, "Network connectivity restored. Platform LED set back to solid ready state.");
                }

                if (feedingManager.IsVacationModeEnabled)
                {
                    RunVacationPatternStep(DateTimeOffset.UtcNow);
                    await Task.Delay(ConnectivityActivePollingInterval, cancellationToken);
                }
                else
                {
                    ResetVacationPattern();
                    externalPlatformLed.State = true;
                    await Task.Delay(ConnectivityCheckInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn(Tag, $"Connectivity monitor iteration failed: {ex.Message}");
                await Task.Delay(ConnectivityCheckInterval, cancellationToken);
            }
        }
    }

    void ResetVacationPattern()
    {
        vacationPatternInitialized = false;
        vacationPatternStep = 0;
        vacationPatternStepStartedAt = DateTimeOffset.MinValue;
    }

    void RunVacationPatternStep(DateTimeOffset now)
    {
        if (!vacationPatternInitialized)
        {
            vacationPatternInitialized = true;
            vacationPatternStep = 0;
            vacationPatternStepStartedAt = now;
            externalPlatformLed.State = true;
            return;
        }

        var elapsed = now - vacationPatternStepStartedAt;
        var stepDuration = GetVacationStepDuration(vacationPatternStep);
        if (elapsed < stepDuration)
        {
            return;
        }

        vacationPatternStep = (vacationPatternStep + 1) % 4;
        vacationPatternStepStartedAt = now;
        externalPlatformLed.State = vacationPatternStep == 0 || vacationPatternStep == 2;
    }

    static TimeSpan GetVacationStepDuration(int step)
    {
        switch (step)
        {
            case 0:
                return VacationPatternBlinkOnDuration;
            case 1:
                return VacationPatternBlinkOffDuration;
            case 2:
                return VacationPatternBlinkOnDuration;
            default:
                return VacationPatternPauseDuration;
        }
    }
}

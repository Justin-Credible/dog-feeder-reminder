using System;
using System.Threading;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public readonly struct FeedingIndicatorState : IEquatable<FeedingIndicatorState>
{
    public bool DayLedOn { get; }
    public bool NightLedOn { get; }

    public FeedingIndicatorState(bool dayLedOn, bool nightLedOn)
    {
        DayLedOn = dayLedOn;
        NightLedOn = nightLedOn;
    }

    public bool Equals(FeedingIndicatorState other)
    {
        return DayLedOn == other.DayLedOn && NightLedOn == other.NightLedOn;
    }

    public override bool Equals(object obj)
    {
        return obj is FeedingIndicatorState other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(DayLedOn, NightLedOn);
    }
}

public readonly struct FeedingStatusSnapshot
{
    public bool MorningFed { get; }
    public bool EveningFed { get; }
    public bool InMorningWindow { get; }
    public bool InEveningWindow { get; }
    public bool MorningWindowMissed { get; }
    public bool EveningWindowMissed { get; }
    public bool VacationModeEnabled { get; }

    public FeedingStatusSnapshot(
        bool morningFed,
        bool eveningFed,
        bool inMorningWindow,
        bool inEveningWindow,
        bool morningWindowMissed,
        bool eveningWindowMissed,
        bool vacationModeEnabled)
    {
        MorningFed = morningFed;
        EveningFed = eveningFed;
        InMorningWindow = inMorningWindow;
        InEveningWindow = inEveningWindow;
        MorningWindowMissed = morningWindowMissed;
        EveningWindowMissed = eveningWindowMissed;
        VacationModeEnabled = vacationModeEnabled;
    }
}

public readonly struct FeedingScheduleConfiguration
{
    public TimeSpan MorningWindowStart { get; }
    public TimeSpan MorningWindowEnd { get; }
    public TimeSpan EveningWindowStart { get; }
    public TimeSpan EveningWindowEnd { get; }
    public TimeSpan DailyResetTime { get; }

    public FeedingScheduleConfiguration(
        TimeSpan morningWindowStart,
        TimeSpan morningWindowEnd,
        TimeSpan eveningWindowStart,
        TimeSpan eveningWindowEnd,
        TimeSpan dailyResetTime = default)
    {
        MorningWindowStart = morningWindowStart;
        MorningWindowEnd = morningWindowEnd;
        EveningWindowStart = eveningWindowStart;
        EveningWindowEnd = eveningWindowEnd;
        DailyResetTime = dailyResetTime == default ? TimeSpan.FromHours(4) : dailyResetTime;
    }

    public static FeedingScheduleConfiguration Default => new FeedingScheduleConfiguration(
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(11),
        TimeSpan.FromHours(18),
        TimeSpan.FromHours(24),
        TimeSpan.FromHours(4));
}

public class FeedingManager
{
    const string Tag = "FeedingManager";

    readonly object gate = new object();
    readonly PushNotificationManager pushNotificationManager;
    readonly Func<DateTime> nowProvider;
    readonly TimeSpan dailyResetTime;
    readonly TimeSpan morningWindowStart;
    readonly TimeSpan morningWindowEnd;
    readonly TimeSpan eveningWindowStart;
    readonly TimeSpan eveningWindowEnd;

    bool morningFed;
    bool eveningFed;
    bool morningMissedNotificationSent;
    bool eveningMissedNotificationSent;
    bool vacationModeEnabled;
    DateTime lastResetDate;

    public event Action<FeedingIndicatorState> IndicatorStateChanged;

    public FeedingIndicatorState CurrentIndicatorState { get; private set; }

    public string FeedingStateText
    {
        get
        {
            lock (gate)
            {
                if (morningFed && eveningFed)
                {
                    return "Morning and evening fed";
                }

                if (morningFed)
                {
                    return "Morning fed";
                }

                if (eveningFed)
                {
                    return "Evening fed";
                }

                return "Not fed";
            }
        }
    }

    public string IndicatorStateText =>
        $"Day {(CurrentIndicatorState.DayLedOn ? "On" : "Off")}, Night {(CurrentIndicatorState.NightLedOn ? "On" : "Off")}";

    public bool IsVacationModeEnabled
    {
        get
        {
            lock (gate)
            {
                return vacationModeEnabled;
            }
        }
    }

    public string MorningWindowLabel => FormatWindowLabel(morningWindowStart, morningWindowEnd);

    public string EveningWindowLabel => FormatWindowLabel(eveningWindowStart, eveningWindowEnd);

    public FeedingManager(
        PushNotificationManager pushNotificationManager,
        FeedingScheduleConfiguration? scheduleConfiguration = null,
        Func<DateTime> nowProvider = null)
    {
        this.pushNotificationManager = pushNotificationManager;
        this.nowProvider = nowProvider ?? (() => DateTime.Now);

        var schedule = scheduleConfiguration ?? FeedingScheduleConfiguration.Default;
        morningWindowStart = schedule.MorningWindowStart;
        morningWindowEnd = schedule.MorningWindowEnd;
        eveningWindowStart = schedule.EveningWindowStart;
        eveningWindowEnd = schedule.EveningWindowEnd;
        dailyResetTime = schedule.DailyResetTime;

        InitializeResetState(this.nowProvider());
        CurrentIndicatorState = ComputeIndicatorState(this.nowProvider());
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var now = nowProvider();

                ApplyDailyResetIfNeeded(now);
                PublishIndicatorStateIfChanged(now);
                await SendMissedFeedingNotificationsIfNeededAsync(now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error(Tag, $"Monitoring loop iteration failed: {ex.Message}");
            }

            await Task.Delay(1000, cancellationToken);
        }
    }

    public void OnFeedButtonPressed()
    {
        lock (gate)
        {
            if (!morningFed && !eveningFed)
            {
                morningFed = true;
            }
            else if (morningFed && !eveningFed)
            {
                eveningFed = true;
            }
            else
            {
                morningFed = false;
                eveningFed = false;
            }
        }

        Logger.Info(Tag, "Feed button pressed; feeding state advanced.");
        PublishIndicatorStateIfChanged(nowProvider(), forcePublish: true);
    }

    public void MarkFeeding(FeedingWindow window)
    {
        var now = nowProvider();
        ApplyDailyResetIfNeeded(now);

        lock (gate)
        {
            if (window == FeedingWindow.Morning)
            {
                morningFed = true;
            }
            else
            {
                eveningFed = true;
            }
        }

        Logger.Info(Tag, $"{window} feeding marked from external request.");
        PublishIndicatorStateIfChanged(now, forcePublish: true);
    }

    public void ResetFeedings()
    {
        lock (gate)
        {
            morningFed = false;
            eveningFed = false;
            morningMissedNotificationSent = false;
            eveningMissedNotificationSent = false;
        }

        Logger.Info(Tag, "Feeding state manually reset.");
        PublishIndicatorStateIfChanged(nowProvider(), forcePublish: true);
    }

    public void ToggleVacationMode()
    {
        var now = nowProvider();
        lock (gate)
        {
            vacationModeEnabled = !vacationModeEnabled;
            if (vacationModeEnabled)
            {
                // Prevent immediate catch-up pushes when entering vacation mode.
                morningMissedNotificationSent = true;
                eveningMissedNotificationSent = true;
            }
        }

        Logger.Info(Tag, $"Vacation mode {(IsVacationModeEnabled ? "enabled" : "disabled")}." );
        PublishIndicatorStateIfChanged(now, forcePublish: true);
    }

    public FeedingStatusSnapshot GetStatusSnapshot()
    {
        var now = nowProvider();
        ApplyDailyResetIfNeeded(now);

        bool localMorningFed;
        bool localEveningFed;
        bool localVacationModeEnabled;

        lock (gate)
        {
            localMorningFed = morningFed;
            localEveningFed = eveningFed;
            localVacationModeEnabled = vacationModeEnabled;
        }

        var inMorningWindow = IsWithinWindow(now.TimeOfDay, morningWindowStart, morningWindowEnd);
        var inEveningWindow = IsWithinWindow(now.TimeOfDay, eveningWindowStart, eveningWindowEnd);
        var morningWindowMissed = !localMorningFed && now.TimeOfDay >= morningWindowEnd;
        var eveningWindowMissed = !localEveningFed && now.TimeOfDay >= eveningWindowEnd;

        return new FeedingStatusSnapshot(
            localMorningFed,
            localEveningFed,
            inMorningWindow,
            inEveningWindow,
            morningWindowMissed,
            eveningWindowMissed,
            localVacationModeEnabled);
    }

    void InitializeResetState(DateTime now)
    {
        lock (gate)
        {
            lastResetDate = now.TimeOfDay >= dailyResetTime ? now.Date : now.Date.AddDays(-1);
            morningFed = false;
            eveningFed = false;
            vacationModeEnabled = false;

            // Avoid backfilling missed-window pushes immediately on cold boot.
            // If the app starts after a window already ended, consider that window's
            // missed-notification already accounted for until the next daily reset.
            morningMissedNotificationSent = now.TimeOfDay >= morningWindowEnd;
            eveningMissedNotificationSent = now.TimeOfDay >= eveningWindowEnd;
        }
    }

    void ApplyDailyResetIfNeeded(DateTime now)
    {
        lock (gate)
        {
            if (now.TimeOfDay < dailyResetTime)
            {
                return;
            }

            if (lastResetDate >= now.Date)
            {
                return;
            }

            morningFed = false;
            eveningFed = false;
            morningMissedNotificationSent = false;
            eveningMissedNotificationSent = false;
            lastResetDate = now.Date;
            Logger.Info(Tag, $"Daily feeding state reset at {FormatTimeLabel(dailyResetTime)} window.");
        }
    }

    async Task SendMissedFeedingNotificationsIfNeededAsync(DateTime now)
    {
        bool sendMorning = false;
        bool sendEvening = false;
        bool localVacationModeEnabled;

        lock (gate)
        {
            localVacationModeEnabled = vacationModeEnabled;

            if (!morningFed && !morningMissedNotificationSent && now.TimeOfDay >= morningWindowEnd)
            {
                morningMissedNotificationSent = true;
                sendMorning = !localVacationModeEnabled;
            }

            if (!eveningFed && !eveningMissedNotificationSent && now.TimeOfDay >= eveningWindowEnd)
            {
                eveningMissedNotificationSent = true;
                sendEvening = !localVacationModeEnabled;
            }
        }

        if (sendMorning)
        {
            await pushNotificationManager.SendMissedFeedingNotificationAsync(FeedingWindow.Morning, now);
        }

        if (sendEvening)
        {
            await pushNotificationManager.SendMissedFeedingNotificationAsync(FeedingWindow.Evening, now);
        }
    }

    void PublishIndicatorStateIfChanged(DateTime now, bool forcePublish = false)
    {
        var nextState = ComputeIndicatorState(now);
        if (!forcePublish && nextState.Equals(CurrentIndicatorState))
        {
            return;
        }

        CurrentIndicatorState = nextState;
        IndicatorStateChanged?.Invoke(nextState);
    }

    FeedingIndicatorState ComputeIndicatorState(DateTime now)
    {
        bool localMorningFed;
        bool localEveningFed;
        bool localVacationModeEnabled;

        lock (gate)
        {
            localMorningFed = morningFed;
            localEveningFed = eveningFed;
            localVacationModeEnabled = vacationModeEnabled;
        }

        if (localVacationModeEnabled)
        {
            return new FeedingIndicatorState(dayLedOn: false, nightLedOn: false);
        }

        var blinkOn = now.Second % 2 == 0;
        var inMorningWindow = IsWithinWindow(now.TimeOfDay, morningWindowStart, morningWindowEnd);
        var inEveningWindow = IsWithinWindow(now.TimeOfDay, eveningWindowStart, eveningWindowEnd);

        var dayLedOn = localMorningFed || (!localMorningFed && inMorningWindow && blinkOn);
        var nightLedOn = localEveningFed || (!localEveningFed && inEveningWindow && blinkOn);

        return new FeedingIndicatorState(dayLedOn, nightLedOn);
    }

    static bool IsWithinWindow(TimeSpan current, TimeSpan start, TimeSpan end)
    {
        return current >= start && current < end;
    }

    static string FormatWindowLabel(TimeSpan start, TimeSpan end)
    {
        return $"{FormatTimeLabel(start)}-{FormatTimeLabel(end)}";
    }

    static string FormatTimeLabel(TimeSpan value)
    {
        if (value == TimeSpan.FromHours(24))
        {
            return "12am";
        }

        var totalHours = ((int)value.TotalHours) % 24;
        var minutes = value.Minutes;
        var meridiem = totalHours < 12 ? "am" : "pm";
        var hour12 = totalHours % 12;
        if (hour12 == 0)
        {
            hour12 = 12;
        }

        if (minutes == 0)
        {
            return $"{hour12}{meridiem}";
        }

        return $"{hour12}:{minutes:00}{meridiem}";
    }
}
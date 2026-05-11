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

    public FeedingStatusSnapshot(
        bool morningFed,
        bool eveningFed,
        bool inMorningWindow,
        bool inEveningWindow,
        bool morningWindowMissed,
        bool eveningWindowMissed)
    {
        MorningFed = morningFed;
        EveningFed = eveningFed;
        InMorningWindow = inMorningWindow;
        InEveningWindow = inEveningWindow;
        MorningWindowMissed = morningWindowMissed;
        EveningWindowMissed = eveningWindowMissed;
    }
}

public class FeedingManager
{
    const string Tag = "FeedingManager";

    static readonly TimeSpan MorningWindowStart = TimeSpan.FromHours(7);
    static readonly TimeSpan MorningWindowEnd = TimeSpan.FromHours(9);
    static readonly TimeSpan EveningWindowStart = TimeSpan.FromHours(18);
    static readonly TimeSpan EveningWindowEnd = TimeSpan.FromHours(21);
    static readonly TimeSpan DailyResetTime = TimeSpan.FromHours(4);

    readonly object gate = new object();
    readonly PushNotificationManager pushNotificationManager;
    readonly Func<DateTime> nowProvider;

    bool morningFed;
    bool eveningFed;
    bool morningMissedNotificationSent;
    bool eveningMissedNotificationSent;
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

    public FeedingManager(PushNotificationManager pushNotificationManager, Func<DateTime> nowProvider = null)
    {
        this.pushNotificationManager = pushNotificationManager;
        this.nowProvider = nowProvider ?? (() => DateTime.Now);

        InitializeResetState(this.nowProvider());
        CurrentIndicatorState = ComputeIndicatorState(this.nowProvider());
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = nowProvider();

            ApplyDailyResetIfNeeded(now);
            await SendMissedFeedingNotificationsIfNeededAsync(now);
            PublishIndicatorStateIfChanged(now);

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

    public FeedingStatusSnapshot GetStatusSnapshot()
    {
        var now = nowProvider();
        ApplyDailyResetIfNeeded(now);

        bool localMorningFed;
        bool localEveningFed;

        lock (gate)
        {
            localMorningFed = morningFed;
            localEveningFed = eveningFed;
        }

        var inMorningWindow = IsWithinWindow(now.TimeOfDay, MorningWindowStart, MorningWindowEnd);
        var inEveningWindow = IsWithinWindow(now.TimeOfDay, EveningWindowStart, EveningWindowEnd);
        var morningWindowMissed = !localMorningFed && now.TimeOfDay >= MorningWindowEnd;
        var eveningWindowMissed = !localEveningFed && now.TimeOfDay >= EveningWindowEnd;

        return new FeedingStatusSnapshot(
            localMorningFed,
            localEveningFed,
            inMorningWindow,
            inEveningWindow,
            morningWindowMissed,
            eveningWindowMissed);
    }

    void InitializeResetState(DateTime now)
    {
        lock (gate)
        {
            lastResetDate = now.TimeOfDay >= DailyResetTime ? now.Date : now.Date.AddDays(-1);
            morningFed = false;
            eveningFed = false;
            morningMissedNotificationSent = false;
            eveningMissedNotificationSent = false;
        }
    }

    void ApplyDailyResetIfNeeded(DateTime now)
    {
        lock (gate)
        {
            if (now.TimeOfDay < DailyResetTime)
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
            Logger.Info(Tag, "Daily feeding state reset at 4am window.");
        }
    }

    async Task SendMissedFeedingNotificationsIfNeededAsync(DateTime now)
    {
        bool sendMorning = false;
        bool sendEvening = false;

        lock (gate)
        {
            if (!morningFed && !morningMissedNotificationSent && now.TimeOfDay >= MorningWindowEnd)
            {
                morningMissedNotificationSent = true;
                sendMorning = true;
            }

            if (!eveningFed && !eveningMissedNotificationSent && now.TimeOfDay >= EveningWindowEnd)
            {
                eveningMissedNotificationSent = true;
                sendEvening = true;
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

        lock (gate)
        {
            localMorningFed = morningFed;
            localEveningFed = eveningFed;
        }

        var blinkOn = now.Second % 2 == 0;
        var inMorningWindow = IsWithinWindow(now.TimeOfDay, MorningWindowStart, MorningWindowEnd);
        var inEveningWindow = IsWithinWindow(now.TimeOfDay, EveningWindowStart, EveningWindowEnd);

        var dayLedOn = localMorningFed || (!localMorningFed && inMorningWindow && blinkOn);
        var nightLedOn = localEveningFed || (!localEveningFed && inEveningWindow && blinkOn);

        return new FeedingIndicatorState(dayLedOn, nightLedOn);
    }

    static bool IsWithinWindow(TimeSpan current, TimeSpan start, TimeSpan end)
    {
        return current >= start && current < end;
    }
}
using System;
using System.Collections.Generic;
using System.Globalization;

namespace dog_feeder_reminder;

public readonly struct DogFeederConfiguration
{
    public TimeSpan FeedingScheduleUtcOffset { get; }
    public FeedingScheduleConfiguration FeedingSchedule { get; }
    public PushoverConfiguration Pushover { get; }
    public bool PushoverTestEnabled { get; }

    public DogFeederConfiguration(
        TimeSpan feedingScheduleUtcOffset,
        FeedingScheduleConfiguration feedingSchedule,
        PushoverConfiguration pushover,
        bool pushoverTestEnabled)
    {
        FeedingScheduleUtcOffset = feedingScheduleUtcOffset;
        FeedingSchedule = feedingSchedule;
        Pushover = pushover;
        PushoverTestEnabled = pushoverTestEnabled;
    }
}

public readonly struct PushoverConfiguration
{
    public string ApiToken { get; }
    public string UserKey { get; }
    public string AppName { get; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiToken) &&
        !string.IsNullOrWhiteSpace(UserKey);

    public PushoverConfiguration(string apiToken, string userKey, string appName)
    {
        ApiToken = apiToken ?? string.Empty;
        UserKey = userKey ?? string.Empty;
        AppName = string.IsNullOrWhiteSpace(appName) ? "Dog Feeder Reminder" : appName;
    }
}

public static class ConfigurationManager
{
    const string Tag = "ConfigurationManager";

    const string DogFeederSectionDot = "DogFeeder.";
    const string DogFeederSectionColon = "DogFeeder:";

    const string OffsetHoursKey = "FeedingScheduleUtcOffsetHours";
    const string MorningStartHoursKey = "MorningWindowStartHour";
    const string MorningEndHoursKey = "MorningWindowEndHour";
    const string EveningStartHoursKey = "EveningWindowStartHour";
    const string EveningEndHoursKey = "EveningWindowEndHour";
    const string DailyResetTimeHoursKey = "DailyResetTimeHour";
    const string PushoverApiTokenKey = "PushoverApiToken";
    const string PushoverUserKey = "PushoverUserKey";
    const string PushoverAppNameKey = "PushoverAppName";
    const string PushoverTestKey = "PushoverTest";

    static readonly TimeSpan DefaultFeedingScheduleUtcOffset = TimeSpan.FromHours(-7);

    public static DogFeederConfiguration LoadDogFeederConfiguration(IDictionary<string, string> settings)
    {
        var fallbackSchedule = FeedingScheduleConfiguration.Default;

        var offsetHours = GetDouble(settings, OffsetHoursKey, DefaultFeedingScheduleUtcOffset.TotalHours);
        var morningStartHours = GetDouble(settings, MorningStartHoursKey, fallbackSchedule.MorningWindowStart.TotalHours);
        var morningEndHours = GetDouble(settings, MorningEndHoursKey, fallbackSchedule.MorningWindowEnd.TotalHours);
        var eveningStartHours = GetDouble(settings, EveningStartHoursKey, fallbackSchedule.EveningWindowStart.TotalHours);
        var eveningEndHours = GetDouble(settings, EveningEndHoursKey, fallbackSchedule.EveningWindowEnd.TotalHours);
        var dailyResetHours = GetDouble(settings, DailyResetTimeHoursKey, fallbackSchedule.DailyResetTime.TotalHours);
        var pushoverApiToken = GetString(settings, PushoverApiTokenKey, string.Empty);
        var pushoverUserKey = GetString(settings, PushoverUserKey, string.Empty);
        var pushoverAppName = GetString(settings, PushoverAppNameKey, "Dog Feeder Reminder");
        var pushoverTestEnabled = GetBool(settings, PushoverTestKey, false);

        var configuration = new DogFeederConfiguration(
            TimeSpan.FromHours(offsetHours),
            new FeedingScheduleConfiguration(
                TimeSpan.FromHours(morningStartHours),
                TimeSpan.FromHours(morningEndHours),
                TimeSpan.FromHours(eveningStartHours),
                TimeSpan.FromHours(eveningEndHours),
                TimeSpan.FromHours(dailyResetHours)),
            new PushoverConfiguration(pushoverApiToken, pushoverUserKey, pushoverAppName),
            pushoverTestEnabled);

        Logger.Info(Tag,
            $"Loaded config: utcOffset={offsetHours:0.##}, " +
            $"morning={morningStartHours:0.##}-{morningEndHours:0.##}, " +
            $"evening={eveningStartHours:0.##}-{eveningEndHours:0.##}, " +
            $"dailyReset={dailyResetHours:0.##}, " +
            $"pushoverConfigured={configuration.Pushover.IsConfigured}, " +
            $"pushoverTestEnabled={configuration.PushoverTestEnabled}");

        return configuration;
    }

    static double GetDouble(IDictionary<string, string> settings, string key, double fallback)
    {
        if (TryGetDouble(settings, DogFeederSectionDot + key, out var scopedDotValue))
        {
            return scopedDotValue;
        }

        if (TryGetDouble(settings, DogFeederSectionColon + key, out var scopedColonValue))
        {
            return scopedColonValue;
        }

        if (TryGetDouble(settings, key, out var flatValue))
        {
            return flatValue;
        }

        return fallback;
    }

    static bool TryGetDouble(IDictionary<string, string> settings, string key, out double value)
    {
        value = default;

        if (settings == null)
        {
            return false;
        }

        if (settings.TryGetValue(key, out var raw) && TryParseDouble(raw, out value))
        {
            return true;
        }

        foreach (var entry in settings)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)
                && TryParseDouble(entry.Value, out value))
            {
                return true;
            }
        }

        return false;
    }

    static string GetString(IDictionary<string, string> settings, string key, string fallback)
    {
        if (TryGetString(settings, DogFeederSectionDot + key, out var scopedDotValue))
        {
            return scopedDotValue;
        }

        if (TryGetString(settings, DogFeederSectionColon + key, out var scopedColonValue))
        {
            return scopedColonValue;
        }

        if (TryGetString(settings, key, out var flatValue))
        {
            return flatValue;
        }

        return fallback;
    }

    static bool TryGetString(IDictionary<string, string> settings, string key, out string value)
    {
        value = null;

        if (settings == null)
        {
            return false;
        }

        if (settings.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw.Trim();
            return true;
        }

        foreach (var entry in settings)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                value = entry.Value.Trim();
                return true;
            }
        }

        return false;
    }

    static bool GetBool(IDictionary<string, string> settings, string key, bool fallback)
    {
        if (TryGetBool(settings, DogFeederSectionDot + key, out var scopedDotValue))
        {
            return scopedDotValue;
        }

        if (TryGetBool(settings, DogFeederSectionColon + key, out var scopedColonValue))
        {
            return scopedColonValue;
        }

        if (TryGetBool(settings, key, out var flatValue))
        {
            return flatValue;
        }

        return fallback;
    }

    static bool TryGetBool(IDictionary<string, string> settings, string key, out bool value)
    {
        value = default;

        if (settings == null)
        {
            return false;
        }

        if (settings.TryGetValue(key, out var raw) && bool.TryParse(raw, out value))
        {
            return true;
        }

        foreach (var entry in settings)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)
                && bool.TryParse(entry.Value, out value))
            {
                return true;
            }
        }

        return false;
    }

    static bool TryParseDouble(string input, out double value)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }
}

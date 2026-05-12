using System;
using System.Collections.Generic;
using System.Globalization;

namespace dog_feeder_reminder;

public readonly struct DogFeederConfiguration
{
    public TimeSpan FeedingScheduleUtcOffset { get; }
    public FeedingScheduleConfiguration FeedingSchedule { get; }

    public DogFeederConfiguration(TimeSpan feedingScheduleUtcOffset, FeedingScheduleConfiguration feedingSchedule)
    {
        FeedingScheduleUtcOffset = feedingScheduleUtcOffset;
        FeedingSchedule = feedingSchedule;
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

    static readonly TimeSpan DefaultFeedingScheduleUtcOffset = TimeSpan.FromHours(-7);

    public static DogFeederConfiguration LoadDogFeederConfiguration(IDictionary<string, string> settings)
    {
        var fallbackSchedule = FeedingScheduleConfiguration.Default;

        var offsetHours = GetDouble(settings, OffsetHoursKey, DefaultFeedingScheduleUtcOffset.TotalHours);
        var morningStartHours = GetDouble(settings, MorningStartHoursKey, fallbackSchedule.MorningWindowStart.TotalHours);
        var morningEndHours = GetDouble(settings, MorningEndHoursKey, fallbackSchedule.MorningWindowEnd.TotalHours);
        var eveningStartHours = GetDouble(settings, EveningStartHoursKey, fallbackSchedule.EveningWindowStart.TotalHours);
        var eveningEndHours = GetDouble(settings, EveningEndHoursKey, fallbackSchedule.EveningWindowEnd.TotalHours);

        var configuration = new DogFeederConfiguration(
            TimeSpan.FromHours(offsetHours),
            new FeedingScheduleConfiguration(
                TimeSpan.FromHours(morningStartHours),
                TimeSpan.FromHours(morningEndHours),
                TimeSpan.FromHours(eveningStartHours),
                TimeSpan.FromHours(eveningEndHours)));

        Logger.Info(Tag,
            $"Loaded config: utcOffset={offsetHours:0.##}, " +
            $"morning={morningStartHours:0.##}-{morningEndHours:0.##}, " +
            $"evening={eveningStartHours:0.##}-{eveningEndHours:0.##}");

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

    static bool TryParseDouble(string input, out double value)
    {
        return double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }
}

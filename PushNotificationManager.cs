using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public enum FeedingWindow
{
    Morning,
    Evening,
}

public class PushNotificationManager
{
    const string Tag = "PushNotificationManager";
    const string PushoverEndpoint = "https://api.pushover.net/1/messages.json";

    readonly PushoverConfiguration pushoverConfiguration;
    readonly HttpClient httpClient;
    readonly TimeSpan utcOffset;

    public PushNotificationManager(PushoverConfiguration pushoverConfiguration, HttpClient httpClient = null, TimeSpan utcOffset = default)
    {
        this.pushoverConfiguration = pushoverConfiguration;
        this.httpClient = httpClient ?? new HttpClient();
        this.httpClient.Timeout = TimeSpan.FromSeconds(15);
        this.utcOffset = utcOffset;
    }

    public Task SendMissedFeedingNotificationAsync(FeedingWindow window, DateTime triggeredAt)
    {
        if (!pushoverConfiguration.IsConfigured)
        {
            Logger.Warn(Tag, $"Pushover is not configured; skipping missed {window} feeding notification.");
            return Task.CompletedTask;
        }

        var localTime = triggeredAt + utcOffset;
        return SendAsync(
            title: $"{pushoverConfiguration.AppName} reminder",
            message: $"Missed {window.ToString().ToLowerInvariant()} feeding window at {localTime:yyyy-MM-dd HH:mm:ss}.",
            successLogMessage: $"Pushover notification sent for missed {window} feeding.");
    }

    public async Task<bool> SendTestNotificationAsync(DateTime triggeredAt)
    {
        if (!pushoverConfiguration.IsConfigured)
        {
            Logger.Warn(Tag, "Pushover is not configured; skipping test notification.");
            return false;
        }

        var localTime = triggeredAt + utcOffset;
        return await SendAsync(
            title: $"{pushoverConfiguration.AppName} test",
            message: $"This is a test push notification sent at {localTime:yyyy-MM-dd HH:mm:ss}.",
            successLogMessage: "Pushover test notification sent.");
    }

    async Task<bool> SendAsync(string title, string message, string successLogMessage)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("token", pushoverConfiguration.ApiToken),
            new KeyValuePair<string, string>("user", pushoverConfiguration.UserKey),
            new KeyValuePair<string, string>("title", title),
            new KeyValuePair<string, string>("message", message),
            new KeyValuePair<string, string>("priority", "0"),
        };

        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await httpClient.PostAsync(PushoverEndpoint, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                Logger.Info(Tag, successLogMessage);
                return true;
            }

            Logger.Warn(Tag, $"Pushover request failed with {(int)response.StatusCode}: {TrimResponse(responseText)}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn(Tag, $"Pushover send failed: {ex.Message}");
            return false;
        }
    }

    static string TrimResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return string.Empty;
        }

        var trimmed = responseText.Trim();
        if (trimmed.Length <= 120)
        {
            return trimmed;
        }

        return trimmed.Substring(0, 120);
    }
}
using System;
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

    public Task SendMissedFeedingNotificationAsync(FeedingWindow window, DateTime triggeredAt)
    {
        // Stub implementation: replace with provider integration (e.g., APNS/FCM/webhook) later.
        Logger.Warn(Tag, $"Push notification stub invoked for missed {window} feeding at {triggeredAt:yyyy-MM-dd HH:mm:ss}");
        return Task.CompletedTask;
    }
}
using Meadow;

namespace dog_feeder_reminder;

public static class Logger
{
    public static void Info(string tag, string message) =>
        Resolver.Log.Info($"[{tag}] {message}");

    public static void Warn(string tag, string message) =>
        Resolver.Log.Warn($"[{tag}] {message}");

    public static void Error(string tag, string message) =>
        Resolver.Log.Error($"[{tag}] {message}");
}

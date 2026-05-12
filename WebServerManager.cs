using Meadow;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public class WebServerManager
{
    const string Tag = "WebServerManager";
    const string FlashNoticeCookieName = "dogfeeder_flash";
    readonly WiFiManager wifiManager;
    readonly DeviceTimeManager deviceTimeManager;
    readonly FeedingManager feedingManager;
    readonly PowerStatusManager powerStatusManager;
    readonly HttpResponseManager responseManager;
    readonly PushNotificationManager pushNotificationManager;
    readonly PushoverConfiguration pushoverConfiguration;
    readonly bool pushoverTestEnabled;
    readonly DateTimeOffset startedAt;
    readonly string deviceName;

    HttpListener webServer;
    bool webServerStarted;
    readonly TaskCompletionSource<bool> webServerReadySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object tokenGate = new object();
    readonly Dictionary<string, DateTimeOffset> activeFormTokens = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
    readonly Dictionary<string, FlashNoticeEntry> activeFlashNotices = new Dictionary<string, FlashNoticeEntry>(StringComparer.Ordinal);
    static readonly TimeSpan FormTokenLifetime = TimeSpan.FromMinutes(5);
    static readonly TimeSpan FlashNoticeLifetime = TimeSpan.FromMinutes(2);

    readonly struct FlashNoticeEntry
    {
        public FlashNoticeEntry(string notice, string level, DateTimeOffset issuedAt)
        {
            Notice = notice;
            Level = level;
            IssuedAt = issuedAt;
        }

        public string Notice { get; }
        public string Level { get; }
        public DateTimeOffset IssuedAt { get; }
    }

    public bool IsStarted => webServerStarted;

    public WebServerManager(
        WiFiManager wifiManager,
        DeviceTimeManager deviceTimeManager,
        FeedingManager feedingManager,
        PowerStatusManager powerStatusManager,
        PushNotificationManager pushNotificationManager,
        PushoverConfiguration pushoverConfiguration,
        bool pushoverTestEnabled,
        DateTimeOffset startedAt,
        string deviceName)
    {
        this.wifiManager = wifiManager;
        this.deviceTimeManager = deviceTimeManager;
        this.feedingManager = feedingManager;
        this.powerStatusManager = powerStatusManager;
        this.pushNotificationManager = pushNotificationManager;
        this.pushoverConfiguration = pushoverConfiguration;
        this.pushoverTestEnabled = pushoverTestEnabled;
        this.responseManager = new HttpResponseManager();
        this.startedAt = startedAt;
        this.deviceName = deviceName;
    }

    public Task WaitForStartedAsync()
    {
        if (webServerStarted)
        {
            return Task.CompletedTask;
        }

        return webServerReadySource.Task;
    }

    public async Task StartAsync()
    {
        if (webServerStarted)
        {
            return;
        }

        var ipAddress = await wifiManager.WaitForNetworkReadyAsync();
        if (ipAddress == null)
        {
            Logger.Error(Tag, "WiFi reported ready but no IP address was available.");
            webServerReadySource.TrySetException(new InvalidOperationException("No IP address available for web server startup."));
            return;
        }

        webServer = new HttpListener();
        webServer.Prefixes.Add($"http://{ipAddress}:8081/");
        webServer.Start();
        webServerStarted = true;
        webServerReadySource.TrySetResult(true);

        Logger.Info(Tag, $"Web server listening at http://{ipAddress}:8081/");

        try
        {
            while (webServerStarted)
            {
                HttpListenerContext context;
                try
                {
                    context = await webServer.GetContextAsync();
                }
                catch (HttpListenerException ex)
                {
                    Logger.Warn(Tag, $"Web server listener error: {ex.Message}");
                    continue;
                }

                try
                {
                    await ProcessRequestAsync(context);
                }
                catch (Exception ex)
                {
                    Logger.Warn(Tag, $"Request handling failed: {ex.Message}");
                    SafeCloseResponse(context?.Response);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(Tag, $"Web server stopped: {ex.Message}");
            webServerReadySource.TrySetException(ex);
        }
        finally
        {
            webServerStarted = false;
            webServer?.Close();
        }
    }

    static void SafeCloseResponse(HttpListenerResponse response)
    {
        if (response == null)
        {
            return;
        }

        try
        {
            response.Close();
        }
        catch
        {
        }
    }

    async Task ProcessRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var absolutePath = request.Url.AbsolutePath;

        Logger.Info(Tag, $"HTTP {request.HttpMethod} {request.Url}");

        if (absolutePath.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return;
        }

        if (absolutePath.Equals("/feedings/mark", StringComparison.OrdinalIgnoreCase))
        {
            await HandleFeedingMarkRequestAsync(request, response);
            return;
        }

        if (absolutePath.Equals("/vacation/toggle", StringComparison.OrdinalIgnoreCase))
        {
            await HandleVacationToggleRequestAsync(request, response);
            return;
        }

        if (absolutePath.Equals("/api/pushover/test", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePushoverTestRequestAsync(request, response);
            return;
        }

        string body;
        string contentType;
        response.StatusCode = (int)HttpStatusCode.OK;

        if (absolutePath.Equals("/api/diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            contentType = "application/json";
            body = BuildDiagnosticsJsonResponse();
        }
        else if (absolutePath.Equals("/api/feedings", StringComparison.OrdinalIgnoreCase))
        {
            contentType = "application/json";
            body = BuildFeedingsJsonResponse();
        }
        else if (absolutePath.Equals("/", StringComparison.OrdinalIgnoreCase))
        {
            contentType = "text/html";
            var flashNotice = TryConsumeFlashNotice(request, response);
            var notice = flashNotice?.Notice;
            var level = flashNotice?.Level;
            body = BuildMainPageResponse(notice, level);
        }
        else
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            contentType = "text/plain";
            body = "Not Found";
        }

        byte[] payload = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = payload.LongLength;

        await response.OutputStream.WriteAsync(payload, 0, payload.Length);
        response.Close();
    }

    async Task HandleFeedingMarkRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.ContentType = "text/plain";
            byte[] methodError = Encoding.UTF8.GetBytes("Method Not Allowed");
            response.ContentLength64 = methodError.LongLength;
            await response.OutputStream.WriteAsync(methodError, 0, methodError.Length);
            response.Close();
            return;
        }

        var formValues = await ReadFormDataAsync(request);
        var formToken = formValues.TryGetValue("formToken", out var formTokenValue) ? formTokenValue : string.Empty;

        if (!TryConsumeFormToken(formToken))
        {
            RedirectToNotice(response, "Duplicate or invalid form submission. Please refresh and try again.", "error");
            response.Close();
            return;
        }

        var action = formValues.TryGetValue("action", out var actionValue) ? actionValue : "mark";
        var slot = formValues.TryGetValue("slot", out var slotValue) ? slotValue : string.Empty;
        string notice;
        string level = "success";

        if (action.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            feedingManager.ResetFeedings();
            notice = $"Feedings reset at {DateTime.Now:HH:mm:ss}.";
        }
        else if (slot.Equals("morning", StringComparison.OrdinalIgnoreCase))
        {
            feedingManager.MarkFeeding(FeedingWindow.Morning);
            notice = $"Morning feeding marked at {DateTime.Now:HH:mm:ss}.";
        }
        else if (slot.Equals("evening", StringComparison.OrdinalIgnoreCase))
        {
            feedingManager.MarkFeeding(FeedingWindow.Evening);
            notice = $"Evening feeding marked at {DateTime.Now:HH:mm:ss}.";
        }
        else if (slot.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            feedingManager.MarkFeeding(FeedingWindow.Morning);
            feedingManager.MarkFeeding(FeedingWindow.Evening);
            notice = $"Morning and evening feedings marked at {DateTime.Now:HH:mm:ss}.";
        }
        else
        {
            Logger.Warn(Tag, $"Unknown feeding mark request. action={action}, slot={slot}");
            notice = "Unknown action request.";
            level = "error";
        }

        RedirectToNotice(response, notice, level);
        response.Close();
    }

    async Task HandleVacationToggleRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.ContentType = "text/plain";
            byte[] methodError = Encoding.UTF8.GetBytes("Method Not Allowed");
            response.ContentLength64 = methodError.LongLength;
            await response.OutputStream.WriteAsync(methodError, 0, methodError.Length);
            response.Close();
            return;
        }

        var formValues = await ReadFormDataAsync(request);
        var formToken = formValues.TryGetValue("formToken", out var formTokenValue) ? formTokenValue : string.Empty;

        if (!TryConsumeFormToken(formToken))
        {
            RedirectToNotice(response, "Duplicate or invalid form submission. Please refresh and try again.", "error");
            response.Close();
            return;
        }

        feedingManager.ToggleVacationMode();
        var vacationEnabled = feedingManager.IsVacationModeEnabled;
        var notice = vacationEnabled ? "Vacation mode enabled." : "Vacation mode disabled.";
        RedirectToNotice(response, notice, "success");
        response.Close();
    }

    void RedirectToNotice(HttpListenerResponse response, string notice, string level)
    {
        var flashToken = IssueFlashNotice(notice, level);
        var cookie = new Cookie(FlashNoticeCookieName, flashToken, "/")
        {
            HttpOnly = true
        };

        response.Cookies.Add(cookie);
        response.StatusCode = (int)HttpStatusCode.Redirect;
        response.RedirectLocation = "/";
    }

    static async Task<Dictionary<string, string>> ReadFormDataAsync(HttpListenerRequest request)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (request.InputStream == null)
        {
            return values;
        }

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return values;
        }

        var parts = raw.Split('&');
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var index = part.IndexOf('=');
            if (index < 0)
            {
                continue;
            }

            var key = WebUtility.UrlDecode(part.Substring(0, index));
            var value = WebUtility.UrlDecode(part.Substring(index + 1));

            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }

    string BuildMainPageResponse(string notice = null, string noticeLevel = null)
    {
        var uptime = DateTimeOffset.UtcNow - startedAt;
        var uptimeText = FormatDuration(uptime);
        var feedingStatus = feedingManager.GetStatusSnapshot();
        var powerStatus = powerStatusManager.GetSnapshot();
        var formToken = IssueFormToken();
        var ipText = wifiManager.CurrentIpAddress != null
            ? wifiManager.CurrentIpAddress.ToString()
            : "waiting for WiFi";
        return responseManager.BuildMainPage(new MainPageResponseOptions
        {
            Notice = notice,
            NoticeLevel = noticeLevel,
            FormToken = formToken,
            FeedingStateText = feedingManager.FeedingStateText,
            IndicatorStateText = feedingManager.IndicatorStateText,
            MorningWindowLabel = feedingManager.MorningWindowLabel,
            EveningWindowLabel = feedingManager.EveningWindowLabel,
            FeedingStatus = feedingStatus,
            WiFiConnectionState = wifiManager.ConnectionState,
            IpText = ipText,
            DeviceTimeText = deviceTimeManager.CurrentDeviceTimeText,
            UptimeText = uptimeText,
            PowerStatus = powerStatus,
            PushoverConfigured = pushoverConfiguration.IsConfigured,
            PushoverAppName = pushoverConfiguration.AppName,
            PushoverTestEnabled = pushoverTestEnabled,
            VacationModeEnabled = feedingStatus.VacationModeEnabled,
        });
    }

    string BuildDiagnosticsJsonResponse()
    {
        var uptime = DateTimeOffset.UtcNow - startedAt;
        var uptimeText = FormatDuration(uptime);
        var feedingStatus = feedingManager.GetStatusSnapshot();
        var powerStatus = powerStatusManager.GetSnapshot();
        var ipText = wifiManager.CurrentIpAddress != null ? wifiManager.CurrentIpAddress.ToString() : string.Empty;

        return responseManager.BuildDiagnosticsJson(new DiagnosticsJsonOptions
        {
            DeviceName = deviceName,
            WiFiConnected = wifiManager.IsConnected,
            WiFiState = wifiManager.ConnectionState,
            IpAddress = ipText,
            DeviceTime = deviceTimeManager.CurrentDeviceTimeText,
            Uptime = uptimeText,
            FeedingState = feedingManager.FeedingStateText,
            IndicatorState = feedingManager.IndicatorStateText,
            FeedingStatus = feedingStatus,
            PowerStatus = powerStatus,
            PushoverConfigured = pushoverConfiguration.IsConfigured,
            PushoverAppName = pushoverConfiguration.AppName,
            PushoverTestEnabled = pushoverTestEnabled,
            VacationModeEnabled = feedingStatus.VacationModeEnabled,
        });
    }

    async Task HandlePushoverTestRequestAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (!pushoverTestEnabled)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.ContentType = "text/plain";
            byte[] body = Encoding.UTF8.GetBytes("Not Found");
            response.ContentLength64 = body.LongLength;
            await response.OutputStream.WriteAsync(body, 0, body.Length);
            response.Close();
            return;
        }

        if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.ContentType = "text/plain";
            byte[] methodError = Encoding.UTF8.GetBytes("Method Not Allowed");
            response.ContentLength64 = methodError.LongLength;
            await response.OutputStream.WriteAsync(methodError, 0, methodError.Length);
            response.Close();
            return;
        }

        var formValues = await ReadFormDataAsync(request);
        var formToken = formValues.TryGetValue("formToken", out var formTokenValue) ? formTokenValue : string.Empty;

        if (!TryConsumeFormToken(formToken))
        {
            RedirectToNotice(response, "Duplicate or invalid form submission. Please refresh and try again.", "error");
            response.Close();
            return;
        }

        var sent = await pushNotificationManager.SendTestNotificationAsync(DateTime.Now);
        var notice = sent
            ? $"Test push notification sent at {DateTime.Now:HH:mm:ss}."
            : "Test push notification could not be sent. Check Pushover configuration.";
        var level = sent ? "success" : "error";

        RedirectToNotice(response, notice, level);
        response.Close();
    }

    string BuildFeedingsJsonResponse()
    {
        var feedingStatus = feedingManager.GetStatusSnapshot();
        return responseManager.BuildFeedingsJson(new FeedingsJsonOptions
        {
            FeedingStatus = feedingStatus,
            Summary = feedingManager.FeedingStateText,
        });
    }

    string IssueFormToken()
    {
        lock (tokenGate)
        {
            PruneExpiredFormTokensLocked(DateTimeOffset.UtcNow);
            var token = Guid.NewGuid().ToString("N");
            activeFormTokens[token] = DateTimeOffset.UtcNow;
            return token;
        }
    }

    bool TryConsumeFormToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (tokenGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredFormTokensLocked(now);

            if (!activeFormTokens.TryGetValue(token, out var issuedAt))
            {
                return false;
            }

            if (now - issuedAt > FormTokenLifetime)
            {
                activeFormTokens.Remove(token);
                return false;
            }

            activeFormTokens.Remove(token);
            return true;
        }
    }

    void PruneExpiredFormTokensLocked(DateTimeOffset now)
    {
        var keysToRemove = new List<string>();

        foreach (var item in activeFormTokens)
        {
            if (now - item.Value > FormTokenLifetime)
            {
                keysToRemove.Add(item.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            activeFormTokens.Remove(key);
        }
    }

    string IssueFlashNotice(string notice, string level)
    {
        var safeNotice = notice ?? string.Empty;
        var normalizedLevel = string.Equals(level, "error", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : (string.Equals(level, "success", StringComparison.OrdinalIgnoreCase) ? "success" : "info");

        lock (tokenGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredFlashNoticesLocked(now);
            var token = Guid.NewGuid().ToString("N");
            activeFlashNotices[token] = new FlashNoticeEntry(safeNotice, normalizedLevel, now);
            return token;
        }
    }

    FlashNoticeEntry? TryConsumeFlashNotice(HttpListenerRequest request, HttpListenerResponse response)
    {
        var cookie = request.Cookies[FlashNoticeCookieName];
        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value))
        {
            return null;
        }

        // Always expire the client cookie after one read attempt.
        response.Cookies.Add(new Cookie(FlashNoticeCookieName, string.Empty, "/")
        {
            Expires = DateTime.UtcNow.AddDays(-1),
            HttpOnly = true
        });

        lock (tokenGate)
        {
            var now = DateTimeOffset.UtcNow;
            PruneExpiredFlashNoticesLocked(now);

            if (!activeFlashNotices.TryGetValue(cookie.Value, out var entry))
            {
                return null;
            }

            if (now - entry.IssuedAt > FlashNoticeLifetime)
            {
                activeFlashNotices.Remove(cookie.Value);
                return null;
            }

            activeFlashNotices.Remove(cookie.Value);
            return entry;
        }
    }

    void PruneExpiredFlashNoticesLocked(DateTimeOffset now)
    {
        var keysToRemove = new List<string>();

        foreach (var item in activeFlashNotices)
        {
            if (now - item.Value.IssuedAt > FlashNoticeLifetime)
            {
                keysToRemove.Add(item.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            activeFlashNotices.Remove(key);
        }
    }

    static string FormatDuration(TimeSpan duration)
    {
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

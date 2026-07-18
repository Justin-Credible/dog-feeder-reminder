using System;
using System.Net;
using System.Text;

namespace dog_feeder_reminder;

public class MainPageResponseOptions
{
    public string Notice { get; set; }
    public string NoticeLevel { get; set; }
    public string FormToken { get; set; }
    public string FeedingStateText { get; set; }
    public string IndicatorStateText { get; set; }
    public string MorningWindowLabel { get; set; }
    public string EveningWindowLabel { get; set; }
    public FeedingStatusSnapshot FeedingStatus { get; set; }
    public string WiFiConnectionState { get; set; }
    public string IpText { get; set; }
    public string DeviceTimeText { get; set; }
    public string UptimeText { get; set; }
    public PowerStatusSnapshot PowerStatus { get; set; }
    public bool PushoverConfigured { get; set; }
    public string PushoverAppName { get; set; }
    public bool PushoverTestEnabled { get; set; }
    public bool VacationModeEnabled { get; set; }
}

public class DiagnosticsJsonOptions
{
    public string DeviceName { get; set; }
    public bool WiFiConnected { get; set; }
    public string WiFiState { get; set; }
    public string IpAddress { get; set; }
    public string DeviceTime { get; set; }
    public string Uptime { get; set; }
    public string FeedingState { get; set; }
    public string IndicatorState { get; set; }
    public FeedingStatusSnapshot FeedingStatus { get; set; }
    public PowerStatusSnapshot PowerStatus { get; set; }
    public bool PushoverConfigured { get; set; }
    public string PushoverAppName { get; set; }
    public bool PushoverTestEnabled { get; set; }
    public bool VacationModeEnabled { get; set; }
}

public class FeedingsJsonOptions
{
    public FeedingStatusSnapshot FeedingStatus { get; set; }
    public string Summary { get; set; }
}

public class HttpResponseManager
{
    public string BuildMainPage(MainPageResponseOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var safeNotice = string.IsNullOrWhiteSpace(options.Notice) ? string.Empty : WebUtility.HtmlEncode(options.Notice);
        var normalizedNoticeLevel = string.Equals(options.NoticeLevel, "error", StringComparison.OrdinalIgnoreCase)
            ? "error"
            : (string.Equals(options.NoticeLevel, "success", StringComparison.OrdinalIgnoreCase) ? "success" : "info");
        var morningWindowLabel = string.IsNullOrWhiteSpace(options.MorningWindowLabel) ? "morning" : WebUtility.HtmlEncode(options.MorningWindowLabel);
        var eveningWindowLabel = string.IsNullOrWhiteSpace(options.EveningWindowLabel) ? "evening" : WebUtility.HtmlEncode(options.EveningWindowLabel);

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"utf-8\" />");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.AppendLine("    <title>Dog Feeder Control</title>");
        html.AppendLine("    <style>");
        html.AppendLine("        :root { color-scheme: light dark; --bg: #f7f4ef; --bg-accent: #e8f2ff; --panel: #fffdf8; --panel-secondary: #fff7ea; --text: #1d2328; --muted: #5f6b73; --accent: #c04f1d; --accent-strong: #8a3612; --border: #d8d0c2; --shadow: rgba(29, 35, 40, 0.08); }");
        html.AppendLine("        @media (prefers-color-scheme: dark) { :root { --bg: #0d1117; --bg-accent: #131c28; --panel: #141a22; --panel-secondary: #1b222d; --text: #e6edf3; --muted: #9fb0c1; --accent: #ff9b61; --accent-strong: #ffd0b2; --border: #2d3a4a; --shadow: rgba(0, 0, 0, 0.35); } }");
        html.AppendLine("        body { margin: 0; font-family: Segoe UI, Helvetica, Arial, sans-serif; background: linear-gradient(180deg, var(--bg-accent) 0%, var(--bg) 48%, var(--bg-accent) 100%); color: var(--text); }");
        html.AppendLine("        main { max-width: 980px; margin: 0 auto; padding: 20px; }");
        html.AppendLine("        .panel { background: var(--panel); border: 1px solid var(--border); border-radius: 18px; padding: 20px; box-shadow: 0 12px 28px var(--shadow); }");
        html.AppendLine("        .panel + .panel { margin-top: 16px; }");
        html.AppendLine("        h1 { margin: 0 0 8px; font-size: 2rem; }");
        html.AppendLine("        h2 { margin: 0 0 10px; font-size: 1.2rem; }");
        html.AppendLine("        p { margin: 0 0 14px; color: var(--muted); line-height: 1.5; }");
        html.AppendLine("        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin-top: 20px; }");
        html.AppendLine("        .card { background: var(--panel-secondary); border: 1px solid var(--border); border-radius: 14px; padding: 14px; }");
        html.AppendLine("        .label { display: block; color: var(--muted); font-size: 0.84rem; text-transform: uppercase; letter-spacing: 0.08em; margin-bottom: 8px; }");
        html.AppendLine("        .value { font-size: 1.1rem; font-weight: 700; }");
        html.AppendLine("        .subvalue { margin-top: 4px; color: var(--muted); font-size: 0.9rem; }");
        html.AppendLine("        .actions { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 20px; }");
        html.AppendLine("        .actions form { margin: 0; }");
        html.AppendLine("        .banner { margin-top: 12px; border-radius: 12px; padding: 12px 14px; border: 1px solid var(--border); font-weight: 600; transition: opacity 280ms ease, transform 280ms ease, max-height 280ms ease, margin 280ms ease, padding 280ms ease; opacity: 1; transform: translateY(0); max-height: 120px; overflow: hidden; }");
        html.AppendLine("        .banner.hidden { opacity: 0; transform: translateY(-4px); max-height: 0; margin-top: 0; padding-top: 0; padding-bottom: 0; border-width: 0; }");
        html.AppendLine("        .banner-info { background: color-mix(in srgb, var(--panel-secondary) 85%, var(--bg-accent)); }");
        html.AppendLine("        .banner-success { background: color-mix(in srgb, #5bcf7a 22%, var(--panel-secondary)); border-color: color-mix(in srgb, #5bcf7a 45%, var(--border)); }");
        html.AppendLine("        .banner-error { background: color-mix(in srgb, #ff7b72 24%, var(--panel-secondary)); border-color: color-mix(in srgb, #ff7b72 48%, var(--border)); }");
        html.AppendLine("        button { border: 1px solid var(--border); background: var(--panel-secondary); color: var(--text); border-radius: 10px; padding: 10px 14px; font-weight: 700; cursor: pointer; }");
        html.AppendLine("        button:hover { border-color: var(--accent); color: var(--accent-strong); }");
        html.AppendLine("        .button-primary { background: var(--accent); border-color: var(--accent); color: #fff; }");
        html.AppendLine("        .button-primary:hover { filter: brightness(1.05); color: #fff; }");
        html.AppendLine("        a { color: var(--accent); text-decoration: none; }");
        html.AppendLine("        code { background: var(--panel-secondary); border: 1px solid var(--border); padding: 2px 6px; border-radius: 8px; }");
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("    <main>");
        html.AppendLine("        <section class=\"panel\">");
        html.AppendLine("            <h1>Dog Feeder Control</h1>");
        html.AppendLine("            <p>Mark feedings from this page. Morning and evening status is shown live and follows the configured feeding windows.</p>");
        if (!string.IsNullOrWhiteSpace(safeNotice))
        {
            html.AppendLine($"            <div id=\"action-banner\" class=\"banner banner-{normalizedNoticeLevel}\">{safeNotice}</div>");
        }
        html.AppendLine("            <div class=\"grid\">");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Feeding Status</span><div class=\"value\">{options.FeedingStateText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Indicator LEDs</span><div class=\"value\">{options.IndicatorStateText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Morning Feeding ({morningWindowLabel})</span><div class=\"value\">{FormatFeedingStatus(options.FeedingStatus.MorningFed, options.FeedingStatus.InMorningWindow, options.FeedingStatus.MorningWindowMissed)}</div><div class=\"subvalue\">Fed: {FormatBoolean(options.FeedingStatus.MorningFed)} | Window Active: {FormatBoolean(options.FeedingStatus.InMorningWindow)}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Evening Feeding ({eveningWindowLabel})</span><div class=\"value\">{FormatFeedingStatus(options.FeedingStatus.EveningFed, options.FeedingStatus.InEveningWindow, options.FeedingStatus.EveningWindowMissed)}</div><div class=\"subvalue\">Fed: {FormatBoolean(options.FeedingStatus.EveningFed)} | Window Active: {FormatBoolean(options.FeedingStatus.InEveningWindow)}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Vacation Mode</span><div class=\"value\">{FormatBoolean(options.VacationModeEnabled)}</div></div>");
        html.AppendLine("            </div>");
        html.AppendLine("            <div class=\"actions\">");
        html.AppendLine($"                <form method=\"post\" action=\"/feedings/mark\"><input type=\"hidden\" name=\"formToken\" value=\"{options.FormToken}\" /><input type=\"hidden\" name=\"slot\" value=\"morning\" /><button class=\"button-primary\" type=\"submit\">Mark Morning Fed</button></form>");
        html.AppendLine($"                <form method=\"post\" action=\"/feedings/mark\"><input type=\"hidden\" name=\"formToken\" value=\"{options.FormToken}\" /><input type=\"hidden\" name=\"slot\" value=\"evening\" /><button class=\"button-primary\" type=\"submit\">Mark Evening Fed</button></form>");
        html.AppendLine($"                <form method=\"post\" action=\"/feedings/mark\"><input type=\"hidden\" name=\"formToken\" value=\"{options.FormToken}\" /><input type=\"hidden\" name=\"slot\" value=\"both\" /><button type=\"submit\">Mark Both Fed</button></form>");
        html.AppendLine($"                <form method=\"post\" action=\"/feedings/mark\"><input type=\"hidden\" name=\"formToken\" value=\"{options.FormToken}\" /><input type=\"hidden\" name=\"action\" value=\"reset\" /><button type=\"submit\">Reset Feedings</button></form>");
        html.AppendLine($"                <form method=\"post\" action=\"/vacation/toggle\"><input type=\"hidden\" name=\"formToken\" value=\"{options.FormToken}\" /><button type=\"submit\">{(options.VacationModeEnabled ? "Disable Vacation Mode" : "Enable Vacation Mode")}</button></form>");
        if (options.PushoverTestEnabled)
        {
            html.AppendLine($"                <form method=\"post\" action=\"/api/pushover/test\"><input type=\"hidden\" name=\"formToken\" value=\"{options.FormToken}\" /><button type=\"submit\">Send Test Push</button></form>");
        }
        html.AppendLine("            </div>");
        html.AppendLine("        </section>");
        html.AppendLine("        <section class=\"panel\">");
        html.AppendLine("            <h2>Device Diagnostics</h2>");
        html.AppendLine("            <p>System and network details are shown below for troubleshooting.</p>");
        html.AppendLine("            <p>JSON endpoint: <a href=\"/api/diagnostics\">/api/diagnostics</a> | Feedings endpoint: <a href=\"/api/feedings\">/api/feedings</a> | Log file: <a href=\"/log.txt\">/log.txt</a></p>");
        html.AppendLine("            <p>OS files: <a href=\"/system-log.txt\">Boot log</a> | <a href=\"/crash/app.txt\">App crash</a> | <a href=\"/crash/runtime.txt\">Runtime crash</a> | <a href=\"/crash/os.txt\">OS crash</a></p>");
        html.AppendLine("            <div class=\"grid\">");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">WiFi</span><div class=\"value\">{options.WiFiConnectionState}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">IP Address</span><div class=\"value\">{options.IpText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Device Time</span><div class=\"value\">{options.DeviceTimeText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Uptime</span><div class=\"value\">{options.UptimeText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Pushover</span><div class=\"value\">{FormatBoolean(options.PushoverConfigured)}</div><div class=\"subvalue\">App: {WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(options.PushoverAppName) ? "Dog Feeder Reminder" : options.PushoverAppName)}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Pushover Test</span><div class=\"value\">{FormatBoolean(options.PushoverTestEnabled)}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Battery Voltage</span><div class=\"value\">{options.PowerStatus.BatteryVoltageText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Charge State</span><div class=\"value\">{options.PowerStatus.ChargeStateText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Power Source</span><div class=\"value\">{options.PowerStatus.SourceText}</div><div class=\"subvalue\">{options.PowerStatus.Notes}</div></div>");
        html.AppendLine("            </div>");
        html.AppendLine("        </section>");
        html.AppendLine("    </main>");
        html.AppendLine("    <script>");
        html.AppendLine("        (function () {");
        html.AppendLine("            var banner = document.getElementById('action-banner');");
        html.AppendLine("            if (!banner) { return; }");
        html.AppendLine("            setTimeout(function () { banner.classList.add('hidden'); }, 4200);");
        html.AppendLine("        })();");
        html.AppendLine("    </script>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    public string BuildDiagnosticsJson(DiagnosticsJsonOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return "{" +
               $"\"device\":\"{EscapeJson(options.DeviceName)}\"," +
               $"\"wifiConnected\":{options.WiFiConnected.ToString().ToLowerInvariant()}," +
               $"\"wifiState\":\"{EscapeJson(options.WiFiState)}\"," +
               $"\"ipAddress\":\"{EscapeJson(options.IpAddress)}\"," +
               $"\"deviceTime\":\"{EscapeJson(options.DeviceTime)}\"," +
               $"\"uptime\":\"{EscapeJson(options.Uptime)}\"," +
               $"\"feedingStatus\":\"{EscapeJson(options.FeedingState)}\"," +
               $"\"indicatorState\":\"{EscapeJson(options.IndicatorState)}\"," +
               $"\"pushoverConfigured\":{options.PushoverConfigured.ToString().ToLowerInvariant()}," +
               $"\"pushoverAppName\":\"{EscapeJson(options.PushoverAppName)}\"," +
               $"\"pushoverTestEnabled\":{options.PushoverTestEnabled.ToString().ToLowerInvariant()}," +
               $"\"vacationModeEnabled\":{options.VacationModeEnabled.ToString().ToLowerInvariant()}," +
               $"\"morningFed\":{options.FeedingStatus.MorningFed.ToString().ToLowerInvariant()}," +
               $"\"eveningFed\":{options.FeedingStatus.EveningFed.ToString().ToLowerInvariant()}," +
               $"\"inMorningWindow\":{options.FeedingStatus.InMorningWindow.ToString().ToLowerInvariant()}," +
               $"\"inEveningWindow\":{options.FeedingStatus.InEveningWindow.ToString().ToLowerInvariant()}," +
               $"\"morningWindowMissed\":{options.FeedingStatus.MorningWindowMissed.ToString().ToLowerInvariant()}," +
               $"\"eveningWindowMissed\":{options.FeedingStatus.EveningWindowMissed.ToString().ToLowerInvariant()}," +
               $"\"batteryVoltage\":\"{EscapeJson(options.PowerStatus.BatteryVoltageText)}\"," +
               $"\"chargeState\":\"{EscapeJson(options.PowerStatus.ChargeStateText)}\"," +
               $"\"powerSource\":\"{EscapeJson(options.PowerStatus.SourceText)}\"," +
               $"\"powerNotes\":\"{EscapeJson(options.PowerStatus.Notes)}\"" +
               "}";
    }

    public string BuildFeedingsJson(FeedingsJsonOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return "{" +
               $"\"morningFed\":{options.FeedingStatus.MorningFed.ToString().ToLowerInvariant()}," +
               $"\"eveningFed\":{options.FeedingStatus.EveningFed.ToString().ToLowerInvariant()}," +
               $"\"inMorningWindow\":{options.FeedingStatus.InMorningWindow.ToString().ToLowerInvariant()}," +
               $"\"inEveningWindow\":{options.FeedingStatus.InEveningWindow.ToString().ToLowerInvariant()}," +
               $"\"morningWindowMissed\":{options.FeedingStatus.MorningWindowMissed.ToString().ToLowerInvariant()}," +
               $"\"eveningWindowMissed\":{options.FeedingStatus.EveningWindowMissed.ToString().ToLowerInvariant()}," +
               $"\"summary\":\"{EscapeJson(options.Summary)}\"" +
               "}";
    }

    static string FormatFeedingStatus(bool fed, bool inWindow, bool missedWindow)
    {
        if (fed)
        {
            return "Fed";
        }

        if (inWindow)
        {
            return "Due now (blinking)";
        }

        if (missedWindow)
        {
            return "Missed window";
        }

        return "Pending";
    }

    static string FormatBoolean(bool value)
    {
        return value ? "Yes" : "No";
    }

    static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
using Meadow;
using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public class WebServerManager
{
    const string Tag = "WebServerManager";
    readonly WiFiManager wifiManager;
    readonly DeviceTimeManager deviceTimeManager;
    readonly DateTimeOffset startedAt;
    readonly string deviceName;
    readonly string ledState;

    HttpListener webServer;
    bool webServerStarted;

    public WebServerManager(
        WiFiManager wifiManager,
        DeviceTimeManager deviceTimeManager,
        DateTimeOffset startedAt,
        string deviceName,
        string ledState)
    {
        this.wifiManager = wifiManager;
        this.deviceTimeManager = deviceTimeManager;
        this.startedAt = startedAt;
        this.deviceName = deviceName;
        this.ledState = ledState;
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
            return;
        }

        webServer = new HttpListener();
        webServer.Prefixes.Add($"http://{ipAddress}:8081/");
        webServer.Start();
        webServerStarted = true;

        Logger.Info(Tag, $"Web server listening at http://{ipAddress}:8081/");

        try
        {
            while (webServerStarted)
            {
                var context = await webServer.GetContextAsync();
                await ProcessRequestAsync(context);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(Tag, $"Web server stopped: {ex.Message}");
        }
        finally
        {
            webServer?.Close();
        }
    }

    async Task ProcessRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        Logger.Info(Tag, $"HTTP {request.HttpMethod} {request.Url}");

        string body;
        string contentType;

        if (request.Url.AbsolutePath.Equals("/api/diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            contentType = "application/json";
            body = BuildDiagnosticsJson();
        }
        else
        {
            contentType = "text/html";
            body = BuildDiagnosticsPage();
        }

        byte[] payload = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = payload.LongLength;

        await response.OutputStream.WriteAsync(payload, 0, payload.Length);
        response.Close();
    }

    string BuildDiagnosticsPage()
    {
        var uptime = DateTimeOffset.UtcNow - startedAt;
        var ipText = wifiManager.CurrentIpAddress != null
            ? wifiManager.CurrentIpAddress.ToString()
            : "waiting for WiFi";

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("    <meta charset=\"utf-8\" />");
        html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        html.AppendLine("    <title>Dog Feeder Diagnostics</title>");
        html.AppendLine("    <style>");
        html.AppendLine("        :root { color-scheme: light; --bg: #f4f0e8; --panel: #fffdf8; --text: #1d2328; --muted: #5f6b73; --accent: #d97706; --border: #d8d0c2; }");
        html.AppendLine("        body { margin: 0; font-family: Arial, Helvetica, sans-serif; background: linear-gradient(180deg, #fff7ea 0%, var(--bg) 45%, #eef3f7 100%); color: var(--text); }");
        html.AppendLine("        main { max-width: 900px; margin: 0 auto; padding: 24px; }");
        html.AppendLine("        .hero { background: var(--panel); border: 1px solid var(--border); border-radius: 20px; padding: 24px; box-shadow: 0 12px 32px rgba(29, 35, 40, 0.08); }");
        html.AppendLine("        h1 { margin: 0 0 8px; font-size: 2rem; }");
        html.AppendLine("        p { margin: 0 0 16px; color: var(--muted); line-height: 1.5; }");
        html.AppendLine("        .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin-top: 20px; }");
        html.AppendLine("        .card { background: #ffffff; border: 1px solid var(--border); border-radius: 16px; padding: 16px; }");
        html.AppendLine("        .label { display: block; color: var(--muted); font-size: 0.84rem; text-transform: uppercase; letter-spacing: 0.08em; margin-bottom: 8px; }");
        html.AppendLine("        .value { font-size: 1.1rem; font-weight: 700; }");
        html.AppendLine("        a { color: var(--accent); text-decoration: none; }");
        html.AppendLine("        code { background: #f4efe6; padding: 2px 6px; border-radius: 8px; }");
        html.AppendLine("    </style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");
        html.AppendLine("    <main>");
        html.AppendLine("        <section class=\"hero\">");
        html.AppendLine("            <h1>Dog Feeder Diagnostics</h1>");
        html.AppendLine("            <p>Live status for the Feather F7. Use this page to confirm WiFi, uptime, and app state from any browser on the same network.</p>");
        html.AppendLine("            <p>JSON endpoint: <a href=\"/api/diagnostics\">/api/diagnostics</a></p>");
        html.AppendLine("            <div class=\"grid\">");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">WiFi</span><div class=\"value\">{wifiManager.ConnectionState}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">IP Address</span><div class=\"value\">{ipText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Device Time</span><div class=\"value\">{deviceTimeManager.CurrentDeviceTimeText}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">Uptime</span><div class=\"value\">{FormatDuration(uptime)}</div></div>");
        html.AppendLine($"                <div class=\"card\"><span class=\"label\">LED Cycle</span><div class=\"value\">{ledState}</div></div>");
        html.AppendLine("            </div>");
        html.AppendLine("        </section>");
        html.AppendLine("    </main>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return html.ToString();
    }

    string BuildDiagnosticsJson()
    {
        var uptime = DateTimeOffset.UtcNow - startedAt;
        var ipText = wifiManager.CurrentIpAddress != null ? wifiManager.CurrentIpAddress.ToString() : string.Empty;

        return "{" +
               $"\"device\":\"{EscapeJson(deviceName)}\"," +
               $"\"wifiConnected\":{wifiManager.IsConnected.ToString().ToLowerInvariant()}," +
               $"\"wifiState\":\"{EscapeJson(wifiManager.ConnectionState)}\"," +
               $"\"ipAddress\":\"{EscapeJson(ipText)}\"," +
               $"\"deviceTime\":\"{EscapeJson(deviceTimeManager.CurrentDeviceTimeText)}\"," +
               $"\"uptime\":\"{FormatDuration(uptime)}\"," +
               $"\"ledState\":\"{EscapeJson(ledState)}\"" +
               "}";
    }

    static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static string FormatDuration(TimeSpan duration)
    {
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

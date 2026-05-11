using Meadow;
using Meadow.Hardware;
using System;
using System.Net;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public class WiFiManager
{
    const string Tag = "WiFiManager";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan ConnectingGracePeriod = TimeSpan.FromSeconds(90);

    readonly IWiFiNetworkAdapter wifi;
    readonly string wifiSsid;
    readonly string wifiPassword;

    DateTimeOffset lastConnectAttempt = DateTimeOffset.MinValue;
    DateTimeOffset lastScanAttempt = DateTimeOffset.MinValue;
    DateTimeOffset connectingSince = DateTimeOffset.MinValue;
    bool isConnecting;
    TaskCompletionSource<IPAddress> networkReadySource = new TaskCompletionSource<IPAddress>(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPAddress CurrentIpAddress { get; private set; }

    public bool IsConnected => wifi.IsConnected;

    public string ConnectionState => IsConnected ? "Connected" : (isConnecting ? "Connecting" : "Disconnected");

    public Task<IPAddress> WaitForNetworkReadyAsync()
    {
        if (CurrentIpAddress != null)
        {
            return Task.FromResult(CurrentIpAddress);
        }

        return networkReadySource.Task;
    }

    // Credentials are only needed for explicitly overrideing the securely stored ESP crednetials.
    // Auto connect via the ESP stored credentials occurs via AutomaticallyStartNetwork config.
    public WiFiManager(IWiFiNetworkAdapter wifi, string wifiSsid = "", string wifiPassword = "")
    {
        this.wifi = wifi;
        this.wifiSsid = wifiSsid;
        this.wifiPassword = wifiPassword;
    }

    public async Task InitializeAsync()
    {
        wifi.NetworkConnected += OnNetworkConnected;
        wifi.NetworkConnecting += OnNetworkConnecting;
        wifi.NetworkDisconnected += OnNetworkDisconnected;

        if (wifi.IsConnected)
        {
            CurrentIpAddress = wifi.IpAddress;
            networkReadySource.TrySetResult(CurrentIpAddress);
            return;
        }

        await EnsureConnectedAsync();
    }

    public async Task EnsureConnectedAsync()
    {
        if (wifi.IsConnected)
        {
            CurrentIpAddress = wifi.IpAddress;
            networkReadySource.TrySetResult(CurrentIpAddress);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (isConnecting)
        {
            var connectingFor = now - connectingSince;
            if (connectingFor < ConnectingGracePeriod)
            {
                return;
            }

            Logger.Warn(Tag, $"WiFi has been connecting for {connectingFor.TotalSeconds:0}s without success. Will try a fresh connect attempt.");
            isConnecting = false;
        }

        if (now - lastConnectAttempt < RetryInterval)
        {
            return;
        }

        lastConnectAttempt = now;

        if (!string.IsNullOrWhiteSpace(wifiSsid) && !string.IsNullOrWhiteSpace(wifiPassword))
        {
            try
            {
                Logger.Info(Tag, $"Attempting explicit WiFi connect to '{wifiSsid}'...");
                await wifi.Connect(wifiSsid, wifiPassword, ConnectTimeout);
            }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("already connecting", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    isConnecting = true;
                    if (connectingSince == DateTimeOffset.MinValue)
                    {
                        connectingSince = DateTimeOffset.UtcNow;
                    }

                    Logger.Info(Tag, "Adapter is already connecting. Waiting for completion.");
                }
                else
                {
                    Logger.Error(Tag, $"Explicit WiFi connect failed: {ex.Message}");
                }
            }
        }
        else
        {
            Logger.Warn(Tag, "Credentials not explicitly provided to WiFiManager; waiting for OS auto-connect using stored credentials.");
        }

        if (!wifi.IsConnected && !isConnecting && (now - lastScanAttempt >= RetryInterval))
        {
            try
            {
                Logger.Info(Tag, "WiFi still disconnected. Scanning for nearby access points...");
                lastScanAttempt = DateTimeOffset.UtcNow;
                var networks = await wifi.Scan(ScanTimeout);
                Logger.Info(Tag, $"Scan complete. Found {networks.Count} access point(s).");

                foreach (var network in networks)
                {
                    Logger.Info(Tag, $"AP: {network.Ssid} RSSI:{network.SignalDbStrength}");
                }

                if (networks.Count == 0)
                {
                    Logger.Warn(Tag, "No WiFi access points detected. Verify 2.4GHz WiFi is enabled and the board antenna path is correct.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Tag, $"WiFi scan failed: {ex.Message}");
            }
        }
    }

    void OnNetworkConnected(INetworkAdapter sender, NetworkConnectionEventArgs args)
    {
        isConnecting = false;
        connectingSince = DateTimeOffset.MinValue;
        CurrentIpAddress = args.IpAddress;
        networkReadySource.TrySetResult(CurrentIpAddress);
        Logger.Info(Tag, $"WiFi connected. IP address: {CurrentIpAddress}");
    }

    void OnNetworkConnecting(INetworkAdapter sender)
    {
        if (!isConnecting)
        {
            connectingSince = DateTimeOffset.UtcNow;
        }

        isConnecting = true;
        Logger.Info(Tag, "WiFi connecting...");
    }

    void OnNetworkDisconnected(INetworkAdapter sender, NetworkDisconnectionEventArgs args)
    {
        isConnecting = false;
        connectingSince = DateTimeOffset.MinValue;
        Logger.Warn(Tag, $"WiFi disconnected because {args.Reason}");
    }
}

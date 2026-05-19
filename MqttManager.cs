using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace dog_feeder_reminder;

public readonly struct MqttConfiguration
{
    public string BrokerHostname { get; }
    public int BrokerPort { get; }
    public string TopicPrefix { get; }
    public string ClientId { get; }
    public string Username { get; }
    public string Password { get; }
    public bool IsConfigured { get; }

    public MqttConfiguration(
        string brokerHostname,
        int brokerPort,
        string topicPrefix,
        string clientId,
        string username,
        string password)
    {
        BrokerHostname = brokerHostname ?? string.Empty;
        BrokerPort = brokerPort > 0 ? brokerPort : 1883;
        TopicPrefix = string.IsNullOrWhiteSpace(topicPrefix) ? "dog-feeder" : topicPrefix.Trim();
        ClientId = string.IsNullOrWhiteSpace(clientId) ? "dog-feeder-" + Guid.NewGuid().ToString("N").Substring(0, 8) : clientId;
        Username = username ?? string.Empty;
        Password = password ?? string.Empty;
        IsConfigured = !string.IsNullOrWhiteSpace(brokerHostname);
    }
}

public class MqttManager
{
    const string Tag = "MqttManager";
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    readonly MqttConfiguration mqttConfiguration;
    readonly TaskCompletionSource<bool> connectedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly SemaphoreSlim publishGate = new SemaphoreSlim(1, 1);
    readonly string feedingCommandTopic;
    readonly string vacationCommandTopic;
    TcpClient tcpClient;
    NetworkStream networkStream;
    Action<string> feedingCommandHandler;
    Action<string> vacationCommandHandler;
    int nextPacketId = 1;
    int reconnectLoopActive;
    bool isConnected;

    public bool IsConnected => isConnected;

    public Task WaitForConnectedAsync()
    {
        if (isConnected)
        {
            return Task.CompletedTask;
        }

        return connectedSource.Task;
    }

    public MqttManager(MqttConfiguration mqttConfiguration)
    {
        this.mqttConfiguration = mqttConfiguration;
        feedingCommandTopic = $"{mqttConfiguration.TopicPrefix}/commands/feeding";
        vacationCommandTopic = $"{mqttConfiguration.TopicPrefix}/commands/vacation";
    }

    public void RegisterCommandHandlers(Action<string> feedingCommandHandler, Action<string> vacationCommandHandler)
    {
        this.feedingCommandHandler = feedingCommandHandler;
        this.vacationCommandHandler = vacationCommandHandler;
    }

    public async Task InitializeAsync()
    {
        if (!mqttConfiguration.IsConfigured)
        {
            Logger.Warn(Tag, "MQTT is not configured. Skipping initialization.");
            return;
        }

        if (Interlocked.CompareExchange(ref reconnectLoopActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (!isConnected)
            {
                try
                {
                    var hasCredentials =
                        !string.IsNullOrWhiteSpace(mqttConfiguration.Username) &&
                        !string.IsNullOrWhiteSpace(mqttConfiguration.Password);

                    Logger.Info(
                        Tag,
                        $"Connecting to MQTT broker {mqttConfiguration.BrokerHostname}:{mqttConfiguration.BrokerPort} as client '{mqttConfiguration.ClientId}' (auth={(hasCredentials ? "on" : "off")}, protocol=3.1.1)." );

                    await ConnectSocketAsync();
                    await SendPacketAsync(BuildConnectPacket(hasCredentials));
                    await ReadConnAckAsync();
                    await SendPacketAsync(BuildSubscribePacket(feedingCommandTopic, vacationCommandTopic));

                    isConnected = true;
                    connectedSource.TrySetResult(true);
                    Logger.Info(Tag, $"MQTT connected to {mqttConfiguration.BrokerHostname}:{mqttConfiguration.BrokerPort}");

                    _ = Task.Run(ReceiveLoopAsync);
                    return;
                }
                catch (Exception ex)
                {
                    ResetConnection();
                    Logger.Error(Tag, $"MQTT initialization failed: {ex.GetType().Name}: {ex.Message}");
                }

                Logger.Warn(Tag, $"Retrying MQTT connection in {RetryDelay.TotalSeconds:0} seconds.");
                await Task.Delay(RetryDelay);
            }
        }
        finally
        {
            Interlocked.Exchange(ref reconnectLoopActive, 0);
        }
    }

    public async Task PublishMorningFedStateAsync(bool fed)
    {
        await PublishAsync($"{mqttConfiguration.TopicPrefix}/feeding/morning-fed", fed.ToString().ToLower());
    }

    public async Task PublishEveningFedStateAsync(bool fed)
    {
        await PublishAsync($"{mqttConfiguration.TopicPrefix}/feeding/evening-fed", fed.ToString().ToLower());
    }

    public async Task PublishVacationModeStateAsync(bool enabled)
    {
        await PublishAsync($"{mqttConfiguration.TopicPrefix}/vacation-mode", enabled.ToString().ToLower());
    }

    public async Task PublishFeedingStatusSnapshotAsync(FeedingStatusSnapshot snapshot)
    {
        var json = BuildStatusJson(snapshot);
        await PublishAsync($"{mqttConfiguration.TopicPrefix}/status", json);
    }

    async Task PublishAsync(string topic, string payload)
    {
        if (!isConnected || networkStream == null)
        {
            return;
        }

        await publishGate.WaitAsync();
        try
        {
            await SendPacketAsync(BuildPublishPacket(topic, payload));
        }
        catch (Exception ex)
        {
            TriggerReconnect();
            Logger.Warn(Tag, $"Failed to publish to {topic}: {ex.Message}");
        }
        finally
        {
            publishGate.Release();
        }
    }

    async Task ConnectSocketAsync()
    {
        tcpClient?.Dispose();
        tcpClient = new TcpClient();

        var connectTask = Task.Run(() => tcpClient.Connect(mqttConfiguration.BrokerHostname, mqttConfiguration.BrokerPort));
        if (await Task.WhenAny(connectTask, Task.Delay(ConnectTimeout)) != connectTask)
        {
            throw new TimeoutException("Timed out opening MQTT TCP connection.");
        }

        await connectTask;
        networkStream = tcpClient.GetStream();
        networkStream.ReadTimeout = (int)ReadTimeout.TotalMilliseconds;
        networkStream.WriteTimeout = (int)ReadTimeout.TotalMilliseconds;
    }

    async Task ReceiveLoopAsync()
    {
        while (isConnected)
        {
            try
            {
                var fixedHeader = await TryReadByteAsync(ReadTimeout);
                if (!fixedHeader.HasValue)
                {
                    continue;
                }

                var remainingLength = await ReadRemainingLengthAsync();
                var body = await ReadExactAsync(remainingLength);
                await HandleIncomingPacketAsync(fixedHeader.Value, body);
            }
            catch (Exception ex)
            {
                if (isConnected)
                {
                    Logger.Warn(Tag, $"MQTT receive loop stopped: {ex.GetType().Name}: {ex.Message}");
                    TriggerReconnect();
                }

                return;
            }
        }
    }

    async Task HandleIncomingPacketAsync(byte fixedHeader, byte[] body)
    {
        var packetType = (fixedHeader >> 4) & 0x0F;
        switch (packetType)
        {
            case 3:
                HandleIncomingPublish(fixedHeader, body);
                break;
            case 13:
                // PINGRESP
                break;
            case 9:
                // SUBACK
                Logger.Info(Tag, "MQTT command subscription acknowledged.");
                break;
            case 12:
                // PINGREQ from broker - reply with PINGRESP
                await SendPacketAsync(new byte[] { 0xD0, 0x00 });
                break;
            default:
                break;
        }
    }

    void HandleIncomingPublish(byte fixedHeader, byte[] body)
    {
        if (body == null || body.Length < 2)
        {
            return;
        }

        var qos = (fixedHeader >> 1) & 0x03;
        var topicLength = (body[0] << 8) | body[1];
        if (body.Length < 2 + topicLength)
        {
            return;
        }

        var index = 2;
        var topic = Encoding.UTF8.GetString(body, index, topicLength);
        index += topicLength;

        if (qos > 0)
        {
            if (body.Length < index + 2)
            {
                return;
            }

            index += 2;
        }

        var payloadLength = body.Length - index;
        var payload = payloadLength > 0
            ? Encoding.UTF8.GetString(body, index, payloadLength)
            : string.Empty;

        DispatchCommand(topic, payload);
    }

    void DispatchCommand(string topic, string payload)
    {
        var normalizedPayload = (payload ?? string.Empty).Trim();
        if (string.Equals(topic, feedingCommandTopic, StringComparison.Ordinal))
        {
            Logger.Info(Tag, $"MQTT feeding command received: '{normalizedPayload}'");
            feedingCommandHandler?.Invoke(normalizedPayload);
            return;
        }

        if (string.Equals(topic, vacationCommandTopic, StringComparison.Ordinal))
        {
            Logger.Info(Tag, $"MQTT vacation command received: '{normalizedPayload}'");
            vacationCommandHandler?.Invoke(normalizedPayload);
        }
    }

    async Task SendPacketAsync(byte[] packet)
    {
        if (networkStream == null)
        {
            throw new InvalidOperationException("MQTT stream is not connected.");
        }

        await networkStream.WriteAsync(packet, 0, packet.Length);
        await networkStream.FlushAsync();
    }

    async Task ReadConnAckAsync()
    {
        if (networkStream == null)
        {
            throw new InvalidOperationException("MQTT stream is not connected.");
        }

        var response = new byte[4];
        var bytesRead = 0;

        while (bytesRead < response.Length)
        {
            var readTask = networkStream.ReadAsync(response, bytesRead, response.Length - bytesRead);
            if (await Task.WhenAny(readTask, Task.Delay(ReadTimeout)) != readTask)
            {
                throw new TimeoutException("Timed out waiting for MQTT CONNACK.");
            }

            var count = await readTask;
            if (count <= 0)
            {
                throw new IOException("MQTT broker closed connection before CONNACK.");
            }

            bytesRead += count;
        }

        if (response[0] != 0x20 || response[1] != 0x02)
        {
            throw new InvalidOperationException($"Unexpected MQTT CONNACK header: {response[0]:X2} {response[1]:X2}");
        }

        if (response[3] != 0x00)
        {
            throw new InvalidOperationException($"MQTT broker rejected connection with code {response[3]}.");
        }
    }

    byte[] BuildConnectPacket(bool hasCredentials)
    {
        using var body = new MemoryStream();
        WriteString(body, "MQTT");
        body.WriteByte(0x04);

        byte connectFlags = 0x02;
        if (hasCredentials)
        {
            connectFlags |= 0x80;
            connectFlags |= 0x40;
        }

        body.WriteByte(connectFlags);
    WriteUInt16(body, 0);
        WriteString(body, mqttConfiguration.ClientId);

        if (hasCredentials)
        {
            WriteString(body, mqttConfiguration.Username);
            WriteString(body, mqttConfiguration.Password);
        }

        return BuildPacket(0x10, body.ToArray());
    }

    byte[] BuildSubscribePacket(string firstTopic, string secondTopic)
    {
        using var body = new MemoryStream();
        WriteUInt16(body, GetNextPacketId());

        WriteString(body, firstTopic);
        body.WriteByte(0x00);

        WriteString(body, secondTopic);
        body.WriteByte(0x00);

        return BuildPacket(0x82, body.ToArray());
    }

    byte[] BuildPublishPacket(string topic, string payload)
    {
        using var body = new MemoryStream();
        WriteString(body, topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);
        body.Write(payloadBytes, 0, payloadBytes.Length);
        return BuildPacket(0x31, body.ToArray());
    }

    static byte[] BuildPacket(byte fixedHeader, byte[] body)
    {
        using var packet = new MemoryStream();
        packet.WriteByte(fixedHeader);
        WriteRemainingLength(packet, body.Length);
        packet.Write(body, 0, body.Length);
        return packet.ToArray();
    }

    static void WriteRemainingLength(Stream stream, int length)
    {
        do
        {
            var encodedByte = length % 128;
            length /= 128;

            if (length > 0)
            {
                encodedByte |= 0x80;
            }

            stream.WriteByte((byte)encodedByte);
        }
        while (length > 0);
    }

    static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteUInt16(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    int GetNextPacketId()
    {
        var packetId = Interlocked.Increment(ref nextPacketId);
        var normalized = packetId % 0xFFFF;
        return normalized == 0 ? 1 : normalized;
    }

    async Task<byte?> TryReadByteAsync(TimeSpan timeout)
    {
        if (networkStream == null)
        {
            throw new InvalidOperationException("MQTT stream is not connected.");
        }

        var buffer = new byte[1];
        var readTask = networkStream.ReadAsync(buffer, 0, 1);
        if (await Task.WhenAny(readTask, Task.Delay(timeout)) != readTask)
        {
            return null;
        }

        var count = await readTask;
        if (count <= 0)
        {
            throw new IOException("MQTT broker closed connection.");
        }

        return buffer[0];
    }

    async Task<int> ReadRemainingLengthAsync()
    {
        var multiplier = 1;
        var value = 0;

        for (var i = 0; i < 4; i++)
        {
            var next = await TryReadByteAsync(ReadTimeout);
            if (!next.HasValue)
            {
                throw new TimeoutException("Timed out reading MQTT remaining length.");
            }

            var encodedByte = next.Value;
            value += (encodedByte & 127) * multiplier;

            if ((encodedByte & 128) == 0)
            {
                return value;
            }

            multiplier *= 128;
        }

        throw new InvalidOperationException("Malformed MQTT remaining length field.");
    }

    async Task<byte[]> ReadExactAsync(int length)
    {
        if (networkStream == null)
        {
            throw new InvalidOperationException("MQTT stream is not connected.");
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var buffer = new byte[length];
        var read = 0;

        while (read < length)
        {
            var readTask = networkStream.ReadAsync(buffer, read, length - read);
            if (await Task.WhenAny(readTask, Task.Delay(ReadTimeout)) != readTask)
            {
                throw new TimeoutException("Timed out reading MQTT packet body.");
            }

            var count = await readTask;
            if (count <= 0)
            {
                throw new IOException("MQTT broker closed connection while reading packet body.");
            }

            read += count;
        }

        return buffer;
    }

    void TriggerReconnect()
    {
        if (!isConnected)
        {
            _ = Task.Run(InitializeAsync);
            return;
        }

        ResetConnection();
        _ = Task.Run(InitializeAsync);
    }

    void ResetConnection()
    {
        isConnected = false;

        try
        {
            networkStream?.Dispose();
        }
        catch
        {
        }

        try
        {
            tcpClient?.Dispose();
        }
        catch
        {
        }

        networkStream = null;
        tcpClient = null;
    }

    static string BuildStatusJson(FeedingStatusSnapshot snapshot)
    {
        return $"{{\"morning_fed\":{(snapshot.MorningFed ? "true" : "false")},\"evening_fed\":{(snapshot.EveningFed ? "true" : "false")},\"in_morning_window\":{(snapshot.InMorningWindow ? "true" : "false")},\"in_evening_window\":{(snapshot.InEveningWindow ? "true" : "false")},\"morning_missed\":{(snapshot.MorningWindowMissed ? "true" : "false")},\"evening_missed\":{(snapshot.EveningWindowMissed ? "true" : "false")},\"vacation_mode\":{(snapshot.VacationModeEnabled ? "true" : "false")}}}";
    }
}


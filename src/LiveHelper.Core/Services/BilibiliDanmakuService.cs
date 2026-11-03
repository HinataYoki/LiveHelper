using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveHelper.Core.Models;
using LiveHelper.Core.Serialization;

namespace LiveHelper.Core.Services;

/// <summary>
/// Records Bilibili live chat as JSON Lines. The legacy Danmu/getConf endpoint is used
/// because the web getDanmuInfo endpoint is frequently blocked by upstream risk control.
/// </summary>
public sealed class BilibiliDanmakuService : IAsyncDisposable
{
    private const int HeaderLength = 16;
    private const int HeartbeatIntervalSeconds = 30;

    private readonly HttpClient _httpClient;

    public BilibiliDanmakuService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public static bool CanRecord(MonitoredRoom room) =>
        room.Platform == LivePlatform.Bilibili
        && !string.IsNullOrWhiteSpace(RecordingFileNaming.ResolveRoomId(room));

    public async Task<BilibiliDanmakuSession?> StartAsync(
        MonitoredRoom room,
        string recordingPath,
        CancellationToken cancellationToken,
        Action<BilibiliDanmakuSession>? created = null)
    {
        if (!CanRecord(room))
        {
            return null;
        }

        var roomId = RecordingFileNaming.ResolveRoomId(room);
        var target = await ResolveTargetAsync(roomId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        var path = RecordingFileNaming.BuildDanmakuPath(recordingPath);
        var session = new BilibiliDanmakuSession(path, roomId);
        created?.Invoke(session);
        session.Start(target, cancellationToken);
        return session;
    }

    public static string? ParseChatMessage(string json)
    {
        var trimmed = json.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!trimmed.StartsWith('{'))
        {
            return null;
        }

        var end = FindJsonObjectEnd(trimmed);
        if (end <= 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed[..end]);
            var root = document.RootElement;
            if (GetString(root, "cmd") != "DANMU_MSG")
            {
                return null;
            }

            if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var items = info.EnumerateArray().ToArray();
            if (items.Length < 3 || items[1].ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var message = items[1].GetString() ?? string.Empty;
            var user = items[2].ValueKind == JsonValueKind.Array && items[2].GetArrayLength() > 1
                ? items[2][1].GetString() ?? string.Empty
                : string.Empty;
            return JsonSerializer.Serialize(
                new BilibiliDanmakuRecord(
                    DateTimeOffset.Now.ToString("O"),
                    user,
                    message),
                BilibiliJsonContext.Default.BilibiliDanmakuRecord);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int FindJsonObjectEnd(string text)
    {
        var depth = 0;
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (current == '\\')
                {
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
            }
            else if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }
        }

        return -1;
    }

    private async Task<DanmakuTarget?> ResolveTargetAsync(
        string roomId,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.live.bilibili.com/room/v1/Danmu/getConf?room_id={Uri.EscapeDataString(roomId)}&platform=pc&player=web";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri($"https://live.bilibili.com/{roomId}");
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var code) && code.TryGetInt32(out var codeValue) && codeValue != 0)
        {
            return null;
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var token = GetString(data, "token");
        if (!data.TryGetProperty("host_server_list", out var hosts) || hosts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var host in hosts.EnumerateArray())
        {
            var name = GetString(host, "host");
            var port = GetInt(host, "wss_port");
            if (!string.IsNullOrWhiteSpace(name) && port is > 0)
            {
                return new DanmakuTarget(name, port.Value, token);
            }
        }

        return null;
    }

    private static ClientWebSocket CreateSocket() =>
        new()
        {
            Options =
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20)
            }
        };

    internal static byte[] BuildAuthPacket(string roomId, string token)
    {
        var payload = JsonSerializer.Serialize(
            new BilibiliAuthPacket(
                0,
                long.TryParse(roomId, out var parsed) ? parsed : 0,
                3,
                "web",
                2,
                token),
            BilibiliJsonContext.Default.BilibiliAuthPacket);
        return BuildPacket(payload, operation: 7);
    }

    internal static byte[] BuildHeartbeatPacket() => BuildPacket("{}", operation: 2);

    internal static byte[] BuildPacket(string payload, int operation, int version = 1)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var packet = new byte[HeaderLength + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, packet.Length);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(4), HeaderLength);
        BinaryPrimitives.WriteInt16BigEndian(packet.AsSpan(6), (short)version);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), operation);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), 1);
        body.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/134.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    internal sealed record DanmakuTarget(string Host, int Port, string Token);

    internal sealed record BilibiliDanmakuRecord(
        [property: JsonPropertyName("Time")] string Time,
        [property: JsonPropertyName("User")] string User,
        [property: JsonPropertyName("Text")] string Text);

    internal sealed record BilibiliAuthPacket(
        [property: JsonPropertyName("uid")] int Uid,
        [property: JsonPropertyName("roomid")] long Roomid,
        [property: JsonPropertyName("protover")] int Protover,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("key")] string Key);

    public sealed class BilibiliDanmakuSession : IAsyncDisposable
    {
        private readonly string _path;
        private readonly string _roomId;
        private readonly CancellationTokenSource _cts = new();
        private Task? _runTask;
        private ClientWebSocket? _socket;
        private int _disposed;

        internal BilibiliDanmakuSession(string path, string roomId)
        {
            _path = path;
            _roomId = roomId;
        }

        public string FilePath => _path;

        public bool HasFaulted { get; private set; }

        public string LastError { get; private set; } = string.Empty;

        internal void Start(DanmakuTarget target, CancellationToken cancellationToken)
        {
            _runTask = RunAsync(target, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                if (_socket is { State: WebSocketState.Open })
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "recording stopped",
                        _cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // The server may have already closed the connection.
            }

            try
            {
                if (_runTask is not null)
                {
                    await _runTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the recording stops.
            }
            catch
            {
                HasFaulted = true;
            }

            _socket?.Dispose();
            _cts.Dispose();
        }

        private async Task RunAsync(DanmakuTarget target, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cts.Token);
            var token = linked.Token;
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory);

            for (var attempt = 0; attempt < 3 && !token.IsCancellationRequested; attempt++)
            {
                using var socket = CreateSocket();
                _socket = socket;
                try
                {
                    await socket.ConnectAsync(
                        new Uri($"wss://{target.Host}/sub"),
                        token).ConfigureAwait(false);
                    await socket.SendAsync(
                        BuildAuthPacket(_roomId, target.Token),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        token).ConfigureAwait(false);

                    Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? AppContext.BaseDirectory);
                    using (File.Open(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                    }

                    await using var output = new StreamWriter(
                        new FileStream(
                            _path,
                            FileMode.Append,
                            FileAccess.Write,
                            FileShare.Read,
                            bufferSize: 4096,
                            useAsync: true),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    await PumpAsync(socket, output, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message;
                    if (attempt == 2)
                    {
                        HasFaulted = true;
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), token)
                        .ConfigureAwait(false);
                }
            }
        }

        private static async Task PumpAsync(
            ClientWebSocket socket,
            StreamWriter output,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[16_384];
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var heartbeatTask = SendHeartbeatsAsync(socket, heartbeat, heartbeatCts.Token);
            try
            {
                using var message = new MemoryStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var packet = message.ToArray();
                    foreach (var text in ExtractChatMessages(packet))
                    {
                        await output.WriteLineAsync(text).ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                heartbeatCts.Cancel();
                try
                {
                    await heartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the recording stops.
                }
            }
        }

        private static async Task SendHeartbeatsAsync(
            ClientWebSocket socket,
            PeriodicTimer timer,
            CancellationToken cancellationToken)
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await socket.SendAsync(
                    BuildHeartbeatPacket(),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Bilibili wraps multiple messages in one WebSocket frame. Protocol 3 packets
        /// are Brotli-compressed and may contain nested packets, so the parser walks
        /// the length-prefixed frames recursively.
        /// </summary>
        internal static IReadOnlyList<string> ExtractChatMessages(byte[] packet)
        {
            var results = new List<string>();
            var span = packet.AsSpan();
            var offset = 0;
            while (offset + HeaderLength <= span.Length)
            {
                var packetLength = BinaryPrimitives.ReadInt32BigEndian(span[offset..]);
                var headerLength = BinaryPrimitives.ReadInt16BigEndian(span[(offset + 4)..]);
                var version = BinaryPrimitives.ReadInt16BigEndian(span[(offset + 6)..]);
                var operation = BinaryPrimitives.ReadInt32BigEndian(span[(offset + 8)..]);
                if (packetLength < headerLength || offset + packetLength > span.Length)
                {
                    break;
                }

                var body = span.Slice(offset + headerLength, packetLength - headerLength);
                if (operation == 5 && version >= 2)
                {
                    try
                    {
                        var decompressed = DecompressBrotli(body);
                        if (decompressed.Length > 0)
                        {
                            results.AddRange(ExtractChatMessages(decompressed));
                        }
                    }
                    catch
                    {
                        // A single malformed packet must not terminate the live session.
                    }
                }
                else if (operation == 5 && (version == 0 || version == 1))
                {
                    var json = Encoding.UTF8.GetString(body);
                    foreach (var message in ExtractJsonObjects(json))
                    {
                        var record = ParseChatMessage(message);
                        if (record is not null)
                        {
                            results.Add(record);
                        }
                    }
                }

                offset += packetLength;
            }

            return results;
        }

        private static IEnumerable<string> ExtractJsonObjects(string text)
        {
            var offset = 0;
            while (offset < text.Length)
            {
                var start = text.IndexOf('{', offset);
                if (start < 0)
                {
                    yield break;
                }

                var end = FindJsonObjectEnd(text[start..]);
                if (end <= 0)
                {
                    yield break;
                }

                yield return text.Substring(start, end);
                offset = start + end;
            }
        }

        private static byte[] DecompressBrotli(ReadOnlySpan<byte> body)
        {
            using var input = new MemoryStream(body.Length);
            input.Write(body);
            input.Position = 0;
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }
    }
}

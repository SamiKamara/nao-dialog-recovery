using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NaoRecovery.Tests;

internal enum Scenario
{
    Healthy, HealthyFinnish, IdleFinnish, StalledEnglish, StalledEnglishChat, StalledFinnish, StalledFinnishChat, Transient, Speaking, Paused,
    LoadedButPaused, UnknownState, ChangedTopics, MixedTopics, MismatchedLanguages, EmptyFinnish, EmptyBoth,
    NoFinnish, Recoverable, MissingEnglish, Busy, WrongService, RestartRefused, Stuck, MissingService, Silent, BadHandshake
}

internal sealed class FakeNao : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task server;
    private readonly Scenario scenario;
    private readonly bool arrayArgs;
    private readonly bool fragmented;
    private bool restarted;
    private int dialogId = 1;
    private int contextReads;
    private int topicReads;
    private string? restoredLanguage;
    public int Port { get; }
    public ConcurrentQueue<string> Mutations { get; } = new();
    public int Heartbeats { get; private set; }

    public FakeNao(Scenario scenario, bool arrayArgs = false, bool fragmented = false)
    {
        this.scenario = scenario;
        this.arrayArgs = arrayArgs;
        this.fragmented = fragmented;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        server = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                await HandleAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
        var line = await reader.ReadLineAsync(lifetime.Token);
        if (line is null) return;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while ((line = await reader.ReadLineAsync(lifetime.Token)) is { Length: > 0 })
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key))
        {
            var body = scenario == Scenario.BadHandshake ? "<html>Not a robot</html>" : "testsession:60:60:websocket";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, lifetime.Token);
            return;
        }
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), lifetime.Token);
        using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(30));
        await SendAsync(socket, "1::");
        await SendAsync(socket, "2::");
        try
        {
            var buffer = new byte[8192];
            while (!lifetime.IsCancellationRequested)
            {
                using var body = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                    if (part.MessageType == WebSocketMessageType.Close) return;
                    body.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                var packet = Encoding.UTF8.GetString(body.ToArray());
                if (packet == "2::") { Heartbeats++; continue; }
                if (!packet.StartsWith("5:::")) continue;
                using var document = JsonDocument.Parse(packet[4..]);
                var request = document.RootElement.GetProperty("args")[0];
                if (scenario == Scenario.Silent) continue;
                var id = request.GetProperty("idm").GetInt32();
                var parameters = request.GetProperty("params");
                var (result, error) = Respond(parameters);
                object args = new { idm = id.ToString(), result };
                if (arrayArgs) args = new[] { args };
                var reply = "5:::" + JsonSerializer.Serialize(new { name = error ? "error" : "reply", args });
                await SendAsync(socket, reply);
            }
        }
        catch (WebSocketException) { }
        catch (IOException) { }
    }

    private (object? Result, bool Error) Respond(JsonElement p)
    {
        var obj = p.GetProperty("obj");
        var member = p.GetProperty("member").GetString();
        var args = p.GetProperty("args");
        if (obj.ValueKind == JsonValueKind.String && obj.GetString() == "ServiceDirectory")
        {
            var name = args[0].GetString();
            if (name == "ALDialog" && scenario == Scenario.MissingService && !restarted)
                return ("Cannot find service 'ALDialog' in index", true);
            return name switch
            {
                "ALDialog" => (new { pyobject = dialogId }, false),
                "ALSpeechRecognition" => (new { pyobject = 2 }, false),
                "ALServiceManager" => (new { pyobject = 3 }, false),
                "ALMemory" => (new { pyobject = 4 }, false),
                _ => ($"Cannot find service '{name}' in index", true)
            };
        }
        var objectId = obj.GetInt32();
        if (objectId == dialogId)
        {
            if (member == "getLanguage") return (restarted ? scenario == Scenario.Stuck ? "" : restoredLanguage ?? "English" : InitialDialogLanguage, false);
            if (member == "getActivatedTopics")
            {
                topicReads++;
                if (scenario == Scenario.Busy) return (new[] { "OtherChat" }, false);
                if (scenario == Scenario.StalledEnglishChat) return (new[] { "EliasChatEN428" }, false);
                if (scenario == Scenario.StalledFinnishChat) return (new[] { "eliasChatFi" }, false);
                if (scenario == Scenario.ChangedTopics && topicReads > 1) return (Array.Empty<string>(), false);
                if (scenario == Scenario.MixedTopics) return (new[] { "DynamicVocabularyFinnish", "OtherChat" }, false);
                return (HasLesson ? new[] { "DynamicVocabulary" + InitialDialogLanguage } : Array.Empty<string>(), false);
            }
            if (member == "setLanguage" && restarted)
            {
                restoredLanguage = args[0].GetString();
                Mutations.Enqueue("language:" + restoredLanguage);
                return (null, false);
            }
        }
        if (objectId == 2)
        {
            if (member == "getAvailableLanguages")
                return (scenario == Scenario.MissingEnglish ? new[] { "Japanese" } : scenario == Scenario.NoFinnish ? new[] { "English" } : new[] { "English", "Finnish", "Japanese" }, false);
            if (member == "getLanguage")
                return (restarted ? restoredLanguage ?? "English" : scenario == Scenario.EmptyBoth ? "" : scenario == Scenario.MismatchedLanguages ? "English" : IsFinnish ? "Finnish" : "English", false);
            if (member == "getContexts")
            {
                contextReads++;
                return (HasLoadedContext ? new[] { "BNFcontent" + InitialDialogLanguage } : Array.Empty<string>(), false);
            }
        }
        if (objectId == 4 && member == "getListData")
        {
            var expectedKeys = new[] { "ALSpeechRecognition/IsRunning", "ALSpeechRecognition/ActiveListening", "ALSpeechRecognition/Status", "ALTextToSpeech/TextStarted" };
            if (!args[0].EnumerateArray().Select(item => item.GetString()).SequenceEqual(expectedKeys))
                return ("Unexpected memory keys", true);
            return (new object?[] { scenario != Scenario.Paused, HasLoadedContext && scenario != Scenario.LoadedButPaused,
                scenario == Scenario.Paused ? "ListenOff" : "ListenOn", scenario == Scenario.UnknownState ? null : scenario == Scenario.Speaking }, false);
        }
        if (objectId == 3)
        {
            if (member == "service")
                return (new { name = "dialog", execStart = scenario == Scenario.WrongService ? "unrelated-service --name OtherService" : "./bin/qilaunch --name ALDialog --legacy dialog --qi-url tcp://127.0.0.1:9559", running = true }, false);
            if (member == "restartService")
            {
                var target = args[0].GetString()!;
                Mutations.Enqueue($"restart:{target}");
                if (scenario == Scenario.RestartRefused) return (false, false);
                restarted = true;
                dialogId = 10;
                return (true, false);
            }
        }
        Mutations.Enqueue($"unexpected:{objectId}:{member}");
        return ("Unexpected method or stale service proxy", true);
    }

    private bool IsFinnish => scenario is Scenario.HealthyFinnish or Scenario.IdleFinnish or Scenario.StalledFinnish or Scenario.StalledFinnishChat
        or Scenario.Transient or Scenario.Speaking or Scenario.Paused or Scenario.LoadedButPaused or Scenario.UnknownState
        or Scenario.ChangedTopics or Scenario.MixedTopics or Scenario.MismatchedLanguages or Scenario.EmptyFinnish or Scenario.NoFinnish;
    private bool HasLesson => scenario is Scenario.Healthy or Scenario.HealthyFinnish or Scenario.StalledEnglish or Scenario.StalledEnglishChat or Scenario.StalledFinnish or Scenario.StalledFinnishChat
        or Scenario.Transient or Scenario.Speaking or Scenario.Paused or Scenario.LoadedButPaused or Scenario.UnknownState
        or Scenario.ChangedTopics or Scenario.MixedTopics or Scenario.MismatchedLanguages or Scenario.NoFinnish;
    private string InitialDialogLanguage => HasLesson || scenario == Scenario.IdleFinnish ? IsFinnish ? "Finnish" : "English" : "";
    private bool HasLoadedContext => scenario is Scenario.Healthy or Scenario.HealthyFinnish or Scenario.LoadedButPaused || scenario == Scenario.Transient && contextReads > 1;

    private async Task SendAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (fragmented && bytes.Length > 10)
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes, 0, 7), WebSocketMessageType.Text, false, lifetime.Token);
            await socket.SendAsync(new ArraySegment<byte>(bytes, 7, bytes.Length - 7), WebSocketMessageType.Text, true, lifetime.Token);
        }
        else await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, lifetime.Token);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        listener.Stop();
        await server;
        lifetime.Dispose();
    }
}

using System.Net.WebSockets;
using System.Text.Json;
using IPadTablet.Backend;

BackendOptions options;
try { options = BackendOptions.Parse(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    Console.Error.WriteLine("Mit --help werden alle Optionen angezeigt.");
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5) });

await using var state = new BackendState(options);
await using var udp = options.Udp ? new UdpBridge(options, state) : null;
await using var usb = options.Usb ? new UsbBridge(options, state) : null;
state.Attach(udp, usb);
udp?.Start();
usb?.Start();
await state.StartAsync();

app.MapGet("/", () => Results.Json(state.Health));
app.MapGet("/health", () => Results.Json(state.Health));

app.Map("/stream", async context =>
{
    if (!Authorized(context, options.Token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var (id, reader) = state.AddClient();
    try
    {
        await foreach (var item in reader.ReadAllAsync(context.RequestAborted))
            await socket.SendAsync(item.Data,
                item.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                true, context.RequestAborted);
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    finally
    {
        state.RemoveClient(id);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }
});

app.Map("/input", async context =>
{
    if (!Authorized(context, options.Token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                await message.WriteAsync(buffer.AsMemory(0, result.Count), context.RequestAborted);
                if (message.Length > 1024 * 1024) throw new JsonException("Input-Nachricht zu groß.");
            } while (!result.EndOfMessage);
            message.Position = 0;
            using var document = await JsonDocument.ParseAsync(message, cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                await state.HandleInputAsync(document.RootElement, context.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    catch (JsonException) { }
});

Console.WriteLine($"iPad Tablet Windows backend: http://{options.Host}:{options.Port}");
Console.WriteLine($"Input: {options.InputMode}; UDP: {options.Udp}; USB: {options.Usb}");
await app.RunAsync();
return 0;

static bool Authorized(HttpContext context, string token) =>
    string.IsNullOrEmpty(token) || string.Equals(context.Request.Query["token"], token, StringComparison.Ordinal);

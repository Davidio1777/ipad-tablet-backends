using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace IPadTablet.Backend;

internal interface IPenSink : IAsyncDisposable
{
    long EventsReceived { get; }
    Task StartAsync(CancellationToken cancellationToken);
    ValueTask ApplyAsync(JsonElement message, CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(CancellationToken cancellationToken = default);
}

internal sealed class NullPenSink : IPenSink
{
    public long EventsReceived => 0;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask ApplyAsync(JsonElement message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class OtdPipePenSink : IPenSink
{
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly object connectionLock = new();
    private NamedPipeServerStream? connection;
    private TaskCompletionSource disconnected = NewSignal();
    private long eventsReceived;
    private long lastSequence = -1;
    private ushort x, y, pressure;
    private sbyte tiltX, tiltY;
    private byte buttons;

    public long EventsReceived => Interlocked.Read(ref eventsReceived);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream("ipad-pencil", PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough);
            Console.WriteLine("OTD: warte auf \\.\\pipe\\ipad-pencil");
            await pipe.WaitForConnectionAsync(cancellationToken);
            lock (connectionLock)
            {
                connection = pipe;
                disconnected = NewSignal();
            }
            Console.WriteLine("OTD: OpenTabletDriver connected");
            try { await disconnected.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            lock (connectionLock) if (ReferenceEquals(connection, pipe)) connection = null;
        }
    }

    public async ValueTask ApplyAsync(JsonElement message, CancellationToken cancellationToken = default)
    {
        if (!message.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString();
        var sequence = message.TryGetProperty("sequence", out var sequenceElement)
            ? sequenceElement.GetInt64() : lastSequence + 1;
        if (sequence <= lastSequence) return;
        lastSequence = sequence;

        if (type == "button")
        {
            var button = Math.Clamp(GetInt(message, "button", 1), 1, 3);
            var mask = (byte)(1 << button);
            if (GetBool(message, "pressed")) buttons |= mask; else buttons &= (byte)~mask;
        }
        else if (type == "pencil")
        {
            var phase = GetString(message, "phase", "move");
            x = (ushort)Math.Round(Math.Clamp(GetDouble(message, "x"), 0, 1) * 32767);
            y = (ushort)Math.Round(Math.Clamp(GetDouble(message, "y"), 0, 1) * 32767);
            pressure = phase is "hover" or "leave" or "up" or "cancel" ? (ushort)0 :
                (ushort)Math.Round(Math.Clamp(GetDouble(message, "pressure"), 0, 1) * 8191);
            var altitude = GetDouble(message, "altitude", Math.PI / 2);
            var azimuth = GetDouble(message, "azimuth");
            var magnitude = Math.Clamp(1 - altitude / (Math.PI / 2), 0, 1);
            tiltX = (sbyte)Math.Round(Math.Sin(azimuth) * magnitude * 90);
            tiltY = (sbyte)Math.Round(-Math.Cos(azimuth) * magnitude * 90);
        }
        else return;

        await EmitAsync(true, cancellationToken);
    }

    public async ValueTask ReleaseAsync(CancellationToken cancellationToken = default)
    {
        pressure = 0;
        buttons = 0;
        await EmitAsync(false, cancellationToken);
    }

    private async ValueTask EmitAsync(bool count, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pipe;
        lock (connectionLock) pipe = connection;
        if (pipe is null || !pipe.IsConnected) return;
        var report = new byte[10];
        report[0] = 1;
        report[1] = buttons;
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(2), x);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(4), y);
        BinaryPrimitives.WriteUInt16LittleEndian(report.AsSpan(6), pressure);
        report[8] = unchecked((byte)tiltX);
        report[9] = unchecked((byte)tiltY);
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await pipe.WriteAsync(report, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
            if (count) Interlocked.Increment(ref eventsReceived);
        }
        catch (IOException) { disconnected.TrySetResult(); }
        finally { writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await writeLock.WaitAsync();
        try { connection?.Dispose(); connection = null; disconnected.TrySetResult(); }
        finally { writeLock.Release(); writeLock.Dispose(); }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static string GetString(JsonElement e, string key, string fallback) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : fallback;
    private static double GetDouble(JsonElement e, string key, double fallback = 0) =>
        e.TryGetProperty(key, out var p) && p.TryGetDouble(out var v) ? v : fallback;
    private static int GetInt(JsonElement e, string key, int fallback) =>
        e.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) ? v : fallback;
    private static bool GetBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
}

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RdpHost;

/// <summary>
/// Named-pipe server the ok-ww Python side connects to. Reads framed JSON requests,
/// dispatches them to <paramref name="handler"/> (which must marshal to the UI thread),
/// and writes back framed JSON responses plus unsolicited events and raw-mouse frames.
///
/// The pipe is opened with FILE_FLAG_OVERLAPPED (PipeOptions.Asynchronous) and all reads
/// and writes go through async I/O. The reader thread blocks in ReadFrameAsync while the
/// UI thread pushes events through SendEventAsync; on a synchronous handle those two would
/// serialize on the handle and deadlock, so overlapped I/O is required on this side too.
/// </summary>
internal sealed class PipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<JsonObject, JsonObject?> _handler;
    private readonly Action<PayloadType, byte[]>? _onBinaryFrame;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ManualResetEventSlim _stopped = new(false);

    private NamedPipeServerStream? _stream;
    private Thread? _thread;
    private volatile bool _running;

    internal event Action? ClientConnected;

    internal PipeServer(string pipeName, Func<JsonObject, JsonObject?> handler,
        Action<PayloadType, byte[]>? onBinaryFrame = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _onBinaryFrame = onBinaryFrame;
    }

    internal void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "rdphost-pipe" };
        _thread.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
                _stream = server;
                server.WaitForConnection();
                ClientConnected?.Invoke();
                ReadLoop(server).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                // Client went away; recreate the pipe and wait for a reconnect.
            }
            finally
            {
                _stream = null;
                try
                {
                    server?.Dispose();
                }
                catch (Exception)
                {
                    // ignore
                }

                if (_running)
                {
                    Thread.Sleep(200);
                }
            }
        }

        _stopped.Set();
    }

    private async Task ReadLoop(Stream stream)
    {
        while (_running)
        {
            var (type, payload) = await Protocol.ReadFrameAsync(stream).ConfigureAwait(false);
            if (payload is null)
            {
                return;
            }

            if (type != PayloadType.Json)
            {
                _onBinaryFrame?.Invoke(type, payload);
                continue;
            }

            await HandleJsonAsync(stream, payload).ConfigureAwait(false);
        }
    }

    private async Task HandleJsonAsync(Stream stream, byte[] payload)
    {
        string id = string.Empty;
        try
        {
            var request = JsonNode.Parse(payload) as JsonObject
                          ?? throw new InvalidOperationException("命名管道 JSON 消息为空。");
            id = request["id"]?.GetValue<string>() ?? string.Empty;
            var data = _handler(request);
            await WriteJsonAsync(stream, new JsonObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["op"] = "response",
                ["ok"] = true,
                ["data"] = data ?? new JsonObject(),
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(stream, new JsonObject
            {
                ["v"] = 1,
                ["id"] = id,
                ["op"] = "response",
                ["ok"] = false,
                ["error"] = ex.GetBaseException().Message,
            }).ConfigureAwait(false);
        }
    }

    internal async Task SendEventAsync(string name, JsonNode? data = null)
    {
        var ev = new JsonObject
        {
            ["v"] = 1,
            ["op"] = "event",
            ["event"] = name,
        };
        if (data is not null)
        {
            ev["data"] = data;
        }

        await SendFrameAsync(PayloadType.Json, Encoding.UTF8.GetBytes(ev.ToJsonString())).ConfigureAwait(false);
    }

    internal async Task SendFrameAsync(PayloadType type, byte[] payload)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Protocol.WriteFrameAsync(stream, type, payload).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Client disconnected mid-write; the read loop will notice and reconnect.
        }
    }

    private async Task WriteJsonAsync(Stream stream, JsonObject obj)
    {
        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Protocol.WriteFrameAsync(stream, PayloadType.Json, Encoding.UTF8.GetBytes(obj.ToJsonString())).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // ignore
        }
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _stream?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        _stopped.Wait(500);
    }
}

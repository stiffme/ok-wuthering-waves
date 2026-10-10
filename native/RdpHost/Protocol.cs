using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RdpHost;

internal enum PayloadType : byte
{
    Json = 1,
    RelativeMouseBatch = 2,
    RelativeMouseResult = 3,
}

/// <summary>
/// Frame codec shared by the helper pipe. Same layout as BetterGenshinImpact's
/// InstanceIpcProtocol: uint32 payloadLength (LE, max 1 MiB) + byte payloadType + payload.
///
/// Reads and writes are asynchronous (overlapped I/O) because the pipe handle is opened
/// with FILE_FLAG_OVERLAPPED. A synchronous blocking read on the reader thread and a
/// synchronous blocking write on the UI thread would deadlock each other (Windows
/// serializes I/O per synchronous handle), so both sides must use overlapped I/O.
/// </summary>
internal static class Protocol
{
    internal const int MaxPayloadLength = 1024 * 1024;
    private const int FrameHeaderLength = sizeof(uint) + sizeof(byte);

    internal static async Task WriteFrameAsync(Stream stream, PayloadType type, ReadOnlyMemory<byte> payload,
        CancellationToken ct = default)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new InvalidDataException($"命名管道消息超过 {MaxPayloadLength} 字节限制。");
        }

        var header = new byte[FrameHeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        header[sizeof(uint)] = (byte)type;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns a frame with a null payload on a clean end-of-stream.</summary>
    internal static async Task<(PayloadType Type, byte[]? Payload)> ReadFrameAsync(Stream stream,
        CancellationToken ct = default)
    {
        var header = new byte[FrameHeaderLength];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
        {
            return (PayloadType.Json, null);
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadLength > MaxPayloadLength)
        {
            throw new InvalidDataException($"命名管道消息超过 {MaxPayloadLength} 字节限制。");
        }

        var payloadType = (PayloadType)header[sizeof(uint)];
        if (!Enum.IsDefined(payloadType))
        {
            throw new InvalidDataException($"未知命名管道载荷类型：{header[sizeof(uint)]}。");
        }

        var payload = new byte[payloadLength];
        if (payload.Length > 0 && !await ReadExactlyOrEofAsync(stream, payload, ct).ConfigureAwait(false))
        {
            return (payloadType, null);
        }

        return (payloadType, payload);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}

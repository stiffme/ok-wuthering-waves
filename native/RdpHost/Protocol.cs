using System;
using System.Buffers.Binary;
using System.IO;

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
/// </summary>
internal static class Protocol
{
    internal const int MaxPayloadLength = 1024 * 1024;
    private const int FrameHeaderLength = sizeof(uint) + sizeof(byte);

    internal static void WriteFrame(Stream stream, PayloadType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new InvalidDataException($"命名管道消息超过 {MaxPayloadLength} 字节限制。");
        }

        Span<byte> header = stackalloc byte[FrameHeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        header[sizeof(uint)] = (byte)type;
        stream.Write(header);
        if (!payload.IsEmpty)
        {
            stream.Write(payload);
        }

        stream.Flush();
    }

    /// <summary>Returns null on a clean end-of-stream.</summary>
    internal static byte[]? ReadFrame(Stream stream, out PayloadType type)
    {
        type = PayloadType.Json;
        var header = new byte[FrameHeaderLength];
        if (!ReadExactlyOrEof(stream, header))
        {
            return null;
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

        type = payloadType;
        var payload = new byte[payloadLength];
        if (payload.Length > 0 && !ReadExactlyOrEof(stream, payload))
        {
            return null;
        }

        return payload;
    }

    private static bool ReadExactlyOrEof(Stream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                return offset == 0;
            }

            offset += read;
        }

        return true;
    }
}

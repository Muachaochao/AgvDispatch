using System.Buffers.Binary;

namespace AgvDispatch.Connectors.Agv;

internal static class SeerProtocolCodec
{
    public const int HeaderLength = 16;
    public const byte Sync = 0x5A;
    public const ushort ResponseOffset = 10000;

    public static byte[] CreateHeader(
        byte version,
        ushort sequence,
        int payloadLength,
        ushort messageType)
    {
        if (payloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
        }

        var header = new byte[HeaderLength];
        header[0] = Sync;
        header[1] = version;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), checked((uint)payloadLength));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8, 2), messageType);
        return header;
    }

    public static SeerFrameHeader ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderLength || header[0] != Sync)
        {
            throw new InvalidDataException("Robokit响应报文头无效或同步字节不是0x5A。");
        }

        return new SeerFrameHeader(
            header[1],
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(2, 2)),
            checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4))),
            BinaryPrimitives.ReadUInt16BigEndian(header.Slice(8, 2)));
    }
}

internal sealed record SeerFrameHeader(
    byte Version,
    ushort Sequence,
    int PayloadLength,
    ushort MessageType);

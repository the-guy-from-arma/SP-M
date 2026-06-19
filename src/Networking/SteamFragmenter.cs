using System;
using System.Collections.Generic;

namespace SeapowerMultiplayer.Transport
{
    internal static class SteamFragmenter
    {
        public const int ChunkPayloadBytes = 32 * 1024;
        public const int HeaderSize = 9;
        public const byte Marker = 0xFF;

        public static List<byte[]> Split(byte[] data, int length, uint fragmentId)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (length < 0 || length > data.Length) throw new ArgumentOutOfRangeException(nameof(length));

            int totalChunks = Math.Max(1, (length + ChunkPayloadBytes - 1) / ChunkPayloadBytes);
            if (totalChunks > ushort.MaxValue)
                throw new InvalidOperationException($"Steam transfer needs too many chunks: {totalChunks}.");

            var chunks = new List<byte[]>(totalChunks);
            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * ChunkPayloadBytes;
                int payloadLength = Math.Min(ChunkPayloadBytes, length - offset);
                var chunk = new byte[HeaderSize + payloadLength];

                chunk[0] = Marker;
                chunk[1] = (byte)(fragmentId & 0xFF);
                chunk[2] = (byte)((fragmentId >> 8) & 0xFF);
                chunk[3] = (byte)((fragmentId >> 16) & 0xFF);
                chunk[4] = (byte)((fragmentId >> 24) & 0xFF);
                chunk[5] = (byte)(i & 0xFF);
                chunk[6] = (byte)((i >> 8) & 0xFF);
                chunk[7] = (byte)(totalChunks & 0xFF);
                chunk[8] = (byte)((totalChunks >> 8) & 0xFF);

                if (payloadLength > 0)
                    Buffer.BlockCopy(data, offset, chunk, HeaderSize, payloadLength);
                chunks.Add(chunk);
            }

            return chunks;
        }

        public static bool TryReadHeader(byte[] data, int length,
            out uint fragmentId, out int chunkIndex, out int totalChunks)
        {
            fragmentId = 0;
            chunkIndex = 0;
            totalChunks = 0;

            if (data == null || length < HeaderSize || data[0] != Marker)
                return false;

            fragmentId = (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24));
            chunkIndex = data[5] | (data[6] << 8);
            totalChunks = data[7] | (data[8] << 8);
            return totalChunks > 0 && chunkIndex >= 0 && chunkIndex < totalChunks;
        }
    }
}

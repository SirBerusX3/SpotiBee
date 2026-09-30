using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpotiBee.Library
{
    /// <summary>
    /// Writes an Ogg Opus file of digital silence with a given duration and Vorbis-comment tags.
    /// Used for Spotify placeholder tracks: MusicBee "plays" the silence as a clock while
    /// Spotify produces the audio. A 4-minute file is about 6 KB plus artwork.
    /// </summary>
    public static class SilentOpusFile
    {
        private const int SampleRate = 48000;           // Opus granule positions are always 48 kHz
        private const ushort PreSkip = 312;             // conventional encoder delay
        private const int SamplesPerPacket = 5760;      // 120 ms, the maximum Opus packet duration

        // TOC byte: config 31 (CELT fullband, 20 ms frames), mono, frame-count code 3.
        // Second byte: 6 frames, CBR, no padding. With no bytes left every frame is
        // zero-length, which decoders treat as DTX and render as silence (RFC 6716 §3.2.5).
        private static readonly byte[] SilencePacket = { 0xF8 | 0x03, 0x06 };

        public sealed class Picture
        {
            public byte[] Data { get; set; }
            public string MimeType { get; set; } = "image/jpeg";
            public int Width { get; set; }
            public int Height { get; set; }
        }

        public static void Write(string path, TimeSpan duration, IEnumerable<KeyValuePair<string, string>> tags, Picture cover = null)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Write(stream, duration, tags, cover);
        }

        public static void Write(Stream output, TimeSpan duration, IEnumerable<KeyValuePair<string, string>> tags, Picture cover = null)
        {
            var totalSamples = Math.Max(SamplesPerPacket, (long)Math.Round(duration.TotalSeconds * SampleRate));
            var writer = new OggWriter(output, serial: unchecked((uint)Environment.TickCount ^ 0x5B0E5B0E));

            writer.WritePacketOnOwnPages(OpusHead(), granule: 0, beginOfStream: true);
            writer.WritePacketOnOwnPages(OpusTags(tags, cover), granule: 0);

            var packetCount = (totalSamples + SamplesPerPacket - 1) / SamplesPerPacket;
            var packets = new List<byte[]>(OggWriter.MaxSegments);
            long samplesWritten = 0;
            for (long i = 0; i < packetCount; i++)
            {
                packets.Add(SilencePacket);
                samplesWritten += SamplesPerPacket;
                var last = i == packetCount - 1;
                // The final packet always gets its own page: a short granule on the *first* audio
                // page means start-trimming to decoders, so the end-trim must be on a later page
                var beforeLast = i == packetCount - 2;
                if (packets.Count == OggWriter.MaxSegments || last || beforeLast)
                {
                    // The final granule trims the last packet so the stream ends at the exact duration
                    var granule = PreSkip + (last ? totalSamples : samplesWritten);
                    writer.WriteSmallPackets(packets, granule, endOfStream: last);
                    packets.Clear();
                }
            }
        }

        private static byte[] OpusHead()
        {
            using var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("OpusHead"));
            w.Write((byte)1);             // version
            w.Write((byte)1);             // channels
            w.Write(PreSkip);
            w.Write((uint)SampleRate);    // original input rate (informational)
            w.Write((short)0);            // output gain
            w.Write((byte)0);             // channel mapping family: mono/stereo
            return ms.ToArray();
        }

        private static byte[] OpusTags(IEnumerable<KeyValuePair<string, string>> tags, Picture cover)
        {
            var comments = new List<byte[]>();
            foreach (var tag in tags)
                if (!string.IsNullOrEmpty(tag.Value))
                    comments.Add(Encoding.UTF8.GetBytes(tag.Key.ToUpperInvariant() + "=" + tag.Value));
            if (cover?.Data != null && cover.Data.Length > 0)
                comments.Add(Encoding.ASCII.GetBytes("METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(FlacPictureBlock(cover))));

            using var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            var vendor = Encoding.UTF8.GetBytes("SpotiBee");
            w.Write(Encoding.ASCII.GetBytes("OpusTags"));
            w.Write((uint)vendor.Length);
            w.Write(vendor);
            w.Write((uint)comments.Count);
            foreach (var comment in comments)
            {
                w.Write((uint)comment.Length);
                w.Write(comment);
            }
            return ms.ToArray();
        }

        // FLAC METADATA_BLOCK_PICTURE layout (all integers big-endian), as used by Vorbis comments
        private static byte[] FlacPictureBlock(Picture cover)
        {
            using var ms = new MemoryStream();
            void U32(uint v)
            {
                ms.WriteByte((byte)(v >> 24));
                ms.WriteByte((byte)(v >> 16));
                ms.WriteByte((byte)(v >> 8));
                ms.WriteByte((byte)v);
            }
            var mime = Encoding.ASCII.GetBytes(cover.MimeType ?? "image/jpeg");
            U32(3);                               // front cover
            U32((uint)mime.Length);
            ms.Write(mime, 0, mime.Length);
            U32(0);                               // description length
            U32((uint)Math.Max(0, cover.Width));
            U32((uint)Math.Max(0, cover.Height));
            U32(24);                              // colour depth
            U32(0);                               // indexed colours
            U32((uint)cover.Data.Length);
            ms.Write(cover.Data, 0, cover.Data.Length);
            return ms.ToArray();
        }

        private sealed class OggWriter
        {
            public const int MaxSegments = 255;

            private static readonly uint[] CrcTable = BuildCrcTable();
            private readonly Stream output;
            private readonly uint serial;
            private uint sequence;

            public OggWriter(Stream output, uint serial)
            {
                this.output = output;
                this.serial = serial;
            }

            /// <summary>Writes one packet of any size, spanning pages as needed; nothing else shares its pages.</summary>
            public void WritePacketOnOwnPages(byte[] packet, long granule, bool beginOfStream = false)
            {
                // Lacing: 255-byte segments, then a final segment < 255 (possibly 0) marks the end
                var segments = new List<int>();
                var remaining = packet.Length;
                while (remaining >= 255)
                {
                    segments.Add(255);
                    remaining -= 255;
                }
                segments.Add(remaining);

                var offset = 0;
                var segIndex = 0;
                var first = true;
                while (segIndex < segments.Count)
                {
                    var count = Math.Min(MaxSegments, segments.Count - segIndex);
                    var table = segments.GetRange(segIndex, count);
                    var bytes = 0;
                    foreach (var s in table)
                        bytes += s;
                    var endsPacket = segIndex + count == segments.Count;

                    byte flags = 0;
                    if (!first) flags |= 0x01;                       // continued packet
                    if (first && beginOfStream) flags |= 0x02;
                    // Pages on which no packet finishes carry granule -1
                    WritePage(flags, endsPacket ? granule : -1, table, packet, offset, bytes);

                    offset += bytes;
                    segIndex += count;
                    first = false;
                }
            }

            /// <summary>Writes packets shorter than 255 bytes, one segment each, on a single page.</summary>
            public void WriteSmallPackets(List<byte[]> packets, long granule, bool endOfStream)
            {
                var table = new List<int>(packets.Count);
                var total = 0;
                foreach (var p in packets)
                {
                    table.Add(p.Length);
                    total += p.Length;
                }
                var body = new byte[total];
                var pos = 0;
                foreach (var p in packets)
                {
                    Buffer.BlockCopy(p, 0, body, pos, p.Length);
                    pos += p.Length;
                }
                WritePage(endOfStream ? (byte)0x04 : (byte)0, granule, table, body, 0, total);
            }

            private void WritePage(byte flags, long granule, List<int> segmentTable, byte[] data, int offset, int length)
            {
                var page = new byte[27 + segmentTable.Count + length];
                Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
                page[4] = 0;                                   // stream structure version
                page[5] = flags;
                WriteLE(page, 6, (ulong)granule, 8);
                WriteLE(page, 14, serial, 4);
                WriteLE(page, 18, sequence++, 4);
                // bytes 22..25: CRC, filled in below
                page[26] = (byte)segmentTable.Count;
                for (var i = 0; i < segmentTable.Count; i++)
                    page[27 + i] = (byte)segmentTable[i];
                Buffer.BlockCopy(data, offset, page, 27 + segmentTable.Count, length);

                WriteLE(page, 22, Crc(page), 4);
                output.Write(page, 0, page.Length);
            }

            private static void WriteLE(byte[] buffer, int offset, ulong value, int bytes)
            {
                for (var i = 0; i < bytes; i++)
                    buffer[offset + i] = (byte)(value >> (8 * i));
            }

            // Ogg CRC-32: polynomial 0x04C11DB7, no reflection, zero initial value and no final XOR
            private static uint Crc(byte[] data)
            {
                uint crc = 0;
                foreach (var b in data)
                    crc = (crc << 8) ^ CrcTable[((crc >> 24) & 0xFF) ^ b];
                return crc;
            }

            private static uint[] BuildCrcTable()
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    var r = i << 24;
                    for (var j = 0; j < 8; j++)
                        r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
                    table[i] = r;
                }
                return table;
            }
        }
    }
}

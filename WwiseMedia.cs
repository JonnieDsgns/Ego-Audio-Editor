using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RegularAudioTool;

/// <summary>Conservative Wwise PCM and packetized Opus replacement. RIFF control chunks are preserved.</summary>
public static class WwiseMedia
{
    public sealed record Info(ushort Codec, int Channels, int SampleRate, long SampleCount,
        int BitsPerSample, bool IsComplete, bool IsPcm, bool CanReplace)
    {
        public TimeSpan Duration => SampleRate > 0 ? TimeSpan.FromSeconds((double)SampleCount / SampleRate) : TimeSpan.Zero;
        public string Description => $"{(IsPcm ? "PCM" : Codec == 0x3041 ? "Wwise Opus" : $"Codec 0x{Codec:X4}")}, {Channels} Kanäle, {SampleRate} Hz, {Duration.TotalSeconds:0.000} s";
    }

    private sealed record Chunk(string Id, byte[] Data, int DeclaredSize);
    private static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
    private static void Put16(byte[] b, int p, int v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), checked((ushort)v));
    private static void Put32(byte[] b, int p, long v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), checked((uint)v));

    private static List<Chunk> ReadChunks(byte[] b)
    {
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a RIFF/WAVE media file.");
        var result = new List<Chunk>();
        int p = 12;
        while (p + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            uint rawSize = U32(b, p + 4);
            if (rawSize > int.MaxValue) throw new InvalidDataException("Invalid chunk size.");
            int size = (int)rawSize;
            int available = Math.Min(size, b.Length - p - 8);
            if (available != size && id != "data") throw new InvalidDataException("Incomplete WAVE header.");
            result.Add(new Chunk(id, b.AsSpan(p + 8, available).ToArray(), size));
            p += 8 + available;
            if (available != size) break;
            if ((size & 1) != 0 && p < b.Length) p++;
        }
        if (p != b.Length) throw new InvalidDataException("Incomplete RIFF chunk.");
        return result;
    }

    private static Chunk Required(List<Chunk> chunks, string id) => chunks.SingleOrDefault(c => c.Id == id)
        ?? throw new InvalidDataException($"Chunk '{id}' is missing.");

    public static Info Inspect(byte[] b)
    {
        var chunks = ReadChunks(b);
        byte[] f = Required(chunks, "fmt ").Data;
        var data = Required(chunks, "data");
        if (f.Length < 16) throw new InvalidDataException("fmt chunk is too short.");
        ushort codec = U16(f, 0);
        int channels = U16(f, 2), rate = checked((int)U32(f, 4)), bits = U16(f, 14), block = U16(f, 12);
        bool pcm = codec == 1 || (codec == 0xfffe && f.Length == 24 && bits == 16);
        bool complete = U32(b, 4) + 8L <= b.Length && data.DeclaredSize == data.Data.Length;
        long samples = pcm && block > 0 ? data.DeclaredSize / block : codec == 0x3041 && f.Length >= 36 ? U32(f, 24) : 0;
        bool opus = codec == 0x3041 && f.Length == 36 && rate == 48000 && U16(f, 18) == 960 && f[34] == 1 && f[35] is 0 or 1;
        if (opus)
        {
            var seek = chunks.SingleOrDefault(c => c.Id == "seek");
            if (seek == null || seek.Data.Length != U32(f, 28) * 2L) complete = false;
            else
            {
                long packetBytes = 0;
                for (int p = 0; p < seek.Data.Length; p += 2) packetBytes += U16(seek.Data, p);
                complete &= packetBytes == data.Data.Length;
            }
        }
        bool replace = complete && channels is 1 or 2 && samples > 0 && ((pcm && bits == 16 && block == channels * 2) || opus);
        return new Info(codec, channels, rate, samples, bits, complete, pcm, replace);
    }

    public static byte[] CreatePcmWave(byte[] b)
    {
        var info = Inspect(b);
        if (!info.IsPcm || !info.IsComplete || info.BitsPerSample != 16)
            throw new InvalidDataException("Only complete PCM16 can be directly exported as WAV.");
        byte[] f = new byte[16];
        Put16(f, 0, 1); Put16(f, 2, info.Channels); Put32(f, 4, info.SampleRate);
        Put32(f, 8, info.SampleRate * info.Channels * 2L); Put16(f, 12, info.Channels * 2); Put16(f, 14, 16);
        return Build(new List<Chunk> { new("fmt ", f, f.Length), Required(ReadChunks(b), "data") });
    }

    public static void ValidateReplacement(byte[] original, byte[] replacement)
    {
        var a = Inspect(original); var b = Inspect(replacement);
        if (!a.CanReplace || !b.IsComplete || a.Codec != b.Codec || a.Channels != b.Channels ||
            a.SampleRate != b.SampleRate || !b.CanReplace)
            throw new InvalidDataException("Replacement WEM must preserve codec, channel count, and sample rate, and contain complete, non-empty audio.");
        var oldChunks = ReadChunks(original); var newChunks = ReadChunks(replacement);
        byte[] oldFmt = Required(oldChunks,"fmt ").Data, newFmt = Required(newChunks,"fmt ").Data;
        if (a.IsPcm && !oldFmt.AsSpan().SequenceEqual(newFmt) || a.Codec == 0x3041 &&
            (oldFmt.Length != newFmt.Length || !oldFmt.AsSpan(18,6).SequenceEqual(newFmt.AsSpan(18,6)) || !oldFmt.AsSpan(34,2).SequenceEqual(newFmt.AsSpan(34,2))))
            throw new InvalidDataException("Replacement WEM changes the channel or codec configuration.");
        UpdateTiming(oldChunks, a.SampleCount, b.SampleCount);
        var controls = oldChunks.Where(c=>c.Id is not ("fmt " or "hash" or "seek" or "data")).ToArray();
        var other = newChunks.Where(c=>c.Id is not ("fmt " or "hash" or "seek" or "data")).ToArray();
        if (controls.Length != other.Length || controls.Where((c,i)=>c.Id != other[i].Id || !c.Data.AsSpan().SequenceEqual(other[i].Data)).Any())
            throw new InvalidDataException("Replacement WEM must preserve control chunks and adjust loop/cue points to the new length. For edited sounds, use WAV re-import.");
    }

    public static async Task<byte[]> EncodeReplacementAsync(byte[] original, string wavPath, string ffmpegPath, CancellationToken ct = default)
    {
        var info = Inspect(original);
        if (!info.CanReplace)
            throw new InvalidDataException(info.Channels > 2 ? "Multichannel WEM can be listened to; WAV re-import is not supported due to the unverified channel mapping." : "This media is incomplete or uses an unsupported format.");
        byte[] wav = await File.ReadAllBytesAsync(wavPath, ct);
        var wc = ReadChunks(wav);
        byte[] wf = Required(wc, "fmt ").Data;
        var wd = Required(wc, "data");
        if (wf.Length < 16 || U16(wf, 0) != 1 || U16(wf, 14) != 16 || U16(wf, 12) != info.Channels * 2)
            throw new InvalidDataException("Replacement WAV must contain uncompressed 16-bit PCM.");
        if (U16(wf, 2) != info.Channels || U32(wf, 4) != info.SampleRate || wd.Data.Length == 0 || wd.Data.Length % (info.Channels * 2) != 0 || wd.DeclaredSize != wd.Data.Length || !Inspect(wav).IsComplete)
            throw new InvalidDataException($"Replacement WAV requires {info.Channels} channels, {info.SampleRate} Hz, and complete, non-empty PCM16 audio. Length may vary; no automatic resampling.");
        long sampleCount = wd.Data.Length / (info.Channels * 2);
        var chunks = ReadChunks(original);
        byte[] encoded;
        if (info.IsPcm) encoded = wd.Data;
        else
        {
            string temp = Path.Combine(Path.GetTempPath(), "EgoOpus-" + Guid.NewGuid().ToString("N") + ".ogg");
            try
            {
                var start = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-i", Path.GetFullPath(wavPath), "-map", "0:a:0", "-c:a", "libopus", "-application", "audio", "-frame_duration", "20", "-vbr", "on", "-b:a", info.Channels == 1 ? "96k" : "160k", "-f", "ogg", temp }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("FFmpeg could not be started.");
                var errorTask = process.StandardError.ReadToEndAsync(ct);
                var outputTask = process.StandardOutput.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct); }
                catch { if (!process.HasExited) process.Kill(true); throw; }
                string error = await errorTask; await outputTask;
                if (process.ExitCode != 0) throw new InvalidDataException("FFmpeg: " + error);
                var packets = ReadOggPackets(await File.ReadAllBytesAsync(temp, ct));
                if (packets.Count < 3 || Encoding.ASCII.GetString(packets[0], 0, 8) != "OpusHead" || packets[0][9] != info.Channels)
                    throw new InvalidDataException("FFmpeg did not produce suitable Opus packets.");
                byte[] fmt = (byte[])Required(chunks, "fmt ").Data.Clone();
                int skip = U16(packets[0], 10);
                int count = packets.Count - 2;
                if ((long)count * 960 < sampleCount + skip) throw new InvalidDataException("Opus packets do not cover the audio length.");
                byte[] seek = new byte[count * 2];
                using var data = new MemoryStream();
                for (int i = 0; i < count; i++) { Put16(seek, i * 2, packets[i + 2].Length); data.Write(packets[i + 2]); }
                encoded = data.ToArray();
                Put32(fmt, 24, sampleCount); Put32(fmt, 28, count); Put16(fmt, 32, skip);
                Put32(fmt, 8, (long)Math.Round(encoded.Length * (double)info.SampleRate / sampleCount));
                Replace(chunks, "fmt ", fmt); Replace(chunks, "seek", seek);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        Replace(chunks, "data", encoded);
        UpdateTiming(chunks, info.SampleCount, sampleCount);
        if (chunks.Any(c => c.Id == "hash" && c.Data.Length == 16)) Replace(chunks, "hash", MD5.HashData(encoded));
        byte[] result = Build(chunks);
        var check = Inspect(result);
        if (!check.IsComplete || check.SampleCount != sampleCount) throw new InvalidDataException("The generated WEM did not pass the structural check.");
        ValidateReplacement(original, result);
        return result;
    }

    // Keep markers at their relative position. RIFF smpl loop ends are inclusive.
    private static void UpdateTiming(List<Chunk> chunks, long oldCount, long newCount)
    {
        if (oldCount == newCount) return;
        long Scale(long value) => Math.Min(newCount - 1, checked((long)Math.Round(value * (double)newCount / oldCount)));
        foreach (var chunk in chunks)
        {
            byte[] d = chunk.Data;
            if (chunk.Id == "fact")
            {
                if (d.Length < 4) throw new InvalidDataException("Invalid fact chunk.");
                Put32(d, 0, newCount);
            }
            else if (chunk.Id == "cue ")
            {
                if (d.Length < 4 || 4L + U32(d, 0) * 24L != d.Length) throw new InvalidDataException("Invalid cue chunk.");
                for (int p = 4; p < d.Length; p += 24)
                {
                    if (!d.AsSpan(p + 8, 4).SequenceEqual("data"u8) || U32(d, p + 12) != 0 || U32(d, p + 16) != 0)
                        throw new InvalidDataException("Cue points with block positions do not yet support length changes.");
                    Put32(d, p + 4, Scale(U32(d, p + 4)));
                    Put32(d, p + 20, Scale(U32(d, p + 20)));
                }
            }
            else if (chunk.Id == "smpl")
            {
                if (d.Length < 36 || 36L + U32(d, 28) * 24L > d.Length) throw new InvalidDataException("Invalid smpl chunk.");
                for (int p = 36, i = 0; i < U32(d, 28); i++, p += 24)
                {
                    long start = Scale(U32(d, p + 8));
                    long end = Math.Clamp(checked((long)Math.Round((U32(d, p + 12) + 1L) * (double)newCount / oldCount)) - 1, start, newCount - 1);
                    Put32(d, p + 8, start); Put32(d, p + 12, end);
                }
            }
        }
    }

    private static void Replace(List<Chunk> chunks, string id, byte[] data)
    {
        int index = chunks.FindIndex(c => c.Id == id);
        if (index < 0) throw new InvalidDataException($"Chunk '{id}' fehlt.");
        chunks[index] = new Chunk(id, data, data.Length);
    }
    private static byte[] Build(List<Chunk> chunks)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream, Encoding.ASCII, true);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(0u); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i]; w.Write(Encoding.ASCII.GetBytes(c.Id)); w.Write((uint)c.Data.Length); w.Write(c.Data);
            // Wwise leaves an odd final data chunk unpadded.
            if ((c.Data.Length & 1) != 0 && i + 1 < chunks.Count) w.Write((byte)0);
        }
        byte[] result = stream.ToArray(); Put32(result, 4, result.Length - 8); return result;
    }
    private static List<byte[]> ReadOggPackets(byte[] bytes)
    {
        var packets = new List<byte[]>(); using var packet = new MemoryStream(); int p = 0;
        while (p < bytes.Length)
        {
            if (p + 27 > bytes.Length || Encoding.ASCII.GetString(bytes, p, 4) != "OggS" || bytes[p + 4] != 0)
                throw new InvalidDataException("Invalid Ogg data stream.");
            int segments = bytes[p + 26], body = p + 27 + segments;
            if (body > bytes.Length) throw new InvalidDataException("Incomplete Ogg page.");
            for (int i = 0; i < segments; i++)
            {
                int length = bytes[p + 27 + i];
                if (body + length > bytes.Length) throw new InvalidDataException("Incomplete Ogg packet.");
                packet.Write(bytes, body, length); body += length;
                if (length < 255) { packets.Add(packet.ToArray()); packet.SetLength(0); }
            }
            p = body;
        }
        if (packet.Length != 0) throw new InvalidDataException("Incomplete last Ogg packet.");
        return packets;
    }
}

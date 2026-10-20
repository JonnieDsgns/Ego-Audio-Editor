using System.Buffers.Binary;
using System.IO;

namespace RegularAudioTool;

/// <summary>Bounded Wwise 150 music edits. Unsupported arrangements fail before saving.</summary>
public static class MusicMetadata
{
    public sealed record Change(uint Id, int OldSize, int NewSize, long OldSamples, long NewSamples, int Rate);
    private sealed record ObjectRow(byte Type, uint Id, int Start, int End);
    private static uint U(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
    private static double D(byte[] b, int p) => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(p, 8));
    private static float F(byte[] b, int p) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(p, 4));
    private static void PutU(byte[] b, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), v);
    private static void PutD(byte[] b, int p, double v) => BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(p, 8), v);
    private static void PutF(byte[] b, int p, float v) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(p, 4), v);
    private static int Count(byte[] b, ref int p, int end, int stride)
    {
        if (p > end - 4) throw new InvalidDataException("Incomplete music control.");
        int n = checked((int)U(b, p)); p += 4;
        if (n > (end - p) / stride) throw new InvalidDataException("Invalid music control.");
        return n;
    }

    public static bool IsMusicBank(AudioBank bank)
    {
        var header = bank.Chunks.Single(c => c.Tag == "BKHD").Data;
        uint hash = 2166136261;
        foreach (byte c in "music"u8) hash = unchecked(hash * 16777619) ^ c;
        return header.Length >= 8 && U(header, 4) == hash;
    }

    public static byte[] Rebuild(byte[] original, IReadOnlyList<Change> changes)
    {
        if (changes.Count == 0) return original;
        var bank = new AudioBank(original);
        if (U(bank.Chunks.Single(c => c.Tag == "BKHD").Data, 0) != 150)
            throw new InvalidDataException("Music control currently only supports Wwise bank version 150.");
        byte[] result = (byte[])original.Clone();
        int chunk = 0, hirc = -1, end = -1;
        while (chunk < result.Length)
        {
            int size = checked((int)U(result, chunk + 4));
            if (result.AsSpan(chunk, 4).SequenceEqual("HIRC"u8)) { hirc = chunk + 8; end = hirc + size; }
            chunk += size + 8;
        }
        if (hirc < 0) throw new InvalidDataException("Music control bank contains no HIRC.");
        int p = hirc;
        int count = Count(result, ref p, end, 9);
        var objects = new Dictionary<uint, ObjectRow>();
        for (int i = 0; i < count; i++)
        {
            if (p > end - 9) throw new InvalidDataException("Incomplete HIRC object.");
            int length = checked((int)U(result, p + 1));
            if (length < 4 || length > end - p - 5) throw new InvalidDataException("Invalid HIRC object size.");
            var row = new ObjectRow(result[p], U(result, p + 5), p + 5, p + 5 + length);
            objects.Add(row.Id, row); p = row.End;
        }
        if (p != end) throw new InvalidDataException("Unexpected data after HIRC objects.");
        var map = changes.ToDictionary(c => c.Id);
        var seen = new HashSet<uint>();
        var scaledSegments = new HashSet<uint>();
        foreach (var row in objects.Values.Where(o => o.Type == 11))
        {
            p = row.Start + 5; // ID and flags
            int sources = Count(result, ref p, row.End, 14);
            var relevant = new List<Change>();
            bool supported = true;
            for (int i = 0; i < sources; i++, p += 14)
            {
                if ((U(result, p) & 15) != 1) { supported = false; break; } // codec source only
                uint id = U(result, p + 5);
                if (!map.TryGetValue(id, out var change)) continue;
                if (U(result, p + 9) != change.OldSize)
                    throw new InvalidDataException($"Music control and original bank entry {id} do not match (file size). Please open the original bank with the corresponding metadata.bnk.");
                PutU(result, p + 9, checked((uint)change.NewSize));
                relevant.Add(change); seen.Add(id);
            }
            if (!supported) continue;
            if (relevant.Count == 0) continue;
            int clips = Count(result, ref p, row.End, 44);
            var factors = new Dictionary<int, double>();
            double oldEnd = 0, newEnd = 0;
            for (int i = 0; i < clips; i++, p += 44)
            {
                if (!map.TryGetValue(U(result, p + 4), out var change) || change.NewSamples == change.OldSamples) continue;
                if (sources != 1 || clips != 1)
                    throw new InvalidDataException("Length changes in multi-part music arrangements require separate handling of the music control.");
                double duration = D(result, p + 36), expected = change.OldSamples * 1000.0 / change.Rate;
                if (!double.IsFinite(duration) || Math.Abs(duration - expected) > 1)
                    throw new InvalidDataException($"Music control and bank entry {change.Id} have different original durations.");
                double factor = (double)change.NewSamples / change.OldSamples;
                factors.Add(i, factor);
                oldEnd = D(result, p + 12) + duration - D(result, p + 20) + D(result, p + 28);
                foreach (int offset in new[] {12, 20, 28, 36}) PutD(result, p + offset, D(result, p + offset) * factor);
                newEnd = oldEnd * factor;
            }
            if (clips > 0) p += 4; // numSubTrack
            int automations = Count(result, ref p, row.End, 12);
            for (int i = 0; i < automations; i++)
            {
                int clipIndex = checked((int)U(result, p)); p += 8;
                int points = Count(result, ref p, row.End, 12);
                for (int j = 0; j < points; j++, p += 12)
                    if (factors.TryGetValue(clipIndex, out double factor)) PutF(result, p, checked((float)(F(result, p) * factor)));
            }
            if (factors.Count == 0) continue;
            // Verified zero-FX/zero-metadata NodeBase prefix. Other layouts are rejected.
            if (p > row.End - 12 || !result.AsSpan(p, 4).SequenceEqual(new byte[4]))
                throw new InvalidDataException("Music track with FX-/metadata overrides requires separate handling.");
            uint parent = U(result, p + 8);
            if (!objects.TryGetValue(parent, out var segment) || segment.Type != 10 || scaledSegments.Contains(parent))
                throw new InvalidDataException("Music segment is missing or used by multiple modified tracks.");
            if (ScaleSegment(result, segment, row.Id, oldEnd, newEnd)) scaledSegments.Add(parent);
        }
        if (map.Keys.Any(id => !seen.Contains(id)))
            throw new InvalidDataException("At least one modified music entry could not be uniquely associated with a supported music control.");
        _ = new AudioBank(result); // bank envelope remains valid
        return result;
    }

    private static bool ScaleSegment(byte[] b, ObjectRow segment, uint track, double oldEnd, double newEnd)
    {
        // The validated single-track suffix is children, meter, zero stingers,
        // duration and null-terminated markers. No unrestricted byte-pattern patches.
        var candidates = new List<(int Duration, List<int> Markers, int Children)>();
        for (int p = segment.Start + 35; p <= segment.End - 12; p++)
        {
            if (U(b, p - 4) != 0) continue;
            int children = 0;
            for (int n = 1; n <= 64 && p - 31 - n * 4 >= segment.Start + 4; n++)
            {
                int start = p - 31 - n * 4;
                if (U(b,start) != n) continue;
                for (int j = 0; j < n; j++)
                    if (U(b,start + 4 + j * 4) == track) children = n;
            }
            if (children == 0) continue;
            double duration = D(b, p);
            if (!double.IsFinite(duration) || oldEnd <= 0 || newEnd <= 0 || Math.Abs(duration - oldEnd) > 1) continue;
            uint count = U(b, p + 8);
            if (count < 2 || count > 128) continue;
            int q = p + 12; var markers = new List<int>(); bool valid = true;
            for (int i = 0; i < count; i++)
            {
                if (q > segment.End - 13) { valid = false; break; }
                double position = D(b, q + 4);
                if (!double.IsFinite(position) || position < 0 || position > duration + 1) { valid = false; break; }
                markers.Add(q + 4); q += 12;
                int terminator = Array.IndexOf(b, (byte)0, q, segment.End - q);
                if (terminator < 0) { valid = false; break; }
                q = terminator + 1;
            }
            if (valid && q == segment.End) candidates.Add((p, markers, children));
        }
        if (candidates.Count != 1) throw new InvalidDataException("Music segment is not a uniquely supported single-track segment; control bank is not modified.");
        var chosen = candidates[0];
        // Layered music retains the shared timeline: resizing one stem must not
        // move cues or change playback of its untouched siblings.
        if (chosen.Children > 1) return false;
        double factor = newEnd / oldEnd;
        PutD(b, chosen.Duration, newEnd);
        foreach (int marker in chosen.Markers) PutD(b, marker, D(b, marker) * factor);
        return true;
    }
}

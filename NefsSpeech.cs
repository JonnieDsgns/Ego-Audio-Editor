using System.IO;
using System.IO.Abstractions;
using System.Security.Cryptography;
using VictorBush.Ego.NefsLib;
using VictorBush.Ego.NefsLib.DataSource;
using VictorBush.Ego.NefsLib.IO;
using VictorBush.Ego.NefsLib.Item;
using VictorBush.Ego.NefsLib.Progress;

namespace RegularAudioTool;

public sealed record NefsSpeechEntry(uint Id, string Name, uint Size);

public static class NefsSpeech
{
    public static NefsSpeechArchive Read(string path) => new(Path.GetFullPath(path));
}

/// <summary>NeFS 1.6 speech archives. Never changes the loaded archive or its source file.</summary>
public sealed class NefsSpeechArchive
{
    private readonly FileSystem fs = new();
    private readonly NefsArchive archive;
    public string SourcePath { get; }
    public IReadOnlyList<NefsSpeechEntry> Entries { get; }

    internal NefsSpeechArchive(string path)
    {
        SourcePath = path;
        ValidateHeaderHash(path);
        archive = new NefsReader(fs).ReadArchiveAsync(path, new NefsProgress()).GetAwaiter().GetResult();
        if (archive.Header.Version.ToString() != "Version160" || archive.Header.IsEncrypted ||
            archive.Items.Volumes.Count != 1)
            throw new NotSupportedException("Only unencrypted NeFS-1.6 speech archives with a single data volume are supported.");
        Entries = archive.Items.EnumerateById().Where(x => x.Type == NefsItemType.File)
            .Select(x => new NefsSpeechEntry(x.Id.Value, archive.Items.GetItemFilePath(x.Id), x.ExtractedSize)).ToArray();
    }

    private static void ValidateHeaderHash(string path)
    {
        using var file = File.OpenRead(path);
        var intro = new byte[132];
        file.ReadExactly(intro);
        var size = BitConverter.ToUInt32(intro, 100);
        if (!intro.AsSpan(0,4).SequenceEqual("NeFS"u8) || BitConverter.ToUInt32(intro,104) != 0x10600 ||
            size < 256 || size > 64 * 1024 * 1024 || size > file.Length)
            throw new NotSupportedException("Not a supported NeFS-1.6 speech archive.");
        var header = new byte[checked((int)size)];
        file.Position = 0;
        file.ReadExactly(header);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header.AsSpan(0,4));
        hash.AppendData(header.AsSpan(36));
        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(header.AsSpan(4,32)))
            throw new InvalidDataException("The NeFS header checksum is invalid; saving has been blocked to protect the data.");
    }

    public byte[] Extract(NefsSpeechEntry entry)
    {
        var known = Entries.SingleOrDefault(x => x.Id == entry.Id);
        if (known != entry) throw new ArgumentException("Entry does not belong to this archive.", nameof(entry));
        return ExtractItem(archive.Items.GetItem(new NefsItemId(entry.Id)));
    }

    private byte[] ExtractItem(NefsItem item)
    {
        using var input = item.DataSource.OpenRead(fs);
        using var output = new MemoryStream();
        new NefsTransformer(fs).DetransformAsync(input, item.DataSource.Offset, output, 0,
            item.ExtractedSize, item.DataSource.Size.Chunks, new NefsProgress()).GetAwaiter().GetResult();
        if (output.Length != item.ExtractedSize) throw new InvalidDataException("NeFS extraction has an unexpected length.");
        return output.ToArray();
    }

    public void SaveAs(string path, IReadOnlyDictionary<uint, byte[]> replacements)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Please save a new archive copy; the opened original remains unchanged.");
        foreach (var pair in replacements)
        {
            if (!Entries.Any(x => x.Id == pair.Key)) throw new ArgumentException($"Unknown NeFS ID {pair.Key}.");
            if (pair.Value is null || pair.Value.Length == 0) throw new ArgumentException("A replacement file cannot be empty.");
        }
        // Use a private directory so the writer's volume copies cannot overwrite siblings.
        var staging = Path.Combine(Path.GetTempPath(), "EgoAudioNeFS", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var clone = new NefsArchive(archive.Header, (NefsItemList)archive.Items.Clone());
            foreach (var pair in replacements)
            {
                var replacementPath = Path.Combine(staging, pair.Key + ".replacement");
                File.WriteAllBytes(replacementPath, pair.Value);
                clone.Items.GetItem(new NefsItemId(pair.Key)).UpdateDataSource(
                    new NefsFileDataSource(replacementPath, 0, new NefsItemSize(checked((uint)pair.Value.Length)), false),
                    NefsItemState.Replaced);
            }
            var stagedPath = Path.Combine(staging, Path.GetFileName(path));
            new NefsWriter(Path.Combine(staging, "work"), fs, new NefsTransformer(fs))
                .WriteArchiveAsync(stagedPath, clone, new NefsProgress()).GetAwaiter().GetResult();
            // Reopen and compare every file, including all untouched siblings, before committing output.
            var verified = NefsSpeech.Read(stagedPath);
            if (!Entries.Select(x => (x.Id, x.Name)).SequenceEqual(verified.Entries.Select(x => (x.Id, x.Name))))
                throw new InvalidDataException("NeFS entry names or IDs have changed during saving.");
            foreach (var entry in Entries)
            {
                var expected = replacements.TryGetValue(entry.Id, out var replacement) ? replacement : Extract(entry);
                var actual = verified.Extract(verified.Entries.Single(x => x.Id == entry.Id));
                if (!SHA256.HashData(expected).AsSpan().SequenceEqual(SHA256.HashData(actual)))
                    throw new InvalidDataException($"NeFS verification for ID {entry.Id} failed.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(stagedPath, path, false);
        }
        finally
        {
            // Only this generated per-operation directory is removed.
            Directory.Delete(staging, true);
        }
    }
}

using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace RegularAudioTool;

public sealed class AudioEntry
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public uint Id { get; init; }
    public string DisplayName => ArchiveId != null && BankKey == null ? Path.GetFileNameWithoutExtension(Name.Replace('\\', '/')) : Name;
    public bool MatchesSearch(string query) => Name.Contains(query,StringComparison.OrdinalIgnoreCase) || DisplayName.Contains(query,StringComparison.OrdinalIgnoreCase) || Id.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(query,StringComparison.Ordinal) || Format.Contains(query,StringComparison.OrdinalIgnoreCase);
    public required byte[] Embedded { get; init; }
    public byte[]? Full { get; init; }
    public string? ExternalPath { get; init; }
    public string? BankKey { get; init; }
    public uint? ArchiveId { get; init; }
    private byte[]? replacement;
    private WwiseMedia.Info? replacementInfo, originalInfo;
    public byte[]? Replacement
    {
        get => replacement;
        set { var parsed = value == null ? null : WwiseMedia.Inspect(value); replacement = value; replacementInfo = parsed; }
    }
    public bool IsModified => Replacement != null;
    public byte[] Current => Replacement ?? Full ?? Embedded;
    public bool IsPrefetch => Full != null || Embedded.Length >= 12 && Embedded.AsSpan(0,4).SequenceEqual("RIFF"u8) && BinaryPrimitives.ReadUInt32LittleEndian(Embedded.AsSpan(4)) + 8L > Embedded.Length;
    public string State => Replacement != null ? "Replaced" : IsPrefetch ? Full != null ? "Stream + Bank" : "Stream missing" : "Original";
    public WwiseMedia.Info? Info { get => replacementInfo ?? originalInfo; init => originalInfo = value; }
    public string Format => Info?.Description ?? "Plugin / unknown";
    public string Duration => Info is { } i ? i.Duration.ToString(@"mm\:ss\.fff") : "—";
    public string Size => $"{Current.Length / 1024.0:N1} KB";
    public bool CanPlay => Info is { IsComplete: true };
    public bool CanReplace => CanPlay && Info?.CanReplace == true;
}

public sealed class AudioBank
{
    public byte[] Original { get; }
    public List<(string Tag, byte[] Data)> Chunks { get; } = [];
    public List<(uint Id, int Offset, int Size)> Media { get; } = [];
    public AudioBank(byte[] data)
    {
        Original = data;
        int cursor = 0;
        while (cursor < data.Length)
        {
            if (cursor > data.Length - 8) throw new InvalidDataException("Incomplete bank header.");
            string tag = Encoding.ASCII.GetString(data, cursor, 4);
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4)));
            if (size < 0 || size > data.Length - cursor - 8) throw new InvalidDataException("Bank chunk out of file bounds.");
            Chunks.Add((tag, data.AsSpan(cursor + 8, size).ToArray()));
            cursor += 8 + size;
        }
        if (Chunks.Count == 0 || Chunks[0].Tag != "BKHD") throw new InvalidDataException("Not a Wwise bank.");
        var didx = Chunks.FirstOrDefault(x => x.Tag == "DIDX").Data;
        var audio = Chunks.FirstOrDefault(x => x.Tag == "DATA").Data;
        if (didx == null) return;
        if (audio == null || didx.Length % 12 != 0) throw new InvalidDataException("Invalid media index.");
        for (int i = 0; i < didx.Length; i += 12)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(didx.AsSpan(i));
            int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(didx.AsSpan(i + 4)));
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(didx.AsSpan(i + 8)));
            if (offset > audio.Length - size) throw new InvalidDataException("Media entry out of DATA.");
            Media.Add((id, offset, size));
        }
        if (Media.Select(x=>x.Id).Distinct().Count() != Media.Count) throw new InvalidDataException("Duplicate media IDs.");
    }
    public byte[] Extract(uint id)
    {
        var m = Media.Single(x => x.Id == id);
        return Chunks.Single(x => x.Tag == "DATA").Data.AsSpan(m.Offset,m.Size).ToArray();
    }
    public byte[] Rebuild(IReadOnlyDictionary<uint,byte[]> replacements)
    {
        if (replacements.Count == 0) return Original;
        if (replacements.Keys.Any(id=>Media.All(m=>m.Id != id))) throw new InvalidDataException("Unknown media ID.");
        using var audio = new MemoryStream();
        using var index = new MemoryStream();
        using var ix = new BinaryWriter(index, Encoding.UTF8, true);
        foreach (var m in Media)
        {
            while (audio.Position % 16 != 0) audio.WriteByte(0);
            byte[] bytes = replacements.TryGetValue(m.Id,out var replacement) ? replacement : Extract(m.Id);
            ix.Write(m.Id); ix.Write(checked((uint)audio.Position)); ix.Write(checked((uint)bytes.Length));
            audio.Write(bytes);
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        foreach (var c in Chunks)
        {
            byte[] bytes = c.Tag == "DIDX" ? index.ToArray() : c.Tag == "DATA" ? audio.ToArray() : c.Data;
            writer.Write(Encoding.ASCII.GetBytes(c.Tag)); writer.Write(checked((uint)bytes.Length)); writer.Write(bytes);
        }
        byte[] result = output.ToArray();
        var verify = new AudioBank(result);
        foreach (var m in Media)
            if (!verify.Extract(m.Id).AsSpan().SequenceEqual(replacements.TryGetValue(m.Id,out var r) ? r : Extract(m.Id)))
                throw new InvalidDataException("Bank verification failed.");
        return result;
    }
}

public sealed class AudioDocument
{
    public string SourcePath { get; }
    public byte[]? Original { get; private set; }
    public NefsSpeechArchive? Archive { get; private set; }
    public Dictionary<string,AudioBank> Banks { get; } = [];
    public List<AudioEntry> Entries { get; } = [];
    public bool HasChanges => Entries.Any(x=>x.Replacement != null);
    private AudioDocument(string path) { SourcePath = Path.GetFullPath(path); }
    public static AudioDocument Read(string path)
    {
        var doc = new AudioDocument(path);
        if (Path.GetExtension(path).Equals(".nfs",StringComparison.OrdinalIgnoreCase))
        {
            var archive = NefsSpeech.Read(path);
            doc.Archive = archive;
            foreach (var item in archive.Entries)
            {
                byte[] bytes = archive.Extract(item);
                if (bytes.AsSpan().StartsWith("BKHD"u8)) doc.AddBank(item.Name,bytes,item.Id);
                else if (bytes.AsSpan().StartsWith("RIFF"u8) || bytes.AsSpan().StartsWith("PLUG"u8))
                    doc.AddEntry(item.Name,item.Name,item.Id,bytes,null,item.Id);
            }
        }
        else
        {
            byte[] bytes = File.ReadAllBytes(path);
            doc.Original = bytes;
            if (bytes.AsSpan().StartsWith("BKHD"u8)) doc.AddBank(Path.GetFileName(path),bytes,null);
            else doc.AddEntry(Path.GetFileName(path),Path.GetFileName(path),uint.TryParse(Path.GetFileNameWithoutExtension(path),out var id) ? id : 0,bytes,null,null);
        }
        return doc;
    }
    private void AddBank(string key,byte[] bytes,uint? archiveId)
    {
        var bank = new AudioBank(bytes); Banks.Add(key,bank);
        foreach (var media in bank.Media)
            AddEntry(key + "/" + media.Id,key + " / " + media.Id,media.Id,bank.Extract(media.Id),key,archiveId);
    }
    private void AddEntry(string key,string name,uint id,byte[] bytes,string? bankKey,uint? archiveId)
    {
        byte[]? full = null; string? external = null;
        bool prefetch = bytes.Length >= 12 && bytes.AsSpan(0,4).SequenceEqual("RIFF"u8) && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) + 8L > bytes.Length;
        if (prefetch)
        {
            string dir = Path.GetDirectoryName(SourcePath)!;
            foreach (string candidate in new[]{Path.Combine(dir,"streams",$"{id}.wem"),Path.Combine(dir,$"{id}.wem"),Path.Combine(dir,"..","soundbanks","streams",$"{id}.wem")})
                if (File.Exists(candidate))
                {
                    byte[] stream = File.ReadAllBytes(candidate);
                    if (!stream.AsSpan().StartsWith(bytes)) throw new InvalidDataException($"Stream {id} does not match the bank prefix.");
                    full = stream; external = Path.GetFullPath(candidate); break;
                }
        }
        WwiseMedia.Info? info = null;
        try { info = WwiseMedia.Inspect(full ?? bytes); } catch (InvalidDataException) { }
        Entries.Add(new AudioEntry {Key=key,Name=name,Id=id,Embedded=bytes,Full=full,ExternalPath=external,BankKey=bankKey,ArchiveId=archiveId,Info=info});
    }
    public void SaveAs(string path)
    {
        path = Path.GetFullPath(path);
        if (string.Equals(path,SourcePath,StringComparison.OrdinalIgnoreCase)) throw new IOException("Please choose a new output file; the original will be preserved.");
        var outputs = new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        var archiveReplacements = new Dictionary<uint,byte[]>();
        var musicChanges = new List<MusicMetadata.Change>();
        // Include unchanged companion streams so the saved bank is usable on its own.
        foreach (var entry in Entries.Where(e=>e.Full != null && e.Replacement == null))
        {
            string streamPath = Path.Combine(Path.GetDirectoryName(path)!,"streams",$"{entry.Id}.wem");
            if (string.Equals(streamPath,entry.ExternalPath,StringComparison.OrdinalIgnoreCase))
                throw new IOException("Please save the bank copy in a different folder to preserve the original streams.");
            outputs.TryAdd(streamPath,entry.Full!);
        }
        foreach (var (key,bank) in Banks)
        {
            var replacements = new Dictionary<uint,byte[]>();
            foreach (var entry in Entries.Where(e=>e.BankKey == key && e.Replacement != null))
            {
                byte[] replacement = entry.Replacement!;
                if (entry.Full != null)
                {
                    // Retain a matching prefix; the RIFF header still declares full length.
                    int oldDataStart = FindRiffData(entry.Embedded);
                    int newDataStart = FindRiffData(replacement);
                    // A shorter replacement may need a smaller prefetch; keep it truncated
                    // so reopening still resolves the full companion stream.
                    int prefixLength = Math.Min(replacement.Length - 1,checked(newDataStart + entry.Embedded.Length - oldDataStart));
                    if (prefixLength < newDataStart) throw new InvalidDataException("The new stream does not contain any prefetch audio data.");
                    replacements[entry.Id] = replacement[..prefixLength];
                    string streamPath = Path.Combine(Path.GetDirectoryName(path)!,"streams",$"{entry.Id}.wem");
                    if (string.Equals(streamPath,entry.ExternalPath,StringComparison.OrdinalIgnoreCase)) throw new IOException("The output would overwrite the original stream. Please choose a different folder.");
                    outputs.Add(streamPath,replacement);
                }
                else replacements[entry.Id] = replacement;
                if (MusicMetadata.IsMusicBank(bank))
                {
                    var before = WwiseMedia.Inspect(entry.Full ?? entry.Embedded);
                    var after = WwiseMedia.Inspect(replacement);
                    musicChanges.Add(new(entry.Id,entry.Embedded.Length,replacements[entry.Id].Length,before.SampleCount,after.SampleCount,before.SampleRate));
                }
            }
            byte[] rebuilt = bank.Rebuild(replacements);
            if (Archive != null)
            {
                uint member = Entries.First(e=>e.BankKey==key).ArchiveId!.Value;
                if (replacements.Count > 0) archiveReplacements[member] = rebuilt;
            }
            else outputs.Add(path,rebuilt);
        }
        foreach (var entry in Entries.Where(e=>e.BankKey == null && e.Replacement != null))
            if (Archive != null) archiveReplacements[entry.ArchiveId!.Value] = entry.Replacement!;
            else outputs.Add(path,entry.Replacement!);
        if (Archive == null && outputs.Count == 0) outputs.Add(path,Original!);
        if (musicChanges.Count > 0)
        {
            string sourceMetadata = Path.Combine(Path.GetDirectoryName(SourcePath)!,"metadata.bnk");
            string outputMetadata = Path.Combine(Path.GetDirectoryName(path)!,"metadata.bnk");
            if (!File.Exists(sourceMetadata)) throw new FileNotFoundException("Music output requires the metadata.bnk matching the original to be next to the opened bank.",sourceMetadata);
            if (string.Equals(sourceMetadata,outputMetadata,StringComparison.OrdinalIgnoreCase)) throw new IOException("Please save the music copy in a new folder; the original metadata.bnk will be preserved.");
            outputs.Add(outputMetadata,MusicMetadata.Rebuild(File.ReadAllBytes(sourceMetadata),musicChanges));
        }
        if (File.Exists(path) || outputs.Keys.Any(File.Exists)) throw new IOException("An output file already exists. Please choose a new name or folder.");
        if (Archive != null) Archive.SaveAs(path,archiveReplacements);
        foreach (var (output,bytes) in outputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var f = new FileStream(output,FileMode.CreateNew,FileAccess.Write); f.Write(bytes);
        }
    }
    private static int FindRiffData(byte[] bytes)
    {
        int pos = 12;
        while (pos <= bytes.Length - 8)
        {
            if (bytes.AsSpan(pos,4).SequenceEqual("data"u8)) return pos + 8;
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos+4)));
            pos = checked(pos + 8 + count + count % 2);
        }
        throw new InvalidDataException("Stream does not contain a data chunk.");
    }
}

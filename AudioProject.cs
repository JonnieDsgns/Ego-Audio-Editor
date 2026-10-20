using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace RegularAudioTool;

public sealed record AudioModDetails(string Name, string Author, string Version);
public sealed record AudioProjectInfo(int FormatVersion, string GamePath, AudioModDetails Mod, Dictionary<string,string> Replacements);
public sealed record OpenedAudioProject(AudioDocument Document, AudioProjectInfo Info);

public static class AudioProject
{
    public static void Save(AudioDocument document, string output, string gamePath, AudioModDetails details)
    {
        if(File.Exists(output))throw new IOException("The project already exists. Please choose a new name.");
        string staging=Path.Combine(Path.GetTempPath(),"EgoAudioProjects",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string content=Path.Combine(staging,"content");
            string source=SafePath(content,"original/"+gamePath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.Copy(document.SourcePath,source);
            foreach(var entry in document.Entries.Where(e=>e.ExternalPath!=null))
            {
                string relative=Path.GetRelativePath(Path.GetDirectoryName(document.SourcePath)!,entry.ExternalPath!).Replace('\\','/');
                string destination=SafePath(content,"original/"+Path.GetDirectoryName(gamePath)!.Replace('\\','/')+"/"+relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if(!File.Exists(destination))File.Copy(entry.ExternalPath!,destination);
            }
            string metadata=Path.Combine(Path.GetDirectoryName(document.SourcePath)!,"metadata.bnk");
            if(document.Banks.Values.Any(MusicMetadata.IsMusicBank) && File.Exists(metadata))
                File.Copy(metadata,Path.Combine(Path.GetDirectoryName(source)!,"metadata.bnk"),true);
            var replacements=new Dictionary<string,string>();
            foreach(var entry in document.Entries.Where(e=>e.Replacement!=null))
            {
                string relative=$"replacements/{replacements.Count}.wem";
                string destination=SafePath(content,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination,entry.Replacement!);
                replacements.Add(entry.Key,relative);
            }
            File.WriteAllText(Path.Combine(content,"project.json"),JsonSerializer.Serialize(new AudioProjectInfo(1,gamePath,details,replacements),new JsonSerializerOptions{WriteIndented=true}));
            string zip=Path.Combine(staging,"project.zip");
            ZipFile.CreateFromDirectory(content,zip);
            File.Copy(zip,output,false);
        }
        finally{Directory.Delete(staging,true);}
    }

    public static OpenedAudioProject Open(string path,string extractionFolder)
    {
        if(Directory.Exists(extractionFolder))throw new IOException("Project extraction folder already exists.");
        Directory.CreateDirectory(extractionFolder);
        try
        {
            ZipFile.ExtractToDirectory(path,extractionFolder);
            var info=JsonSerializer.Deserialize<AudioProjectInfo>(File.ReadAllText(Path.Combine(extractionFolder,"project.json")))??throw new InvalidDataException("Invalid audio project.");
            if(info.FormatVersion!=1 || info.Mod==null || info.Replacements==null || !info.GamePath.StartsWith("audio/",StringComparison.Ordinal))throw new InvalidDataException("Unsupported audio project.");
            string source=SafePath(extractionFolder,"original/"+info.GamePath);
            var document=AudioDocument.Read(source);
            foreach(var pair in info.Replacements)
            {
                var entry=document.Entries.SingleOrDefault(e=>e.Key==pair.Key)??throw new InvalidDataException("Unknown project sound.");
                byte[] replacement=File.ReadAllBytes(SafePath(extractionFolder,pair.Value));
                WwiseMedia.ValidateReplacement(entry.Full??entry.Embedded,replacement);
                entry.Replacement=replacement;
            }
            return new(document,info);
        }
        catch{Directory.Delete(extractionFolder,true);throw;}
    }

    private static string SafePath(string root,string relative)
    {
        string prefix=Path.GetFullPath(root)+Path.DirectorySeparatorChar;
        string result=Path.GetFullPath(Path.Combine(root,relative));
        if(!result.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid project path.");
        return result;
    }
}

using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace RegularAudioTool;

public static class AudioModExporter
{
    // Preserve the path below audio, including the year and language folders.
    public static string? FindGamePath(string path)
    {
        path = Path.GetFullPath(path);
        for (var folder = new DirectoryInfo(Path.GetDirectoryName(path)!); folder != null; folder = folder.Parent)
            if (folder.Name.Equals("audio", StringComparison.OrdinalIgnoreCase))
                return "audio/" + Path.GetRelativePath(folder.FullName, path).Replace('\\', '/');
        return null;
    }

    public static void Export(AudioDocument document, string output, string gamePath, AudioModDetails? details = null)
    {
        var parts = gamePath.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || parts[0] != "audio" || parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Select a destination inside the game's audio folder.");
        if (!Path.GetFileName(gamePath).Equals(Path.GetFileName(document.SourcePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The mod must preserve the original audio filename.");

        details ??= new AudioModDetails(Path.GetFileNameWithoutExtension(output), "", "1.0");
        output = Path.GetFullPath(output);
        string projectOutput = Path.ChangeExtension(output, ".egoaudio");
        if (File.Exists(projectOutput)) throw new IOException("The project file already exists. Please choose a new name.");
        if (File.Exists(output)) throw new IOException("The mod file already exists. Please choose a new name.");
        string staging = Path.Combine(Path.GetTempPath(), "EgoAudioMods", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string content = Path.Combine(staging, "package");
            string audioPath = Path.Combine(content, "F1 25", Path.Combine(parts));
            Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
            document.SaveAs(audioPath);
            File.WriteAllText(Path.Combine(content, "mod.serps"), JsonSerializer.Serialize(new
            {
                mod_name = details.Name,
                author = details.Author,
                version = details.Version,
                game = "F1 25"
            }, new JsonSerializerOptions { WriteIndented = true }));
            string archive = Path.Combine(staging, "mod.zip");
            ZipFile.CreateFromDirectory(content, archive, CompressionLevel.Optimal, false);
            // Commit only a completed archive, without replacing any existing file.
            string project = Path.Combine(staging, "project.egoaudio");
            AudioProject.Save(document, project, gamePath, details);
            File.Copy(archive, output, false);
            try { File.Copy(project, projectOutput, false); }
            catch { File.Delete(output); throw; }
        }
        finally
        {
            Directory.Delete(staging, true);
        }
    }
}

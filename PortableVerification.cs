using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RegularAudioTool;

/// <summary>Headless verification of the shipped single-file executable.</summary>
internal static class PortableVerification
{
    public static async Task<int> Run(string output,string opusFixture)
    {
        Directory.CreateDirectory(output);
        string report=Path.Combine(output,"portable-verification.json");
        try
        {
            string root=PortableDependencies.DirectoryPath;
            string encoder=Path.Combine(root,"vendor/ffmpeg/ffmpeg.exe");
            string decoder=Path.Combine(root,"vendor/vgmstream/vgmstream-cli.exe");
            _=Assembly.Load("VictorBush.Ego.NefsLib").GetTypes();
            _=Assembly.Load("Testably.Abstractions.FileSystem.Interface").GetTypes();
            var window=new MainWindow();
            var content=(System.Windows.FrameworkElement)window.Content;
            content.Measure(new System.Windows.Size(1192,740));content.Arrange(new System.Windows.Rect(0,0,1192,740));content.UpdateLayout();
            window.Close();
            string wave=Path.Combine(output,"input.wav");
            var info=WwiseMedia.Inspect(await File.ReadAllBytesAsync(opusFixture));
            using(var stream=File.Create(wave))using(var w=new BinaryWriter(stream))
            {
                int frames=4800,bytes=frames*info.Channels*2;
                w.Write("RIFF"u8);w.Write(36+bytes);w.Write("WAVEfmt "u8);w.Write(16);w.Write((ushort)1);w.Write((ushort)info.Channels);
                w.Write(info.SampleRate);w.Write(info.SampleRate*info.Channels*2);w.Write((ushort)(info.Channels*2));w.Write((ushort)16);w.Write("data"u8);w.Write(bytes);
                for(int i=0;i<frames;i++)for(int c=0;c<info.Channels;c++)w.Write((short)(8000*Math.Sin(i*2*Math.PI*440/info.SampleRate)));
            }
            byte[] encoded=await WwiseMedia.EncodeReplacementAsync(await File.ReadAllBytesAsync(opusFixture),wave,encoder);
            string wem=Path.Combine(output,"encoded.wem"),decoded=Path.Combine(output,"decoded.wav");
            await File.WriteAllBytesAsync(wem,encoded);
            var start=new ProcessStartInfo(decoder){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{"-i","-o",decoded,wem})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new IOException("Bundled decoder did not start.");
            var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();await stdout;string error=await stderr;
            if(process.ExitCode!=0)throw new IOException(error);
            var result=WwiseMedia.Inspect(await File.ReadAllBytesAsync(decoded));
            if(result.SampleCount!=4800 || result.Channels!=info.Channels || result.SampleRate!=info.SampleRate)throw new IOException("Portable codec roundtrip failed.");
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{success=true,framework=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,tools=root,bundledEncoder=encoder,bundledDecoder=decoder,nefsLoaded=true,wpfRendered=true,frames=result.SampleCount,channels=result.Channels},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception ex){await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{success=false,error=ex.ToString()}));return 1;}
    }
}

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RegularAudioTool;

public partial class MainWindow : Window
{
    private sealed record FileRow(string Path,string Label)
    {
        public bool IsModified { get; set; }
    }
    private AudioModDetails? modDetails;
    private string? projectGamePath;
    private AudioDocument? document;
    private readonly MediaPlayer player = new();
    private readonly DispatcherTimer timer = new() {Interval=TimeSpan.FromMilliseconds(250)};
    private readonly string cache = Path.Combine(Path.GetTempPath(),"AlterEgoAudioPreview",Guid.NewGuid().ToString("N"));
    private string ffmpeg = LocateFfmpeg();
    private bool busy, suppressSelection, changesSaved;
    private FileRow? openedRow;
    private string? playbackKey;
    private AudioEntry? Selected => Sounds.SelectedItem as AudioEntry;
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_,e)=>{if(busy)e.Handled=true;};
        Directory.CreateDirectory(cache);
        player.Volume = Volume.Value;
        player.MediaOpened += (_,_)=>{ if(player.NaturalDuration.HasTimeSpan) Timeline.Maximum = player.NaturalDuration.TimeSpan.TotalSeconds; };
        player.MediaEnded += (_,_)=>{ if(Loop.IsChecked==true){player.Position=TimeSpan.Zero;player.Play();}else player.Stop(); };
        player.MediaFailed += (_,e)=>Status.Text="Playback failed: " + e.ErrorException.Message;
        timer.Tick += (_,_)=>
        {
            if (!Timeline.IsMouseCaptureWithin) Timeline.Value=player.Position.TotalSeconds;
            TimeLabel.Text=$"{player.Position:mm\\:ss} / {(player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan : TimeSpan.Zero):mm\\:ss}";
        };
        timer.Start();
        Closing += (_,e)=>{if(!DiscardAllowed())e.Cancel=true;};
        Closed += (_,_)=>{timer.Stop();player.Close();try{Directory.Delete(cache,true);}catch(IOException){};};
        var args=Environment.GetCommandLineArgs();
        if(args.Length>1 && (File.Exists(args[1]) || Directory.Exists(args[1]))) Loaded += async (_,_)=>await OpenAsync(args[1]);
    }
    private static string LocateFfmpeg()
    {
        string local=Path.Combine(PortableDependencies.DirectoryPath,"vendor","ffmpeg","ffmpeg.exe");
        if(File.Exists(local))return local;
        foreach(string dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
        {
            try{string p=Path.Combine(dir,"ffmpeg.exe");if(File.Exists(p))return p;}catch(ArgumentException){}
        }
        return "";
    }
    private bool DiscardAllowed()
    {
        if(busy){MessageBox.Show(this,"Please wait until the current action is finished.","Ego Audio Editor");return false;}
        return changesSaved || document?.HasChanges!=true || MessageBox.Show(this,"The current replacements have not been exported. Discard changes?","Unexported Changes",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
    }
    private async Task RunAsync(Func<Task> work)
    {
        if(busy)return;
        busy=true;OpenBar.IsEnabled=false;EditBar.IsEnabled=false;Files.IsHitTestVisible=false;Sounds.IsHitTestVisible=false;
        try{await work();}catch(Exception ex){Status.Text=ex.Message;MessageBox.Show(this,ex.Message,"Ego Audio Editor",MessageBoxButton.OK,MessageBoxImage.Warning);}
        finally{busy=false;OpenBar.IsEnabled=true;EditBar.IsEnabled=true;Files.IsHitTestVisible=true;Sounds.IsHitTestVisible=true;}
    }
    private async void OpenFile_Click(object sender,RoutedEventArgs e)
    {
        if(!DiscardAllowed())return;
        var dialog=new OpenFileDialog{Filter="Audio and Projects|*.nfs;*.bnk;*.wem;*.egoaudio|Ego Audio Project|*.egoaudio|F1 Audio|*.nfs;*.bnk;*.wem|All Files|*.*"};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.OpenFile", global: true)==true)await OpenAsync(dialog.FileName);
    }
    private async void OpenFolder_Click(object sender,RoutedEventArgs e)
    {
        if(!DiscardAllowed())return;
        var dialog=new OpenFolderDialog{Title="Select F1 Audio Folder"};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.OpenFolder", global: true)==true)await OpenAsync(dialog.FolderName);
    }
    private async Task OpenAsync(string path)
    {
        await RunAsync(async ()=>
        {
            if(Path.GetExtension(path).Equals(".egoaudio",StringComparison.OrdinalIgnoreCase))
            {
                var project=await Task.Run(()=>AudioProject.Open(path,Path.Combine(cache,"projects",Guid.NewGuid().ToString("N"))));
                var row=new FileRow(project.Document.SourcePath,Path.GetFileName(project.Document.SourcePath)){IsModified=project.Document.HasChanges};
                suppressSelection=true;Files.ItemsSource=new[]{row};Files.SelectedIndex=0;suppressSelection=false;
                ShowDocument(project.Document,row);
                modDetails=project.Info.Mod;projectGamePath=project.Info.GamePath;changesSaved=true;
                Status.Text="Project opened: "+project.Info.Mod.Name+". Modified files and sounds are shown in bold.";
                return;
            }
            var files=await Task.Run(()=>Directory.Exists(path)
                ? Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories).Where(p=>new[]{".nfs",".bnk",".wem"}.Contains(Path.GetExtension(p).ToLowerInvariant())).OrderBy(p=>p).Select(p=>new FileRow(p,Path.GetRelativePath(path,p))).ToList()
                : new List<FileRow>{new(path,Path.GetFileName(path))});
            if(files.Count==0)throw new InvalidDataException("No .nfs, .bnk, or .wem files were found in this folder.");
            suppressSelection=true;Files.ItemsSource=files;Files.SelectedIndex=0;suppressSelection=false;
            await LoadAsync(files[0]);
        });
    }
    private async Task LoadAsync(FileRow row)
    {
        Status.Text="Lese " + row.Label + "…";
        var loaded=await Task.Run(()=>AudioDocument.Read(row.Path));
        modDetails=null;projectGamePath=null;
        ShowDocument(loaded,row);
    }
    private void ShowDocument(AudioDocument loaded,FileRow row)
    {
        player.Close();playbackKey=null;document=loaded;openedRow=row;changesSaved=false;
        DocumentTitle.Text=row.Label;
        Search.Text="";Sounds.ItemsSource=loaded.Entries;
        if(loaded.Entries.Count>0)Sounds.SelectedIndex=0;
        Status.Text=$"{loaded.Entries.Count} Sounds loaded. WAV replacements may be longer or shorter; the number of channels and sample rate must be preserved.";
        if(loaded.Archive!=null && loaded.Entries.Count>0)Status.Text+=" Original speech filenames are shown; search by name or archive ID.";
        if(loaded.Entries.Count==0)Status.Text="This bank contains control data but no directly playable media.";
    }
    private async void Files_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(suppressSelection || Files.SelectedItem is not FileRow row || row==openedRow)return;
        if(!DiscardAllowed()){suppressSelection=true;Files.SelectedItem=openedRow;suppressSelection=false;return;}
        await RunAsync(async ()=>{try{await LoadAsync(row);}catch{suppressSelection=true;Files.SelectedItem=openedRow;suppressSelection=false;throw;}});
    }
    private void Search_TextChanged(object sender,TextChangedEventArgs e)
    {
        if(Sounds?.ItemsSource==null)return;
        var view=CollectionViewSource.GetDefaultView(Sounds.ItemsSource);
        string query=Search.Text;
        view.Filter=o=>o is AudioEntry row && row.MatchesSearch(query);
    }
    private void Sounds_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(Selected is not {} entry)return;
        player.Stop();playbackKey=null;
        Details.Text=$"{entry.Format} · {entry.State}" + (entry.IsPrefetch && entry.Full==null ? " · The full external stream is missing; playback/replacement is locked." : entry.CanReplace ? " · WAV or matching WEM can be used as a replacement." : " · Replacement is not yet available for this format.");
    }
    private async Task<string> DecodeAsync(AudioEntry entry)
    {
        if(!entry.CanPlay)throw new InvalidDataException("The entry is incomplete or an unsupported plugin media.");
        byte[] data=entry.Current;
        string key=await Task.Run(()=>Convert.ToHexString(SHA256.HashData(data)));
        string output=Path.Combine(cache,key+".wav");
        if(File.Exists(output))return output;
        if(WwiseMedia.Inspect(data).IsPcm){byte[] wave=await Task.Run(()=>WwiseMedia.CreatePcmWave(data));await File.WriteAllBytesAsync(output,wave);return output;}
        string input=Path.Combine(cache,key+".wem");
        await File.WriteAllBytesAsync(input,data);
        string decoder=Path.Combine(PortableDependencies.DirectoryPath,"vendor","vgmstream","vgmstream-cli.exe");
        if(!File.Exists(decoder))throw new FileNotFoundException("The bundled vgmstream decoder is missing.",decoder);
        var start=new ProcessStartInfo(decoder){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"-i","-o",output,input})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Decoder could not be started.");
        Task<string> log=process.StandardOutput.ReadToEndAsync(),error=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try{await process.WaitForExitAsync(timeout.Token);}catch{process.Kill(true);throw;}
        await log;string err=await error;
        if(process.ExitCode!=0 || !File.Exists(output)){if(File.Exists(output))File.Delete(output);throw new InvalidDataException("Audio could not be decoded. " + err);}
        return output;
    }
    private async void Play_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} row)return;
        await RunAsync(async ()=>
        {
            string file=await DecodeAsync(row);
            if(playbackKey!=file){player.Open(new Uri(file));playbackKey=file;}
            player.Play();Status.Text="Vorschau: " + row.Name + (row.Replacement!=null ? " (Ersatz)" : "");
        });
    }
    private void Pause_Click(object sender,RoutedEventArgs e)=>player.Pause();
    private void Stop_Click(object sender,RoutedEventArgs e)=>player.Stop();
    private void Timeline_Seek(object sender,MouseButtonEventArgs e){if(player.NaturalDuration.HasTimeSpan)player.Position=TimeSpan.FromSeconds(Timeline.Value);}
    private void Volume_Changed(object sender,RoutedPropertyChangedEventArgs<double> e){if(player!=null)player.Volume=e.NewValue;}
    private async void ExportWave_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} row)return;
        var dialog=new SaveFileDialog{Filter="PCM WAV|*.wav",FileName=SafeName(row.Name)+".wav"};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.ExportWav", global: true)!=true)return;
        await RunAsync(async ()=>{ProtectOriginal(dialog.FileName,row);string file=await DecodeAsync(row);await using var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read,81920,true);await using var output=new FileStream(dialog.FileName,FileMode.Create,FileAccess.Write,FileShare.None,81920,true);await input.CopyToAsync(output);Status.Text="WAV exported: " + dialog.FileName;});
    }
    private async void ExportMedia_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} row)return;
        var dialog=new SaveFileDialog{Filter="Wwise-Medium|*.wem",FileName=SafeName(row.Name)+".wem"};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.ExportWem", global: true)!=true)return;
        await RunAsync(async ()=>{ProtectOriginal(dialog.FileName,row);await File.WriteAllBytesAsync(dialog.FileName,row.Current);Status.Text="Medium exported: " + dialog.FileName;});
    }
    private async void Replace_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} row)return;
        if(!row.CanReplace){MessageBox.Show(this,"This entry is incomplete or its format is not yet supported for replacement. Multichannel Opus can be previewed and exported.","Ego Audio Editor");return;}
        var dialog=new OpenFileDialog{Filter="Replacement Sound|*.wav;*.wem|PCM WAV|*.wav|Wwise-Medium|*.wem"};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.ImportReplacement", global: true)!=true)return;
        await RunAsync(async ()=>
        {
            Status.Text="Checking and encoding replacement sound…";
            byte[] replacement;
            if(Path.GetExtension(dialog.FileName).Equals(".wem",StringComparison.OrdinalIgnoreCase))
            {
                replacement=await File.ReadAllBytesAsync(dialog.FileName);
                byte[] original=row.Full??row.Embedded;
                await Task.Run(()=>WwiseMedia.ValidateReplacement(original,replacement));
            }
            else replacement=await WwiseMedia.EncodeReplacementAsync(row.Full??row.Embedded,dialog.FileName,ffmpeg);
            // Decode once before accepting an encoded replacement.
            byte[]? old=row.Replacement;row.Replacement=replacement;
            try{await DecodeAsync(row);}catch{row.Replacement=old;throw;}
            player.Close();playbackKey=null;changesSaved=false;RefreshModified();Sounds.Items.Refresh();
            Details.Text=row.Format + " · Replacement pending";
            Status.Text="Replacement is pending and can now be previewed. Use \"Export to Mod\" to output.";
        });
    }
    private void Revert_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} row || busy)return;
        row.Replacement=null;changesSaved=false;RefreshModified();player.Close();playbackKey=null;Sounds.Items.Refresh();Sounds_SelectionChanged(sender,null!);Status.Text="Selected sound reverted.";
    }
    private async void Save_Click(object sender,RoutedEventArgs e)
    {
        if(document==null)return;
        var selectedDocument=document;
        var detailsDialog=new ModDetailsDialog(this,modDetails??new AudioModDetails(Path.GetFileNameWithoutExtension(document.SourcePath)+" Audio Mod","","1.0"));
        if(detailsDialog.ShowDialog()!=true)return;
        modDetails=detailsDialog.Details;
        string? gamePath=projectGamePath??AudioModExporter.FindGamePath(selectedDocument.SourcePath);
        if(gamePath==null)
        {
            var folder=new OpenFolderDialog{Title="Select the target folder for this file inside F1 25/audio"};
            if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(folder,this,"Audio.ModTarget",global:true)!=true)return;
            gamePath=AudioModExporter.FindGamePath(Path.Combine(folder.FolderName,Path.GetFileName(selectedDocument.SourcePath)));
            if(gamePath==null){Status.Text="Please select a destination inside the game's audio folder.";return;}
        }
        var dialog=new SaveFileDialog{Title="Export to Mod",Filter="Simplified Launcher Mod (*.zip)|*.zip",DefaultExt=".zip",FileName=SafeName(modDetails.Name)+".zip",OverwritePrompt=false};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.ExportMod", global: true)!=true)return;
        if(!ReferenceEquals(selectedDocument,document))return;
        string targetPath=gamePath;
        await RunAsync(async ()=>{await Task.Run(()=>AudioModExporter.Export(selectedDocument,dialog.FileName,targetPath,modDetails));changesSaved=true;Status.Text="Mod exported: " + dialog.FileName + ". Project saved: " + Path.ChangeExtension(dialog.FileName,".egoaudio");});
    }
    private void RefreshModified()
    {
        if(openedRow!=null)openedRow.IsModified=document?.HasChanges==true;
        Files.Items.Refresh();
    }
    private async void Ffmpeg_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Filter="FFmpeg|ffmpeg.exe|Software|*.exe"};
        if(await StandaloneEditor.DirectoryHistoryDialogs.ShowAsync(dialog, this, "Audio.ChooseFFmpeg", global: true)==true){ffmpeg=dialog.FileName;Status.Text="FFmpeg for WAV re-import: " + ffmpeg;}
    }
    private static string SafeName(string name)=>string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c));
    private void ProtectOriginal(string path,AudioEntry row)
    {
        path=Path.GetFullPath(path);
        if(document?.SourcePath.Equals(path,StringComparison.OrdinalIgnoreCase)==true || document?.Entries.Any(e=>e.ExternalPath?.Equals(path,StringComparison.OrdinalIgnoreCase)==true)==true ||
            Files.Items.OfType<FileRow>().Any(f=>Path.GetFullPath(f.Path).Equals(path,StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Export cannot overwrite a loaded original file.");
    }

    private void TitleBar_DragMove(
    object sender,
    System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }

        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Logo_Click(object sender, MouseButtonEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://www.patreon.com/cw/JonnieDesigns",
            UseShellExecute = true
        });
    }
}

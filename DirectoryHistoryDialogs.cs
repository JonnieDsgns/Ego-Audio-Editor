using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace StandaloneEditor;

// Each editor keeps its own native-dialog directory history, without suite dependencies.
internal static class DirectoryHistoryDialogs
{
    private static readonly SemaphoreSlim Gate=new(1);
    private static readonly string HistoryFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),typeof(DirectoryHistoryDialogs).Assembly.GetName().Name!,"directory-history.json");
    private static Dictionary<string,string>? history;
    public static async Task<bool?> ShowAsync(CommonItemDialog dialog,Window? owner,string action,bool global=false)
    {
        if(!await Gate.WaitAsync(0))return false;
        try
        {
            if(history==null)
            {
                try{history=JsonSerializer.Deserialize<Dictionary<string,string>>(await File.ReadAllTextAsync(HistoryFile))??new();}
                catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){history=new();}
            }
            if(history.TryGetValue(action,out string? folder)&&Directory.Exists(folder))dialog.InitialDirectory=folder;
            if(dialog is FileDialog file && Path.IsPathFullyQualified(file.FileName))file.FileName=Path.GetFileName(file.FileName);
            bool? result=owner==null?dialog.ShowDialog():dialog.ShowDialog(owner);
            if(result==true)
            {
                string? selected=dialog is OpenFolderDialog pick?pick.FolderName:Path.GetDirectoryName(((FileDialog)dialog).FileName);
                if(!string.IsNullOrEmpty(selected))
                {
                    history[action]=selected;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
                        string temporary=HistoryFile+".tmp";
                        await File.WriteAllTextAsync(temporary,JsonSerializer.Serialize(history));
                        File.Move(temporary,HistoryFile,true);
                    }
                    catch(Exception e) when(e is IOException or UnauthorizedAccessException){ }
                }
            }
            return result;
        }
        finally{Gate.Release();}
    }
}

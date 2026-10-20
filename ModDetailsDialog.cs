using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RegularAudioTool;

public sealed class ModDetailsDialog : Window
{
    public AudioModDetails Details { get; private set; }
    public ModDetailsDialog(Window owner,AudioModDetails initial)
    {
        Details=initial;
        Owner=owner;Title="Export to Mod";Width=440;SizeToContent=SizeToContent.Height;WindowStyle=WindowStyle.None;
        ResizeMode =ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(31,31,31));Foreground=Brushes.White;
        var panel=new StackPanel{Margin=new Thickness(24)};Content=panel;
        TextBox Field(string label,string value)
        {
            panel.Children.Add(new TextBlock{Text=label,Margin=new Thickness(0,0,0,6)});
            var input=new TextBox{Text=value,Margin=new Thickness(0,0,0,16)};
            panel.Children.Add(input);return input;
        }
        var name=Field("Mod name",initial.Name);
        var author=Field("Creator",initial.Author);
        var version=Field("Version",initial.Version);
        panel.Children.Add(new TextBlock{Text="Creates a Launcher ZIP and an editable .egoaudio project.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);
        var cancel=new Button{Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,8,0)};buttons.Children.Add(cancel);
        var export=new Button{Content="Continue…",IsDefault=true};buttons.Children.Add(export);
        export.Click+=(_,_)=>
        {
            if(string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(author.Text)||string.IsNullOrWhiteSpace(version.Text))
            {MessageBox.Show(this,"Enter a mod name, creator, and version.",Title);return;}
            Details=new(name.Text.Trim(),author.Text.Trim(),version.Text.Trim());DialogResult=true;
        };
    }
}

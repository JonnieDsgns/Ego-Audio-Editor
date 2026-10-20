using System.Windows;
namespace RegularAudioTool;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if(e.Args.Length==3 && e.Args[0]=="--verify-portable")
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            int code=await PortableVerification.Run(e.Args[1],e.Args[2]);
            Shutdown(code);
            return;
        }
        StartupUri=new System.Uri("MainWindow.xaml",System.UriKind.Relative);
        base.OnStartup(e);
    }
}

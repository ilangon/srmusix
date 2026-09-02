using System.Windows;
using LibVLCSharp.Shared;

namespace SRMusix.Playout;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        Core.Initialize();
        base.OnStartup(e);
    }
}


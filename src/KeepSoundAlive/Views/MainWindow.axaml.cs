using Avalonia.Controls;
using KeepSoundAlive.ViewModels;

namespace KeepSoundAlive.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Raised when the window is closed while "close to tray" is off, i.e. closing the window means exiting.
    /// With "close to tray" on, the window is simply destroyed (freeing its rendering resources) and the app
    /// keeps running; it creates a new window when the user opens it again.
    /// </summary>
    public event Action? ExitRequested;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is MainViewModel { CloseToTray: false })
        {
            ExitRequested?.Invoke();
        }
    }
}

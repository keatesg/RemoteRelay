using Avalonia.Controls;
using Avalonia.Input;

namespace RemoteRelay;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainWindowViewModel();
        DataContext = vm;
        
        WindowState = vm.IsFullscreen ? WindowState.FullScreen : WindowState.Normal;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Close();
            e.Handled = true;
        }

        if (e.Key == Key.F11)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
            if (DataContext is MainWindowViewModel vm)
                vm.SaveFullscreenState(WindowState == WindowState.FullScreen);
            e.Handled = true;
        }
    }
}
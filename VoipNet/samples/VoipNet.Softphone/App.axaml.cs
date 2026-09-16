using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using VoipNet.Softphone.ViewModels;

namespace VoipNet.Softphone;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new SoftphoneViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow.Opened += async (_, _) => await viewModel.StartAsync();
            desktop.ShutdownRequested += async (_, _) => await viewModel.DisposeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

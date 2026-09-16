using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace VoipNet.Gallery;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new GalleryViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) =>
            {
                foreach (var page in viewModel.Pages)
                {
                    page.ResetAsync().GetAwaiter().GetResult();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

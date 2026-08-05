using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Telepati.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The window is created after Program.Main started the host, so the address is
        // already known; pointing the WebView at it is the last step of startup.
        var address = Program.BlazorAddress;
        if (string.IsNullOrEmpty(address)) return;

        var webView = this.FindControl<NativeWebView>("WebView");
        var splash = this.FindControl<StackPanel>("Splash");

        if (webView is null) return;

        webView.Source = new Uri(address);
        webView.IsVisible = true;

        if (splash is not null) splash.IsVisible = false;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}

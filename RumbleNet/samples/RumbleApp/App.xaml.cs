namespace RumbleApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		return new Window(new MainPage())
		{
			Title = "RumbleApp",
			Width = 1180,
			Height = 760,
			MinimumWidth = 380,
			MinimumHeight = 520,
		};
	}
}

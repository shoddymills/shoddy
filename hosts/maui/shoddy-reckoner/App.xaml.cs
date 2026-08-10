using Shoddy.Maui;

namespace ShoddyReckoner;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell());
		// Content backends (B1.4a): installed once the shell exists,
		// so a canvas opened by the first typed line has somewhere to
		// go. Audio drains on the way out.
		window.Created += (_, _) => Backends.Install();
		window.Destroying += (_, _) => MauiAudioBackend.Shutdown();
		return window;
	}
}
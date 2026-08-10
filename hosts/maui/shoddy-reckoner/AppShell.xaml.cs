namespace ShoddyReckoner;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		// `--open <Route>` jumps straight to a page at launch — a
		// deep-link for scripts and for driving the app under test
		// automation (any route from this file: PacPage, MungoPage...).
		string[] args = Environment.GetCommandLineArgs();
		int at = Array.IndexOf(args, "--open");
		if (at >= 0 && at + 1 < args.Length)
		{
			string route = args[at + 1];
			Loaded += (_, _) => _ = GoToAsync("//" + route);
		}
	}
}

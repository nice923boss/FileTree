using System.ComponentModel;
using System.Diagnostics;
using FileTree.Scanning;
using FileTree.UI;

namespace FileTree;

internal static class Program
{
    private const int ErrorCancelled = 1223;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench") return Bench.Run(args);

        if (!ScanService.IsAdministrator && !args.Contains("--no-elevate") && TryRelaunchElevated(args))
            return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(Arg(args, "--scan")));
        return 0;
    }

    /// <summary>Restarts the app through UAC. Returns false when the user declines (slow path is used instead).</summary>
    private static bool TryRelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            psi.ArgumentList.Add("--no-elevate");
            foreach (string a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

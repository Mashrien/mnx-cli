using System.Diagnostics;

namespace Mash.UI.MNXFile.CLI;

internal static class Program {
    private static int Main(string[] args) {
        ConsoleText.Configure();
        if (args.Length == 1 && args[0] == "--version") {
            return VersionDisplay.Print("mnx");
            }

        if (args.Length > 0 && IsInteractiveSwitch(args[0])) {
            return LaunchInteractive(args.Skip(1));
            }

        return new CommandApplication().Run(args);
        }

    private static bool IsInteractiveSwitch(string argument) {
        return argument.ToLowerInvariant() is "-i" or "-interactive" or "--i" or "--interactive" or "/i";
        }

    private static int LaunchInteractive(IEnumerable<string> arguments) {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mnxi.exe" : "mnxi");
        string assembly = Path.Combine(AppContext.BaseDirectory, "mnxi.dll");
        ProcessStartInfo start = new ProcessStartInfo {
            UseShellExecute = false
            };
        if (System.IO.File.Exists(executable)) {
            start.FileName = executable;
            }
        else if (System.IO.File.Exists(assembly)) {
            start.FileName = "dotnet";
            start.ArgumentList.Add(assembly);
            }
        else {
            Console.Error.WriteLine("error: mnxi was not found beside mnx. Install both programs from mnx-cli.");
            return 1;
            }

        foreach (string argument in arguments) {
            start.ArgumentList.Add(argument);
            }

        try {
            using Process child = Process.Start(start) ?? throw new IOException("Unable to start mnxi.");
            // The child receives terminal Ctrl-C itself; keep its parent alive to return its status.
            ConsoleCancelEventHandler preserveChild = (_, cancel) => cancel.Cancel = true;
            Console.CancelKeyPress += preserveChild;
            try {
                child.WaitForExit();
                return child.ExitCode;
                }
            finally {
                Console.CancelKeyPress -= preserveChild;
                }
            }
        catch (Exception error) {
            Console.Error.WriteLine("error: " + error.Message);
            return 1;
            }
        }
    }

using System.Reflection;

namespace Mash.UI.MNXFile.CLI;

internal static class VersionDisplay {
    public static int Print(string programName) {
        Assembly assembly = typeof(VersionDisplay).Assembly;
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        Console.WriteLine(programName + " " + version);
        return 0;
        }
    }

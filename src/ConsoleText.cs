using System.Text;

namespace Mash.UI.MNXFile.CLI;

internal static class ConsoleText {
    internal static void Configure() {
        // Classic Windows consoles otherwise replace Unicode names with question marks.
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        }
    }

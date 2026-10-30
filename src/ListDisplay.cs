using System.Globalization;
using System.Text;

/// <summary>Fixed listing columns; paths and exact counts expand instead of being cut off.</summary>
namespace Mash.UI.MNXFile.CLI;

internal static class ListDisplay {
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];
    internal static (string Number, string Unit) HumanSize(ulong bytes) {
        decimal scaled = bytes;
        int unit = 0;
        while (scaled >= 1024 && unit < Units.Length - 1) {
            scaled /= 1024;
            unit++;
            }

        int decimals = unit == 0 || scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
        decimal rounded = Math.Round(scaled, decimals, MidpointRounding.AwayFromZero);
        // Rounding 1023.999 KB should display 1 MB, rather than 1024 KB.
        if (rounded >= 1024 && unit < Units.Length - 1) {
            scaled /= 1024;
            unit++;
            decimals = scaled >= 100 ? 0 : scaled >= 10 ? 1 : 2;
            rounded = Math.Round(scaled, decimals, MidpointRounding.AwayFromZero);
            }

        string format = decimals switch {
            0 => "0",
            1 => "0.#",
            _ => "0.##"
            };
        string number = rounded.ToString(format, CultureInfo.InvariantCulture);
        // Keep the requested unit vocabulary even for sizes beyond 9999 TB.
        if (number.Length > 4) {
            number = rounded.ToString("0e0", CultureInfo.InvariantCulture);
            }

        return (number, Units[unit]);
        }

    internal static string DisplayPath(string path) {
        StringBuilder result = new StringBuilder(path.Length);
        foreach (char c in path) {
            result.Append(c switch {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString()});
            }

        return result.ToString();
        }

    internal static string Row(
        bool directory,
        string name,
        ulong bytes,
        DateTime creation,
        int nameWidth,
        int byteWidth,
        bool compressed = false,
        bool encrypted = false,
        string? recursivePath = null
        ) {
        (string? number, string? unit) = HumanSize(bytes);
        string count = bytes.ToString(CultureInfo.InvariantCulture);
        string date = creation.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        // Each letter has its own fixed slot. Spanning is reserved, not implemented.
        string flags = $"[{(!directory && compressed ? 'C' : ' ')}{(encrypted ? 'E' : ' ')} ]";
        string prefix = $" [{(directory ? 'D' : 'F')}]   ";
        if (recursivePath is not null) {
            prefix += recursivePath.PadLeft(22) + "   ";
            }

        return prefix + $"{name.PadLeft(nameWidth)}   " + $"{number.PadLeft(4)}  {unit.PadLeft(2)}  {flags}  ({count.PadLeft(byteWidth)})  {date}";
        }
    }

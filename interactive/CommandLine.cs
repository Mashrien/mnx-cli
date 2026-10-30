using System.Text;

namespace Mash.UI.MNXFile.CLI;

/// <summary>Split quoted arguments without executing or expanding shell syntax.</summary>
internal static class CommandLine {
    public static string[] Parse(string line, bool allowUnterminatedQuote = false) {
        List<string> arguments = new List<string>();
        StringBuilder current = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int index = 0; index < line.Length; index++) {
            char character = line[index];
            if (quote == '\0' && char.IsWhiteSpace(character)) {
                if (started) {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                    }

                continue;
                }

            if (character is '\'' or '"') {
                if (quote == '\0') {
                    quote = character;
                    started = true;
                    continue;
                    }

                if (quote == character) {
                    quote = '\0';
                    continue;
                    }
                }

            if (character == '\\' && quote != '\'' && index + 1 < line.Length) {
                char next = line[index + 1];
                if (next == '"' || (quote == '\0' && (next == '\'' || char.IsWhiteSpace(next))) || next == '\\') {
                    current.Append(next);
                    index++;
                    started = true;
                    continue;
                    }
                }

            current.Append(character);
            started = true;
            }

        if (quote != '\0' && !allowUnterminatedQuote) {
            throw new ArgumentException("Unterminated quote. Close the matching single or double quote.");
            }

        if (started) {
            arguments.Add(current.ToString());
            }

        return arguments.ToArray();
        }

    public static string Quote(string value) {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }
    }

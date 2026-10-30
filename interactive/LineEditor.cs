using System.Globalization;
using System.Text;

namespace Mash.UI.MNXFile.CLI;

/// <summary>Small terminal editor; redirected input always uses ordinary lines.</summary>
internal sealed class LineEditor {
    private readonly List<string> _history = new List<string>();

    public string? ReadLine(string prompt, Func<string, IReadOnlyList<string>> complete) {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) {
            return Console.ReadLine();
            }

        bool previousControlMode = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try {
            string text = "";
            string savedInput = "";
            int cursor = 0;
            int historyIndex = _history.Count;
            IReadOnlyList<string> completions = Array.Empty<string>();
            int completionIndex = -1;
            Console.Write(prompt);
            while (true) {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                bool control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
                if (key.Key != ConsoleKey.Tab) {
                    completions = Array.Empty<string>();
                    completionIndex = -1;
                    }

                if (key.Key == ConsoleKey.Enter) {
                    Console.WriteLine();
                    if (!string.IsNullOrWhiteSpace(text) && (_history.Count == 0 || _history[_history.Count - 1] != text)) {
                        _history.Add(text);
                        if (_history.Count > 250) {
                            _history.RemoveAt(0);
                            }
                        }

                    return text;
                    }

                if (control && key.Key == ConsoleKey.C) {
                    Console.WriteLine("^C");
                    return "";
                    }

                if (control && key.Key == ConsoleKey.D && text.Length == 0) {
                    Console.WriteLine();
                    return null;
                    }

                switch (key.Key) {
                    case ConsoleKey.LeftArrow:
                        cursor = PreviousBoundary(text, cursor);
                        break;
                    case ConsoleKey.RightArrow:
                        cursor = NextBoundary(text, cursor);
                        break;
                    case ConsoleKey.Home:
                        cursor = 0;
                        break;
                    case ConsoleKey.End:
                        cursor = text.Length;
                        break;
                    case ConsoleKey.Backspace:
                        if (cursor > 0) {
                            int previous = PreviousBoundary(text, cursor);
                            text = text.Remove(previous, cursor - previous);
                            cursor = previous;
                            }

                        break;
                    case ConsoleKey.Delete:
                        if (cursor < text.Length) {
                            text = text.Remove(cursor, NextBoundary(text, cursor) - cursor);
                            }

                        break;
                    case ConsoleKey.UpArrow:
                        if (historyIndex == _history.Count) {
                            savedInput = text;
                            }

                        if (historyIndex > 0) {
                            historyIndex--;
                            text = _history[historyIndex];
                            cursor = text.Length;
                            }

                        break;
                    case ConsoleKey.DownArrow:
                        if (historyIndex < _history.Count) {
                            historyIndex++;
                            text = historyIndex == _history.Count ? savedInput : _history[historyIndex];
                            cursor = text.Length;
                            }

                        break;
                    case ConsoleKey.Tab:
                        if (completions.Count == 0) {
                            completions = complete(text);
                            }

                        if (completions.Count > 0) {
                            completionIndex = (completionIndex + 1) % completions.Count;
                            text = completions[completionIndex];
                            cursor = text.Length;
                            }

                        break;
                    case ConsoleKey.Escape:
                        text = "";
                        cursor = 0;
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0') {
                            text = text.Insert(cursor, key.KeyChar.ToString());
                            cursor++;
                            }

                        break;
                    }

                Redraw(prompt, text, cursor);
                }
            }
        finally {
            Console.TreatControlCAsInput = previousControlMode;
            }
        }

    public string ReadPassword(string prompt) {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected) {
            string password = Console.ReadLine() ?? throw new EndOfStreamException("A passphrase is required.");
            Console.Error.WriteLine();
            return password;
            }

        bool previousControlMode = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        StringBuilder secret = new StringBuilder();
        try {
            while (true) {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) {
                    Console.Error.WriteLine();
                    return secret.ToString();
                    }

                if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C) {
                    Console.Error.WriteLine();
                    throw new OperationCanceledException();
                    }

                if (key.Key == ConsoleKey.Backspace && secret.Length > 0) {
                    string current = secret.ToString();
                    secret.Length = PreviousBoundary(current, current.Length);
                    }
                else if (!char.IsControl(key.KeyChar) && key.KeyChar != '\0') {
                    secret.Append(key.KeyChar);
                    }
                }
            }
        finally {
            secret.Clear();
            Console.TreatControlCAsInput = previousControlMode;
            }
        }

    private static int PreviousBoundary(string text, int cursor) {
        int previous = 0;
        foreach (int boundary in StringInfo.ParseCombiningCharacters(text)) {
            if (boundary >= cursor) {
                break;
                }

            previous = boundary;
            }

        return previous;
        }

    private static int NextBoundary(string text, int cursor) {
        foreach (int boundary in StringInfo.ParseCombiningCharacters(text)) {
            if (boundary > cursor) {
                return boundary;
                }
            }

        return text.Length;
        }

    private static void Redraw(string prompt, string text, int cursor) {
        Console.Write("\r\u001b[2K");
        Console.Write(prompt);
        Console.Write(text);
        // Most terminals count wide CJK/emoji glyphs as two columns.
        int distance = DisplayWidth(text[cursor..]);
        if (distance > 0) {
            Console.Write($"\u001b[{distance}D");
            }
        }

    private static int DisplayWidth(string text) {
        int width = 0;
        foreach (Rune rune in text.EnumerateRunes()) {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) {
                continue;
                }

            int value = rune.Value;
            bool wide = value >= 0x1100 && (value <= 0x115F || value is 0x2329 or 0x232A || value is >= 0x2E80 and <= 0xA4CF || value is >= 0xAC00 and <= 0xD7A3 || value is >= 0xF900 and <= 0xFAFF || value is >= 0xFE10 and <= 0xFE6F || value is >= 0xFF00 and <= 0xFF60 || value is >= 0x1F000 and <= 0x1FAFF || value is >= 0x20000 and <= 0x3FFFD);
            width += wide ? 2 : 1;
            }

        return width;
        }
    }

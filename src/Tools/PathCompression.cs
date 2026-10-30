using System.Text;
using System;

namespace Mash.UI.MNXFile.Tools;

public static class PathCompression {
    public static string CompressPath(string fullPath) {
        if (string.IsNullOrEmpty(fullPath)) {
            return string.Empty;
            }

        // Archive paths must behave identically on Linux and Windows.
        string normalized = fullPath.Replace('\\', '/');
        int separatorIndex = normalized.LastIndexOf('/');
        string directoryPath = separatorIndex < 0 ? "" : normalized[..separatorIndex];
        string[] segments = directoryPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++) {
            segments[i] = EscapeSegment(segments[i]);
            }

        if (segments.Length == 0) {
            return "/";
            }

        if (segments.Length == 1) {
            return "/" + Abbreviate(segments[0], 21);
            }

        string firstFolder = segments[0];
        string lastDir = segments[segments.Length - 1];
        string separator = segments.Length == 2 ? "/" : "/.../";
        int available = 22 - 1 - separator.Length;
        int firstWidth = Math.Min(firstFolder.Length, available / 2);
        int lastWidth = Math.Min(lastDir.Length, available - firstWidth);
        firstWidth = Math.Min(firstFolder.Length, available - lastWidth);
        return "/" + Abbreviate(firstFolder, firstWidth) + separator + Abbreviate(lastDir, lastWidth);
        }

    private static string Abbreviate(string segment, int width) {
        if (segment.Length <= width) {
            return segment;
            }

        if (width <= 3) {
            return Prefix(segment, width);
            }

        int headWidth = (width - 3 + 1) / 2;
        int tailStart = segment.Length - (width - 3) / 2;
        // Do not leave a split UTF-16 surrogate pair at either cut.
        if (tailStart > 0 && tailStart < segment.Length && char.IsLowSurrogate(segment[tailStart]) && char.IsHighSurrogate(segment[tailStart - 1])) {
            tailStart++;
            }

        return Prefix(segment, headWidth) + "..." + segment[tailStart..];
        }

    private static string Prefix(string segment, int width) {
        if (width > 0 && width < segment.Length && char.IsHighSurrogate(segment[width - 1]) && char.IsLowSurrogate(segment[width])) {
            width--;
            }

        return segment[..width];
        }

    private static string EscapeSegment(string segment) {
        StringBuilder result = new StringBuilder(segment.Length);
        foreach (char c in segment) {
            result.Append(c switch {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString()});
            }

        return result.ToString();
        }
    }

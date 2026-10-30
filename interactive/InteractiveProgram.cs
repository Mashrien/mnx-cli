using System.Security.Cryptography;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Archive.Resume;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.UI.MNXFile.CLI;

internal static class InteractiveProgram {
    private static int Main(string[] args) {
        ConsoleText.Configure();
        if (args.Length == 1 && args[0] == "--version") {
            return VersionDisplay.Print("mnxi");
            }

        using InteractiveSession session = new InteractiveSession();
        return session.Run(args);
        }
    }

/// <summary>Keep one archive open while each command uses the same CLI operations.</summary>
internal sealed class InteractiveSession : IDisposable {
    internal static readonly string[] CommandNames = {
        "help", "open", "create", "close", "pwd", "cd", "list", "search", "info",
        "add", "extract", "delete", "compact", "verify", "quit"
        };
    private ArchiveFile? _archive;
    private Guid _directory;
    private CancellationTokenSource? _operation;
    private readonly LineEditor _editor = new LineEditor();

    public int Run(string[] args) {
        if (args.Length > 0 && args[0] is "--help" or "-h" or "help") {
            Help();
            return args.Length == 1 ? 0 : 1;
            }

        // Parse startup options before entering the loop so launchers preserve usage failures.
        try {
            CommandApplication.Arguments startup = CommandApplication.Parse(new[] { "open" }.Concat(args).ToArray(), "password-env");
            startup.Require(0, 1);
            if (startup.Positionals.Count == 0 && startup.Get("password-env") is not null) {
                throw new ArgumentException("--password-env requires an archive path.");
                }
            }
        catch (Exception error) {
            Console.Error.WriteLine("error: " + error.Message);
            return 1;
            }

        ConsoleCancelEventHandler cancel = (_, eventArgs) => {
            eventArgs.Cancel = true;
            _operation?.Cancel();
            };
        Console.CancelKeyPress += cancel;
        try {
            if (!Console.IsInputRedirected) {
                Console.WriteLine("MNX interactive archive session. Type help for commands.");
                }

            if (args.Length > 0) {
                ExecuteSafely(new[] { "open" }.Concat(args).ToArray());
                }

            while (true) {
                string? line = _editor.ReadLine(Prompt(), Complete);
                if (line is null) {
                    return 0;
                    }

                try {
                    string[] command = CommandLine.Parse(line);
                    if (command.Length == 0) {
                        continue;
                        }

                    if (command[0].Equals("quit", StringComparison.OrdinalIgnoreCase) || command[0].Equals("exit", StringComparison.OrdinalIgnoreCase)) {
                        if (command.Length != 1) {
                            throw new ArgumentException("Usage: quit");
                            }

                        return 0;
                        }

                    ExecuteSafely(command);
                    }
                catch (Exception error) {
                    Console.Error.WriteLine("error: " + error.Message);
                    }
                }
            }
        finally {
            Console.CancelKeyPress -= cancel;
            }
        }

    private void ExecuteSafely(string[] command) {
        using CancellationTokenSource operation = new CancellationTokenSource();
        _operation = operation;
        try {
            if (_archive is not null) {
                _archive.Options.CancellationToken = operation.Token;
                }

            Execute(command);
            }
        catch (OperationCanceledException) {
            Console.Error.WriteLine("Cancelled. Completed files remain in the archive.");
            }
        catch (Exception error) {
            Console.Error.WriteLine("error: " + error.Message);
            }
        finally {
            if (_archive is not null) {
                _archive.Options.CancellationToken = CancellationToken.None;
                if (operation.IsCancellationRequested && _archive.FileQueue.Count > 0) {
                    string queuePath = RemainingQueue.Save(_archive.ArchivePath!, _archive.FileQueue);
                    Console.Error.WriteLine("Remaining queue: " + queuePath);
                    foreach (FileQueueItem item in _archive.FileQueue.ToArray()) {
                        _archive.Queue_RemoveFromQueue(item.ID);
                        }
                    }
                }

            _operation = null;
            }
        }

    private void Execute(string[] command) {
        string name = command[0].ToLowerInvariant();
        switch (name) {
            case "help":
                RequireCount(command, 1, "help");
                Help();
                return;
            case "open":
                Open(command);
                return;
            case "create":
                Create(command);
                return;
            case "close":
                RequireCount(command, 1, "close");
                Close();
                Console.WriteLine("Archive closed.");
                return;
            case "pwd":
                RequireCount(command, 1, "pwd");
                RequireArchive();
                Console.WriteLine(CurrentPath());
                return;
            case "cd":
                RequireCount(command, 2, "cd ARCHIVE-DIRECTORY");
                ChangeDirectory(command[1]);
                return;
            }

        if (!CommandNames.Contains(name, StringComparer.Ordinal)) {
            throw new ArgumentException($"Unknown command '{command[0]}'. Type help for commands.");
            }

        ArchiveFile archive = RequireArchive();
        List<string> arguments = new List<string> { name, archive.ArchivePath! };
        arguments.AddRange(command.Skip(1));
        if (name == "list") {
            CommandApplication.Arguments parsed = CommandApplication.Parse(command, "ids", "recursive");
            parsed.Require(0, 1);
            string path = parsed.Positionals.Count == 0 ? CurrentPath() : ResolvePath(parsed.Positionals[0]);
            arguments = new List<string> { "list", archive.ArchivePath!, path };
            if (parsed.Has("ids")) {
                arguments.Add("--ids");
                }

            if (parsed.Has("recursive")) {
                arguments.Add("--recursive");
                }
            }
        else if (name is "extract" or "delete") {
            if (command.Length < 2) {
                throw new ArgumentException($"Usage: {name} ARCHIVE-PATH" + (name == "extract" ? " DEST [--overwrite]" : ""));
                }

            arguments[2] = ResolvePath(command[1]);
            }

        new CommandApplication(archive, _directory).Run(arguments.ToArray());
        // A deletion of this directory or an ancestor moves the prompt to the root.
        if (_directory != archive.RootDirectoryId && archive.GetFile(_directory) is null) {
            _directory = archive.RootDirectoryId;
            }
        }

    private void Open(string[] command) {
        CommandApplication.Arguments parsed = CommandApplication.Parse(command, "password-env");
        parsed.Require(1, 1);
        string path = Path.GetFullPath(parsed.Positionals[0]);
        string? password = CommandApplication.Password(parsed);
        ArchiveFile opened = new ArchiveFile();
        try {
            try {
                opened.Open(path, password);
                }
            catch (CryptographicException) when (password is null) {
                password = _editor.ReadPassword("Passphrase: ");
                opened.Open(path, password);
                }

            Close();
            _archive = opened;
            _directory = opened.RootDirectoryId;
            Console.WriteLine("Opened " + opened.ArchivePath);
            }
        catch {
            opened.Dispose();
            throw;
            }
        }

    private void Create(string[] command) {
        CommandApplication.Arguments parsed = CommandApplication.Parse(command, "compression", "level", "layout", "chunk-size", "password-env", "encrypt");
        bool encrypt = parsed.Has("encrypt");
        parsed.Require(1, 1);
        if (encrypt && parsed.Get("password-env") is not null) {
            throw new ArgumentException("Use either --encrypt or --password-env VARIABLE.");
            }

        string? password = encrypt ? _editor.ReadPassword("New passphrase: ") : null;
        ArchiveFile created = CommandApplication.CreateArchive(parsed, password);
        Close();
        _archive = created;
        _directory = created.RootDirectoryId;
        Console.WriteLine("Created " + created.ArchivePath);
        }

    private ArchiveFile RequireArchive() {
        return _archive ?? throw new InvalidOperationException("No archive is open. Use open ARCHIVE or create ARCHIVE.");
        }

    private string CurrentPath() {
        ArchiveFile archive = RequireArchive();
        if (_directory == archive.RootDirectoryId) {
            return "/";
            }

        return "/" + CommandApplication.Paths(archive)[_directory];
        }

    private string ResolvePath(string value) {
        if (Guid.TryParse(value, out Guid id)) {
            return id.ToString();
            }

        string path = OperatingSystem.IsWindows() ? value.Replace('\\', '/') : value;
        List<string> parts = path.StartsWith('/') ? new List<string>() : CurrentPath().Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (part == ".") {
                continue;
                }

            if (part == "..") {
                if (parts.Count > 0) {
                    parts.RemoveAt(parts.Count - 1);
                    }
                }
            else {
                parts.Add(part);
                }
            }

        return "/" + string.Join('/', parts);
        }

    private void ChangeDirectory(string path) {
        ArchiveFile archive = RequireArchive();
        string resolved = ResolvePath(path);
        if (resolved == "/" || resolved == archive.RootDirectoryId.ToString()) {
            _directory = archive.RootDirectoryId;
            return;
            }

        VirtualFile folder = CommandApplication.Resolve(archive, resolved);
        if (!folder.IsDirectory) {
            throw new ArgumentException("The selected entry is not an archive directory.");
            }

        _directory = folder.FileID;
        }

    private string Prompt() {
        return _archive is null ? "mnxi> " : "mnxi:" + CurrentPath() + "> ";
        }

    private IReadOnlyList<string> Complete(string text) {
        string[] arguments;
        try {
            arguments = CommandLine.Parse(text, allowUnterminatedQuote: true);
            }
        catch (ArgumentException) {
            return Array.Empty<string>();
            }

        bool nextArgument = text.Length > 0 && char.IsWhiteSpace(text[text.Length - 1]);
        if (arguments.Length == 0 || (arguments.Length == 1 && !nextArgument)) {
            string prefix = arguments.Length == 0 ? "" : arguments[0];
            return CommandNames.Where(command => command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(command => command + " ").ToArray();
            }

        string commandName = arguments[0].ToLowerInvariant();
        string fragment = nextArgument ? "" : arguments[arguments.Length - 1];
        string[] previous = nextArgument ? arguments : arguments[..^1];
        List<string> candidates = new List<string>();
        bool archivePath = commandName is "cd" or "list" or "delete" || (commandName == "extract" && previous.Length == 1);
        try {
            if (archivePath && _archive is not null) {
                string resolved = ResolvePath(fragment);
                foreach (KeyValuePair<Guid, string> entry in CommandApplication.Paths(_archive)) {
                    VirtualFile file = _archive.GetFile(entry.Key)!;
                    string fullPath = "/" + entry.Value;
                    if (commandName == "cd" && !file.IsDirectory) {
                        continue;
                        }

                    if (fullPath.StartsWith(resolved, StringComparison.OrdinalIgnoreCase)) {
                        candidates.Add(fullPath + (file.IsDirectory ? "/" : ""));
                        }
                    }
                }
            else if (commandName is "open" or "create" or "add" or "extract") {
                string fullPath = Path.GetFullPath(fragment.Length == 0 ? "." : fragment);
                string directory = fragment.Length == 0 || Path.EndsInDirectorySeparator(fragment) ? fullPath : Path.GetDirectoryName(fullPath)!;
                string prefix = fragment.Length == 0 || Path.EndsInDirectorySeparator(fragment) ? "" : Path.GetFileName(fullPath);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory)) {
                    if (Path.GetFileName(entry).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                        candidates.Add(entry + (Directory.Exists(entry) ? Path.DirectorySeparatorChar.ToString() : ""));
                        }
                    }
                }
            }
        catch (IOException) {
            return Array.Empty<string>();
            }
        catch (UnauthorizedAccessException) {
            return Array.Empty<string>();
            }

        string before = string.Join(" ", previous.Select(CommandLine.Quote));
        return candidates.OrderBy(candidate => candidate, StringComparer.Ordinal).Select(candidate => before + " " + CommandLine.Quote(candidate)).ToArray();
        }

    private static void RequireCount(string[] command, int count, string usage) {
        if (command.Length != count) {
            throw new ArgumentException("Usage: " + usage);
            }
        }

    private static void Help() {
        Console.WriteLine("Usage: mnxi [ARCHIVE] [--password-env VARIABLE]");
        Console.WriteLine("Commands:");
        Console.WriteLine("  help                                Show commands");
        Console.WriteLine("  open ARCHIVE [--password-env VARIABLE]");
        Console.WriteLine("  create ARCHIVE [--encrypt | --password-env VARIABLE]");
        Console.WriteLine("    [--compression none|brotli|snappy|lz4] [--level N] [--layout continuous|chunked] [--chunk-size N]");
        Console.WriteLine("  close                               Close the current archive");
        Console.WriteLine("  pwd | cd ARCHIVE-DIRECTORY           Show or change the archive directory");
        Console.WriteLine("  list [ARCHIVE-DIRECTORY] [-r] [--ids]");
        Console.WriteLine("  search TEXT [--ids]                  Search names across the archive");
        Console.WriteLine("  info | verify | compact");
        Console.WriteLine("  add DISK-PATH [DISK-PATH ...]        Add into the current archive directory");
        Console.WriteLine("  extract ARCHIVE-PATH DEST [--overwrite]");
        Console.WriteLine("  delete ARCHIVE-PATH");
        Console.WriteLine("  quit                                Close the archive and exit");
        Console.WriteLine("Quote paths containing spaces. / starts at the archive root; .. selects the parent.");
        Console.WriteLine("Disk paths are relative to the process working directory. Shell expressions stay literal.");
        Console.WriteLine("Up/Down recalls session history; Left/Right/Home/End edits; Tab completes commands and paths.");
        Console.WriteLine("Ctrl-C cancels current work; Ctrl-D at an empty prompt exits. Encrypted opens request a hidden passphrase.");
        }

    private void Close() {
        _archive?.Dispose();
        _archive = null;
        _directory = Guid.Empty;
        }

    public void Dispose() {
        Close();
        }
    }

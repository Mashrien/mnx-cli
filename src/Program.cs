using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Mash.IO.Format.MNX.Archive.Codecs;
using Mash.IO.Format.MNX.Archive.Resume;
using Mash.IO.Format.MNX.Archive;
using Mash.IO.Format.MNX.Tools;
using Mash.IO.Format.MNX;
using Mash.UI.MNXFile.Tools;
using ArchiveFile = Mash.IO.Format.MNX.MNX;
using DiskFile = System.IO.File;
using VirtualFile = Mash.IO.Format.MNX.VFile.VFile;

namespace Mash.UI.MNXFile.CLI;

internal sealed class CommandApplication {
    private const long MiB = 1024L * 1024;
    private readonly ArchiveFile? _activeArchive;
    private readonly Guid _parentDirectory;
    public CommandApplication(ArchiveFile? activeArchive = null, Guid parentDirectory = default) {
        _activeArchive = activeArchive;
        _parentDirectory = parentDirectory;
        }

    public int Run(string[] args) {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") {
            Usage();
            return args.Length == 0 ? 1 : 0;
            }

        try {
            return args[0].ToLowerInvariant() switch {
                "create" => Create(Parse(args, "compression", "level", "layout", "chunk-size", "password-env")),
                "add" => Add(Parse(args, "password-env")),
                "resume" => Resume(Parse(args, "password-env")),
                "compare" => Compare(Parse(args, "password-env")),
                "list" => List(Parse(args, "password-env", "ids", "recursive")),
                "search" => Search(Parse(args, "password-env", "ids")),
                "info" => Info(Parse(args, "password-env")),
                "verify" => Verify(Parse(args, "password-env")),
                "extract" => Extract(Parse(args, "password-env", "overwrite")),
                "range" => Range(Parse(args, "password-env", "overwrite")),
                "delete" => Delete(Parse(args, "password-env")),
                "compact" => Compact(Parse(args, "password-env")),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'. Run 'help' for usage.")};
            }
        catch (Exception error) {
            Console.Error.WriteLine($"error: {error.Message}");
            if (Environment.GetEnvironmentVariable("MNX_CLI_TRACE") == "1") {
                Console.Error.WriteLine(error);
                }

            return 1;
            }
        }

    private static int Create(Arguments args) {
        using ArchiveFile archive = CreateArchive(args);
        Console.WriteLine($"Created {archive.ArchivePath} ({archive.Compression}, level {archive.CompressionLevel}, " + $"layout {archive.CompressionLayout}, chunk {archive.ChunkSize:N0}, " + $"encrypted: {archive.IsEncrypted}).");
        return 0;
        }

    internal static ArchiveFile CreateArchive(Arguments args, string? password = null) {
        args.Require(1, 1);
        string path = Path.GetFullPath(args.Positionals[0]);
        ArchiveCompression codec = ParseCompression(args.Get("compression") ?? "none");
        ArchiveFile archive = new ArchiveFile();
        try {
            archive.Options.ChunkSize = args.Get("chunk-size") is { } chunkSize ? int.Parse(chunkSize, CultureInfo.InvariantCulture) : 32768;
            archive.Options.Compression = codec;
            archive.Options.CompressionLayout = (args.Get("layout") ?? "continuous").ToLowerInvariant() switch {
                "continuous" => ArchiveCompressionLayout.Continuous,
                "chunked" => ArchiveCompressionLayout.IndependentChunks,
                _ => throw new ArgumentException("Compression layout must be continuous or chunked.")};
            archive.Options.CompressionLevel = args.Get("level") is { } level ? int.Parse(level, CultureInfo.InvariantCulture) : codec is ArchiveCompression.Snappy or ArchiveCompression.LZ4 ? 0 : 4;
            archive.Create(new FileInfo(path), password ?? Password(args));
            return archive;
            }
        catch {
            archive.Dispose();
            throw;
            }
        }

    private int Add(Arguments args) {
        args.Require(2, int.MaxValue);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        string[] inputs = args.Positionals.Skip(1).Select(Path.GetFullPath).Where(path => !FileSystemTools.IsLink(new FileInfo(path))).ToArray();
        CheckInputs(archive, inputs, _parentDirectory);
        archive.Options.OverrideByDefault = false;
        archive.Options.ProgressReportFrequency = 1000;
        using ProgressDisplay progress = new ProgressDisplay("add");
        foreach (string path in inputs) {
            ArchiveFile.ReturnCode queued = DiskFile.Exists(path) ? archive.Queue_AddFile(new FileInfo(path), _parentDirectory) : archive.Queue_AddDirectory(new DirectoryInfo(path), _parentDirectory);
            RequireSuccess(queued, "queue");
            }

        FileQueueItem[] items = archive.FileQueue.ToArray();
        int totalFiles = items.Count(item => !item.IsDirectory);
        long totalBytes = 0;
        foreach (FileQueueItem item in items) {
            if (!item.IsDirectory) {
                totalBytes = checked(totalBytes + (long)item.Length);
                }
            }

        progress.Configure(totalBytes, totalFiles);
        int itemIndex = 0;
        lease.OnProgress((_, report) => {
            if (itemIndex < items.Length && !items[itemIndex].IsDirectory) {
                progress.AdvanceFile(report.BytesComplete);
                }
            });
        Stopwatch? timer = Stopwatch.StartNew();
        RequireSuccess(archive.ProcessAddQueue((_, _, phase, _) => {
            FileQueueItem item = items[itemIndex];
            if (phase == 0 && !item.IsDirectory) {
                progress.StartFile(item.SourcePath, (long)item.Length);
                }

            if (phase == 1) {
                if (!item.IsDirectory) {
                    progress.CompleteFile();
                    }

                itemIndex++;
                }

            return false;
            }), "add");
        progress.Finish();
        Console.WriteLine($"Added {args.Positionals.Count - 1} input(s) in {timer.Elapsed}; " + $"archive size {Size(new FileInfo(archive.ArchivePath!).Length)}.");
        return 0;
        }

    private static int Resume(Arguments args) {
        args.Require(1, 2);
        FileInfo queueFile = new FileInfo(args.Positionals[0]);
        QueuePayload? saved = RemainingQueue.Read(queueFile);
        string archivePath = args.Positionals.Count == 2 ? Path.GetFullPath(args.Positionals[1]) : RemainingQueue.LocateArchive(queueFile, saved);
        string? password = Password(args) ?? ResumePassword.Read(queueFile, archivePath);
        using ArchiveFile archive = new ArchiveFile().Open(archivePath, password);
        archive.Options.OverrideByDefault = false;
        archive.Options.ProgressReportFrequency = 1000;
        archive.LoadRemainingQueue(queueFile);
        archive.ConsumeResumeKey(queueFile);
        FileQueueItem[]? items = archive.FileQueue.ToArray();
        using ProgressDisplay progress = new ProgressDisplay("resume");
        progress.Configure(
            items.Where(item => !item.IsDirectory).Sum(item => checked((long)item.Length)),
            items.Count(item => !item.IsDirectory)
            );
        int index = 0;
        int stop = 0;
        ConsoleCancelEventHandler onCancel = (_, args) => {
            args.Cancel = true;
            Interlocked.Exchange(ref stop, 1);
            };
        Console.CancelKeyPress += onCancel;
        archive.ProgressReport += (_, report) => {
            if (index < items.Length && !items[index].IsDirectory) {
                progress.AdvanceFile(report.BytesComplete);
                }
            };
        try {
            ArchiveFile.ReturnCode result = archive.ProcessAddQueue((_, _, phase, _) => {
                if (phase == 0) {
                    if (Volatile.Read(ref stop) != 0) {
                        return true;
                        }

                    if (!items[index].IsDirectory) {
                        progress.StartFile(items[index].SourcePath, checked((long)items[index].Length));
                        }
                    }
                else {
                    if (!items[index].IsDirectory) {
                        progress.CompleteFile();
                        }

                    index++;
                    }

                return Volatile.Read(ref stop) != 0;
                });
            if (Volatile.Read(ref stop) != 0) {
                string remaining = RemainingQueue.Save(archivePath, archive.FileQueue);
                Console.WriteLine("Stopped after the current file. Remaining queue: " + remaining);
                return 130;
                }

            RequireSuccess(result, "resume");
            progress.Finish();
            RemainingQueue.MarkCompleted(queueFile);
            Console.WriteLine("Resume complete: " + archivePath);
            return 0;
            }
        finally {
            Console.CancelKeyPress -= onCancel;
            }
        }

    private int Compare(Arguments args) {
        args.Require(2, int.MaxValue);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        Dictionary<Guid, string>? paths = Paths(archive);
        Dictionary<string, VirtualFile>? byPath = archive.Files.ToDictionary(file => paths[file.FileID], file => file, StringComparer.Ordinal);
        string[] inputs = args.Positionals.Skip(1).Select(Path.GetFullPath).Where(path => !FileSystemTools.IsLink(new FileInfo(path))).ToArray();
        string[] roots = inputs.Select(path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path))).Distinct(StringComparer.Ordinal).ToArray();
        VirtualFile[] selected = byPath.Where(pair => roots.Any(root => pair.Key == root || pair.Key.StartsWith(root + "/", StringComparison.Ordinal))).Select(pair => pair.Value).Where(file => !file.IsDirectory).ToArray();
        long totalBytes = 0;
        foreach (VirtualFile file in selected) {
            totalBytes = checked(totalBytes + (long)file.SizeOriginal);
            }

        using ProgressDisplay progress = new ProgressDisplay("compare", totalBytes, selected.Length);
        HashSet<string> expected = new HashSet<string>(StringComparer.Ordinal);
        long comparedBytes = 0;
        int comparedFiles = 0;
        Stopwatch? timer = Stopwatch.StartNew();
        foreach (string input in inputs) {
            string source = Path.GetFullPath(input);
            string rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (DiskFile.Exists(source)) {
                CompareFile(archive, byPath, expected, source, rootName, progress);
                comparedBytes += new FileInfo(source).Length;
                comparedFiles++;
                }
            else if (Directory.Exists(source)) {
                CompareDirectory(archive, byPath, expected, source, rootName, ref comparedFiles, ref comparedBytes, progress);
                }
            else {
                throw new FileNotFoundException($"Input does not exist: {source}");
                }
            }

        foreach (string root in roots) {
            foreach (string stored in byPath.Keys.Where(path => path == root || path.StartsWith(root + "/", StringComparison.Ordinal))) {
                if (!expected.Contains(stored)) {
                    throw new InvalidDataException($"Archive contains an extra entry: {stored}");
                    }
                }
            }

        progress.Finish();
        Console.WriteLine($"PASS: {comparedFiles:N0} real file(s), {Size(comparedBytes)} " + $"compared byte for byte in {timer.Elapsed}.");
        return 0;
        }

    private static void CheckInputs(ArchiveFile archive, string[] inputs, Guid parentDirectory = default) {
        HashSet<string> rootNames = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? storedNames = archive.Files.Where(file => file.OwnerID == (parentDirectory == Guid.Empty ? archive.RootDirectoryId : parentDirectory)).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string path in inputs) {
            if (!DiskFile.Exists(path) && !Directory.Exists(path)) {
                throw new FileNotFoundException($"Input does not exist: {path}");
                }

            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            if (string.IsNullOrEmpty(name) || name is "." or ".." || System.Text.Encoding.UTF8.GetByteCount(name) > ushort.MaxValue) {
                throw new ArgumentException($"Invalid root name for archive entry: {name}");
                }

            if (!rootNames.Add(name)) {
                throw new IOException($"Two inputs have the same root name: {name}");
                }

            if (storedNames.Contains(name)) {
                throw new IOException($"Archive already contains {name}; add will not replace existing entries.");
                }

            if (Directory.Exists(path) && IsInside(path, archive.ArchivePath!)) {
                throw new IOException("The archive is inside an input directory; move it outside before adding.");
                }
            }

        for (int i = 0; i < inputs.Length; i++) {
            for (int j = i + 1; j < inputs.Length; j++) {
                if ((Directory.Exists(inputs[i]) && IsInside(inputs[i], inputs[j])) || (Directory.Exists(inputs[j]) && IsInside(inputs[j], inputs[i]))) {
                    throw new IOException("Input paths overlap; choose each source file or directory only once.");
                    }
                }
            }
        }

    private static bool IsInside(string parent, string candidate) {
        string relative = Path.GetRelativePath(parent, candidate);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }

    private static void CompareDirectory(
        ArchiveFile archive,
        Dictionary<string, VirtualFile> byPath,
        HashSet<string> expected,
        string source,
        string rootName,
        ref int comparedFiles,
        ref long comparedBytes,
        ProgressDisplay progress
        ) {
        Stack<string> pending = new Stack<string>();
        pending.Push(source);
        while (pending.Count > 0) {
            string directory = pending.Pop();
            if (FileSystemTools.IsLink(new DirectoryInfo(directory))) {
                continue;
                }

            string relative = ArchivePath(Path.GetRelativePath(source, directory));
            string stored = relative == "." ? rootName : rootName + "/" + relative;
            ExpectEntry(byPath, expected, stored, true);
            foreach (string child in Directory.EnumerateDirectories(directory)) {
                pending.Push(child);
                }

            foreach (string file in Directory.EnumerateFiles(directory)) {
                if (FileSystemTools.IsLink(new FileInfo(file))) {
                    continue;
                    }

                string fileRelative = ArchivePath(Path.GetRelativePath(source, file));
                CompareFile(archive, byPath, expected, file, rootName + "/" + fileRelative, progress);
                comparedBytes += new FileInfo(file).Length;
                comparedFiles++;
                }
            }
        }

    private static void CompareFile(
        ArchiveFile archive,
        Dictionary<string, VirtualFile> byPath,
        HashSet<string> expected,
        string sourcePath,
        string storedPath,
        ProgressDisplay progress
        ) {
        VirtualFile entry = ExpectEntry(byPath, expected, storedPath, false);
        using FileStream source = DiskFile.OpenRead(sourcePath);
        using Stream stored = archive.OpenRead(entry.FileID);
        if ((ulong)source.Length != entry.SizeOriginal || stored.Length != source.Length) {
            throw new InvalidDataException($"Size differs: {storedPath} " + $"(source {source.Length:N0}, archive {entry.SizeOriginal:N0}).");
            }

        progress.StartFile(storedPath, source.Length);
        byte[] left = new byte[MiB];
        byte[] right = new byte[MiB];
        long offset = 0;
        while (offset < source.Length) {
            int count = (int)Math.Min(left.Length, source.Length - offset);
            source.ReadExactly(left.AsSpan(0, count));
            stored.ReadExactly(right.AsSpan(0, count));
            if (!left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) {
                int first = 0;
                while (left[first] == right[first]) {
                    first++;
                    }

                throw new InvalidDataException($"Content differs in {storedPath} at byte {offset + first:N0}.");
                }

            offset += count;
            progress.AdvanceFile(offset);
            }

        if (source.Length != offset || stored.ReadByte() != -1) {
            throw new InvalidDataException($"Length changed during comparison: {storedPath}");
            }

        progress.CompleteFile();
        }

    private static VirtualFile ExpectEntry(Dictionary<string, VirtualFile> byPath, HashSet<string> expected, string path, bool directory) {
        if (!expected.Add(path)) {
            throw new IOException($"Duplicate source path: {path}");
            }

        if (!byPath.TryGetValue(path, out VirtualFile? file)) {
            throw new FileNotFoundException($"Archive is missing {path}");
            }

        if (file.IsDirectory != directory) {
            throw new InvalidDataException($"File/directory type differs: {path}");
            }

        return file;
        }

    private int List(Arguments args) {
        args.Require(1, 2);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        Dictionary<Guid, string>? paths = Paths(archive);
        Guid parentId = archive.RootDirectoryId;
        string selectedPath = "";
        if (args.Positionals.Count == 2) {
            string requested = ArchivePath(args.Positionals[1]).Trim('/');
            if (requested is not ("" or ".") && requested != parentId.ToString()) {
                VirtualFile folder = Resolve(archive, requested);
                if (!folder.IsDirectory) {
                    throw new ArgumentException($"'{requested}' is not an archive directory.");
                    }

                parentId = folder.FileID;
                selectedPath = paths[folder.FileID];
                }
            }

        bool recursive = args.Has("recursive");
        IEnumerable<VirtualFile>? selected = archive.Files.Where(file => recursive ? selectedPath.Length == 0 || paths[file.FileID].StartsWith(selectedPath + "/", StringComparison.Ordinal) : file.OwnerID == parentId);
        return PrintEntries(
            archive,
            paths,
            selected,
            recursive,
            args.Has("ids"),
            args.Positionals.Count == 2 ? "/" + selectedPath : null
            );
        }

    private int Search(Arguments args) {
        args.Require(2, 2);
        string text = args.Positionals[1];
        if (text.Length == 0) {
            throw new ArgumentException("Search text must not be empty.");
            }

        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        Dictionary<Guid, string>? paths = Paths(archive);
        IEnumerable<VirtualFile>? selected = archive.Files.Where(file => file.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        return PrintEntries(archive, paths, selected, recursive: true, ids: args.Has("ids"), countNoun: "matches");
        }

    private static int PrintEntries(
        ArchiveFile archive,
        Dictionary<Guid, string> paths,
        IEnumerable<VirtualFile> selected,
        bool recursive,
        bool ids,
        string? heading = null,
        string countNoun = "entries"
        ) {
        (VirtualFile File, string Name, string? ParentPath)[]? entries = selected.OrderBy(file => paths[file.FileID], StringComparer.Ordinal).Select(file => (File: file, Name: ListDisplay.DisplayPath(file.Name), ParentPath: recursive ? PathCompression.CompressPath(paths[file.FileID]) : null)).ToArray();
        int nameWidth = Math.Max(20, entries.Select(entry => entry.Name.Length).DefaultIfEmpty(0).Max());
        int byteWidth = Math.Max(
            14,
            entries.Select(entry => entry.File.SizeOriginal.ToString(CultureInfo.InvariantCulture).Length).DefaultIfEmpty(0).Max()
            );
        Console.WriteLine();
        if (heading is not null) {
            Console.WriteLine(ListDisplay.DisplayPath(heading));
            }

        foreach ((VirtualFile? file, string? name, string? parentPath) in entries) {
            string row = ListDisplay.Row(
                file.IsDirectory,
                name,
                file.SizeOriginal,
                file.CreationDateUtc.ToLocalTime(),
                nameWidth,
                byteWidth,
                archive.Compression != ArchiveCompression.None,
                archive.IsEncrypted,
                parentPath
                );
            Console.WriteLine(ids ? $"{row}  {file.FileID}" : row);
            }

        Console.WriteLine($"{entries.Length} {countNoun}.");
        return 0;
        }

    private int Info(Arguments args) {
        args.Require(1, 1);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        ArchiveInfo info = archive.GetArchiveInfo();
        Console.WriteLine($"Archive:       {info.Path}");
        Console.WriteLine($"ID:            {info.ArchiveGuid}");
        Console.WriteLine($"Format:        {archive.FormatVersion}");
        Console.WriteLine($"Chunk size:    {archive.ChunkSize:N0} bytes");
        Console.WriteLine($"Compression:   {archive.Compression} (level {archive.CompressionLevel})");
        Console.WriteLine($"Layout:        {archive.CompressionLayout}");
        Console.WriteLine($"Encrypted:     {archive.IsEncrypted}");
        Console.WriteLine($"Files/dirs:    {info.FileCount}/{info.DirectoryCount}");
        Console.WriteLine($"On disk:       {Size(new FileInfo(info.Path).Length)}");
        Console.WriteLine($"Chunks:        {info.ChunkCount:N0} total, " + $"{info.IndexChunkCount:N0} front index, {info.FreeChunkCount:N0} free " + $"in {info.FreeRangeCount:N0} range(s)");
        return 0;
        }

    private int Verify(Arguments args) {
        args.Require(1, 1);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        Console.WriteLine($"Verifying {archive.GetArchiveInfo().ChunkCount:N0} chunks...");
        long totalBytes = archive.Files.Sum(file => (long)file.ChunkCount) * archive.ChunkSize;
        if (archive.CompressionLayout == ArchiveCompressionLayout.Continuous) {
            totalBytes = checked(totalBytes + archive.Files.Where(file => !file.IsDirectory).Sum(file => checked((long)file.SizeOriginal)));
            }

        using ProgressDisplay progress = new ProgressDisplay("verify", totalBytes, 0);
        archive.Options.ProgressReportFrequency = 1000;
        lease.OnProgress((_, report) => progress.AdvanceBytes(report.BytesComplete));
        Stopwatch? timer = Stopwatch.StartNew();
        ArchiveFile.ReturnCode result = archive.VerifyArchive();
        if (result == ArchiveFile.ReturnCode.Success) {
            progress.Finish();
            }

        Console.WriteLine($"{(result == ArchiveFile.ReturnCode.Success ? "PASS" : "FAIL")} " + $"({result}) in {timer.Elapsed}.");
        return result == ArchiveFile.ReturnCode.Success ? 0 : 2;
        }

    private int Extract(Arguments args) {
        args.Require(3, 3);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        VirtualFile file = Resolve(archive, args.Positionals[1]);
        string destination = Path.GetFullPath(args.Positionals[2]);
        bool overwrite = args.Has("overwrite");
        if (file.IsDirectory) {
            RequireSuccess(archive.ExtractDirectory(file.FileID, new DirectoryInfo(destination), overwrite), "extract");
            }
        else {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            RequireSuccess(archive.ExtractFile(file.FileID, new FileInfo(destination), overwrite), "extract");
            }

        Console.WriteLine($"Extracted {file.Name} to {destination}.");
        return 0;
        }

    private int Range(Arguments args) {
        args.Require(5, 5);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        VirtualFile file = Resolve(archive, args.Positionals[1]);
        if (file.IsDirectory) {
            throw new ArgumentException("Range reads require a file entry.");
            }

        long offset = ParseSize(args.Positionals[2]);
        long length = ParseSize(args.Positionals[3]);
        string destination = Path.GetFullPath(args.Positionals[4]);
        if (destination == archive.ArchivePath) {
            throw new InvalidOperationException("The output cannot be the archive itself.");
            }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using FileStream output = new FileStream(
            destination,
            args.Has("overwrite") ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
            );
        long copied = archive.CopyFileRange(file.FileID, output, offset, length);
        Console.WriteLine($"Copied {Size(copied)} from offset {Size(offset)} to {destination}.");
        return 0;
        }

    private int Delete(Arguments args) {
        args.Require(2, 2);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        VirtualFile file = Resolve(archive, args.Positionals[1]);
        RequireSuccess(archive.DeleteFileFromArchive(file.FileID), "delete");
        Console.WriteLine($"Deleted {file.Name}. Run compact to reclaim trailing space.");
        return 0;
        }

    private int Compact(Arguments args) {
        args.Require(1, 1);
        using ArchiveLease lease = Open(args);
        ArchiveFile archive = lease.Archive;
        long before = new FileInfo(archive.ArchivePath!).Length;
        Stopwatch? timer = Stopwatch.StartNew();
        RequireSuccess(archive.CompactArchive(), "compact");
        long after = new FileInfo(archive.ArchivePath!).Length;
        Console.WriteLine($"Compacted in {timer.Elapsed}: {Size(before)} -> {Size(after)} " + $"(reclaimed {Size(before - after)}).");
        return 0;
        }

    private ArchiveLease Open(Arguments args) {
        if (_activeArchive is not null) {
            return new ArchiveLease(_activeArchive, ownsArchive: false);
            }

        ArchiveFile archive = new ArchiveFile();
        try {
            archive.Open(Path.GetFullPath(args.Positionals[0]), Password(args));
            return new ArchiveLease(archive, ownsArchive: true);
            }
        catch {
            archive.Dispose();
            throw;
            }
        }
    internal static string? Password(Arguments args) {
        string? variable = args.Get("password-env");
        if (variable is null) {
            return null;
            }

        string? password = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(password)) {
            throw new ArgumentException($"Environment variable {variable} is empty or unset.");
            }

        return password;
        }

    private static string ArchivePath(string path) {
        return OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;
        }

    internal static VirtualFile Resolve(ArchiveFile archive, string nameOrId) {
        if (Guid.TryParse(nameOrId, out Guid id) && archive.GetFile(id) is { } byId) {
            return byId;
            }

        string path = ArchivePath(nameOrId).Trim('/');
        Dictionary<Guid, string>? paths = Paths(archive);
        return archive.Files.FirstOrDefault(file => paths[file.FileID] == path) ?? throw new FileNotFoundException($"No archive entry named '{nameOrId}'. Run list to see paths, or list --ids for IDs.");
        }

    internal static Dictionary<Guid, string> Paths(ArchiveFile archive) {
        Dictionary<Guid, VirtualFile>? files = archive.Files.ToDictionary(file => file.FileID);
        Dictionary<Guid, string> paths = new Dictionary<Guid, string>();
        foreach (VirtualFile file in files.Values) {
            if (paths.ContainsKey(file.FileID)) {
                continue;
                }

            Stack<VirtualFile> chain = new Stack<VirtualFile>();
            VirtualFile current = file;
            while (!paths.ContainsKey(current.FileID)) {
                chain.Push(current);
                if (current.OwnerID == archive.RootDirectoryId) {
                    break;
                    }

                current = files[current.OwnerID];
                }

            string parentPath = paths.GetValueOrDefault(current.FileID, "");
            while (chain.Count > 0) {
                VirtualFile part = chain.Pop();
                parentPath = parentPath.Length == 0 ? part.Name : parentPath + "/" + part.Name;
                paths[part.FileID] = parentPath;
                }
            }

        return paths;
        }

    private static void AttachProgress(ArchiveFile archive) {
        archive.Options.ProgressReportFrequency = 1024;
        string? lastName = null;
        long lastBytes = 0;
        archive.ProgressReport += (_, progress) => {
            if (progress.BytesMax <= 0) {
                return;
                }

            if (lastName != progress.FileName) {
                lastName = progress.FileName;
                lastBytes = 0;
                }

            if (progress.BytesComplete - lastBytes < 256 * MiB && progress.BytesComplete < progress.BytesMax) {
                return;
                }

            lastBytes = progress.BytesComplete;
            Console.Error.WriteLine($"  {progress.FileName}: " + $"{Size(progress.BytesComplete)} / {Size(progress.BytesMax)} " + $"({progress.Progress:P1})");
            };
        }

    private static ArchiveCompression ParseCompression(string text) => text.ToLowerInvariant() switch {
        "none" => ArchiveCompression.None,
        "brotli" => ArchiveCompression.Brotli,
        "snappy" => ArchiveCompression.Snappy,
        "lz4" => ArchiveCompression.LZ4,
        _ => throw new ArgumentException("Compression must be none, brotli, snappy, or lz4.")};
    private static long ParseSize(string text) {
        Match match = Regex.Match(text, @"^([0-9]+(?:\.[0-9]+)?)([A-Za-z]*)$");
        if (!match.Success) {
            throw new ArgumentException($"Invalid size '{text}'. Try 512MiB or 32GiB.");
            }

        decimal amount = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        long multiplier = match.Groups[2].Value.ToLowerInvariant() switch {
            "" or "b" => 1,
            "k" or "kib" => 1024L,
            "m" or "mib" => 1024L * 1024,
            "g" or "gib" => 1024L * 1024 * 1024,
            "t" or "tib" => 1024L * 1024 * 1024 * 1024,
            "kb" => 1000L,
            "mb" => 1000L * 1000,
            "gb" => 1000L * 1000 * 1000,
            "tb" => 1000L * 1000 * 1000 * 1000,
            _ => throw new ArgumentException($"Unknown size unit in '{text}'.")};
        decimal bytes = amount * multiplier;
        if (bytes != decimal.Truncate(bytes) || bytes < 0 || bytes > long.MaxValue) {
            throw new ArgumentOutOfRangeException(nameof(text), "Size must be a whole number of bytes within Int64 range.");
            }

        return (long)bytes;
        }

    private static string Size(long bytes) {
        const long KiB = 1024;
        const long GiB = 1024 * MiB;
        if (bytes < KiB) {
            return $"{bytes:N0} bytes";
            }

        string amount = bytes >= GiB ? $"{bytes / (double)GiB:N2} GiB" : bytes >= MiB ? $"{bytes / (double)MiB:N2} MiB" : $"{bytes / (double)KiB:N2} KiB";
        return $"{amount} ({bytes:N0} bytes)";
        }

    private static void RequireSuccess(ArchiveFile.ReturnCode result, string operation) {
        if (result != ArchiveFile.ReturnCode.Success) {
            throw new IOException($"{operation} failed: {result}");
            }
        }

    internal static Arguments Parse(string[] args, params string[] allowed) {
        HashSet<string>? permitted = allowed.ToHashSet(StringComparer.Ordinal);
        List<string> positionals = new List<string>();
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> switches = new HashSet<string>(StringComparer.Ordinal);
        bool afterDoubleDash = false;
        for (int i = 1; i < args.Length; i++) {
            string value = args[i];
            if (!afterDoubleDash && value == "--") {
                afterDoubleDash = true;
                }
            else if (!afterDoubleDash && value == "-r") {
                if (!permitted.Contains("recursive")) {
                    throw new ArgumentException("Unknown option -r.");
                    }

                if (!switches.Add("recursive")) {
                    throw new ArgumentException("Duplicate option -r.");
                    }
                }
            else if (!afterDoubleDash && value.StartsWith("--", StringComparison.Ordinal)) {
                string option = value[2..];
                if (!permitted.Contains(option)) {
                    throw new ArgumentException($"Unknown option {value}.");
                    }

                if (option is "overwrite" or "ids" or "recursive" or "encrypt") {
                    if (!switches.Add(option)) {
                        throw new ArgumentException($"Duplicate option {value}.");
                        }
                    }
                else {
                    if (values.ContainsKey(option) || i + 1 == args.Length) {
                        throw new ArgumentException($"Option {value} needs one value and may appear only once.");
                        }

                    values.Add(option, args[++i]);
                    }
                }
            else {
                positionals.Add(value);
                }
            }

        return new Arguments(positionals, values, switches);
        }

    private static void Usage() {
        Console.WriteLine("mnx - MNX archive CLI (file contents use bounded memory)");
        Console.WriteLine("Usage: mnx COMMAND ARCHIVE [OPTIONS]");
        Console.WriteLine("  -i | -interactive | --i | --interactive | /i [ARCHIVE]  Start mnxi");
        Console.WriteLine("  create ARCHIVE [--compression none|brotli|snappy|lz4] [--level 1..9] [--layout continuous|chunked] [--chunk-size 32768|4096]");
        Console.WriteLine("    New archives use 32768-byte chunks by default; 4096 selects smaller chunks.");
        Console.WriteLine("    Brotli uses level 1..9; Snappy and LZ4 use level 0. LZ4 requires continuous layout.");
        Console.WriteLine("  add ARCHIVE PATH [PATH ...]          Add files or directory trees");
        Console.WriteLine("  resume QUEUE [ARCHIVE]                Continue saved work; Ctrl+C keeps completed files and updates the queue");
        Console.WriteLine("  compare ARCHIVE PATH [PATH ...]      Compare archived bytes to real sources");
        Console.WriteLine("  list ARCHIVE [PATH] [-r] [--ids]       List directory entries; -r includes descendants and a path column");
        Console.WriteLine("    PATH selects a folder inside the archive. --recursive is an alias for -r.");
        Console.WriteLine("  search ARCHIVE TEXT [--ids]            Case-insensitive substring search of file/folder names");
        Console.WriteLine("  info ARCHIVE                          Show format and space usage");
        Console.WriteLine("  verify ARCHIVE                        Read and check every occupied chunk");
        Console.WriteLine("  extract ARCHIVE PATH-OR-ID DEST [--overwrite]");
        Console.WriteLine("  range ARCHIVE PATH-OR-ID OFFSET LENGTH DEST [--overwrite]");
        Console.WriteLine("  delete ARCHIVE PATH-OR-ID");
        Console.WriteLine("  compact ARCHIVE                       Fill holes and truncate the tail");
        Console.WriteLine();
        Console.WriteLine("Every archive command accepts --password-env VARIABLE for encrypted archives.");
        Console.WriteLine("SIZE accepts bytes, decimal GB, or binary GiB/MiB (32GiB, 500MiB, 4GB).");
        Console.WriteLine("Snappy and LZ4 have fixed level 0; Brotli accepts levels 1-9.");
        Console.WriteLine("Run 'mnx help', or 'dotnet run --project src/MNXFile.Cli.csproj -- help' from the project root.");
        }

    internal sealed record Arguments(List<string> Positionals, Dictionary<string, string> Values, HashSet<string> Switches) {
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public bool Has(string name) => Switches.Contains(name);
        public void Require(int minimum, int maximum) {
            if (Positionals.Count < minimum || Positionals.Count > maximum) {
                throw new ArgumentException("Wrong number of arguments. Run 'help' for usage.");
                }
            }
        }
    }

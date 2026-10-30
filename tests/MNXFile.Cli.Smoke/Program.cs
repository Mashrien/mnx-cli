using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

internal static class Program {
    private static string _cli = "";
    private static string _interactive = "";
    private static readonly string Password = "Windows integration あい password";

    private sealed class CommandResult {
        internal string Output = "";
        internal string Error = "";
        internal int ExitCode;
        }

    private static int Main(string[] arguments) {
        if (arguments.Length != 1) {
            Console.Error.WriteLine("Usage: MNXFile.Cli.Smoke RELEASE-DIRECTORY");
            return 1;
            }

        string directory = Path.GetFullPath(arguments[0]);
        string executableSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
        _cli = Path.Combine(directory, "mnx" + executableSuffix);
        _interactive = Path.Combine(directory, "mnxi" + executableSuffix);
        string work = Path.Combine(Path.GetTempPath(), "mnx-cli-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try {
            Check(System.IO.File.Exists(_cli) && System.IO.File.Exists(_interactive), "Both native CLI executables exist");
            Check(Run(_cli, ["--version"]).Output.Contains("mnx "), "Native CLI version");
            Check(Run(_interactive, ["--version"]).Output.Contains("mnxi "), "Native interactive version");
            foreach (string alias in new[] { "-i", "-interactive", "--i", "--interactive", "/i" }) {
                Check(Run(_cli, [alias, "--version"]).Output.Contains("mnxi "), "Interactive alias " + alias);
                }

            string source = Path.Combine(work, "あい source.with.periods");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "empty directory"));
            byte[] first = RandomNumberGenerator.GetBytes(300123);
            byte[] second = RandomNumberGenerator.GetBytes(700321);
            string firstName = "うえ multi.part.name.bin";
            string secondName = "Another file.bin";
            System.IO.File.WriteAllBytes(Path.Combine(source, firstName), first);
            System.IO.File.WriteAllBytes(Path.Combine(source, secondName), second);
            string sourceName = Path.GetFileName(source);
            foreach (string codec in new[] { "none", "brotli", "snappy", "lz4" }) {
                string archive = Path.Combine(work, "おか " + codec + ".mnx");
                string output = Path.Combine(work, "output " + codec);
                string[] authentication = codec == "none" ? [] : ["--password-env", "MNX_WINDOWS_SMOKE_PASSWORD"];
                string[] WithAuthentication(params string[] values) {
                    return values.Concat(authentication).ToArray();
                    }

                Run(_cli, WithAuthentication("create", archive, "--compression", codec, "--level", codec == "brotli" ? "1" : "0"));
                Run(_cli, WithAuthentication("add", archive, source));
                CommandResult listing = Run(_cli, WithAuthentication("list", archive, sourceName));
                Check(listing.Output.Contains(firstName) && listing.Output.Contains(secondName), "Exact Unicode filenames in listing");
                Check(Run(_cli, WithAuthentication("search", archive, "MULTI.PART")).Output.Contains(firstName), "Case-insensitive filename search");
                Run(_cli, WithAuthentication("compare", archive, source));
                Run(_cli, WithAuthentication("verify", archive));
                Run(_cli, WithAuthentication("extract", archive, sourceName, output));
                Check(System.IO.File.ReadAllBytes(Path.Combine(output, firstName)).AsSpan().SequenceEqual(first), "First extracted payload");
                Check(System.IO.File.ReadAllBytes(Path.Combine(output, secondName)).AsSpan().SequenceEqual(second), "Second extracted payload");
                string range = Path.Combine(work, "range " + codec + ".bin");
                Run(_cli, WithAuthentication("range", archive, sourceName + "/" + firstName, "123", "127", range));
                Check(System.IO.File.ReadAllBytes(range).AsSpan().SequenceEqual(first.AsSpan(123, 127)), "Continuous compressed range read");
                Run(_cli, WithAuthentication("info", archive));
                CommandResult interactive = Run(_interactive, [archive, .. authentication], "list\ncd \"" + sourceName + "\"\npwd\nsearch MULTI.PART\nverify\nquit\n");
                Check(interactive.Output.Contains(firstName) && !interactive.Error.Contains("error:", StringComparison.OrdinalIgnoreCase), "Interactive archive operations");

                string? interop = Environment.GetEnvironmentVariable("MNX_WINDOWS_INTEROP");
                if (!string.IsNullOrEmpty(interop)) {
                    Directory.CreateDirectory(interop);
                    System.IO.File.Copy(archive, Path.Combine(interop, codec + ".mnx"), true);
                    System.IO.File.WriteAllBytes(Path.Combine(interop, "first.bin"), first);
                    System.IO.File.WriteAllBytes(Path.Combine(interop, "second.bin"), second);
                    }

                Run(_cli, WithAuthentication("delete", archive, sourceName + "/" + secondName));
                long before = new FileInfo(archive).Length;
                Run(_cli, WithAuthentication("compact", archive));
                Check(new FileInfo(archive).Length <= before, "Compaction does not grow archive");
                Run(_cli, WithAuthentication("verify", archive));
                Check(!Run(_cli, WithAuthentication("list", archive, sourceName)).Output.Contains(secondName), "Deleted entry absent after compaction");
                Console.WriteLine("PASS: native CLI " + codec + ", Unicode, encryption, extraction, interactive session, range and compaction");
                }

            CommandResult invalid = Run(_cli, ["--interactive", "--unknown"], expectedExitCode: 1);
            Check(invalid.Error.Contains("error:"), "Interactive launcher preserves invalid-argument status");
            Console.WriteLine("PASS: native CLI and interactive aliases");
            return 0;
            }
        catch (Exception error) {
            Console.Error.WriteLine(error);
            return 1;
            }
        finally {
            Directory.Delete(work, true);
            }
        }

    private static CommandResult Run(string executable, string[] arguments, string? input = null, int expectedExitCode = 0) {
        ProcessStartInfo start = new ProcessStartInfo(executable) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
            };
        foreach (string argument in arguments) {
            start.ArgumentList.Add(argument);
            }
        start.Environment["MNX_WINDOWS_SMOKE_PASSWORD"] = Password;
        using Process process = Process.Start(start) ?? throw new IOException("Could not start native CLI.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (input != null) {
            process.StandardInput.Write(input);
            }
        process.StandardInput.Close();
        if (!process.WaitForExit(120000)) {
            process.Kill(true);
            throw new TimeoutException("Native CLI timed out: " + string.Join(" ", arguments));
            }
        CommandResult result = new CommandResult {
            Output = output.GetAwaiter().GetResult(),
            Error = error.GetAwaiter().GetResult(),
            ExitCode = process.ExitCode
            };
        if (result.ExitCode != expectedExitCode) {
            throw new InvalidOperationException(string.Join(" ", arguments) + " returned " + result.ExitCode + "\n" + result.Output + "\n" + result.Error);
            }
        return result;
        }

    private static void Check(bool condition, string description) {
        if (!condition) {
            throw new InvalidOperationException(description);
            }
        }
    }

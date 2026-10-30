using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModemKit.Interop;

internal static class Program
{
    private static readonly string[] SupportedProtocols = { "z32", "z16", "z4k", "z8k", "zedzap", "zedzip", "zstream", "zwindow", "y", "yg", "ychecksum", "xchecksum", "xcrc", "x1k" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("""
                ModemKit external lrzsz interoperability fixture
                  --protocol all|z32,z16,z4k,z8k,zedzap,zedzip,zstream,zwindow,y,yg,ychecksum,xchecksum,xcrc,x1k
                  --direction both|send|receive   (managed endpoint role)
                  --sizes 0,1,129,1025,8193,65537
                  --lrzsz-directory PATH         (directory containing sz and rz)
                  --artifacts PATH               (default: artifacts)
                  --wsl-distribution NAME        (Windows only; default: Ubuntu)
                  --timeout-seconds NUMBER       (per case; default: 30)
                  --idle-timeout-ms NUMBER        (protocol retry timer; default: 2000)
                YMODEM-G and YMODEM checksum run native sz -> managed receiver only.
                """);
            return 0;
        }

        Settings settings;
        try
        {
            settings = Settings.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var runName = "interop-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var runDirectory = Path.Combine(settings.ArtifactsDirectory, runName);
        Directory.CreateDirectory(runDirectory);
        Console.WriteLine("Evidence: " + runDirectory);
        var results = new List<CaseResult>();
        foreach (var protocol in settings.Protocols)
        {
            foreach (var sender in settings.SenderRoles)
            {
                if ((protocol == "yg" || protocol == "ychecksum") && sender)
                {
                    Console.WriteLine("SKIP " + protocol + " send: native rz does not request this transfer mode.");
                    continue;
                }

                foreach (var size in settings.Sizes)
                {
                    var result = await RunCase(settings, runDirectory, protocol, sender, size).ConfigureAwait(false);
                    results.Add(result);
                    Console.WriteLine(
                        $"{(result.Passed ? "PASS" : "FAIL")} {protocol} {(sender ? "send" : "receive")} {size}: {result.Detail}");
                    await File.WriteAllTextAsync(
                        Path.Combine(runDirectory, "results.json"),
                        JsonSerializer.Serialize(results, JsonOptions)).ConfigureAwait(false);
                }
            }
        }

        var failures = results.Count(result => !result.Passed);
        Console.WriteLine($"{results.Count - failures}/{results.Count} external interoperability cases passed.");
        return failures == 0 && results.Count > 0 ? 0 : 1;
    }

    private static async Task<CaseResult> RunCase(Settings settings, string root, string protocol, bool sender, int size)
    {
        var directory = Path.Combine(root, $"{protocol}-{(sender ? "send" : "receive")}-{size}");
        var sourceDirectory = Path.Combine(directory, "source");
        var receiveDirectory = Path.Combine(directory, "received");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(receiveDirectory);
        var source = Enumerable.Range(0, size).Select(index => (byte)index).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "payload.bin"), source).ConfigureAwait(false);
        using var endpoint = Endpoint.Create(protocol, source, sender);
        var arguments = NativeArguments(protocol, sender);
        var workingDirectory = sender ? receiveDirectory : sourceDirectory;
        var start = settings.CreateProcess(sender ? "rz" : "sz", workingDirectory, arguments);
        var command = new
        {
            start.FileName,
            Arguments = start.ArgumentList.ToArray(),
            start.WorkingDirectory,
            settings.TimeoutSeconds,
            settings.IdleTimeoutMilliseconds
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "command.json"),
            JsonSerializer.Serialize(command, JsonOptions)).ConfigureAwait(false);
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var clock = Stopwatch.StartNew();
        var stderr = Task.FromResult(string.Empty);
        string detail;
        var passed = false;
        byte[]? received = null;
        int? exitCode = null;
        await using var sentTrace = File.Create(Path.Combine(directory, "to-native.bin"));
        await using var receivedTrace = File.Create(Path.Combine(directory, "from-native.bin"));
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start native process.");
            }

            stderr = process.StandardError.ReadToEndAsync();
            var input = process.StandardInput.BaseStream;
            var output = process.StandardOutput.BaseStream;
            await Send(endpoint.Start(), input, sentTrace, deadline.Token).ConfigureAwait(false);
            var buffer = new byte[4096];
            var read = output.ReadAsync(buffer, deadline.Token).AsTask();
            while (true)
            {
                var timer = Task.Delay(settings.IdleTimeoutMilliseconds, deadline.Token);
                var completed = await Task.WhenAny(read, timer).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (completed == read || read.IsCompleted)
                {
                    var count = await read.ConfigureAwait(false);
                    if (count == 0)
                    {
                        break;
                    }

                    var bytes = buffer.AsSpan(0, count).ToArray();
                    await receivedTrace.WriteAsync(bytes, deadline.Token).ConfigureAwait(false);
                    await Send(endpoint.Feed(bytes), input, sentTrace, deadline.Token).ConfigureAwait(false);
                    read = output.ReadAsync(buffer, deadline.Token).AsTask();
                }
                else
                {
                    await Send(endpoint.Timeout(), input, sentTrace, deadline.Token).ConfigureAwait(false);
                }
            }

            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            exitCode = process.ExitCode;
            if (!endpoint.Completed)
            {
                throw new InvalidOperationException($"Native process exited with managed phase {endpoint.State}.");
            }

            if (exitCode != 0)
            {
                throw new InvalidOperationException("Native process exited with code " + exitCode);
            }

            var receivedPath = Path.Combine(receiveDirectory, "payload.bin");
            if (sender)
            {
                received = await File.ReadAllBytesAsync(receivedPath, deadline.Token).ConfigureAwait(false);
            }
            else
            {
                received = endpoint.Received;
                await File.WriteAllBytesAsync(receivedPath, received, deadline.Token).ConfigureAwait(false);
            }

            VerifyBytes(protocol, source, received);
            passed = true;
            detail = $"exact source bytes verified; received {received.Length} bytes; native exit 0";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            detail = exception is OperationCanceledException
                ? $"wall-clock deadline ({settings.TimeoutSeconds}s) exceeded; phase {endpoint.State}"
                : exception.Message + "; phase " + endpoint.State;
        }
        finally
        {
            try
            {
                if (process.Id != 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }

                exitCode ??= process.ExitCode;
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                // Startup may fail before the Process has an ID; the case already records that error.
            }

            try
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "native-stderr.txt"),
                    await stderr.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or IOException)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(directory, "native-stderr.txt"),
                    "Native stderr did not finish within the case deadline.").ConfigureAwait(false);
            }
        }

        return new CaseResult(
            protocol,
            sender ? "send" : "receive",
            size,
            passed,
            detail,
            exitCode,
            clock.ElapsedMilliseconds,
            Convert.ToHexString(SHA256.HashData(source)),
            received == null ? null : Convert.ToHexString(SHA256.HashData(received)),
            directory);
    }

    private static async Task Send(
        IReadOnlyList<byte[]> writes, Stream input, Stream trace, CancellationToken cancellationToken)
    {
        foreach (var bytes in writes)
        {
            await trace.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        if (writes.Count > 0)
        {
            await input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void VerifyBytes(string protocol, byte[] expected, byte[] actual)
    {
        if (protocol.StartsWith('x'))
        {
            if (actual.Length < expected.Length || !actual.AsSpan(0, expected.Length).SequenceEqual(expected))
            {
                throw new InvalidDataException("XMODEM source prefix does not match.");
            }

            if (actual.Length - expected.Length > 1024 || actual.AsSpan(expected.Length).ContainsAnyExcept((byte)0x1A))
            {
                throw new InvalidDataException("XMODEM trailing bytes are not bounded SUB padding.");
            }
        }
        else if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException($"Received bytes do not match: expected {expected.Length}, actual {actual.Length}.");
        }
    }

    private static List<string> NativeArguments(string protocol, bool managedSender)
    {
        var arguments = new List<string> { "-b", "-q" };
        if (managedSender)
        {
            arguments.Add("-y");
        }

        switch (protocol)
        {
            case "z16":
                if (!managedSender)
                {
                    arguments.Add("-o");
                }

                break;
            case "z4k":
            case "z8k":
            case "zedzap":
                if (!managedSender)
                {
                    arguments.Add(protocol == "z4k" ? "--start-4k" : "--start-8k");
                    arguments.Add("-L");
                    arguments.Add(protocol == "z4k" ? "4096" : "8192");
                }

                break;
            case "zwindow":
                if (!managedSender)
                {
                    arguments.Add("-w");
                    arguments.Add("16384");
                }

                break;
            case "y":
            case "yg":
            case "ychecksum":
                arguments.Add("--ymodem");
                break;
            case "xchecksum":
            case "xcrc":
            case "x1k":
                arguments.Add("-X");
                if (managedSender && protocol != "xchecksum")
                {
                    arguments.Add("-c");
                }
                else if (!managedSender && protocol == "x1k")
                {
                    arguments.Add("-k");
                }

                break;
        }

        if (!managedSender || protocol.StartsWith('x'))
        {
            arguments.Add("payload.bin");
        }

        return arguments;
    }

    private sealed record CaseResult(
        string Protocol,
        string ManagedRole,
        int SourceLength,
        bool Passed,
        string Detail,
        int? NativeExitCode,
        long ElapsedMilliseconds,
        string SourceSha256,
        string? ReceivedSha256,
        string EvidenceDirectory);

    private sealed class Settings
    {
        public required string[] Protocols { get; init; }

        public required bool[] SenderRoles { get; init; }

        public required int[] Sizes { get; init; }

        public required string LrzszDirectory { get; init; }

        public required string ArtifactsDirectory { get; init; }

        public required string WslDistribution { get; init; }

        public required int TimeoutSeconds { get; init; }

        public required int IdleTimeoutMilliseconds { get; init; }

        public static Settings Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Options require --name value pairs. Use --help.");
                }

                values.Add(args[index], args[index + 1]);
            }

            var validNames = new[]
            {
                "--protocol", "--direction", "--sizes", "--lrzsz-directory",
                "--artifacts", "--wsl-distribution", "--timeout-seconds", "--idle-timeout-ms"
            };
            var unknown = values.Keys.Except(validNames, StringComparer.Ordinal).FirstOrDefault();
            if (unknown != null)
            {
                throw new ArgumentException("Unknown option: " + unknown);
            }

            var protocolText = values.GetValueOrDefault("--protocol", "all");
            var protocols = protocolText == "all" ? SupportedProtocols : protocolText.Split(',');
            if (protocols.Length == 0 || protocols.Any(protocol => !SupportedProtocols.Contains(protocol, StringComparer.Ordinal)))
            {
                throw new ArgumentException("Unknown protocol. Use --help for supported names.");
            }

            var roles = values.GetValueOrDefault("--direction", "both") switch
            {
                "both" => new[] { true, false },
                "send" => new[] { true },
                "receive" => new[] { false },
                _ => throw new ArgumentException("Direction must be both, send or receive.")
            };
            var sizes = values.GetValueOrDefault("--sizes", "0,1,129,1025,8193,65537").Split(',')
                .Select(value => ParseNumber(value, 0, 16 * 1024 * 1024, "sizes")).Distinct().ToArray();
            var nativeDefault = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.CurrentDirectory, "artifacts", "lrzsz", "extracted", "usr", "bin")
                : "/usr/bin";
            return new Settings
            {
                Protocols = protocols.Distinct(StringComparer.Ordinal).ToArray(),
                SenderRoles = roles,
                Sizes = sizes,
                LrzszDirectory = Path.GetFullPath(values.GetValueOrDefault("--lrzsz-directory", nativeDefault)),
                ArtifactsDirectory = Path.GetFullPath(values.GetValueOrDefault("--artifacts", "artifacts")),
                WslDistribution = values.GetValueOrDefault("--wsl-distribution", "Ubuntu"),
                TimeoutSeconds = ParseNumber(values.GetValueOrDefault("--timeout-seconds", "30"), 1, 600, "timeout-seconds"),
                // lrzsz needs a one-second quiet interval before accepting X/Y EOT.
                IdleTimeoutMilliseconds = ParseNumber(values.GetValueOrDefault("--idle-timeout-ms", "2000"), 100, 60000, "idle-timeout-ms")
            };
        }

        public ProcessStartInfo CreateProcess(string executable, string workingDirectory, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "wsl.exe" : Path.Combine(LrzszDirectory, executable),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows())
            {
                foreach (var argument in new[]
                {
                    "-d", WslDistribution, "--cd", ToWslPath(workingDirectory), "--",
                    ToWslPath(Path.Combine(LrzszDirectory, executable))
                })
                {
                    start.ArgumentList.Add(argument);
                }
            }

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            return start;
        }

        private static int ParseNumber(string value, int minimum, int maximum, string name)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || number < minimum || number > maximum)
            {
                throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
            }

            return number;
        }

        private static string ToWslPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Length < 3 || fullPath[1] != ':' || (fullPath[2] != '\\' && fullPath[2] != '/'))
            {
                throw new ArgumentException("Windows native paths must use a drive-letter path for WSL.");
            }

            return "/mnt/" + char.ToLowerInvariant(fullPath[0]) + fullPath[2..].Replace('\\', '/');
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DebugBundle.Transport;

public sealed class FileEventTransport : IEventTransport
{
    private static readonly Regex InvalidServiceCharacters = new("[^A-Za-z0-9._-]+", RegexOptions.Compiled);
    private readonly string _root;
    private readonly object _sync = new();
    private ulong _sequence;

    public FileEventTransport(string root)
    {
        _root = ValidateRoot(root);
        Directory.CreateDirectory(_root);
        EnsureOwnerOnlyDirectory(_root);
    }

    public async Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequence = NextSequence();
        var service = SanitizeServiceName(request.Events.FirstOrDefault()?.Service.Name);
        var finalPath = Path.Combine(_root, $"{timestamp}-{sequence}-{service}.events.json");
        var tempPath = finalPath + ".tmp-" + RandomHex(8);

        if (File.Exists(finalPath) && File.GetAttributes(finalPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Refusing to write through a reparse-point target.");
        }

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, request.Events, JsonSerialization.Options, cancellationToken).ConfigureAwait(false);
            }

            EnsureOwnerOnlyFile(tempPath);
            File.Move(tempPath, finalPath);
            EnsureOwnerOnlyFile(finalPath);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        return new EventTransportResult
        {
            StatusCode = 202,
            WrittenFilePath = finalPath
        };
    }

    private ulong NextSequence()
    {
        lock (_sync)
        {
            _sequence++;
            return _sequence;
        }
    }

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Events directory is required.", nameof(root));
        }

        var fullPath = Path.GetFullPath(root);
        var current = fullPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("Symlinked or reparse-point events directories are not allowed.");
                }
            }

            var parent = Directory.GetParent(current);
            if (parent == null || parent.FullName == current)
            {
                break;
            }

            current = parent.FullName;
        }

        return fullPath;
    }

    private static string SanitizeServiceName(string? serviceName)
    {
        var normalized = InvalidServiceCharacters.Replace((serviceName ?? "service").Trim(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "service" : normalized;
    }

    private static string RandomHex(int byteCount)
    {
        var bytes = new byte[byteCount];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
#if NET8_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
#endif
    }

    private static void EnsureOwnerOnlyFile(string path)
    {
#if NET8_0_OR_GREATER
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
#endif
    }
}

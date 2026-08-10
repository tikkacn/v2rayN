using System.Diagnostics;
using System.IO.Compression;

namespace AmazTool;

internal class UpgradeApp
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static void Upgrade(string fileName)
    {
        Console.WriteLine($"{Resx.Resource.StartUnzipping}\n{fileName}");

        Utils.Waiting(5);

        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException(Resx.Resource.UpgradeFileNotFound, fileName);
        }

        var installRoot = Path.GetFullPath(Utils.StartupPath());
        Console.WriteLine($"Installation directory: {installRoot}");

        TerminateCurrentApp(installRoot);

        Console.WriteLine(Resx.Resource.StartUnzipping);
        using (var archive = ZipFile.OpenRead(fileName))
        {
            var archiveRoot = DetectArchiveRoot(archive);
            Console.WriteLine(archiveRoot.Length == 0
                ? "Archive layout: files at ZIP root"
                : $"Archive layout: wrapper directory '{archiveRoot.TrimEnd('/')}'");

            ExtractArchive(archive, archiveRoot, installRoot);
        }

        Console.WriteLine(Resx.Resource.SuccessUpgrade);
        Console.WriteLine(Resx.Resource.Restartv2rayN);
        Utils.Waiting(2);
        Utils.StartV2RayN();
    }

    public static void ValidateArchive(string fileName)
    {
        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException(Resx.Resource.UpgradeFileNotFound, fileName);
        }

        using var archive = ZipFile.OpenRead(fileName);
        var archiveRoot = DetectArchiveRoot(archive);
        var mappings = GetMappedEntries(archive, archiveRoot);
        var installRoot = Path.GetFullPath(Utils.StartupPath());

        Console.WriteLine(archiveRoot.Length == 0
            ? "Archive layout: files at ZIP root"
            : $"Archive layout: wrapper directory '{archiveRoot.TrimEnd('/')}'");

        foreach (var mapping in mappings)
        {
            _ = GetSafeOutputPath(installRoot, mapping.RelativePath);
            Console.WriteLine($"MAP {mapping.Entry.FullName} -> {mapping.RelativePath}");
        }
    }

    private static void TerminateCurrentApp(string installRoot)
    {
        Console.WriteLine(Resx.Resource.TryTerminateProcess);
        var expectedExecutable = Path.GetFullPath(Path.Combine(installRoot, $"{Utils.V2rayN}.exe"));

        foreach (var process in Process.GetProcessesByName(Utils.V2rayN))
        {
            try
            {
                var processPath = process.MainModule?.FileName;
                if (string.IsNullOrEmpty(processPath) ||
                    !string.Equals(Path.GetFullPath(processPath), expectedExecutable, PathComparison))
                {
                    continue;
                }

                process.Kill(true);
                if (!process.WaitForExit(5000))
                {
                    throw new IOException($"Timed out while stopping {expectedExecutable}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Resx.Resource.FailedTerminateProcess} {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string DetectArchiveRoot(ZipArchive archive)
    {
        var files = archive.Entries
            .Select(entry => NormalizeEntryPath(entry.FullName))
            .Where(path => path.Length > 0 && !path.EndsWith('/'))
            .ToList();

        var mainExecutables = files
            .Where(path => string.Equals(GetArchiveFileName(path), "v2rayN.exe", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mainExecutables.Count != 1)
        {
            throw new InvalidDataException(
                $"The update archive must contain exactly one v2rayN.exe; found {mainExecutables.Count}.");
        }

        var mainExecutable = mainExecutables[0];
        ValidateArchivePath(mainExecutable);

        var slash = mainExecutable.LastIndexOf('/');
        var root = slash >= 0 ? mainExecutable[..(slash + 1)] : string.Empty;
        var updaterPath = $"{root}AmazTool.exe";

        if (!files.Any(path => string.Equals(path, updaterPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The update archive does not contain AmazTool.exe beside v2rayN.exe.");
        }

        return root;
    }

    private static void ExtractArchive(ZipArchive archive, string archiveRoot, string installRoot)
    {
        var updaterPath = Path.GetFullPath(Utils.GetExePath());
        var updaterBackupPath = $"{updaterPath}.tmp";
        File.Delete(updaterBackupPath);

        var extractedMainExecutable = false;

        foreach (var mapping in GetMappedEntries(archive, archiveRoot))
        {
            var entry = mapping.Entry;
            var relativePath = mapping.RelativePath;
            var outputPath = GetSafeOutputPath(installRoot, relativePath);
            Console.WriteLine($"{entry.FullName} -> {outputPath}");

            if (relativePath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) && File.Exists(outputPath))
            {
                Console.WriteLine($"Skip existing core file: {relativePath}");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var replacingUpdater = string.Equals(outputPath, updaterPath, PathComparison);
            if (replacingUpdater)
            {
                File.Move(updaterPath, updaterBackupPath);
            }

            if (!TryExtractToFile(entry, outputPath))
            {
                if (replacingUpdater && !File.Exists(updaterPath) && File.Exists(updaterBackupPath))
                {
                    File.Move(updaterBackupPath, updaterPath);
                }

                throw new IOException($"Failed to replace update file after retries: {relativePath}");
            }

            if (string.Equals(relativePath, "v2rayN.exe", StringComparison.OrdinalIgnoreCase))
            {
                extractedMainExecutable = true;
            }
        }

        if (!extractedMainExecutable)
        {
            throw new InvalidDataException("v2rayN.exe was found but was not extracted.");
        }
    }

    private static List<(ZipArchiveEntry Entry, string RelativePath)> GetMappedEntries(
        ZipArchive archive,
        string archiveRoot)
    {
        var mappings = new List<(ZipArchiveEntry Entry, string RelativePath)>();
        var mappedPaths = new HashSet<string>(PathComparer);

        foreach (var entry in archive.Entries)
        {
            var entryPath = NormalizeEntryPath(entry.FullName);
            if (entryPath.Length == 0 || entryPath.EndsWith('/'))
            {
                continue;
            }

            if (archiveRoot.Length > 0 && !entryPath.StartsWith(archiveRoot, PathComparison))
            {
                continue;
            }

            var relativePath = archiveRoot.Length > 0 ? entryPath[archiveRoot.Length..] : entryPath;
            ValidateArchivePath(relativePath);
            if (!mappedPaths.Add(relativePath))
            {
                throw new InvalidDataException($"Duplicate update path: {relativePath}");
            }

            mappings.Add((entry, relativePath));
        }

        return mappings;
    }

    private static string NormalizeEntryPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string GetArchiveFileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"Unsafe update path: {path}");
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Unsafe update path: {path}");
        }
    }

    private static string GetSafeOutputPath(string installRoot, string relativePath)
    {
        var localPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var outputPath = Path.GetFullPath(Path.Combine(installRoot, localPath));
        var rootWithSeparator = installRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!outputPath.StartsWith(rootWithSeparator, PathComparison))
        {
            throw new InvalidDataException($"Update path escapes the installation directory: {relativePath}");
        }

        return outputPath;
    }

    private static bool TryExtractToFile(ZipArchiveEntry entry, string outputPath)
    {
        const int retryCount = 5;
        const int delayMs = 1000;

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            try
            {
                entry.ExtractToFile(outputPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Replace attempt {attempt}/{retryCount} failed for {outputPath}: {ex.Message}");
                if (attempt < retryCount)
                {
                    Thread.Sleep(delayMs * attempt);
                }
            }
        }

        return false;
    }
}

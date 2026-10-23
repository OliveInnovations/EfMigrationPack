using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace EfMigrationPack;

internal sealed class PackManifest
{
    public int FormatVersion { get; set; } = 1;

    public string PackId { get; set; } = string.Empty;

    public string Context { get; set; } = string.Empty;

    public string ToolVersion { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary>Original migration ids, in the order they were packed.</summary>
    public List<string> Steps { get; set; } = new();

    public List<ManifestFile> Files { get; set; } = new();
}

internal sealed class ManifestFile
{
    /// <summary>Path relative to the project folder, with forward slashes.</summary>
    public string Path { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class ArchivedFile
{
    public ArchivedFile(byte[] bytes, DateTime lastWriteTime)
    {
        Bytes = bytes;
        LastWriteTime = lastWriteTime;
    }

    public byte[] Bytes { get; }

    public DateTime LastWriteTime { get; }
}

internal sealed class ArchiveContents
{
    public ArchiveContents(PackManifest manifest, Dictionary<string, ArchivedFile> files)
    {
        Manifest = manifest;
        Files = files;
    }

    public PackManifest Manifest { get; }

    /// <summary>Archived files keyed by project-relative path.</summary>
    public Dictionary<string, ArchivedFile> Files { get; }
}

/// <summary>
/// The zip holding a pack's original migration files. Entries are stored at their path relative to the
/// project folder, so unzipping it by hand at the project root also puts every file back in place.
/// </summary>
internal static class PackArchive
{
    public const string ManifestEntry = "ef-pack.manifest.json";

    private static readonly DateTime MinZipTime = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
    private static readonly DateTime MaxZipTime = new(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);

    /// <summary>Writes the archive, reads it back and checks every byte before it is moved into place.</summary>
    public static void Create(string zipPath, string projectDir, IReadOnlyList<string> files, PackManifest manifest)
    {
        var temp = zipPath + ".tmp";
        if (File.Exists(temp))
        {
            File.Delete(temp);
        }

        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var relative = Relative(projectDir, file);
                var bytes = File.ReadAllBytes(file);
                originals[relative] = bytes;
                manifest.Files.Add(new ManifestFile { Path = relative, Sha256 = Hash(bytes) });

                var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(Clamp(File.GetLastWriteTime(file)));
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }

            var manifestEntry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            using var manifestStream = manifestEntry.Open();
            JsonSerializer.Serialize(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        var check = Read(temp);
        foreach (var original in originals)
        {
            if (!check.Files.TryGetValue(original.Key, out var archived) || !archived.Bytes.AsSpan().SequenceEqual(original.Value))
            {
                File.Delete(temp);
                throw new PackException($"Verifying {zipPath} failed for {original.Key}; nothing was changed.");
            }
        }

        File.Move(temp, zipPath);
    }

    public static ArchiveContents Read(string zipPath)
    {
        PackManifest? manifest = null;
        var files = new Dictionary<string, ArchivedFile>(StringComparer.Ordinal);

        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue; // folder entry
                }

                var bytes = ReadAll(entry);
                if (entry.FullName == ManifestEntry)
                {
                    manifest = JsonSerializer.Deserialize<PackManifest>(bytes);
                }
                else
                {
                    files[entry.FullName] = new ArchivedFile(bytes, entry.LastWriteTime.DateTime);
                }
            }
        }

        if (manifest is null)
        {
            throw new PackException($"{zipPath} has no {ManifestEntry}; it was not created by ef-pack.");
        }

        foreach (var file in manifest.Files)
        {
            if (!files.TryGetValue(file.Path, out var archived) || Hash(archived.Bytes) != file.Sha256)
            {
                throw new PackException($"{zipPath} is damaged: {file.Path} is missing or does not match its checksum.");
            }
        }

        if (files.Count != manifest.Files.Count)
        {
            throw new PackException($"{zipPath} contains files that are not in its manifest.");
        }

        return new ArchiveContents(manifest, files);
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Relative(string projectDir, string file)
    {
        var relative = Path.GetRelativePath(projectDir, file);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            throw new PackException($"{file} is outside the project folder {projectDir}.");
        }

        return Code.NormalizePath(relative);
    }

    /// <summary>Maps an archived path back to the project folder, refusing anything that would escape it.</summary>
    public static string Resolve(string projectDir, string relative)
    {
        var root = Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackException($"Archived path {relative} points outside the project folder.");
        }

        return full;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static DateTime Clamp(DateTime time) =>
        time < MinZipTime ? MinZipTime : time > MaxZipTime ? MaxZipTime : time;
}

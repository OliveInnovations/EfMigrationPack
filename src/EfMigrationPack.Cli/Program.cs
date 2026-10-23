namespace EfMigrationPack;

internal static class Program
{
    private const string Usage =
@"usage:
  dotnet ef-pack pack   [--project <folder|.csproj>] [--context <DbContext>] [--single-file] [--dry-run]
  dotnet ef-pack unpack [--project <folder|.csproj>] [--context <DbContext>] [--all] [--force] [--dry-run]

  pack       Packs every migration that is not already packed into a new packed migration
             (PackedMigrationsNNNN) and moves the original files into PackedMigrationsNNNN.zip.
  unpack     Restores the newest pack's original migrations from its zip and removes the pack.
             --all unpacks every pack, newest first.

  --project      Project folder or .csproj (default: current folder).
  --context      DbContext name; required when the project has migrations for several contexts.
  --single-file  pack: put every step in the pack's main file instead of a .Steps folder.
  --force        unpack: continue even if the pack was edited after packing or files would be overwritten.
  --dry-run      Show what would change without changing anything.

  pack-migrations and unpack-migrations are accepted too, as are compress and expand from version 1.0.";

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "-?" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var command = args[0].ToLowerInvariant() switch
        {
            "pack" or "pack-migrations" or "compress" or "compress-migrations" => Command.Pack,
            "unpack" or "unpack-migrations" or "expand" or "expand-migrations" => Command.Unpack,
            _ => (Command?)null,
        };

        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        Options options;
        try
        {
            options = Options.Parse(command.Value, args.Skip(1).ToList());
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return command == Command.Pack ? Packer.Run(options) : Unpacker.Run(options);
        }
        catch (PackException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }
}

internal enum Command
{
    Pack,
    Unpack,
}

internal sealed class Options
{
    public string ProjectDir { get; private set; } = Directory.GetCurrentDirectory();

    public string? Context { get; private set; }

    public bool SingleFile { get; private set; }

    public bool DryRun { get; private set; }

    public bool All { get; private set; }

    public bool Force { get; private set; }

    public static Options Parse(Command command, IReadOnlyList<string> args)
    {
        var o = new Options();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{arg} needs a value.");

            switch (arg)
            {
                case "--project":
                    o.ProjectDir = ResolveProjectDir(Next());
                    break;
                case "--context":
                    o.Context = Next();
                    break;
                case "--dry-run":
                    o.DryRun = true;
                    break;
                case "--single-file" when command == Command.Pack:
                    o.SingleFile = true;
                    break;
                case "--all" when command == Command.Unpack:
                    o.All = true;
                    break;
                case "--force" when command == Command.Unpack:
                    o.Force = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}' for {command.ToString().ToLowerInvariant()}.");
            }
        }

        o.ProjectDir = Path.GetFullPath(o.ProjectDir);
        return o;
    }

    private static string ResolveProjectDir(string value)
    {
        if (File.Exists(value) && value.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(Path.GetFullPath(value))!;
        }

        if (Directory.Exists(value))
        {
            return value;
        }

        throw new ArgumentException($"Project not found: {value}");
    }
}

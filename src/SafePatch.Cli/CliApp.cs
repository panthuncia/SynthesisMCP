using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda;
using SafePatch.Authoring;
using SafePatch.Authoring.Output;
using SafePatch.Authoring.Query;
using SafePatch.Authoring.Sources;
using SafePatch.Host;

namespace SafePatch.Cli;

/// <summary>
/// The <c>safepatch</c> command line: each command maps to one authoring operation. Load-order queries print their
/// result as text (or JSON with <c>--json</c>); program commands print JSON. Exit codes: 0 done, 1 rejected or
/// invalid, 2 usage error.
/// </summary>
public static class CliApp
{
    public const string Usage = """
        safepatch <command> [arguments] [options]

        Load order: every command below needs one of
            --data <Data folder> --plugins <plugins.txt>
            --mo2 <MO2 instance folder> [--profile <name>]        (read-only; MO2 need not run)
          and takes [--release SkyrimSE]. Results print as text, all of them unless --budget <characters>
          summarises; --offset <n> starts from a row; --json prints JSON; --out <file> [--format jsonl|csv|text]
          writes a new file instead.

          load-order [<plugin>]           the plugins; or one plugin's header and records by type
          find [--types <T,...>] [--plugin <p> [--role any|defines|overrides|wins]] [--editor-id <glob>]
               [--name <text>] [--where "<Field op value and ...>"] [--links-to <id>] [--min-versions <n>]
                                          records by filter, each by its winning version
          record <id> [--plugin <p>] [--fields <F,...>]
                                          one version of a record (FormKey or EditorID)
          compare <id> [--fields <F,...>] every version side by side, with lost edits
          conflicts (--types <T,...> | --plugin <p>) [--status conflict|resolved|lost|itm|all] [--field <F>] [--group <key>]
                                          lost edits summarised by group, or one group's records
          refs <id> [--direction both|out|in] [--types <T,...>]
          asset <path> [--record-types <T,...>]
          type [<Type>]                   record types, or one type's fields
          query <query.cs> [--assets <glob,...>]
                                          run a read-only query program in the sandbox
          test <program.cs> <scope>       run a patch in the sandbox; nothing is written to the load order
          query and test take [--worker-memory <MiB>]: the worker's memory limit (default 4096, or
          SAFEPATCH_WORKER_MEMORY_MB)

        Programs:
          validate <program.cs> [--settings <settings.cs>]
          package <program.cs> <scope> --name <Name> --out <empty folder>
                  [--description <text>] [--requires <a.esp,b.esp>] [--runtime-project <SafePatch.Synthesis.csproj>]

        <scope>: --writable <Type.Field,...> [--creatable <Type,...>] [--removable <Type,...>]
                 [--assets <glob,...>] [--max-records <n>] [--settings <settings.cs>] [--settings-json <values.json>]
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HashSet<string> Commands =
        ["load-order", "find", "record", "compare", "conflicts", "refs", "asset", "type", "query", "test", "validate", "package"];

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, IWorkerLauncher? launcher = null)
    {
        CommandLine command;
        try
        {
            command = CommandLine.Parse(args);
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine(Usage);
            return 2;
        }

        try
        {
            var (result, ok) = Execute(command, launcher);
            output.WriteLine(result as string ?? JsonSerializer.Serialize(result, Json));
            return ok ? 0 : 1;
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            error.WriteLine(Usage);
            return 2;
        }
        catch (Exception e) when (e is SafePatchException or IOException)
        {
            error.WriteLine(e.Message);
            return 1;
        }
    }

    private static (object Result, bool Ok) Execute(CommandLine command, IWorkerLauncher? launcher)
    {
        if (!Commands.Contains(command.Name)) throw new ArgumentException($"Unknown command '{command.Name}'.");
        switch (command.Name)
        {
            case "validate":
            {
                var result = AuthoringService.Validate(ReadFile(command.Positional(0)), command.Option("settings") is { } s ? ReadFile(s) : null);
                return (result, result.Success);
            }
            case "package":
            {
                var request = new PackageRequest(command.Required("name"), ReadFile(command.Positional(0)), Scope(command), command.Required("out"),
                    Settings(command), command.Option("description"), command.List("requires"), command.Option("runtime-project"));
                var result = AuthoringService.Package(request);
                return (result, result.Success);
            }
        }

        using var snapshot = LoadOrderSnapshot.Open(Source(command));
        var queries = new QueryService(snapshot);
        var service = new AuthoringService(snapshot, launcher, AuthoringService.WorkerMemory(command.Option("worker-memory")));
        if (command.Name == "test")
        {
            var result = service.TestPatch(ReadFile(command.Positional(0)), Scope(command), Settings(command));
            return (result, result.Accepted);
        }

        var set = command.Name switch
        {
            "load-order" => queries.LoadOrder(command.OptionalPositional(0)),
            "find" => queries.FindRecords(new RecordFilter(command.List("types"), command.Option("plugin"),
                AuthoringSession.Option(command.Option("role"), PluginRole.Any), command.Option("editor-id"), command.Option("name"),
                command.Option("where"), command.Option("links-to"), command.Int("min-versions", 0))),
            "record" => queries.GetRecord(command.Positional(0), command.Option("plugin"), command.List("fields")),
            "compare" => queries.CompareRecord(command.Positional(0), command.List("fields")),
            "conflicts" => queries.FindConflicts(new ConflictFilter(command.List("types"), command.Option("plugin"),
                AuthoringSession.Option(command.Option("status"), ConflictKind.Conflict), command.Option("field"), command.Option("group"))),
            "refs" => queries.References(command.Positional(0), AuthoringSession.Option(command.Option("direction"), LinkDirection.Both), command.List("types")),
            "asset" => queries.FindAsset(command.Positional(0), command.List("record-types")),
            "type" => queries.DescribeType(command.OptionalPositional(0)),
            "query" => service.RunQuery(ReadFile(command.Positional(0)), command.List("assets")),
            _ => throw new ArgumentException($"Unknown command '{command.Name}'."),
        };
        return (Present(set, command, snapshot), !set.Failed);
    }

    /// <summary>A result as text (all of it unless --budget), as JSON, or written to a new file with --out.</summary>
    private static object Present(ResultSet set, CommandLine command, LoadOrderSnapshot snapshot)
    {
        if (command.Option("out") is { } path)
        {
            AuthoringSession.GuardOutput(snapshot, path);
            var (full, bytes) = ResultExport.Write(set, path, AuthoringSession.Option(command.Option("format"), ExportFormat.Jsonl));
            return $"Wrote {set.Rows.Count:N0} {(set.IsLines ? "lines" : "rows")} to {full} ({bytes:N0} bytes).";
        }
        if (command.Flag("json")) return set;
        var budget = command.Option("budget") is not null ? Budgeted.Clamp(command.Int("budget", Budgeted.DefaultBudget)) : int.MaxValue;
        return Budgeted.Render(set, command.Int("offset", 0), budget, continuation: "--offset {0}");
    }

    private static LoadOrderSource Source(CommandLine command)
    {
        GameRelease? release = command.Option("release") is { } text ? Enum.Parse<GameRelease>(text, ignoreCase: true) : null;
        if (command.Option("mo2") is { } mo2)
        {
            if (command.Option("data") is not null) throw new ArgumentException("Pass --mo2 or --data, not both.");
            return new Mo2ProfileSource(mo2, command.Option("profile"), release);
        }
        return new DataFolderSource(command.Required("data"), command.Required("plugins"), release ?? GameRelease.SkyrimSE);
    }

    private static PatchScope Scope(CommandLine command) => new(
        command.List("writable") ?? [], command.List("creatable"), command.List("removable"), command.List("assets"), command.Int("max-records", 10_000));

    private static SettingsInput? Settings(CommandLine command) => command.Option("settings") is { } source
        ? new SettingsInput(ReadFile(source), command.Option("settings-json") is { } json ? ReadFile(json) : null)
        : null;

    private static string ReadFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : throw new ArgumentException($"File {path} does not exist.");
}

/// <summary>A command, its positional arguments, its <c>--name value</c> options and its <c>--flag</c> flags.</summary>
internal sealed record CommandLine(string Name, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Options)
{
    /// <summary>Options that take no value.</summary>
    private static readonly HashSet<string> Flags = ["json"];

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith('-')) throw new ArgumentException("Missing command.");
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
                continue;
            }
            var name = args[i][2..];
            if (Flags.Contains(name))
            {
                if (!options.TryAdd(name, "true")) throw new ArgumentException($"Option --{name} is given twice.");
                continue;
            }
            if (i + 1 >= args.Count) throw new ArgumentException($"Option --{name} needs a value.");
            if (!options.TryAdd(name, args[++i])) throw new ArgumentException($"Option --{name} is given twice.");
        }
        return new CommandLine(args[0], positional, options);
    }

    public string Positional(int index) =>
        index < Arguments.Count ? Arguments[index] : throw new ArgumentException($"'{Name}' needs {index + 1} argument(s).");

    public string? OptionalPositional(int index) => index < Arguments.Count ? Arguments[index] : null;

    public string? Option(string name) => Options.GetValueOrDefault(name);

    public bool Flag(string name) => Options.ContainsKey(name);

    public string Required(string name) => Option(name) ?? throw new ArgumentException($"'{Name}' needs --{name}.");

    public IReadOnlyList<string>? List(string name) =>
        Option(name)?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public int Int(string name, int fallback) => Option(name) is not { } text ? fallback
        : int.TryParse(text, out var value) && value >= 0 ? value : throw new ArgumentException($"--{name} must be a number, 0 or more.");
}

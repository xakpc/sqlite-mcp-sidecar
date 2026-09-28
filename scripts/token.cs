#!/usr/bin/env dotnet
#:package Spectre.Console@0.57.2

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Spectre.Console;

// Mints one deployment token for sqlite-sidecar-mcp and prints the matching SQLITE_SIDECAR_ block.
//
//     dotnet run scripts/token.cs
//
// The sidecar never generates, persists or returns a token, thus an operator needs a separate
// tool. The questions follow the startup validation of SidecarOptions.Load: the read floor and the
// backup directory rule are the same here, thus a block from this tool starts the sidecar.

// An interactive terminal is a requirement. A redirected input reaches the first prompt and then
// throws, thus the check happens before any output.
if (!AnsiConsole.Profile.Capabilities.Interactive)
{
    AnsiConsole.MarkupLine("[red]This tool asks questions, thus it needs an interactive terminal.[/]");
    Environment.Exit(1);
}

string[] permissionNames = ["schema", "read", "write", "backup", "diagnostics", "danger-raw-write"];

AnsiConsole.Write(new FigletText("sidecar").Color(Color.SteelBlue));
AnsiConsole.Write(new Rule("[steelblue]deployment token[/]").LeftJustified());
AnsiConsole.MarkupLine("[grey]One deployment has one database, one token and one permission set.[/]");
AnsiConsole.WriteLine();

// 1. The name labels the output file and the compose service. It is not a sidecar setting.
var name = AnsiConsole.Prompt(
    new TextPrompt<string>("Deployment [green]name[/]:")
        .DefaultValue("sqlite-sidecar")
        .Validate(value => Path.GetInvalidFileNameChars().Any(value.Contains)
            ? ValidationResult.Error("[red]The name is part of a file name.[/]")
            : ValidationResult.Success()));

// 2. Permission set. The read floor is repaired here and reported, because the sidecar would
//    otherwise refuse to start and the operator would read the failure instead of this warning.
var permissionPrompt = new MultiSelectionPrompt<string>()
    .Title("Which [green]permissions[/] does the deployment get?")
    .Required()
    .InstructionsText("[grey]<space> toggles, <enter> accepts[/]");

foreach (var permission in permissionNames)
{
    var item = permissionPrompt.AddChoice(permission);
    if (permission is "schema" or "read")
    {
        item.Select();
    }
}

var selected = AnsiConsole.Prompt(permissionPrompt).ToHashSet(StringComparer.Ordinal);

if (selected.Contains("write") || selected.Contains("danger-raw-write"))
{
    var added = new[] { "schema", "read" }.Where(floor => selected.Add(floor)).ToArray();
    if (added.Length > 0)
    {
        AnsiConsole.MarkupLine($"[yellow]Read floor:[/] added [green]{string.Join("[/] and [green]", added)}[/]. "
            + "A write permission is only valid together with schema and read.");
    }
}

if (selected.Contains("danger-raw-write"))
{
    AnsiConsole.MarkupLine("[red]danger-raw-write[/] gives the agent [red]INSERT / UPDATE / DELETE SQL[/] on each table. "
        + "Give this token to one client only.");
}

var permissions = string.Join(',', permissionNames.Where(selected.Contains));

// 3. Paths. The sidecar never creates the database file, and it refuses to start when the backup
//    permission is on without a writable backup directory.
var databasePath = AnsiConsole.Prompt(
    new TextPrompt<string>("Database [green]path[/] as the sidecar sees it:").DefaultValue("/data/app.db"));

var backupDirectory = selected.Contains("backup")
    ? AnsiConsole.Prompt(new TextPrompt<string>("Backup [green]directory[/]:").DefaultValue("/data/backups"))
    : null;

// 4. Token strength. 256 bit is far above a guessing attack over a network. The larger sizes are
//    for a policy that demands them.
var bits = AnsiConsole.Prompt(
    new SelectionPrompt<int>()
        .Title("Token [green]strength[/]:")
        .UseConverter(value => value == 256 ? "256 bit (recommended)" : $"{value} bit")
        .AddChoices(256, 384, 512));

// Base64Url keeps the token to the unreserved characters, thus no shell, YAML or header quoting
// rule can change the value that the sidecar compares.
var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bits / 8));

var format = AnsiConsole.Prompt(
    new SelectionPrompt<string>()
        .Title("Output [green]format[/]:")
        .AddChoices(".env file", "PowerShell", "bash", "docker compose"));

var settings = new List<(string Key, string Value)>
{
    ("SQLITE_SIDECAR_DB", databasePath),
    ("SQLITE_SIDECAR_TOKEN", token),
    ("SQLITE_SIDECAR_PERMISSIONS", permissions),
};

if (backupDirectory is not null)
{
    settings.Add(("SQLITE_SIDECAR_BACKUP_DIR", backupDirectory));
}

var block = Render(format, name, settings);

AnsiConsole.WriteLine();
AnsiConsole.Write(new Panel(new Text(block))
{
    Header = new PanelHeader($" {format} "),
    Border = BoxBorder.Rounded,
    BorderStyle = new Style(Color.SteelBlue),
    Expand = true,
});

AnsiConsole.Write(new Panel(new Text($"Authorization: Bearer {token}"))
{
    Header = new PanelHeader(" the client header "),
    Border = BoxBorder.Rounded,
    BorderStyle = new Style(Color.Green),
    Expand = true,
});

AnsiConsole.MarkupLine($"[grey]{bits} bit of entropy. The sidecar logs no token and returns no token. "
    + "This terminal now holds the only copy.[/]");
AnsiConsole.WriteLine();

// 5. The file is optional, because a secret manager takes the value directly from the panel.
if (AnsiConsole.Confirm("Write the block to a file?", defaultValue: false))
{
    var suggestion = Path.Combine("build", "dev", $"{name}{(format == "docker compose" ? ".compose.yml" : ".env")}");

    var path = AnsiConsole.Prompt(new TextPrompt<string>("File:").DefaultValue(suggestion));
    var full = Path.GetFullPath(path);

    if (File.Exists(full) && !AnsiConsole.Confirm($"[yellow]{Markup.Escape(full)}[/] exists. Overwrite?", defaultValue: false))
    {
        AnsiConsole.MarkupLine("[grey]Nothing written.[/]");
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    File.WriteAllText(full, block + Environment.NewLine);

    if (!OperatingSystem.IsWindows())
    {
        // Owner only. A secret file that each account can read is not a secret.
        File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    AnsiConsole.MarkupLine($"[green]Written[/] {Markup.Escape(full)}");

    // build/ is git-ignored, thus it is the safe default. Each other path needs a check.
    if (!full.Replace(Path.DirectorySeparatorChar, '/').Contains("/build/", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[yellow]The file holds a secret and it is not under build/. "
            + "Confirm that git ignores this path before the next commit.[/]");
    }
}

static string Render(string format, string name, List<(string Key, string Value)> settings)
{
    var text = new StringBuilder();

    switch (format)
    {
        case "PowerShell":
            foreach (var (key, value) in settings)
            {
                text.AppendLine($"$env:{key} = '{value}'");
            }

            break;

        case "bash":
            foreach (var (key, value) in settings)
            {
                text.AppendLine($"export {key}='{value}'");
            }

            break;

        case "docker compose":
            text.AppendLine("services:");
            text.AppendLine($"  {name}:");
            text.AppendLine("    image: ghcr.io/xakpc/sqlite-mcp-sidecar:latest");
            text.AppendLine("    environment:");
            foreach (var (key, value) in settings)
            {
                text.AppendLine($"      {key}: \"{value}\"");
            }

            break;

        default:
            foreach (var (key, value) in settings)
            {
                text.AppendLine($"{key}={value}");
            }

            break;
    }

    return text.ToString().TrimEnd();
}

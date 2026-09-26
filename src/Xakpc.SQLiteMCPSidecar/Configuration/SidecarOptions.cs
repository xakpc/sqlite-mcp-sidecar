using Xakpc.SQLiteMCPSidecar.Security;

namespace Xakpc.SQLiteMCPSidecar.Configuration;

/// <summary>
/// Every configuration value of one deployment. Bound one time at startup and then immutable.
/// There is no reconfiguration at runtime, because a permission change must be a visible
/// deployment change.
/// </summary>
public sealed class SidecarOptions
{
    public required string DatabasePath { get; init; }
    public required string Token { get; init; }
    public required PermissionSet Permissions { get; init; }
    public string? BackupDirectory { get; init; }
    public int MaxRows { get; init; } = 1000;
    public int MaxResultBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxSqlBytes { get; init; } = 32 * 1024;
    public int QueryTimeoutSeconds { get; init; } = 10;
    public int BusyTimeoutSeconds { get; init; } = 3;
    public int MaxConcurrency { get; init; } = 4;
    public int MaxWriteRows { get; init; } = 100;
    public int MaxWriteRowsPerMinute { get; init; } = 500;

    /// <summary>
    /// Reads the <c>SQLITE_SIDECAR_</c> configuration and validates it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read is explicit and not a binder call. The permission set decides which authorization
    /// policies the container gets, thus the values must be available before the service provider
    /// exists. The configuration keys also do not match the property names: the environment
    /// variable provider removes the prefix but keeps a single underscore, thus <c>MAX_ROWS</c>
    /// never matches <c>MaxRows</c>.
    /// </para>
    /// <para>
    /// Startup fails when a value is absent or invalid. The sidecar does not start in a degraded
    /// state, because a silent fallback hides a deployment mistake.
    /// </para>
    /// </remarks>
    /// <exception cref="SidecarConfigurationException">One or more values are absent or invalid.</exception>
    public static SidecarOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var problems = new List<string>();

        var databasePath = configuration["DB"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            problems.Add("SQLITE_SIDECAR_DB is required. It is the path of the SQLite database file.");
        }
        else if (!File.Exists(databasePath))
        {
            // Never create the file. Creation hides a wrong path and makes an empty database next
            // to the correct one.
            problems.Add("SQLITE_SIDECAR_DB does not name an existing file. The sidecar never creates the database.");
        }

        var token = configuration["TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add("SQLITE_SIDECAR_TOKEN is required. Deployment infrastructure supplies it.");
        }

        var permissions = default(PermissionSet);
        if (PermissionSet.TryParse(configuration["PERMISSIONS"] ?? PermissionSet.DefaultSpecification, out permissions, out var permissionError))
        {
            problems.AddRange(permissions.ReadFloorProblems());
        }
        else
        {
            problems.Add(permissionError!);
        }

        var backupDirectory = configuration["BACKUP_DIR"];
        if (permissions.Has(Permission.Backup))
        {
            if (string.IsNullOrWhiteSpace(backupDirectory))
            {
                problems.Add("SQLITE_SIDECAR_BACKUP_DIR is required when the backup permission is on.");
            }
            else if (!IsWritableDirectory(backupDirectory))
            {
                problems.Add("SQLITE_SIDECAR_BACKUP_DIR does not name a writable directory.");
            }
        }

        var options = new SidecarOptions
        {
            DatabasePath = databasePath ?? string.Empty,
            Token = token ?? string.Empty,
            Permissions = permissions,
            BackupDirectory = backupDirectory,
            MaxRows = ReadPositiveInt(configuration, "MAX_ROWS", 1000, 1, 1_000_000, problems),
            MaxResultBytes = ReadPositiveInt(configuration, "MAX_RESULT_BYTES", 4 * 1024 * 1024, 1024, 256 * 1024 * 1024, problems),
            MaxSqlBytes = ReadPositiveInt(configuration, "MAX_SQL_BYTES", 32 * 1024, 64, 4 * 1024 * 1024, problems),
            QueryTimeoutSeconds = ReadPositiveInt(configuration, "QUERY_TIMEOUT_SECONDS", 10, 1, 3600, problems),
            BusyTimeoutSeconds = ReadPositiveInt(configuration, "BUSY_TIMEOUT_SECONDS", 3, 1, 3600, problems),
            MaxConcurrency = ReadPositiveInt(configuration, "MAX_CONCURRENCY", 4, 1, 1024, problems),
            MaxWriteRows = ReadPositiveInt(configuration, "MAX_WRITE_ROWS", 100, 1, 1_000_000, problems),
            MaxWriteRowsPerMinute = ReadPositiveInt(configuration, "MAX_WRITE_ROWS_PER_MINUTE", 500, 1, 10_000_000, problems),
        };

        if (problems.Count > 0)
        {
            throw new SidecarConfigurationException(problems);
        }

        return options;
    }

    private static int ReadPositiveInt(IConfiguration configuration, string key, int fallback, int minimum, int maximum, List<string> problems)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var value))
        {
            problems.Add($"SQLITE_SIDECAR_{key} is not an integer.");
            return fallback;
        }

        if (value < minimum || value > maximum)
        {
            problems.Add($"SQLITE_SIDECAR_{key} is {value}. It must be between {minimum} and {maximum}.");
            return fallback;
        }

        return value;
    }

    private static bool IsWritableDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        // A permission bit check is not portable. A probe file is the reliable test.
        var probe = Path.Combine(path, $".sidecar-write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Reports every configuration problem at one time, thus an operator repairs one deployment and
/// not one variable.
/// </summary>
public sealed class SidecarConfigurationException(IReadOnlyList<string> problems)
    : Exception(BuildMessage(problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;

    private static string BuildMessage(IReadOnlyList<string> problems) =>
        "The sidecar configuration is not valid:" + Environment.NewLine
        + string.Join(Environment.NewLine, problems.Select(p => "  - " + p));
}

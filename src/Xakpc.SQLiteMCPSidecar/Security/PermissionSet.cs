namespace Xakpc.SQLiteMCPSidecar.Security;

/// <summary>
/// A deployment-level capability. A permission controls which MCP tools exist.
/// </summary>
[Flags]
public enum Permission
{
    None = 0,
    Schema = 1 << 0,
    Read = 1 << 1,
    Write = 1 << 2,
    Backup = 1 << 3,
    Diagnostics = 1 << 4,
    DangerRawWrite = 1 << 5,
}

/// <summary>
/// The permission set of one deployment. One deployment has one token and one permission set.
/// </summary>
/// <remarks>
/// The wire names contain a hyphen (<c>danger-raw-write</c>), thus <see cref="Enum.Parse{T}(string)"/>
/// is not usable and the names come from a table.
/// </remarks>
public readonly struct PermissionSet(Permission value) : IEquatable<PermissionSet>
{
    /// <summary>The default set. It is read-only.</summary>
    public const string DefaultSpecification = "schema,read";

    private static readonly (string Name, Permission Value)[] Names =
    [
        ("schema", Permission.Schema),
        ("read", Permission.Read),
        ("write", Permission.Write),
        ("backup", Permission.Backup),
        ("diagnostics", Permission.Diagnostics),
        ("danger-raw-write", Permission.DangerRawWrite),
    ];

    /// <summary>The policy name prefix that <c>[Authorize(Policy = ...)]</c> uses on a tool.</summary>
    public const string PolicyPrefix = "perm:";

    /// <summary>The claim type that carries one permission name.</summary>
    public const string ClaimType = "perm";

    public Permission Value { get; } = value;

    public bool Has(Permission permission) => (Value & permission) == permission;

    /// <summary>Every permission name, for policy registration.</summary>
    public static IEnumerable<string> AllNames => Names.Select(n => n.Name);

    /// <summary>The policy name for one permission name.</summary>
    public static string PolicyFor(string permissionName) => PolicyPrefix + permissionName;

    /// <summary>The names in this set, for claim issuing.</summary>
    public IEnumerable<string> ToNames()
    {
        // A lambda in a struct cannot reach an instance member, thus the value goes to a local.
        var value = Value;
        return Names.Where(n => (value & n.Value) == n.Value).Select(n => n.Name);
    }

    public override string ToString() => string.Join(',', ToNames());

    /// <summary>
    /// Parses a comma-separated specification. An unknown name is an error, because an ignored
    /// name gives a deployment with fewer tools than the operator expects, or a false sense of
    /// restriction.
    /// </summary>
    public static bool TryParse(string? specification, out PermissionSet result, out string? error)
    {
        result = default;
        error = null;

        var value = Permission.None;
        var unknown = new List<string>();

        foreach (var raw in (specification ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Array.FindIndex(Names, n => string.Equals(n.Name, raw, StringComparison.OrdinalIgnoreCase));
            if (match < 0)
            {
                unknown.Add(raw);
                continue;
            }

            value |= Names[match].Value;
        }

        if (unknown.Count > 0)
        {
            error = $"SQLITE_SIDECAR_PERMISSIONS has an unknown permission name: {string.Join(", ", unknown)}. "
                  + $"The known names are {string.Join(", ", AllNames)}.";
            return false;
        }

        if (value == Permission.None)
        {
            error = "SQLITE_SIDECAR_PERMISSIONS is empty. It needs at least one permission name.";
            return false;
        }

        result = new PermissionSet(value);
        return true;
    }

    /// <summary>
    /// The read floor. <c>write</c> and <c>danger-raw-write</c> are only valid together with
    /// <c>schema</c> and <c>read</c>.
    /// </summary>
    public IEnumerable<string> ReadFloorProblems()
    {
        foreach (var (name, permission) in new[] { ("write", Permission.Write), ("danger-raw-write", Permission.DangerRawWrite) })
        {
            if (!Has(permission))
            {
                continue;
            }

            var missing = new List<string>();
            if (!Has(Permission.Schema))
            {
                missing.Add("schema");
            }

            if (!Has(Permission.Read))
            {
                missing.Add("read");
            }

            if (missing.Count > 0)
            {
                yield return $"{name} requires schema and read. SQLITE_SIDECAR_PERMISSIONS is missing {string.Join(" and ", missing)}.";
            }
        }
    }

    public bool Equals(PermissionSet other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is PermissionSet other && Equals(other);

    public override int GetHashCode() => (int)Value;

    public static bool operator ==(PermissionSet left, PermissionSet right) => left.Equals(right);

    public static bool operator !=(PermissionSet left, PermissionSet right) => !left.Equals(right);
}

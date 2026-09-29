using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xakpc.SQLiteMCPSidecar.Tests.Harness;

/// <summary>
/// The corpus of malformed and hostile MCP calls that a badly behaved agent sends.
/// </summary>
/// <remarks>
/// <para>
/// The cases live in <c>Fixtures/bad-agent-corpus.json</c> as data and not as C# literals, because
/// the point of each case is the raw JSON shape that reaches the server. A value goes into
/// <c>CallToolRequestParams.Arguments</c> as the <see cref="JsonElement"/> that the file holds, thus
/// a wrong type reaches the SDK binder exactly as an agent would send it. Adding a case is one JSON
/// object and no code change. See <c>.lode/testing/bad-agent-suite.md</c>.
/// </para>
/// </remarks>
public static class BadAgentCorpus
{
    private const string ResourceName = "Xakpc.SQLiteMCPSidecar.Tests.Fixtures.bad-agent-corpus.json";

    /// <summary>
    /// The macro that builds a long string without putting one in the file.
    /// <c>@@repeat:A:100000@@</c> becomes one hundred thousand letter A.
    /// </summary>
    private const string RepeatPrefix = "@@repeat:";

    private static readonly Lazy<IReadOnlyList<BadAgentCase>> Loaded = new(Load);

    /// <summary>Every case of the corpus, in file order.</summary>
    public static IReadOnlyList<BadAgentCase> Cases => Loaded.Value;

    /// <summary>The identifier of each case. Theory data must be serializable, thus a test takes this.</summary>
    public static IEnumerable<string> Ids => Cases.Select(c => c.Id);

    /// <summary>Finds one case by identifier.</summary>
    public static BadAgentCase Find(string id) =>
        Cases.FirstOrDefault(c => c.Id == id)
        ?? throw new InvalidOperationException($"The corpus holds no case with the id '{id}'.");

    private static IReadOnlyList<BadAgentCase> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is absent.");

        var cases = JsonSerializer.Deserialize<List<BadAgentCase>>(stream, Options)
            ?? throw new InvalidOperationException("The corpus is empty.");

        var duplicate = cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            // A repeated id would silently run one case two times and hide the other one.
            throw new InvalidOperationException($"The corpus holds the id '{duplicate.Key}' more than one time.");
        }

        foreach (var item in cases)
        {
            item.Validate();
        }

        return cases;
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Expands the <c>@@repeat:</c> macro of a string argument, and returns any other element
    /// unchanged.
    /// </summary>
    internal static JsonElement Expand(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.String)
        {
            return element;
        }

        var text = element.GetString();
        if (text is null || !text.Contains(RepeatPrefix, StringComparison.Ordinal))
        {
            return element;
        }

        while (true)
        {
            var start = text.IndexOf(RepeatPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = text.IndexOf("@@", start + RepeatPrefix.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException($"A repeat macro is not closed: {text}");
            }

            var parts = text[(start + RepeatPrefix.Length)..end].Split(':');
            if (parts.Length != 2 || parts[0].Length != 1 || !int.TryParse(parts[1], out var count))
            {
                throw new InvalidOperationException($"A repeat macro is malformed: {text[start..(end + 2)]}");
            }

            text = text[..start] + new string(parts[0][0], count) + text[(end + 2)..];
        }

        return JsonSerializer.SerializeToElement(text);
    }
}

/// <summary>One case of the corpus: a tool call that a badly behaved agent makes.</summary>
public sealed class BadAgentCase
{
    /// <summary>The stable name of the case. It is the theory data and it appears in the test name.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The tool to call. It does not have to exist.</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>
    /// The permission set of the sidecar for this case. Cases are grouped by it, and the harness skips
    /// a group that the external sidecar does not carry.
    /// </summary>
    public string Permissions { get; init; } = string.Empty;

    /// <summary>
    /// The raw arguments, or <c>null</c> to send a call with no arguments member at all.
    /// </summary>
    public Dictionary<string, JsonElement>? Arguments { get; init; }

    /// <summary>One of <c>error-code</c>, <c>rejected-opaque</c> and <c>ok</c>.</summary>
    public string Expect { get; init; } = string.Empty;

    /// <summary>
    /// The texts that the message may carry. It is required for, and only for, <c>error-code</c>.
    /// </summary>
    /// <remarks>
    /// Usually a <c>SidecarError</c> name. A refusal that the protocol layer makes before the tool
    /// method runs, for example an unknown tool name or the authorization filter, never reaches the
    /// error model, thus such a case names the distinctive text of that refusal instead.
    /// </remarks>
    public List<string> Codes { get; init; } = [];

    /// <summary>Why the case exists and why the expectation is what it is. Every case needs one.</summary>
    public string Why { get; init; } = string.Empty;

    /// <summary>The arguments with every macro expanded, ready to send.</summary>
    [JsonIgnore]
    public Dictionary<string, JsonElement>? ExpandedArguments =>
        Arguments?.ToDictionary(pair => pair.Key, pair => BadAgentCorpus.Expand(pair.Value));

    /// <summary>The case writes rows, thus the unchanged-database invariant does not apply to it.</summary>
    [JsonIgnore]
    public bool MayWrite => Expect == ExpectOk;

    /// <summary>The case is a recorded gap rather than the behaviour the product intends.</summary>
    [JsonIgnore]
    public bool IsRecordedGap => Expect is ExpectRejectedOpaque or ExpectAcceptedGap;

    /// <summary>The call succeeds, and that is correct.</summary>
    public const string ExpectOk = "ok";

    /// <summary>The call fails and the message names one of <see cref="Codes"/>.</summary>
    public const string ExpectErrorCode = "error-code";

    /// <summary>
    /// The call fails and the message carries no error code, because the SDK binder replaced it.
    /// </summary>
    /// <remarks>
    /// This is the recorded-gap bucket. Every case in it is evidence for
    /// <c>.scratch/agent-abuse-hardening/issues/02-binder-failures-lose-the-error-code.md</c>, and a
    /// case moves to <see cref="ExpectErrorCode"/> when that issue is fixed.
    /// </remarks>
    public const string ExpectRejectedOpaque = "rejected-opaque";

    /// <summary>
    /// The call succeeds today although the tool contract says that it must not.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="ExpectRejectedOpaque"/>, and it exists for the same reason: the corpus
    /// records what the sidecar does and never launders a gap into an expected success. The case names
    /// its issue in <c>why</c>, and it moves to <see cref="ExpectErrorCode"/> when that issue is fixed.
    /// </remarks>
    public const string ExpectAcceptedGap = "accepted-gap";

    /// <summary>Fails the load when a case cannot mean anything.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Tool)
            || string.IsNullOrWhiteSpace(Permissions) || string.IsNullOrWhiteSpace(Why))
        {
            throw new InvalidOperationException($"The case '{Id}' is missing id, tool, permissions or why.");
        }

        if (Expect is not (ExpectOk or ExpectErrorCode or ExpectRejectedOpaque or ExpectAcceptedGap))
        {
            throw new InvalidOperationException($"The case '{Id}' has an unknown expect value '{Expect}'.");
        }

        if (Expect == ExpectErrorCode && Codes.Count == 0)
        {
            throw new InvalidOperationException($"The case '{Id}' expects an error code and names none.");
        }

        if (Expect != ExpectErrorCode && Codes.Count > 0)
        {
            throw new InvalidOperationException($"The case '{Id}' names codes and does not expect one.");
        }
    }

    public override string ToString() => Id;
}

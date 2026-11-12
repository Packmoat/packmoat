using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using NJsonSchema.Validation;
using Packmoat.Domain;
using Packmoat.Protocols.NuGet;

namespace Packmoat.Host.Tests;

/// <summary>One conformance vector (spec/README.md): a named case of one group, at a fixed time.</summary>
public sealed record VectorCase(string Group, string Name, DateTimeOffset Now, JsonObject Input, JsonObject Expected);

/// <summary>A vector file that does not follow spec/README.md and its schema. Nothing in it runs.</summary>
public sealed class MalformedVectorException(string message) : Exception(message);

/// <summary>
/// Reads and checks the shared conformance vectors in spec/vectors. The files are found from the core folder (the one holding
/// dotnet/Packmoat.sln), never from the repository root, so the core alone runs them. A file is checked against its group's JSON Schema
/// in spec/schema (every object closed, every code and time in its one form), then for what a schema cannot say: unique names and
/// real calendar times. A file breaking any rule is refused whole.
/// </summary>
public static partial class ConformanceVectors
{
    public const string TokenCheck = "auth/token-check";
    public const string FlatContainer = "nuget/flat-container";
    public const string Registration = "nuget/registration";
    public const string Routes = "nuget/routes";
    public const string ServiceIndex = "nuget/service-index";
    public const string PolicyDecisions = "policy/decisions";

    /// <summary>Every group this implementation runs. Each must have a schema and a vector file, and nothing else may.</summary>
    public static readonly IReadOnlyList<string> Groups = [TokenCheck, FlatContainer, Registration, Routes, ServiceIndex, PolicyDecisions];

    /// <summary>The stable code of each verdict reason. The .NET names may change; these may not.</summary>
    public static readonly IReadOnlyDictionary<VerdictReason, string> VerdictCodes = new Dictionary<VerdictReason, string>
    {
        [VerdictReason.Cooldown] = "cooldown",
        [VerdictReason.CooldownElapsed] = "cooldown_elapsed",
        [VerdictReason.Waiver] = "waiver",
        [VerdictReason.InternalNamespace] = "internal_namespace",
        [VerdictReason.DateUnknown] = "date_unknown",
        [VerdictReason.DateSourceUnavailable] = "date_source_unavailable",
        [VerdictReason.NoPolicyDecided] = "no_policy_decided",
        [VerdictReason.PolicyError] = "policy_error",
    };

    /// <summary>The stable code of each kind of upstream path.</summary>
    public static readonly IReadOnlyDictionary<NuGetResourceKind, string> KindCodes = new Dictionary<NuGetResourceKind, string>
    {
        [NuGetResourceKind.ServiceIndex] = "service_index",
        [NuGetResourceKind.FlatContainerVersions] = "flat_container_versions",
        [NuGetResourceKind.FlatContainerContent] = "flat_container_content",
        [NuGetResourceKind.RegistrationIndex] = "registration_index",
        [NuGetResourceKind.RegistrationPage] = "registration_page",
        [NuGetResourceKind.RegistrationLeaf] = "registration_leaf",
        [NuGetResourceKind.CatalogEntry] = "catalog_entry",
        [NuGetResourceKind.Unknown] = "unknown",
    };

    /// <summary>The token check's outcomes.</summary>
    public static readonly IReadOnlyList<string> TokenCodes = ["authenticated", "no_credentials", "token_rejected", "unavailable"];

    /// <summary>The fields that hold a time, wherever they are; and every value of a publishDates map.</summary>
    private static readonly string[] TimeFields = ["now", "expiresAt", "publishedAt"];

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,3})?Z$")]
    private static partial Regex UtcTime();

    /// <summary>spec/, found from the test's output folder: up to dotnet/Packmoat.sln, then its parent (the core folder).</summary>
    public static string SpecRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Packmoat.sln"))) dir = dir.Parent;
        var core = dir?.Parent ?? throw new InvalidOperationException("Packmoat.sln not found above the test output folder.");
        return Path.Combine(core.FullName, "spec");
    }

    public static string Root() => Path.Combine(SpecRoot(), "vectors");
    public static string SchemaRoot() => Path.Combine(SpecRoot(), "schema");

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<VectorCase>>> All = new(() => LoadAll(Root(), SchemaRoot()));
    private static readonly Dictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    /// <summary>The case names of one group, for a theory's member data.</summary>
    public static TheoryData<string> Names(string group)
    {
        var data = new TheoryData<string>();
        foreach (var c in All.Value[group]) data.Add(c.Name);
        return data;
    }

    public static VectorCase Case(string group, string name) => All.Value[group].Single(c => c.Name == name);

    /// <summary>Every *.json file under the vectors root, by group (its path without .json). Every group must be there, and nothing else.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<VectorCase>> LoadAll(string root, string schemaRoot)
    {
        var result = new SortedDictionary<string, IReadOnlyList<VectorCase>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var group = Path.ChangeExtension(Path.GetRelativePath(root, file), null).Replace('\\', '/');
            result[group] = Parse(group, File.ReadAllText(file), schemaRoot);
        }
        var missing = Groups.Where(g => !result.ContainsKey(g)).ToArray();
        if (missing.Length > 0) throw new MalformedVectorException($"No vector file for: {string.Join(", ", missing)} (looked in {root}).");
        return result;
    }

    /// <summary>The group's JSON Schema, read once.</summary>
    public static JsonSchema Schema(string group, string schemaRoot)
    {
        var path = Path.GetFullPath(Path.Combine(schemaRoot, group + ".json"));
        lock (Schemas)
        {
            if (!Schemas.TryGetValue(path, out var schema))
                Schemas[path] = schema = JsonSchema.FromJsonAsync(File.ReadAllText(path)).GetAwaiter().GetResult();
            return schema;
        }
    }

    /// <summary>The schema's errors for a document, as "path: kind" lines (nested oneOf errors included). Empty when it conforms.</summary>
    public static IReadOnlyList<string> SchemaErrors(string group, string json, string schemaRoot)
    {
        // Read with dates left as text: a time is checked as the string it is, never as a date the reader guessed.
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        var token = JToken.ReadFrom(reader);
        return Flatten(Schema(group, schemaRoot).Validate(token)).ToArray();

        static IEnumerable<string> Flatten(IEnumerable<ValidationError> errors) => errors.SelectMany(e =>
            e is ChildSchemaValidationError child
                ? new[] { $"{e.Path}: {e.Kind}" }.Concat(Flatten(child.Errors.SelectMany(x => x.Value)))
                : [$"{e.Path}: {e.Kind}"]);
    }

    /// <summary>One group's file, checked. Throws <see cref="MalformedVectorException"/> naming the first broken rule.</summary>
    public static IReadOnlyList<VectorCase> Parse(string group, string json, string? schemaRoot = null)
    {
        schemaRoot ??= SchemaRoot();
        if (!Groups.Contains(group)) throw Bad(group, null, "is not a known group");
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException e) { throw Bad(group, null, "is not JSON: " + e.Message); }

        var errors = SchemaErrors(group, json, schemaRoot);
        if (errors.Count > 0) throw Bad(group, null, "does not match its schema:\n  " + string.Join("\n  ", errors));

        var list = new List<VectorCase>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in root!["cases"]!.AsArray().Select(n => n!.AsObject()))
        {
            var name = (string)c["name"]!;
            if (!names.Add(name)) throw Bad(group, name, "is a duplicate name");
            foreach (var (path, text) in Times(c, ""))
                if (Time(text) is null) throw Bad(group, name, $"has {path} \"{text}\", not a real UTC time");
            list.Add(new VectorCase(group, name, Time((string)c["now"]!)!.Value, c["input"]!.AsObject(), c["expected"]!.AsObject()));
        }
        return list;
    }

    /// <summary>Every time in a case: the time fields anywhere, and each value of a publishDates map.</summary>
    private static IEnumerable<(string Path, string Text)> Times(JsonNode? node, string path) => node switch
    {
        JsonObject o => o.SelectMany(kv =>
            (TimeFields.Contains(kv.Key) || path.EndsWith(".publishDates", StringComparison.Ordinal)) && kv.Value is JsonValue v && v.TryGetValue<string>(out var s)
                ? [(path + "." + kv.Key, s)]
                : Times(kv.Value, path + "." + kv.Key)),
        JsonArray a => a.SelectMany((x, i) => Times(x, $"{path}[{i}]")),
        _ => [],
    };

    /// <summary>A time in the vectors' one format: UTC, ending in Z, seconds always, fractions (up to milliseconds) only when there are some.</summary>
    public static string Format(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);

    /// <summary>A time in the vectors' format, or null when it is not one (or is not a real date and time).</summary>
    public static DateTimeOffset? Time(string? text) =>
        text is not null && UtcTime().IsMatch(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;

    public static string Code(VerdictReason reason) => VerdictCodes[reason];

    /// <summary>The verdict a code stands for in a NuGet vector: cooldown_elapsed and waiver allow, every other code hides.</summary>
    public static Verdict Verdict(string code)
    {
        var reason = VerdictCodes.Single(kv => kv.Value == code).Key;
        return new Verdict(reason is VerdictReason.CooldownElapsed or VerdictReason.Waiver ? Decision.Allow : Decision.Hide, reason);
    }

    /// <summary>Compares as JSON: the same values, whatever the property order or the spacing.</summary>
    public static void AssertJson(JsonNode expected, JsonNode actual, string name)
    {
        var e = JsonNode.Parse(expected.ToJsonString())!;
        var a = JsonNode.Parse(actual.ToJsonString())!;
        Assert.True(JsonNode.DeepEquals(e, a), $"{name}\nexpected: {e.ToJsonString()}\nactual:   {a.ToJsonString()}");
    }

    private static MalformedVectorException Bad(string group, string? name, string what) =>
        new($"Vector file {group}.json" + (name is null ? "" : $", case \"{name}\",") + " " + what + ".");
}

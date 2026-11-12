using System.Text.Json.Nodes;
using Packmoat.Domain;
using Packmoat.Protocols.NuGet;
using static Packmoat.Host.Tests.ConformanceVectors;

namespace Packmoat.Host.Tests;

/// <summary>The vector loader refuses a file that breaks the format, so a broken vector can never pass by not running.</summary>
public class ConformanceVectorLoaderTests
{
    private const string Good = """
        { "cases": [ { "name": "a", "now": "2026-10-10T12:00:00Z",
          "input": { "policy": { "cooldownDays": 14, "internalPrefixes": [ "Contoso." ],
                                 "waivers": [ { "id": "w1", "packageId": "Fabrikam.Json", "version": "2.0.0", "expiresAt": "2026-10-11T12:00:00Z" } ] },
                     "package": { "id": "Fabrikam.Json", "version": "1.0.0" }, "publishDates": { "1.0.0": "2026-10-01T12:00:00Z" } },
          "expected": { "decision": "hide", "reason": "cooldown", "cooldownDaysApplied": 14, "publishedAt": "2026-10-01T12:00:00Z" } } ] }
        """;

    private const string GoodToken = """
        { "cases": [ { "name": "a", "now": "2026-10-10T12:00:00Z",
          "input": { "authorization": "Bearer pmt_x", "tenants": [ { "id": "contoso", "gate": "serve" } ],
                     "tokens": [ { "token": "pmt_x", "tenant": "contoso", "revoked": false, "expiresAt": "2026-10-11T12:00:00Z" } ], "store": "available" },
          "expected": { "status": 200, "reason": "authenticated", "tenant": "contoso" } } ] }
        """;

    private static string With(string file, Action<JsonObject> change)
    {
        var doc = JsonNode.Parse(file)!.AsObject();
        change(doc["cases"]![0]!.AsObject());
        return doc.ToJsonString();
    }

    private static string Refused(string group, string json) =>
        Assert.Throws<MalformedVectorException>(() => Parse(group, json)).Message;

    [Fact]
    public void Well_formed_files_load()
    {
        var c = Assert.Single(Parse(PolicyDecisions, Good));
        Assert.Equal(("a", new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)), (c.Name, c.Now));
        Assert.Single(Parse(TokenCheck, GoodToken));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("now")]
    [InlineData("input")]
    [InlineData("expected")]
    public void A_case_missing_a_field_is_refused(string field) =>
        Assert.Contains(field, Refused(PolicyDecisions, With(Good, c => c.Remove(field))));

    [Fact]
    public void A_case_missing_a_required_nested_field_is_refused()
    {
        Assert.Contains("publishDates", Refused(PolicyDecisions, With(Good, c => c["input"]!.AsObject().Remove("publishDates"))));
        Assert.Contains("reason", Refused(PolicyDecisions, With(Good, c => c["expected"]!.AsObject().Remove("reason"))));
        Assert.Contains("expiresAt", Refused(PolicyDecisions, With(Good, c => c["input"]!["policy"]!["waivers"]![0]!.AsObject().Remove("expiresAt"))));
        Assert.Contains("tenant", Refused(TokenCheck, With(GoodToken, c => c["input"]!["tokens"]![0]!.AsObject().Remove("tenant"))));
    }

    /// <summary>The review's probes: each used to load and pass vacuously.</summary>
    [Fact]
    public void A_misspelled_field_anywhere_is_refused_rather_than_ignored()
    {
        Assert.Contains("when", Refused(PolicyDecisions, With(Good, c => c["when"] = "x")));
        Assert.Contains("internalPrefix", Refused(PolicyDecisions, With(Good, c =>
        {
            var p = c["input"]!["policy"]!.AsObject();
            p["internalPrefix"] = p["internalPrefixes"]!.DeepClone();
            p.Remove("internalPrefixes");
        })));
        Assert.Contains("revokd", Refused(TokenCheck, With(GoodToken, c => c["input"]!["tokens"]![0]!["revokd"] = true)));
        Assert.Contains("expiry", Refused(PolicyDecisions, With(Good, c => c["input"]!["policy"]!["waivers"]![0]!["expiry"] = "2026-10-11T12:00:00Z")));
        Assert.Contains("active", Refused(TokenCheck, With(GoodToken, c => c["input"]!["tenants"]![0]!["active"] = "no")));
        Assert.Contains("waiverID", Refused(PolicyDecisions, With(Good, c => c["expected"]!["waiverID"] = "w1")));
    }

    [Theory]
    [InlineData("2026-10-10T12:00:00+02:00")]   // an offset, not Z
    [InlineData("2026-10-10T12:00:00")]
    [InlineData("2026-10-10 12:00:00Z")]
    [InlineData("2026-10-11")]                  // a date alone
    [InlineData("2026-10-10T12:00:00.1234Z")]   // more than milliseconds: JavaScript's Date keeps milliseconds only
    [InlineData("2026-13-10T12:00:00Z")]        // the right form, but no such month
    [InlineData("2026-02-30T12:00:00Z")]
    public void A_time_not_in_the_one_utc_form_is_refused_wherever_it_is(string time)
    {
        Refused(PolicyDecisions, With(Good, c => c["now"] = time));
        Refused(PolicyDecisions, With(Good, c => c["input"]!["publishDates"]!["1.0.0"] = time));
        Refused(PolicyDecisions, With(Good, c => c["input"]!["policy"]!["waivers"]![0]!["expiresAt"] = time));
        Refused(PolicyDecisions, With(Good, c => c["expected"]!["publishedAt"] = time));
        Refused(TokenCheck, With(GoodToken, c => c["input"]!["tokens"]![0]!["expiresAt"] = time));
    }

    [Fact]
    public void Milliseconds_are_the_finest_time_allowed() =>
        Assert.Single(Parse(PolicyDecisions, With(Good, c => c["now"] = "2026-10-10T12:00:00.123Z")));

    [Theory]
    [InlineData("Cooldown")]           // the .NET name, not the code
    [InlineData("too_young")]
    [InlineData("")]
    public void An_unknown_reason_code_is_refused(string code)
    {
        Refused(PolicyDecisions, With(Good, c => c["expected"]!["reason"] = code));
        Refused(TokenCheck, With(GoodToken, c => c["expected"]!["reason"] = code));
        const string flat = """
            { "cases": [ { "name": "a", "now": "2026-10-10T12:00:00Z",
              "input": { "upstreamBase": "https://upstream.example.com/", "proxyBase": "https://packmoat.example.com/v3/u/",
                         "body": { "versions": [ "1.0.0" ] }, "verdicts": { "1.0.0": "CODE" } },
              "expected": { "status": 200, "body": { "versions": [ "1.0.0" ] } } } ] }
            """;
        Assert.Single(Parse(FlatContainer, flat.Replace("CODE", "waiver")));
        Assert.Contains("1.0.0", Refused(FlatContainer, flat.Replace("CODE", code)));
    }

    [Theory]
    [InlineData("store", "down")]
    [InlineData("gate", "maybe")]
    public void An_unknown_store_or_gate_value_is_refused(string field, string value) =>
        Refused(TokenCheck, With(GoodToken, c =>
        {
            if (field == "store") c["input"]!["store"] = value;
            else c["input"]!["tenants"]![0]!["gate"] = value;
        }));

    [Fact]
    public void A_duplicate_name_is_refused()
    {
        var file = JsonNode.Parse(Good)!.AsObject();
        file["cases"]!.AsArray().Add(file["cases"]![0]!.DeepClone());
        Assert.Contains("duplicate name", Refused(PolicyDecisions, file.ToJsonString()));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{ "cases": [] }""")]
    [InlineData("""{ "cases": [ 1 ] }""")]
    [InlineData("""{ "cases": [], "extra": 1 }""")]
    [InlineData("""not json""")]
    public void A_file_without_cases_or_not_json_is_refused(string json) => Refused(PolicyDecisions, json);

    [Fact]
    public void A_file_of_an_unknown_group_or_a_missing_group_is_refused()
    {
        var root = Directory.CreateTempSubdirectory("packmoat-vectors-");
        try
        {
            foreach (var group in Groups)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root.FullName, group + ".json"))!);
                File.Copy(Path.Combine(Root(), group + ".json"), Path.Combine(root.FullName, group + ".json"));
            }
            Assert.Equal(Groups.Order(StringComparer.Ordinal), LoadAll(root.FullName, SchemaRoot()).Keys);

            File.WriteAllText(Path.Combine(root.FullName, "nuget", "search.json"), Good);
            Assert.Contains("nuget/search.json", Assert.Throws<MalformedVectorException>(() => LoadAll(root.FullName, SchemaRoot())).Message);

            File.Delete(Path.Combine(root.FullName, "nuget", "search.json"));
            File.Delete(Path.Combine(root.FullName, "auth", "token-check.json"));
            Assert.Contains("auth/token-check", Assert.Throws<MalformedVectorException>(() => LoadAll(root.FullName, SchemaRoot())).Message);
        }
        catch when (TempDir.Failing(root.FullName)) { throw; }
        finally { TempDir.Delete(root.FullName); }
    }

    [Fact]
    public void The_shared_vectors_and_schemas_are_found_from_the_core_folder_and_all_load()
    {
        var spec = SpecRoot();
        // The core folder's own name is not checked: it is core/ here, but the public repository's checkout is named after the repository
        // What matters is that spec/ sits beside dotnet/.
        Assert.Equal("spec", Path.GetFileName(spec));
        Assert.True(File.Exists(Path.Combine(spec, "..", "dotnet", "Packmoat.sln")), "the spec sits beside dotnet/");
        var schemas = Directory.GetFiles(SchemaRoot(), "*.json", SearchOption.AllDirectories)
            .Select(f => Path.ChangeExtension(Path.GetRelativePath(SchemaRoot(), f), null).Replace('\\', '/'));
        Assert.Equal(Groups.Order(StringComparer.Ordinal), schemas.Order(StringComparer.Ordinal));
        Assert.All(LoadAll(Root(), SchemaRoot()).Values, cases => Assert.NotEmpty(cases));
    }

    /// <summary>Every vector file conforms to its group's JSON Schema, which a second implementation can check with any validator.</summary>
    [Fact]
    public void Every_vector_file_validates_against_its_schema()
    {
        foreach (var group in Groups)
            Assert.Empty(SchemaErrors(group, File.ReadAllText(Path.Combine(Root(), group + ".json")), SchemaRoot()));
    }

    [Fact]   // a schema's code list and this loader's code maps cannot drift apart
    public void The_schemas_list_exactly_the_codes_this_implementation_maps()
    {
        static string[] Enum(string group, string definition) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(SchemaRoot(), group + ".json")))!["definitions"]![definition]!["enum"]!
                .AsArray().Select(x => (string)x!).Order(StringComparer.Ordinal).ToArray();
        static string[] PropertyEnum(string group, string property) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(SchemaRoot(), group + ".json")))!["definitions"]!["expected"]!["properties"]![property]!["enum"]!
                .AsArray().Select(x => (string)x!).Order(StringComparer.Ordinal).ToArray();

        var verdictCodes = VerdictCodes.Values.Order(StringComparer.Ordinal).ToArray();
        foreach (var group in new[] { PolicyDecisions, FlatContainer, Registration, ServiceIndex })
            Assert.Equal(verdictCodes, Enum(group, "verdictCode"));
        Assert.Equal(TokenCodes.Order(StringComparer.Ordinal), PropertyEnum(TokenCheck, "reason"));
        Assert.Equal(KindCodes.Values.Order(StringComparer.Ordinal), PropertyEnum(Routes, "kind"));
    }

    [Fact]   // a new VerdictReason or route kind needs its own stable code before any vector can name it
    public void Every_verdict_reason_and_route_kind_has_one_distinct_stable_code()
    {
        Assert.Equal(Enum.GetValues<VerdictReason>().Order(), VerdictCodes.Keys.Order());
        Assert.Equal(Enum.GetValues<NuGetResourceKind>().Order(), KindCodes.Keys.Order());
        foreach (var codes in new[] { VerdictCodes.Values, KindCodes.Values, TokenCodes })
        {
            Assert.Equal(codes.Count(), codes.Distinct().Count());
            Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));
        }
    }

    [Fact]
    public void Outputs_compare_as_json_whatever_the_property_order_but_not_whatever_the_values()
    {
        AssertJson(JsonNode.Parse("""{ "a": 1, "b": [ "x", { "c": null } ] }""")!, JsonNode.Parse("""{"b":["x",{"c":null}],"a":1}""")!, "order");
        Assert.ThrowsAny<Exception>(() => AssertJson(JsonNode.Parse("""{ "a": 1 }""")!, JsonNode.Parse("""{ "a": 2 }""")!, "value"));
        Assert.ThrowsAny<Exception>(() => AssertJson(JsonNode.Parse("""{ "a": [1, 2] }""")!, JsonNode.Parse("""{ "a": [2, 1] }""")!, "array order"));
        Assert.ThrowsAny<Exception>(() => AssertJson(JsonNode.Parse("""{ "a": 1 }""")!, JsonNode.Parse("""{ "a": 1, "b": null }""")!, "extra field"));
    }

    [Fact]
    public void Times_are_written_in_utc_with_fractions_only_when_there_are_some()
    {
        Assert.Equal("2026-10-10T12:00:00Z", Format(new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.FromHours(2))));
        Assert.Equal("2026-10-10T12:00:00.5Z", Format(new DateTimeOffset(2026, 10, 10, 12, 0, 0, 500, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 12, 0, 0, 500, TimeSpan.Zero), Time("2026-10-10T12:00:00.5Z"));
    }
}

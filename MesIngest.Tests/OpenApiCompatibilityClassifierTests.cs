using System.Text.Json.Nodes;

namespace MesIngest.Tests;

public sealed class OpenApiCompatibilityClassifierTests
{
    [Fact]
    public void Compatibility_classifier_distinguishes_additive_breaking_and_documentation_drift()
    {
        var baseline = CreateBaseline();

        Assert.Equal(
            OpenApiCompatibilityKind.Exact,
            OpenApiCompatibilityClassifier.Classify(baseline, baseline.DeepClone()));

        var additive = baseline.DeepClone();
        Properties(additive)["note"] = JsonNode.Parse("""{"type":"string","nullable":true}""");
        Assert.Equal(
            OpenApiCompatibilityKind.Additive,
            OpenApiCompatibilityClassifier.Classify(baseline, additive));

        var breaking = baseline.DeepClone();
        Properties(breaking)["id"]!["type"] = "integer";
        Assert.Equal(
            OpenApiCompatibilityKind.Breaking,
            OpenApiCompatibilityClassifier.Classify(baseline, breaking));

        var documentationOnly = baseline.DeepClone();
        documentationOnly["info"]!["title"] = "Renamed reference document";
        documentationOnly["paths"]!["/api/v2/items"]!["get"]!["summary"] = "Reworded summary";
        Assert.Equal(
            OpenApiCompatibilityKind.DocumentationOnly,
            OpenApiCompatibilityClassifier.Classify(baseline, documentationOnly));
    }

    [Fact]
    public void Compatibility_classifier_marks_removed_paths_changed_types_new_required_and_enum_semantics_as_breaking()
    {
        var baseline = CreateBaseline();

        var removedPath = baseline.DeepClone();
        removedPath["paths"]!.AsObject().Remove("/api/v2/items");

        var changedType = baseline.DeepClone();
        Properties(changedType)["id"]!["type"] = "integer";

        var newRequired = baseline.DeepClone();
        Properties(newRequired)["note"] = JsonNode.Parse("""{"type":"string"}""");
        Schema(newRequired)["required"]!.AsArray().Add("note");

        var narrowedEnum = baseline.DeepClone();
        Properties(narrowedEnum)["state"]!["enum"] = JsonNode.Parse("""["ACTIVE"]""");

        var changedEnumMeaning = baseline.DeepClone();
        Properties(changedEnumMeaning)["state"]!["enum"] = JsonNode.Parse("""["ACTIVE","CLOSED"]""");

        Assert.All(
            new[] { removedPath, changedType, newRequired, narrowedEnum, changedEnumMeaning },
            candidate => Assert.Equal(
                OpenApiCompatibilityKind.Breaking,
                OpenApiCompatibilityClassifier.Classify(baseline, candidate)));
    }

    [Fact]
    public void Compatibility_classifier_treats_optional_properties_and_new_get_paths_as_additive()
    {
        var baseline = CreateBaseline();

        var optionalProperty = baseline.DeepClone();
        Properties(optionalProperty)["projectionCommit"] = JsonNode.Parse(
            """{"type":"integer","format":"int64"}""");

        var newGetPath = baseline.DeepClone();
        newGetPath["paths"]!["/api/v2/health"] = JsonNode.Parse(
            """
            {
              "get": {
                "responses": {
                  "200": {
                    "description": "Healthy"
                  }
                }
              }
            }
            """);

        Assert.Equal(
            OpenApiCompatibilityKind.Additive,
            OpenApiCompatibilityClassifier.Classify(baseline, optionalProperty));
        Assert.Equal(
            OpenApiCompatibilityKind.Additive,
            OpenApiCompatibilityClassifier.Classify(baseline, newGetPath));
    }

    [Fact]
    public void Compatibility_classifier_treats_only_openapi_documentation_keywords_as_documentation_drift()
    {
        var baseline = CreateBaseline();
        var candidate = baseline.DeepClone();

        candidate["info"]!["title"] = "Demand contract reference";
        candidate["info"]!["description"] = "Updated overview prose.";
        candidate["paths"]!["/api/v2/items"]!["get"]!["summary"] = "Updated operation prose";
        candidate["paths"]!["/api/v2/items"]!["get"]!["description"] = "More operation detail";
        Properties(candidate)["id"]!["example"] = "series-42";
        Properties(candidate)["state"]!["examples"] = JsonNode.Parse(
            """{"active":{"value":"ACTIVE"}}""");

        Assert.Equal(
            OpenApiCompatibilityKind.DocumentationOnly,
            OpenApiCompatibilityClassifier.Classify(baseline, candidate));
    }

    [Fact]
    public void Compatibility_classifier_is_deterministic_and_does_not_mutate_documents()
    {
        var baseline = CreateBaseline();
        var candidate = baseline.DeepClone();
        Properties(candidate)["note"] = JsonNode.Parse("""{"type":"string"}""");
        var baselineBefore = baseline.ToJsonString();
        var candidateBefore = candidate.ToJsonString();

        var first = OpenApiCompatibilityClassifier.Classify(baseline, candidate);
        var second = OpenApiCompatibilityClassifier.Classify(baseline, candidate);

        Assert.Equal(OpenApiCompatibilityKind.Additive, first);
        Assert.Equal(first, second);
        Assert.Equal(baselineBefore, baseline.ToJsonString());
        Assert.Equal(candidateBefore, candidate.ToJsonString());
    }

    [Fact]
    public void Canonical_contract_makes_additive_enum_category_endpoint_and_documentation_drift_observable()
    {
        var baseline = JsonNode.Parse(
            File.ReadAllText(Path.Combine(PackRoot, "openapi", "v2.json")))!;

        var additive = baseline.DeepClone();
        additive["components"]!["schemas"]!["NewMesIngestContractDto"]!["properties"]!
            ["optionalFutureNote"] = JsonNode.Parse("""{"type":"string","nullable":true}""");

        var enumReuse = baseline.DeepClone();
        enumReuse["components"]!["schemas"]!["SeriesErrorDefinitionDto"]!["properties"]!
            ["code"]!["enum"]![0] = "REPURPOSED_ERROR";

        var categoryChange = baseline.DeepClone();
        categoryChange["components"]!["schemas"]!["SeriesErrorDefinitionDto"]!["properties"]!
            ["category"]!["enum"]![0] = "REPURPOSED_CATEGORY";

        var categoryRemap = baseline.DeepClone();
        categoryRemap["x-mes-series-error-catalog"]![0]!["category"] = "DATA_FORMAT";

        var endpointFork = baseline.DeepClone();
        endpointFork["paths"]!["/api/v2/contract"]!["post"] =
            endpointFork["paths"]!["/api/v2/contract"]!["get"]!.DeepClone();

        var documentation = baseline.DeepClone();
        documentation["paths"]!["/api/v2/contract"]!["get"]!["description"] =
            "A deliberately changed explanation requiring snapshot review.";

        Assert.Equal(
            OpenApiCompatibilityKind.Additive,
            OpenApiCompatibilityClassifier.Classify(baseline, additive));
        Assert.Equal(
            OpenApiCompatibilityKind.Breaking,
            OpenApiCompatibilityClassifier.Classify(baseline, enumReuse));
        Assert.Equal(
            OpenApiCompatibilityKind.Breaking,
            OpenApiCompatibilityClassifier.Classify(baseline, categoryChange));
        Assert.Equal(
            OpenApiCompatibilityKind.Breaking,
            OpenApiCompatibilityClassifier.Classify(baseline, categoryRemap));
        Assert.Equal(
            OpenApiCompatibilityKind.Breaking,
            OpenApiCompatibilityClassifier.Classify(baseline, endpointFork));
        Assert.Equal(
            OpenApiCompatibilityKind.DocumentationOnly,
            OpenApiCompatibilityClassifier.Classify(baseline, documentation));
    }

    private static JsonNode CreateBaseline()
    {
        return JsonNode.Parse(
            """
            {
              "openapi": "3.0.1",
              "info": {
                "title": "New MES Ingest V2",
                "version": "2026.08.new-mes-ingest.v2.0",
                "description": "Frozen contract reference."
              },
              "paths": {
                "/api/v2/items": {
                  "get": {
                    "summary": "Lists items",
                    "responses": {
                      "200": {
                        "description": "Successful response",
                        "content": {
                          "application/json": {
                            "schema": {
                              "$ref": "#/components/schemas/Item"
                            }
                          }
                        }
                      }
                    }
                  }
                }
              },
              "components": {
                "schemas": {
                  "Item": {
                    "type": "object",
                    "required": ["id", "state"],
                    "properties": {
                      "id": {
                        "type": "string",
                        "description": "Stable item identity."
                      },
                      "state": {
                        "type": "string",
                        "enum": ["ACTIVE", "ENDED"]
                      }
                    }
                  }
                }
              }
            }
            """)!;
    }

    private static JsonObject Schema(JsonNode document) =>
        document["components"]!["schemas"]!["Item"]!.AsObject();

    private static JsonObject Properties(JsonNode document) =>
        Schema(document)["properties"]!.AsObject();

    private static string PackRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "pack", "openapi", "v2.json");
                if (File.Exists(candidate))
                {
                    return Path.GetDirectoryName(Path.GetDirectoryName(candidate))!;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate canonical V2 OpenAPI.");
        }
    }
}

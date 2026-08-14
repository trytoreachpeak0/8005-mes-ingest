using System.Text.Json.Nodes;

namespace MesIngest.Tests;

internal enum OpenApiCompatibilityKind
{
    Exact,
    DocumentationOnly,
    Additive,
    Breaking,
}

/// <summary>
/// Classifies a candidate OpenAPI document against a frozen baseline. The
/// implementation deliberately recognizes a small set of proven-safe additive
/// changes and treats every unknown structural change as breaking.
/// </summary>
internal static class OpenApiCompatibilityClassifier
{
    private static readonly HashSet<string> DocumentationKeywords = new(StringComparer.Ordinal)
    {
        "description",
        "example",
        "examples",
        "externalDocs",
        "summary",
        "title",
    };

    public static OpenApiCompatibilityKind Classify(JsonNode baseline, JsonNode candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (JsonNode.DeepEquals(baseline, candidate))
        {
            return OpenApiCompatibilityKind.Exact;
        }

        var baselineContract = RemoveDocumentation(baseline);
        var candidateContract = RemoveDocumentation(candidate);
        if (JsonNode.DeepEquals(baselineContract, candidateContract))
        {
            return OpenApiCompatibilityKind.DocumentationOnly;
        }

        return Compare(baselineContract, candidateContract, "$") == Difference.Additive
            ? OpenApiCompatibilityKind.Additive
            : OpenApiCompatibilityKind.Breaking;
    }

    private static Difference Compare(JsonNode? baseline, JsonNode? candidate, string path)
    {
        if (JsonNode.DeepEquals(baseline, candidate))
        {
            return Difference.Same;
        }

        if (baseline is null || candidate is null)
        {
            return Difference.Breaking;
        }

        if (baseline is JsonArray baselineArray && candidate is JsonArray candidateArray)
        {
            return path.EndsWith(".enum", StringComparison.Ordinal)
                ? CompareEnum(baselineArray, candidateArray)
                : Difference.Breaking;
        }

        if (baseline is not JsonObject baselineObject || candidate is not JsonObject candidateObject)
        {
            return Difference.Breaking;
        }

        if (path == "$.paths")
        {
            return ComparePaths(baselineObject, candidateObject, path);
        }

        if (path == "$.components.schemas")
        {
            return CompareSchemaCatalog(baselineObject, candidateObject, path);
        }

        if (LooksLikeObjectSchema(baselineObject) || LooksLikeObjectSchema(candidateObject))
        {
            return CompareObjectSchema(baselineObject, candidateObject, path);
        }

        return CompareObject(baselineObject, candidateObject, path);
    }

    private static Difference ComparePaths(
        JsonObject baseline,
        JsonObject candidate,
        string path)
    {
        var difference = Difference.Same;
        foreach (var (pathName, baselinePath) in baseline)
        {
            if (!candidate.TryGetPropertyValue(pathName, out var candidatePath))
            {
                return Difference.Breaking;
            }

            var pathDifference = ComparePathItem(
                baselinePath,
                candidatePath,
                $"{path}[{pathName}]");
            difference = Merge(difference, pathDifference);
            if (difference == Difference.Breaking)
            {
                return difference;
            }
        }

        foreach (var (pathName, candidatePath) in candidate)
        {
            if (baseline.ContainsKey(pathName))
            {
                continue;
            }

            if (!IsGetOnlyPath(candidatePath))
            {
                return Difference.Breaking;
            }

            difference = Difference.Additive;
        }

        return difference;
    }

    private static Difference ComparePathItem(
        JsonNode? baseline,
        JsonNode? candidate,
        string path)
    {
        if (JsonNode.DeepEquals(baseline, candidate))
        {
            return Difference.Same;
        }

        if (baseline is not JsonObject baselineObject || candidate is not JsonObject candidateObject)
        {
            return Difference.Breaking;
        }

        var difference = Difference.Same;
        foreach (var (key, baselineValue) in baselineObject)
        {
            if (!candidateObject.TryGetPropertyValue(key, out var candidateValue))
            {
                return Difference.Breaking;
            }

            difference = Merge(difference, Compare(baselineValue, candidateValue, $"{path}.{key}"));
            if (difference == Difference.Breaking)
            {
                return difference;
            }
        }

        foreach (var (key, candidateValue) in candidateObject)
        {
            if (baselineObject.ContainsKey(key))
            {
                continue;
            }

            if (!string.Equals(key, "get", StringComparison.Ordinal)
                || candidateValue is not JsonObject)
            {
                return Difference.Breaking;
            }

            difference = Difference.Additive;
        }

        return difference;
    }

    private static Difference CompareSchemaCatalog(
        JsonObject baseline,
        JsonObject candidate,
        string path)
    {
        var difference = Difference.Same;
        foreach (var (schemaName, baselineSchema) in baseline)
        {
            if (!candidate.TryGetPropertyValue(schemaName, out var candidateSchema))
            {
                return Difference.Breaking;
            }

            difference = Merge(
                difference,
                Compare(baselineSchema, candidateSchema, $"{path}.{schemaName}"));
            if (difference == Difference.Breaking)
            {
                return difference;
            }
        }

        if (candidate.Any(entry => !baseline.ContainsKey(entry.Key)))
        {
            difference = Difference.Additive;
        }

        return difference;
    }

    private static Difference CompareObjectSchema(
        JsonObject baseline,
        JsonObject candidate,
        string path)
    {
        if (!TryReadStringSet(baseline["required"], out var baselineRequired)
            || !TryReadStringSet(candidate["required"], out var candidateRequired))
        {
            return Difference.Breaking;
        }

        if (!candidateRequired.IsSubsetOf(baselineRequired))
        {
            return Difference.Breaking;
        }

        var difference = baselineRequired.SetEquals(candidateRequired)
            ? Difference.Same
            : Difference.Additive;

        var baselineProperties = baseline["properties"] as JsonObject;
        var candidateProperties = candidate["properties"] as JsonObject;
        if (baselineProperties is not null)
        {
            if (candidateProperties is null)
            {
                return Difference.Breaking;
            }

            foreach (var (propertyName, baselineProperty) in baselineProperties)
            {
                if (!candidateProperties.TryGetPropertyValue(propertyName, out var candidateProperty))
                {
                    return Difference.Breaking;
                }

                difference = Merge(
                    difference,
                    Compare(baselineProperty, candidateProperty, $"{path}.properties.{propertyName}"));
                if (difference == Difference.Breaking)
                {
                    return difference;
                }
            }
        }

        if (candidateProperties is not null)
        {
            foreach (var (propertyName, _) in candidateProperties)
            {
                if (baselineProperties?.ContainsKey(propertyName) == true)
                {
                    continue;
                }

                if (candidateRequired.Contains(propertyName))
                {
                    return Difference.Breaking;
                }

                difference = Difference.Additive;
            }
        }

        foreach (var (key, baselineValue) in baseline)
        {
            if (key is "properties" or "required")
            {
                continue;
            }

            if (!candidate.TryGetPropertyValue(key, out var candidateValue))
            {
                return Difference.Breaking;
            }

            difference = Merge(difference, Compare(baselineValue, candidateValue, $"{path}.{key}"));
            if (difference == Difference.Breaking)
            {
                return difference;
            }
        }

        foreach (var (key, _) in candidate)
        {
            if (key is "properties" or "required" || baseline.ContainsKey(key))
            {
                continue;
            }

            return Difference.Breaking;
        }

        return difference;
    }

    private static Difference CompareObject(
        JsonObject baseline,
        JsonObject candidate,
        string path)
    {
        var difference = Difference.Same;
        foreach (var (key, baselineValue) in baseline)
        {
            if (!candidate.TryGetPropertyValue(key, out var candidateValue))
            {
                return Difference.Breaking;
            }

            difference = Merge(difference, Compare(baselineValue, candidateValue, $"{path}.{key}"));
            if (difference == Difference.Breaking)
            {
                return difference;
            }
        }

        return candidate.Any(entry => !baseline.ContainsKey(entry.Key))
            ? Difference.Breaking
            : difference;
    }

    private static Difference CompareEnum(JsonArray baseline, JsonArray candidate)
    {
        foreach (var baselineValue in baseline)
        {
            if (!candidate.Any(candidateValue => JsonNode.DeepEquals(baselineValue, candidateValue)))
            {
                return Difference.Breaking;
            }
        }

        return candidate.Count > baseline.Count
            ? Difference.Additive
            : Difference.Same;
    }

    private static bool LooksLikeObjectSchema(JsonObject node)
    {
        return node.ContainsKey("properties")
            || node.ContainsKey("required")
            || node["type"]?.GetValue<string>() == "object";
    }

    private static bool IsGetOnlyPath(JsonNode? node)
    {
        if (node is not JsonObject path
            || path["get"] is not JsonObject)
        {
            return false;
        }

        return path.All(entry => string.Equals(entry.Key, "get", StringComparison.Ordinal));
    }

    private static bool TryReadStringSet(JsonNode? node, out HashSet<string> values)
    {
        values = new HashSet<string>(StringComparer.Ordinal);
        if (node is null)
        {
            return true;
        }

        if (node is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array)
        {
            if (item is not JsonValue value
                || !value.TryGetValue<string>(out var text)
                || !values.Add(text))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonNode RemoveDocumentation(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            var result = new JsonObject();
            foreach (var (key, value) in jsonObject.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (!DocumentationKeywords.Contains(key))
                {
                    result[key] = value is null ? null : RemoveDocumentation(value);
                }
            }

            return result;
        }

        if (node is JsonArray jsonArray)
        {
            var result = new JsonArray();
            foreach (var value in jsonArray)
            {
                result.Add(value is null ? null : RemoveDocumentation(value));
            }

            return result;
        }

        return node.DeepClone();
    }

    private static Difference Merge(Difference current, Difference next)
    {
        if (current == Difference.Breaking || next == Difference.Breaking)
        {
            return Difference.Breaking;
        }

        return current == Difference.Additive || next == Difference.Additive
            ? Difference.Additive
            : Difference.Same;
    }

    private enum Difference
    {
        Same,
        Additive,
        Breaking,
    }
}

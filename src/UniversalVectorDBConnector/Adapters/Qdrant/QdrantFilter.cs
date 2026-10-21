using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalVectorDBConnector.Core;

namespace UniversalVectorDBConnector.Adapters.Qdrant
{
    /// <summary>Translates the portable filter into a Qdrant filter object.</summary>
    internal static class QdrantFilter
    {
        public const string NamespaceKey = "_namespace";

        /// <summary>Builds the final Qdrant filter, or null when there is nothing to filter on.</summary>
        public static JsonObject? Build(JsonObject? portable, string ns, JsonArray? ids = null)
        {
            var must = new JsonArray();
            if (ids != null)
            {
                must.Add(new JsonObject { ["has_id"] = ids });
            }
            if (!string.IsNullOrEmpty(ns))
            {
                must.Add(Match(NamespaceKey, JsonValue.Create(ns)!));
            }
            if (portable != null)
            {
                must.Add(Translate(portable));
            }
            return must.Count == 0 ? null : new JsonObject { ["must"] = must };
        }

        /// <summary>Translates one portable filter object (implicit AND of its keys).</summary>
        public static JsonObject Translate(JsonObject portable)
        {
            var must = new JsonArray();
            foreach (var (key, value) in portable)
            {
                switch (key)
                {
                    case "$and":
                        must.Add(new JsonObject { ["must"] = SubFilters(key, value) });
                        break;
                    case "$or":
                        must.Add(new JsonObject { ["should"] = SubFilters(key, value) });
                        break;
                    case "$not":
                        if (value is not JsonObject notObj)
                        {
                            throw VdbException.Invalid("$not must contain a filter object.");
                        }
                        must.Add(new JsonObject { ["must_not"] = new JsonArray(Translate(notObj)) });
                        break;
                    default:
                        if (key.StartsWith('$'))
                        {
                            throw new VdbException(ErrorCodes.Unsupported, $"Filter operator {key} is not supported here.");
                        }
                        foreach (var c in FieldConditions(key, value))
                        {
                            must.Add(c);
                        }
                        break;
                }
            }
            return new JsonObject { ["must"] = must };
        }

        private static JsonArray SubFilters(string op, JsonNode? value)
        {
            if (value is not JsonArray arr || arr.Count == 0)
            {
                throw VdbException.Invalid($"{op} must be a non-empty array of filter objects.");
            }
            var result = new JsonArray();
            foreach (var item in arr)
            {
                if (item is not JsonObject o)
                {
                    throw VdbException.Invalid($"Every item in {op} must be a filter object.");
                }
                result.Add(Translate(o));
            }
            return result;
        }

        private static IEnumerable<JsonNode> FieldConditions(string field, JsonNode? value)
        {
            // Shorthand: {"field": value} means $eq.
            if (value is not JsonObject ops)
            {
                yield return Equal(field, value, negate: false);
                yield break;
            }

            var range = new JsonObject();
            bool rangeIsDate = false;
            foreach (var (op, operand) in ops)
            {
                switch (op)
                {
                    case "$eq":
                        yield return Equal(field, operand, negate: false);
                        break;
                    case "$ne":
                        yield return Equal(field, operand, negate: true);
                        break;
                    case "$in":
                    case "$nin":
                        if (operand is not JsonArray list || list.Count == 0)
                        {
                            throw VdbException.Invalid($"{op} on '{field}' needs a non-empty array.");
                        }
                        foreach (var item in list)
                        {
                            if (!IsString(item) && !IsInteger(item))
                            {
                                throw new VdbException(ErrorCodes.Unsupported, $"{op} on '{field}' supports only text or whole numbers in Qdrant.");
                            }
                        }
                        yield return new JsonObject
                        {
                            ["key"] = field,
                            ["match"] = new JsonObject { [op == "$in" ? "any" : "except"] = list.DeepClone() },
                        };
                        break;
                    case "$gt":
                    case "$gte":
                    case "$lt":
                    case "$lte":
                        if (IsString(operand))
                        {
                            rangeIsDate = true;
                        }
                        else if (!IsNumber(operand))
                        {
                            throw VdbException.Invalid($"{op} on '{field}' needs a number or an ISO date text.");
                        }
                        range[op.Substring(1)] = operand!.DeepClone();
                        break;
                    case "$exists":
                        if (operand is not JsonValue ev || !ev.TryGetValue<bool>(out var exists))
                        {
                            throw VdbException.Invalid($"$exists on '{field}' needs true or false.");
                        }
                        var isEmpty = new JsonObject { ["is_empty"] = new JsonObject { ["key"] = field } };
                        yield return exists ? new JsonObject { ["must_not"] = new JsonArray(isEmpty) } : isEmpty;
                        break;
                    default:
                        throw new VdbException(ErrorCodes.Unsupported, $"Filter operator {op} is not supported.");
                }
            }
            if (range.Count > 0)
            {
                yield return new JsonObject
                {
                    ["key"] = field,
                    [rangeIsDate ? "datetime_range" : "range"] = range,
                };
            }
        }

        private static JsonNode Equal(string field, JsonNode? operand, bool negate)
        {
            JsonNode condition;
            if (IsString(operand) || IsInteger(operand) || IsBool(operand))
            {
                condition = Match(field, operand!.DeepClone());
            }
            else if (IsNumber(operand))
            {
                // Qdrant cannot match decimals exactly; use a closed range instead.
                condition = new JsonObject
                {
                    ["key"] = field,
                    ["range"] = new JsonObject { ["gte"] = operand!.DeepClone(), ["lte"] = operand!.DeepClone() },
                };
            }
            else
            {
                throw VdbException.Invalid($"Equality on '{field}' needs text, a number or true/false.");
            }
            return negate ? new JsonObject { ["must_not"] = new JsonArray(condition) } : condition;
        }

        private static JsonObject Match(string field, JsonNode value) => new()
        {
            ["key"] = field,
            ["match"] = new JsonObject { ["value"] = value },
        };

        private static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;
        private static bool IsBool(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
        private static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
        private static bool IsInteger(JsonNode? n) => IsNumber(n) && ((JsonValue)n!).TryGetValue<long>(out _) && !n!.ToJsonString().Contains('.');
    }
}

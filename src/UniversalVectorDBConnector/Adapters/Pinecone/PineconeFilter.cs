using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalVectorDBConnector.Core;

namespace UniversalVectorDBConnector.Adapters.Pinecone
{
    /// <summary>
    /// Translates the portable filter into a Pinecone metadata filter.
    /// Pinecone's syntax is close to the portable one, with two differences handled here:
    /// range operators accept numbers only, and $not wraps a single field condition,
    /// so a portable $not over a whole filter is pushed down with De Morgan's rules.
    /// </summary>
    internal static class PineconeFilter
    {
        private const int MaxListValues = 10000;

        /// <summary>Returns the Pinecone filter, or null for an empty filter.</summary>
        public static JsonObject? Build(JsonObject? portable)
        {
            return portable == null ? null : Translate(portable);
        }

        /// <summary>Translates one portable filter object (implicit AND of its keys).</summary>
        public static JsonObject Translate(JsonObject portable)
        {
            var clauses = new List<JsonObject>();
            foreach (var (key, value) in portable)
            {
                switch (key)
                {
                    case "$and":
                        clauses.Add(new JsonObject { ["$and"] = ToArray(SubFilters(key, value)) });
                        break;
                    case "$or":
                        clauses.Add(new JsonObject { ["$or"] = ToArray(SubFilters(key, value)) });
                        break;
                    case "$not":
                        if (value is not JsonObject notObj || notObj.Count == 0)
                        {
                            throw VdbException.Invalid("$not must contain a filter object.");
                        }
                        clauses.Add(Negate(Translate(notObj)));
                        break;
                    default:
                        if (key.StartsWith('$'))
                        {
                            throw new VdbException(ErrorCodes.Unsupported, $"Filter operator {key} is not supported here.");
                        }
                        clauses.AddRange(FieldConditions(key, value));
                        break;
                }
            }
            if (clauses.Count == 0)
            {
                throw VdbException.Invalid("A filter object cannot be empty.");
            }
            return clauses.Count == 1 ? clauses[0] : new JsonObject { ["$and"] = ToArray(clauses) };
        }

        /// <summary>Negates a translated clause using only operators Pinecone supports.</summary>
        internal static JsonObject Negate(JsonObject clause)
        {
            var (key, value) = clause.Single();
            switch (key)
            {
                case "$and":
                    return new JsonObject { ["$or"] = ToArray(((JsonArray)value!).Select(c => Negate((JsonObject)c!))) };
                case "$or":
                    return new JsonObject { ["$and"] = ToArray(((JsonArray)value!).Select(c => Negate((JsonObject)c!))) };
                default:
                    var condition = (JsonObject)value!;
                    // Double negation: {"f":{"$not":{...}}} becomes {"f":{...}}.
                    if (condition.Count == 1 && condition["$not"] is JsonObject inner)
                    {
                        return new JsonObject { [key] = inner.DeepClone() };
                    }
                    return new JsonObject { [key] = new JsonObject { ["$not"] = condition.DeepClone() } };
            }
        }

        private static List<JsonObject> SubFilters(string op, JsonNode? value)
        {
            if (value is not JsonArray arr || arr.Count == 0)
            {
                throw VdbException.Invalid($"{op} must be a non-empty array of filter objects.");
            }
            var result = new List<JsonObject>();
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

        private static IEnumerable<JsonObject> FieldConditions(string field, JsonNode? value)
        {
            // Shorthand: {"field": value} means $eq.
            if (value is not JsonObject ops)
            {
                yield return Condition(field, "$eq", Scalar(field, "$eq", value));
                yield break;
            }
            if (ops.Count == 0)
            {
                throw VdbException.Invalid($"The condition on '{field}' is empty.");
            }

            foreach (var (op, operand) in ops)
            {
                switch (op)
                {
                    case "$eq":
                    case "$ne":
                        yield return Condition(field, op, Scalar(field, op, operand));
                        break;
                    case "$gt":
                    case "$gte":
                    case "$lt":
                    case "$lte":
                        if (IsString(operand))
                        {
                            throw new VdbException(ErrorCodes.Unsupported,
                                $"{op} on '{field}': Pinecone compares numbers only. Store dates as numbers, e.g. Unix seconds or yyyymmdd.");
                        }
                        if (!IsNumber(operand))
                        {
                            throw VdbException.Invalid($"{op} on '{field}' needs a number.");
                        }
                        yield return Condition(field, op, operand!.DeepClone());
                        break;
                    case "$in":
                    case "$nin":
                        if (operand is not JsonArray list || list.Count == 0)
                        {
                            throw VdbException.Invalid($"{op} on '{field}' needs a non-empty array.");
                        }
                        if (list.Count > MaxListValues)
                        {
                            throw VdbException.Invalid($"{op} on '{field}' can hold at most {MaxListValues} values in Pinecone.");
                        }
                        foreach (var item in list)
                        {
                            if (!IsString(item) && !IsNumber(item))
                            {
                                throw new VdbException(ErrorCodes.Unsupported, $"{op} on '{field}' supports only text or numbers in Pinecone.");
                            }
                        }
                        yield return Condition(field, op, list.DeepClone());
                        break;
                    case "$exists":
                        if (operand is not JsonValue ev || !ev.TryGetValue<bool>(out var exists))
                        {
                            throw VdbException.Invalid($"$exists on '{field}' needs true or false.");
                        }
                        yield return Condition(field, "$exists", JsonValue.Create(exists)!);
                        break;
                    default:
                        throw new VdbException(ErrorCodes.Unsupported, $"Filter operator {op} is not supported.");
                }
            }
        }

        private static JsonNode Scalar(string field, string op, JsonNode? operand)
        {
            if (IsString(operand) || IsNumber(operand) || IsBool(operand))
            {
                return operand!.DeepClone();
            }
            if (operand is JsonArray)
            {
                throw VdbException.Invalid($"{op} on '{field}' needs a single value. Use $in to match a list.");
            }
            throw VdbException.Invalid($"{op} on '{field}' needs text, a number or true/false.");
        }

        private static JsonObject Condition(string field, string op, JsonNode operand) => new()
        {
            [field] = new JsonObject { [op] = operand },
        };

        private static JsonArray ToArray(IEnumerable<JsonObject> items)
        {
            var arr = new JsonArray();
            foreach (var i in items)
            {
                arr.Add(i);
            }
            return arr;
        }

        private static bool IsString(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String;
        private static bool IsBool(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
        private static bool IsNumber(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number;
    }
}

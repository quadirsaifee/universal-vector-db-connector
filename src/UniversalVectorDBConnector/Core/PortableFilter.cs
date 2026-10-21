using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalVectorDBConnector.Core
{
    /// <summary>Parses the portable filter JSON shared by every provider.</summary>
    internal static class PortableFilter
    {
        public static readonly HashSet<string> FieldOperators = new() { "$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$exists" };

        /// <summary>Returns null for an empty filter.</summary>
        public static JsonObject? Parse(string? filterJson)
        {
            if (string.IsNullOrWhiteSpace(filterJson))
            {
                return null;
            }
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(filterJson);
            }
            catch (JsonException)
            {
                throw VdbException.Invalid("FilterJson is not valid JSON.");
            }
            if (node is not JsonObject obj)
            {
                throw VdbException.Invalid("FilterJson must be a JSON object.");
            }
            return obj.Count == 0 ? null : obj;
        }
    }
}

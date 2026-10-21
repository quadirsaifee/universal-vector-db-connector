using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalVectorDBConnector.Core
{
    /// <summary>Validated connection settings plus the time budget for one action call.</summary>
    internal sealed class CallContext : IDisposable
    {
        public const int DefaultTimeoutSeconds = 30;
        public const int MaxTimeoutSeconds = 85; // ODC stops external actions at 95 s.

        private readonly CancellationTokenSource _cts;

        public string Provider { get; }
        public Uri Endpoint { get; }
        public string ApiKey { get; }
        public string Database { get; }
        public JsonObject Options { get; }
        public CancellationToken Token => _cts.Token;
        public DateTime Deadline { get; }

        private CallContext(string provider, Uri endpoint, string apiKey, string database, JsonObject options, int timeoutSeconds)
        {
            Provider = provider;
            Endpoint = endpoint;
            ApiKey = apiKey;
            Database = database;
            Options = options;
            Deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        }

        public TimeSpan Remaining => Deadline - DateTime.UtcNow;

        public static CallContext Create(VdbConnection c)
        {
            var provider = (c.Provider ?? "").Trim().ToLowerInvariant();
            if (provider.Length == 0)
            {
                throw VdbException.Invalid("Connection.Provider is required, e.g. qdrant.");
            }

            var options = ParseOptions(c.OptionsJson);

            var endpointText = (c.Endpoint ?? "").Trim().TrimEnd('/');
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            {
                throw VdbException.Invalid("Connection.Endpoint must be an absolute http(s) URL.");
            }
            if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback && !GetBool(options, "allowInsecure"))
            {
                throw VdbException.Invalid("Connection.Endpoint must use https. For local testing only, set OptionsJson to {\"allowInsecure\":true}.");
            }

            var timeout = c.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : Math.Min(c.TimeoutSeconds, MaxTimeoutSeconds);
            return new CallContext(provider, endpoint, c.ApiKey ?? "", c.Database ?? "", options, timeout);
        }

        private static JsonObject ParseOptions(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JsonObject();
            }
            try
            {
                return JsonNode.Parse(json) as JsonObject
                    ?? throw VdbException.Invalid("Connection.OptionsJson must be a JSON object.");
            }
            catch (JsonException)
            {
                throw VdbException.Invalid("Connection.OptionsJson is not valid JSON.");
            }
        }

        private static bool GetBool(JsonObject o, string key)
        {
            return o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
        }

        public void Dispose() => _cts.Dispose();
    }
}

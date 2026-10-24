using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniversalVectorDBConnector.Core;

namespace UniversalVectorDBConnector.Adapters.Pinecone
{
    /// <summary>
    /// Pinecone serverless over its REST API, version 2026-07.
    /// Connection.Endpoint is the control plane (https://api.pinecone.io). Each index has its own
    /// data-plane host, which is looked up once per index and cached.
    /// </summary>
    internal sealed partial class PineconeAdapter : IVectorStoreAdapter
    {
        public const string ApiVersion = "2026-07";

        private const string IdKey = "_id";      // original id, stored only when it had to be mapped
        private const string TextKey = "_text";
        private const string VectorField = "_values"; // reserved schema name of the dense vector field
        private const int UpsertBatchSize = 1000;
        private const int UpsertBatchBytes = 1_800_000; // Pinecone allows 2 MB per request
        private const int FetchBatchSize = 100;         // keeps the GET url short
        private const int DeleteBatchSize = 1000;
        private const int MaxTopK = 1000;

        // Index host and metric, cached per control endpoint + index.
        private static readonly ConcurrentDictionary<string, (IndexInfo Info, DateTime Expires)> InfoCache = new();

        private sealed record IndexInfo(string Host, string Metric);

        public VdbCapabilities Capabilities => new()
        {
            Provider = "pinecone",
            Implemented = true,
            Namespaces = true,
            MetadataFilters = true,
            DeleteByFilter = true,
            HybridSearch = false,
            MaxTopK = MaxTopK,
        };

        public string TestConnection(CallContext ctx)
        {
            Send(ctx, HttpMethod.Get, "/indexes", null);
            return $"pinecone (api {ApiVersion})";
        }

        public void CreateCollection(CallContext ctx, VdbCollectionSpec spec)
        {
            var name = CheckName(spec.Name);
            if (spec.Dimension <= 0 || spec.Dimension > 20000)
            {
                throw VdbException.Invalid("Spec.Dimension must be between 1 and 20000 for Pinecone.");
            }
            var metric = (spec.Metric ?? "").Trim().ToLowerInvariant() switch
            {
                "" or "cosine" => "cosine",
                "dot" or "dotproduct" => "dotproduct",
                "euclidean" or "euclid" or "l2" => "euclidean",
                var m => throw VdbException.Invalid($"Spec.Metric '{m}' is not supported. Use cosine, dot or euclidean."),
            };
            // Pinecone indexes every metadata field automatically, so index definitions are only validated.
            ValidateIndexes(spec.MetadataIndexesJson);

            var body = new JsonObject
            {
                ["name"] = name,
                ["deployment"] = new JsonObject
                {
                    ["deployment_type"] = "managed",
                    ["cloud"] = OptionText(ctx, "cloud", "aws"),
                    ["region"] = OptionText(ctx, "region", "us-east-1"),
                },
                ["schema"] = new JsonObject
                {
                    ["fields"] = new JsonObject
                    {
                        [VectorField] = new JsonObject { ["type"] = "dense_vector", ["dimension"] = spec.Dimension, ["metric"] = metric },
                    },
                },
                ["deletion_protection"] = OptionBool(ctx, "deletionProtection") ? "enabled" : "disabled",
            };
            var created = Send(ctx, HttpMethod.Post, "/indexes", body);
            InfoCache.TryRemove(CacheKey(ctx, name), out _);
            WaitUntilReady(ctx, name, created);
        }

        public void DeleteCollection(CallContext ctx, string name)
        {
            name = CheckName(name);
            InfoCache.TryRemove(CacheKey(ctx, name), out _);
            Send(ctx, HttpMethod.Delete, $"/indexes/{Path(name)}", null);
        }

        public List<string> ListCollections(CallContext ctx)
        {
            var result = Send(ctx, HttpMethod.Get, "/indexes", null);
            var names = new List<string>();
            if (result?["indexes"] is JsonArray arr)
            {
                foreach (var i in arr)
                {
                    var n = i?["name"]?.GetValue<string>();
                    if (n != null)
                    {
                        names.Add(n);
                    }
                }
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public bool CollectionExists(CallContext ctx, string name)
        {
            name = CheckName(name);
            try
            {
                Send(ctx, HttpMethod.Get, $"/indexes/{Path(name)}", null);
                return true;
            }
            catch (VdbException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                return false;
            }
        }

        public int Upsert(CallContext ctx, string collection, IReadOnlyList<NormalizedRecord> records, string ns)
        {
            collection = CheckName(collection);
            ns = CheckNamespace(ns);
            var info = GetInfo(ctx, collection);

            int written = 0;
            var batch = new JsonArray();
            int batchBytes = 0;
            void Flush()
            {
                if (batch.Count == 0)
                {
                    return;
                }
                var body = new JsonObject { ["vectors"] = batch };
                if (ns.Length > 0)
                {
                    body["namespace"] = ns;
                }
                var result = SendData(ctx, info, HttpMethod.Post, "/vectors/upsert", body);
                written += result?["upsertedCount"]?.GetValue<int>() ?? batch.Count;
                batch = new JsonArray();
                batchBytes = 0;
            }

            for (int i = 0; i < records.Count; i++)
            {
                var vector = ToPineconeVector(records[i], $"Records[{i}]");
                var bytes = Encoding.UTF8.GetByteCount(vector.ToJsonString());
                if (bytes > UpsertBatchBytes)
                {
                    throw new VdbException(ErrorCodes.PayloadTooLarge, $"Records[{i}] is larger than Pinecone's 2 MB request limit.");
                }
                if (batch.Count >= UpsertBatchSize || batchBytes + bytes > UpsertBatchBytes)
                {
                    Flush();
                }
                batch.Add(vector);
                batchBytes += bytes;
            }
            Flush();
            return written;
        }

        public List<VdbMatch> Query(CallContext ctx, VdbQuery query, float[] vector)
        {
            var collection = CheckName(query.Collection);
            var ns = CheckNamespace(query.Namespace);
            var topK = query.TopK <= 0 ? 10 : query.TopK;
            if (topK > MaxTopK)
            {
                throw VdbException.Invalid($"Query.TopK cannot exceed {MaxTopK}.");
            }
            var filter = PineconeFilter.Build(PortableFilter.Parse(query.FilterJson));
            var info = GetInfo(ctx, collection);

            var body = new JsonObject
            {
                ["vector"] = ToJsonArray(vector),
                ["topK"] = topK,
                ["includeMetadata"] = true,
                ["includeValues"] = query.IncludeVectors,
            };
            if (ns.Length > 0)
            {
                body["namespace"] = ns;
            }
            if (filter != null)
            {
                body["filter"] = filter;
            }

            var result = SendData(ctx, info, HttpMethod.Post, "/query", body);
            var matches = new List<VdbMatch>();
            if (result?["matches"] is not JsonArray items)
            {
                return matches;
            }
            foreach (var item in items)
            {
                if (item is not JsonObject match)
                {
                    continue;
                }
                var score = match["score"]?.GetValue<double>() ?? 0;
                var similarity = Normalize(info.Metric, score);
                if (query.MinScore > 0 && (decimal)similarity < query.MinScore)
                {
                    continue;
                }
                var (id, text, metadata) = ReadRecord(match);
                matches.Add(new VdbMatch
                {
                    Id = id,
                    Score = (decimal)score,
                    Similarity = (decimal)similarity,
                    Text = text,
                    MetadataJson = metadata,
                    Vector = query.IncludeVectors ? ReadVector(match["values"]) : new List<decimal>(),
                });
            }
            return matches;
        }

        public List<VdbRecord> Fetch(CallContext ctx, string collection, IReadOnlyList<string> ids, string ns, bool includeVectors)
        {
            collection = CheckName(collection);
            ns = CheckNamespace(ns);
            var info = GetInfo(ctx, collection);
            var records = new List<VdbRecord>();
            foreach (var chunk in ids.Select(id => IdMapper.ToPineconeId(id, out _)).Chunk(FetchBatchSize))
            {
                var query = new StringBuilder("/vectors/fetch?");
                query.AppendJoin('&', chunk.Select(id => "ids=" + Uri.EscapeDataString(id)));
                if (ns.Length > 0)
                {
                    query.Append("&namespace=").Append(Uri.EscapeDataString(ns));
                }
                var result = SendData(ctx, info, HttpMethod.Get, query.ToString(), null);
                if (result?["vectors"] is not JsonObject vectors)
                {
                    continue;
                }
                // Keep the order of the requested ids.
                foreach (var pid in chunk)
                {
                    if (vectors[pid] is not JsonObject v)
                    {
                        continue;
                    }
                    var (id, text, metadata) = ReadRecord(v);
                    records.Add(new VdbRecord
                    {
                        Id = id,
                        Text = text,
                        MetadataJson = metadata,
                        Vector = includeVectors ? ReadVector(v["values"]) : new List<decimal>(),
                        VectorBase64 = "",
                    });
                }
            }
            return records;
        }

        public void Delete(CallContext ctx, string collection, IReadOnlyList<string> ids, string ns)
        {
            collection = CheckName(collection);
            ns = CheckNamespace(ns);
            var info = GetInfo(ctx, collection);
            foreach (var chunk in ids.Select(id => IdMapper.ToPineconeId(id, out _)).Chunk(DeleteBatchSize))
            {
                var body = new JsonObject { ["ids"] = new JsonArray(chunk.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()) };
                if (ns.Length > 0)
                {
                    body["namespace"] = ns;
                }
                SendData(ctx, info, HttpMethod.Post, "/vectors/delete", body);
            }
        }

        public void DeleteByFilter(CallContext ctx, string collection, string filterJson, string ns)
        {
            collection = CheckName(collection);
            ns = CheckNamespace(ns);
            var portable = PortableFilter.Parse(filterJson)
                ?? throw VdbException.Invalid("FilterJson cannot be empty for DeleteByFilter. Use DeleteCollection to remove everything.");
            var body = new JsonObject { ["filter"] = PineconeFilter.Translate(portable) };
            if (ns.Length > 0)
            {
                body["namespace"] = ns;
            }
            SendData(ctx, GetInfo(ctx, collection), HttpMethod.Post, "/vectors/delete", body);
        }

        public long Count(CallContext ctx, string collection, string ns, string filterJson)
        {
            collection = CheckName(collection);
            ns = CheckNamespace(ns);
            if (PortableFilter.Parse(filterJson) != null)
            {
                throw new VdbException(ErrorCodes.Unsupported, "Pinecone serverless indexes cannot count by metadata filter. Count without a filter, or use Query.");
            }
            var result = SendData(ctx, GetInfo(ctx, collection), HttpMethod.Post, "/describe_index_stats", new JsonObject());
            if (ns.Length == 0)
            {
                return result?["totalVectorCount"]?.GetValue<long>() ?? 0;
            }
            return result?["namespaces"]?[ns]?["vectorCount"]?.GetValue<long>() ?? 0;
        }

        // ---------- helpers ----------

        /// <summary>Maps a Pinecone score to 0-1 where possible. Euclidean scores are squared distances (lower is closer).</summary>
        internal static double Normalize(string metric, double score) => metric switch
        {
            "cosine" => Math.Clamp((score + 1) / 2, 0, 1),
            "euclidean" => 1 / (1 + Math.Sqrt(Math.Max(score, 0))),
            _ => score, // dotproduct: unbounded, left as is
        };

        internal static JsonObject ToPineconeVector(NormalizedRecord r, string where)
        {
            var pid = IdMapper.ToPineconeId(r.Id, out var mapped);
            var metadata = new JsonObject();
            foreach (var (key, value) in r.Metadata)
            {
                if (key.StartsWith('$'))
                {
                    throw VdbException.Invalid($"{where}.MetadataJson key '{key}' cannot start with $.");
                }
                if (value == null)
                {
                    continue; // Pinecone has no null: leaving the key out means the same thing
                }
                CheckMetadataValue(value, $"{where}.MetadataJson '{key}'");
                metadata[key] = value.DeepClone();
            }
            if (!string.IsNullOrEmpty(r.Text))
            {
                metadata[TextKey] = r.Text;
            }
            if (mapped)
            {
                metadata[IdKey] = r.Id;
            }
            var vector = new JsonObject
            {
                ["id"] = pid,
                ["values"] = ToJsonArray(r.Vector),
            };
            if (metadata.Count > 0)
            {
                vector["metadata"] = metadata;
            }
            return vector;
        }

        private static void CheckMetadataValue(JsonNode value, string where)
        {
            switch (value)
            {
                case JsonValue v when v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    return;
                case JsonArray arr when arr.All(i => i is JsonValue iv && iv.GetValueKind() == JsonValueKind.String):
                    return;
                case JsonArray:
                    throw VdbException.Invalid($"{where}: Pinecone lists can only hold text.");
                case JsonObject:
                    throw VdbException.Invalid($"{where}: Pinecone does not support nested objects. Flatten it, e.g. \"address_city\".");
                default:
                    throw VdbException.Invalid($"{where}: unsupported value.");
            }
        }

        private static (string Id, string Text, string MetadataJson) ReadRecord(JsonObject vector)
        {
            var stored = vector["metadata"] as JsonObject ?? new JsonObject();
            var id = stored[IdKey]?.ToString() ?? vector["id"]?.ToString() ?? "";
            var text = stored[TextKey]?.ToString() ?? "";
            var metadata = new JsonObject();
            foreach (var (k, v) in stored)
            {
                if (k is IdKey or TextKey)
                {
                    continue;
                }
                metadata[k] = v?.DeepClone();
            }
            return (id, text, metadata.ToJsonString());
        }

        private static List<decimal> ReadVector(JsonNode? node)
        {
            return node is JsonArray arr
                ? VectorCodec.ToDecimals(arr.Select(v => v?.GetValue<float>() ?? 0f))
                : new List<decimal>();
        }

        private static JsonArray ToJsonArray(float[] vector)
        {
            var arr = new JsonArray();
            foreach (var f in vector)
            {
                if (!float.IsFinite(f))
                {
                    throw VdbException.Invalid("Vectors cannot contain NaN or infinite values.");
                }
                arr.Add(f);
            }
            return arr;
        }

        private IndexInfo GetInfo(CallContext ctx, string index)
        {
            var key = CacheKey(ctx, index);
            if (InfoCache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                return cached.Info;
            }
            var info = ParseInfo(Send(ctx, HttpMethod.Get, $"/indexes/{Path(index)}", null), index);
            InfoCache[key] = (info, DateTime.UtcNow.AddMinutes(5));
            return info;
        }

        private static IndexInfo ParseInfo(JsonNode? model, string index)
        {
            var host = model?["host"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new VdbException(ErrorCodes.ProviderError, $"Pinecone did not return a host for index '{index}'.");
            }
            string? metric = null;
            if (model?["schema"]?["fields"] is JsonObject fields)
            {
                var dense = fields[VectorField] as JsonObject
                    ?? fields.Select(f => f.Value as JsonObject).FirstOrDefault(f => f?["type"]?.ToString() == "dense_vector");
                metric = dense?["metric"]?.ToString();
            }
            if (metric == null)
            {
                throw new VdbException(ErrorCodes.Unsupported, $"Pinecone index '{index}' has no dense vector field. Sparse-only and document indexes are not supported.");
            }
            return new IndexInfo(host, metric);
        }

        private void WaitUntilReady(CallContext ctx, string name, JsonNode? model)
        {
            // Serverless indexes usually become ready within seconds. Wait while the time budget allows;
            // if it runs out, the index still exists and becomes usable shortly after.
            while (model?["status"]?["ready"]?.GetValue<bool>() != true && ctx.Remaining > TimeSpan.FromSeconds(4))
            {
                try
                {
                    Task.Delay(TimeSpan.FromSeconds(1), ctx.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                model = Send(ctx, HttpMethod.Get, $"/indexes/{Path(name)}", null);
            }
            if (model?["status"]?["ready"]?.GetValue<bool>() == true)
            {
                InfoCache[CacheKey(ctx, name)] = (ParseInfo(model, name), DateTime.UtcNow.AddMinutes(5));
            }
        }

        private static void ValidateIndexes(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }
            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(json) as JsonObject;
            }
            catch (JsonException)
            {
                obj = null;
            }
            if (obj == null)
            {
                throw VdbException.Invalid("Spec.MetadataIndexesJson must be a JSON object like {\"category\":\"keyword\"}.");
            }
            foreach (var (field, _) in obj)
            {
                if (field.StartsWith('_'))
                {
                    throw VdbException.Invalid($"Metadata field '{field}' is reserved (fields starting with _ are used by the connector).");
                }
            }
        }

        private static string CheckName(string? name)
        {
            name = (name ?? "").Trim();
            if (!NameRegex().IsMatch(name))
            {
                throw VdbException.Invalid("Pinecone index names must be 1-45 characters: lowercase letters, digits or -, not starting or ending with -.");
            }
            return name;
        }

        private static string CheckNamespace(string? ns)
        {
            ns ??= "";
            if (ns.Length > 512 || ns.Any(ch => ch is '\0' or > '\x7F'))
            {
                throw VdbException.Invalid("Pinecone namespaces must be at most 512 ASCII characters.");
            }
            return ns;
        }

        private static string OptionText(CallContext ctx, string key, string fallback)
        {
            return ctx.Options[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : fallback;
        }

        private static bool OptionBool(CallContext ctx, string key)
        {
            return ctx.Options[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        }

        private static string Path(string name) => Uri.EscapeDataString(name);

        private static string CacheKey(CallContext ctx, string index) => ctx.Endpoint + "|" + ctx.ApiKey.GetHashCode() + "|" + index;

        /// <summary>Data-plane base url. Follows the control plane's scheme, so local http mocks work.</summary>
        private static Uri DataUri(CallContext ctx, string host)
        {
            if (host.Contains("://", StringComparison.Ordinal))
            {
                return new Uri(host);
            }
            return new Uri($"{ctx.Endpoint.Scheme}://{host}");
        }

        private static JsonNode? Send(CallContext ctx, HttpMethod method, string path, JsonNode? body)
        {
            return HttpJson.Send(ctx, method, path, body, request => AddHeaders(ctx, request), MapError);
        }

        private JsonNode? SendData(CallContext ctx, IndexInfo info, HttpMethod method, string path, JsonNode? body)
        {
            try
            {
                return HttpJson.Send(ctx, method, path, body, request => AddHeaders(ctx, request), MapError, DataUri(ctx, info.Host));
            }
            catch (VdbException ex) when (ex.Code == ErrorCodes.NotFound || ex.Code == ErrorCodes.NetworkError)
            {
                // The index may have been deleted or recreated: forget its cached host.
                foreach (var key in InfoCache.Where(kv => kv.Value.Info == info).Select(kv => kv.Key).ToList())
                {
                    InfoCache.TryRemove(key, out _);
                }
                throw;
            }
        }

        private static void AddHeaders(CallContext ctx, HttpRequestMessage request)
        {
            request.Headers.Add("X-Pinecone-Api-Version", ApiVersion);
            if (!string.IsNullOrEmpty(ctx.ApiKey))
            {
                request.Headers.Add("Api-Key", ctx.ApiKey);
            }
        }

        internal static VdbException MapError(int status, string body)
        {
            string message = body;
            string? reason = null;
            try
            {
                var node = JsonNode.Parse(body);
                // Control plane: {"error":{"code":"NOT_FOUND","message":"..."},"status":404}
                // Data plane:    {"code":5,"message":"...","details":[]}
                var error = node?["error"] as JsonObject;
                message = error?["message"]?.ToString() ?? node?["message"]?.ToString() ?? body;
                reason = error?["code"]?.ToString();
            }
            catch (JsonException)
            {
                // keep raw text, e.g. "Unauthorized"
            }
            if (message.Length > 500)
            {
                message = message[..500];
            }
            var code = HttpJson.CodeFor(status);
            if (reason == "ALREADY_EXISTS" || message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                code = ErrorCodes.AlreadyExists;
            }
            else if (reason == "NOT_FOUND" || status == 404)
            {
                code = ErrorCodes.NotFound;
            }
            return new VdbException(code, $"Pinecone: {message}", status);
        }

        [GeneratedRegex("^(?=.{1,45}$)[a-z0-9]([a-z0-9-]*[a-z0-9])?$")]
        private static partial Regex NameRegex();
    }
}

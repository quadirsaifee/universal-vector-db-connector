using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniversalVectorDBConnector.Core;

namespace UniversalVectorDBConnector.Adapters.Qdrant
{
    /// <summary>Qdrant over its REST API (Qdrant Cloud or self-hosted, port 6333).</summary>
    internal sealed partial class QdrantAdapter : IVectorStoreAdapter
    {
        private const string IdKey = "_id";
        private const string TextKey = "_text";
        private const int UpsertBatchSize = 256;
        private const int MaxTopK = 1000;

        // Collection info (metric, named vector) cached per endpoint + collection.
        private static readonly ConcurrentDictionary<string, (CollectionInfo Info, DateTime Expires)> InfoCache = new();

        private sealed record CollectionInfo(string Distance, string? VectorName);

        public VdbCapabilities Capabilities => new()
        {
            Provider = "qdrant",
            Implemented = true,
            Namespaces = true,
            MetadataFilters = true,
            DeleteByFilter = true,
            HybridSearch = false,
            MaxTopK = MaxTopK,
        };

        public string TestConnection(CallContext ctx)
        {
            var root = Send(ctx, HttpMethod.Get, "/", null);
            // The root may be public; listing collections proves the API key works.
            Send(ctx, HttpMethod.Get, "/collections", null);
            var version = root?["version"]?.GetValue<string>();
            return version == null ? "qdrant" : $"qdrant {version}";
        }

        public void CreateCollection(CallContext ctx, VdbCollectionSpec spec)
        {
            var name = CheckName(spec.Name);
            if (spec.Dimension <= 0 || spec.Dimension > 65536)
            {
                throw VdbException.Invalid("Spec.Dimension must be between 1 and 65536.");
            }
            var distance = (spec.Metric ?? "").Trim().ToLowerInvariant() switch
            {
                "" or "cosine" => "Cosine",
                "dot" or "dotproduct" => "Dot",
                "euclidean" or "euclid" or "l2" => "Euclid",
                var m => throw VdbException.Invalid($"Spec.Metric '{m}' is not supported. Use cosine, dot or euclidean."),
            };

            // Validate index definitions before creating anything.
            var indexes = ParseIndexes(spec.MetadataIndexesJson);

            Send(ctx, HttpMethod.Put, $"/collections/{Path(name)}", new JsonObject
            {
                ["vectors"] = new JsonObject { ["size"] = spec.Dimension, ["distance"] = distance },
            });
            InfoCache.TryRemove(CacheKey(ctx, name), out _);

            CreateIndex(ctx, name, QdrantFilter.NamespaceKey, "keyword");
            foreach (var (field, schema) in indexes)
            {
                CreateIndex(ctx, name, field, schema);
            }
        }

        public void DeleteCollection(CallContext ctx, string name)
        {
            name = CheckName(name);
            var result = Send(ctx, HttpMethod.Delete, $"/collections/{Path(name)}", null);
            InfoCache.TryRemove(CacheKey(ctx, name), out _);
            // Qdrant answers 200 with result=false when the collection did not exist.
            if (result?["result"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok)
            {
                throw new VdbException(ErrorCodes.NotFound, $"Collection '{name}' was not found.", 404);
            }
        }

        public List<string> ListCollections(CallContext ctx)
        {
            var result = Send(ctx, HttpMethod.Get, "/collections", null);
            var names = new List<string>();
            if (result?["result"]?["collections"] is JsonArray arr)
            {
                foreach (var c in arr)
                {
                    var n = c?["name"]?.GetValue<string>();
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
                var result = Send(ctx, HttpMethod.Get, $"/collections/{Path(name)}/exists", null);
                return result?["result"]?["exists"]?.GetValue<bool>() ?? false;
            }
            catch (VdbException ex) when (ex.HttpStatus == 404)
            {
                // Older Qdrant versions lack /exists: fall back to describing the collection.
                try
                {
                    Send(ctx, HttpMethod.Get, $"/collections/{Path(name)}", null);
                    return true;
                }
                catch (VdbException inner) when (inner.Code == ErrorCodes.NotFound)
                {
                    return false;
                }
            }
        }

        public int Upsert(CallContext ctx, string collection, IReadOnlyList<NormalizedRecord> records, string ns)
        {
            collection = CheckName(collection);
            var info = GetInfo(ctx, collection);
            int written = 0;
            for (int start = 0; start < records.Count; start += UpsertBatchSize)
            {
                var points = new JsonArray();
                foreach (var r in records.Skip(start).Take(UpsertBatchSize))
                {
                    var payload = (JsonObject)r.Metadata.DeepClone();
                    payload[IdKey] = r.Id;
                    if (!string.IsNullOrEmpty(r.Text))
                    {
                        payload[TextKey] = r.Text;
                    }
                    if (!string.IsNullOrEmpty(ns))
                    {
                        payload[QdrantFilter.NamespaceKey] = ns;
                    }
                    var vector = ToJsonArray(r.Vector);
                    points.Add(new JsonObject
                    {
                        ["id"] = IdMapper.ToQdrantId(r.Id),
                        ["vector"] = info.VectorName == null ? vector : new JsonObject { [info.VectorName] = vector },
                        ["payload"] = payload,
                    });
                }
                Send(ctx, HttpMethod.Put, $"/collections/{Path(collection)}/points?wait=true", new JsonObject { ["points"] = points });
                written += points.Count;
            }
            return written;
        }

        public List<VdbMatch> Query(CallContext ctx, VdbQuery query, float[] vector)
        {
            var collection = CheckName(query.Collection);
            var topK = query.TopK <= 0 ? 10 : query.TopK;
            if (topK > MaxTopK)
            {
                throw VdbException.Invalid($"Query.TopK cannot exceed {MaxTopK}.");
            }
            var info = GetInfo(ctx, collection);
            var vectorJson = ToJsonArray(vector);
            var body = new JsonObject
            {
                ["vector"] = info.VectorName == null ? vectorJson : new JsonObject { ["name"] = info.VectorName, ["vector"] = vectorJson },
                ["limit"] = topK,
                ["with_payload"] = true,
                ["with_vector"] = query.IncludeVectors,
            };
            var filter = QdrantFilter.Build(PortableFilter.Parse(query.FilterJson), query.Namespace ?? "");
            if (filter != null)
            {
                body["filter"] = filter;
            }

            var result = Send(ctx, HttpMethod.Post, $"/collections/{Path(collection)}/points/search", body);
            var matches = new List<VdbMatch>();
            if (result?["result"] is not JsonArray points)
            {
                return matches;
            }
            foreach (var p in points)
            {
                if (p is not JsonObject point)
                {
                    continue;
                }
                var score = point["score"]?.GetValue<double>() ?? 0;
                var similarity = Normalize(info.Distance, score);
                if (query.MinScore > 0 && (decimal)similarity < query.MinScore)
                {
                    continue;
                }
                var (id, text, metadata) = ReadPayload(point);
                matches.Add(new VdbMatch
                {
                    Id = id,
                    Score = (decimal)score,
                    Similarity = (decimal)similarity,
                    Text = text,
                    MetadataJson = metadata,
                    Vector = query.IncludeVectors ? ReadVector(point["vector"], info.VectorName) : new List<decimal>(),
                });
            }
            return matches;
        }

        public List<VdbRecord> Fetch(CallContext ctx, string collection, IReadOnlyList<string> ids, string ns, bool includeVectors)
        {
            collection = CheckName(collection);
            var info = GetInfo(ctx, collection);
            var result = Send(ctx, HttpMethod.Post, $"/collections/{Path(collection)}/points", new JsonObject
            {
                ["ids"] = new JsonArray(ids.Select(IdMapper.ToQdrantId).ToArray()),
                ["with_payload"] = true,
                ["with_vector"] = includeVectors,
            });
            var records = new List<VdbRecord>();
            if (result?["result"] is not JsonArray points)
            {
                return records;
            }
            foreach (var p in points)
            {
                if (p is not JsonObject point)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(ns) && point["payload"]?[QdrantFilter.NamespaceKey]?.ToString() != ns)
                {
                    continue;
                }
                var (id, text, metadata) = ReadPayload(point);
                records.Add(new VdbRecord
                {
                    Id = id,
                    Text = text,
                    MetadataJson = metadata,
                    Vector = includeVectors ? ReadVector(point["vector"], info.VectorName) : new List<decimal>(),
                    VectorBase64 = "",
                });
            }
            return records;
        }

        public void Delete(CallContext ctx, string collection, IReadOnlyList<string> ids, string ns)
        {
            collection = CheckName(collection);
            var filter = QdrantFilter.Build(null, ns, new JsonArray(ids.Select(IdMapper.ToQdrantId).ToArray()));
            Send(ctx, HttpMethod.Post, $"/collections/{Path(collection)}/points/delete?wait=true", new JsonObject { ["filter"] = filter });
        }

        public void DeleteByFilter(CallContext ctx, string collection, string filterJson, string ns)
        {
            collection = CheckName(collection);
            var portable = PortableFilter.Parse(filterJson)
                ?? throw VdbException.Invalid("FilterJson cannot be empty for DeleteByFilter. Use DeleteCollection to remove everything.");
            var filter = QdrantFilter.Build(portable, ns);
            Send(ctx, HttpMethod.Post, $"/collections/{Path(collection)}/points/delete?wait=true", new JsonObject { ["filter"] = filter });
        }

        public long Count(CallContext ctx, string collection, string ns, string filterJson)
        {
            collection = CheckName(collection);
            var body = new JsonObject { ["exact"] = true };
            var filter = QdrantFilter.Build(PortableFilter.Parse(filterJson), ns);
            if (filter != null)
            {
                body["filter"] = filter;
            }
            var result = Send(ctx, HttpMethod.Post, $"/collections/{Path(collection)}/points/count", body);
            return result?["result"]?["count"]?.GetValue<long>() ?? 0;
        }

        // ---------- helpers ----------

        internal static double Normalize(string distance, double score) => distance switch
        {
            "Cosine" => Math.Clamp((score + 1) / 2, 0, 1),
            "Euclid" or "Manhattan" => 1 / (1 + Math.Max(score, 0)), // Qdrant returns the distance here
            _ => score, // Dot: unbounded, left as is
        };

        private CollectionInfo GetInfo(CallContext ctx, string collection)
        {
            var key = CacheKey(ctx, collection);
            if (InfoCache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
            {
                return cached.Info;
            }
            var result = Send(ctx, HttpMethod.Get, $"/collections/{Path(collection)}", null);
            var vectors = result?["result"]?["config"]?["params"]?["vectors"] as JsonObject;
            CollectionInfo info;
            if (vectors == null)
            {
                info = new CollectionInfo("Cosine", null);
            }
            else if (vectors["distance"] != null)
            {
                info = new CollectionInfo(vectors["distance"]!.GetValue<string>(), null);
            }
            else
            {
                // Named vectors: use the first one. Multi-vector selection is planned.
                var first = vectors.FirstOrDefault();
                info = new CollectionInfo(first.Value?["distance"]?.GetValue<string>() ?? "Cosine", first.Key);
            }
            InfoCache[key] = (info, DateTime.UtcNow.AddMinutes(5));
            return info;
        }

        private void CreateIndex(CallContext ctx, string collection, string field, string schema)
        {
            Send(ctx, HttpMethod.Put, $"/collections/{Path(collection)}/index?wait=true", new JsonObject
            {
                ["field_name"] = field,
                ["field_schema"] = schema,
            });
        }

        private static List<(string Field, string Schema)> ParseIndexes(string? json)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
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
            foreach (var (field, typeNode) in obj)
            {
                var type = typeNode?.ToString().Trim().ToLowerInvariant() ?? "";
                var schema = type switch
                {
                    "keyword" or "string" => "keyword",
                    "integer" or "int" => "integer",
                    "float" or "decimal" or "number" => "float",
                    "bool" or "boolean" => "bool",
                    "datetime" or "date" => "datetime",
                    "text" => "text",
                    "uuid" => "uuid",
                    _ => throw VdbException.Invalid($"Index type '{type}' for '{field}' is not supported. Use keyword, integer, float, bool, datetime or text."),
                };
                if (field.StartsWith('_'))
                {
                    throw VdbException.Invalid($"Metadata field '{field}' is reserved (fields starting with _ are used by the connector).");
                }
                result.Add((field, schema));
            }
            return result;
        }

        private static (string Id, string Text, string MetadataJson) ReadPayload(JsonObject point)
        {
            var payload = point["payload"] as JsonObject ?? new JsonObject();
            var id = payload[IdKey]?.ToString() ?? point["id"]?.ToString() ?? "";
            var text = payload[TextKey]?.ToString() ?? "";
            var metadata = new JsonObject();
            foreach (var (k, v) in payload)
            {
                if (k is IdKey or TextKey || k == QdrantFilter.NamespaceKey)
                {
                    continue;
                }
                metadata[k] = v?.DeepClone();
            }
            return (id, text, metadata.ToJsonString());
        }

        private static List<decimal> ReadVector(JsonNode? node, string? vectorName)
        {
            if (vectorName != null && node is JsonObject named)
            {
                node = named[vectorName];
            }
            if (node is not JsonArray arr)
            {
                return new List<decimal>();
            }
            return VectorCodec.ToDecimals(arr.Select(v => v?.GetValue<float>() ?? 0f));
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

        private static string CheckName(string? name)
        {
            name = (name ?? "").Trim();
            if (!NameRegex().IsMatch(name))
            {
                throw VdbException.Invalid("Collection name must be 1-255 characters: letters, digits, _ or -.");
            }
            return name;
        }

        private static string Path(string name) => Uri.EscapeDataString(name);

        private static string CacheKey(CallContext ctx, string collection) => ctx.Endpoint + "|" + collection;

        private static JsonNode? Send(CallContext ctx, HttpMethod method, string path, JsonNode? body)
        {
            return HttpJson.Send(ctx, method, path, body,
                request =>
                {
                    if (!string.IsNullOrEmpty(ctx.ApiKey))
                    {
                        request.Headers.Add("api-key", ctx.ApiKey);
                    }
                },
                MapError);
        }

        private static VdbException MapError(int status, string body)
        {
            string message = body;
            try
            {
                message = JsonNode.Parse(body)?["status"]?["error"]?.ToString() ?? body;
            }
            catch (JsonException)
            {
                // keep raw text
            }
            if (message.Length > 500)
            {
                message = message[..500];
            }
            var code = HttpJson.CodeFor(status);
            if (message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                code = ErrorCodes.AlreadyExists;
            }
            else if (status == 404 || message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                code = ErrorCodes.NotFound;
            }
            return new VdbException(code, $"Qdrant: {message}", status);
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{1,255}$")]
        private static partial Regex NameRegex();
    }
}

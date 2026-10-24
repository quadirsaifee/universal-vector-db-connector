using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;

namespace UniversalVectorDBConnector.UnitTests
{
    /// <summary>
    /// In-memory stand-in for the Pinecone 2026-07 REST API (control plane + data plane),
    /// enough to exercise the adapter end to end without an account.
    /// </summary>
    internal sealed class FakePinecone : HttpMessageHandler
    {
        public const string ControlHost = "api.pinecone.test";
        public const string Key = "test-key";

        private sealed class Index
        {
            public required string Name;
            public required int Dimension;
            public required string Metric;
            public bool Ready;
            public string Host => $"{Name}-abc123.svc.pinecone.test";
            public readonly Dictionary<string, Dictionary<string, (float[] Values, JsonObject Metadata)>> Namespaces = new();
        }

        private readonly Dictionary<string, Index> _indexes = new();
        public readonly List<(string Method, string Path, JsonNode? Body)> Requests = new();
        public bool CreateNotReady { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bodyText = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var body = string.IsNullOrEmpty(bodyText) ? null : JsonNode.Parse(bodyText);
            var uri = request.RequestUri!;
            Requests.Add((request.Method.Method, uri.PathAndQuery, body));

            if (!request.Headers.TryGetValues("X-Pinecone-Api-Version", out var versions) || versions.Single() != "2026-07")
            {
                return Reply(HttpStatusCode.BadRequest, new JsonObject { ["error"] = new JsonObject { ["code"] = "INVALID_ARGUMENT", ["message"] = "missing version" }, ["status"] = 400 });
            }
            if (!request.Headers.TryGetValues("Api-Key", out var keys) || keys.Single() != Key)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("Unauthorized") });
            }

            return uri.Host == ControlHost
                ? Control(request.Method, uri.AbsolutePath, body)
                : Data(uri, request.Method, body);
        }

        private Task<HttpResponseMessage> Control(HttpMethod method, string path, JsonNode? body)
        {
            if (path == "/indexes" && method == HttpMethod.Get)
            {
                return Reply(HttpStatusCode.OK, new JsonObject { ["indexes"] = new JsonArray(_indexes.Values.Select(i => (JsonNode)Model(i)).ToArray()) });
            }
            if (path == "/indexes" && method == HttpMethod.Post)
            {
                var name = body!["name"]!.GetValue<string>();
                if (_indexes.ContainsKey(name))
                {
                    return Reply(HttpStatusCode.Conflict, new JsonObject { ["error"] = new JsonObject { ["code"] = "ALREADY_EXISTS", ["message"] = "Resource already exists" }, ["status"] = 409 });
                }
                var field = body["schema"]!["fields"]!["_values"]!;
                var index = new Index
                {
                    Name = name,
                    Dimension = field["dimension"]!.GetValue<int>(),
                    Metric = field["metric"]!.GetValue<string>(),
                    Ready = !CreateNotReady,
                };
                _indexes[name] = index;
                return Reply(HttpStatusCode.Created, Model(index));
            }
            var indexName = Uri.UnescapeDataString(path.Replace("/indexes/", ""));
            if (!_indexes.TryGetValue(indexName, out var existing))
            {
                return Reply(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["code"] = "NOT_FOUND", ["message"] = $"Index {indexName} not found." }, ["status"] = 404 });
            }
            if (method == HttpMethod.Delete)
            {
                _indexes.Remove(indexName);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }
            var model = Model(existing);
            existing.Ready = true; // ready from the next describe on
            return Reply(HttpStatusCode.OK, model);
        }

        private Task<HttpResponseMessage> Data(Uri uri, HttpMethod method, JsonNode? body)
        {
            var index = _indexes.Values.FirstOrDefault(i => i.Host == uri.Host);
            if (index == null)
            {
                return Reply(HttpStatusCode.NotFound, new JsonObject { ["code"] = 5, ["message"] = "Not Found" });
            }
            string Ns(JsonNode? b) => b?["namespace"]?.GetValue<string>() ?? "";
            Dictionary<string, (float[] Values, JsonObject Metadata)> Space(string ns) =>
                index.Namespaces.TryGetValue(ns, out var s) ? s : index.Namespaces[ns] = new();

            switch (uri.AbsolutePath)
            {
                case "/vectors/upsert":
                {
                    var vectors = (JsonArray)body!["vectors"]!;
                    foreach (var v in vectors)
                    {
                        var values = v!["values"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
                        if (values.Length != index.Dimension)
                        {
                            return Reply(HttpStatusCode.BadRequest, new JsonObject { ["code"] = 3, ["message"] = $"Vector dimension {values.Length} does not match the dimension of the index {index.Dimension}" });
                        }
                        Space(Ns(body))[v["id"]!.GetValue<string>()] = (values, (JsonObject?)v["metadata"]?.DeepClone() ?? new JsonObject());
                    }
                    return Reply(HttpStatusCode.OK, new JsonObject { ["upsertedCount"] = vectors.Count });
                }
                case "/query":
                {
                    var q = body!["vector"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
                    var filter = body["filter"] as JsonObject;
                    var includeValues = body["includeValues"]?.GetValue<bool>() ?? false;
                    var ranked = Space(Ns(body))
                        .Where(kv => filter == null || Matches(kv.Value.Metadata, filter))
                        .Select(kv => (kv.Key, kv.Value, Score: Score(index.Metric, q, kv.Value.Values)))
                        .OrderBy(x => index.Metric == "euclidean" ? x.Score : -x.Score)
                        .Take(body["topK"]!.GetValue<int>());
                    var matches = new JsonArray();
                    foreach (var (id, value, score) in ranked)
                    {
                        var m = new JsonObject { ["id"] = id, ["score"] = score, ["metadata"] = value.Metadata.DeepClone() };
                        m["values"] = includeValues ? new JsonArray(value.Values.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()) : new JsonArray();
                        matches.Add(m);
                    }
                    return Reply(HttpStatusCode.OK, new JsonObject { ["matches"] = matches, ["namespace"] = Ns(body) });
                }
                case "/vectors/fetch":
                {
                    var qs = HttpUtility.ParseQueryString(uri.Query);
                    var space = Space(qs["namespace"] ?? "");
                    var vectors = new JsonObject();
                    foreach (var id in qs.GetValues("ids") ?? Array.Empty<string>())
                    {
                        if (space.TryGetValue(id, out var v))
                        {
                            vectors[id] = new JsonObject
                            {
                                ["id"] = id,
                                ["values"] = new JsonArray(v.Values.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()),
                                ["metadata"] = v.Metadata.DeepClone(),
                            };
                        }
                    }
                    return Reply(HttpStatusCode.OK, new JsonObject { ["vectors"] = vectors, ["namespace"] = qs["namespace"] ?? "" });
                }
                case "/vectors/delete":
                {
                    var space = Space(Ns(body));
                    if (body!["ids"] is JsonArray ids)
                    {
                        foreach (var id in ids)
                        {
                            space.Remove(id!.GetValue<string>());
                        }
                    }
                    else if (body["filter"] is JsonObject filter)
                    {
                        foreach (var id in space.Where(kv => Matches(kv.Value.Metadata, filter)).Select(kv => kv.Key).ToList())
                        {
                            space.Remove(id);
                        }
                    }
                    return Reply(HttpStatusCode.OK, new JsonObject());
                }
                case "/describe_index_stats":
                {
                    var namespaces = new JsonObject();
                    foreach (var (ns, space) in index.Namespaces.Where(n => n.Value.Count > 0))
                    {
                        namespaces[ns.Length == 0 ? "__default__" : ns] = new JsonObject { ["vectorCount"] = space.Count };
                    }
                    return Reply(HttpStatusCode.OK, new JsonObject
                    {
                        ["namespaces"] = namespaces,
                        ["dimension"] = index.Dimension,
                        ["totalVectorCount"] = index.Namespaces.Values.Sum(s => s.Count),
                        ["metric"] = index.Metric,
                    });
                }
            }
            return Reply(HttpStatusCode.NotFound, new JsonObject { ["code"] = 5, ["message"] = "Not Found" });
        }

        private static JsonObject Model(Index i) => new()
        {
            ["name"] = i.Name,
            ["host"] = i.Host,
            ["status"] = new JsonObject { ["ready"] = i.Ready, ["state"] = i.Ready ? "Ready" : "Initializing" },
            ["deletion_protection"] = "disabled",
            ["deployment"] = new JsonObject { ["deployment_type"] = "managed", ["cloud"] = "aws", ["region"] = "us-east-1" },
            ["schema"] = new JsonObject
            {
                ["fields"] = new JsonObject
                {
                    ["_values"] = new JsonObject { ["type"] = "dense_vector", ["dimension"] = i.Dimension, ["metric"] = i.Metric },
                },
            },
        };

        private static double Score(string metric, float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0, sq = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
                sq += (a[i] - b[i]) * (a[i] - b[i]);
            }
            return metric switch
            {
                "cosine" => dot / (Math.Sqrt(na) * Math.Sqrt(nb)),
                "euclidean" => sq, // Pinecone reports squared distance
                _ => dot,
            };
        }

        /// <summary>Evaluates a Pinecone filter (the documented operators) against one record's metadata.</summary>
        internal static bool Matches(JsonObject metadata, JsonObject filter)
        {
            foreach (var (key, value) in filter)
            {
                bool ok = key switch
                {
                    "$and" => value!.AsArray().All(f => Matches(metadata, (JsonObject)f!)),
                    "$or" => value!.AsArray().Any(f => Matches(metadata, (JsonObject)f!)),
                    _ => value is JsonObject ops ? FieldMatches(metadata[key], ops) : Equal(metadata[key], value),
                };
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool FieldMatches(JsonNode? field, JsonObject ops)
        {
            foreach (var (op, operand) in ops)
            {
                bool ok = op switch
                {
                    "$eq" => Equal(field, operand),
                    "$ne" => !Equal(field, operand),
                    "$gt" => Num(field) is double f1 && f1 > Num(operand),
                    "$gte" => Num(field) is double f2 && f2 >= Num(operand),
                    "$lt" => Num(field) is double f3 && f3 < Num(operand),
                    "$lte" => Num(field) is double f4 && f4 <= Num(operand),
                    "$in" => operand!.AsArray().Any(o => Equal(field, o)),
                    "$nin" => !operand!.AsArray().Any(o => Equal(field, o)),
                    "$exists" => (field != null) == operand!.GetValue<bool>(),
                    "$not" => !FieldMatches(field, (JsonObject)operand!),
                    _ => throw new InvalidOperationException("Fake Pinecone: unknown operator " + op),
                };
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Equal(JsonNode? field, JsonNode? value)
        {
            if (field is JsonArray list)
            {
                return list.Any(item => Equal(item, value)); // list fields match any element
            }
            if (field == null || value == null)
            {
                return false;
            }
            if (Num(field) is double a && Num(value) is double b)
            {
                return a == b;
            }
            return field.ToJsonString() == value.ToJsonString();
        }

        private static double? Num(JsonNode? n) =>
            n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

        private static Task<HttpResponseMessage> Reply(HttpStatusCode status, JsonNode body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            });
    }
}

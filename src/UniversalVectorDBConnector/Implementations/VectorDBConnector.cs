using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalVectorDBConnector.Adapters;
using UniversalVectorDBConnector.Adapters.Pinecone;
using UniversalVectorDBConnector.Adapters.Qdrant;
using UniversalVectorDBConnector.Core;

namespace UniversalVectorDBConnector
{
    /// <summary>ODC entry point. Validates input, picks the provider adapter, and turns errors into VdbStatus.</summary>
    public class VectorDBConnector : IUniversalVectorDBConnector
    {
        internal static readonly string[] PlannedProviders =
            { "weaviate", "milvus", "pgvector", "mongodb", "azureaisearch", "elastic", "redis" };

        private static readonly Dictionary<string, IVectorStoreAdapter> Adapters = new()
        {
            ["qdrant"] = new QdrantAdapter(),
            ["pinecone"] = new PineconeAdapter(),
        };


        public void TestConnection(VdbConnection Connection, out VdbStatus Status, out string ServerInfo)
        {
            ServerInfo = Run(nameof(TestConnection), Connection, out Status, (ctx, a) => a.TestConnection(ctx)) ?? "";
        }

        public VdbCapabilities GetCapabilities(string Provider)
        {
            var key = (Provider ?? "").Trim().ToLowerInvariant();
            if (Adapters.TryGetValue(key, out var adapter))
            {
                return adapter.Capabilities;
            }
            return new VdbCapabilities { Provider = key, Implemented = false };
        }

        public void CreateCollection(VdbConnection Connection, VdbCollectionSpec Spec, out VdbStatus Status)
        {
            Run(nameof(CreateCollection), Connection, out Status, (ctx, a) => { a.CreateCollection(ctx, Spec); return true; });
        }

        public void DeleteCollection(VdbConnection Connection, string Name, out VdbStatus Status)
        {
            Run(nameof(DeleteCollection), Connection, out Status, (ctx, a) => { a.DeleteCollection(ctx, Name); return true; });
        }

        public void ListCollections(VdbConnection Connection, out VdbStatus Status, out IEnumerable<string> Names)
        {
            Names = Run(nameof(ListCollections), Connection, out Status, (ctx, a) => a.ListCollections(ctx)) ?? new List<string>();
        }

        public void CollectionExists(VdbConnection Connection, string Name, out VdbStatus Status, out bool Exists)
        {
            Exists = Run(nameof(CollectionExists), Connection, out Status, (ctx, a) => a.CollectionExists(ctx, Name));
        }

        public void Upsert(VdbConnection Connection, string Collection, IEnumerable<VdbRecord> Records,
            out VdbStatus Status, out int UpsertedCount, string? Namespace = null)
        {
            UpsertedCount = Run(nameof(Upsert), Connection, out Status, (ctx, a) =>
            {
                var normalized = Normalize(Records);
                return normalized.Count == 0 ? 0 : a.Upsert(ctx, Collection, normalized, Namespace ?? "");
            });
        }

        public void Query(VdbConnection Connection, VdbQuery Query, out VdbStatus Status, out IEnumerable<VdbMatch> Matches)
        {
            Matches = Run(nameof(Query), Connection, out Status, (ctx, a) =>
            {
                var vector = VectorCodec.Resolve(Query.Vector, Query.VectorBase64, "Query");
                return a.Query(ctx, Query, vector);
            }) ?? new List<VdbMatch>();
        }

        public void Fetch(VdbConnection Connection, string Collection, IEnumerable<string> Ids,
            out VdbStatus Status, out IEnumerable<VdbRecord> Records, string? Namespace = null, bool? IncludeVectors = null)
        {
            Records = Run(nameof(Fetch), Connection, out Status, (ctx, a) =>
            {
                var ids = CheckIds(Ids);
                return ids.Count == 0 ? new List<VdbRecord>() : a.Fetch(ctx, Collection, ids, Namespace ?? "", IncludeVectors ?? false);
            }) ?? new List<VdbRecord>();
        }

        public void Delete(VdbConnection Connection, string Collection, IEnumerable<string> Ids,
            out VdbStatus Status, string? Namespace = null)
        {
            Run(nameof(Delete), Connection, out Status, (ctx, a) =>
            {
                var ids = CheckIds(Ids);
                if (ids.Count > 0)
                {
                    a.Delete(ctx, Collection, ids, Namespace ?? "");
                }
                return true;
            });
        }

        public void DeleteByFilter(VdbConnection Connection, string Collection, string FilterJson,
            out VdbStatus Status, string? Namespace = null)
        {
            Run(nameof(DeleteByFilter), Connection, out Status, (ctx, a) => { a.DeleteByFilter(ctx, Collection, FilterJson, Namespace ?? ""); return true; });
        }

        public void Count(VdbConnection Connection, string Collection, out VdbStatus Status, out long RecordCount,
            string? Namespace = null, string? FilterJson = null)
        {
            RecordCount = Run(nameof(Count), Connection, out Status, (ctx, a) => a.Count(ctx, Collection, Namespace ?? "", FilterJson ?? ""));
        }

        public string EncodeVector(IEnumerable<decimal> Vector)
        {
            return VectorCodec.Encode(VectorCodec.FromDecimals(Vector ?? Array.Empty<decimal>()));
        }

        public IEnumerable<decimal> DecodeVector(string VectorBase64)
        {
            if (string.IsNullOrWhiteSpace(VectorBase64))
            {
                return new List<decimal>();
            }
            try
            {
                return VectorCodec.ToDecimals(VectorCodec.Decode(VectorBase64));
            }
            catch (VdbException ex)
            {
                // Utility actions have no Status output, so surface the problem as an ODC exception.
                throw new ArgumentException(ex.Message, nameof(VectorBase64));
            }
        }

        // ---------- plumbing ----------

        private T? Run<T>(string action, VdbConnection connection, out VdbStatus status, Func<CallContext, IVectorStoreAdapter, T> work)
        {
            try
            {
                using var ctx = CallContext.Create(connection);
                if (!Adapters.TryGetValue(ctx.Provider, out var adapter))
                {
                    var message = PlannedProviders.Contains(ctx.Provider)
                        ? $"Provider '{ctx.Provider}' is planned for a later version of this connector."
                        : $"Unknown provider '{ctx.Provider}'. Supported: {string.Join(", ", Adapters.Keys)}.";
                    throw new VdbException(ErrorCodes.Unsupported, message);
                }
                var result = work(ctx, adapter);
                status = StatusFactory.Ok();
                return result;
            }
            catch (VdbException ex)
            {
                status = StatusFactory.From(ex);
            }
            catch (Exception ex)
            {
                // Unexpected bug: report it without leaking request data.
                status = StatusFactory.From(new VdbException(ErrorCodes.ProviderError, "Unexpected error: " + ex.GetType().Name + ": " + ex.Message));
            }
            return default;
        }

        internal static List<NormalizedRecord> Normalize(IEnumerable<VdbRecord>? records)
        {
            var list = new List<NormalizedRecord>();
            if (records == null)
            {
                return list;
            }
            int index = 0;
            int? dimension = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in records)
            {
                var where = $"Records[{index}]";
                if (string.IsNullOrWhiteSpace(r.Id))
                {
                    throw VdbException.Invalid($"{where}.Id is required.");
                }
                if (!seen.Add(r.Id))
                {
                    throw VdbException.Invalid($"{where}.Id '{r.Id}' appears more than once in this call.");
                }
                var vector = VectorCodec.Resolve(r.Vector, r.VectorBase64, where);
                dimension ??= vector.Length;
                if (vector.Length != dimension)
                {
                    throw VdbException.Invalid($"{where} has {vector.Length} dimensions; earlier records have {dimension}.");
                }
                list.Add(new NormalizedRecord(r.Id, vector, r.Text ?? "", ParseMetadata(r.MetadataJson, where)));
                index++;
            }
            return list;
        }

        private static JsonObject ParseMetadata(string? json, string where)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JsonObject();
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
                throw VdbException.Invalid($"{where}.MetadataJson must be a JSON object.");
            }
            foreach (var (key, _) in obj)
            {
                if (key.StartsWith('_'))
                {
                    throw VdbException.Invalid($"{where}.MetadataJson key '{key}' is reserved (keys starting with _ are used by the connector).");
                }
            }
            return obj;
        }

        private static List<string> CheckIds(IEnumerable<string>? ids)
        {
            var list = (ids ?? Array.Empty<string>()).ToList();
            if (list.Any(string.IsNullOrWhiteSpace))
            {
                throw VdbException.Invalid("Ids cannot contain empty values.");
            }
            return list.Distinct(StringComparer.Ordinal).ToList();
        }
    }
}

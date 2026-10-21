using OutSystems.ExternalLibraries.SDK;

namespace UniversalVectorDBConnector
{
    [OSStructure(Description = "How to reach a vector database. Pass it to every action.")]
    public struct VdbConnection
    {
        [OSStructureField(Description = "Provider id. Supported in 0.1: qdrant. Planned: pinecone, weaviate, milvus, pgvector, mongodb, azureaisearch, elastic, redis.", IsMandatory = true)]
        public string Provider { get; set; }

        [OSStructureField(Description = "Base URL of the database, e.g. https://xyz.cloud.qdrant.io:6333", IsMandatory = true)]
        public string Endpoint { get; set; }

        [OSStructureField(Description = "API key. Store it in a secret Setting; it is never logged.")]
        public string ApiKey { get; set; }

        [OSStructureField(Description = "Optional database or schema name (provider specific).")]
        public string Database { get; set; }

        [OSStructureField(Description = "Time budget for the whole call, including retries. Default 30, maximum 85.")]
        public int TimeoutSeconds { get; set; }

        [OSStructureField(Description = "Optional provider-specific options as a JSON object, e.g. {\"allowInsecure\":true} for local http testing.")]
        public string OptionsJson { get; set; }
    }

    [OSStructure(Description = "Definition of a new collection (index).")]
    public struct VdbCollectionSpec
    {
        [OSStructureField(Description = "Collection name.", IsMandatory = true)]
        public string Name { get; set; }

        [OSStructureField(Description = "Vector dimension, e.g. 1536.", IsMandatory = true)]
        public int Dimension { get; set; }

        [OSStructureField(Description = "Distance metric: cosine (default), dot or euclidean.")]
        public string Metric { get; set; }

        [OSStructureField(Description = "Optional JSON object of metadata fields to index for filtering, e.g. {\"category\":\"keyword\",\"year\":\"integer\"}. Types: keyword, integer, float, bool, datetime, text.")]
        public string MetadataIndexesJson { get; set; }
    }

    [OSStructure(Description = "One vector with its id, text and metadata.")]
    public struct VdbRecord
    {
        [OSStructureField(Description = "Record id (any text). Mapped to a provider id automatically.", IsMandatory = true)]
        public string Id { get; set; }

        [OSStructureField(Description = "Dense vector. Use this or VectorBase64.")]
        public IEnumerable<decimal> Vector { get; set; }

        [OSStructureField(Description = "Dense vector as base64 little-endian float32 bytes (about 5x smaller than Vector). Use this or Vector.")]
        public string VectorBase64 { get; set; }

        [OSStructureField(Description = "Optional source text stored with the vector.")]
        public string Text { get; set; }

        [OSStructureField(Description = "Optional metadata as a JSON object. Keys starting with _ are reserved.")]
        public string MetadataJson { get; set; }
    }

    [OSStructure(Description = "A similarity search request.")]
    public struct VdbQuery
    {
        [OSStructureField(Description = "Collection name.", IsMandatory = true)]
        public string Collection { get; set; }

        [OSStructureField(Description = "Optional namespace to search in. Empty searches the whole collection.")]
        public string Namespace { get; set; }

        [OSStructureField(Description = "Query vector. Use this or VectorBase64.")]
        public IEnumerable<decimal> Vector { get; set; }

        [OSStructureField(Description = "Query vector as base64 float32. Use this or Vector.")]
        public string VectorBase64 { get; set; }

        [OSStructureField(Description = "Number of results. Default 10, maximum 1000.")]
        public int TopK { get; set; }

        [OSStructureField(Description = "Optional portable filter, e.g. {\"category\":{\"$eq\":\"invoice\"},\"year\":{\"$gte\":2024}}. Operators: $eq $ne $gt $gte $lt $lte $in $nin $exists $and $or $not.")]
        public string FilterJson { get; set; }

        [OSStructureField(Description = "Return the stored vectors with each match.")]
        public bool IncludeVectors { get; set; }

        [OSStructureField(Description = "Drop matches whose normalized Similarity (0-1) is below this value. 0 keeps all.")]
        public decimal MinScore { get; set; }
    }

    [OSStructure(Description = "One search result.")]
    public struct VdbMatch
    {
        [OSStructureField(Description = "Record id as given at upsert.")]
        public string Id { get; set; }

        [OSStructureField(Description = "Raw score from the provider.")]
        public decimal Score { get; set; }

        [OSStructureField(Description = "Score normalized to 0-1 where the metric allows (cosine, euclidean). Equals Score for dot product.")]
        public decimal Similarity { get; set; }

        [OSStructureField(Description = "Stored text, if any.")]
        public string Text { get; set; }

        [OSStructureField(Description = "Stored metadata as a JSON object.")]
        public string MetadataJson { get; set; }

        [OSStructureField(Description = "Stored vector, when IncludeVectors was set.")]
        public IEnumerable<decimal> Vector { get; set; }
    }

    [OSStructure(Description = "Outcome of an action. Check Success before using other outputs.")]
    public struct VdbStatus
    {
        [OSStructureField(Description = "True when the action succeeded.")]
        public bool Success { get; set; }

        [OSStructureField(Description = "Empty on success. One of InvalidInput, Unauthorized, NotFound, AlreadyExists, RateLimited, Timeout, PayloadTooLarge, Unsupported, ProviderError, NetworkError.")]
        public string ErrorCode { get; set; }

        [OSStructureField(Description = "Human-readable error message.")]
        public string ErrorMessage { get; set; }

        [OSStructureField(Description = "HTTP status returned by the provider, 0 if none.")]
        public int ProviderHttpStatus { get; set; }

        [OSStructureField(Description = "Suggested wait before retrying, in milliseconds. 0 if none.")]
        public int RetryAfterMs { get; set; }
    }

    [OSStructure(Description = "Features a provider supports in this version of the connector.")]
    public struct VdbCapabilities
    {
        [OSStructureField(Description = "Provider id.")]
        public string Provider { get; set; }

        [OSStructureField(Description = "True when this connector version implements the provider.")]
        public bool Implemented { get; set; }

        [OSStructureField(Description = "Namespaces are supported.")]
        public bool Namespaces { get; set; }

        [OSStructureField(Description = "Metadata filters are supported.")]
        public bool MetadataFilters { get; set; }

        [OSStructureField(Description = "Delete by filter is supported.")]
        public bool DeleteByFilter { get; set; }

        [OSStructureField(Description = "Hybrid (vector + keyword) search is supported.")]
        public bool HybridSearch { get; set; }

        [OSStructureField(Description = "Largest TopK accepted.")]
        public int MaxTopK { get; set; }
    }
}

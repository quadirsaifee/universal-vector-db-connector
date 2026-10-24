using OutSystems.ExternalLibraries.SDK;

namespace UniversalVectorDBConnector
{
    [OSInterface(
        Name = "UniversalVectorDBConnector",
        Description = "One set of actions for vector databases: create collections, upsert, similarity search with metadata filters, fetch, count and delete. Version 0.2 supports Qdrant and Pinecone; pgvector, Azure AI Search, Weaviate, Milvus, Elastic, MongoDB Atlas and Redis are planned.",
        IconResourceName = "UniversalVectorDBConnector.Resources.icon.png")]
    public interface IUniversalVectorDBConnector
    {
        [OSAction(Description = "Checks that the database is reachable and the API key is accepted.")]
        void TestConnection(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Server name and version, when the provider reports them.")] out string ServerInfo);

        [OSAction(Description = "Lists which features a provider supports in this connector version. Makes no network call.", ReturnName = "Capabilities")]
        VdbCapabilities GetCapabilities(
            [OSParameter(Description = "Provider id, e.g. qdrant.")] string Provider);

        [OSAction(Description = "Creates a collection (index) with the given dimension and metric.")]
        void CreateCollection(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection definition.")] VdbCollectionSpec Spec,
            [OSParameter(Description = "Outcome. ErrorCode AlreadyExists if the name is taken.")] out VdbStatus Status);

        [OSAction(Description = "Deletes a collection and all its records. Cannot be undone.")]
        void DeleteCollection(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Name,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status);

        [OSAction(Description = "Lists collection names.")]
        void ListCollections(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Collection names.")] out IEnumerable<string> Names);

        [OSAction(Description = "Checks whether a collection exists.")]
        void CollectionExists(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Name,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "True if the collection exists.")] out bool Exists);

        [OSAction(Description = "Inserts or replaces records. Batches automatically.")]
        void Upsert(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Collection,
            [OSParameter(Description = "Records to write.")] IEnumerable<VdbRecord> Records,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Number of records written.")] out int UpsertedCount,
            [OSParameter(Description = "Optional namespace.")] string? Namespace = null);

        [OSAction(Description = "Similarity search with an optional metadata filter.")]
        void Query(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Search request.")] VdbQuery Query,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Matches, best first.")] out IEnumerable<VdbMatch> Matches);

        [OSAction(Description = "Reads records by id.")]
        void Fetch(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Collection,
            [OSParameter(Description = "Record ids.")] IEnumerable<string> Ids,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Records found. Missing ids are skipped.")] out IEnumerable<VdbRecord> Records,
            [OSParameter(Description = "Optional namespace.")] string? Namespace = null,
            [OSParameter(Description = "Also return vectors. Default false.")] bool? IncludeVectors = null);

        [OSAction(Description = "Deletes records by id.")]
        void Delete(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Collection,
            [OSParameter(Description = "Record ids.")] IEnumerable<string> Ids,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Optional namespace.")] string? Namespace = null);

        [OSAction(Description = "Deletes every record matching a filter. An empty filter is rejected.")]
        void DeleteByFilter(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Collection,
            [OSParameter(Description = "Portable filter (same syntax as Query).")] string FilterJson,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Optional namespace.")] string? Namespace = null);

        [OSAction(Description = "Counts records, optionally matching a filter.")]
        void Count(
            [OSParameter(Description = "Connection settings.")] VdbConnection Connection,
            [OSParameter(Description = "Collection name.")] string Collection,
            [OSParameter(Description = "Outcome.")] out VdbStatus Status,
            [OSParameter(Description = "Number of records.")] out long RecordCount,
            [OSParameter(Description = "Optional namespace.")] string? Namespace = null,
            [OSParameter(Description = "Optional portable filter.")] string? FilterJson = null);

        [OSAction(Description = "Converts a vector to compact base64 float32 text.", ReturnName = "VectorBase64")]
        string EncodeVector(
            [OSParameter(Description = "Vector values.")] IEnumerable<decimal> Vector);

        [OSAction(Description = "Converts base64 float32 text back to a list of numbers.", ReturnName = "Vector")]
        IEnumerable<decimal> DecodeVector(
            [OSParameter(Description = "Base64 float32 text.")] string VectorBase64);
    }
}

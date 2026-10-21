using UniversalVectorDBConnector.Core;

namespace UniversalVectorDBConnector.Adapters
{
    /// <summary>A record after input validation: vector resolved, metadata parsed.</summary>
    internal sealed record NormalizedRecord(string Id, float[] Vector, string Text, System.Text.Json.Nodes.JsonObject Metadata);

    /// <summary>One provider implementation. Methods throw VdbException on failure.</summary>
    internal interface IVectorStoreAdapter
    {
        VdbCapabilities Capabilities { get; }
        string TestConnection(CallContext ctx);
        void CreateCollection(CallContext ctx, VdbCollectionSpec spec);
        void DeleteCollection(CallContext ctx, string name);
        List<string> ListCollections(CallContext ctx);
        bool CollectionExists(CallContext ctx, string name);
        int Upsert(CallContext ctx, string collection, IReadOnlyList<NormalizedRecord> records, string ns);
        List<VdbMatch> Query(CallContext ctx, VdbQuery query, float[] vector);
        List<VdbRecord> Fetch(CallContext ctx, string collection, IReadOnlyList<string> ids, string ns, bool includeVectors);
        void Delete(CallContext ctx, string collection, IReadOnlyList<string> ids, string ns);
        void DeleteByFilter(CallContext ctx, string collection, string filterJson, string ns);
        long Count(CallContext ctx, string collection, string ns, string filterJson);
    }
}

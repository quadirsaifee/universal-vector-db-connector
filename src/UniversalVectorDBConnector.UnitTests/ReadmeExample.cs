namespace UniversalVectorDBConnector.UnitTests
{
    /// <summary>
    /// Compile-only copy of the README's .NET example. Never run (it needs a live Qdrant);
    /// it exists so a breaking API change fails the build instead of silently breaking the README.
    /// </summary>
    internal static class ReadmeExample
    {
        internal static void Run()
        {
            var vdb  = new VectorDBConnector();
            var conn = new VdbConnection
            {
                Provider = "qdrant",
                Endpoint = "https://YOUR-CLUSTER.cloud.qdrant.io:6333",
                ApiKey   = Environment.GetEnvironmentVariable("QDRANT_API_KEY") ?? "",
            };

            vdb.CreateCollection(conn, new VdbCollectionSpec
            {
                Name = "docs", Dimension = 3, Metric = "cosine",
                MetadataIndexesJson = "{\"category\":\"keyword\"}",
            }, out var status);

            vdb.Upsert(conn, "docs", new[]
            {
                new VdbRecord { Id = "doc-1", Vector = new[] { 0.9m, 0.1m, 0.0m }, Text = "Invoice guide",
                                MetadataJson = "{\"category\":\"invoice\"}" },
            }, out status, out var upserted);

            vdb.Query(conn, new VdbQuery
            {
                Collection = "docs", Vector = new[] { 1m, 0m, 0m }, TopK = 5,
                FilterJson = "{\"category\":\"invoice\"}",
            }, out status, out var matches);

            if (!status.Success) Console.WriteLine($"{status.ErrorCode}: {status.ErrorMessage}");
            foreach (var m in matches) Console.WriteLine($"{m.Id}  {m.Similarity:P0}  {m.Text}");
            _ = upserted;
        }
    }
}

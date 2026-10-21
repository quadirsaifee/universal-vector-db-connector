using Xunit;

namespace UniversalVectorDBConnector.UnitTests
{
    /// <summary>
    /// End-to-end tests against a real Qdrant. Skipped unless QDRANT_URL is set, e.g.
    /// QDRANT_URL=http://localhost:6333 QDRANT_API_KEY=local-test-key dotnet test
    /// </summary>
    public class QdrantLiveTests
    {
        private static readonly string? Url = Environment.GetEnvironmentVariable("QDRANT_URL");
        private readonly VectorDBConnector _sut = new();

        private static VdbConnection Conn(string? key = null) => new()
        {
            Provider = "qdrant",
            Endpoint = Url!,
            ApiKey = key ?? Environment.GetEnvironmentVariable("QDRANT_API_KEY") ?? "",
        };

        private static void Ok(VdbStatus s) => Assert.True(s.Success, $"{s.ErrorCode}: {s.ErrorMessage}");

        [Fact]
        public void FullLifecycle()
        {
            Assert.SkipWhen(Url == null, "QDRANT_URL not set");
            var c = Conn();
            var name = "uvdb_test_" + Guid.NewGuid().ToString("N")[..8];

            _sut.TestConnection(c, out var s, out var info);
            Ok(s);
            Assert.StartsWith("qdrant", info);

            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 3, Metric = "cosine", MetadataIndexesJson = "{\"category\":\"keyword\",\"year\":\"integer\"}" }, out s);
            Ok(s);
            try
            {
                _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 3 }, out s);
                Assert.Equal("AlreadyExists", s.ErrorCode);

                _sut.CollectionExists(c, name, out s, out var exists);
                Ok(s);
                Assert.True(exists);

                _sut.ListCollections(c, out s, out var names);
                Ok(s);
                Assert.Contains(name, names);

                var records = new[]
                {
                    new VdbRecord { Id = "doc-1", Vector = new[] { 1m, 0m, 0m }, Text = "invoice from 2024", MetadataJson = "{\"category\":\"invoice\",\"year\":2024}" },
                    new VdbRecord { Id = "doc-2", Vector = new[] { 0.9m, 0.1m, 0m }, Text = "invoice from 2021", MetadataJson = "{\"category\":\"invoice\",\"year\":2021}" },
                    new VdbRecord { Id = "doc-3", VectorBase64 = _sut.EncodeVector(new[] { 0m, 1m, 0m }), Text = "contract", MetadataJson = "{\"category\":\"contract\",\"year\":2024}" },
                };
                _sut.Upsert(c, name, records, out s, out var count);
                Ok(s);
                Assert.Equal(3, count);

                // Same id in another namespace is a different record.
                _sut.Upsert(c, name, new[] { new VdbRecord { Id = "ns-doc", Vector = new[] { 1m, 0m, 0m }, MetadataJson = "{\"category\":\"invoice\",\"year\":2024}" } }, out s, out _, "tenantA");
                Ok(s);

                _sut.Query(c, new VdbQuery { Collection = name, Vector = new[] { 1m, 0m, 0m }, TopK = 10 }, out s, out var all);
                Ok(s);
                Assert.Equal(4, all.Count());

                _sut.Query(c, new VdbQuery
                {
                    Collection = name,
                    Vector = new[] { 1m, 0m, 0m },
                    TopK = 5,
                    FilterJson = "{\"category\":\"invoice\",\"year\":{\"$gte\":2023}}",
                    IncludeVectors = true,
                }, out s, out var matches);
                Ok(s);
                var list = matches.ToList();
                Assert.Equal(2, list.Count); // doc-1 and ns-doc
                Assert.All(list, m => Assert.Equal(3, m.Vector.Count()));
                Assert.All(list, m => Assert.InRange(m.Similarity, 0.99m, 1.0m));
                Assert.Contains(list, m => m.Id == "doc-1" && m.Text == "invoice from 2024" && m.MetadataJson.Contains("\"year\":2024"));

                _sut.Query(c, new VdbQuery { Collection = name, Namespace = "tenantA", Vector = new[] { 1m, 0m, 0m } }, out s, out var nsMatches);
                Ok(s);
                Assert.Equal("ns-doc", Assert.Single(nsMatches).Id);

                _sut.Fetch(c, name, new[] { "doc-1", "doc-3", "missing" }, out s, out var fetched);
                Ok(s);
                Assert.Equal(new[] { "doc-1", "doc-3" }, fetched.Select(r => r.Id).OrderBy(x => x));

                _sut.Count(c, name, out s, out var total);
                Ok(s);
                Assert.Equal(4, total);
                _sut.Count(c, name, out s, out var invoices, null, "{\"category\":{\"$in\":[\"invoice\"]}}");
                Ok(s);
                Assert.Equal(3, invoices);

                _sut.Delete(c, name, new[] { "doc-1" }, out s);
                Ok(s);
                _sut.DeleteByFilter(c, name, "{\"year\":{\"$lt\":2022}}", out s);
                Ok(s);
                _sut.DeleteByFilter(c, name, "", out s);
                Assert.Equal("InvalidInput", s.ErrorCode);
                _sut.Count(c, name, out s, out total);
                Ok(s);
                Assert.Equal(2, total); // doc-3 and ns-doc remain
            }
            finally
            {
                _sut.DeleteCollection(c, name, out s);
                Ok(s);
            }

            _sut.CollectionExists(c, name, out s, out var stillThere);
            Ok(s);
            Assert.False(stillThere);
            _sut.DeleteCollection(c, name, out s);
            Assert.Equal("NotFound", s.ErrorCode);
        }

        [Fact]
        public void WrongApiKey_IsUnauthorized()
        {
            Assert.SkipWhen(Url == null, "QDRANT_URL not set");
            _sut.ListCollections(Conn("wrong-key"), out var s, out _);
            Assert.Equal("Unauthorized", s.ErrorCode);
        }

        [Fact]
        public void MissingCollection_IsNotFound()
        {
            Assert.SkipWhen(Url == null, "QDRANT_URL not set");
            _sut.Query(Conn(), new VdbQuery { Collection = "does_not_exist_" + Guid.NewGuid().ToString("N")[..6], Vector = new[] { 1m } }, out var s, out _);
            Assert.Equal("NotFound", s.ErrorCode);
        }
    }
}

using System.Text.Json.Nodes;
using UniversalVectorDBConnector.Adapters.Pinecone;
using UniversalVectorDBConnector.Core;
using Xunit;

namespace UniversalVectorDBConnector.UnitTests
{
    public class PineconeFilterTests
    {
        private static string T(string portable) => PineconeFilter.Translate(PortableFilter.Parse(portable)!).ToJsonString();

        [Fact]
        public void Shorthand_BecomesEq_AndKeysAreAnded()
        {
            Assert.Equal("{\"category\":{\"$eq\":\"invoice\"}}", T("{\"category\":\"invoice\"}"));
            Assert.Equal("{\"$and\":[{\"category\":{\"$eq\":\"invoice\"}},{\"year\":{\"$gte\":2024}}]}",
                T("{\"category\":\"invoice\",\"year\":{\"$gte\":2024}}"));
        }

        [Fact]
        public void Range_NeedsNumbers()
        {
            Assert.Equal("{\"$and\":[{\"year\":{\"$gte\":2020}},{\"year\":{\"$lt\":2024}}]}", T("{\"year\":{\"$gte\":2020,\"$lt\":2024}}"));
            var ex = Assert.Throws<VdbException>(() => T("{\"date\":{\"$gte\":\"2024-01-01\"}}"));
            Assert.Equal(ErrorCodes.Unsupported, ex.Code);
            Assert.Contains("numbers", ex.Message);
        }

        [Fact]
        public void InNinExists()
        {
            Assert.Equal("{\"tag\":{\"$in\":[\"a\",\"b\"]}}", T("{\"tag\":{\"$in\":[\"a\",\"b\"]}}"));
            Assert.Equal("{\"n\":{\"$nin\":[1,2]}}", T("{\"n\":{\"$nin\":[1,2]}}"));
            Assert.Equal("{\"x\":{\"$exists\":false}}", T("{\"x\":{\"$exists\":false}}"));
            Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<VdbException>(() => T("{\"tag\":{\"$in\":[true]}}")).Code);
            Assert.Equal(ErrorCodes.InvalidInput, Assert.Throws<VdbException>(() => T("{\"tag\":[\"a\"]}")).Code);
        }

        [Fact]
        public void LogicalOperators()
        {
            Assert.Equal("{\"$or\":[{\"a\":{\"$eq\":1}},{\"b\":{\"$eq\":2}}]}", T("{\"$or\":[{\"a\":1},{\"b\":2}]}"));
            // $not over a whole filter is pushed down to field level (De Morgan).
            Assert.Equal("{\"$or\":[{\"a\":{\"$not\":{\"$eq\":1}}},{\"b\":{\"$not\":{\"$gt\":2}}}]}",
                T("{\"$not\":{\"a\":1,\"b\":{\"$gt\":2}}}"));
            Assert.Equal("{\"$and\":[{\"a\":{\"$not\":{\"$eq\":1}}},{\"b\":{\"$not\":{\"$eq\":2}}}]}",
                T("{\"$not\":{\"$or\":[{\"a\":1},{\"b\":2}]}}"));
            Assert.Equal("{\"a\":{\"$eq\":1}}", T("{\"$not\":{\"$not\":{\"a\":1}}}"));
        }

        [Fact]
        public void UnknownOperator_IsUnsupported()
        {
            Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<VdbException>(() => T("{\"a\":{\"$regex\":\"x\"}}")).Code);
            Assert.Equal(ErrorCodes.Unsupported, Assert.Throws<VdbException>(() => T("{\"$nor\":[]}")).Code);
        }

        [Fact]
        public void TranslatedFilters_SelectTheRightRecords()
        {
            // Run translated filters through the fake's evaluator of Pinecone's documented semantics.
            var rec = JsonNode.Parse("{\"category\":\"invoice\",\"year\":2024,\"tags\":[\"paid\",\"eu\"]}")!.AsObject();
            bool M(string portable) => FakePinecone.Matches(rec, PineconeFilter.Translate(PortableFilter.Parse(portable)!));
            Assert.True(M("{\"category\":\"invoice\",\"year\":{\"$gte\":2024}}"));
            Assert.False(M("{\"year\":{\"$lt\":2024}}"));
            Assert.True(M("{\"tags\":\"eu\"}"));
            Assert.False(M("{\"$not\":{\"category\":\"invoice\"}}"));
            Assert.True(M("{\"$not\":{\"category\":\"invoice\",\"year\":2020}}"));
            Assert.True(M("{\"$or\":[{\"year\":1999},{\"tags\":{\"$in\":[\"paid\"]}}]}"));
        }
    }

    public class PineconeAdapterTests
    {
        private readonly VectorDBConnector _sut = new();
        private readonly FakePinecone _fake = new();

        private VdbConnection Conn(string key = FakePinecone.Key, string options = "")
        {
            HttpJson.TestTransport.Value = new HttpMessageInvoker(_fake, disposeHandler: false);
            return new VdbConnection
            {
                Provider = "pinecone",
                Endpoint = "https://" + FakePinecone.ControlHost,
                ApiKey = key,
                OptionsJson = options,
            };
        }

        private static string NewName() => "uvdb-test-" + Guid.NewGuid().ToString("N")[..8];

        private static void Ok(VdbStatus s) => Assert.True(s.Success, $"{s.ErrorCode}: {s.ErrorMessage}");

        [Fact]
        public void FullLifecycle()
        {
            var c = Conn();
            var name = NewName();

            _sut.TestConnection(c, out var s, out var info);
            Ok(s);
            Assert.StartsWith("pinecone", info);

            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 3, Metric = "cosine", MetadataIndexesJson = "{\"category\":\"keyword\"}" }, out s);
            Ok(s);
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
            _sut.Upsert(c, name, new[] { new VdbRecord { Id = "ns-doc", Vector = new[] { 1m, 0m, 0m }, MetadataJson = "{\"category\":\"invoice\",\"year\":2024}" } }, out s, out _, "tenantA");
            Ok(s);

            // Pinecone searches one namespace at a time: the empty namespace is the default one.
            _sut.Query(c, new VdbQuery { Collection = name, Vector = new[] { 1m, 0m, 0m }, TopK = 10 }, out s, out var all);
            Ok(s);
            Assert.Equal(new[] { "doc-1", "doc-2", "doc-3" }, all.Select(m => m.Id));

            _sut.Query(c, new VdbQuery
            {
                Collection = name,
                Vector = new[] { 1m, 0m, 0m },
                TopK = 5,
                FilterJson = "{\"category\":\"invoice\",\"year\":{\"$gte\":2023}}",
                IncludeVectors = true,
            }, out s, out var matches);
            Ok(s);
            var hit = Assert.Single(matches);
            Assert.Equal("doc-1", hit.Id);
            Assert.Equal("invoice from 2024", hit.Text);
            Assert.Equal("{\"category\":\"invoice\",\"year\":2024}", hit.MetadataJson); // _text is not exposed
            Assert.Equal(new[] { 1m, 0m, 0m }, hit.Vector);
            Assert.InRange(hit.Similarity, 0.99m, 1.0m);

            _sut.Query(c, new VdbQuery { Collection = name, Namespace = "tenantA", Vector = new[] { 1m, 0m, 0m } }, out s, out var nsMatches);
            Ok(s);
            Assert.Equal("ns-doc", Assert.Single(nsMatches).Id);

            _sut.Fetch(c, name, new[] { "doc-3", "doc-1", "missing" }, out s, out var fetched);
            Ok(s);
            Assert.Equal(new[] { "doc-3", "doc-1" }, fetched.Select(r => r.Id));
            Assert.All(fetched, r => Assert.Empty(r.Vector));

            _sut.Count(c, name, out s, out var total);
            Ok(s);
            Assert.Equal(4, total); // all namespaces
            _sut.Count(c, name, out s, out var inTenant, "tenantA");
            Ok(s);
            Assert.Equal(1, inTenant);
            _sut.Count(c, name, out s, out _, null, "{\"category\":\"invoice\"}");
            Assert.Equal("Unsupported", s.ErrorCode);

            _sut.Delete(c, name, new[] { "doc-1" }, out s);
            Ok(s);
            _sut.DeleteByFilter(c, name, "{\"year\":{\"$lt\":2022}}", out s);
            Ok(s);
            _sut.DeleteByFilter(c, name, "", out s);
            Assert.Equal("InvalidInput", s.ErrorCode);
            _sut.Count(c, name, out s, out total);
            Ok(s);
            Assert.Equal(2, total); // doc-3 and ns-doc remain

            _sut.DeleteCollection(c, name, out s);
            Ok(s);
            _sut.CollectionExists(c, name, out s, out var stillThere);
            Ok(s);
            Assert.False(stillThere);
            _sut.DeleteCollection(c, name, out s);
            Assert.Equal("NotFound", s.ErrorCode);
        }

        [Fact]
        public void CreateCollection_SendsSchemaAndOptions()
        {
            var c = Conn(options: "{\"cloud\":\"gcp\",\"region\":\"us-central1\",\"deletionProtection\":true}");
            var name = NewName();
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 8, Metric = "dot" }, out var s);
            Ok(s);
            var body = _fake.Requests.First(r => r.Method == "POST" && r.Path == "/indexes").Body!;
            Assert.Equal("dense_vector", body["schema"]!["fields"]!["_values"]!["type"]!.ToString());
            Assert.Equal(8, body["schema"]!["fields"]!["_values"]!["dimension"]!.GetValue<int>());
            Assert.Equal("dotproduct", body["schema"]!["fields"]!["_values"]!["metric"]!.ToString());
            Assert.Equal("managed", body["deployment"]!["deployment_type"]!.ToString());
            Assert.Equal("gcp", body["deployment"]!["cloud"]!.ToString());
            Assert.Equal("us-central1", body["deployment"]!["region"]!.ToString());
            Assert.Equal("enabled", body["deletion_protection"]!.ToString());
        }

        [Fact]
        public void CreateCollection_WaitsUntilReady()
        {
            _fake.CreateNotReady = true;
            var c = Conn();
            var name = NewName();
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 2 }, out var s);
            Ok(s);
            Assert.Contains(_fake.Requests, r => r.Method == "GET" && r.Path == "/indexes/" + name);
            _sut.Upsert(c, name, new[] { new VdbRecord { Id = "a", Vector = new[] { 1m, 0m } } }, out s, out _);
            Ok(s);
        }

        [Fact]
        public void NonAsciiAndLongIds_AreMapped_AndRoundTrip()
        {
            var c = Conn();
            var name = NewName();
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 2 }, out var s);
            Ok(s);
            var longId = new string('x', 600);
            _sut.Upsert(c, name, new[]
            {
                new VdbRecord { Id = "café-1", Vector = new[] { 1m, 0m } },
                new VdbRecord { Id = longId, Vector = new[] { 0m, 1m } },
            }, out s, out _);
            Ok(s);

            var sent = (JsonArray)_fake.Requests.Last(r => r.Path == "/vectors/upsert").Body!["vectors"]!;
            Assert.All(sent, v => Assert.StartsWith("uvdb-", v!["id"]!.ToString()));

            _sut.Fetch(c, name, new[] { "café-1", longId }, out s, out var fetched);
            Ok(s);
            Assert.Equal(new[] { "café-1", longId }, fetched.Select(r => r.Id));
            Assert.All(fetched, r => Assert.Equal("{}", r.MetadataJson)); // _id is not exposed

            _sut.Query(c, new VdbQuery { Collection = name, Vector = new[] { 1m, 0m }, TopK = 1 }, out s, out var top);
            Ok(s);
            Assert.Equal("café-1", Assert.Single(top).Id);

            _sut.Delete(c, name, new[] { "café-1" }, out s);
            Ok(s);
            _sut.Count(c, name, out s, out var n);
            Ok(s);
            Assert.Equal(1, n);
        }

        [Fact]
        public void Metadata_IsCheckedAgainstPineconeRules()
        {
            var c = Conn();
            var name = NewName();
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 1 }, out var s);
            Ok(s);

            _sut.Upsert(c, name, new[] { new VdbRecord { Id = "a", Vector = new[] { 1m }, MetadataJson = "{\"address\":{\"city\":\"Lisbon\"}}" } }, out s, out _);
            Assert.Equal("InvalidInput", s.ErrorCode);
            Assert.Contains("nested", s.ErrorMessage);

            _sut.Upsert(c, name, new[] { new VdbRecord { Id = "a", Vector = new[] { 1m }, MetadataJson = "{\"scores\":[1,2]}" } }, out s, out _);
            Assert.Equal("InvalidInput", s.ErrorCode);

            _sut.Upsert(c, name, new[] { new VdbRecord { Id = "a", Vector = new[] { 1m }, MetadataJson = "{\"note\":null,\"tags\":[\"x\"],\"ok\":true}" } }, out s, out _);
            Ok(s);
            var meta = _fake.Requests.Last(r => r.Path == "/vectors/upsert").Body!["vectors"]![0]!["metadata"]!.ToJsonString();
            Assert.Equal("{\"tags\":[\"x\"],\"ok\":true}", meta); // null dropped
        }

        [Fact]
        public void Upsert_IsBatched()
        {
            var c = Conn();
            var name = NewName();
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 2 }, out var s);
            Ok(s);
            var records = Enumerable.Range(0, 2500).Select(i => new VdbRecord { Id = "r" + i, Vector = new[] { 1m, i } });
            _sut.Upsert(c, name, records, out s, out var count);
            Ok(s);
            Assert.Equal(2500, count);
            Assert.Equal(3, _fake.Requests.Count(r => r.Path == "/vectors/upsert"));
        }

        [Fact]
        public void Euclidean_ScoresAreNormalized()
        {
            var c = Conn();
            var name = NewName();
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 2, Metric = "euclidean" }, out var s);
            Ok(s);
            _sut.Upsert(c, name, new[]
            {
                new VdbRecord { Id = "near", Vector = new[] { 1m, 0m } },
                new VdbRecord { Id = "far", Vector = new[] { 4m, 0m } },
            }, out s, out _);
            Ok(s);
            _sut.Query(c, new VdbQuery { Collection = name, Vector = new[] { 1m, 0m }, MinScore = 0.5m }, out s, out var matches);
            Ok(s);
            var m = Assert.Single(matches); // far: squared distance 9 -> similarity 0.25, dropped
            Assert.Equal("near", m.Id);
            Assert.Equal(1m, m.Similarity);
        }

        [Fact]
        public void Errors_AreMapped()
        {
            _sut.ListCollections(Conn("wrong-key"), out var s, out _);
            Assert.Equal("Unauthorized", s.ErrorCode);
            Assert.Equal(401, s.ProviderHttpStatus);

            _sut.Query(Conn(), new VdbQuery { Collection = "does-not-exist", Vector = new[] { 1m } }, out s, out _);
            Assert.Equal("NotFound", s.ErrorCode);

            _sut.CreateCollection(Conn(), new VdbCollectionSpec { Name = "My_Docs", Dimension = 3 }, out s);
            Assert.Equal("InvalidInput", s.ErrorCode);
            Assert.Contains("lowercase", s.ErrorMessage);

            _sut.Upsert(Conn(), "docs", new[] { new VdbRecord { Id = "a", Vector = new[] { 1m } } }, out s, out _, "naïve");
            Assert.Equal("InvalidInput", s.ErrorCode);
        }

        [Fact]
        public void ScoreNormalization()
        {
            Assert.Equal(1.0, PineconeAdapter.Normalize("cosine", 1));
            Assert.Equal(0.5, PineconeAdapter.Normalize("cosine", 0));
            Assert.Equal(0.5, PineconeAdapter.Normalize("euclidean", 1));
            Assert.Equal(0.25, PineconeAdapter.Normalize("euclidean", 9));
            Assert.Equal(7.0, PineconeAdapter.Normalize("dotproduct", 7));
        }

        [Fact]
        public void ErrorBodies_AreParsed()
        {
            var e = PineconeAdapter.MapError(409, "{\"error\":{\"code\":\"ALREADY_EXISTS\",\"message\":\"Resource already exists\"},\"status\":409}");
            Assert.Equal(ErrorCodes.AlreadyExists, e.Code);
            Assert.Equal("Pinecone: Resource already exists", e.Message);
            e = PineconeAdapter.MapError(400, "{\"code\":3,\"message\":\"Vector dimension 2 does not match the dimension of the index 3\",\"details\":[]}");
            Assert.Equal(ErrorCodes.InvalidInput, e.Code);
            Assert.Contains("dimension", e.Message);
            e = PineconeAdapter.MapError(401, "Unauthorized");
            Assert.Equal(ErrorCodes.Unauthorized, e.Code);
        }
    }

    /// <summary>
    /// End-to-end tests against real Pinecone. Skipped unless PINECONE_API_KEY is set.
    /// Uses (and deletes) one serverless index on aws us-east-1, which the free Starter plan allows.
    /// </summary>
    public class PineconeLiveTests
    {
        private static readonly string? Key = Environment.GetEnvironmentVariable("PINECONE_API_KEY");
        private readonly VectorDBConnector _sut = new();

        private static VdbConnection Conn(string? key = null) => new()
        {
            Provider = "pinecone",
            Endpoint = Environment.GetEnvironmentVariable("PINECONE_ENDPOINT") ?? "https://api.pinecone.io",
            ApiKey = key ?? Key!,
            TimeoutSeconds = 85,
        };

        private static void Ok(VdbStatus s) => Assert.True(s.Success, $"{s.ErrorCode}: {s.ErrorMessage}");

        /// <summary>Pinecone is eventually consistent: retry a read until it shows the expected state.</summary>
        private static void Eventually(Func<bool> check)
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!check())
            {
                Assert.True(DateTime.UtcNow < until, "Pinecone did not reach the expected state within 60 s.");
                Thread.Sleep(2000);
            }
        }

        [Fact]
        public void FullLifecycle()
        {
            Assert.SkipWhen(Key == null, "PINECONE_API_KEY not set");
            var c = Conn();
            var name = "uvdb-test-" + Guid.NewGuid().ToString("N")[..8];

            _sut.TestConnection(c, out var s, out _);
            Ok(s);
            _sut.CreateCollection(c, new VdbCollectionSpec { Name = name, Dimension = 3, Metric = "cosine" }, out s);
            Ok(s);
            try
            {
                Eventually(() => { _sut.CollectionExists(c, name, out var st, out var e); return st.Success && e; });
                var records = new[]
                {
                    new VdbRecord { Id = "doc-1", Vector = new[] { 1m, 0m, 0m }, Text = "invoice from 2024", MetadataJson = "{\"category\":\"invoice\",\"year\":2024}" },
                    new VdbRecord { Id = "doc-2", Vector = new[] { 0.9m, 0.1m, 0m }, Text = "invoice from 2021", MetadataJson = "{\"category\":\"invoice\",\"year\":2021}" },
                    new VdbRecord { Id = "café-3", Vector = new[] { 0m, 1m, 0m }, Text = "contract", MetadataJson = "{\"category\":\"contract\",\"year\":2024}" },
                };
                // The first write can fail while a new index is still starting up.
                Eventually(() => { _sut.Upsert(c, name, records, out var st, out _); return st.Success; });

                Eventually(() => { _sut.Count(c, name, out var st, out var n); return st.Success && n == 3; });

                List<VdbMatch> list = new();
                Eventually(() =>
                {
                    _sut.Query(c, new VdbQuery
                    {
                        Collection = name,
                        Vector = new[] { 1m, 0m, 0m },
                        TopK = 5,
                        FilterJson = "{\"category\":\"invoice\",\"year\":{\"$gte\":2023}}",
                    }, out var st, out var m);
                    list = m.ToList();
                    return st.Success && list.Count == 1;
                });
                Assert.Equal("doc-1", list[0].Id);
                Assert.Equal("invoice from 2024", list[0].Text);

                _sut.Fetch(c, name, new[] { "café-3", "missing" }, out s, out var fetched);
                Ok(s);
                Assert.Equal("café-3", Assert.Single(fetched).Id);

                _sut.DeleteByFilter(c, name, "{\"year\":{\"$lt\":2022}}", out s);
                Ok(s);
                _sut.Delete(c, name, new[] { "café-3" }, out s);
                Ok(s);
                Eventually(() => { _sut.Count(c, name, out var st, out var n); return st.Success && n == 1; });
            }
            finally
            {
                _sut.DeleteCollection(c, name, out s);
                Ok(s);
            }
        }

        [Fact]
        public void WrongApiKey_IsUnauthorized()
        {
            Assert.SkipWhen(Key == null, "PINECONE_API_KEY not set");
            _sut.ListCollections(Conn("wrong-key"), out var s, out _);
            Assert.Equal("Unauthorized", s.ErrorCode);
        }
    }
}

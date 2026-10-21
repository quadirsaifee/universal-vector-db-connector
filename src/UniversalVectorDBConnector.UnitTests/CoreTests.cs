using System.Text.Json.Nodes;
using UniversalVectorDBConnector.Adapters.Qdrant;
using UniversalVectorDBConnector.Core;
using Xunit;

namespace UniversalVectorDBConnector.UnitTests
{
    public class VectorCodecTests
    {
        [Fact]
        public void EncodeDecode_RoundTrips()
        {
            var v = new[] { 0.1f, -2.5f, 3.25f, 0f };
            var decoded = VectorCodec.Decode(VectorCodec.Encode(v));
            Assert.Equal(v, decoded);
        }

        [Fact]
        public void Decode_RejectsPartialFloat()
        {
            var ex = Assert.Throws<VdbException>(() => VectorCodec.Decode(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
            Assert.Equal(ErrorCodes.InvalidInput, ex.Code);
        }

        [Fact]
        public void Resolve_PrefersDecimalList_ThenBase64()
        {
            var b64 = VectorCodec.Encode(new[] { 9f });
            Assert.Equal(new[] { 1f, 2f }, VectorCodec.Resolve(new[] { 1m, 2m }, b64, "x"));
            Assert.Equal(new[] { 9f }, VectorCodec.Resolve(Array.Empty<decimal>(), b64, "x"));
            Assert.Throws<VdbException>(() => VectorCodec.Resolve(null, "", "x"));
        }
    }

    public class IdMapperTests
    {
        [Fact]
        public void Guid_IsKept()
        {
            var g = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
            Assert.Equal(g, IdMapper.ToQdrantId(g).GetValue<string>());
        }

        [Fact]
        public void Digits_BecomeNumber()
        {
            Assert.Equal(42UL, IdMapper.ToQdrantId("42").GetValue<ulong>());
        }

        [Fact]
        public void Text_BecomesStableUuidV5()
        {
            var a = IdMapper.ToQdrantId("doc-1#chunk-3").GetValue<string>();
            var b = IdMapper.ToQdrantId("doc-1#chunk-3").GetValue<string>();
            Assert.Equal(a, b);
            Assert.Equal('5', a[14]); // version nibble
            Assert.Contains(a[19], "89ab");  // RFC 4122 variant
            Assert.NotEqual(a, IdMapper.ToQdrantId("doc-1#chunk-4").GetValue<string>());
        }
    }

    public class CallContextTests
    {
        [Fact]
        public void Http_RejectedForRemoteHost()
        {
            var ex = Assert.Throws<VdbException>(() => CallContext.Create(new VdbConnection { Provider = "qdrant", Endpoint = "http://db.example.com:6333" }));
            Assert.Equal(ErrorCodes.InvalidInput, ex.Code);
        }

        [Fact]
        public void Http_AllowedForLocalhost_AndWithOption()
        {
            using var a = CallContext.Create(new VdbConnection { Provider = "qdrant", Endpoint = "http://localhost:6333" });
            using var b = CallContext.Create(new VdbConnection { Provider = "QDRANT", Endpoint = "http://db:6333/", OptionsJson = "{\"allowInsecure\":true}" });
            Assert.Equal("qdrant", b.Provider);
        }

        [Fact]
        public void Timeout_IsCapped()
        {
            using var ctx = CallContext.Create(new VdbConnection { Provider = "qdrant", Endpoint = "https://x.io", TimeoutSeconds = 500 });
            Assert.True(ctx.Remaining <= TimeSpan.FromSeconds(CallContext.MaxTimeoutSeconds));
        }
    }

    public class QdrantFilterTests
    {
        private static string T(string portable, string ns = "") =>
            QdrantFilter.Build(PortableFilter.Parse(portable), ns)!.ToJsonString();

        [Fact]
        public void Equality_Shorthand_And_Operator()
        {
            Assert.Equal(
                "{\"must\":[{\"must\":[{\"key\":\"category\",\"match\":{\"value\":\"invoice\"}}]}]}",
                T("{\"category\":\"invoice\"}"));
            Assert.Equal(T("{\"category\":\"invoice\"}"), T("{\"category\":{\"$eq\":\"invoice\"}}"));
        }

        [Fact]
        public void Range_IsMergedIntoOneCondition()
        {
            var json = T("{\"year\":{\"$gte\":2020,\"$lt\":2025}}");
            Assert.Contains("\"range\":{\"gte\":2020,\"lt\":2025}", json);
        }

        [Fact]
        public void DateRange_UsesDatetimeRange()
        {
            Assert.Contains("datetime_range", T("{\"created\":{\"$gte\":\"2024-01-01T00:00:00Z\"}}"));
        }

        [Fact]
        public void InAndNin()
        {
            Assert.Contains("\"match\":{\"any\":[\"a\",\"b\"]}", T("{\"tag\":{\"$in\":[\"a\",\"b\"]}}"));
            Assert.Contains("\"match\":{\"except\":[1,2]}", T("{\"n\":{\"$nin\":[1,2]}}"));
        }

        [Fact]
        public void LogicalOperators()
        {
            var json = T("{\"$or\":[{\"a\":1},{\"b\":{\"$ne\":\"x\"}}]}");
            Assert.Contains("\"should\":[", json);
            Assert.Contains("\"must_not\":[{\"key\":\"b\"", json);
            Assert.Contains("\"must_not\":[{\"must\"", T("{\"$not\":{\"a\":1}}"));
        }

        [Fact]
        public void Exists()
        {
            Assert.Contains("\"must_not\":[{\"is_empty\":{\"key\":\"a\"}}]", T("{\"a\":{\"$exists\":true}}"));
            Assert.Contains("{\"is_empty\":{\"key\":\"a\"}}", T("{\"a\":{\"$exists\":false}}"));
        }

        [Fact]
        public void DecimalEquality_UsesClosedRange()
        {
            Assert.Contains("\"range\":{\"gte\":0.5,\"lte\":0.5}", T("{\"score\":0.5}"));
        }

        [Fact]
        public void Namespace_IsAdded()
        {
            Assert.Contains("{\"key\":\"_namespace\",\"match\":{\"value\":\"tenantA\"}}", T("{\"a\":1}", "tenantA"));
            Assert.Null(QdrantFilter.Build(null, ""));
        }

        [Fact]
        public void UnknownOperator_IsUnsupported()
        {
            var ex = Assert.Throws<VdbException>(() => T("{\"a\":{\"$regex\":\"x\"}}"));
            Assert.Equal(ErrorCodes.Unsupported, ex.Code);
        }

        [Fact]
        public void InvalidJson_IsInvalidInput()
        {
            var ex = Assert.Throws<VdbException>(() => PortableFilter.Parse("{nope"));
            Assert.Equal(ErrorCodes.InvalidInput, ex.Code);
        }
    }

    public class ConnectorTests
    {
        private readonly VectorDBConnector _sut = new();

        [Fact]
        public void UnknownProvider_ReturnsUnsupportedStatus()
        {
            _sut.ListCollections(new VdbConnection { Provider = "pinecone", Endpoint = "https://x.io" }, out var status, out var names);
            Assert.False(status.Success);
            Assert.Equal("Unsupported", status.ErrorCode);
            Assert.Contains("planned", status.ErrorMessage);
            Assert.Empty(names);
        }

        [Fact]
        public void MissingProvider_ReturnsInvalidInput()
        {
            _sut.TestConnection(new VdbConnection { Endpoint = "https://x.io" }, out var status, out _);
            Assert.Equal("InvalidInput", status.ErrorCode);
        }

        [Fact]
        public void Normalize_RejectsMixedDimensionsAndReservedKeys()
        {
            var ex = Assert.Throws<VdbException>(() => VectorDBConnector.Normalize(new[]
            {
                new VdbRecord { Id = "a", Vector = new[] { 1m, 2m } },
                new VdbRecord { Id = "b", Vector = new[] { 1m } },
            }));
            Assert.Contains("dimensions", ex.Message);

            ex = Assert.Throws<VdbException>(() => VectorDBConnector.Normalize(new[]
            {
                new VdbRecord { Id = "a", Vector = new[] { 1m }, MetadataJson = "{\"_id\":1}" },
            }));
            Assert.Contains("reserved", ex.Message);
        }

        [Fact]
        public void Capabilities()
        {
            Assert.True(_sut.GetCapabilities("Qdrant").Implemented);
            Assert.False(_sut.GetCapabilities("pinecone").Implemented);
        }

        [Fact]
        public void EncodeDecodeActions()
        {
            var b64 = _sut.EncodeVector(new[] { 0.5m, -1m });
            Assert.Equal(new[] { 0.5m, -1m }, _sut.DecodeVector(b64));
        }

        [Fact]
        public void ScoreNormalization()
        {
            Assert.Equal(1.0, QdrantAdapter.Normalize("Cosine", 1));
            Assert.Equal(0.5, QdrantAdapter.Normalize("Cosine", 0));
            Assert.Equal(0.5, QdrantAdapter.Normalize("Euclid", 1));
            Assert.Equal(7.0, QdrantAdapter.Normalize("Dot", 7));
        }
    }
}

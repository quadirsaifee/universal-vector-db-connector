using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UniversalVectorDBConnector.Core
{
    /// <summary>
    /// Maps free-text ODC ids to provider ids. Qdrant only accepts unsigned integers or UUIDs,
    /// so other text becomes a deterministic UUIDv5. The original id is always stored in the payload.
    /// </summary>
    internal static class IdMapper
    {
        // Fixed namespace for UUIDv5 generation. Never change it: existing records depend on it.
        private static readonly Guid Namespace = new("6f1e2d3c-4b5a-5968-8776-a5b4c3d2e1f0");

        public static JsonNode ToQdrantId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw VdbException.Invalid("Record Id cannot be empty.");
            }
            if (Guid.TryParseExact(id, "D", out var guid))
            {
                return JsonValue.Create(guid.ToString("D"))!;
            }
            if (id.Length <= 19 && id.All(char.IsAsciiDigit) && ulong.TryParse(id, out var number))
            {
                return JsonValue.Create(number)!;
            }
            return JsonValue.Create(UuidV5(id).ToString("D"))!;
        }

        /// <summary>Pinecone ids must be 1-512 ASCII characters (no NUL). Other ids become "uvdb-" + UUIDv5.</summary>
        public static string ToPineconeId(string id, out bool mapped)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw VdbException.Invalid("Record Id cannot be empty.");
            }
            mapped = id.Length > 512 || id.Any(ch => ch is '\0' or > '\x7F');
            return mapped ? "uvdb-" + UuidV5(id).ToString("D") : id;
        }

        public static Guid UuidV5(string name)
        {
            Span<byte> ns = stackalloc byte[16];
            Namespace.TryWriteBytes(ns, bigEndian: true, out _);
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var input = new byte[16 + nameBytes.Length];
            ns.CopyTo(input);
            nameBytes.CopyTo(input, 16);
            var hash = SHA1.HashData(input);
            hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
            return new Guid(hash.AsSpan(0, 16), bigEndian: true);
        }
    }
}

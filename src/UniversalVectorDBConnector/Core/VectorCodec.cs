using System.Buffers.Binary;

namespace UniversalVectorDBConnector.Core
{
    /// <summary>Converts vectors between ODC decimal lists, float arrays and base64 float32 text.</summary>
    internal static class VectorCodec
    {
        public static string Encode(IReadOnlyList<float> vector)
        {
            var bytes = new byte[vector.Count * 4];
            for (int i = 0; i < vector.Count; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
            }
            return Convert.ToBase64String(bytes);
        }

        public static float[] Decode(string base64)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                throw VdbException.Invalid("VectorBase64 is not valid base64.");
            }
            if (bytes.Length == 0 || bytes.Length % 4 != 0)
            {
                throw VdbException.Invalid("VectorBase64 must hold a whole number of float32 values.");
            }
            var result = new float[bytes.Length / 4];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
            }
            return result;
        }

        public static float[] FromDecimals(IEnumerable<decimal> values)
        {
            return values.Select(v => (float)v).ToArray();
        }

        public static List<decimal> ToDecimals(IEnumerable<float> values)
        {
            var list = new List<decimal>();
            foreach (var v in values)
            {
                list.Add(float.IsFinite(v) ? (decimal)v : 0m);
            }
            return list;
        }

        /// <summary>Picks the vector from either the decimal list or the base64 text.</summary>
        public static float[] Resolve(IEnumerable<decimal>? vector, string? vectorBase64, string what)
        {
            if (vector != null)
            {
                var floats = FromDecimals(vector);
                if (floats.Length > 0)
                {
                    return floats;
                }
            }
            if (!string.IsNullOrWhiteSpace(vectorBase64))
            {
                return Decode(vectorBase64);
            }
            throw VdbException.Invalid($"{what} needs a vector: set Vector or VectorBase64.");
        }
    }
}

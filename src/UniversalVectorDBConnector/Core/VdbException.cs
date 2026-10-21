namespace UniversalVectorDBConnector.Core
{
    internal static class ErrorCodes
    {
        public const string InvalidInput = "InvalidInput";
        public const string Unauthorized = "Unauthorized";
        public const string NotFound = "NotFound";
        public const string AlreadyExists = "AlreadyExists";
        public const string RateLimited = "RateLimited";
        public const string Timeout = "Timeout";
        public const string PayloadTooLarge = "PayloadTooLarge";
        public const string Unsupported = "Unsupported";
        public const string ProviderError = "ProviderError";
        public const string NetworkError = "NetworkError";
    }

    /// <summary>Internal error carrying a normalized code. Turned into a VdbStatus at the ODC boundary.</summary>
    internal sealed class VdbException : Exception
    {
        public string Code { get; }
        public int HttpStatus { get; }
        public int RetryAfterMs { get; }

        public VdbException(string code, string message, int httpStatus = 0, int retryAfterMs = 0)
            : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
            RetryAfterMs = retryAfterMs;
        }

        public static VdbException Invalid(string message) => new(ErrorCodes.InvalidInput, message);
    }

    internal static class StatusFactory
    {
        public static VdbStatus Ok() => new()
        {
            Success = true,
            ErrorCode = "",
            ErrorMessage = "",
        };

        public static VdbStatus From(VdbException ex) => new()
        {
            Success = false,
            ErrorCode = ex.Code,
            ErrorMessage = ex.Message,
            ProviderHttpStatus = ex.HttpStatus,
            RetryAfterMs = ex.RetryAfterMs,
        };
    }
}

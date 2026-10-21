using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UniversalVectorDBConnector.Core
{
    /// <summary>Shared HTTP client with retries inside the call's time budget.</summary>
    internal static class HttpJson
    {
        private const int MaxAttempts = 3;

        private static readonly HttpClient Client = new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // the call's CancellationToken enforces the budget
        };

        /// <summary>Sends a JSON request and returns the parsed body (null for an empty body).</summary>
        public static JsonNode? Send(
            CallContext ctx,
            HttpMethod method,
            string pathAndQuery,
            JsonNode? body,
            Action<HttpRequestMessage> addHeaders,
            Func<int, string, VdbException> mapError)
        {
            var url = new Uri(ctx.Endpoint, ctx.Endpoint.AbsolutePath.TrimEnd('/') + pathAndQuery);
            var payload = body?.ToJsonString();

            for (int attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, url);
                addHeaders(request);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (payload != null)
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }

                HttpResponseMessage response;
                try
                {
                    response = Client.SendAsync(request, ctx.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    throw new VdbException(ErrorCodes.Timeout, "The call ran out of its time budget.");
                }
                catch (HttpRequestException ex)
                {
                    if (CanRetry(ctx, attempt, out var wait, null))
                    {
                        Sleep(ctx, wait);
                        continue;
                    }
                    throw new VdbException(ErrorCodes.NetworkError, "Could not reach the database: " + ex.Message);
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    string text;
                    try
                    {
                        text = response.Content.ReadAsStringAsync(ctx.Token).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        throw new VdbException(ErrorCodes.Timeout, "The call ran out of its time budget.");
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            return null;
                        }
                        try
                        {
                            return JsonNode.Parse(text);
                        }
                        catch (JsonException)
                        {
                            throw new VdbException(ErrorCodes.ProviderError, "The database returned a response that is not JSON.", status);
                        }
                    }

                    var retryAfter = RetryAfter(response);
                    if (IsTransient(status) && CanRetry(ctx, attempt, out var wait, retryAfter))
                    {
                        Sleep(ctx, wait);
                        continue;
                    }

                    var ex = mapError(status, text);
                    if (ex.RetryAfterMs == 0 && retryAfter.HasValue)
                    {
                        ex = new VdbException(ex.Code, ex.Message, ex.HttpStatus, (int)retryAfter.Value.TotalMilliseconds);
                    }
                    throw ex;
                }
            }
        }

        /// <summary>Default mapping from HTTP status to error code.</summary>
        public static string CodeFor(int status) => status switch
        {
            400 or 422 => ErrorCodes.InvalidInput,
            401 or 403 => ErrorCodes.Unauthorized,
            404 => ErrorCodes.NotFound,
            409 => ErrorCodes.AlreadyExists,
            413 => ErrorCodes.PayloadTooLarge,
            429 => ErrorCodes.RateLimited,
            408 or 504 => ErrorCodes.Timeout,
            _ => ErrorCodes.ProviderError,
        };

        private static bool IsTransient(int status) => status is 429 or 502 or 503 or 504;

        private static bool CanRetry(CallContext ctx, int attempt, out TimeSpan wait, TimeSpan? retryAfter)
        {
            var backoff = TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));
            wait = retryAfter.HasValue && retryAfter.Value > backoff ? retryAfter.Value : backoff;
            // Keep at least 2 s of budget for the retried request itself.
            return attempt < MaxAttempts && ctx.Remaining - wait > TimeSpan.FromSeconds(2);
        }

        private static void Sleep(CallContext ctx, TimeSpan wait)
        {
            try
            {
                Task.Delay(wait, ctx.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw new VdbException(ErrorCodes.Timeout, "The call ran out of its time budget.");
            }
        }

        private static TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var ra = response.Headers.RetryAfter;
            if (ra == null)
            {
                return null;
            }
            if (ra.Delta.HasValue)
            {
                return ra.Delta.Value;
            }
            if (ra.Date.HasValue)
            {
                var d = ra.Date.Value - DateTimeOffset.UtcNow;
                return d > TimeSpan.Zero ? d : TimeSpan.Zero;
            }
            return null;
        }
    }
}

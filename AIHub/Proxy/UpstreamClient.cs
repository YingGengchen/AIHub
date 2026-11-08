using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIHub.Proxy
{
    /// <summary>
    /// 面向客户端的协议错误（会以 Ollama/OpenAI 风格 JSON 返回）。
    /// </summary>
    internal sealed class ProxyException : Exception
    {
        public ProxyException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }

    /// <summary>
    /// 上游 OpenAI 兼容服务返回的错误。
    /// </summary>
    internal sealed class UpstreamException : Exception
    {
        public UpstreamException(int statusCode, string message, bool retriable) : base(message)
        {
            StatusCode = statusCode;
            Retriable = retriable;
        }

        public int StatusCode { get; }

        public bool Retriable { get; }
    }

    internal sealed class ResolvedModel
    {
        public ResolvedModel(string modelName, string providerName, string modelId, ProviderSetting provider)
        {
            ModelName = modelName;
            ProviderName = providerName;
            ModelId = modelId;
            Provider = provider;
        }

        public string ModelName { get; }

        public string ProviderName { get; }

        public string ModelId { get; }

        public ProviderSetting Provider { get; }
    }

    internal sealed class OpenAiCompatibleClient : IDisposable
    {
        private const string AnthropicVersion = "2023-06-01";

        private readonly HttpClient _http;

        public OpenAiCompatibleClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };

            _http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMinutes(10),
            };
        }

        /// <summary>按提供方协议打开聊天流；Anthropic 的 SSE 会被实时翻译成 OpenAI 分片。</summary>
        public async Task<HttpResponseMessage> SendChatStreamAsync(ResolvedModel resolved, JObject body, CancellationToken cancellationToken)
        {
            if (!resolved.Provider.IsAnthropic)
            {
                return await SendChatAsync(resolved.Provider, body, cancellationToken).ConfigureAwait(false);
            }

            var request = BuildAnthropicRequest(resolved, body, stream: true);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return response;
            }

            var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var translator = new AnthropicSseTranslator(source, resolved.ModelName, response);

            var synthetic = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(translator),
            };

            synthetic.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return synthetic;
        }

        public Task<HttpResponseMessage> SendChatAsync(ProviderSetting provider, JObject body, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(provider.BaseUrl))
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", "AIHub-VSIX/1.2");

            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + provider.ApiKey.Trim());
            }

            return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        public async Task<JObject> SendChatJsonAsync(ResolvedModel resolved, JObject body, CancellationToken cancellationToken)
        {
            if (!resolved.Provider.IsAnthropic)
            {
                return await SendJsonAsync(BuildEndpoint(resolved.Provider.BaseUrl), resolved.Provider, body, cancellationToken)
                    .ConfigureAwait(false);
            }

            var request = BuildAnthropicRequest(resolved, body, stream: false);
            var payload = await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
            return AnthropicProtocol.ToOpenAiResponse(payload, resolved.ModelName);
        }

        /// <summary>转发 embeddings；Anthropic 没有 embeddings 接口。</summary>
        public async Task<JObject> SendEmbeddingsJsonAsync(ProviderSetting provider, JObject body, CancellationToken cancellationToken)
        {
            if (provider.IsAnthropic)
            {
                throw new ProxyException(400, "提供方 anthropic 不支持 embeddings。");
            }

            return await SendJsonAsync(BuildEndpoint(provider.BaseUrl, "embeddings"), provider, body, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<JObject> SendJsonAsync(string endpoint, ProviderSetting provider, JObject body, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", "AIHub-VSIX/1.2");

            if (!string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + provider.ApiKey.Trim());
            }

            return await SendAndReadAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private async Task<JObject> SendAndReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;

            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
            {
                throw new UpstreamException(502, "无法连接上游服务：" + ex.Message, true);
            }

            using (response)
            {
                var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var retriable = statusCode == 408 || statusCode == 429 || statusCode >= 500;
                    throw new UpstreamException(statusCode, ExtractErrorMessage(payload, statusCode), retriable);
                }

                try
                {
                    return JObject.Parse(payload);
                }
                catch (JsonException ex)
                {
                    throw new UpstreamException((int)response.StatusCode, $"上游返回了无法解析的响应：{ex.Message}", false);
                }
            }
        }

        private HttpRequestMessage BuildAnthropicRequest(ResolvedModel resolved, JObject body, bool stream)
        {
            var anthropicBody = AnthropicProtocol.BuildRequest(body);
            anthropicBody["stream"] = stream;

            var request = new HttpRequestMessage(HttpMethod.Post, AnthropicProtocol.BuildEndpoint(resolved.Provider.BaseUrl))
            {
                Content = new StringContent(anthropicBody.ToString(Formatting.None), Encoding.UTF8, "application/json"),
            };

            request.Headers.TryAddWithoutValidation("User-Agent", "AIHub-VSIX/1.2");
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

            var apiKey = (resolved.Provider.ApiKey ?? string.Empty).Trim();
            if (apiKey.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

                // 第三方 Anthropic 兼容网关通常用 Bearer，官方端点只认 x-api-key。
                if (!IsOfficialAnthropicEndpoint(resolved.Provider.BaseUrl))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                }
            }

            return request;
        }

        private static bool IsOfficialAnthropicEndpoint(string? baseUrl)
        {
            return Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
                && uri.Host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase);
        }

        public static string BuildEndpoint(string baseUrl)
        {
            return BuildEndpoint(baseUrl, "chat/completions");
        }

        public static string BuildEndpoint(string baseUrl, string suffix)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (trimmed.Length == 0)
            {
                throw new ProxyException(400, "未配置提供方的 Base URL。");
            }

            if (trimmed.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return trimmed + "/" + suffix;
        }

        public static string ExtractErrorMessage(string payload, int statusCode)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return $"上游返回 HTTP {statusCode}。";
            }

            try
            {
                var json = JObject.Parse(payload);
                var error = json["error"];

                if (error is JObject errorObject)
                {
                    var message = errorObject.Value<string>("message");
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        return $"上游错误：{message}";
                    }
                }

                if (error?.Type == JTokenType.String)
                {
                    return $"上游错误：{error.Value<string>()}";
                }

                var detail = json.Value<string>("detail") ?? json.Value<string>("message");
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    return $"上游错误：{detail}";
                }
            }
            catch (JsonException)
            {
                // 非 JSON 错误体，直接截断返回。
            }

            var text = payload.Trim();
            if (text.Length > 600)
            {
                text = text.Substring(0, 600) + "...";
            }

            return $"上游返回 HTTP {statusCode}：{text}";
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}

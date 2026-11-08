using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIHub.Proxy
{
    /// <summary>
    /// 用提供方的 API Key 调用 OpenAI 兼容的 <c>/models</c> 接口，拉取可用模型 ID。
    /// </summary>
    internal static class ModelCatalog
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        /// <summary>
        /// 拉取模型列表；失败时抛出带中文说明的异常（多条候选地址的错误会合并）。
        /// </summary>
        public static async Task<IReadOnlyList<string>> FetchAsync(ProviderSetting provider, CancellationToken cancellationToken)
        {
            var errors = new List<string>();

            foreach (var url in BuildCandidates(provider.BaseUrl))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var models = await FetchFromAsync(url, provider, cancellationToken).ConfigureAwait(false);
                    if (models.Count > 0)
                    {
                        return models;
                    }

                    errors.Add($"{url}：没有返回任何模型");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add($"{url}：{DescribeError(ex)}");
                }
            }

            throw new InvalidOperationException(errors.Count == 0
                ? "上游没有返回任何可用模型。"
                : string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal)));
        }

        /// <summary>
        /// .NET Framework 的 HttpRequestException 外层文案很空（“发送请求时出错”），取最内层的具体原因。
        /// </summary>
        private static string DescribeError(Exception ex)
        {
            var message = ex.Message;

            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(inner.Message))
                {
                    message = inner.Message;
                }
            }

            return string.IsNullOrWhiteSpace(message) ? ex.GetType().Name : message.Trim();
        }

        private static IEnumerable<string> BuildCandidates(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');

            if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - "/chat/completions".Length).TrimEnd('/');
            }

            if (trimmed.Length == 0)
            {
                yield break;
            }

            if (trimmed.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                yield return trimmed;
                yield break;
            }

            yield return trimmed + "/models";

            // DeepSeek、Perplexity 等 Base URL 不带 /v1，两种写法都试一次。
            if (!trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                yield return trimmed + "/v1/models";
            }
        }

        private static async Task<IReadOnlyList<string>> FetchFromAsync(string url, ProviderSetting provider, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "AIHub-VSIX/1.2");

                var apiKey = (provider.ApiKey ?? string.Empty).Trim();

                if (apiKey.Length > 0)
                {
                    if (provider.IsAnthropic)
                    {
                        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    }

                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                }

                if (provider.IsAnthropic)
                {
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }

                using (var response = await Http
                           .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                           .ConfigureAwait(false))
                {
                    var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            OpenAiCompatibleClient.ExtractErrorMessage(payload, (int)response.StatusCode));
                    }

                    return ParseModelIds(payload);
                }
            }
        }

        private static IReadOnlyList<string> ParseModelIds(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return Array.Empty<string>();
            }

            JToken root;
            try
            {
                root = JToken.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("上游返回了无法解析的响应：" + ex.Message);
            }

            var ids = new List<string>();

            foreach (var array in EnumerateArrays(root))
            {
                foreach (var item in array)
                {
                    var id = item.Type == JTokenType.String
                        ? item.Value<string>()
                        : item["id"]?.Value<string>()
                          ?? item["name"]?.Value<string>()
                          ?? item["model"]?.Value<string>();

                    var normalized = NormalizeId(id);
                    if (normalized != null)
                    {
                        ids.Add(normalized);
                    }
                }
            }

            return ids
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<JToken> EnumerateArrays(JToken root)
        {
            if (root.Type == JTokenType.Array)
            {
                yield return root;
                yield break;
            }

            foreach (var name in new[] { "data", "models", "result", "items" })
            {
                var token = root[name];
                if (token != null && token.Type == JTokenType.Array)
                {
                    yield return token;
                }
            }
        }

        private static string? NormalizeId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var value = id!.Trim();

            // Gemini 返回的 name 形如 models/gemini-2.5-pro。
            if (value.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("models/".Length).Trim();
            }

            return value.Length == 0 ? null : value;
        }
    }
}

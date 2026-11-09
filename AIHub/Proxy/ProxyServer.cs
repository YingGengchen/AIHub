using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIHub.Proxy
{
    /// <summary>
    /// 本地 Ollama 兼容代理：同时提供 Ollama（/api/*）与 OpenAI（/v1/*）协议，
    /// 将请求转发到配置中的 OpenAI 兼容云端大模型。
    /// </summary>
    internal sealed class ProxyServer : IDisposable
    {
        private const long MaxBodyBytes = 100L * 1024 * 1024;

        private static readonly string[] V1PassthroughFields =
        {
            "messages", "temperature", "top_p", "max_tokens", "stop", "tools", "tool_choice",
            "response_format", "frequency_penalty", "presence_penalty", "seed", "n", "logprobs", "top_logprobs",
        };

        private static readonly string[] V1StreamDeltaFields =
        {
            "role", "content", "tool_calls", "function_call", "reasoning_content",
        };

        private readonly object _gate = new object();
        private readonly ProxyLogger _logger;
        private readonly OpenAiCompatibleClient _client = new OpenAiCompatibleClient();

        private HttpListener? _listener;
        private CancellationTokenSource? _cancellation;
        private Task? _acceptLoop;
        private ProxyConfig _config = new ProxyConfig();

        public ProxyServer(ProxyLogger logger)
        {
            _logger = logger;
        }

        public bool IsRunning { get; private set; }

        public int Port => _config.Port;

        public event Action? StateChanged;

        public void ApplyConfig(ProxyConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Normalize();
        }

        public void Start(ProxyConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (_gate)
            {
                if (IsRunning)
                {
                    return;
                }

                _config = config;
                _config.Normalize();

                var listener = CreateAndStartListener(_config.Port);
                var cancellation = new CancellationTokenSource();

                _listener = listener;
                _cancellation = cancellation;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, cancellation.Token));
                IsRunning = true;
            }

            _logger.Info($"代理已启动：http://localhost:{_config.Port}（模型 {_config.Models.Count} 个，提供方 {_config.Providers.Count} 个）");
            RaiseStateChanged();
        }

        public void Stop()
        {
            HttpListener? listener;
            CancellationTokenSource? cancellation;
            Task? loop;

            lock (_gate)
            {
                if (!IsRunning)
                {
                    return;
                }

                listener = _listener;
                cancellation = _cancellation;
                loop = _acceptLoop;

                _listener = null;
                _cancellation = null;
                _acceptLoop = null;
                IsRunning = false;
            }

            try
            {
                cancellation?.Cancel();
            }
            catch (Exception)
            {
                // 忽略取消时的异常。
            }

            try
            {
                listener?.Stop();
                listener?.Close();
            }
            catch (Exception)
            {
                // 忽略关闭监听器时的异常。
            }

            try
            {
                loop?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // 忽略等待循环退出时的异常。
            }

            cancellation?.Dispose();
            _logger.Info("代理已停止。");
            RaiseStateChanged();
        }

        private static HttpListener CreateAndStartListener(int port)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");

            var hasLoopbackPrefix = false;
            try
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                hasLoopbackPrefix = true;
            }
            catch (Exception)
            {
                // 个别环境不允许注册 127.0.0.1 前缀，仅保留 localhost。
            }

            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException ex)
            {
                listener.Close();

                if (!hasLoopbackPrefix)
                {
                    throw BuildStartException(ex, port);
                }

                var fallback = new HttpListener();
                fallback.Prefixes.Add($"http://localhost:{port}/");

                try
                {
                    fallback.Start();
                    return fallback;
                }
                catch (Exception fallbackEx)
                {
                    fallback.Close();
                    throw BuildStartException(fallbackEx, port);
                }
            }
        }

        /// <summary>
        /// 端口冲突时先探测占用者：如果是另一个 AI Hub 实例，就抛专用异常让上层复用。
        /// </summary>
        private static Exception BuildStartException(Exception exception, int port)
        {
            if (IsPortInUseError(exception))
            {
                var host = ProbeHost(port);
                if (host != null)
                {
                    return new ProxyAlreadyHostedException(host);
                }
            }

            return new InvalidOperationException(DescribeListenerError(exception, port), exception);
        }

        /// <summary>
        /// 探测指定端口上是否已有 AI Hub 代理在运行；返回 null 表示没有（或是别的程序）。
        /// </summary>
        public static ProxyHostInfo? ProbeHost(int port, int timeoutMs = 700)
        {
            if (port <= 0 || port > 65535)
            {
                return null;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"http://localhost:{port}/health");
                request.Method = "GET";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Proxy = null;
                request.KeepAlive = false;
                request.AllowAutoRedirect = false;
                request.UserAgent = "AIHub-Probe";

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var body = reader.ReadToEnd();
                    var json = JObject.Parse(body);

                    var product = (string?)json["product"];
                    if (!string.Equals(product, "aihub", StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    return new ProxyHostInfo(
                        port,
                        json.Value<int?>("pid") ?? 0,
                        (string?)json["version"] ?? string.Empty);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsPortInUseError(Exception exception)
        {
            var listenerException = exception as HttpListenerException;
            var code = listenerException?.ErrorCode ?? 0;
            var message = exception.Message ?? string.Empty;

            return code == 183 || code == 32 || code == 10048
                || message.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("正在使用", StringComparison.Ordinal) >= 0
                || message.IndexOf("in use", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DescribeListenerError(Exception exception, int port)
        {
            var listenerException = exception as HttpListenerException;
            var code = listenerException?.ErrorCode ?? 0;
            var message = exception.Message ?? string.Empty;

            if (IsPortInUseError(exception))
            {
                return $"端口 {port} 已被占用。请关闭占用该端口的程序（例如 Ollama 或旧代理进程），或在本面板中改用其它端口。";
            }

            if (code == 5)
            {
                return $"绑定端口 {port} 被拒绝。可在管理员命令行执行：netsh http add urlacl url=http://localhost:{port}/ user=Everyone";
            }

            return $"启动本地代理失败（端口 {port}）：{message}";
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error("接受连接失败：" + ex.Message);
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(context, cancellationToken));
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            try
            {
                await DispatchAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (ProxyException ex)
            {
                await TryWriteErrorAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
            }
            catch (UpstreamException ex)
            {
                _logger.Error($"上游请求失败（HTTP {ex.StatusCode}）：{ex.Message}");
                await TryWriteErrorAsync(context, 502, ex.Message).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 代理解除或客户端断开。
            }
            catch (HttpListenerException)
            {
                // 客户端断开。
            }
            catch (IOException)
            {
                // 客户端断开。
            }
            catch (Exception ex)
            {
                _logger.Error("处理请求失败：" + ex);
                await TryWriteErrorAsync(context, 500, ex.Message).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                    // 忽略关闭响应时的异常。
                }
            }
        }

        private async Task DispatchAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var path = (context.Request.Url?.AbsolutePath ?? "/").ToLowerInvariant();
            if (path.Length > 1)
            {
                path = path.TrimEnd('/');
            }

            switch (path)
            {
                case "":
                case "/":
                    await WriteTextAsync(context, 200, "Ollama is running").ConfigureAwait(false);
                    return;

                case "/api/version":
                    await WriteJsonAsync(context, 200, new JObject { ["version"] = "0.6.5" }).ConfigureAwait(false);
                    return;

                case "/api/tags":
                    await WriteJsonAsync(context, 200, BuildTags()).ConfigureAwait(false);
                    return;

                case "/api/ps":
                    await WriteJsonAsync(context, 200, new JObject { ["models"] = new JArray() }).ConfigureAwait(false);
                    return;

                case "/api/show":
                    await WriteJsonAsync(context, 200, BuildShow(await ReadBodyAsync(context).ConfigureAwait(false))).ConfigureAwait(false);
                    return;

                case "/api/chat":
                    await HandleOllamaChatAsync(context, cancellationToken, generate: false).ConfigureAwait(false);
                    return;

                case "/api/generate":
                    await HandleOllamaChatAsync(context, cancellationToken, generate: true).ConfigureAwait(false);
                    return;

                case "/api/pull":
                    await HandlePullAsync(context).ConfigureAwait(false);
                    return;

                case "/api/delete":
                case "/api/copy":
                    await WriteJsonAsync(context, 200, new JObject()).ConfigureAwait(false);
                    return;

                case "/api/push":
                case "/api/create":
                    await WriteJsonAsync(context, 501, new JObject { ["error"] = "云端代理不支持该操作。" }).ConfigureAwait(false);
                    return;

                case "/api/embeddings":
                    await HandleLegacyEmbeddingsAsync(context, cancellationToken).ConfigureAwait(false);
                    return;

                case "/api/embed":
                    await HandleEmbeddingsAsync(context, cancellationToken).ConfigureAwait(false);
                    return;

                case "/v1/chat/completions":
                    await HandleOpenAiChatAsync(context, cancellationToken).ConfigureAwait(false);
                    return;

                case "/v1/models":
                    await WriteJsonAsync(context, 200, BuildOpenAiModels()).ConfigureAwait(false);
                    return;

                case "/health":
                    await WriteJsonAsync(context, 200, BuildHealth()).ConfigureAwait(false);
                    return;

                case "/health/providers":
                    await WriteJsonAsync(context, 200, BuildProvidersHealth()).ConfigureAwait(false);
                    return;

                default:
                    await WriteJsonAsync(context, 404, new JObject { ["error"] = $"未知接口：{path}" }).ConfigureAwait(false);
                    return;
            }
        }

        private async Task HandleOllamaChatAsync(HttpListenerContext context, CancellationToken cancellationToken, bool generate)
        {
            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var model = body.Value<string>("model") ?? _config.Models.FirstOrDefault() ?? "deepseek/deepseek-v4-flash";
            var stream = body["stream"]?.Type != JTokenType.Boolean || body.Value<bool>("stream");

            var messages = generate ? BuildGenerateMessages(body) : body["messages"] as JArray ?? new JArray();
            if (messages.Count == 0)
            {
                throw new ProxyException(400, "messages 不能为空。");
            }

            var resolved = ResolveModel(model);
            var tools = body["tools"] as JArray;
            var options = body["options"] as JObject;

            var upstreamBody = BuildUpstreamBody(
                resolved,
                MessageConverter.ToOpenAiMessages(messages),
                stream,
                tools,
                body["format"],
                options);

            _logger.Info($"REQ {(generate ? "/api/generate" : "/api/chat")} model={model} stream={stream} messages={messages.Count} tools={tools?.Count ?? 0}");

            if (stream)
            {
                await StreamOllamaAsync(context, resolved, upstreamBody, generate, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await NonStreamOllamaAsync(context, resolved, upstreamBody, generate, messages, cancellationToken).ConfigureAwait(false);
            }
        }

        private static JArray BuildGenerateMessages(JObject body)
        {
            var messages = new JArray();
            var system = body.Value<string>("system");
            if (!string.IsNullOrEmpty(system))
            {
                messages.Add(new JObject { ["role"] = "system", ["content"] = system });
            }

            var userMessage = new JObject
            {
                ["role"] = "user",
                ["content"] = body.Value<string>("prompt") ?? string.Empty,
            };

            if (body["images"] is JArray images && images.Count > 0)
            {
                userMessage["images"] = images.DeepClone();
            }

            messages.Add(userMessage);
            return messages;
        }

        private static JObject BuildUpstreamBody(
            ResolvedModel resolved,
            JArray messages,
            bool stream,
            JArray? tools,
            JToken? format,
            JObject? options)
        {
            var body = new JObject
            {
                ["model"] = resolved.ModelId,
                ["messages"] = messages,
                ["stream"] = stream,
            };

            if (options != null)
            {
                if (options["temperature"] != null)
                {
                    body["temperature"] = options["temperature"]!.DeepClone();
                }

                if (options["top_p"] != null)
                {
                    body["top_p"] = options["top_p"]!.DeepClone();
                }

                if (options["frequency_penalty"] != null)
                {
                    body["frequency_penalty"] = options["frequency_penalty"]!.DeepClone();
                }

                if (options["presence_penalty"] != null)
                {
                    body["presence_penalty"] = options["presence_penalty"]!.DeepClone();
                }

                if (options["num_predict"] != null)
                {
                    body["max_tokens"] = options["num_predict"]!.DeepClone();
                }

                if (options["max_tokens"] != null)
                {
                    body["max_tokens"] = options["max_tokens"]!.DeepClone();
                }

                if (options["seed"] != null)
                {
                    body["seed"] = options["seed"]!.DeepClone();
                }

                if (options["stop"] != null)
                {
                    body["stop"] = options["stop"]!.DeepClone();
                }
            }

            var normalizedTools = MessageConverter.NormalizeTools(tools);
            if (normalizedTools.Count > 0)
            {
                body["tools"] = normalizedTools;
            }

            // Moonshot 的 kimi-k2 系列默认会输出思考内容，会把正文挤空，这里显式关掉。
            if (string.Equals(resolved.ProviderName, "kimi", StringComparison.OrdinalIgnoreCase)
                && resolved.ModelId.IndexOf("k2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                body["thinking"] = new JObject { ["type"] = "disabled" };
            }

            if (format != null)
            {
                if (format.Type == JTokenType.String && string.Equals(format.Value<string>(), "json", StringComparison.OrdinalIgnoreCase))
                {
                    body["response_format"] = new JObject { ["type"] = "json_object" };
                }
                else if (format.Type == JTokenType.Object)
                {
                    body["response_format"] = new JObject
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new JObject
                        {
                            ["name"] = "response",
                            ["schema"] = format.DeepClone(),
                        },
                    };
                }
            }

            return body;
        }

        private async Task StreamOllamaAsync(
            HttpListenerContext context,
            ResolvedModel resolved,
            JObject upstreamBody,
            bool generate,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                HttpResponseMessage response;

                try
                {
                    response = await OpenChatStreamWithRetryAsync(resolved, upstreamBody, linked.Token).ConfigureAwait(false);
                }
                catch (UpstreamException ex)
                {
                    throw new UpstreamException(ex.StatusCode, ex.Message, ex.Retriable);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    throw new UpstreamException(502, "无法连接上游服务：" + ex.Message, false);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorPayload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        throw new UpstreamException(
                            (int)response.StatusCode,
                            OpenAiCompatibleClient.ExtractErrorMessage(errorPayload, (int)response.StatusCode),
                            false);
                    }

                    var writer = new StreamingResponse(context.Response, "application/x-ndjson");
                    var toolCalls = new ToolCallAccumulator();
                    long evalCount = 0;
                    long promptTokens = 0;
                    string? finishReason = null;

                    try
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string? line;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                if (linked.IsCancellationRequested)
                                {
                                    break;
                                }

                                if (line.Length == 0 || !line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                var payload = line.Substring(5).Trim();
                                if (payload.Length == 0)
                                {
                                    continue;
                                }

                                if (payload == "[DONE]")
                                {
                                    break;
                                }

                                JObject chunk;
                                try
                                {
                                    chunk = JObject.Parse(payload);
                                }
                                catch (JsonException)
                                {
                                    continue;
                                }

                                if (chunk["usage"] is JObject usage)
                                {
                                    promptTokens = Math.Max(promptTokens, usage.Value<long?>("prompt_tokens") ?? 0);
                                }

                                if (chunk["choices"] is not JArray choices || choices.Count == 0 || choices[0] is not JObject choice)
                                {
                                    continue;
                                }

                                if (choice["delta"] is JObject delta)
                                {
                                    var reasoning = delta["reasoning_content"] ?? delta["reasoning"];
                                    var reasoningText = reasoning?.Type == JTokenType.String ? reasoning.Value<string>() : null;

                                    if (!string.IsNullOrEmpty(reasoningText))
                                    {
                                        await writer.WriteNdjsonAsync(CreateOllamaDelta(resolved.ModelName, generate, null, reasoningText))
                                            .ConfigureAwait(false);
                                    }

                                    var content = delta.Value<string>("content");
                                    if (!string.IsNullOrEmpty(content))
                                    {
                                        evalCount += EstimateTokens(content);
                                        await writer.WriteNdjsonAsync(CreateOllamaDelta(resolved.ModelName, generate, content, null))
                                            .ConfigureAwait(false);
                                    }

                                    if (delta["tool_calls"] is JArray deltaToolCalls)
                                    {
                                        toolCalls.Add(deltaToolCalls);
                                    }
                                }

                                var reason = choice.Value<string>("finish_reason");
                                if (!string.IsNullOrEmpty(reason))
                                {
                                    finishReason = reason;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpListenerException || ex is ObjectDisposedException)
                    {
                        linked.Cancel();
                        _logger.Warn("客户端连接已断开，已停止转发。");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("流式转发失败：" + ex.Message);

                        try
                        {
                            await writer.WriteNdjsonAsync(CreateOllamaErrorChunk(resolved.ModelName, generate, ex.Message))
                                .ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // 客户端可能已断开。
                        }

                        return;
                    }

                    var ollamaToolCalls = MessageConverter.ToOllamaToolCalls(toolCalls.ToOpenAiToolCalls());
                    if (!generate && ollamaToolCalls.Count > 0)
                    {
                        await writer.WriteNdjsonAsync(new JObject
                        {
                            ["model"] = resolved.ModelName,
                            ["created_at"] = IsoNow(),
                            ["message"] = new JObject
                            {
                                ["role"] = "assistant",
                                ["content"] = string.Empty,
                                ["tool_calls"] = ollamaToolCalls,
                            },
                            ["done"] = false,
                        }).ConfigureAwait(false);
                    }

                    var duration = ToNanoseconds(stopwatch);
                    var done = new JObject
                    {
                        ["model"] = resolved.ModelName,
                        ["created_at"] = IsoNow(),
                        ["done"] = true,
                        ["done_reason"] = "stop",
                        ["total_duration"] = duration,
                        ["load_duration"] = 0,
                        ["prompt_eval_count"] = promptTokens,
                        ["prompt_eval_duration"] = 0,
                        ["eval_count"] = evalCount,
                        ["eval_duration"] = duration,
                    };

                    if (generate)
                    {
                        done["response"] = string.Empty;
                        done["context"] = new JArray();
                    }
                    else
                    {
                        done["message"] = new JObject
                        {
                            ["role"] = "assistant",
                            ["content"] = string.Empty,
                        };
                    }

                    await writer.WriteNdjsonAsync(done).ConfigureAwait(false);

                    _logger.Info(
                        $"RES {(generate ? "/api/generate" : "/api/chat")} status=200 stream=true finish={finishReason ?? "stop"} " +
                        $"eval={evalCount} elapsed={stopwatch.ElapsedMilliseconds}ms");
                }
            }
        }

        private async Task NonStreamOllamaAsync(
            HttpListenerContext context,
            ResolvedModel resolved,
            JObject upstreamBody,
            bool generate,
            JArray messages,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var upstream = await SendChatJsonWithRetryAsync(resolved, upstreamBody, cancellationToken).ConfigureAwait(false);

            var choice = (upstream["choices"] as JArray)?.OfType<JObject>().FirstOrDefault();
            var message = choice?["message"] as JObject;
            var content = message?.Value<string>("content") ?? string.Empty;
            var reasoning = message?.Value<string>("reasoning_content");
            var toolCalls = MessageConverter.ToOllamaToolCalls(message?["tool_calls"] as JArray);
            var usage = upstream["usage"] as JObject;
            var promptTokens = usage?.Value<long?>("prompt_tokens")
                ?? messages.Sum(m => EstimateTokens(m.Value<string>("content") ?? string.Empty));
            var evalCount = usage?.Value<long?>("completion_tokens") ?? EstimateTokens(content);
            var duration = ToNanoseconds(stopwatch);

            JObject result;

            if (generate)
            {
                result = new JObject
                {
                    ["model"] = resolved.ModelName,
                    ["created_at"] = IsoNow(),
                    ["response"] = content,
                    ["done"] = true,
                    ["done_reason"] = "stop",
                    ["context"] = new JArray(),
                    ["total_duration"] = duration,
                    ["load_duration"] = 0,
                    ["prompt_eval_count"] = promptTokens,
                    ["prompt_eval_duration"] = 0,
                    ["eval_count"] = evalCount,
                    ["eval_duration"] = duration,
                };
            }
            else
            {
                var ollamaMessage = new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = content,
                };

                if (!string.IsNullOrEmpty(reasoning))
                {
                    ollamaMessage["thinking"] = reasoning;
                }

                if (toolCalls.Count > 0)
                {
                    ollamaMessage["tool_calls"] = toolCalls;
                }

                result = new JObject
                {
                    ["model"] = resolved.ModelName,
                    ["created_at"] = IsoNow(),
                    ["message"] = ollamaMessage,
                    ["done"] = true,
                    ["done_reason"] = "stop",
                    ["total_duration"] = duration,
                    ["load_duration"] = 0,
                    ["prompt_eval_count"] = promptTokens,
                    ["prompt_eval_duration"] = 0,
                    ["eval_count"] = evalCount,
                    ["eval_duration"] = duration,
                };
            }

            await WriteJsonAsync(context, 200, result).ConfigureAwait(false);
            _logger.Info(
                $"RES {(generate ? "/api/generate" : "/api/chat")} status=200 stream=false tools={toolCalls.Count} " +
                $"eval={evalCount} elapsed={stopwatch.ElapsedMilliseconds}ms");
        }

        private async Task HandleOpenAiChatAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            JObject upstreamBody;

            try
            {
                var body = await ReadBodyAsync(context).ConfigureAwait(false);
                var model = body.Value<string>("model") ?? _config.Models.FirstOrDefault() ?? "deepseek/deepseek-v4-flash";
                var stream = body.Value<bool?>("stream") ?? false;
                var messages = body["messages"] as JArray;

                if (messages == null || messages.Count == 0)
                {
                    throw new ProxyException(400, "messages 不能为空。");
                }

                var resolved = ResolveModel(model);

                upstreamBody = new JObject
                {
                    ["model"] = resolved.ModelId,
                    ["stream"] = stream,
                };

                foreach (var field in V1PassthroughFields)
                {
                    if (body[field] != null)
                    {
                        upstreamBody[field] = body[field]!.DeepClone();
                    }
                }

                if (body["max_completion_tokens"] != null && upstreamBody["max_tokens"] == null)
                {
                    upstreamBody["max_tokens"] = body["max_completion_tokens"]!.DeepClone();
                }

                if (string.Equals(resolved.ProviderName, "kimi", StringComparison.OrdinalIgnoreCase)
                    && resolved.ModelId.IndexOf("k2", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    upstreamBody["thinking"] = new JObject { ["type"] = "disabled" };
                }

                _logger.Info($"REQ /v1/chat/completions model={model} stream={stream} messages={messages.Count}");

                if (stream)
                {
                    await StreamOpenAiAsync(context, resolved, upstreamBody, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var result = await SendChatJsonWithRetryAsync(resolved, upstreamBody, cancellationToken).ConfigureAwait(false);
                result["model"] = model;
                result["object"] = "chat.completion";
                if (result["id"] == null)
                {
                    result["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                }

                if (result["created"] == null)
                {
                    result["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                await WriteJsonAsync(context, 200, result).ConfigureAwait(false);
                _logger.Info("RES /v1/chat/completions status=200 stream=false");
            }
            catch (ProxyException ex)
            {
                await TryWriteOpenAiErrorAsync(context, ex.StatusCode, ex.Message).ConfigureAwait(false);
            }
            catch (UpstreamException ex)
            {
                _logger.Error($"上游请求失败（HTTP {ex.StatusCode}）：{ex.Message}");
                await TryWriteOpenAiErrorAsync(context, 502, ex.Message).ConfigureAwait(false);
            }
        }

        private async Task StreamOpenAiAsync(
            HttpListenerContext context,
            ResolvedModel resolved,
            JObject upstreamBody,
            CancellationToken cancellationToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                HttpResponseMessage response;

                try
                {
                    response = await OpenChatStreamWithRetryAsync(resolved, upstreamBody, linked.Token).ConfigureAwait(false);
                }
                catch (UpstreamException ex)
                {
                    await TryWriteOpenAiErrorAsync(context, 502, ex.Message).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    await TryWriteOpenAiErrorAsync(context, 502, "无法连接上游服务：" + ex.Message).ConfigureAwait(false);
                    return;
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorPayload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)response.StatusCode;
                        _logger.Error($"上游请求失败（HTTP {status}）：{OpenAiCompatibleClient.ExtractErrorMessage(errorPayload, status)}");
                        await TryWriteOpenAiErrorAsync(context, 502, OpenAiCompatibleClient.ExtractErrorMessage(errorPayload, status))
                            .ConfigureAwait(false);
                        return;
                    }

                    var writer = new StreamingResponse(context.Response, "text/event-stream");
                    var chatId = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long evalCount = 0;

                    try
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string? line;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                if (linked.IsCancellationRequested)
                                {
                                    break;
                                }

                                if (!line.StartsWith("data:", StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                var payload = line.Substring(5).Trim();
                                if (payload.Length == 0)
                                {
                                    continue;
                                }

                                if (payload == "[DONE]")
                                {
                                    break;
                                }

                                JObject chunk;
                                try
                                {
                                    chunk = JObject.Parse(payload);
                                }
                                catch (JsonException)
                                {
                                    continue;
                                }

                                var output = new JObject
                                {
                                    ["id"] = chatId,
                                    ["object"] = "chat.completion.chunk",
                                    ["created"] = created,
                                    ["model"] = resolved.ModelName,
                                };

                                var outputChoices = new JArray();
                                if (chunk["choices"] is JArray choices)
                                {
                                    foreach (var choice in choices.OfType<JObject>())
                                    {
                                        var delta = new JObject();
                                        if (choice["delta"] is JObject sourceDelta)
                                        {
                                            foreach (var field in V1StreamDeltaFields)
                                            {
                                                if (sourceDelta[field] != null)
                                                {
                                                    delta[field] = sourceDelta[field]!.DeepClone();
                                                }
                                            }

                                            var text = sourceDelta.Value<string>("content");
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                evalCount += EstimateTokens(text);
                                            }
                                        }

                                        outputChoices.Add(new JObject
                                        {
                                            ["index"] = choice["index"] ?? 0,
                                            ["delta"] = delta,
                                            ["finish_reason"] = choice["finish_reason"] ?? JValue.CreateNull(),
                                        });
                                    }
                                }

                                output["choices"] = outputChoices;

                                if (chunk["usage"] != null)
                                {
                                    output["usage"] = chunk["usage"]!.DeepClone();
                                }

                                await writer.WriteSseAsync(output).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpListenerException || ex is ObjectDisposedException)
                    {
                        linked.Cancel();
                        _logger.Warn("客户端连接已断开，已停止转发。");
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("流式转发失败：" + ex.Message);

                        try
                        {
                            await writer.WriteSseAsync(new JObject
                            {
                                ["error"] = new JObject
                                {
                                    ["message"] = ex.Message,
                                    ["type"] = "server_error",
                                },
                            }).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // 客户端可能已断开。
                        }

                        return;
                    }

                    await writer.WriteSseDoneAsync().ConfigureAwait(false);
                    _logger.Info($"RES /v1/chat/completions status=200 stream=true eval={evalCount}");
                }
            }
        }

        private async Task HandlePullAsync(HttpListenerContext context)
        {
            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var model = body.Value<string>("model") ?? "unknown";
            var stream = body["stream"]?.Type != JTokenType.Boolean || body.Value<bool>("stream");

            if (!stream)
            {
                await WriteJsonAsync(context, 200, new JObject { ["status"] = "success" }).ConfigureAwait(false);
                return;
            }

            var writer = new StreamingResponse(context.Response, "application/x-ndjson");
            await writer.WriteNdjsonAsync(new JObject { ["status"] = $"pulling manifest for {model}" }).ConfigureAwait(false);
            await writer.WriteNdjsonAsync(new JObject { ["status"] = "verifying sha256 digest" }).ConfigureAwait(false);
            await writer.WriteNdjsonAsync(new JObject { ["status"] = "success" }).ConfigureAwait(false);
        }

        private JObject BuildTags()
        {
            var models = new JArray();

            foreach (var name in _config.Models)
            {
                var family = name.IndexOf('/') > 0 ? name.Substring(0, name.IndexOf('/')) : _config.DefaultProvider;

                models.Add(new JObject
                {
                    ["name"] = name,
                    ["model"] = name,
                    ["modified_at"] = "2025-01-01T00:00:00Z",
                    ["size"] = 0,
                    ["digest"] = BuildDigest(name),
                    ["details"] = new JObject
                    {
                        ["parent_model"] = string.Empty,
                        ["format"] = "cloud",
                        ["family"] = family,
                        ["families"] = new JArray(family),
                        ["parameter_size"] = "cloud",
                        ["quantization_level"] = "none",
                    },
                    ["model_info"] = new JObject
                    {
                        ["general.architecture"] = family,
                        ["general.basename"] = name,
                        ["llama.context_length"] = _config.ContextLength,
                    },
                });
            }

            return new JObject { ["models"] = models };
        }

        private JObject BuildShow(JObject body)
        {
            var model = body.Value<string>("model") ?? string.Empty;
            var prefix = model.IndexOf('/') > 0
                ? model.Substring(0, model.IndexOf('/')).ToLowerInvariant()
                : _config.DefaultProvider;

            var known = _config.Providers.ContainsKey(prefix)
                || _config.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));

            if (!known)
            {
                throw new ProxyException(404, $"model '{model}' not found");
            }

            return new JObject
            {
                ["model"] = model,
                ["modelfile"] = $"# Modelfile for {model}\nFROM {model}\n",
                ["parameters"] = "temperature 0.8\nstop \"<|end|>\"",
                ["template"] = "{{ .System }}\n{{ .Prompt }}",
                ["details"] = new JObject
                {
                    ["parent_model"] = string.Empty,
                    ["format"] = "cloud",
                    ["family"] = prefix,
                    ["families"] = new JArray(prefix),
                    ["parameter_size"] = "cloud",
                    ["quantization_level"] = "none",
                },
                ["model_info"] = new JObject
                {
                    ["general.architecture"] = prefix,
                    ["general.basename"] = model,
                    ["llama.context_length"] = _config.ContextLength,
                },
                ["capabilities"] = new JArray("completion", "tools"),
            };
        }

        private JObject BuildOpenAiModels()
        {
            var data = new JArray();

            foreach (var name in _config.Models)
            {
                var owner = name.IndexOf('/') > 0 ? name.Substring(0, name.IndexOf('/')) : _config.DefaultProvider;
                data.Add(new JObject
                {
                    ["id"] = name,
                    ["object"] = "model",
                    ["created"] = 1700000000,
                    ["owned_by"] = owner,
                });
            }

            return new JObject
            {
                ["object"] = "list",
                ["data"] = data,
            };
        }

        private JObject BuildHealth()
        {
            return new JObject
            {
                ["status"] = "ok",
                ["product"] = "aihub",
                ["version"] = "1.2",
                ["pid"] = Process.GetCurrentProcess().Id,
                ["port"] = _config.Port,
                ["models_registered"] = _config.Models.Count,
                ["providers"] = new JArray(_config.Providers.Keys.ToArray<object>()),
            };
        }

        private JObject BuildProvidersHealth()
        {
            var providers = new JArray();

            foreach (var pair in _config.Providers)
            {
                var provider = pair.Value;
                if (provider == null)
                {
                    continue;
                }

                providers.Add(new JObject
                {
                    ["name"] = pair.Key,
                    ["base_url"] = provider.BaseUrl ?? string.Empty,
                    ["protocol"] = string.IsNullOrWhiteSpace(provider.Protocol)
                        ? ProviderSetting.ProtocolOpenAi
                        : provider.Protocol,
                    ["has_api_key"] = !string.IsNullOrWhiteSpace(provider.ApiKey),
                    ["retries"] = provider.Retries,
                    ["fallback"] = provider.Fallback ?? string.Empty,
                });
            }

            return new JObject
            {
                ["status"] = "ok",
                ["count"] = providers.Count,
                ["providers"] = providers,
            };
        }

        private async Task HandleEmbeddingsAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var resolved = ResolveModel(body.Value<string>("model") ?? string.Empty);
            var input = body["input"];
            if (input == null)
            {
                throw new ProxyException(400, "缺少 input 字段。");
            }

            var upstreamBody = new JObject
            {
                ["model"] = resolved.ModelId,
                ["input"] = input.DeepClone(),
            };

            _logger.Info($"REQ /api/embed model={resolved.ModelName}");

            var upstream = await SendEmbeddingsWithRetryAsync(resolved, upstreamBody, cancellationToken).ConfigureAwait(false);
            var vectors = new JArray();
            var embeddings = upstream["data"] as JArray;

            if (embeddings != null)
            {
                foreach (var item in embeddings.OfType<JObject>())
                {
                    vectors.Add(item["embedding"]?.DeepClone() ?? new JArray());
                }
            }

            await WriteJsonAsync(context, 200, new JObject
            {
                ["model"] = resolved.ModelName,
                ["embeddings"] = vectors,
            }).ConfigureAwait(false);
        }

        private async Task HandleLegacyEmbeddingsAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var body = await ReadBodyAsync(context).ConfigureAwait(false);
            var resolved = ResolveModel(body.Value<string>("model") ?? string.Empty);
            var prompt = body.Value<string>("prompt");
            if (prompt == null)
            {
                throw new ProxyException(400, "缺少 prompt 字段。");
            }

            var upstreamBody = new JObject
            {
                ["model"] = resolved.ModelId,
                ["input"] = prompt,
            };

            _logger.Info($"REQ /api/embeddings model={resolved.ModelName}");

            var upstream = await SendEmbeddingsWithRetryAsync(resolved, upstreamBody, cancellationToken).ConfigureAwait(false);
            var embedding = (upstream["data"] as JArray)?.OfType<JObject>().FirstOrDefault()?["embedding"]?.DeepClone();

            await WriteJsonAsync(context, 200, new JObject
            {
                ["embedding"] = embedding ?? new JArray(),
            }).ConfigureAwait(false);
        }

        /// <summary>非流式聊天：按提供方重试次数退避，耗尽后尝试备用模型。</summary>
        private async Task<JObject> SendChatJsonWithRetryAsync(ResolvedModel resolved, JObject body, CancellationToken cancellationToken)
        {
            return await RetryPolicy.ExecuteAsync(
                resolved,
                (attemptResolved, token) => _client.SendChatJsonAsync(attemptResolved, WithModel(body, attemptResolved), token),
                _logger,
                cancellationToken,
                ResolveModel).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> OpenChatStreamWithRetryAsync(ResolvedModel resolved, JObject body, CancellationToken cancellationToken)
        {
            return await RetryPolicy.ExecuteAsync(
                resolved,
                (attemptResolved, token) => _client.SendChatStreamAsync(attemptResolved, WithModel(body, attemptResolved), token),
                _logger,
                cancellationToken,
                ResolveModel).ConfigureAwait(false);
        }

        private async Task<JObject> SendEmbeddingsWithRetryAsync(ResolvedModel resolved, JObject body, CancellationToken cancellationToken)
        {
            return await RetryPolicy.ExecuteAsync(
                resolved,
                (attemptResolved, token) => _client.SendEmbeddingsJsonAsync(attemptResolved.Provider, WithModel(body, attemptResolved), token),
                _logger,
                cancellationToken,
                ResolveModel).ConfigureAwait(false);
        }

        private static JObject WithModel(JObject body, ResolvedModel resolved)
        {
            var copy = (JObject)body.DeepClone();
            copy["model"] = resolved.ModelId;
            return copy;
        }

        private ResolvedModel ResolveModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ProxyException(400, "缺少 model 字段。");
            }

            string providerName;
            string modelId;
            var slash = model.IndexOf('/');

            if (slash > 0)
            {
                providerName = model.Substring(0, slash).ToLowerInvariant();
                modelId = model.Substring(slash + 1);
            }
            else
            {
                providerName = (_config.DefaultProvider ?? "deepseek").ToLowerInvariant();
                modelId = model;
            }

            if (!_config.Providers.TryGetValue(providerName, out var provider) || provider == null)
            {
                throw new ProxyException(
                    400,
                    $"未找到提供方 \"{providerName}\"。请检查模型名是否为 \"提供方/模型\" 格式，并在 AI Hub 代理面板中添加该提供方。");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                throw new ProxyException(400, $"提供方 \"{providerName}\" 未配置 Base URL。");
            }

            if (string.IsNullOrWhiteSpace(provider.ApiKey) && !IsLocalEndpoint(provider.BaseUrl))
            {
                throw new ProxyException(
                    400,
                    $"提供方 \"{providerName}\" 尚未配置 API Key。请在「工具 → AI Hub 代理设置」中填写后重试。");
            }

            return new ResolvedModel(model, providerName, modelId, provider);
        }

        private static bool IsLocalEndpoint(string baseUrl)
        {
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.IsLoopback
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<JObject> ReadBodyAsync(HttpListenerContext context)
        {
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[81920];
                int read;

                while ((read = await context.Request.InputStream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBodyBytes)
                    {
                        throw new ProxyException(413, "请求体过大。");
                    }

                    #pragma warning disable VSTHRD103 // MemoryStream 同步写入不会阻塞
                                        buffer.Write(chunk, 0, read);
                    #pragma warning restore VSTHRD103
                                    }

                                    var text = Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new JObject();
                }

                try
                {
                    return JObject.Parse(text);
                }
                catch (JsonException)
                {
                    throw new ProxyException(400, "请求体不是有效的 JSON。");
                }
            }
        }

        private static async Task WriteJsonAsync(HttpListenerContext context, int statusCode, JToken payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
            var response = context.Response;

            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;

            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static async Task WriteTextAsync(HttpListenerContext context, int statusCode, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var response = context.Response;

            response.StatusCode = statusCode;
            response.ContentType = "text/plain";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;

            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static async Task TryWriteErrorAsync(HttpListenerContext context, int statusCode, string message)
        {
            try
            {
                await WriteJsonAsync(context, statusCode, new JObject { ["error"] = message }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 响应可能已开始写入，忽略。
            }
        }

        private static async Task TryWriteOpenAiErrorAsync(HttpListenerContext context, int statusCode, string message)
        {
            try
            {
                await WriteJsonAsync(context, statusCode, new JObject
                {
                    ["error"] = new JObject
                    {
                        ["message"] = message,
                        ["type"] = statusCode >= 500 ? "server_error" : "invalid_request_error",
                    },
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 响应可能已开始写入，忽略。
            }
        }

        private static JObject CreateOllamaDelta(string model, bool generate, string? content, string? thinking)
        {
            if (generate)
            {
                return new JObject
                {
                    ["model"] = model,
                    ["created_at"] = IsoNow(),
                    ["response"] = content ?? string.Empty,
                    ["done"] = false,
                };
            }

            var message = new JObject
            {
                ["role"] = "assistant",
                ["content"] = content ?? string.Empty,
            };

            if (!string.IsNullOrEmpty(thinking))
            {
                message["thinking"] = thinking;
            }

            return new JObject
            {
                ["model"] = model,
                ["created_at"] = IsoNow(),
                ["message"] = message,
                ["done"] = false,
            };
        }

        private static JObject CreateOllamaErrorChunk(string model, bool generate, string message)
        {
            var chunk = new JObject
            {
                ["model"] = model,
                ["created_at"] = IsoNow(),
                ["done"] = true,
                ["done_reason"] = "error",
                ["error"] = message,
            };

            if (generate)
            {
                chunk["response"] = string.Empty;
            }
            else
            {
                chunk["message"] = new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.Empty,
                };
            }

            return chunk;
        }

        private static string BuildDigest(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return "sha256:" + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static long ToNanoseconds(Stopwatch stopwatch)
        {
            return (long)(stopwatch.Elapsed.TotalMilliseconds * 1_000_000.0);
        }

        private static string IsoNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        private static long EstimateTokens(string? text)
        {
            if (text == null || text.Length == 0)
            {
                return 0;
            }

            return Math.Max(1, (text.Length + 3) / 4);
        }

        private void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception)
            {
                // 忽略订阅者异常。
            }
        }

        public void Dispose()
        {
            Stop();
            _client.Dispose();
        }
    }

    /// <summary>
    /// 以 chunked 方式写出流式响应，每写一条立即 flush，确保客户端能实时看到增量。
    /// </summary>
    internal sealed class StreamingResponse
    {
        private readonly Stream _stream;

        public StreamingResponse(HttpListenerResponse response, string contentType, int statusCode = 200)
        {
            response.StatusCode = statusCode;
            response.ContentType = contentType;
            response.ContentEncoding = Encoding.UTF8;
            response.SendChunked = true;
            response.KeepAlive = false;
            response.Headers["Cache-Control"] = "no-cache";

            _stream = response.OutputStream;
        }

        public Task WriteNdjsonAsync(JToken payload)
        {
            return WriteAsync(payload.ToString(Formatting.None) + "\n");
        }

        public Task WriteSseAsync(JToken payload)
        {
            return WriteAsync("data: " + payload.ToString(Formatting.None) + "\n\n");
        }

        public Task WriteSseDoneAsync()
        {
            return WriteAsync("data: [DONE]\n\n");
        }

        public async Task WriteAsync(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await _stream.FlushAsync().ConfigureAwait(false);
        }
    }
}

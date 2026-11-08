using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIHub.Proxy
{
    /// <summary>
    /// OpenAI 兼容请求/响应 与 Anthropic Messages API 之间的转换。
    /// 代理内部统一用 OpenAI 形状，只有真正发往上游时才转换，因此转发链路本身无需区分协议。
    /// </summary>
    internal static class AnthropicProtocol
    {
        private const int DefaultMaxTokens = 8192;

        public static string BuildEndpoint(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (trimmed.Length == 0)
            {
                throw new ProxyException(400, "未配置提供方的 Base URL。");
            }

            if (trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed + "/messages";
            }

            return trimmed + "/v1/messages";
        }

        public static JObject BuildRequest(JObject openAiBody)
        {
            var request = new JObject
            {
                ["model"] = openAiBody["model"]?.DeepClone() ?? JValue.CreateString(string.Empty),
                ["max_tokens"] = openAiBody["max_tokens"] ?? openAiBody["max_completion_tokens"] ?? new JValue(DefaultMaxTokens),
            };

            var systemParts = new List<string>();
            var messages = new JArray();

            var sourceMessages = openAiBody["messages"] as JArray ?? new JArray();
            foreach (var raw in sourceMessages.OfType<JObject>())
            {
                var role = raw.Value<string>("role") ?? "user";

                if (role == "system" || role == "developer")
                {
                    var text = FlattenText(raw["content"]);
                    if (text.Length > 0)
                    {
                        systemParts.Add(text);
                    }

                    continue;
                }

                if (role == "tool" || role == "function")
                {
                    var toolResult = new JObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = raw.Value<string>("tool_call_id") ?? raw.Value<string>("name") ?? string.Empty,
                        ["content"] = FlattenText(raw["content"]),
                    };

                    AppendBlocks(messages, "user", new JArray(toolResult));
                    continue;
                }

                if (role == "assistant")
                {
                    var blocks = new JArray();
                    var text = FlattenText(raw["content"]);
                    if (text.Length > 0)
                    {
                        blocks.Add(new JObject { ["type"] = "text", ["text"] = text });
                    }

                    if (raw["tool_calls"] is JArray toolCalls)
                    {
                        foreach (var call in toolCalls.OfType<JObject>())
                        {
                            var function = call["function"] as JObject;
                            if (function == null)
                            {
                                continue;
                            }

                            blocks.Add(new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = call.Value<string>("id") ?? NewToolId(),
                                ["name"] = function.Value<string>("name") ?? string.Empty,
                                ["input"] = ParseArguments(function["arguments"]),
                            });
                        }
                    }

                    if (blocks.Count == 0)
                    {
                        blocks.Add(new JObject { ["type"] = "text", ["text"] = string.Empty });
                    }

                    AppendBlocks(messages, "assistant", blocks);
                    continue;
                }

                AppendBlocks(messages, "user", ConvertUserContent(raw["content"]));
            }

            if (systemParts.Count > 0)
            {
                request["system"] = string.Join("\n\n", systemParts);
            }

            request["messages"] = messages;

            if (openAiBody["temperature"] != null)
            {
                request["temperature"] = openAiBody["temperature"]!.DeepClone();
            }

            if (openAiBody["top_p"] != null)
            {
                request["top_p"] = openAiBody["top_p"]!.DeepClone();
            }

            if (openAiBody["stop"] is JArray stop)
            {
                request["stop_sequences"] = new JArray(stop.Where(s => s.Type == JTokenType.String).Select(s => s.DeepClone()));
            }
            else if (openAiBody["stop"]?.Type == JTokenType.String)
            {
                request["stop_sequences"] = new JArray(openAiBody.Value<string>("stop")!);
            }

            var tools = ConvertTools(openAiBody["tools"] as JArray);
            if (tools.Count > 0)
            {
                var choice = ConvertToolChoice(openAiBody["tool_choice"]);

                // Anthropic 没有 "none"，这种情况干脆不传 tools。
                var suppressed = choice != null
                    && string.Equals(choice.Value<string>("type"), "none", StringComparison.OrdinalIgnoreCase);

                if (!suppressed)
                {
                    request["tools"] = tools;

                    if (choice != null)
                    {
                        request["tool_choice"] = choice;
                    }
                }
            }

            return request;
        }

        public static JObject ToOpenAiResponse(JObject anthropicResponse, string modelId)
        {
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var toolCalls = new JArray();

            if (anthropicResponse["content"] is JArray blocks)
            {
                foreach (var block in blocks.OfType<JObject>())
                {
                    switch (block.Value<string>("type"))
                    {
                        case "text":
                            content.Append(block.Value<string>("text") ?? string.Empty);
                            break;

                        case "thinking":
                            reasoning.Append(block.Value<string>("thinking") ?? string.Empty);
                            break;

                        case "tool_use":
                            toolCalls.Add(new JObject
                            {
                                ["id"] = block.Value<string>("id") ?? NewToolId(),
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = block.Value<string>("name") ?? string.Empty,
                                    ["arguments"] = (block["input"] ?? new JObject()).ToString(Formatting.None),
                                },
                            });
                            break;
                    }
                }
            }

            var message = new JObject
            {
                ["role"] = "assistant",
                ["content"] = content.ToString(),
            };

            if (reasoning.Length > 0)
            {
                message["reasoning_content"] = reasoning.ToString();
            }

            if (toolCalls.Count > 0)
            {
                message["tool_calls"] = toolCalls;
            }

            var usage = anthropicResponse["usage"] as JObject;
            var promptTokens = usage?.Value<long?>("input_tokens") ?? 0;
            var completionTokens = usage?.Value<long?>("output_tokens") ?? 0;

            return new JObject
            {
                ["id"] = anthropicResponse.Value<string>("id") ?? "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 16),
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = modelId,
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["index"] = 0,
                        ["message"] = message,
                        ["finish_reason"] = MapStopReason(anthropicResponse.Value<string>("stop_reason")),
                    },
                },
                ["usage"] = new JObject
                {
                    ["prompt_tokens"] = promptTokens,
                    ["completion_tokens"] = completionTokens,
                    ["total_tokens"] = promptTokens + completionTokens,
                },
            };
        }

        public static string MapStopReason(string? stopReason)
        {
            switch ((stopReason ?? string.Empty).ToLowerInvariant())
            {
                case "tool_use":
                    return "tool_calls";
                case "max_tokens":
                    return "length";
                case "refusal":
                    return "content_filter";
                default:
                    return "stop";
            }
        }

        private static string NewToolId()
        {
            return "toolu_" + Guid.NewGuid().ToString("N").Substring(0, 20);
        }

        /// <summary>Anthropic 要求 user/assistant 交替，这里把连续同角色消息合并成一个。</summary>
        private static void AppendBlocks(JArray messages, string role, JArray blocks)
        {
            if (messages.Count > 0
                && messages[messages.Count - 1] is JObject previous
                && string.Equals(previous.Value<string>("role"), role, StringComparison.Ordinal)
                && previous["content"] is JArray existing)
            {
                foreach (var block in blocks)
                {
                    existing.Add(block);
                }

                return;
            }

            messages.Add(new JObject
            {
                ["role"] = role,
                ["content"] = blocks,
            });
        }

        private static JArray ConvertUserContent(JToken? content)
        {
            var blocks = new JArray();

            if (content == null)
            {
                blocks.Add(new JObject { ["type"] = "text", ["text"] = string.Empty });
                return blocks;
            }

            if (content.Type == JTokenType.String)
            {
                blocks.Add(new JObject { ["type"] = "text", ["text"] = content.Value<string>() ?? string.Empty });
                return blocks;
            }

            if (content is JArray parts)
            {
                foreach (var part in parts.OfType<JObject>())
                {
                    var type = part.Value<string>("type");

                    if (type == "image_url" || type == "image")
                    {
                        var image = ConvertImage(part);
                        if (image != null)
                        {
                            blocks.Add(image);
                        }

                        continue;
                    }

                    var text = part.Value<string>("text") ?? part.Value<string>("content");
                    if (!string.IsNullOrEmpty(text))
                    {
                        blocks.Add(new JObject { ["type"] = "text", ["text"] = text });
                    }
                }
            }

            if (blocks.Count == 0)
            {
                blocks.Add(new JObject { ["type"] = "text", ["text"] = string.Empty });
            }

            return blocks;
        }

        private static JObject? ConvertImage(JObject part)
        {
            var url = part["image_url"] is JObject imageUrl
                ? imageUrl.Value<string>("url")
                : part.Value<string>("url") ?? part.Value<string>("data");

            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            const string dataPrefix = "data:";
            if (!url!.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // Anthropic 只接受 base64 或 URL 来源，直接透传远程地址。
                return new JObject
                {
                    ["type"] = "image",
                    ["source"] = new JObject
                    {
                        ["type"] = "url",
                        ["url"] = url,
                    },
                };
            }

            var comma = url.IndexOf(",", StringComparison.Ordinal);
            if (comma < 0)
            {
                return null;
            }

            var header = url.Substring(dataPrefix.Length, comma - dataPrefix.Length);
            var mediaType = header.Split(';')[0];
            var data = url.Substring(comma + 1);

            if (string.IsNullOrEmpty(mediaType))
            {
                mediaType = "image/png";
            }

            return new JObject
            {
                ["type"] = "image",
                ["source"] = new JObject
                {
                    ["type"] = "base64",
                    ["media_type"] = mediaType,
                    ["data"] = data,
                },
            };
        }

        private static string FlattenText(JToken? content)
        {
            if (content == null)
            {
                return string.Empty;
            }

            if (content.Type == JTokenType.String)
            {
                return content.Value<string>() ?? string.Empty;
            }

            if (content is JArray parts)
            {
                var builder = new StringBuilder();
                foreach (var part in parts.OfType<JObject>())
                {
                    var text = part.Value<string>("text") ?? part.Value<string>("content");
                    if (!string.IsNullOrEmpty(text))
                    {
                        builder.Append(text);
                    }
                }

                return builder.ToString();
            }

            return content.ToString();
        }

        private static JToken ParseArguments(JToken? arguments)
        {
            if (arguments == null)
            {
                return new JObject();
            }

            if (arguments.Type == JTokenType.Object)
            {
                return arguments.DeepClone();
            }

            var text = arguments.Type == JTokenType.String ? arguments.Value<string>() : arguments.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JObject();
            }

            try
            {
                return JToken.Parse(text!);
            }
            catch (JsonException)
            {
                return new JObject();
            }
        }

        private static JArray ConvertTools(JArray? tools)
        {
            var result = new JArray();
            if (tools == null)
            {
                return result;
            }

            foreach (var tool in tools.OfType<JObject>())
            {
                var function = tool["function"] as JObject ?? tool;
                var name = function.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var converted = new JObject
                {
                    ["name"] = name,
                    ["input_schema"] = function["parameters"]?.DeepClone()
                        ?? function["input_schema"]?.DeepClone()
                        ?? new JObject { ["type"] = "object", ["properties"] = new JObject() },
                };

                var description = function.Value<string>("description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    converted["description"] = description;
                }

                result.Add(converted);
            }

            return result;
        }

        private static JObject? ConvertToolChoice(JToken? toolChoice)
        {
            if (toolChoice == null)
            {
                return null;
            }

            if (toolChoice.Type == JTokenType.String)
            {
                var value = toolChoice.Value<string>() ?? "auto";
                if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "none" };
                }

                if (string.Equals(value, "required", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "any" };
                }

                return new JObject { ["type"] = "auto" };
            }

            if (toolChoice is JObject choice)
            {
                var function = choice["function"] as JObject;
                var name = function?.Value<string>("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return new JObject
                    {
                        ["type"] = "tool",
                        ["name"] = name,
                    };
                }

                var type = choice.Value<string>("type");
                if (string.Equals(type, "any", StringComparison.OrdinalIgnoreCase))
                {
                    return new JObject { ["type"] = "any" };
                }
            }

            return new JObject { ["type"] = "auto" };
        }
    }

    /// <summary>
    /// 把 Anthropic 的 SSE 事件流实时翻译成 OpenAI 的 <c>data: {...}</c> 分片流，
    /// 这样上层的流式转发逻辑（Ollama NDJSON 与 OpenAI SSE）都不用为协议分支。
    /// </summary>
    internal sealed class AnthropicSseTranslator : Stream
    {
        private readonly StreamReader _source;
        private readonly string _modelId;
        private readonly IDisposable? _owner;
        private readonly MemoryStream _pending = new MemoryStream();
        private readonly Dictionary<int, int> _toolIndexByBlock = new Dictionary<int, int>();
        private readonly string _chatId = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 16);
        private readonly long _created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private long _pendingOffset;
        private int _nextToolIndex;
        private bool _finished;
        private bool _disposed;
        private long _promptTokens;
        private long _completionTokens;
        private string? _stopReason;

        public AnthropicSseTranslator(Stream source, string modelId, IDisposable? owner = null)
        {
            _source = new StreamReader(source, Encoding.UTF8);
            _modelId = modelId;
            _owner = owner;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                var copied = Drain(buffer, offset, count);
                if (copied > 0)
                {
                    return copied;
                }

                if (_finished)
                {
                    return 0;
                }

                ProcessLine(_source.ReadLine());
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (true)
            {
                var copied = Drain(buffer, offset, count);
                if (copied > 0)
                {
                    return copied;
                }

                if (_finished)
                {
                    return 0;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ProcessLine(await _source.ReadLineAsync().ConfigureAwait(false));
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _source.Dispose();
                    _owner?.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private int Drain(byte[] buffer, int offset, int count)
        {
            var available = (int)(_pending.Length - _pendingOffset);
            if (available <= 0)
            {
                return 0;
            }

            _pending.Position = _pendingOffset;
            var copied = _pending.Read(buffer, offset, Math.Min(count, available));
            _pendingOffset += copied;

            if (_pendingOffset >= _pending.Length)
            {
                _pending.SetLength(0);
                _pendingOffset = 0;
            }

            return copied;
        }

        private void ProcessLine(string? line)
        {
            if (line == null)
            {
                Finish();
                return;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                return;
            }

            var payload = line.Substring(5).Trim();
            if (payload.Length == 0)
            {
                return;
            }

            JObject evt;
            try
            {
                evt = JObject.Parse(payload);
            }
            catch (JsonException)
            {
                return;
            }

            switch (evt.Value<string>("type"))
            {
                case "message_start":
                    if (evt["message"]?["usage"] is JObject startUsage)
                    {
                        _promptTokens = startUsage.Value<long?>("input_tokens") ?? 0;
                    }

                    EmitChunk(new JObject { ["role"] = "assistant" }, null);
                    break;

                case "content_block_start":
                    HandleBlockStart(evt);
                    break;

                case "content_block_delta":
                    HandleBlockDelta(evt);
                    break;

                case "message_delta":
                    _stopReason = evt["delta"]?.Value<string>("stop_reason") ?? _stopReason;
                    if (evt["usage"] is JObject usage)
                    {
                        _completionTokens = usage.Value<long?>("output_tokens") ?? _completionTokens;
                    }

                    break;

                case "message_stop":
                    EmitChunk(new JObject(), AnthropicProtocol.MapStopReason(_stopReason));
                    Finish();
                    break;

                case "error":
                    var message = evt["error"]?.Value<string>("message") ?? "上游返回错误。";
                    throw new IOException("Anthropic 流式错误：" + message);
            }
        }

        private void HandleBlockStart(JObject evt)
        {
            var block = evt["content_block"] as JObject;
            if (block == null)
            {
                return;
            }

            var blockIndex = evt.Value<int?>("index") ?? 0;

            switch (block.Value<string>("type"))
            {
                case "tool_use":
                {
                    var toolIndex = _nextToolIndex++;
                    _toolIndexByBlock[blockIndex] = toolIndex;

                    EmitChunk(
                        new JObject
                        {
                            ["tool_calls"] = new JArray
                            {
                                new JObject
                                {
                                    ["index"] = toolIndex,
                                    ["id"] = block.Value<string>("id") ?? string.Empty,
                                    ["type"] = "function",
                                    ["function"] = new JObject
                                    {
                                        ["name"] = block.Value<string>("name") ?? string.Empty,
                                        ["arguments"] = string.Empty,
                                    },
                                },
                            },
                        },
                        null);

                    break;
                }

                case "text":
                {
                    var text = block.Value<string>("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        EmitChunk(new JObject { ["content"] = text }, null);
                    }

                    break;
                }
            }
        }

        private void HandleBlockDelta(JObject evt)
        {
            var delta = evt["delta"] as JObject;
            if (delta == null)
            {
                return;
            }

            switch (delta.Value<string>("type"))
            {
                case "text_delta":
                {
                    var text = delta.Value<string>("text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        EmitChunk(new JObject { ["content"] = text }, null);
                    }

                    break;
                }

                case "thinking_delta":
                {
                    var thinking = delta.Value<string>("thinking");
                    if (!string.IsNullOrEmpty(thinking))
                    {
                        EmitChunk(new JObject { ["reasoning_content"] = thinking }, null);
                    }

                    break;
                }

                case "input_json_delta":
                {
                    var blockIndex = evt.Value<int?>("index") ?? 0;
                    if (!_toolIndexByBlock.TryGetValue(blockIndex, out var toolIndex))
                    {
                        break;
                    }

                    var partial = delta.Value<string>("partial_json");
                    if (string.IsNullOrEmpty(partial))
                    {
                        break;
                    }

                    EmitChunk(
                        new JObject
                        {
                            ["tool_calls"] = new JArray
                            {
                                new JObject
                                {
                                    ["index"] = toolIndex,
                                    ["function"] = new JObject { ["arguments"] = partial },
                                },
                            },
                        },
                        null);

                    break;
                }
            }
        }

        private void EmitChunk(JObject delta, string? finishReason)
        {
            var chunk = new JObject
            {
                ["id"] = _chatId,
                ["object"] = "chat.completion.chunk",
                ["created"] = _created,
                ["model"] = _modelId,
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["index"] = 0,
                        ["delta"] = delta,
                        ["finish_reason"] = finishReason == null ? JValue.CreateNull() : new JValue(finishReason),
                    },
                },
            };

            if (finishReason != null)
            {
                chunk["usage"] = new JObject
                {
                    ["prompt_tokens"] = _promptTokens,
                    ["completion_tokens"] = _completionTokens,
                    ["total_tokens"] = _promptTokens + _completionTokens,
                };
            }

            Append("data: " + chunk.ToString(Formatting.None) + "\n\n");
        }

        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            Append("data: [DONE]\n\n");
        }

        private void Append(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var keep = (int)(_pending.Length - _pendingOffset);
            if (keep > 0)
            {
                var remaining = new byte[keep];
                _pending.Position = _pendingOffset;
                _pending.Read(remaining, 0, keep);
                _pending.SetLength(0);
                _pending.Write(remaining, 0, keep);
                _pendingOffset = 0;
            }

            _pending.Write(bytes, 0, bytes.Length);
        }
    }
}

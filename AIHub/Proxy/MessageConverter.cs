using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIHub.Proxy
{
    /// <summary>
    /// Ollama 协议与 OpenAI 兼容协议之间的消息/工具调用转换。
    /// </summary>
    internal static class MessageConverter
    {
        public static JArray ToOpenAiMessages(JArray ollamaMessages)
        {
            var result = new JArray();
            var pendingToolCalls = new List<KeyValuePair<string, string>>();

            foreach (var item in ollamaMessages.OfType<JObject>())
            {
                var role = item.Value<string>("role") ?? "user";

                if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    var message = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = NormalizeContent(item["content"]),
                    };

                    var toolCalls = item["tool_calls"] as JArray;
                    if (toolCalls != null && toolCalls.Count > 0)
                    {
                        var converted = new JArray();
                        foreach (var toolCall in toolCalls.OfType<JObject>())
                        {
                            var function = toolCall["function"] as JObject;
                            if (function == null)
                            {
                                continue;
                            }

                            var name = function.Value<string>("name") ?? string.Empty;
                            var id = toolCall.Value<string>("id") ?? NewCallId();

                            var arguments = function["arguments"];
                            var argumentsText = arguments == null
                                ? "{}"
                                : arguments.Type == JTokenType.String
                                    ? arguments.Value<string>() ?? "{}"
                                    : arguments.ToString(Formatting.None);

                            converted.Add(new JObject
                            {
                                ["id"] = id,
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = name,
                                    ["arguments"] = argumentsText,
                                },
                            });

                            pendingToolCalls.Add(new KeyValuePair<string, string>(name, id));
                        }

                        if (converted.Count > 0)
                        {
                            message["tool_calls"] = converted;
                        }
                    }

                    result.Add(message);
                }
                else if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                {
                    var toolName = item.Value<string>("tool_name")
                        ?? (item["function"] as JObject)?.Value<string>("name")
                        ?? string.Empty;

                    var toolCallId = item.Value<string>("tool_call_id");
                    if (string.IsNullOrEmpty(toolCallId))
                    {
                        toolCallId = TakePendingToolCallId(pendingToolCalls, toolName);
                    }

                    var message = new JObject
                    {
                        ["role"] = "tool",
                        ["content"] = NormalizeContent(item["content"]),
                        ["tool_call_id"] = string.IsNullOrEmpty(toolCallId) ? NewCallId() : toolCallId,
                    };

                    if (!string.IsNullOrEmpty(toolName))
                    {
                        message["name"] = toolName;
                    }

                    result.Add(message);
                }
                else
                {
                    var message = new JObject { ["role"] = role };
                    var images = item["images"] as JArray;

                    if (images != null && images.Count > 0
                        && (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)))
                    {
                        var parts = new JArray();
                        var text = item["content"]?.Type == JTokenType.String
                            ? item.Value<string>("content") ?? string.Empty
                            : string.Empty;

                        if (!string.IsNullOrEmpty(text))
                        {
                            parts.Add(new JObject { ["type"] = "text", ["text"] = text });
                        }

                        foreach (var image in images)
                        {
                            var base64 = image.Type == JTokenType.String
                                ? image.Value<string>()
                                : image["data"]?.Value<string>();

                            if (base64 == null || base64.Length == 0)
                            {
                                continue;
                            }

                            if (base64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                            {
                                parts.Add(new JObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JObject { ["url"] = base64 },
                                });
                            }
                            else
                            {
                                parts.Add(new JObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JObject { ["url"] = $"data:{GuessMimeType(base64)};base64,{base64}" },
                                });
                            }
                        }

                        message["content"] = parts;
                    }
                    else
                    {
                        message["content"] = NormalizeContent(item["content"]);
                    }

                    result.Add(message);
                }
            }

            return result;
        }

        public static JArray NormalizeTools(JArray? tools)
        {
            var normalized = new JArray();
            if (tools == null)
            {
                return normalized;
            }

            foreach (var tool in tools.OfType<JObject>())
            {
                if (tool["function"] == null)
                {
                    continue;
                }

                var copy = (JObject)tool.DeepClone();
                if (copy.Value<string>("type") == null)
                {
                    copy["type"] = "function";
                }

                normalized.Add(copy);
            }

            return normalized;
        }

        public static JArray ToOllamaToolCalls(JArray? openAiToolCalls)
        {
            var converted = new JArray();
            if (openAiToolCalls == null)
            {
                return converted;
            }

            foreach (var toolCall in openAiToolCalls.OfType<JObject>())
            {
                var function = toolCall["function"] as JObject;
                if (function == null)
                {
                    continue;
                }

                var arguments = function["arguments"];
                JToken parsedArguments;

                if (arguments == null)
                {
                    parsedArguments = new JObject();
                }
                else if (arguments.Type == JTokenType.String)
                {
                    var text = arguments.Value<string>();
                    try
                    {
                        parsedArguments = string.IsNullOrWhiteSpace(text) ? new JObject() : JToken.Parse(text!);
                    }
                    catch (JsonException)
                    {
                        parsedArguments = new JObject { ["raw"] = text };
                    }
                }
                else
                {
                    parsedArguments = arguments.DeepClone();
                }

                converted.Add(new JObject
                {
                    ["function"] = new JObject
                    {
                        ["name"] = function.Value<string>("name") ?? string.Empty,
                        ["arguments"] = parsedArguments,
                    },
                });
            }

            return converted;
        }

        private static JToken NormalizeContent(JToken? content)
        {
            if (content == null || content.Type == JTokenType.Null)
            {
                return new JValue(string.Empty);
            }

            return content.DeepClone();
        }

        private static string TakePendingToolCallId(List<KeyValuePair<string, string>> pending, string toolName)
        {
            if (pending.Count == 0)
            {
                return string.Empty;
            }

            var index = 0;
            if (!string.IsNullOrEmpty(toolName))
            {
                var match = pending.FindIndex(pair => string.Equals(pair.Key, toolName, StringComparison.OrdinalIgnoreCase));
                if (match >= 0)
                {
                    index = match;
                }
            }

            var id = pending[index].Value;
            pending.RemoveAt(index);
            return id;
        }

        private static string NewCallId() => "call_" + Guid.NewGuid().ToString("N").Substring(0, 12);

        private static string GuessMimeType(string base64)
        {
            if (base64.StartsWith("/9j/", StringComparison.Ordinal))
            {
                return "image/jpeg";
            }

            if (base64.StartsWith("R0lGOD", StringComparison.Ordinal))
            {
                return "image/gif";
            }

            if (base64.StartsWith("UklGR", StringComparison.Ordinal))
            {
                return "image/webp";
            }

            return "image/png";
        }
    }

    /// <summary>
    /// 合并流式返回中分片下发的 tool_calls。
    /// </summary>
    internal sealed class ToolCallAccumulator
    {
        private readonly SortedDictionary<int, AccumulatedToolCall> _items = new SortedDictionary<int, AccumulatedToolCall>();

        public bool Any => _items.Count > 0;

        public void Add(JArray deltas)
        {
            foreach (var delta in deltas.OfType<JObject>())
            {
                var index = delta.Value<int?>("index") ?? _items.Count;
                if (!_items.TryGetValue(index, out var accumulated))
                {
                    accumulated = new AccumulatedToolCall();
                    _items[index] = accumulated;
                }

                var id = delta.Value<string>("id");
                if (!string.IsNullOrEmpty(id))
                {
                    accumulated.Id = id;
                }

                var function = delta["function"] as JObject;
                if (function == null)
                {
                    continue;
                }

                accumulated.Name += function.Value<string>("name") ?? string.Empty;
                accumulated.Arguments += function.Value<string>("arguments") ?? string.Empty;
            }
        }

        public JArray ToOpenAiToolCalls()
        {
            var result = new JArray();
            foreach (var item in _items.Values)
            {
                if (string.IsNullOrEmpty(item.Name))
                {
                    continue;
                }

                result.Add(new JObject
                {
                    ["id"] = string.IsNullOrEmpty(item.Id) ? "call_" + Guid.NewGuid().ToString("N").Substring(0, 12) : item.Id,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = item.Name,
                        ["arguments"] = string.IsNullOrEmpty(item.Arguments) ? "{}" : item.Arguments,
                    },
                });
            }

            return result;
        }

        private sealed class AccumulatedToolCall
        {
            public string? Id { get; set; }

            public string Name { get; set; } = string.Empty;

            public string Arguments { get; set; } = string.Empty;
        }
    }
}

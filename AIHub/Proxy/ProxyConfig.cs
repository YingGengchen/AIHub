using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace AIHub.Proxy
{
    internal static class ProxyPaths
    {
        public static string RootDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHub");

        public static string ConfigFile { get; } = Path.Combine(RootDirectory, "proxy-config.json");

        public static string LogDirectory { get; } = Path.Combine(RootDirectory, "logs");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(LogDirectory);
        }
    }

    internal sealed class ProviderSetting
    {
        public const string ProtocolOpenAi = "openai";
        public const string ProtocolAnthropic = "anthropic";

        [JsonConverter(typeof(ProtectedApiKeyConverter))]
        public string ApiKey { get; set; } = string.Empty;

        public string BaseUrl { get; set; } = string.Empty;

        /// <summary>上游协议：openai（OpenAI 兼容）或 anthropic（Claude Messages）。</summary>
        public string Protocol { get; set; } = ProtocolOpenAi;

        /// <summary>瞬时错误（429/5xx/连接失败）的重试次数，0 表示不重试。</summary>
        public int Retries { get; set; }

        /// <summary>重试耗尽后改用的备用模型，格式为 提供方/模型；留空表示不切换。</summary>
        public string Fallback { get; set; } = string.Empty;

        public bool IsAnthropic => string.Equals(Protocol, ProtocolAnthropic, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class ProxyConfig
    {
        private static readonly (string Prefix, string BaseUrl, string EnvName, string Protocol)[] KnownProviders =
        {
            ("deepseek", "https://api.deepseek.com", "DEEPSEEK_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("openai", "https://api.openai.com/v1", "OPENAI_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "GEMINI_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("kimi", "https://api.moonshot.cn/v1", "KIMI_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("xai", "https://api.x.ai/v1", "XAI_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("groq", "https://api.groq.com/openai/v1", "GROQ_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("mistral", "https://api.mistral.ai/v1", "MISTRAL_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("together", "https://api.together.xyz/v1", "TOGETHER_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("perplexity", "https://api.perplexity.ai", "PERPLEXITY_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY", ProviderSetting.ProtocolAnthropic),
            ("siliconflow", "https://api.siliconflow.cn/v1", "SILICONFLOW_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("dashscope", "https://dashscope.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("glm", "https://open.bigmodel.cn/api/paas/v4", "GLM_API_KEY", ProviderSetting.ProtocolOpenAi),
            ("volces", "https://ark.cn-beijing.volces.com/api/v3", "VOLCES_API_KEY", ProviderSetting.ProtocolOpenAi),
        };

        private static readonly Regex EnvToken = new Regex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

        private static readonly Regex EnvProviderToken = new Regex(@"^PROVIDERS_([A-Za-z0-9_]+?)_(API_KEY|BASE_URL)$", RegexOptions.Compiled);

        // 默认 ObjectCreationHandling.Auto 会复用 Models/Providers 的默认实例并追加，导致 Clone 出现重复项。
        private static readonly JsonSerializerSettings CloneSettings = new JsonSerializerSettings
        {
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public int Port { get; set; } = 11434;

        public int ContextLength { get; set; } = 1048576;

        public bool AutoStart { get; set; } = true;

        /// <summary>是否记录日志；关闭后不写日志文件，面板也不记录。</summary>
        public bool LogEnabled { get; set; } = true;

        /// <summary>自定义日志目录；留空表示使用默认目录（%LOCALAPPDATA%\AIHub\logs）。</summary>
        public string LogDirectory { get; set; } = string.Empty;

        public string DefaultProvider { get; set; } = "deepseek";

        public Dictionary<string, ProviderSetting> Providers { get; set; } = CreateDefaultProviders();

        public List<string> Models { get; set; } = new List<string>
        {
            "deepseek/deepseek-v4-flash",
            "deepseek/deepseek-v4-pro",
        };

        public static Dictionary<string, ProviderSetting> CreateDefaultProviders()
        {
            var providers = new Dictionary<string, ProviderSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var (prefix, baseUrl, envName, protocol) in KnownProviders)
            {
                providers[prefix] = new ProviderSetting
                {
                    BaseUrl = baseUrl,
                    ApiKey = ReadEnvironmentKey(envName),
                    Protocol = protocol,
                };
            }

            return providers;
        }

        /// <summary>
        /// 从环境变量自动注册提供方：<c>PROVIDERS_&lt;NAME&gt;_API_KEY</c> 与 <c>PROVIDERS_&lt;NAME&gt;_BASE_URL</c>。
        /// 只填了 API Key 而没有 Base URL 的会被忽略（无法确定端点）。
        /// </summary>
        public static Dictionary<string, (string BaseUrl, string ApiKey)> DiscoverEnvironmentProviders()
        {
            var found = new Dictionary<string, (string BaseUrl, string ApiKey)>(StringComparer.OrdinalIgnoreCase);
            var baseUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var name = entry.Key as string;
                if (name == null)
                {
                    continue;
                }

                var match = EnvProviderToken.Match(name);
                if (!match.Success)
                {
                    continue;
                }

                var prefix = match.Groups[1].Value.ToLowerInvariant();
                var value = entry.Value as string ?? string.Empty;

                if (match.Groups[2].Value == "BASE_URL")
                {
                    baseUrls[prefix] = value.Trim();
                }
                else
                {
                    keys[prefix] = value.Trim();
                }
            }

            foreach (var pair in baseUrls)
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                keys.TryGetValue(pair.Key, out var apiKey);
                found[pair.Key] = (pair.Value, string.IsNullOrEmpty(apiKey) ? "EMPTY" : apiKey);
            }

            return found;
        }

        /// <summary>
        /// 是否为内置提供方；否则是用户自己新增的自定义提供方。
        /// </summary>
        public static bool IsKnownProvider(string name)
        {
            return KnownProviders.Any(p => string.Equals(p.Prefix, name, StringComparison.OrdinalIgnoreCase));
        }

        public ProxyConfig Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            var clone = JsonConvert.DeserializeObject<ProxyConfig>(json, CloneSettings) ?? new ProxyConfig();

            // 反序列化得到的 Providers 是区分大小写的字典，这里立即归一化，保持与原始配置一致的大小写无关查找。
            clone.Normalize();
            return clone;
        }

        public void Normalize()
        {
            if (Port < 1 || Port > 65535)
            {
                Port = 11434;
            }

            if (ContextLength < 1024)
            {
                ContextLength = 1048576;
            }

            if (string.IsNullOrWhiteSpace(DefaultProvider))
            {
                DefaultProvider = "deepseek";
            }

            LogDirectory = NormalizeLogDirectory(LogDirectory);

            Providers ??= new Dictionary<string, ProviderSetting>(StringComparer.OrdinalIgnoreCase);

            // JSON 反序列化会创建区分大小写的字典，而模型前缀查找是大小写无关的，这里统一一下。
            if (!ReferenceEquals(Providers.Comparer, StringComparer.OrdinalIgnoreCase))
            {
                var insensitive = new Dictionary<string, ProviderSetting>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in Providers)
                {
                    insensitive[pair.Key] = pair.Value;
                }

                Providers = insensitive;
            }

            foreach (var (prefix, baseUrl, envName, protocol) in KnownProviders)
            {
                if (!Providers.TryGetValue(prefix, out var setting) || setting == null)
                {
                    setting = new ProviderSetting { BaseUrl = baseUrl, ApiKey = ReadEnvironmentKey(envName) };
                    Providers[prefix] = setting;
                }

                if (string.IsNullOrWhiteSpace(setting.BaseUrl))
                {
                    setting.BaseUrl = baseUrl;
                }

                if (string.IsNullOrWhiteSpace(setting.ApiKey))
                {
                    setting.ApiKey = ReadEnvironmentKey(envName);
                }

                if (string.IsNullOrWhiteSpace(setting.Protocol))
                {
                    setting.Protocol = protocol;
                }

                setting.BaseUrl = ExpandEnvironmentTokens(setting.BaseUrl.Trim());
                setting.ApiKey = ExpandEnvironmentTokens(setting.ApiKey.Trim());
            }

            // PROVIDERS_<NAME>_BASE_URL / PROVIDERS_<NAME>_API_KEY 自动注册（本地 vLLM、LM Studio 等）。
            foreach (var pair in DiscoverEnvironmentProviders())
            {
                if (!Providers.TryGetValue(pair.Key, out var setting) || setting == null)
                {
                    setting = new ProviderSetting { Protocol = ProviderSetting.ProtocolOpenAi };
                    Providers[pair.Key] = setting;
                }

                if (string.IsNullOrWhiteSpace(setting.BaseUrl))
                {
                    setting.BaseUrl = pair.Value.BaseUrl;
                }

                if (string.IsNullOrWhiteSpace(setting.ApiKey))
                {
                    setting.ApiKey = pair.Value.ApiKey;
                }
            }

            foreach (var setting in Providers.Values)
            {
                if (setting == null)
                {
                    continue;
                }

                setting.Protocol = string.IsNullOrWhiteSpace(setting.Protocol)
                    ? ProviderSetting.ProtocolOpenAi
                    : setting.Protocol.Trim().ToLowerInvariant();

                setting.Retries = Math.Max(0, Math.Min(5, setting.Retries));
                setting.Fallback = (setting.Fallback ?? string.Empty).Trim();
            }

            Models = (Models ?? new List<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .Concat(ReadExtraModelsFromEnvironment())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (Models.Count == 0)
            {
                Models = new List<string>
                {
                    "deepseek/deepseek-v4-flash",
                    "deepseek/deepseek-v4-pro",
                };
            }

            if (!Providers.ContainsKey(DefaultProvider))
            {
                DefaultProvider = Providers.Keys.FirstOrDefault() ?? "deepseek";
            }
        }

        private static string ReadEnvironmentKey(string name)
        {
            return Environment.GetEnvironmentVariable(name) ?? string.Empty;
        }

        /// <summary>日志目录归一化：展开环境变量、必须是绝对路径，否则回退到默认目录（空串）。</summary>
        public static string NormalizeLogDirectory(string? directory)
        {
            var expanded = ExpandEnvironmentTokens((directory ?? string.Empty).Trim());
            if (expanded.Length > 0)
            {
                expanded = Environment.ExpandEnvironmentVariables(expanded).Trim();
            }

            if (expanded.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                if (!Path.IsPathRooted(expanded))
                {
                    return string.Empty;
                }

                return Path.GetFullPath(expanded);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>EXTRA_MODELS 环境变量（逗号分隔）里的模型，优先级高于配置文件。</summary>
        private static IEnumerable<string> ReadExtraModelsFromEnvironment()
        {
            var extra = Environment.GetEnvironmentVariable("EXTRA_MODELS");
            if (string.IsNullOrWhiteSpace(extra))
            {
                return Array.Empty<string>();
            }

            return extra!.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim())
                .Where(m => m.Length > 0);
        }

        private static string ExpandEnvironmentTokens(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf("${", StringComparison.Ordinal) < 0)
            {
                return value;
            }

            return EnvToken.Replace(value, match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? string.Empty);
        }
    }

    internal sealed class ProxyConfigStore
    {
        private static readonly JsonSerializerSettings SerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public string ConfigFile => ProxyPaths.ConfigFile;

        public ProxyConfig Load(out string? warning)
        {
            warning = null;
            ProxyPaths.EnsureDirectories();

            if (!File.Exists(ConfigFile))
            {
                var fresh = new ProxyConfig();
                fresh.Normalize();
                return fresh;
            }

            try
            {
                var config = JsonConvert.DeserializeObject<ProxyConfig>(File.ReadAllText(ConfigFile, Encoding.UTF8)) ?? new ProxyConfig();
                config.Normalize();
                return config;
            }
            catch (Exception ex)
            {
                warning = $"读取配置文件失败，已使用默认配置：{ex.Message}";
                var fallback = new ProxyConfig();
                fallback.Normalize();
                return fallback;
            }
        }

        public void Save(ProxyConfig config)
        {
            ProxyPaths.EnsureDirectories();
            config.Normalize();

            var json = JsonConvert.SerializeObject(config, SerializerSettings);
            var tempFile = ConfigFile + ".tmp";
            File.WriteAllText(tempFile, json, new UTF8Encoding(false));

            if (File.Exists(ConfigFile))
            {
                File.Delete(ConfigFile);
            }

            File.Move(tempFile, ConfigFile);
        }
    }
}

using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace AIHub.Proxy
{
    /// <summary>
    /// 配置文件里的 API Key 落盘加密。
    /// 使用 Windows DPAPI（当前用户作用域）：只有同一台机器上的同一 Windows 用户能解密，
    /// 配置文件被复制到其他机器或账户后无法还原。
    /// </summary>
    internal static class ApiKeyProtector
    {
        private const string Prefix = "dpapi:";

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIHub.ApiKey.v1");

        public static bool IsProtected(string? value)
        {
            return !string.IsNullOrEmpty(value) && value!.StartsWith(Prefix, StringComparison.Ordinal);
        }

        public static string Protect(string? plain)
        {
            if (string.IsNullOrEmpty(plain) || IsProtected(plain))
            {
                return plain ?? string.Empty;
            }

            try
            {
                var cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain!),
                    Entropy,
                    DataProtectionScope.CurrentUser);

                return Prefix + Convert.ToBase64String(cipher);
            }
            catch (CryptographicException)
            {
                return plain!;
            }
            catch (PlatformNotSupportedException)
            {
                return plain!;
            }
        }

        public static string Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return string.Empty;
            }

            // 兼容旧版本留下的明文配置。
            if (!IsProtected(stored))
            {
                return stored!;
            }

            try
            {
                var cipher = Convert.FromBase64String(stored!.Substring(Prefix.Length));
                var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException || ex is ArgumentException)
            {
                // 换了用户或机器就无法解密，按“未配置”处理，避免代理带着乱码 Key 去请求。
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 让 <see cref="ProviderSetting.ApiKey"/> 在内存中是明文、写入 JSON 时自动加密。
    /// </summary>
    internal sealed class ProtectedApiKeyConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(string);
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            return reader.Value is string text ? ApiKeyProtector.Unprotect(text) : string.Empty;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writer.WriteValue(ApiKeyProtector.Protect(value as string));
        }
    }
}

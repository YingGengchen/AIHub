using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIHub.Proxy
{
    /// <summary>
    /// 统一的重试 / 备用模型编排：先按提供方配置重试，耗尽后改用备用模型再跑一轮。
    /// </summary>
    internal static class RetryPolicy
    {
        private const int MaxDelayMs = 10000;
        private const int BaseDelayMs = 500;

        public static async Task<T> ExecuteAsync<T>(
            ResolvedModel resolved,
            Func<ResolvedModel, CancellationToken, Task<T>> action,
            ProxyLogger logger,
            CancellationToken cancellationToken,
            Func<string, ResolvedModel>? fallbackResolver = null)
        {
            var plan = BuildPlan(resolved, fallbackResolver);

            UpstreamException? last = null;

            for (var index = 0; index < plan.Count; index++)
            {
                var current = plan[index];
                var attempts = Math.Max(1, current.Provider.Retries + 1);

                for (var attempt = 1; attempt <= attempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        return await action(current, cancellationToken).ConfigureAwait(false);
                    }
                    catch (UpstreamException ex) when (ex.Retriable)
                    {
                        last = ex;

                        if (attempt >= attempts)
                        {
                            break;
                        }

                        var delay = GetDelay(attempt);
                        logger.Warn(
                            $"上游可重试错误（HTTP {ex.StatusCode}）model={current.ModelName} "
                            + $"第 {attempt}/{attempts} 次，{delay}ms 后重试：{ex.Message}");

                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (index + 1 < plan.Count)
                {
                    logger.Warn($"模型 {current.ModelName} 已重试耗尽，切换到备用模型 {plan[index + 1].ModelName}。");
                }
            }

            throw last ?? new UpstreamException(502, "上游请求失败，且没有可用的备用模型。", false);
        }

        private static List<ResolvedModel> BuildPlan(ResolvedModel resolved, Func<string, ResolvedModel>? fallbackResolver)
        {
            var plan = new List<ResolvedModel> { resolved };

            if (fallbackResolver == null)
            {
                return plan;
            }

            var fallback = (resolved.Provider.Fallback ?? string.Empty).Trim();
            if (fallback.Length == 0)
            {
                return plan;
            }

            if (string.Equals(fallback, resolved.ModelName, StringComparison.OrdinalIgnoreCase))
            {
                return plan;
            }

            try
            {
                plan.Add(fallbackResolver(fallback));
            }
            catch (ProxyException)
            {
                // 备用模型配置有误时不应让主请求失败。
            }

            return plan;
        }

        private static int GetDelay(int attempt)
        {
            var delay = BaseDelayMs * (int)Math.Pow(2, Math.Max(0, attempt - 1));
            return Math.Min(delay, MaxDelayMs);
        }
    }
}

using AIHub.Proxy;

namespace AIHub.ToolWindows
{
    /// <summary>
    /// 提供方列表里的一项。<see cref="IsCustom"/> 为 true 表示用户自己新增的。
    /// </summary>
    internal sealed class ProviderEntry
    {
        public ProviderEntry(string name)
        {
            Name = name;
            IsCustom = !ProxyConfig.IsKnownProvider(name);
        }

        public string Name { get; }

        public bool IsCustom { get; }

        /// <summary>列表里显示的文本，自定义提供方加一个星号标记。</summary>
        public string Display => IsCustom ? Name + " ✱" : Name;

        public string ToolTip => IsCustom ? Name + "（自定义提供方）" : Name + "（内置提供方）";

        public override string ToString() => Name;
    }
}

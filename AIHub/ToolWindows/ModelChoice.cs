using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace AIHub.ToolWindows
{
    /// <summary>
    /// 模型选择器里的一行（可勾选）。
    /// </summary>
    internal sealed class ModelChoice : INotifyPropertyChanged
    {
        private static readonly string[] NonChatHints =
        {
            "embed",
            "whisper",
            "tts",
            "dall-e",
            "moderation",
            "rerank",
            "stable-diffusion",
            "speech",
            "bge-",
            "clip",
            "ocr",
            "guard",
        };

        private bool _isSelected;

        public ModelChoice(string name, bool isSelected)
        {
            Name = name;
            _isSelected = isSelected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        /// <summary>
        /// 明显不是对话模型（向量、语音、绘图等），界面上可以过滤掉。
        /// </summary>
        public static bool LooksNonChat(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            return NonChatHints.Any(hint => name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}

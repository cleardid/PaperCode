using System;
using System.Windows.Media;

namespace DynaOrchestrator.Desktop.ViewModels;

public class LogMessage
{
    /// <summary>
    /// 日志进入 UI 集合的时间。
    /// 注意：这是 UI 记录时间，不一定是 Core 层产生日志的精确时间。
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>
    /// 原始日志内容。
    /// 例如：[Info] xxx、[Error] xxx。
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// UI 最终显示文本。
    /// 这样 XAML 只需要绑定 DisplayText，就能恢复时间前缀。
    /// </summary>
    public string DisplayText => $"[{Timestamp:HH:mm:ss}] {Message}";

    // 根据日志级别动态返回颜色
    public Brush DisplayColor
    {
        get
        {
            string message = Message ?? string.Empty;

            if (message.Contains("[Error]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("致命错误", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[失败]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("失败", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Error;
            }

            if (message.Contains("[Warning]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("警告", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Warning;
            }

            if (message.Contains("[Success]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("成功", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Success;
            }

            if (message.Contains("[DYNA]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("LS-DYNA", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Dyna;
            }

            if (message.Contains("[Batch]", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Batch;
            }

            if (message.Contains("[UI]", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Ui;
            }

            if (message.Contains("[Info]", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Info;
            }

            if (message.Contains("[恢复]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[回滚]", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("回滚", StringComparison.OrdinalIgnoreCase))
            {
                return LogBrushes.Recovery;
            }

            return LogBrushes.Default;
        }
    }

    private static class LogBrushes
    {
        public static readonly Brush Default = Create("#D1D5DB");  // 普通日志：浅灰
        public static readonly Brush Info = Create("#9CA3AF");     // Info：次级灰
        public static readonly Brush Error = Create("#FF6B6B");    // Error：柔和红
        public static readonly Brush Warning = Create("#FBBF24");  // Warning：琥珀黄
        public static readonly Brush Success = Create("#34D399");  // Success：绿色
        public static readonly Brush Dyna = Create("#22D3EE");     // DYNA：青色
        public static readonly Brush Batch = Create("#60A5FA");    // Batch：蓝色
        public static readonly Brush Ui = Create("#A78BFA");       // UI：紫色
        public static readonly Brush Recovery = Create("#F472B6"); // 恢复/回滚：粉紫

        private static Brush Create(string hex)
        {
            var brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(hex));

            brush.Freeze();
            return brush;
        }
    }
}
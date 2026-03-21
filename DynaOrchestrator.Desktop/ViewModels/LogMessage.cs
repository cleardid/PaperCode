using System;
using System.Windows.Media;

namespace DynaOrchestrator.Desktop.ViewModels;

public class LogMessage
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Message { get; set; } = string.Empty;
    public string Level { get; set; } = "Info";

    // 根据日志级别动态返回颜色
    public Brush DisplayColor
    {
        get
        {
            if (Message.Contains("[Error]") || Message.Contains("致命错误") || Message.Contains("[失败]"))
                return Brushes.Red;
            if (Message.Contains("[Warning]") || Message.Contains("警告"))
                return Brushes.DarkOrange;
            if (Message.Contains("[DYNA]"))
                return Brushes.DarkCyan;
            if (Message.Contains("[Batch]") || Message.Contains("[UI]"))
                return Brushes.Blue;
            return Brushes.Black;
        }
    }
}
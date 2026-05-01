using System.Collections.Specialized;
using System.Windows;
using DynaOrchestrator.Desktop.ViewModels;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DynaOrchestrator.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        this.DataContext = viewModel;

        // 订阅日志集合变更事件，实现自动滚动到底部
        viewModel.Logs.CollectionChanged += Logs_CollectionChanged;
    }

    private bool _logScrollPending;

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add &&
            e.Action != NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        // 如果已经安排了一次滚动，就不重复安排。
        if (_logScrollPending)
            return;

        _logScrollPending = true;

        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (LogListBox.Items.Count > 0)
                {
                    var lastItem = LogListBox.Items[LogListBox.Items.Count - 1];
                    LogListBox.ScrollIntoView(lastItem);
                }
            }
            finally
            {
                _logScrollPending = false;
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }


    /// <summary>
    /// 当日志行中的 TextBox 获得焦点时，同步选中对应的 ListBoxItem。
    /// 这样用户点击某一行后，可以直接右键复制当前行。
    /// </summary>
    private void LogLineTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not DependencyObject source)
            return;

        var item = FindVisualParent<ListBoxItem>(source);
        if (item == null)
            return;

        // 普通点击时切换为当前行。
        // Ctrl / Shift 多选时，不破坏已有多选状态。
        if (Keyboard.Modifiers == ModifierKeys.None && !item.IsSelected)
        {
            LogListBox.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    /// <summary>
    /// 日志列表按 Ctrl+C 时的行为：
    /// 1. 如果当前 TextBox 内有选中文本，则保持 TextBox 默认复制行为；
    /// 2. 如果没有选中文本，则复制 ListBox 当前选中的整行日志。
    /// </summary>
    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isCopyShortcut =
            e.Key == Key.C &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        if (!isCopyShortcut)
            return;

        // 如果用户已经在某个只读 TextBox 中拖选了部分文本，
        // 则交给 TextBox 自己处理 Ctrl+C，避免强行复制整行。
        if (Keyboard.FocusedElement is TextBox focusedTextBox &&
            !string.IsNullOrEmpty(focusedTextBox.SelectedText))
        {
            return;
        }

        CopySelectedLogLinesToClipboard();
        e.Handled = true;
    }

    /// <summary>
    /// 右键点击日志行时，先选中对应行。
    /// 如果右键点击的是已经选中的多选区域，则保留原有多选。
    /// </summary>
    private void LogListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var item = FindVisualParent<ListBoxItem>(source);
        if (item == null)
            return;

        if (!item.IsSelected)
        {
            LogListBox.SelectedItems.Clear();
            item.IsSelected = true;
        }

        item.Focus();
    }

    /// <summary>
    /// 右键菜单：复制当前行。
    /// </summary>
    private void CopyCurrentLogLine_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem)
            return;

        if (menuItem.Parent is not ContextMenu contextMenu)
            return;

        if (contextMenu.PlacementTarget is not FrameworkElement target)
            return;

        string? text = GetLogLineText(target.DataContext);

        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    /// <summary>
    /// 右键菜单：复制当前选中的一行或多行。
    /// </summary>
    private void CopySelectedLogLines_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedLogLinesToClipboard();
    }

    /// <summary>
    /// 将 ListBox 当前选中的日志行复制到剪贴板。
    /// 多选时按当前显示顺序拼接，每行之间使用换行符。
    /// </summary>
    private void CopySelectedLogLinesToClipboard()
    {
        var selectedItems = LogListBox.SelectedItems
            .Cast<object>()
            .ToList();

        // 如果没有选中项，但当前焦点在某个日志 TextBox 上，
        // 则复制该 TextBox 对应的当前行。
        if (selectedItems.Count == 0 &&
            Keyboard.FocusedElement is FrameworkElement focusedElement &&
            focusedElement.DataContext != null)
        {
            selectedItems.Add(focusedElement.DataContext);
        }

        if (selectedItems.Count == 0)
            return;

        var lines = selectedItems
            .Select(GetLogLineText)
            .Where(line => line != null)
            .ToList();

        if (lines.Count == 0)
            return;

        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 从日志数据对象中提取显示文本。
    /// </summary>
    private static string? GetLogLineText(object? item)
    {
        if (item is LogMessage log)
            return log.Message;

        return item?.ToString();
    }

    /// <summary>
    /// 沿可视树向上查找指定类型的父元素。
    /// 用于从 TextBox 找到外层 ListBoxItem。
    /// </summary>
    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent)
                return parent;

            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
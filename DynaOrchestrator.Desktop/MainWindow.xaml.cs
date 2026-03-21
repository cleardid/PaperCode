using System.Collections.Specialized;
using System.Windows;
using DynaOrchestrator.Desktop.ViewModels;

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

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // 使用 BeginInvoke 延迟滚动，等待 WPF 内部 UI 元素生成完毕，防止状态冲突
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogListBox.Items.Count > 0)
                {
                    var lastItem = LogListBox.Items[LogListBox.Items.Count - 1];
                    LogListBox.ScrollIntoView(lastItem);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
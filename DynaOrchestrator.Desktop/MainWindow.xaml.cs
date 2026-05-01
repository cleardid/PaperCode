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
}
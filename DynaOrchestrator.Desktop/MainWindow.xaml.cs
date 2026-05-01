using DynaOrchestrator.Desktop.ViewModels;
using System;
using System.Collections.Specialized;
using System.Windows;

namespace DynaOrchestrator.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _logScrollPending;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        // 订阅日志集合变更事件，实现自动滚动到底部。
        _viewModel.Logs.CollectionChanged += Logs_CollectionChanged;

        // 窗口关闭时解除订阅，避免潜在事件引用残留。
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _viewModel.Logs.CollectionChanged -= Logs_CollectionChanged;
        Closed -= MainWindow_Closed;
    }

    private void Logs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add &&
            e.Action != NotifyCollectionChangedAction.Reset)
        {
            return;
        }

        // 节流：一批日志只安排一次滚动，避免大量日志导致 Dispatcher 堆积。
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
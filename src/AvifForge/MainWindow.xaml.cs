using System.ComponentModel;
using System.Windows;
using AvifForge.ViewModels;

namespace AvifForge;

/// <summary>
/// 主窗口。代码隐藏只负责拖拽事件与依赖控件集合（SelectedItems）的命令转发。
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            Vm.IsDragOver = true;
        }
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        Vm.IsDragOver = false;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        Vm.IsDragOver = false;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            Vm.AddPaths(paths);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        Vm.RemoveSelected(QueueList.SelectedItems);
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        Vm.OpenOutputFolderCommand.Execute(QueueList.SelectedItems);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (Vm.IsBusy)
        {
            MessageBoxResult result = MessageBox.Show(
                "正在转换，确定要停止并退出吗？\n（正在运行的编码进程会被终止，未完成的输出文件将被清理。）",
                "AvifForge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            // 立即取消并杀死子进程，保证退出路径快速、干净
            Vm.RequestShutdown();
        }
    }
}

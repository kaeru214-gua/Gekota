using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Gekota.WPF.Controls;
using Gekota.WPF.Services;
using Microsoft.WindowsAPICodePack.Dialogs;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    public partial class ToolboxDanmakuConvertPage
    {
        private static readonly ILogger logger = Log.ForContext<ToolboxDanmakuConvertPage>();
        private static readonly string DesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        private readonly ObservableCollection<string> Files = new();
        private readonly DanmakuConvertOptions Options = DanmakuConvertSettings.Current;

        public ToolboxDanmakuConvertPage()
        {
            this.InitializeComponent();
            this.listView.ItemsSource = this.Files;
            this.OptionsRoot.DataContext = this.Options;
        }

        private void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            var b = (Button)sender;
            if (b.DataContext is string path)
                this.Files.Remove(path);
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void DragDrop(object sender, DragEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    this.AddFiles(files);
                }
            }
            catch (Exception) { }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void AddFile_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            var d = new CommonOpenFileDialog
            {
                Title = "选择弹幕 XML 文件",
                AllowNonFileSystemItems = false,
                EnsureFileExists = true,
                EnsurePathExists = true,
                EnsureValidNames = true,
                DefaultDirectory = DesktopPath,
                DefaultExtension = "xml",
                Multiselect = true,
            };
            d.Filters.Add(new CommonFileDialogFilter("弹幕 XML 文件", "*.xml"));

            if (d.ShowDialog() == CommonFileDialogResult.Ok)
                this.AddFiles(d.FileNames.ToArray());
        }

        private void AddFiles(string[] paths)
        {
            foreach (var p in paths)
            {
                if (!this.Files.Contains(p))
                    this.Files.Add(p);
            }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void Convert_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            try
            {
                if (this.Files.Count == 0)
                {
                    MessageBox.Show("请先添加弹幕 XML 文件", "弹幕转换", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DanmakuConvertSettings.Save();

                var progressDialog = new AutoFixProgressDialog
                {
                    CancelButtonVisibility = Visibility.Collapsed,
                    Owner = Application.Current.MainWindow,
                };
                var showTask = progressDialog.ShowAndDisableMinimizeToTrayAsync();

                var inputs = this.Files.ToArray();
                var total = inputs.Length;

                var (ok, fail) = await Task.Run(() =>
                {
                    int success = 0, failure = 0;
                    for (var i = 0; i < inputs.Length; i++)
                    {
                        var xml = inputs[i];
                        var ass = Path.ChangeExtension(xml, "ass");
                        var code = DanmakuConverter.Convert(xml, ass);

                        if (code == 0)
                        {
                            success++;
                            logger.Information("[弹幕转换] 完成 {Ass}", ass);
                        }
                        else
                        {
                            failure++;
                            logger.Warning("[弹幕转换] 失败（退出码 {Code}）{Xml}", code, xml);
                        }

                        var percent = (int)((i + 1) * 100.0 / total);
                        _ = this.Dispatcher.InvokeAsync(() => progressDialog.Progress = percent);
                    }
                    return (success, failure);
                });

                progressDialog.Hide();
                await showTask.ConfigureAwait(true);

                if (fail == 0)
                {
                    this.Files.Clear();
                    MessageBox.Show($"转换完成，共 {ok} 个文件。", "弹幕转换", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"转换完成：成功 {ok} 个，失败 {fail} 个。详见日志。", "弹幕转换",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "[弹幕转换] 转换时发生未处理的错误");
                MessageBox.Show("转换出错：" + ex.Message, "弹幕转换", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>弹幕密度说明（对应 XAML 里「说明」的 MouseLeftButtonUp）。</summary>
        private void DensityTip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            MessageBox.Show("-1:不重叠   0:无限制\n其他:表示限定条数", "弹幕密度说明",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

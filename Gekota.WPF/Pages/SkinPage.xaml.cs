using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Gekota.WPF.Services;
using Microsoft.Win32;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>窗口材质与页面背景。</summary>
    public partial class SkinPage
    {
        private static readonly ILogger logger = Log.ForContext<SkinPage>();
        private bool loading;
        private bool hostingAttached;

        public SkinPage()
        {
            this.InitializeComponent();
            this.Loaded += this.SkinPage_Loaded;
            this.Unloaded += this.SkinPage_Unloaded;
        }

        internal FrameworkElement TakeContentForHosting()
        {
            this.InitializeHosting();
            var content = (FrameworkElement)this.Content;
            this.Content = null;
            return content;
        }

        internal void DetachHosting()
        {
            if (!this.hostingAttached) return;
            AppearanceManager.Changed -= this.AppearanceManager_Changed;
            this.hostingAttached = false;
        }

        private void InitializeHosting()
        {
            this.loading = true;
            try
            {
                this.DarkModeSwitch.IsOn = AppearanceManager.Current.DarkMode;
                this.AcrylicSwitch.IsOn = AppearanceManager.Current.UseAcrylic;
                this.ImagePathText.Text = string.IsNullOrWhiteSpace(AppearanceManager.Current.BackgroundImagePath)
                    ? "未设置背景图片"
                    : System.IO.Path.GetFileName(AppearanceManager.Current.BackgroundImagePath);
            }
            finally
            {
                this.loading = false;
            }

            if (!this.hostingAttached)
            {
                AppearanceManager.Changed += this.AppearanceManager_Changed;
                this.hostingAttached = true;
            }
        }

        private void SkinPage_Loaded(object sender, RoutedEventArgs e) => this.InitializeHosting();

        private void SkinPage_Unloaded(object sender, RoutedEventArgs e) => this.DetachHosting();

        private void AppearanceManager_Changed(object? sender, EventArgs e)
        {
            this.loading = true;
            try
            {
                this.DarkModeSwitch.IsOn = AppearanceManager.Current.DarkMode;
                this.AcrylicSwitch.IsOn = AppearanceManager.Current.UseAcrylic;
                this.ImagePathText.Text = string.IsNullOrWhiteSpace(AppearanceManager.Current.BackgroundImagePath)
                    ? "未设置背景图片"
                    : System.IO.Path.GetFileName(AppearanceManager.Current.BackgroundImagePath);
            }
            finally { this.loading = false; }
        }

        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                AppearanceManager.SetBackground(dialog.FileName);
                this.ImagePathText.Text = System.IO.Path.GetFileName(dialog.FileName);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[外观] 设置背景图片失败");
            }
        }

        private void ClearImage_Click(object sender, RoutedEventArgs e)
        {
            AppearanceManager.SetBackground(string.Empty);
            this.ImagePathText.Text = "未设置背景图片";
        }

        private void AcrylicSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (this.loading) return;
            AppearanceManager.SetUseAcrylic(this.AcrylicSwitch.IsOn);
        }

        private void DarkModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (this.loading) return;
            AppearanceManager.SetDarkMode(this.DarkModeSwitch.IsOn);
        }
    }
}

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gekota.WPF.Services;
using Microsoft.Win32;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>macOS 风格的外观 / 皮肤页面。</summary>
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
                this.BuildSwatches();
                this.AcrylicSwitch.IsOn = AppearanceManager.Current.UseAcrylic;
                this.ImagePathText.Text = string.IsNullOrWhiteSpace(AppearanceManager.Current.BackgroundImagePath)
                    ? "未设置背景图片"
                    : System.IO.Path.GetFileName(AppearanceManager.Current.BackgroundImagePath);
                this.RefreshPreview();
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
            try { this.RefreshPreview(); }
            finally { this.loading = false; }
        }

        private void BuildSwatches()
        {
            this.ColorPanel.Children.Clear();
            var current = AppearanceManager.Current.ThemeColorHex;

            foreach (var (name, hex, rgb) in AppearanceSettings.Presets)
            {
                var swatch = new RadioButton
                {
                    Style = (Style)this.FindResource("MacSwatchRadioStyle"),
                    Background = AppearanceManager.CreateBrush(hex),
                    GroupName = "SkinColor",
                    Tag = hex,
                    ToolTip = name + "\n" + hex + "  " + rgb,
                    IsChecked = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase),
                };
                swatch.Checked += this.Swatch_Checked;
                this.ColorPanel.Children.Add(swatch);
            }
        }

        private void Swatch_Checked(object sender, RoutedEventArgs e)
        {
            if (this.loading) return;
            if (sender is not RadioButton { Tag: string hex }) return;
            AppearanceManager.SetThemeColor(hex);
            this.RefreshPreview();
        }

        private void RefreshPreview()
        {
            var hex = AppearanceManager.Current.ThemeColorHex;
            var preset = AppearanceSettings.Presets.FirstOrDefault(p =>
                string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase));

            this.PreviewNameText.Text = string.IsNullOrEmpty(preset.Name)
                ? "自定义颜色"
                : preset.Name;
            this.PreviewHexText.Text = hex.ToUpperInvariant();

            if (AppearanceManager.Current.ThemeColorHex.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase))
                this.PreviewHintText.Text = "白色皮肤：白底 + 黑色图案";
            else
                this.PreviewHintText.Text = "彩色皮肤：皮肤色底 + 白色图案";
        }

        private void ResetDefault_Click(object sender, RoutedEventArgs e)
        {
            AppearanceManager.SetThemeColor("#FFFFFF");
            this.loading = true;
            try
            {
                foreach (var child in this.ColorPanel.Children.OfType<RadioButton>())
                    child.IsChecked = (child.Tag as string)?.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) == true;
            }
            finally { this.loading = false; }
            this.RefreshPreview();
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
    }
}

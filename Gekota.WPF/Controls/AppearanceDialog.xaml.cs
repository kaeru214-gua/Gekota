using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Gekota.WPF.Services;
using Microsoft.Win32;
using Serilog;

#nullable enable
namespace Gekota.WPF.Controls
{
    public partial class AppearanceDialog : Window
    {
        private static readonly ILogger logger = Log.ForContext<AppearanceDialog>();
        private bool loading;

        public AppearanceDialog()
        {
            this.InitializeComponent();
            this.loading = true;
            this.BuildColorSwatches();
            this.ImagePathText.Text = string.IsNullOrWhiteSpace(AppearanceManager.Current.BackgroundImagePath)
                ? "（未设置）" : System.IO.Path.GetFileName(AppearanceManager.Current.BackgroundImagePath);
            this.MaskSlider.Value = AppearanceManager.Current.MaskOpacity;
            this.MaskValueText.Text = (int)Math.Round(AppearanceManager.Current.MaskOpacity * 100) + "%";
            this.loading = false;
        }

        private void BuildColorSwatches()
        {
            foreach (var (name, hex, rgb) in AppearanceSettings.Presets)
            {
                var btn = new Button
                {
                    Width = 54,
                    Height = 54,
                    Margin = new Thickness(0, 0, 8, 8),
                    Padding = new Thickness(0),
                    Background = AppearanceManager.CreateBrush(hex),
                    BorderThickness = new Thickness(2),
                    BorderBrush = string.Equals(hex, AppearanceManager.Current.ThemeColorHex,
                        StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent,
                    ToolTip = name + " " + hex + "  " + rgb,
                    Tag = hex,
                    Cursor = System.Windows.Input.Cursors.Hand,
                };
                btn.Click += this.ColorSwatch_Click;
                this.ColorPanel.Children.Add(btn);
            }
        }

        private void ColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string hex) return;
            AppearanceManager.SetThemeColor(hex);

            // 刷新选中边框
            foreach (var child in this.ColorPanel.Children)
            {
                if (child is Button b && b.Tag is string h)
                    b.BorderBrush = string.Equals(h, hex, StringComparison.OrdinalIgnoreCase)
                        ? Brushes.White : Brushes.Transparent;
            }
        }

        private void PickImage_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            };
            if (d.ShowDialog() == true)
            {
                AppearanceManager.SetBackground(d.FileName);
                this.ImagePathText.Text = System.IO.Path.GetFileName(d.FileName);
            }
        }

        private void ClearImage_Click(object sender, RoutedEventArgs e)
        {
            AppearanceManager.SetBackground(string.Empty);
            this.ImagePathText.Text = "（未设置）";
        }

        private void MaskSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (this.MaskValueText == null) return;
            this.MaskValueText.Text = (int)Math.Round(e.NewValue * 100) + "%";
            if (this.loading) return;
            AppearanceManager.SetMaskOpacity(e.NewValue);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => this.Close();
    }
}

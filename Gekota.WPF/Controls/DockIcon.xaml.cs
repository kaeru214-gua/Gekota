using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

#nullable enable
namespace Gekota.WPF.Controls
{
    /// <summary>macOS Dock 内的圆角方形图标。</summary>
    public partial class DockIcon : UserControl
    {
        public DockIcon()
        {
            this.InitializeComponent();
            this.IsEnabledChanged += (s, e) => this.UpdateEnabledVisual();
            this.UpdateEnabledVisual();
        }

        public event EventHandler? Activated;

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(nameof(Label), typeof(string), typeof(DockIcon),
                new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)this.GetValue(LabelProperty);
            set => this.SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty IconStyleProperty =
            DependencyProperty.Register(nameof(IconStyle), typeof(Style), typeof(DockIcon),
                new PropertyMetadata(null));

        public Style? IconStyle
        {
            get => (Style?)this.GetValue(IconStyleProperty);
            set => this.SetValue(IconStyleProperty, value);
        }

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(DockIcon),
                new PropertyMetadata(false, OnIsActiveChanged));

        public bool IsActive
        {
            get => (bool)this.GetValue(IsActiveProperty);
            set => this.SetValue(IsActiveProperty, value);
        }

        public static readonly DependencyProperty BadgeProperty =
            DependencyProperty.Register(nameof(Badge), typeof(string), typeof(DockIcon),
                new PropertyMetadata(string.Empty, OnBadgeChanged));

        public string Badge
        {
            get => (string)this.GetValue(BadgeProperty);
            set => this.SetValue(BadgeProperty, value);
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // 激活状态不再显示左侧小圆点。
        }

        private static void OnBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not DockIcon icon) return;
            var text = e.NewValue as string;
            icon.BadgeText.Text = text ?? string.Empty;
            icon.BadgeHost.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!this.IsEnabled) return;
            e.Handled = true;
            this.Activated?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateEnabledVisual()
        {
            if (this.Tile == null) return;
            this.Tile.Opacity = this.IsEnabled ? 1.0 : 0.35;
            this.Cursor = this.IsEnabled ? Cursors.Hand : Cursors.Arrow;
        }
    }
}

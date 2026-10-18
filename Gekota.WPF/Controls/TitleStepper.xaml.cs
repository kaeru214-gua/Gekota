using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

#nullable enable
namespace Gekota.WPF.Controls
{
    /// <summary>带标题的步进器，复刻 danmakufactory_flutter 的 TitleStepper。</summary>
    public partial class TitleStepper : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitleStepper),
                new PropertyMetadata(string.Empty, OnTitleChanged));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(TitleStepper),
                new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(TitleStepper),
                new PropertyMetadata(0d));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(TitleStepper),
                new PropertyMetadata(double.MaxValue));

        public static readonly DependencyProperty StepProperty =
            DependencyProperty.Register(nameof(Step), typeof(double), typeof(TitleStepper),
                new PropertyMetadata(1d));

        public static readonly DependencyProperty DecimalsProperty =
            DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(TitleStepper),
                new PropertyMetadata(0));

        public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
        public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

        public TitleStepper()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => this.SyncText();
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (TitleStepper)d;
            if (c.TitleText != null) c.TitleText.Text = (string?)e.NewValue ?? string.Empty;
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((TitleStepper)d).SyncText();
        }

        private void SyncText()
        {
            if (this.ValueBox == null) return;
            this.ValueBox.Text = this.Format(this.Value);
        }

        private string Format(double v)
        {
            var r = this.Decimals > 0 ? Math.Round(v, this.Decimals) : Math.Round(v);
            return r.ToString(this.Decimals > 0 ? "F" + this.Decimals : "0", CultureInfo.InvariantCulture);
        }

        private void Commit(string text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                this.SyncText();
                return;
            }
            if (v < this.Minimum) v = this.Minimum;
            if (v > this.Maximum) v = this.Maximum;
            this.Value = v;
            this.SyncText();
        }

        private void Minus_Click(object sender, RoutedEventArgs e)
            => this.Commit((this.Value - this.Step).ToString(CultureInfo.InvariantCulture));

        private void Plus_Click(object sender, RoutedEventArgs e)
            => this.Commit((this.Value + this.Step).ToString(CultureInfo.InvariantCulture));

        private void ValueBox_LostFocus(object sender, RoutedEventArgs e)
            => this.Commit(this.ValueBox.Text);

        private void ValueBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) this.Commit(this.ValueBox.Text);
        }
    }
}

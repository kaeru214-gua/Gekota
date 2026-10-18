using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Gekota.WPF.Services;

#nullable enable
namespace Gekota.WPF.Controls
{
    public partial class QuickSettingsDialog : Window
    {
        private readonly VideoCompressOptions target = VideoCompressSettings.Current;
        private bool loading;

        public QuickSettingsDialog()
        {
            this.InitializeComponent();
            this.LoadFrom(this.target);
        }

        private void LoadFrom(VideoCompressOptions o)
        {
            this.loading = true;

            this.SelectCombo(this.ContainerBox, o.Container);
            this.SelectCombo(this.VideoCodecBox, o.VideoCodec);
            this.SelectCombo(this.ProfileBox, o.Profile);
            this.SelectCombo(this.LevelBox, o.Level);
            this.SelectCombo(this.PresetBox, o.VideoPreset);
            this.SelectCombo(this.TuneBox, o.VideoTune);

            switch ((o.RateMode ?? "bitrate").ToLowerInvariant())
            {
                case "quality": this.RateQualityRadio.IsChecked = true; break;
                case "quantizer": this.RateQuantRadio.IsChecked = true; break;
                default: this.RateBitrateRadio.IsChecked = true; break;
            }

            this.VideoBitrateBox.Text = o.VideoBitrateKbps.ToString(CultureInfo.InvariantCulture);
            this.QualityBox.Text = o.Quality.ToString(CultureInfo.InvariantCulture);
            this.QuantizerBox.Text = o.Quantizer.ToString(CultureInfo.InvariantCulture);
            this.SelectCombo(this.RateControlBox, o.RateControl);

            this.FpsBox.Text = o.Fps.ToString(CultureInfo.InvariantCulture);
            this.CfrBox.IsChecked = string.Equals(o.FpsMode, "cfr", StringComparison.OrdinalIgnoreCase);
            this.KeyframeIntervalBox.Text = o.KeyframeInterval.ToString(CultureInfo.InvariantCulture);

            this.AutoResolutionBox.IsChecked = o.AutoResolution;
            this.OutputWidthBox.Text = o.OutputWidth.ToString(CultureInfo.InvariantCulture);
            this.OutputHeightBox.Text = o.OutputHeight.ToString(CultureInfo.InvariantCulture);
            this.SelectCombo(this.ScaleFilterBox, o.ScaleFilter);

            this.SelectCombo(this.AudioCodecBox, o.AudioCodec);
            this.SelectCombo(this.AacProfileBox, o.AacProfile);
            this.AudioBitrateBox.Text = o.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture);
            this.SelectCombo(this.AudioChannelsBox, o.AudioChannels.ToString(CultureInfo.InvariantCulture));
            this.SelectCombo(this.SampleRateBox, o.SampleRate.ToString(CultureInfo.InvariantCulture));

            this.AutoCompressBox.IsChecked = o.AutoCompressEnabled;

            this.loading = false;
            this.UpdateEnabledState();
        }

        private void CollectTo(VideoCompressOptions o)
        {
            o.Container = this.ComboTag(this.ContainerBox) ?? "mp4";
            o.VideoCodec = this.ComboTag(this.VideoCodecBox) ?? "h264_nvenc";
            o.Profile = this.ComboTag(this.ProfileBox) ?? "baseline";
            o.Level = this.ComboTag(this.LevelBox) ?? "auto";
            o.VideoPreset = this.ComboTag(this.PresetBox) ?? "p4";
            o.VideoTune = this.ComboTag(this.TuneBox) ?? "hq";

            o.RateMode = this.RateQualityRadio.IsChecked == true ? "quality"
                       : this.RateQuantRadio.IsChecked == true ? "quantizer"
                       : "bitrate";
            o.VideoBitrateKbps = this.ParseInt(this.VideoBitrateBox.Text, 10000);
            o.Quality = this.ParseInt(this.QualityBox.Text, 23);
            o.Quantizer = this.ParseInt(this.QuantizerBox.Text, 23);
            o.RateControl = this.ComboTag(this.RateControlBox) ?? "const";

            o.Fps = this.ParseIntAllowZero(this.FpsBox.Text, 60);
            o.FpsMode = this.CfrBox.IsChecked == true ? "cfr" : "vfr";
            o.KeyframeInterval = this.ParseInt(this.KeyframeIntervalBox.Text, 10);

            o.AutoResolution = this.AutoResolutionBox.IsChecked == true;
            o.OutputWidth = this.ParseInt(this.OutputWidthBox.Text, 1920);
            o.OutputHeight = this.ParseInt(this.OutputHeightBox.Text, 1080);
            o.ScaleFilter = this.ComboTag(this.ScaleFilterBox) ?? "bicubic";

            o.AudioCodec = this.ComboTag(this.AudioCodecBox) ?? "libfdk_aac";
            o.AacProfile = this.ComboTag(this.AacProfileBox) ?? "aac_low";
            o.AudioBitrateKbps = this.ParseInt(this.AudioBitrateBox.Text, 512);
            o.AudioChannels = this.ParseIntAllowZero(this.ComboTag(this.AudioChannelsBox) ?? "2", 2);
            o.SampleRate = this.ParseInt(this.ComboTag(this.SampleRateBox) ?? "48000", 48000);

            o.AutoCompressEnabled = this.AutoCompressBox.IsChecked == true;
        }

        private void SelectCombo(ComboBox box, string? tag)
        {
            foreach (var item in box.Items)
            {
                if (item is ComboBoxItem cbi && (cbi.Tag as string ?? cbi.Content as string) == tag)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
        }

        private string? ComboTag(ComboBox box) =>
            (box.SelectedItem as ComboBoxItem)?.Tag as string ?? (box.SelectedItem as ComboBoxItem)?.Content as string;

        private int ParseInt(string? s, int def) => int.TryParse(s, out var v) && v > 0 ? v : def;
        private int ParseIntAllowZero(string? s, int def) => int.TryParse(s, out var v) && v >= 0 ? v : def;

        private void UpdateEnabledState()
        {
            var auto = this.AutoResolutionBox.IsChecked == true;
            this.OutputWidthBox.IsEnabled = !auto;
            this.OutputHeightBox.IsEnabled = !auto;

            var quality = this.RateQualityRadio.IsChecked == true;
            var quant = this.RateQuantRadio.IsChecked == true;

            this.BitratePanel.Visibility = (!quality && !quant) ? Visibility.Visible : Visibility.Collapsed;
            this.QualityPanel.Visibility = quality ? Visibility.Visible : Visibility.Collapsed;
            this.QuantizerPanel.Visibility = quant ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RateMode_Changed(object sender, RoutedEventArgs e)
        {
            if (this.loading) return;
            this.UpdateEnabledState();
        }

        private void AutoResolution_Changed(object sender, RoutedEventArgs e)
        {
            if (this.loading) return;
            this.UpdateEnabledState();
        }

        private void VideoCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.loading) return;
        }

        private void Reset_Click(object sender, RoutedEventArgs e) => this.LoadFrom(new VideoCompressOptions());

        private void Cancel_Click(object sender, RoutedEventArgs e) => this.DialogResult = false;

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            this.CollectTo(this.target);
            VideoCompressSettings.Save();
            this.DialogResult = true;
        }
    }
}

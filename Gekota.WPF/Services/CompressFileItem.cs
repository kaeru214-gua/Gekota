using System;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>压制列表中的一行。</summary>
    public sealed class CompressFileItem : INotifyPropertyChanged
    {
        private string statusText = "等待";
        private string subtitleText = "";
        private string presetText = "自定义";

        public CompressFileItem(string filePath)
        {
            this.FilePath = filePath;
            this.Name = Path.GetFileName(filePath);
            this.Info = MediaProbe.Probe(filePath);
        }

        public string FilePath { get; }
        public string Name { get; }
        public MediaInfo? Info { get; }
        public string? SubtitlePath { get; set; }

        public string DurationText => this.Info?.DurationText ?? "--:--";
        public string FormatText => this.Info?.Format ?? "";

        public string SubtitleText
        {
            get => this.subtitleText;
            set { this.subtitleText = value; this.Raise(nameof(SubtitleText)); }
        }

        public string PresetText
        {
            get => this.presetText;
            set { this.presetText = value; this.Raise(nameof(PresetText)); }
        }

        public string StatusText
        {
            get => this.statusText;
            set
            {
                this.statusText = value;
                this.Raise(nameof(StatusText));
                this.Raise(nameof(StatusBrush));
            }
        }

        public Brush StatusBrush => this.StatusText switch
        {
            "完成" => Brushes.ForestGreen,
            "失败" => Brushes.Red,
            "压制中" => Brushes.DodgerBlue,
            _ => Brushes.Gray,
        };

        /// <summary>输入侧信息（多行）。</summary>
        public string InputInfoText
        {
            get
            {
                var i = this.Info;
                if (i == null) return "输入：解析失败";
                return "输入：" + i.Format + ", " + i.DurationText + ", " + i.SizeText + "\n"
                     + "视频：" + i.VideoText + "\n"
                     + "音频：" + i.AudioText;
            }
        }

        /// <summary>输出侧信息（按当前设置推算）。</summary>
        public string OutputInfoText
        {
            get
            {
                var i = this.Info;
                if (i == null) return "输出：-";
                var o = VideoCompressSettings.Current;

                var v = (o.VideoCodec ?? "").ToUpperInvariant();
                if (v.Contains("NVENC")) v = "H264(NVENC)";
                var res = o.AutoResolution ? (i.Width > 0 ? i.Width + "x" + i.Height : "自动")
                                           : (o.OutputWidth + "x" + o.OutputHeight);
                var fps = o.Fps > 0 ? o.Fps + "fps(" + (o.FpsMode == "cfr" ? "CFR" : "VFR") + ")"
                                    : i.Fps.ToString("0.##") + "fps";
                var br = o.RateMode switch
                {
                    "quality" => "CQ " + o.Quality,
                    "quantizer" => "QP " + o.Quantizer,
                    _ => o.VideoBitrateKbps + "Kbps",
                };
                var a = (o.AudioCodec ?? "").ToUpperInvariant();
                if (a.Contains("FDK")) a = "AAC";
                var ch = o.AudioChannels > 0 ? o.AudioChannels + "ch" : i.Channels + "ch";

                return "输出：" + o.Container + ", " + i.DurationText + ", 约 " + EstimateSizeText + "\n"
                     + "视频：" + v + ", " + res + ", " + fps + ", " + br + "\n"
                     + "音频：" + a + ", " + o.SampleRate + "Hz, " + ch + ", " + o.AudioBitrateKbps + "Kbps";
            }
        }

        private string EstimateSizeText
        {
            get
            {
                var i = this.Info;
                if (i == null || i.DurationSec <= 0) return "-";
                var o = VideoCompressSettings.Current;
                var kbps = o.RateMode == "bitrate" ? o.VideoBitrateKbps : 0;
                if (kbps <= 0) return "（质量模式）";
                var bytes = (long)((kbps + o.AudioBitrateKbps) * 1000.0 / 8.0 * i.DurationSec);
                return MediaProbe.FormatSize(bytes);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public void RaiseAll()
        {
            this.Raise(nameof(OutputInfoText));
            this.Raise(nameof(InputInfoText));
        }
    }
}

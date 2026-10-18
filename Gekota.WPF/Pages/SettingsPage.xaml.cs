using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Gekota.Core.Config.V3;
using Gekota.Core.Scripting;
using Gekota.Core.Templating;
using Gekota.WPF.Services;
using Serilog;

#nullable enable
namespace Gekota.WPF.Pages
{
    /// <summary>
    /// Interaction logic for SettingsPage.xaml
    /// </summary>
    public partial class SettingsPage : INotifyPropertyChanged
    {
        private static readonly ILogger logger = Log.ForContext<SettingsPage>();

        private static readonly FileNameTemplateContext fileNameTestData = new()
        {
            Name = "3号直播间",
            RoomId = 23058,
            ShortId = 3,
            Uid = 11153765,
            Title = "mikufans音悦台",
            AreaParent = "电台",
            AreaChild = "唱见电台",
            Qn = 10000,
            Json = null,
        };

        private sealed class QualityOption
        {
            public string DisplayName { get; set; } = string.Empty;
            public int Qn { get; set; }
            public override string ToString() => this.DisplayName;
        }

        private static readonly QualityOption[] QualityOptions = new[]
        {
            new QualityOption { DisplayName = "720p 超清", Qn = 250 },
            new QualityOption { DisplayName = "1080p 蓝光", Qn = 400 },
            new QualityOption { DisplayName = "1080p 原画(高帧率)", Qn = 10000 },
        };

        private readonly GlobalConfig? globalConfig;
        private readonly Gekota.Core.Api.ICookieTester? httpApiClient;
        private readonly UserScriptRunner? userScriptRunner;

        private DispatcherTimer? fileNameTemplateSaveTimer;

        private bool isCustomCookie;
        private bool isCustomFileNameTemplate;
        private bool suppressCookieCheckBoxEvent;
        private bool suppressFileNameCheckBoxEvent;
        private bool initializingQuality;
        private bool initializingAutomation;

        public SettingsPage()
            : this(
                  RootPage.ServiceProvider?.GetService(typeof(GlobalConfig)) as GlobalConfig,
                  RootPage.ServiceProvider?.GetService(typeof(Gekota.Core.Api.ICookieTester)) as Gekota.Core.Api.ICookieTester,
                  RootPage.ServiceProvider?.GetService(typeof(UserScriptRunner)) as UserScriptRunner
                  )
        {
        }

        public SettingsPage(GlobalConfig? globalConfig, Gekota.Core.Api.ICookieTester? httpApiClient, UserScriptRunner? userScriptRunner)
        {
            this.globalConfig = globalConfig;
            this.httpApiClient = httpApiClient;
            this.userScriptRunner = userScriptRunner;

            this.InitializeComponent();
            this.Loaded += this.SettingsPage_Loaded;
        }

        // ==================== INotifyPropertyChanged ====================
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            this.InitializeQualityComboBox();
            this.InitializeFileNameTemplateUI();
            this.InitializeCookieUI();
            this.InitializeAutomationUI();
        }

        // ==================== 录制结束后自动化 ====================
        private void InitializeAutomationUI()
        {
            this.initializingAutomation = true;
            try
            {
                this.AutoConvertCheckBox.IsChecked = DanmakuConvertSettings.Current.AutoConvertEnabled;
                this.AutoCompressCheckBox.IsChecked = VideoCompressSettings.Current.AutoCompressEnabled;
                this.AutoUploadCheckBox.IsChecked = BiliUploadSettings.AutoUploadEnabled;
            }
            finally
            {
                this.initializingAutomation = false;
            }
        }

        private void AutoConvertCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.initializingAutomation) return;
            if (this.AutoConvertCheckBox.IsChecked is not bool b) return;
            DanmakuConvertSettings.Current.AutoConvertEnabled = b;
            DanmakuConvertSettings.Save();
        }

        private void AutoCompressCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.initializingAutomation) return;
            if (this.AutoCompressCheckBox.IsChecked is not bool b) return;
            VideoCompressSettings.Current.AutoCompressEnabled = b;
            VideoCompressSettings.Save();
        }

        private void AutoUploadCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (this.initializingAutomation) return;
            if (this.AutoUploadCheckBox.IsChecked is not bool b) return;
            BiliUploadSettings.AutoUploadEnabled = b;
        }

        // ==================== 录制画质 ====================
        private void InitializeQualityComboBox()
        {
            if (this.QualityComboBox is null) return;

            this.initializingQuality = true;
            try
            {
                this.QualityComboBox.ItemsSource = QualityOptions;

                var session = Gekota.WPF.Services.UserSessionStore.Load();
                var loggedIn = session is not null;
                this.QualityComboBox.IsEnabled = loggedIn;

                if (!loggedIn)
                {
                    this.QualityComboBox.SelectedIndex = 0;
                    if (this.QualityHintText is not null)
                        this.QualityHintText.Text = "未登录：默认 720p 超清，无法修改。";
                    if (this.globalConfig is { } cfg) cfg.RecordingQuality = "250";
                }
                else
                {
                    var idx = QualityFromConfig(this.globalConfig?.RecordingQuality);
                    this.QualityComboBox.SelectedIndex = idx;
                    if (this.QualityHintText is not null)
                        this.QualityHintText.Text = "已登录：可自由切换录制画质。";
                }

                this.QualityComboBox.SelectionChanged -= this.QualityComboBox_SelectionChanged;
                this.QualityComboBox.SelectionChanged += this.QualityComboBox_SelectionChanged;
            }
            finally
            {
                this.initializingQuality = false;
            }
        }

        private void QualityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.initializingQuality) return;
            if (this.QualityComboBox is null || !this.QualityComboBox.IsEnabled) return;
            if (this.globalConfig is not { } cfg) return;
            if (this.QualityComboBox.SelectedItem is not QualityOption opt) return;

            cfg.RecordingQuality = opt.Qn.ToString();
            logger.Debug("录制画质已设置为 {Qn}", opt.Qn);
        }

        private static int QualityFromConfig(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var first = value!.Split(',')[0].Trim();
            first = System.Text.RegularExpressions.Regex.Replace(first, @"^(avc|hevc)", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!int.TryParse(first, out var qn)) return 0;
            return qn switch { 400 => 1, 10000 => 2, _ => 0 };
        }

        // ==================== 文件名模板 默认 / 自定义 ====================
        public bool IsCustomFileNameTemplate
        {
            get => this.isCustomFileNameTemplate;
            set
            {
                if (this.isCustomFileNameTemplate == value) return;
                this.isCustomFileNameTemplate = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.IsDefaultFileNameTemplate));

                // 勾选"默认" → 恢复模板默认值
                if (!value && !this.suppressFileNameCheckBoxEvent && this.globalConfig is { } cfg)
                {
                    try
                    {
                        cfg.HasFileNameRecordTemplate = false;
                        var def = Gekota.Core.Config.V3.DefaultConfig.Instance.FileNameRecordTemplate;
                        if (this.FileNameTemplateTextBox is not null && this.FileNameTemplateTextBox.Text != def)
                            this.FileNameTemplateTextBox.Text = def;
                    }
                    catch { }
                }
            }
        }

        public bool IsDefaultFileNameTemplate
        {
            get => !this.isCustomFileNameTemplate;
            set => this.IsCustomFileNameTemplate = !value;
        }

        private void InitializeFileNameTemplateUI()
        {
            if (this.globalConfig is null) return;
            try
            {
                var currentTemplate = this.globalConfig.FileNameRecordTemplate;
                var defaultTemplate = Gekota.Core.Config.V3.DefaultConfig.Instance.FileNameRecordTemplate;

                // 只要值与默认一致 或 没有自定义，就算作"默认"
                var custom = this.globalConfig.HasFileNameRecordTemplate
                             && !string.IsNullOrWhiteSpace(currentTemplate)
                             && !string.Equals(currentTemplate, defaultTemplate, StringComparison.Ordinal);
                this.suppressFileNameCheckBoxEvent = true;
                try
                {
                    this.isCustomFileNameTemplate = custom;
                    this.OnPropertyChanged(nameof(this.IsCustomFileNameTemplate));
                    this.OnPropertyChanged(nameof(this.IsDefaultFileNameTemplate));
                }
                finally
                {
                    this.suppressFileNameCheckBoxEvent = false;
                }

                var value = this.globalConfig.FileNameRecordTemplate
                            ?? Gekota.Core.Config.V3.DefaultConfig.Instance.FileNameRecordTemplate;
                if (this.FileNameTemplateTextBox is not null && this.FileNameTemplateTextBox.Text != value)
                    this.FileNameTemplateTextBox.Text = value;
            }
            catch { }
        }

        private void FileNameTemplateTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            this.SaveFileNameTemplateInternal(tb.Text ?? string.Empty);
        }

        private void FileNameTemplateTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            this.fileNameTemplateSaveTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800),
            };
            this.fileNameTemplateSaveTimer.Stop();
            this.fileNameTemplateSaveTimer.Tick -= this.FileNameTemplateSaveTimer_Tick;
            this.fileNameTemplateSaveTimer.Tick += this.FileNameTemplateSaveTimer_Tick;
            this.fileNameTemplateSaveTimer.Tag = tb.Text;
            this.fileNameTemplateSaveTimer.Start();
        }

        private void FileNameTemplateSaveTimer_Tick(object? sender, EventArgs e)
        {
            this.fileNameTemplateSaveTimer?.Stop();
            if (this.fileNameTemplateSaveTimer?.Tag is not string value) return;
            this.SaveFileNameTemplateInternal(value);
        }

        private void SaveFileNameTemplateInternal(string newValue)
        {
            if (this.globalConfig is not { } cfg) return;
            try
            {
                var prop = cfg.GetType().GetProperty("OptionalFileNameRecordTemplate");
                if (prop is null) return;
                SetOptionalValue(cfg, prop, newValue);

                // 与默认值对比：相同 → 视为默认；不同 → 视为自定义
                var def = Gekota.Core.Config.V3.DefaultConfig.Instance.FileNameRecordTemplate;
                var isDefault = string.Equals(newValue, def, StringComparison.Ordinal);

                this.suppressFileNameCheckBoxEvent = true;
                try
                {
                    this.isCustomFileNameTemplate = !isDefault;
                    this.OnPropertyChanged(nameof(this.IsCustomFileNameTemplate));
                    this.OnPropertyChanged(nameof(this.IsDefaultFileNameTemplate));
                }
                finally
                {
                    this.suppressFileNameCheckBoxEvent = false;
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "保存文件名模板失败");
            }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void TestFileNameTemplate_Button_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            if (this.globalConfig is not { } config) return;
            try
            {
                var output = await Task.Run(() =>
                {
                    var generator = new FileNameGenerator(config, null);
                    return generator.CreateFilePath(fileNameTestData);
                });
                this.FileNameTestResultArea.Visibility = Visibility.Visible;
                this.FileNameTestResultArea.DataContext = output;
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "测试文件名模板失败");
            }
        }

        // ==================== Cookie 默认 / 自定义 ====================
        public bool IsCustomCookie
        {
            get => this.isCustomCookie;
            set
            {
                if (this.isCustomCookie == value) return;
                this.isCustomCookie = value;
                this.OnPropertyChanged();
                this.OnPropertyChanged(nameof(this.IsDefaultCookie));

                if (!value && !this.suppressCookieCheckBoxEvent)
                {
                    try
                    {
                        var session = Gekota.WPF.Services.UserSessionStore.Load();
                        var cookie = session?.Cookie ?? string.Empty;
                        this.SetConfigCookie(cookie);
                        if (this.CookieTextBox is not null && this.CookieTextBox.Text != cookie)
                            this.CookieTextBox.Text = cookie;
                    }
                    catch { }
                }

                this.RefreshCookieUI();
            }
        }

        public bool IsDefaultCookie
        {
            get => !this.isCustomCookie;
            set => this.IsCustomCookie = !value;
        }

        private void InitializeCookieUI()
        {
            if (this.globalConfig is null) return;
            try
            {
                var session = Gekota.WPF.Services.UserSessionStore.Load();
                var cookie = this.globalConfig.Cookie ?? string.Empty;

                bool custom;
                if (string.IsNullOrWhiteSpace(cookie))
                    custom = false;
                else if (session is not null && !string.IsNullOrWhiteSpace(session.Cookie)
                         && string.Equals(cookie, session.Cookie, StringComparison.Ordinal))
                    custom = false;
                else
                    custom = true;

                this.suppressCookieCheckBoxEvent = true;
                try
                {
                    this.isCustomCookie = custom;
                    this.OnPropertyChanged(nameof(this.IsCustomCookie));
                    this.OnPropertyChanged(nameof(this.IsDefaultCookie));
                }
                finally
                {
                    this.suppressCookieCheckBoxEvent = false;
                }

                if (this.CookieTextBox is not null && this.CookieTextBox.Text != cookie)
                    this.CookieTextBox.Text = cookie;

                this.RefreshCookieUI();
            }
            catch { }
        }

        private void RefreshCookieUI()
        {
            try
            {
                if (this.CookieStatusText is null || this.globalConfig is null) return;
                var session = Gekota.WPF.Services.UserSessionStore.Load();
                var cookie = this.globalConfig.Cookie ?? string.Empty;

                if (string.IsNullOrWhiteSpace(cookie))
                {
                    this.CookieStatusText.Text = "当前：Cookie 为空";
                    this.CookieStatusText.Foreground = System.Windows.Media.Brushes.Red;
                }
                else if (session is not null && !string.IsNullOrWhiteSpace(session.Cookie)
                         && string.Equals(cookie, session.Cookie, StringComparison.Ordinal))
                {
                    this.CookieStatusText.Text = "当前：使用登录用户的 Cookie";
                    this.CookieStatusText.Foreground = System.Windows.Media.Brushes.Green;
                }
                else
                {
                    this.CookieStatusText.Text = "当前：手动填写的 Cookie";
                    this.CookieStatusText.Foreground = System.Windows.Media.Brushes.Orange;
                }
            }
            catch { }
        }

        private void CookieTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (this.globalConfig is not { } cfg) return;
            if (sender is not TextBox tb) return;
            var value = tb.Text ?? string.Empty;
            try
            {
                this.SetConfigCookie(value);
                var session = Gekota.WPF.Services.UserSessionStore.Load();
                var isDefault = session is not null
                                && !string.IsNullOrWhiteSpace(session.Cookie)
                                && string.Equals(value, session.Cookie, StringComparison.Ordinal);

                this.suppressCookieCheckBoxEvent = true;
                try
                {
                    this.isCustomCookie = !isDefault;
                    this.OnPropertyChanged(nameof(this.IsCustomCookie));
                    this.OnPropertyChanged(nameof(this.IsDefaultCookie));
                }
                finally
                {
                    this.suppressCookieCheckBoxEvent = false;
                }

                this.RefreshCookieUI();
            }
            catch { }
        }

        private void ResetCookie_Click(object sender, RoutedEventArgs e)
        {
            if (this.globalConfig is not { } cfg) return;
            var session = Gekota.WPF.Services.UserSessionStore.Load();
            var cookie = session?.Cookie ?? string.Empty;

            try
            {
                this.SetConfigCookie(cookie);
                if (this.CookieTextBox is not null) this.CookieTextBox.Text = cookie;

                this.suppressCookieCheckBoxEvent = true;
                try
                {
                    this.isCustomCookie = false;
                    this.OnPropertyChanged(nameof(this.IsCustomCookie));
                    this.OnPropertyChanged(nameof(this.IsDefaultCookie));
                }
                finally
                {
                    this.suppressCookieCheckBoxEvent = false;
                }

                this.RefreshCookieUI();
            }
            catch { }
        }

#pragma warning disable VSTHRD100 // Avoid async void methods
        private async void TestCookie_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100 // Avoid async void methods
        {
            try
            {
                if (this.httpApiClient is null)
                {
                    MessageBox.Show("Cookie 测试客户端不可用", "Cookie Test",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var (ok, msg) = await this.httpApiClient.TestCookieAsync().ConfigureAwait(false);
                MessageBox.Show(msg, ok ? "Cookie Test - Succeed" : "Cookie Test - Failed",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Exception in TestCookie");
                MessageBox.Show(ex.ToString(), "Cookie Test - Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ==================== 辅助 ====================
        private void SetConfigCookie(string cookie)
        {
            if (this.globalConfig is not { } cfg) return;
            try
            {
                var prop = cfg.GetType().GetProperty("OptionalCookie");
                if (prop is null) return;
                SetOptionalValue(cfg, prop, cookie);
            }
            catch { }
        }

        private static void SetOptionalValue(object config, System.Reflection.PropertyInfo prop, string value)
        {
            var optType = prop.PropertyType;
            var implicitOp = optType.GetMethod("op_Implicit", new[] { typeof(string) });
            object? optValue = implicitOp is not null
                ? implicitOp.Invoke(null, new object[] { value })
                : Activator.CreateInstance(optType, new object[] { value });
            if (optValue is not null)
                prop.SetValue(config, optValue);
        }
    }
}

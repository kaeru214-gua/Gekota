using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gekota.Core;
using Gekota.WPF.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#nullable enable
namespace Gekota.WPF.Pages
{
    public partial class SchedulePage
    {
        private static readonly string[] Days = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        private static readonly string[] TargetNames = { "贝拉", "嘉然", "乃琳", "心宜", "思诺" };
        private static readonly HttpClient http = CreateHttpClient();
        private static string buvidCookie = "";

        private readonly Dictionary<int, Grid> dayPanels = new();
        private readonly List<ReservationItem> reservations = new();
        private readonly Dictionary<string, Color> themeByRoom = new(StringComparer.Ordinal);
        private readonly HashSet<string> skippedKeys = new(StringComparer.Ordinal);
        private readonly string skipPath = ScheduleSkipPolicy.DefaultPath;
        private readonly DispatcherTimer refreshTimer;
        private bool refreshing;
        private bool initialized;
        private string? selectedRoomName;
        private string? lastFailureMessage;

        private static readonly Dictionary<string, Color> KnownThemes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A-SOUL"] = Color.FromRgb(0xFC, 0x96, 0x6E),
            ["闪耀舞台"] = Color.FromRgb(0x83, 0xBD, 0xF9),
            ["贝拉"] = Color.FromRgb(0xDB, 0x7D, 0x74),
            ["嘉然"] = Color.FromRgb(0xE7, 0x99, 0xB0),
            ["乃琳"] = Color.FromRgb(0x57, 0x66, 0x90),
            ["心宜"] = Color.FromRgb(0xC9, 0x37, 0x73),
            ["思诺"] = Color.FromRgb(0x72, 0x52, 0xC0),
        };

        private static readonly Dictionary<string, long> KnownUids = new(StringComparer.OrdinalIgnoreCase)
        {
            ["贝拉"] = 672353429,
            ["嘉然"] = 672328094,
            ["乃琳"] = 672342685,
            ["心宜"] = 3537115310721181,
            ["思诺"] = 3537115310721781,
        };

        public ObservableCollection<StreamerTile> StreamerTiles { get; } = new();

        public SchedulePage()
        {
            this.InitializeComponent();
            this.StreamerBar.ItemsSource = this.StreamerTiles;
            this.LoadSkips();
            this.CalendarUrlBox.Text = ScheduleCalendarService.Url;
            this.CalendarBar.Visibility = Visibility.Collapsed;
            this.refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            this.refreshTimer.Tick += async (s, e) => await this.LoadReservationsAsync();
            this.Loaded += this.SchedulePage_Loaded;
            this.DataContextChanged += async (s, e) =>
            {
                if (!this.initialized || e.NewValue is not IRecorder || this.refreshing) return;
                await this.BuildStreamerTilesAsync();
                if (this.StreamerBar.SelectedIndex < 0 && this.StreamerTiles.Count > 0)
                    this.StreamerBar.SelectedIndex = 0;
                await this.LoadReservationsAsync();
            };
            this.Unloaded += (s, e) => this.refreshTimer.Stop();
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
            return client;
        }

#pragma warning disable VSTHRD100
        private async void SchedulePage_Loaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (this.initialized)
            {
                this.refreshTimer.Start();
                return;
            }
            this.initialized = true;
            if (!await this.WaitForRecorderAsync())
            {
                this.LoadingOverlay.Visibility = Visibility.Collapsed;
                this.ShowFailure("正在读取房间信息，请稍后再试。");
                return;
            }
            this.BuildWeekGrid();
            await this.BuildStreamerTilesAsync();
            if (this.StreamerBar.SelectedIndex < 0 && this.StreamerTiles.Count > 0)
                this.StreamerBar.SelectedIndex = 0;
            await this.LoadReservationsAsync();
            this.refreshTimer.Start();
        }

        private async Task<bool> WaitForRecorderAsync()
        {
            for (var i = 0; i < 20; i++)
            {
                if (this.DataContext is IRecorder) return true;
                await Task.Delay(200);
            }
            return this.DataContext is IRecorder;
        }

        private async Task BuildStreamerTilesAsync()
        {
            this.StreamerTiles.Clear();
            this.themeByRoom.Clear();
            var rec = this.DataContext as IRecorder;
            var rooms = rec?.Rooms.ToList() ?? new List<IRoom>();
            var accent = this.TryFindResource("MacAccentBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));

            var allBrush = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF8));
            this.StreamerTiles.Add(new StreamerTile("全部", "", null, allBrush, true));

            var avatars = await Task.WhenAll(rooms.Select(room => LoadAvatarAsync(room)));
            for (var i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                var name = room.Name ?? ("房间 " + room.RoomConfig.RoomId);
                var avatar = avatars[i];
                var theme = ResolveTheme(name, avatar);
                this.themeByRoom[CanonicalName(name)] = theme;
                var brush = avatar != null
                    ? new ImageBrush(avatar) { Stretch = Stretch.UniformToFill }
                    : accent;
                this.StreamerTiles.Add(new StreamerTile(name, InitialOf(name), room, brush, false));
            }
        }

        private static string CanonicalName(string? name)
        {
            var value = name ?? "";
            foreach (var target in TargetNames)
                if (value.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0)
                    return target;
            return value;
        }

        private static Color ResolveTheme(string name, BitmapImage? avatar)
        {
            foreach (var pair in KnownThemes)
            {
                if (name.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pair.Value;
            }

            if (avatar != null)
            {
                var sampled = DominantColor(avatar);
                if (sampled != Colors.Transparent) return sampled;
            }

            return Colors.White;
        }

        private static long ResolveUid(IRoom room)
        {
            foreach (var pair in KnownUids)
            {
                if ((room.Name ?? "").IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pair.Value;
            }
            return room.Uid;
        }

        private static Color DominantColor(BitmapImage image)
        {
            try
            {
                var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                var width = converted.PixelWidth;
                var height = converted.PixelHeight;
                if (width < 2 || height < 2) return Colors.Transparent;
                var stride = width * 4;
                var pixels = new byte[stride * height];
                converted.CopyPixels(pixels, stride, 0);

                var step = Math.Max(1, Math.Min(width, height) / 18);
                long r = 0, g = 0, b = 0, count = 0;
                for (var y = 0; y < height; y += step)
                {
                    for (var x = 0; x < width; x += step)
                    {
                        var i = y * stride + x * 4;
                        var bb = pixels[i];
                        var gg = pixels[i + 1];
                        var rr = pixels[i + 2];
                        var max = Math.Max(rr, Math.Max(gg, bb));
                        var min = Math.Min(rr, Math.Min(gg, bb));
                        if (max < 45 || min > 235 || max - min < 28) continue;
                        r += rr;
                        g += gg;
                        b += bb;
                        count++;
                    }
                }
                if (count == 0) return Colors.Transparent;
                return Color.FromRgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            }
            catch
            {
                return Colors.Transparent;
            }
        }

        private static string InitialOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            return name.Substring(0, 1);
        }

        private static async Task<BitmapImage?> LoadAvatarAsync(IRoom room)
        {
            var url = room.RawBilibiliApiJsonData?.SelectToken("user_info.base_info.face")?.Value<string>()
                      ?? room.RawBilibiliApiJsonData?.SelectToken("user_info.face")?.Value<string>()
                      ?? room.RawBilibiliApiJsonData?.SelectToken("room_info.user_info.face")?.Value<string>();
            if (string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    var api = "https://api.live.bilibili.com/live_user/v1/UserInfo/get_anchor_in_room" +
                              "?roomid=" + room.RoomConfig.RoomId;
                    using var response = await http.GetAsync(api);
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    url = json.SelectToken("data.info.face")?.Value<string>();
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(url, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                return image;
            }
            catch
            {
                return null;
            }
        }

        private void BuildWeekGrid()
        {
            this.DayHeader.Children.Clear();
            this.DayHeader.ColumnDefinitions.Clear();
            var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
            for (var i = 0; i < 7; i++)
            {
                this.DayHeader.ColumnDefinitions.Add(new ColumnDefinition());
                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                stack.Children.Add(new TextBlock
                {
                    Text = Days[i],
                    FontSize = 21,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x54)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextAlignment = TextAlignment.Center,
                });
                var date = monday.AddDays(i);
                stack.Children.Add(new TextBlock
                {
                    Text = date.ToString("MM/dd"),
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(date.Date == DateTime.Today
                        ? Color.FromRgb(0x3E, 0x6F, 0xD8)
                        : Color.FromRgb(0x7C, 0x7C, 0x86)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0),
                });
                Grid.SetColumn(stack, i);
                this.DayHeader.Children.Add(stack);
            }

            this.WeekGrid.Children.Clear();
            this.WeekGrid.ColumnDefinitions.Clear();
            this.WeekGrid.RowDefinitions.Clear();
            this.dayPanels.Clear();
            for (var i = 0; i < 7; i++)
                this.WeekGrid.ColumnDefinitions.Add(new ColumnDefinition());
            this.WeekGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            for (var day = 0; day < 7; day++)
            {
                var content = new Grid { Margin = new Thickness(5, 4, 5, 4) };
                var host = new Grid { Margin = new Thickness(3, 0, 3, 0), ClipToBounds = true, Background = Brushes.Transparent };
                host.Children.Add(content);
                Grid.SetColumn(host, day);
                this.WeekGrid.Children.Add(host);
                this.dayPanels[day] = content;
            }
        }

        private void StreamerBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.selectedRoomName = CanonicalName((this.StreamerBar.SelectedItem as StreamerTile)?.Room?.Name);
            this.RenderReservations();
        }

#pragma warning disable VSTHRD100
        private static bool IsCalendarArtist(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOf("A-SOUL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("闪耀舞台", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("枝江", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("贝拉", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("嘉然", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("乃琳", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("心宜", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("思诺", StringComparison.OrdinalIgnoreCase) >= 0;
        }


#pragma warning disable VSTHRD100
        private async void ImportCalendar_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            ScheduleCalendarService.SaveUrl(this.CalendarUrlBox.Text);
            this.CalendarBar.Visibility = Visibility.Collapsed;
            await this.LoadReservationsAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await this.LoadReservationsAsync();
        }

        private async Task LoadReservationsAsync()
        {
            if (this.refreshing) return;
            this.refreshing = true;
            this.LoadingOverlay.Visibility = Visibility.Visible;
            try
            {
                await this.LoadReservationsCoreAsync();
            }
            finally
            {
                this.refreshing = false;
            }
        }

        private async Task LoadReservationsCoreAsync()
        {
            this.reservations.Clear();
            foreach (var panel in this.dayPanels.Values)
            {
                panel.Children.Clear();
                panel.RowDefinitions.Clear();
            }
            if (this.StreamerBar.SelectedIndex < 0 && this.StreamerTiles.Count > 0)
                this.StreamerBar.SelectedIndex = 0;

            try
            {
                var rec = this.DataContext as IRecorder;
                var rooms = rec?.Rooms.ToList() ?? new List<IRoom>();
                var hasCalendarArtists = rooms.Any(r => IsCalendarArtist(r.Name));
                this.CalendarBar.Visibility = hasCalendarArtists &&
                    string.IsNullOrWhiteSpace(ScheduleCalendarService.Url)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                if (rooms.Count == 0)
                {
                    this.ShowFailure("还没有添加主播，请先在首页添加直播间。");
                    return;
                }

                var cookie = UserSessionStore.Load()?.Cookie ?? "";
                if (string.IsNullOrWhiteSpace(buvidCookie))
                {
                    try
                    {
                        using var finger = await http.GetAsync("https://api.bilibili.com/x/frontend/finger/spi");
                        var fj = JObject.Parse(await finger.Content.ReadAsStringAsync());
                        var b3 = fj.SelectToken("data.b_3")?.Value<string>() ?? "";
                        var b4 = fj.SelectToken("data.b_4")?.Value<string>() ?? "";
                        if (b3.Length > 0)
                            buvidCookie = $"buvid3={b3}; buvid4={b4}; b_nut={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    }
                    catch { }
                }
                var requestCookie = string.Join("; ", new[] { cookie, buvidCookie }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                var errors = 0;
                var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
                var end = monday.AddDays(7);

                foreach (var room in rooms)
                {
                    if (TargetNames.Any(n => (room.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue; // 这五位只使用日历

                    var uid = ResolveUid(room);
                    if (uid <= 0) continue;
                    try
                    {
                        var url = "https://api.bilibili.com/x/polymer/web-dynamic/v1/feed/space" +
                                  $"?host_mid={uid}&timezone_offset=-480&platform=web" +
                                  "&features=itemOpusStyle,listOnlyfans,opusBigCover,onlyfansVote,decorationCard,onlyfansAssetsV2,ugcDelete,onlyfansQaCard";
                        JObject? json = null;
                        for (var attempt = 0; attempt < 3; attempt++)
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, url);
                            request.Headers.Referrer = new Uri("https://space.bilibili.com/" + uid);
                            if (!string.IsNullOrWhiteSpace(requestCookie))
                                request.Headers.TryAddWithoutValidation("Cookie", requestCookie);
                            using var response = await http.SendAsync(request, CancellationToken.None);
                            json = JObject.Parse(await response.Content.ReadAsStringAsync());
                            if (json.Value<int?>("code") == 0) break;
                            if (attempt < 2) await Task.Delay(700);
                        }
                        if (json == null || json.Value<int?>("code") != 0)
                        {
                            errors++;
                            continue;
                        }

                        foreach (var item in json.SelectToken("data.items") as JArray ?? new JArray())
                        {
                            try
                            {
                                var postText = item.SelectToken("modules.module_dynamic.desc.text")?.Value<string>()
                                               ?? item.SelectToken("modules.module_dynamic.major.opus.summary.text")?.Value<string>()
                                               ?? "";
                                var additional = item.SelectToken("modules.module_dynamic.additional");
                                if (!string.Equals(additional?.Value<string>("type"), "ADDITIONAL_TYPE_RESERVE",
                                        StringComparison.Ordinal))
                                {
                                    this.AddDynamicSchedules(postText, room, monday, end);
                                    continue;
                                }
                                var reserve = additional?.SelectToken("reserve");
                                if (reserve == null) continue;

                                var stime = reserve.Value<long?>("stime") ?? 0;
                                DateTime start;
                                if (stime > 0)
                                {
                                    start = DateTimeOffset.FromUnixTimeSeconds(stime).LocalDateTime;
                                }
                                else if (!TryParseReserveStart(reserve.SelectToken("desc1.text")?.Value<string>() ?? "",
                                             out start))
                                {
                                    continue;
                                }
                                if (start < monday || start >= end) continue;

                                var title = reserve.Value<string>("title")
                                            ?? reserve.SelectToken("desc1.text")?.Value<string>()
                                            ?? "直播预约";
                                var roomName = CanonicalName(room.Name);
                                var key = ScheduleSkipPolicy.Key(roomName, start);
                                var theme = this.themeByRoom.TryGetValue(roomName, out var color)
                                    ? color
                                    : Colors.White;
                                this.reservations.Add(new ReservationItem(
                                    start, roomName, title, key, theme, this.skippedKeys.Contains(key)));
                            }
                            catch
                            {
                                // 单条预约格式异常不影响其他人。
                            }
                        }

                    }
                    catch
                    {
                        errors++;
                    }
                }

                // 同一主播、同一开始时间只保留一条，防止重复预约动态。

                var calendar = await ScheduleCalendarService.GetAsync(monday, end);
                foreach (var item in calendar)
                {
                    var name = CanonicalName(item.RoomName);
                    var key = ScheduleSkipPolicy.Key(name, item.Start);
                    var theme = this.themeByRoom.TryGetValue(name, out var color) ? color : ResolveTheme(name, null);
                    this.reservations.Add(new ReservationItem(
                        item.Start, name, item.Title, key, theme, this.skippedKeys.Contains(key),
                        cancelled: item.Title.IndexOf("取消", StringComparison.Ordinal) >= 0,
                        isZhijiang: false, rawText: item.Raw));
                }

                var others = rooms.Where(r => !TargetNames.Any(n =>
                    (r.Name ?? "").IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                var weibo = await WeiboScheduleService.GetAsync(others, monday, end);
                foreach (var item in weibo)
                {
                    var name = CanonicalName(item.RoomName);
                    var key = ScheduleSkipPolicy.Key(name, item.Start);
                    var theme = this.themeByRoom.TryGetValue(name, out var color) ? color : ResolveTheme(name, null);
                    this.reservations.Add(new ReservationItem(
                        item.Start, name, item.Title, key, theme, this.skippedKeys.Contains(key),
                        cancelled: false, isZhijiang: false, rawText: item.Raw));
                }

                var seenReservations = new HashSet<string>(StringComparer.Ordinal);
                this.reservations.RemoveAll(r =>
                    !seenReservations.Add(CanonicalName(r.RoomName) + "|" + r.Start.ToString("yyyy-MM-dd HH:mm")));

                this.ApplyStableSchedule(monday, end);
                this.RenderReservations();
                this.LoadingOverlay.Visibility = Visibility.Collapsed;

                if (this.reservations.Count == 0 && errors > 0)
                    this.ShowFailure($"读取预约失败：{errors} 位主播未返回数据。");
                else
                    this.lastFailureMessage = null;
            }
            catch (Exception ex)
            {
                this.ShowFailure("读取预约失败：" + ex.Message);
            }
        }

        private void ApplyStableSchedule(DateTime monday, DateTime end, bool allowCancel = true)
        {
            if (this.reservations.Count == 0) return;

            List<ScheduleSnapshotEntry> previous;
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Gekota", "schedule_snapshot.json");
                previous = File.Exists(path)
                    ? JsonConvert.DeserializeObject<List<ScheduleSnapshotEntry>>(File.ReadAllText(path)) ??
                      new List<ScheduleSnapshotEntry>()
                    : new List<ScheduleSnapshotEntry>();
            }
            catch
            {
                previous = new List<ScheduleSnapshotEntry>();
            }

            var fresh = this.reservations.ToList();
            var merged = new List<ReservationItem>();
            foreach (var old in previous.Where(p => p.Start >= monday && p.Start < end))
            {
                var roomName = CanonicalName(old.RoomName);
                // A-SOUL / 闪耀舞台 只认日历，不再沿用旧的 B 站动态快照。
                if (TargetNames.Any(n => roomName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                var candidate = fresh
                    .Where(r => CanonicalName(r.RoomName) == roomName && r.Start.Date == old.Start.Date)
                    .OrderBy(r => Math.Abs((r.Start - old.Start).TotalMinutes))
                    .FirstOrDefault();
                if (candidate != null)
                {
                    candidate.Cancelled = false;
                    merged.Add(candidate);
                    fresh.Remove(candidate);
                }
                else
                {
                    var theme = this.themeByRoom.TryGetValue(roomName, out var known)
                        ? known
                        : ResolveTheme(roomName, null);
                    var key = ScheduleSkipPolicy.Key(roomName, old.Start);
                    merged.Add(new ReservationItem(old.Start, roomName, old.Title, key, theme,
                        this.skippedKeys.Contains(key),
                        cancelled: allowCancel ? true : old.Cancelled,
                        isZhijiang: old.IsZhijiang, rawText: old.RawText));
                }
            }
            merged.AddRange(fresh);
            this.reservations.Clear();
            this.reservations.AddRange(merged);

            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Gekota", "schedule_snapshot.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var snapshot = this.reservations.Select(r => new ScheduleSnapshotEntry
                {
                    RoomName = CanonicalName(r.RoomName),
                    Start = r.Start,
                    Title = r.Title,
                    RawText = r.RawText,
                    IsZhijiang = r.IsZhijiang,
                    Cancelled = r.Cancelled,
                }).ToList();
                File.WriteAllText(path, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
            }
            catch { }
        }

        private sealed class ScheduleSnapshotEntry
        {
            public string RoomName { get; set; } = "";
            public DateTime Start { get; set; }
            public string Title { get; set; } = "";
            public string RawText { get; set; } = "";
            public bool IsZhijiang { get; set; }
            public bool Cancelled { get; set; }
        }

        private void ShowFailure(string message)
        {
            if (string.Equals(message, this.lastFailureMessage, StringComparison.Ordinal)) return;
            this.lastFailureMessage = message;
            MessageBox.Show(message, "日程表", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void AddDynamicSchedules(string text, IRoom room, DateTime monday, DateTime end)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var roomName = CanonicalName(room.Name);
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryParseDynamicStart(line, out var start)) continue;
                if (start < monday || start >= end) continue;
                var titleMatch = Regex.Match(line, @"[\u3010\[]([^\u3011\]]+)[\u3011\]]");
                var title = titleMatch.Success ? titleMatch.Groups[1].Value : "\u76f4\u64ad";
                var key = ScheduleSkipPolicy.Key(roomName, start);
                var theme = this.themeByRoom.TryGetValue(roomName, out var color)
                    ? color
                    : Colors.White;
                this.reservations.Add(new ReservationItem(
                    start, roomName, title, key, theme, this.skippedKeys.Contains(key),
                    cancelled: false, isZhijiang: false, rawText: line));
            }
        }

        private static bool TryParseDynamicStart(string line, out DateTime start)
        {
            start = default;
            if (string.IsNullOrWhiteSpace(line)) return false;
            var date = Regex.Match(line, @"(?:(\d{1,2})\u6708(\d{1,2})\u65e5|(\d{1,2})[-/](\d{1,2}))");
            var relative = Regex.Match(line, @"(\u4eca\u5929|\u660e\u5929|\u540e\u5929)");
            if (!date.Success && !relative.Success) return false;

            var time = Regex.Match(line, @"(\d{1,2})[：:](\d{2})");
            var hour = time.Success && int.TryParse(time.Groups[1].Value, out var h) ? h : 20;
            var minute = time.Success && int.TryParse(time.Groups[2].Value, out var m) ? m : 0;
            if (hour > 23 || minute > 59) return false;

            try
            {
                if (relative.Success)
                {
                    var offset = relative.Groups[1].Value == "\u660e\u5929" ? 1 :
                        relative.Groups[1].Value == "\u540e\u5929" ? 2 : 0;
                    start = DateTime.Today.AddDays(offset).AddHours(hour).AddMinutes(minute);
                    return true;
                }

                var month = int.TryParse(date.Groups[1].Value, out var mm) ? mm :
                    int.TryParse(date.Groups[3].Value, out var mm2) ? mm2 : 0;
                var day = int.TryParse(date.Groups[2].Value, out var dd) ? dd :
                    int.TryParse(date.Groups[4].Value, out var dd2) ? dd2 : 0;
                if (month < 1 || month > 12 || day < 1 || day > 31) return false;
                start = new DateTime(DateTime.Today.Year, month, day, hour, minute, 0);
                if (start < DateTime.Today.AddMonths(-6)) start = start.AddYears(1);
                else if (start > DateTime.Today.AddMonths(6)) start = start.AddYears(-1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseReserveStart(string text, out DateTime start)
        {
            start = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();
            var timeMatch = Regex.Match(text, @"(\d{1,2}):(\d{2})");
            if (!timeMatch.Success) return false;
            if (!int.TryParse(timeMatch.Groups[1].Value, out var hour) ||
                !int.TryParse(timeMatch.Groups[2].Value, out var minute) ||
                hour > 23 || minute > 59) return false;

            var relativeMatch = Regex.Match(text, @"(今天|今日|明天|明日|后天|昨天)");
            if (relativeMatch.Success)
            {
                var offset = relativeMatch.Groups[1].Value switch
                {
                    "明天" or "明日" => 1,
                    "后天" => 2,
                    "昨天" => -1,
                    _ => 0,
                };
                start = DateTime.Today.AddDays(offset).AddHours(hour).AddMinutes(minute);
                return true;
            }

            var match = Regex.Match(text, @"(\d{1,2})-(\d{1,2})");
            if (!match.Success)
                match = Regex.Match(text, @"(\d{1,2})月(\d{1,2})日");
            if (!match.Success) return false;

            if (!int.TryParse(match.Groups[1].Value, out var month) ||
                !int.TryParse(match.Groups[2].Value, out var day)) return false;
            if (month < 1 || month > 12 || day < 1 || day > 31) return false;

            try
            {
                start = new DateTime(DateTime.Today.Year, month, day, hour, minute, 0);
                if (start < DateTime.Today.AddMonths(-6)) start = start.AddYears(1);
                else if (start > DateTime.Today.AddMonths(6)) start = start.AddYears(-1);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void LoadSkips()
        {
            try
            {
                if (File.Exists(this.skipPath))
                {
                    var values = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(this.skipPath));
                    if (values != null)
                        foreach (var value in values.Where(x => !string.IsNullOrWhiteSpace(x)))
                            this.skippedKeys.Add(value);
                }
            }
            catch { }
            ScheduleSkipPolicy.Load(this.skippedKeys);
        }

        private void SaveSkips()
        {
            try
            {
                var dir = Path.GetDirectoryName(this.skipPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(this.skipPath, JsonConvert.SerializeObject(this.skippedKeys));
            }
            catch { }
            ScheduleSkipPolicy.Load(this.skippedKeys);
        }

        private void ToggleSkip(ReservationItem reservation)
        {
            var willSkip = !reservation.Skipped;
            if (reservation.Skipped)
                this.skippedKeys.Remove(reservation.Key);
            else
                this.skippedKeys.Add(reservation.Key);
            reservation.Skipped = willSkip;
            this.SaveSkips();

            if (willSkip && reservation.Start.Date == DateTime.Today)
            {
                var room = (this.DataContext as IRecorder)?.Rooms
                    .FirstOrDefault(r => CanonicalName(r.Name) == reservation.RoomName);
                if (room?.Recording == true) room.StopRecord();
            }

            this.RenderReservations();
        }

        private static Color Blend(Color a, Color b, double bWeight)
        {
            bWeight = Math.Max(0, Math.Min(1, bWeight));
            var aWeight = 1 - bWeight;
            return Color.FromRgb(
                (byte)(a.R * aWeight + b.R * bWeight),
                (byte)(a.G * aWeight + b.G * bWeight),
                (byte)(a.B * aWeight + b.B * bWeight));
        }

        private void RenderReservations()
        {
            foreach (var panel in this.dayPanels.Values)
            {
                panel.Children.Clear();
                panel.RowDefinitions.Clear();
            }

            var filtered = this.reservations
                .Where(r => string.IsNullOrEmpty(this.selectedRoomName)
                            || string.Equals(CanonicalName(r.RoomName), this.selectedRoomName, StringComparison.Ordinal))
                .OrderBy(r => r.Start)
                .ToList();

            for (var day = 0; day < 7; day++)
            {
                if (!this.dayPanels.TryGetValue(day, out var panel)) continue;
                var dayDate = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7)).AddDays(day);
                var items = filtered
                    .Where(r => r.Start.Date == dayDate.Date)
                    .OrderBy(r => r.Start)
                    .ToList();
                for (var row = 0; row < items.Count; row++)
                {
                    panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var block = this.CreateEventBlock(items[row]);
                    block.HorizontalAlignment = HorizontalAlignment.Stretch;
                    Grid.SetRow(block, row);
                    panel.Children.Add(block);
                }
            }
        }

        private static bool IsLight(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 165;
        }

        private static (string Category, string Title) SplitCategoryTitle(string title)
        {
            var value = (title ?? "").Trim();
            var match = Regex.Match(value, @"^[\s]*[【\[]([^】\]]+)[】\]]\s*(.*)$");
            if (!match.Success) return ("", value);
            var rest = match.Groups[2].Value.Trim();
            return (match.Groups[1].Value.Trim(), string.IsNullOrWhiteSpace(rest) ? match.Groups[1].Value.Trim() : rest);
        }

        private Border CreateEventBlock(ReservationItem reservation)
        {
            var displayName = reservation.RoomName;
            var theme = reservation.ThemeColor;
            var ended = reservation.Start < DateTime.Now;
            var muted = reservation.Cancelled || ended;
            var (category, title) = SplitCategoryTitle(reservation.Title);
            var topColor = muted ? Color.FromRgb(0xB8, 0xB8, 0xC0) : theme;
            var tagColor = muted ? Color.FromRgb(0x8E, 0x90, 0x98) : Blend(theme, Colors.Black, 0.34);
            var bottomColor = muted ? Color.FromRgb(0xEF, 0xEF, 0xF2) : Blend(Colors.White, theme, 0.08);
            var topText = muted ? Brushes.White : (IsLight(theme) ? new SolidColorBrush(Color.FromRgb(0x2F, 0x31, 0x39)) : Brushes.White);

            var top = new Grid { Margin = new Thickness(10, 9, 10, 8) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition());
            if (!string.IsNullOrWhiteSpace(category))
            {
                var tag = new Border
                {
                    Background = new SolidColorBrush(tagColor),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(9, 4, 9, 4),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = category,
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                    },
                };
                top.Children.Add(tag);
            }

            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock
            {
                Text = reservation.Start.ToString("HH:mm"),
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Foreground = topText,
                HorizontalAlignment = HorizontalAlignment.Right,
            });
            right.Children.Add(new TextBlock
            {
                Text = displayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = topText,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
            });
            Grid.SetColumn(right, 1);
            top.Children.Add(right);

            var bottom = new StackPanel { Margin = new Thickness(10, 8, 10, 9) };
            bottom.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                LineHeight = 17,
                Foreground = new SolidColorBrush(muted ? Color.FromRgb(0x8A, 0x8B, 0x92) : Color.FromRgb(0x45, 0x47, 0x50)),
                TextWrapping = TextWrapping.Wrap,
            });
            if (muted)
            {
                bottom.Children.Add(new TextBlock
                {
                    Text = ended ? "已结束" : "已取消",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8B, 0x92)),
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var topHost = new Border { Background = new SolidColorBrush(topColor), CornerRadius = new CornerRadius(14, 14, 0, 0), Child = top };
            var bottomHost = new Border { Background = new SolidColorBrush(bottomColor), CornerRadius = new CornerRadius(0, 0, 14, 14), Child = bottom };
            Grid.SetRow(bottomHost, 1);
            content.Children.Add(topHost);
            content.Children.Add(bottomHost);

            var root = new Grid();
            root.Children.Add(content);
            var toggle = this.CreateSkipButton(reservation);
            if (!muted)
            {
                root.Children.Add(toggle);
                toggle.HorizontalAlignment = HorizontalAlignment.Center;
                toggle.VerticalAlignment = VerticalAlignment.Center;
                toggle.Margin = new Thickness(0);
            }

            var block = new Border
            {
                Background = new SolidColorBrush(topColor),
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = reservation.Title,
                Child = root,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 9, ShadowDepth = 2, Opacity = 0.06, Color = Colors.Black,
                },
            };
            if (!reservation.Skipped && !muted)
            {
                block.MouseEnter += (s, e) => toggle.Visibility = Visibility.Visible;
                block.MouseLeave += (s, e) => toggle.Visibility = Visibility.Collapsed;
            }
            return block;
        }

        private Button CreateSkipButton(ReservationItem reservation)
        {
            var button = new Button
            {
                Content = reservation.Skipped ? "已跳过" : "跳过",
                Style = this.TryFindResource("ScheduleSkipButtonStyle") as Style,
                MinWidth = 82,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = reservation.Skipped ? Visibility.Visible : Visibility.Collapsed,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 14,
                    ShadowDepth = 2,
                    Opacity = 0.22,
                    Color = Colors.Black,
                },
            };
            Panel.SetZIndex(button, 2);
            button.Click += (s, e) =>
            {
                e.Handled = true;
                this.ToggleSkip(reservation);
            };
            return button;
        }

        private Border CreateMultiBlock(IReadOnlyList<ReservationItem> group)
        {
            var first = group[0];
            var theme = first.ThemeColor;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = "当前时段有多个日程",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x3D)),
                TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = "请点击查看",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x6D, 0x77)),
            });
            var block = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(9, 8, 9, 8),
                Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand,
                Child = text,
            };
            block.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                this.ShowGroupWindow(group);
            };
            return block;
        }

        private void ShowGroupWindow(IReadOnlyList<ReservationItem> group)
        {
            var window = new Window
            {
                Owner = Window.GetWindow(this),
                Title = "日程详情",
                Width = 440,
                SizeToContent = SizeToContent.Height,
                MaxHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF7)),
            };
            var stack = new StackPanel { Margin = new Thickness(16) };
            foreach (var reservation in group.OrderBy(r => r.Start))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                var info = new StackPanel();
                info.Children.Add(new TextBlock
                {
                    Text = $"{reservation.Start:HH:mm}  {reservation.RoomName}",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x33, 0x3D)),
                });
                info.Children.Add(new TextBlock
                {
                    Text = reservation.Title,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x58, 0x62)),
                    TextWrapping = TextWrapping.Wrap,
                });
                row.Children.Add(info);

                var toggle = this.CreateSkipButton(reservation);
                toggle.Visibility = Visibility.Collapsed;
                toggle.Click += (s, e) => window.Close();
                row.Children.Add(toggle);
                var rowBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x66, reservation.ThemeColor.R,
                        reservation.ThemeColor.G, reservation.ThemeColor.B)),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(10),
                    Child = row,
                };
                rowBorder.MouseEnter += (s, e) => toggle.Visibility = Visibility.Visible;
                rowBorder.MouseLeave += (s, e) => toggle.Visibility = Visibility.Collapsed;
                stack.Children.Add(rowBorder);
            }
            window.Content = new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            window.ShowDialog();
        }

        public sealed class StreamerTile
        {
            public StreamerTile(string name, string initial, IRoom? room, Brush tileBrush, bool isAll)
            {
                this.Name = name;
                this.Initial = initial;
                this.Room = room;
                this.TileBrush = tileBrush;
                this.IsAll = isAll;
                this.HasAvatar = !isAll && tileBrush is ImageBrush;
            }

            public string Name { get; }
            public string Initial { get; }
            public IRoom? Room { get; }
            public Brush TileBrush { get; }
            public bool IsAll { get; }
            public bool HasAvatar { get; }
        }

        private sealed class ReservationItem
        {
            public ReservationItem(DateTime start, string roomName, string title, string key,
                Color themeColor, bool skipped, bool cancelled = false,
                bool isZhijiang = false, string rawText = "")
            {
                this.Start = start;
                this.RoomName = roomName;
                this.Title = title;
                this.Key = key;
                this.ThemeColor = themeColor;
                this.Skipped = skipped;
                this.Cancelled = cancelled;
                this.IsZhijiang = isZhijiang;
                this.RawText = rawText;
            }

            public DateTime Start { get; }
            public string RoomName { get; }
            public string Title { get; }
            public string Key { get; }
            public Color ThemeColor { get; }
            public bool Skipped { get; set; }
            public bool Cancelled { get; set; }
            public bool IsZhijiang { get; }
            public string RawText { get; }
        }
    }
}

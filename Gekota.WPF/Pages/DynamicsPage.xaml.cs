using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gekota.Core;
using Gekota.WPF.Services;
using Newtonsoft.Json.Linq;

#nullable enable
namespace Gekota.WPF.Pages
{
    public partial class DynamicsPage
    {
        private static readonly HttpClient http = CreateHttpClient();
        private static readonly Dictionary<string, ImageSource?> imageCache = new(StringComparer.Ordinal);
        private static string buvidCookie = "";
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x9B, 0x7B, 0xFF),
            Color.FromRgb(0xFF, 0x9E, 0xB8),
            Color.FromRgb(0x8F, 0xA8, 0xFF),
            Color.FromRgb(0xFF, 0x8F, 0xAB),
            Color.FromRgb(0x8B, 0x7B, 0xEB),
        };

        private static readonly Dictionary<string, long> KnownUids = new(StringComparer.OrdinalIgnoreCase)
        {
            ["贝拉"] = 672353429,
            ["嘉然"] = 672328094,
            ["乃琳"] = 672342685,
            ["心宜"] = 3537115310721181,
            ["思诺"] = 3537115310721781,
        };

        private readonly ObservableCollection<DynamicItem> allItems = new();
        private readonly ObservableCollection<DynamicItem> visibleItems = new();
        private readonly ObservableCollection<StreamerTile> streamerTiles = new();
        private readonly Dictionary<string, Color> themeByRoom = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Brush> avatarByRoom = new(StringComparer.Ordinal);
        private readonly DispatcherTimer refreshTimer;
        private string? selectedRoomName;
        private bool refreshing;
        private bool initialized;
        private bool loadingImages;
        private int lastErrors;
        private INotifyCollectionChanged? subscribedRooms;

        public DynamicsPage()
        {
            this.InitializeComponent();
            this.StreamerBar.ItemsSource = this.streamerTiles;
            this.FeedList.ItemsSource = this.visibleItems;
            this.refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            this.refreshTimer.Tick += async (s, e) => await this.LoadAsync();
            this.Loaded += this.DynamicsPage_Loaded;
            this.DataContextChanged += (s, e) => this.SubscribeRooms();
            this.Unloaded += (s, e) => this.refreshTimer.Stop();
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                         System.Net.DecompressionMethods.Deflate,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(18) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0.0.0 Safari/537.36");
            return client;
        }

#pragma warning disable VSTHRD100
        private async void DynamicsPage_Loaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (this.initialized)
            {
                this.refreshTimer.Start();
                return;
            }
            this.initialized = true;
            this.SubscribeRooms();
            await this.BuildStreamerTilesAsync();
            if (this.StreamerBar.SelectedIndex < 0 && this.streamerTiles.Count > 0)
                this.StreamerBar.SelectedIndex = 0;
            await this.LoadAsync();
            this.refreshTimer.Start();
        }

        private void SubscribeRooms()
        {
            if (this.DataContext is not IRecorder recorder) return;
            var rooms = (INotifyCollectionChanged)recorder.Rooms;
            if (ReferenceEquals(this.subscribedRooms, rooms)) return;
            if (this.subscribedRooms != null)
                this.subscribedRooms.CollectionChanged -= this.Rooms_CollectionChanged;
            this.subscribedRooms = rooms;
            rooms.CollectionChanged += this.Rooms_CollectionChanged;
        }

        private async void Rooms_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!this.initialized) return;
            await this.BuildStreamerTilesAsync();
            if (this.StreamerBar.SelectedIndex < 0 && this.streamerTiles.Count > 0)
                this.StreamerBar.SelectedIndex = 0;
            await this.LoadAsync();
        }

        private async Task BuildStreamerTilesAsync()
        {
            this.streamerTiles.Clear();
            this.themeByRoom.Clear();
            this.avatarByRoom.Clear();
            var rec = this.DataContext as IRecorder;
            var rooms = rec?.Rooms.ToList() ?? new List<IRoom>();
            var accent = this.TryFindResource("MacAccentBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));
            this.streamerTiles.Add(new StreamerTile("全部", "", null,
                new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF8)), true));

            var avatars = await Task.WhenAll(rooms.Select(room => this.LoadAvatarAsync(room)));
            for (var i = 0; i < rooms.Count; i++)
            {
                var room = rooms[i];
                var name = room.Name ?? ("房间 " + room.RoomConfig.RoomId);
                var avatar = avatars[i];
                this.themeByRoom[name] = PickTheme(name);
                var brush = avatar != null
                    ? new ImageBrush(avatar) { Stretch = Stretch.UniformToFill }
                    : accent;
                this.avatarByRoom[name] = brush;
                this.streamerTiles.Add(new StreamerTile(name, InitialOf(name), room, brush, false));
            }
        }

        private static string InitialOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            return name.Substring(0, 1);
        }

        private static JToken? GetToken(JToken? token, params string[] path)
        {
            foreach (var part in path)
            {
                if (token is not JObject obj) return null;
                token = obj[part];
            }
            return token;
        }

        private static string? GetString(JToken? token, params string[] path)
            => GetToken(token, path)?.ToString();

        private static long ResolveUid(IRoom room)
        {
            foreach (var pair in KnownUids)
            {
                if ((room.Name ?? "").IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return pair.Value;
            }
            return room.Uid;
        }

        private static Color PickTheme(string name)
        {
            if (name.IndexOf("贝拉", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromRgb(0xDB, 0x7D, 0x74);
            if (name.IndexOf("嘉然", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromRgb(0xE7, 0x99, 0xB0);
            if (name.IndexOf("乃琳", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromRgb(0x57, 0x66, 0x90);
            if (name.IndexOf("心宜", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromRgb(0xC9, 0x37, 0x73);
            if (name.IndexOf("思诺", StringComparison.OrdinalIgnoreCase) >= 0) return Color.FromRgb(0x72, 0x52, 0xC0);
            var index = Math.Abs((name ?? "").GetHashCode()) % Palette.Length;
            return Palette[index];
        }

        private async Task<BitmapImage?> LoadAvatarAsync(IRoom room)
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

        private void StreamerBar_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            this.selectedRoomName = (this.StreamerBar.SelectedItem as StreamerTile)?.Room?.Name;
            this.ApplyFilter();
        }

#pragma warning disable VSTHRD100
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await this.LoadAsync();
        }

        private void ApplyFilter()
        {
            this.visibleItems.Clear();
            foreach (var item in this.allItems
                         .Where(x => string.IsNullOrEmpty(this.selectedRoomName)
                                     || string.Equals(x.RoomName, this.selectedRoomName, StringComparison.Ordinal))
                         .OrderByDescending(x => x.PubTs)
                         .Take(60))
            {
                this.visibleItems.Add(item);
            }
            this.StatusText.Text = this.visibleItems.Count == 0
                ? "暂时没有动态" + (this.lastErrors > 0 ? $"（{this.lastErrors} 位读取失败）" : "")
                : $"{this.visibleItems.Count} 条动态" + (this.lastErrors > 0 ? $"（{this.lastErrors} 位读取失败）" : "");
        }

        private static async Task<ImageSource?> LoadImageAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url.Substring(7);
            lock (imageCache)
            {
                if (imageCache.TryGetValue(url, out var cached)) return cached;
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri("https://www.bilibili.com/");
                using var response = await http.SendAsync(request);
                if (!response.IsSuccessStatusCode) return null;
                var bytes = await response.Content.ReadAsByteArrayAsync();
                using var stream = new MemoryStream(bytes);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = stream;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 900;
                bitmap.EndInit();
                bitmap.Freeze();
                lock (imageCache) imageCache[url] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (url.StartsWith("//", StringComparison.Ordinal)) return "https:" + url;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                return "https://" + url.Substring(7);
            return url;
        }

        private void FeedItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: DynamicItem item }) return;
            if (string.IsNullOrWhiteSpace(item.Link)) return;
            try
            {
                Process.Start(new ProcessStartInfo(item.Link) { UseShellExecute = true });
            }
            catch { }
        }

        private int wheelAccumulator;

        private void FeedScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer viewer || e.Handled) return;
            if (viewer.ScrollableHeight <= 0) return;
            if (this.FeedList.Items.Count == 0) return;

            this.wheelAccumulator += e.Delta;
            if (Math.Abs(this.wheelAccumulator) < 120)
            {
                e.Handled = true;
                return;
            }

            var wheelDirection = Math.Sign(this.wheelAccumulator);
            this.wheelAccumulator -= wheelDirection * 120;
            var direction = wheelDirection > 0 ? -1 : 1;

            var tops = new List<double>();
            for (var i = 0; i < this.FeedList.Items.Count; i++)
            {
                if (this.FeedList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                    continue;
                try
                {
                    var point = container.TransformToAncestor(viewer).Transform(new Point(0, 0));
                    tops.Add(point.Y + viewer.VerticalOffset);
                }
                catch { }
            }
            if (tops.Count == 0) return;

            var current = -1;
            for (var i = 0; i < tops.Count; i++)
            {
                if (tops[i] <= viewer.VerticalOffset + 4) current = i;
                else break;
            }

            var target = Math.Max(0, Math.Min(tops.Count - 1,
                (current < 0 ? 0 : current) + (direction > 0 ? 1 : -1)));
            viewer.ScrollToVerticalOffset(tops[target]);
            e.Handled = true;
        }

        private void RichText_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock block || block.DataContext is not DynamicItem item) return;
            block.Inlines.Clear();
            foreach (var part in item.Parts)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    block.Inlines.Add(new Run(part.Text));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(part.EmojiUrl)) continue;
                var image = new Image
                {
                    Width = 22,
                    Height = 22,
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(1, 0, 1, 0),
                };
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(part.EmojiUrl, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    image.Source = bitmap;
                }
                catch { }
                block.Inlines.Add(new InlineUIContainer(image)
                {
                    BaselineAlignment = BaselineAlignment.Center,
                });
            }
            if (block.Inlines.Count == 0)
                block.Inlines.Add(new Run(item.Text));
        }

        private async Task LoadImagesAsync()
        {
            if (this.loadingImages) return;
            this.loadingImages = true;
            try
            {
                var pending = this.allItems
                    .Where(x => x.ImageSource == null && !string.IsNullOrWhiteSpace(x.ImageUrl))
                    .ToList();
                using var gate = new System.Threading.SemaphoreSlim(4);
                var tasks = pending.Select(async item =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var source = await LoadImageAsync(item.ImageUrl);
                        if (source != null) item.SetImage(source);
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks);
            }
            finally
            {
                this.loadingImages = false;
            }
        }

        private async Task LoadAsync()
        {
            if (this.refreshing) return;
            this.refreshing = true;
            try
            {
                this.StatusText.Text = "正在读取…";
                var rec = this.DataContext as IRecorder;
                var rooms = rec?.Rooms.ToList() ?? new List<IRoom>();
                if (rooms.Count == 0)
                {
                    this.StatusText.Text = "还没有添加主播";
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

                var results = new List<DynamicItem>();
                var errors = 0;
                foreach (var room in rooms)
                {
                    var uid = ResolveUid(room);
                    if (uid <= 0) continue;
                    try
                    {
                        await Task.Delay(180);
                        var roomName = room.Name ?? "主播";
                        var avatarBrush = this.avatarByRoom.TryGetValue(roomName, out var cachedBrush)
                            ? cachedBrush
                            : new SolidColorBrush(PickTheme(roomName));
                        var hasAvatar = avatarBrush is ImageBrush;

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
                            var author = GetString(item, "modules", "module_author", "name")
                                         ?? room.Name ?? "主播";
                            var tsToken = GetToken(item, "modules", "module_author", "pub_ts");
                            long ts = 0;
                            long.TryParse(tsToken?.ToString(), out ts);
                            var desc = GetToken(item, "modules", "module_dynamic", "desc");
                            var opus = GetToken(item, "modules", "module_dynamic", "major", "opus");
                            var summary = GetToken(opus, "summary");
                            var nodes = GetToken(desc, "rich_text_nodes") as JArray
                                        ?? GetToken(summary, "rich_text_nodes") as JArray;

                            var text = GetString(desc, "text")
                                       ?? GetString(summary, "text")
                                       ?? GetString(item, "modules", "module_dynamic", "major", "archive", "title")
                                       ?? GetString(opus, "title")
                                       ?? GetString(item, "modules", "module_dynamic", "major", "article", "title")
                                       ?? GetString(item, "modules", "module_dynamic", "major", "draw", "items", "description")
                                       ?? GetString(item, "modules", "module_dynamic", "additional", "reserve", "title")
                                       ?? GetString(item, "modules", "module_dynamic", "topic", "name")
                                       ?? "";

                            var emojis = new List<string>();
                            var parts = new List<RichPart>();
                            var emojiTokens = new HashSet<string>(StringComparer.Ordinal);
                            if (nodes != null)
                            {
                                foreach (var node in nodes)
                                {
                                    if (!string.Equals(node.Value<string>("type"), "RICH_TEXT_NODE_TYPE_EMOJI",
                                            StringComparison.Ordinal))
                                    {
                                        var value = node.Value<string>("text") ?? "";
                                        if (value.Length > 0) parts.Add(new RichPart { Text = value });
                                        continue;
                                    }

                                    var emoji = node.SelectToken("emoji");
                                    var token = emoji?.Value<string>("text") ?? node.Value<string>("text");
                                    if (!string.IsNullOrWhiteSpace(token)) emojiTokens.Add(token);
                                    var icon = emoji?.Value<string>("icon_url")
                                               ?? emoji?.Value<string>("webp_url")
                                               ?? node.Value<string>("icon_url");
                                        if (!string.IsNullOrWhiteSpace(icon))
                                        {
                                            icon = NormalizeUrl(icon);
                                            parts.Add(new RichPart { EmojiUrl = icon });
                                            if (!emojis.Contains(icon)) emojis.Add(icon);
                                        }
                                }
                                if (string.IsNullOrWhiteSpace(text))
                                    text = string.Join("", nodes
                                        .Where(n => !string.Equals(n.Value<string>("type"),
                                            "RICH_TEXT_NODE_TYPE_EMOJI", StringComparison.Ordinal))
                                        .Select(n => n.Value<string>("text") ?? ""));
                            }
                            foreach (var token in emojiTokens)
                                text = text.Replace(token, "");
                            text = text.Trim();
                            if (string.IsNullOrWhiteSpace(text)) text = "图片动态";
                            if (parts.Count == 0) parts.Add(new RichPart { Text = text });

                            var drawItems = GetToken(item, "modules", "module_dynamic", "major",
                                "draw", "items") as JArray;
                            var opusPics = GetToken(opus, "pics") as JArray;
                            var imageUrl = drawItems?.First?["src"]?.ToString()
                                           ?? opusPics?.First?["url"]?.ToString()
                                           ?? GetString(item, "modules", "module_dynamic", "major", "archive", "cover")
                                           ?? "";
                            if (!string.IsNullOrWhiteSpace(imageUrl))
                                imageUrl = NormalizeUrl(imageUrl);

                            var itemRoomName = room.Name ?? author;
                            var theme = this.themeByRoom.TryGetValue(itemRoomName, out var themed)
                                ? themed
                                : PickTheme(itemRoomName);
                            var link = GetString(opus, "jump_url") ?? "";
                            if (string.IsNullOrWhiteSpace(link))
                            {
                                var id = item.Value<string>("id_str") ?? item.Value<string>("id") ?? "";
                                if (!string.IsNullOrWhiteSpace(id))
                                    link = "https://www.bilibili.com/opus/" + id;
                            }
                            if (link.StartsWith("//", StringComparison.Ordinal))
                                link = "https:" + link;
                            results.Add(new DynamicItem(
                                itemRoomName,
                                InitialOf(author),
                                ts > 0
                                    ? DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("MM-dd HH:mm")
                                    : "",
                                text,
                                imageUrl,
                                null,
                                emojis,
                                parts,
                                avatarBrush,
                                hasAvatar,
                                new SolidColorBrush(Color.FromArgb(0x66, theme.R, theme.G, theme.B)),
                                new SolidColorBrush(Color.FromArgb(0xC0, theme.R, theme.G, theme.B)),
                                link,
                                ts));
                            }
                            catch
                            {
                                // 单条动态格式异常时跳过，不影响同主播其他动态。
                            }
                        }
                    }
                    catch
                    {
                        errors++;
                    }
                    await Task.Delay(280);
                }

                this.lastErrors = errors;
                this.allItems.Clear();
                foreach (var item in results.OrderByDescending(x => x.PubTs))
                    this.allItems.Add(item);
                this.ApplyFilter();
                _ = this.LoadImagesAsync();
            }
            catch (Exception ex)
            {
                this.StatusText.Text = "读取动态失败：" + ex.Message;
            }
            finally
            {
                this.refreshing = false;
            }
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

        public sealed class RichPart
        {
            public string Text { get; set; } = "";
            public string EmojiUrl { get; set; } = "";
        }

        public sealed class DynamicItem : INotifyPropertyChanged
        {
            private ImageSource? imageSource;

            public DynamicItem(string roomName, string initial, string time, string text,
                string imageUrl, ImageSource? imageSource, List<string> emojiUrls, List<RichPart> parts,
                Brush avatarBrush, bool hasAvatar, Brush cardBrush, Brush borderBrush, string link, long pubTs)
            {
                this.RoomName = roomName;
                this.Author = roomName;
                this.Initial = initial;
                this.Time = time;
                this.Text = text;
                this.ImageUrl = imageUrl;
                this.imageSource = imageSource;
                this.EmojiUrls = emojiUrls;
                this.HasEmoji = emojiUrls.Count > 0;
                this.Parts = parts;
                this.AvatarBrush = avatarBrush;
                this.HasAvatar = hasAvatar;
                this.CardBrush = cardBrush;
                this.BorderBrush = borderBrush;
                this.Link = link;
                this.PubTs = pubTs;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string RoomName { get; }
            public string Author { get; }
            public string Initial { get; }
            public string Time { get; }
            public string Text { get; }
            public string ImageUrl { get; }
            public ImageSource? ImageSource => this.imageSource;
            public bool HasImage => this.imageSource != null;
            public List<string> EmojiUrls { get; }
            public bool HasEmoji { get; }
            public List<RichPart> Parts { get; }
            public Brush AvatarBrush { get; }
            public bool HasAvatar { get; }
            public Brush CardBrush { get; }
            public Brush BorderBrush { get; }
            public string Link { get; }
            public long PubTs { get; }

            public void SetImage(ImageSource source)
            {
                this.imageSource = source;
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ImageSource)));
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.HasImage)));
            }
        }
    }
}

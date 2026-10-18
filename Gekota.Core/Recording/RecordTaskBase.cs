using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Gekota.Core.Api;
using Gekota.Core.Config;
using Gekota.Core.Event;
using Gekota.Core.Scripting;
using Gekota.Core.Templating;
using Serilog;
using Timer = System.Timers.Timer;

namespace Gekota.Core.Recording
{
    internal abstract class RecordTaskBase : IRecordTask
    {
        private const string HttpHeaderAccept = "*/*";
        private const string HttpHeaderOrigin = Api.Http.HttpApiClient.HttpHeaderOrigin;
        private const string HttpHeaderReferer = Api.Http.HttpApiClient.HttpHeaderReferer;
        private const string HttpHeaderUserAgent = Api.Http.HttpApiClient.HttpHeaderUserAgent;

        private const int timer_inverval = 2;
        protected readonly Timer timer = new Timer(1000 * timer_inverval);
        protected readonly Random random = new Random();
        protected readonly CancellationTokenSource cts = new CancellationTokenSource();
        protected readonly CancellationToken ct;

        protected readonly IRoom room;
        protected readonly ILogger logger;
        protected readonly IApiClient apiClient;
        private readonly FileNameGenerator fileNameGenerator;
        private readonly UserScriptRunner userScriptRunner;

        private int partIndex = 0;

        protected string? streamHost;
        protected string? streamHostFull;
        protected bool started = false;
        protected bool timeoutTriggered = false;
        protected int qn;

        private readonly object ioStatsLock = new();
        protected int ioNetworkDownloadedBytes;

        protected Stopwatch ioDiskStopwatch = new();
        protected object ioDiskStatsLock = new();
        protected TimeSpan ioDiskWriteDuration;
        protected int ioDiskWrittenBytes;

        private DateTimeOffset ioStatsLastTrigger;
        private TimeSpan durationSinceNoDataReceived;

        protected RecordTaskBase(IRoom room, ILogger logger, IApiClient apiClient, UserScriptRunner userScriptRunner)
        {
            this.room = room ?? throw new ArgumentNullException(nameof(room));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            this.userScriptRunner = userScriptRunner ?? throw new ArgumentNullException(nameof(userScriptRunner));

            this.fileNameGenerator = new FileNameGenerator(room.RoomConfig, logger);
            this.ct = this.cts.Token;

            this.timer.Elapsed += this.Timer_Elapsed_TriggerIOStats;
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        public int Qn => this.qn;

        public bool IsClipTask { get; set; }

        #region Events

        public event EventHandler<IOStatsEventArgs>? IOStats;
        public event EventHandler<RecordingStatsEventArgs>? RecordingStats;
        public event EventHandler<RecordFileOpeningEventArgs>? RecordFileOpening;
        public event EventHandler<RecordFileClosedEventArgs>? RecordFileClosed;
        public event EventHandler? RecordSessionEnded;

        protected void OnIOStats(IOStatsEventArgs e) => IOStats?.Invoke(this, e);
        protected void OnRecordingStats(RecordingStatsEventArgs e) => RecordingStats?.Invoke(this, e);
        protected void OnRecordFileOpening(RecordFileOpeningEventArgs e)
        {
            this.currentMainFilePath = e.FullPath;
            RecordFileOpening?.Invoke(this, e);
        }
        protected void OnRecordFileClosed(RecordFileClosedEventArgs e) => RecordFileClosed?.Invoke(this, e);
        protected void OnRecordSessionEnded(EventArgs e) => RecordSessionEnded?.Invoke(this, e);

        #endregion

        
        // ==================== 裁剪功能 ====================

        private readonly object clipLock = new object();
        protected string? currentMainFilePath;
        private FileStream? clipFileStream;
        private string? clipTempFilePath;
        private DateTimeOffset clipStartTime;

        public event EventHandler<ClipEventArgs>? ClipStarted;
        public event EventHandler<ClipEventArgs>? ClipEnded;

        public bool IsClipping => this.IsClipTask;

        /// <summary>当前录制文件完整路径（含文件名），未开文件时为 null。</summary>
        public string? CurrentFilePath => this.currentMainFilePath;

        /// <summary>
        /// 从直播流读到的字节调用。正在裁剪则直接写入裁剪文件。
        /// </summary>
        protected void BufferForClip(byte[] data, int offset, int count)
        {
            if (count <= 0) return;

            lock (this.clipLock)
            {
                if (this.clipFileStream is not { } fs) return;

                try
                {
                    fs.Write(data, offset, count);
                }
                catch (Exception ex)
                {
                    this.logger.Warning(ex, "写入裁剪文件失败");
                    try { fs.Dispose(); } catch { }
                    this.clipFileStream = null;
                    this.clipTempFilePath = null;
                }
            }
        }

        public void StartClip()
        {
            bool raised = false;
            lock (this.clipLock)
            {
                if (this.clipFileStream != null) return;

                try
                {
                    var mainPath = this.currentMainFilePath;
                    if (string.IsNullOrWhiteSpace(mainPath))
                    {
                        this.logger.Warning("无法开始裁剪：currentMainFilePath 为空");
                        return;
                    }
                    var dir = Path.GetDirectoryName(mainPath);
                    if (string.IsNullOrWhiteSpace(dir)) return;
                    Directory.CreateDirectory(dir);

                    var name = Path.GetFileNameWithoutExtension(mainPath);
                    var now = DateTimeOffset.Now;
                    var startStr = now.ToString("HHmmss");
                    var tempPath = Path.Combine(dir, $"{name}-clip-{startStr}-XXX.flv");

                    var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read);

                    // FLV Header: "FLV" + version 1 + flags(A+V) + dataOffset 9
                    var header = new byte[] { (byte)'F', (byte)'L', (byte)'V', 0x01, 0x05, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00 };
                    fs.Write(header, 0, header.Length);
                    fs.Flush();

                    this.clipFileStream = fs;
                    this.clipTempFilePath = tempPath;
                    this.clipStartTime = now;
                    raised = true;

                    this.logger.Information("开始裁剪：{Path}", tempPath);
                }
                catch (Exception ex)
                {
                    this.logger.Warning(ex, "开始裁剪失败");
                    try { this.clipFileStream?.Dispose(); } catch { }
                    this.clipFileStream = null;
                    this.clipTempFilePath = null;
                    return;
                }
            }

            if (raised)
                ClipStarted?.Invoke(this, new ClipEventArgs(this.clipTempFilePath ?? string.Empty, this.clipStartTime));
        }

        public void EndClip()
        {
            string? finalPath = null;
            long fileSize = 0;
            DateTimeOffset start;
            DateTimeOffset end;

            lock (this.clipLock)
            {
                if (this.clipFileStream == null) return;

                try { this.clipFileStream.Flush(); } catch { }
                try { fileSize = this.clipFileStream.Length; } catch { }
                try { this.clipFileStream.Dispose(); } catch { }
                this.clipFileStream = null;

                var temp = this.clipTempFilePath;
                this.clipTempFilePath = null;
                start = this.clipStartTime;
                end = DateTimeOffset.Now;

                if (temp != null)
                {
                    try
                    {
                        var endStr = end.ToString("HHmmss");
                        var finalName = temp.Replace("-XXX.flv", $"-{endStr}.flv");
                        File.Move(temp, finalName);
                        finalPath = finalName;
                    }
                    catch (Exception ex)
                    {
                        this.logger.Warning(ex, "重命名裁剪文件失败");
                        finalPath = temp;
                    }
                }
            }

            if (finalPath != null)
            {
                this.logger.Information("裁剪结束：{Path}（{Size} 字节）", finalPath, fileSize);
                ClipEnded?.Invoke(this, new ClipEventArgs(finalPath, start, end));
            }
        }


        public virtual void RequestStop() => this.cts.Cancel();

        public virtual void SplitOutput() { }

        public async virtual Task StartAsync()
        {
            if (this.started)
                throw new InvalidOperationException("Only one StartAsync call allowed per instance.");
            this.started = true;

            var (fullUrl, codecQn) = await this.FetchStreamUrlAsync(this.room.RoomConfig.RoomId).ConfigureAwait(false);

            this.qn = codecQn.Qn;
            this.streamHost = new Uri(fullUrl).Host;
            var qnDesc = StreamQualityNumber.MapToString(codecQn.Qn);

            this.logger.Information("连接直播服务器 {Host} 录制画质 {Qn} ({QnDescription})", this.streamHost, codecQn, qnDesc);
            this.logger.Debug("直播流地址 {Url}", fullUrl);

            var stream = await this.GetStreamAsync(fullUrl: fullUrl, timeout: (int)this.room.RoomConfig.TimingStreamConnect).ConfigureAwait(false);

            this.ioStatsLastTrigger = DateTimeOffset.UtcNow;
            this.durationSinceNoDataReceived = TimeSpan.Zero;

            this.ct.Register(state => _ = Task.Run(async () =>
            {
                try
                {
                    if (state is not WeakReference<Stream> weakRef)
                        return;

                    await Task.Delay(1000);

                    if (weakRef.TryGetTarget(out var weakStream))
                    {
#if NET6_0_OR_GREATER
                        await weakStream.DisposeAsync();
#else
                        weakStream.Dispose();
#endif
                    }
                }
                catch (Exception)
                { }
            }), state: new WeakReference<Stream>(stream), useSynchronizationContext: false);

            this.StartRecordingLoop(stream);
        }

        protected abstract void StartRecordingLoop(Stream stream);

        private void Timer_Elapsed_TriggerIOStats(object? sender, ElapsedEventArgs e)
        {
            int networkDownloadBytes, diskWriteBytes;
            TimeSpan durationDiff, diskWriteDuration;
            DateTimeOffset startTime, endTime;


            lock (this.ioStatsLock) // 锁 timer elapsed 事件本身防止并行运行
            {
                // networks
                networkDownloadBytes = Interlocked.Exchange(ref this.ioNetworkDownloadedBytes, 0); // 锁网络统计
                endTime = DateTimeOffset.UtcNow;
                startTime = this.ioStatsLastTrigger;
                this.ioStatsLastTrigger = endTime;
                durationDiff = endTime - startTime;

                this.durationSinceNoDataReceived = networkDownloadBytes > 0 ? TimeSpan.Zero : this.durationSinceNoDataReceived + durationDiff;

                // disks
                lock (this.ioDiskStatsLock) // 锁硬盘统计
                {
                    diskWriteDuration = this.ioDiskWriteDuration;
                    diskWriteBytes = this.ioDiskWrittenBytes;
                    this.ioDiskWriteDuration = TimeSpan.Zero;
                    this.ioDiskWrittenBytes = 0;
                }
            }

            var netMbps = networkDownloadBytes * (8d / 1024d / 1024d) / durationDiff.TotalSeconds;
            var diskMBps = diskWriteBytes / (1024d * 1024d) / diskWriteDuration.TotalSeconds;

            this.OnIOStats(new IOStatsEventArgs
            {
                StreamHost = this.streamHost,
                NetworkBytesDownloaded = networkDownloadBytes,
                Duration = durationDiff,
                StartTime = startTime,
                EndTime = endTime,
                NetworkMbps = netMbps,
                DiskBytesWritten = diskWriteBytes,
                DiskWriteDuration = diskWriteDuration,
                DiskMBps = diskMBps,
            });

            if ((!this.timeoutTriggered) && (this.durationSinceNoDataReceived.TotalMilliseconds > this.room.RoomConfig.TimingWatchdogTimeout))
            {
                this.timeoutTriggered = true;
                this.logger.Warning("检测到录制卡住，可能是网络或硬盘原因，将会主动断开连接");
                this.RequestStop();
            }
        }

        protected (string fullPath, string relativePath) CreateFileName()
        {
            this.partIndex++;

            var output = this.fileNameGenerator.CreateFilePath(new FileNameTemplateContext
            {
                Name = FileNameGenerator.RemoveInvalidFileName(this.room.Name, ignore_slash: false),
                Title = FileNameGenerator.RemoveInvalidFileName(this.room.Title, ignore_slash: false),
                RoomId = this.room.RoomConfig.RoomId,
                ShortId = this.room.ShortId,
                Uid = this.room.Uid,
                AreaParent = FileNameGenerator.RemoveInvalidFileName(this.room.AreaNameParent, ignore_slash: false),
                AreaChild = FileNameGenerator.RemoveInvalidFileName(this.room.AreaNameChild, ignore_slash: false),
                PartIndex = this.partIndex,
                Qn = this.qn,
                Json = this.room.RawBilibiliApiJsonData,
            });

            if (this.IsClipTask && !string.IsNullOrWhiteSpace(output.FullPath) && !string.IsNullOrWhiteSpace(output.RelativePath))
            {
                // 裁剪任务：文件名加 -clip-HHmmss 后缀
                var suffix = "-clip-" + DateTimeOffset.Now.ToString("HHmmss");
                var fullExt = Path.GetExtension(output.FullPath!);
                var relExt = Path.GetExtension(output.RelativePath);
                var newFull = output.FullPath!.Substring(0, output.FullPath!.Length - fullExt.Length) + suffix + fullExt;
                var newRel = output.RelativePath.Substring(0, output.RelativePath.Length - relExt.Length) + suffix + relExt;
                return (newFull, newRel);
            }

            return (output.FullPath!, output.RelativePath);
        }

        #region Api Requests

        private HttpClient CreateHttpClient()
        {
            var handler = Api.Http.HttpClientWithBindAddress.CreateHandler(
                this.room.RoomConfig.NetworkTransportBindAddress,
                this.room.RoomConfig.NetworkTransportUseSystemProxy,
                allowAutoRedirect: false);

            var httpClient = new HttpClient(handler);
            var headers = httpClient.DefaultRequestHeaders;
            headers.Add("Accept", HttpHeaderAccept);
            headers.Add("Origin", HttpHeaderOrigin);
            headers.Add("Referer", HttpHeaderReferer);
            headers.Add("User-Agent", HttpHeaderUserAgent);
            return httpClient;
        }

        internal static readonly char[] QnParseSeparator = new[] { ',', '，', '、', ' ' };
        private static IReadOnlyList<StreamCodecQn> ParseAllowedQn(string? allowedQn)
        {
            if (string.IsNullOrWhiteSpace(allowedQn)) return Array.Empty<StreamCodecQn>();

            var qns = allowedQn!.Split(QnParseSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(static x =>
                {
                    if (int.TryParse(x, out var num))
                    {
                        return new StreamCodecQn
                        {
                            Qn = num,
                            Codec = StreamCodec.AVC
                        };
                    }
                    else if (x.StartsWith("avc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(x[3..], out num))
                        {
                            return new StreamCodecQn
                            {
                                Qn = num,
                                Codec = StreamCodec.AVC
                            };
                        }
                    }
                    else if (x.StartsWith("hevc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(x[4..], out num))
                        {
                            return new StreamCodecQn
                            {
                                Qn = num,
                                Codec = StreamCodec.HEVC
                            };
                        }
                    }

                    // invalid
                    return new StreamCodecQn
                    {
                        Qn = -1,
                        Codec = StreamCodec.AVC
                    };
                })
                .Where(x => x.Qn >= 0)
                .ToList();

            return qns;
        }

        /// <summary>获取当前直播流地址（fresh，用于抓帧）。</summary>
        public async Task<string> GetCurrentStreamUrlAsync()
        {
            var (url, _) = await this.FetchStreamUrlAsync(this.room.RoomConfig.RoomId).ConfigureAwait(false);
            return url;
        }

        protected async Task<(string url, StreamCodecQn codecQn)> FetchStreamUrlAsync(int roomid)
        {
            var allowedQn = ParseAllowedQn(this.room.RoomConfig.RecordingQuality);

            // 优先使用用户脚本获取直播流地址
            if (this.userScriptRunner.CallOnFetchStreamUrl(this.logger, roomid, allowedQn) is { } urlFromScript)
            {
                this.logger.Information("使用用户脚本返回的直播流地址 {Url}", urlFromScript);
                return (urlFromScript, new StreamCodecQn { Codec = StreamCodec.AVC, Qn = -1 });
            }

            const int DefaultQn = 10000;
            var codecItems = await this.apiClient.GetCodecItemInStreamUrlAsync(roomid: roomid, qn: DefaultQn).ConfigureAwait(false);
            //?? throw new Exception("no supported stream url, qn: " + DefaultQn);

            var allAvailableCodecQn = new List<StreamCodecQn>();

            if (codecItems.avc is not null)
            {
                allAvailableCodecQn.AddRange(codecItems.avc.AcceptQn.Select(x => new StreamCodecQn
                {
                    Codec = StreamCodec.AVC,
                    Qn = x
                }));
            }
            if (codecItems.hevc is not null)
            {
                allAvailableCodecQn.AddRange(codecItems.hevc.AcceptQn.Select(x => new StreamCodecQn
                {
                    Codec = StreamCodec.HEVC,
                    Qn = x
                }));
            }

            StreamCodecQn selectedCodecQn;
            // Select first avaiable qn
            foreach (var qn in allowedQn)
            {
                if (allAvailableCodecQn.Contains(qn))
                {
                    selectedCodecQn = qn;
                    goto match_qn_success;
                }
            }

            this.logger.Information("没有符合设置要求的画质，稍后再试。设置画质 {QnSettings}, 可用画质 {AcceptQn}", allowedQn, allAvailableCodecQn);
            throw new NoMatchingQnValueException();

        match_qn_success:
            this.logger.Debug("设置画质 {QnSettings}, 可用画质 {AcceptQn}, 最终选择 {SelectedQn}", allowedQn, allAvailableCodecQn, selectedCodecQn);

            if (selectedCodecQn.Qn != DefaultQn)
            {
                // 最终选择的 qn 与默认不同，需要重新请求一次
                codecItems = await this.apiClient.GetCodecItemInStreamUrlAsync(roomid: roomid, qn: selectedCodecQn.Qn).ConfigureAwait(false);
            }

            var item = selectedCodecQn.Codec switch
            {
                StreamCodec.AVC => codecItems.avc,
                StreamCodec.HEVC => codecItems.hevc,
                _ => throw new Exception("unknown codec")
            };

            if (item is null)
                throw new Exception("no supported stream url for " + selectedCodecQn);

            if (item.CurrentQn != selectedCodecQn.Qn)
                this.logger.Warning("返回的直播流地址的画质是 {CurrentQn} 而不是请求的 {SelectedQn}", item.CurrentQn, selectedCodecQn);

            var url_infos = item.UrlInfos;
            if (url_infos is null || url_infos.Length == 0)
                throw new Exception("no url_info");

            // https:// xy0x0x0x0xy.mcdn.bilivideo.cn:486
            var url_infos_without_mcdn = url_infos.Where(x => !x.Host.Contains(".mcdn.")).ToArray();

            var url_info = url_infos_without_mcdn.Length != 0
                ? url_infos_without_mcdn[this.random.Next(url_infos_without_mcdn.Length)]
                : url_infos[this.random.Next(url_infos.Length)];

            var fullUrl = url_info.Host + item.BaseUrl + url_info.Extra;

            return (fullUrl, new StreamCodecQn { Codec = selectedCodecQn.Codec, Qn = item.CurrentQn });
        }

        protected async Task<Stream> GetStreamAsync(string fullUrl, int timeout)
        {
            var client = this.CreateHttpClient();

            var streamHostInfoBuilder = new StringBuilder();

            while (true)
            {
                var allowedAddressFamily = this.room.RoomConfig.NetworkTransportAllowedAddressFamily;
                HttpRequestMessage request;
                Uri originalUri;

                if (this.userScriptRunner.CallOnTransformStreamUrl(this.logger, fullUrl) is { } scriptResult)
                {
                    var (scriptUrl, scriptIp) = scriptResult;

                    this.logger.Debug("用户脚本重定向了直播流地址 {NewUrl}, 旧地址 {OldUrl}", scriptUrl, fullUrl);

                    fullUrl = scriptUrl;
                    originalUri = new Uri(fullUrl);


                    if (scriptIp is not null)
                    {
                        this.logger.Debug("用户脚本指定了服务器 IP {IP}", scriptIp);

                        var uri = new Uri(fullUrl);
                        var builder = new UriBuilder(uri)
                        {
                            Host = scriptIp
                        };

                        request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                        request.Headers.Host = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;

                        streamHostInfoBuilder.Append(originalUri.Host);
                        streamHostInfoBuilder.Append(" [");
                        streamHostInfoBuilder.Append(scriptIp);
                        streamHostInfoBuilder.Append(']');

                        goto sendRequest;
                    }
                }
                else
                {
                    originalUri = new Uri(fullUrl);
                }

                if (allowedAddressFamily == AllowedAddressFamily.System)
                {
                    this.logger.Debug("NetworkTransportAllowedAddressFamily is System");
                    request = new HttpRequestMessage(HttpMethod.Get, originalUri);

                    streamHostInfoBuilder.Append(originalUri.Host);
                }
                else
                {
                    var ips = await Dns.GetHostAddressesAsync(originalUri.DnsSafeHost);

                    var filtered = ips.Where(x => allowedAddressFamily switch
                    {
                        AllowedAddressFamily.Ipv4 => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
                        AllowedAddressFamily.Ipv6 => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
                        AllowedAddressFamily.Any => true,
                        _ => false
                    }).ToArray();

                    var selected = filtered[this.random.Next(filtered.Length)];

                    this.logger.Debug("指定直播服务器地址 {DnsHost}: {SelectedIp}, Allowed: {AllowedAddressFamily}, {IPAddresses}", originalUri.DnsSafeHost, selected, allowedAddressFamily, ips);

                    streamHostInfoBuilder.Append(originalUri.Host);
                    streamHostInfoBuilder.Append(" [");
                    streamHostInfoBuilder.Append(selected);
                    streamHostInfoBuilder.Append(']');

                    if (selected is null)
                    {
                        throw new Exception("DNS 没有返回符合要求的 IP 地址");
                    }

                    var builder = new UriBuilder(originalUri)
                    {
                        Host = selected.ToString()
                    };

                    request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                    request.Headers.Host = originalUri.IsDefaultPort ? originalUri.Host : originalUri.Host + ":" + originalUri.Port;
                }

            sendRequest:

                var resp = await client.SendAsync(request,
                     HttpCompletionOption.ResponseHeadersRead,
                     new CancellationTokenSource(timeout).Token)
                     .ConfigureAwait(false);

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.OK:
                        {
                            this.logger.Information("开始接收直播流");
                            this.streamHostFull = streamHostInfoBuilder.ToString();
                            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            return stream;
                        }
                    case HttpStatusCode.Moved:
                    case HttpStatusCode.Redirect:
                        {
                            fullUrl = new Uri(originalUri, resp.Headers.Location!).ToString();
                            this.logger.Debug("跳转到 {Url}, 原文本 {Location}", fullUrl, resp.Headers.Location!.OriginalString);
                            resp.Dispose();
                            streamHostInfoBuilder.Append('\n');
                            break;
                        }
                    default:
                        throw new Exception(string.Format("尝试下载直播流时服务器返回了 ({0}){1}", resp.StatusCode, resp.ReasonPhrase));
                }
            }
        }
        #endregion
    }
}

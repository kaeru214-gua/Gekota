using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

#nullable enable
namespace Gekota.WPF.Services
{
    /// <summary>弹幕转换：XML 预处理（固定弹幕转滚动）+ 调用 DanmakuFactory。</summary>
    public static class DanmakuConverter
    {
        private static readonly ILogger logger = Log.ForContext(typeof(DanmakuConverter));

        /// <summary>p="时间,模式,..."：把模式 4（底部）/ 5（顶部）改成 1（滚动）。</summary>
        private static readonly Regex FixedModeRegex =
            new Regex("(p=\"[\\d.]+),(4|5),", RegexOptions.Compiled);

        /// <summary>转换单个 XML → ASS，返回 DanmakuFactory 退出码。</summary>
        public static int Convert(string xmlPath, string assPath)
        {
            var input = PrepareInput(xmlPath);
            try
            {
                var cmd = DanmakuConvertSettings.Current.BuildCommand(input, assPath, xmlPath);
                var code = DanmakuFactoryInterop.RunCommand(cmd);
                if (code == 0) ApplyMsgBoxTransparency(assPath);
                return code;
            }
            finally
            {
                Cleanup(input, xmlPath);
            }
        }

        /// <summary>
        /// 给特殊弹幕（DanmakuFactory 的 MSG 样式：SC / 礼物 / 上舰）加透明度。
        /// DF 没有消息框透明度参数，所以在生成的 ASS 里给每条 MSG 事件注入 \alpha。
        /// </summary>
        public static void ApplyMsgBoxTransparency(string assPath)
        {
            try
            {
                var options = DanmakuConvertSettings.Current;
                if (!options.MsgBoxTransparentEnabled) return;

                var percent = options.MsgBoxTransparency;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                if (percent == 0) return;

                var alpha = (int)Math.Round(percent / 100.0 * 255);
                if (alpha <= 0) return;
                if (!File.Exists(assPath)) return;

                var tag = "{\\alpha&H" + alpha.ToString("X2") + "&}";
                var lines = File.ReadAllLines(assPath, Encoding.UTF8);
                var changed = false;

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (!line.StartsWith("Dialogue:", StringComparison.Ordinal)) continue;

                    // 字段：Layer,Start,End,Style,Name,MarginL,MarginR,MarginV,Effect,Text
                    var fields = line.Split(new[] { ',' }, 10);
                    if (fields.Length < 10) continue;
                    if (!string.Equals(fields[3].Trim(), "MSG", StringComparison.Ordinal)) continue;

                    fields[9] = tag + fields[9];
                    lines[i] = string.Join(",", fields);
                    changed = true;
                }

                if (changed)
                {
                    File.WriteAllLines(assPath, lines, new UTF8Encoding(false));
                    logger.Debug("[弹幕转换] 特殊弹幕透明度 {Percent}% -> alpha {Alpha}", percent, alpha);
                }
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[弹幕转换] 特殊弹幕透明度处理失败 {Ass}", assPath);
            }
        }

        private static string PrepareInput(string xmlPath)
        {
            try
            {
                if (!DanmakuConvertSettings.Current.ForceScroll) return xmlPath;

                var text = File.ReadAllText(xmlPath);
                var replaced = FixedModeRegex.Replace(text, "$1,1,");
                if (string.Equals(replaced, text, StringComparison.Ordinal)) return xmlPath;

                var temp = Path.Combine(Path.GetTempPath(),
                    "gekota_dm_" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(temp, replaced, new UTF8Encoding(false));
                logger.Debug("[弹幕转换] 固定弹幕转滚动 -> {Temp}", temp);
                return temp;
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "[弹幕转换] XML 预处理失败，使用原文件 {Xml}", xmlPath);
                return xmlPath;
            }
        }

        private static void Cleanup(string prepared, string original)
        {
            if (string.Equals(prepared, original, StringComparison.OrdinalIgnoreCase)) return;
            try { File.Delete(prepared); } catch { }
        }
    }
}

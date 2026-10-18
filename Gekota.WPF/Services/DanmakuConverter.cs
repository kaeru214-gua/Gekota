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
                return DanmakuFactoryInterop.RunCommand(cmd);
            }
            finally
            {
                Cleanup(input, xmlPath);
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

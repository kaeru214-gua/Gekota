namespace Gekota
{
    /// <summary>
    /// 版本号常量。修改版本号时请同时修改 Directory.Build.props 中的 &lt;Version&gt;。
    /// 不依赖 git 或任何自动版本生成工具。
    /// </summary>
    public static class GekotaVersion
    {
        public const string FullSemVer = "1.0.0";
        public const string InformationalVersion = "1.0.0";
        public const string CommitDate = "2026-01-01";
        public const string Sha = "unknown";
        public const string ShortSha = "unknown";
    }
}

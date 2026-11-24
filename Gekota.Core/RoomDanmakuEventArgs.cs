using System;

#nullable enable
namespace Gekota.Core
{
    /// <summary>实时播放页使用的公开弹幕事件参数，不暴露 Core 内部模型。</summary>
    public sealed class RoomDanmakuEventArgs : EventArgs
    {
        public RoomDanmakuEventArgs(string text, string userName, long userId,
            bool isSuperChat = false, double price = 0, int keepTime = 0)
        {
            this.Text = text;
            this.UserName = userName;
            this.UserId = userId;
            this.IsSuperChat = isSuperChat;
            this.Price = price;
            this.KeepTime = keepTime;
        }

        public string Text { get; }
        public string UserName { get; }
        public long UserId { get; }
        public bool IsSuperChat { get; }
        public double Price { get; }
        public int KeepTime { get; }
    }
}

using System;

namespace Simple.FtpServer
{
    public interface IFtpLogSink
    {
        void Write(FtpLogEntry entry);
    }

    public sealed class FtpLogEntry
    {
        public DateTime TimestampUtc { get; internal set; }
        public string SessionId { get; internal set; }
        public string RemoteEndpoint { get; internal set; }
        public string EventName { get; internal set; }
        public string VirtualPath { get; internal set; }
        public long? BytesTransferred { get; internal set; }
        public TimeSpan? Duration { get; internal set; }
        public int? ReplyCode { get; internal set; }
        public string Detail { get; internal set; }
    }
}
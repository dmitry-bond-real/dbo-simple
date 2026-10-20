using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Simple.FtpServer
{
    internal sealed class ControlLineReader
    {
        private const int MaximumLineBytes = 4096;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly byte[] _readBuffer = new byte[1024];
        private readonly MemoryStream _lineBuffer = new MemoryStream();
        private int _offset;
        private int _count;
        private bool _pendingCarriageReturn;

        public async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            _lineBuffer.SetLength(0);
            _pendingCarriageReturn = false;
            while (true)
            {
                if (_offset == _count)
                {
                    _count = await stream.ReadAsync(_readBuffer, 0, _readBuffer.Length, cancellationToken)
                        .ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                    {
                        if (_lineBuffer.Length != 0)
                        {
                            throw new FtpProtocolException("The control connection ended inside a command.");
                        }

                        return null;
                    }
                }

                var value = _readBuffer[_offset++];
                if (value == (byte)'\n')
                {
                    if (!_pendingCarriageReturn)
                    {
                        throw new FtpProtocolException("Commands must use CRLF framing.");
                    }

                    var bytes = _lineBuffer.ToArray();
                    try
                    {
                        return StrictUtf8.GetString(bytes, 0, bytes.Length - 1);
                    }
                    catch (DecoderFallbackException exception)
                    {
                        throw new FtpProtocolException("The command is not valid UTF-8.", exception);
                    }
                }

                if (_pendingCarriageReturn)
                {
                    throw new FtpProtocolException("Commands must use CRLF framing.");
                }

                if (_lineBuffer.Length + 2 > MaximumLineBytes)
                {
                    throw new FtpCommandTooLongException();
                }

                _lineBuffer.WriteByte(value);
                _pendingCarriageReturn = value == (byte)'\r';
            }
        }
    }

    internal class FtpProtocolException : Exception
    {
        public FtpProtocolException(string message) : base(message)
        {
        }

        public FtpProtocolException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    internal sealed class FtpCommandTooLongException : FtpProtocolException
    {
        public FtpCommandTooLongException() : base("The command line exceeds the configured limit.")
        {
        }
    }
}
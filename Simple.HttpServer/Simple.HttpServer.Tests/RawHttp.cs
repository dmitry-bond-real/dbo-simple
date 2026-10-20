using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Simple.HttpServer.Tests
{
    // Raw socket helper for wire-level cases HttpClient refuses to send (missing Host header, etc.).
    internal static class RawHttp
    {
        public static async Task<string> SendAsync(int pPort, string pRawRequest)
        {
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync("127.0.0.1", pPort);
                using (NetworkStream stream = tcp.GetStream())
                {
                    byte[] data = Encoding.ASCII.GetBytes(pRawRequest);
                    await stream.WriteAsync(data, 0, data.Length);

                    tcp.ReceiveTimeout = 2000;
                    using (var reader = new StreamReader(stream, Encoding.ASCII))
                        return await reader.ReadToEndAsync();
                }
            }
        }
    }
}

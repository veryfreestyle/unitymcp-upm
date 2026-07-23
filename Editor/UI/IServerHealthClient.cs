using System.IO;
using System.Net;

namespace VeryFS.UnityMCP.Editor.UI
{
    /// <summary>Polls GET /health. Injected so the window logic is testable
    /// without a live server. Implementations throw on network/parse failure;
    /// the window treats any throw as a failed poll.</summary>
    public interface IServerHealthClient
    {
        ServerHealthSnapshot Poll(int port, string clientToken);
    }

    /// <summary>Production client: GET http://127.0.0.1:{port}/health with the
    /// client bearer token, 1s timeout, body parsed into a snapshot.</summary>
    public sealed class HttpServerHealthClient : IServerHealthClient
    {
        public ServerHealthSnapshot Poll(int port, string clientToken)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
            request.Method = "GET";
            request.Timeout = 1000;
            request.Headers.Add("Authorization", "Bearer " + clientToken);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                return ServerHealthSnapshot.Parse(reader.ReadToEnd());
            }
        }
    }
}

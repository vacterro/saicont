using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using SaiCont;

namespace SaiCont
{
internal static class PublicationRegressionTests
{
    private static int failures;
    private static void Check(bool passed, string name)
    {
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
        if (!passed) failures++;
    }

    public static int Run()
    {
        failures = 0;
        return RunCases();
    }

    private static int RunCases()
    {
        Retention();
        Cancellation(false);
        Cancellation(true);
        return failures == 0 ? 0 : 1;
    }

    private static void Retention()
    {
        string root = Path.Combine(Path.GetTempPath(), "saicont-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "state.xml");
            DateTime recent = DateTime.UtcNow.AddDays(-1);
            for (int i = 0; i < DurableStateStore.MaximumQuarantines; i++)
            {
                string old = path + ".corrupt." + i;
                File.WriteAllText(old, "old evidence " + i);
                File.SetLastWriteTimeUtc(old, recent.AddMinutes(i));
            }
            File.WriteAllText(path, "<broken");
            File.SetLastWriteTimeUtc(path, recent.AddDays(-30));
            new DurableStateStore(path).Load();
            string[] retained = Directory.GetFiles(root, "state.xml.corrupt.*");
            Check(retained.Length == DurableStateStore.MaximumQuarantines, "old-mtime incoming quarantine respects exact cap");
            bool incoming = false;
            foreach (string file in retained) incoming |= File.ReadAllText(file) == "<broken";
            Check(incoming, "incoming evidence retained despite oldest mtime");
            Check(File.Exists(path + ".corrupt." + (DurableStateStore.MaximumQuarantines - 1)), "most recent previous evidence retained");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Cancellation(bool dispose)
    {
        using (var streamReady = new ManualResetEvent(false))
        using (var restReady = new ManualResetEvent(false))
        using (var streamClosed = new ManualResetEvent(false))
        using (var restDone = new ManualResetEvent(false))
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            TcpClient streamClient = null;
            TcpClient restClient = null;
            var server = new Thread(delegate()
            {
                try
                {
                    streamClient = listener.AcceptTcpClient();
                    var stream = streamClient.GetStream();
                    ReadHeaders(stream);
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    streamReady.Set();
                    restClient = listener.AcceptTcpClient();
                    ReadHeaders(restClient.GetStream());
                    restReady.Set();
                    stream.ReadTimeout = 4000;
                    if (stream.ReadByte() == -1) streamClosed.Set();
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true };
            server.Start();
            var transport = new OpenCodeHttpTransport(endpoint, "fixture");
            Thread rest = null;
            try
            {
                Check(streamReady.WaitOne(3000), "owned SSE connected " + dispose);
                rest = new Thread(delegate()
                {
                    try
                    {
                        var method = typeof(OpenCodeHttpTransport).GetMethod("TryHttp", BindingFlags.NonPublic | BindingFlags.Instance);
                        object[] args = { "GET", endpoint, "/fixture", null, null, 10000, 0, null, null };
                        method.Invoke(transport, args);
                    }
                    finally { restDone.Set(); }
                }) { IsBackground = true };
                rest.Start();
                Check(restReady.WaitOne(3000), "owned REST overlaps SSE " + dispose);
                Check(!streamClosed.WaitOne(200), "SSE remains live before cancellation " + dispose);
                if (dispose) transport.Dispose(); else transport.CancelInFlight();
                Check(restDone.WaitOne(2500), "REST cancelled " + dispose);
                Check(streamClosed.WaitOne(2500), "SSE cancelled while REST active " + dispose);
            }
            finally
            {
                transport.Dispose();
                if (streamClient != null) streamClient.Close();
                if (restClient != null) restClient.Close();
                listener.Stop();
                if (rest != null) rest.Join(2000);
                server.Join(2000);
            }
        }
    }

    private static void ReadHeaders(NetworkStream stream)
    {
        stream.ReadTimeout = 4000;
        int state = 0;
        while (state < 4)
        {
            int next = stream.ReadByte();
            if (next < 0) throw new IOException("fixture connection closed before headers");
            int expected = state % 2 == 0 ? 13 : 10;
            state = next == expected ? state + 1 : (next == 13 ? 1 : 0);
        }
    }
}

}

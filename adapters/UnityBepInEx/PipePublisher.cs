using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RealTimeTranslater.UnityBepInEx;

internal sealed class PipePublisher : IDisposable
{
    private const int Port = 47851;

    private readonly object _gate = new object();
    private readonly AutoResetEvent _signal =
        new AutoResetEvent(false);

    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarning;

    private Thread _worker;
    private bool _running;
    private bool _loggedConnection;
    private string _pending;

    public PipePublisher(
        Action<string> logInfo,
        Action<string> logWarning)
    {
        _logInfo = logInfo;
        _logWarning = logWarning;
    }

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RealTimeTranslater.UnityAdapter.Tcp"
        };
        _worker.Start();
    }

    public void Publish(string payload)
    {
        if (!_running || string.IsNullOrEmpty(payload))
            return;

        lock (_gate)
        {
            _pending = payload;
        }

        _signal.Set();
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    client.NoDelay = true;
                    client.SendTimeout = 1500;
                    client.Connect("127.0.0.1", Port);

                    if (!_loggedConnection)
                    {
                        _logInfo(
                            "Connected to RealTimeTranslater desktop receiver on 127.0.0.1:" +
                            Port + ".");
                        _loggedConnection = true;
                    }

                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(
                        stream,
                        new UTF8Encoding(false),
                        8192,
                        false))
                    {
                        writer.AutoFlush = true;

                        while (_running && client.Connected)
                        {
                            var payload = TakePending();

                            if (payload != null)
                                writer.WriteLine(payload);

                            _signal.WaitOne(200);
                        }
                    }
                }
            }
            catch (SocketException)
            {
                _loggedConnection = false;
                _signal.WaitOne(400);
            }
            catch (IOException)
            {
                _loggedConnection = false;
                _signal.WaitOne(250);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (ThreadInterruptedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _loggedConnection = false;
                _logWarning(
                    "Unity adapter IPC error: " +
                    ex.GetType().Name +
                    ": " +
                    ex.Message);
                _signal.WaitOne(500);
            }
        }
    }

    private string TakePending()
    {
        lock (_gate)
        {
            var payload = _pending;
            _pending = null;
            return payload;
        }
    }

    public void Dispose()
    {
        if (!_running)
            return;

        _running = false;
        _signal.Set();

        if (_worker != null &&
            _worker.IsAlive &&
            !_worker.Join(1500))
        {
            try
            {
                _worker.Interrupt();
            }
            catch
            {
            }
        }

        _signal.Dispose();
    }
}

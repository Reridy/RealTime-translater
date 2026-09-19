using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace RealTimeTranslater.UnityBepInEx;

internal sealed class PipePublisher : IDisposable
{
    private const string PipeName =
        "RealTimeTranslater.UnityText.v1";

    private readonly object _gate = new object();
    private readonly AutoResetEvent _signal =
        new AutoResetEvent(false);

    private Thread _worker;
    private bool _running;
    private string _pending;

    public void Start()
    {
        if (_running)
            return;

        _running = true;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "RealTimeTranslater.UnityAdapter.Pipe"
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
                using (var pipe = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous))
                {
                    pipe.Connect(500);

                    using (var writer = new StreamWriter(
                        pipe,
                        new UTF8Encoding(false),
                        8192,
                        true))
                    {
                        writer.AutoFlush = true;

                        while (_running && pipe.IsConnected)
                        {
                            var payload = TakePending();

                            if (payload != null)
                                writer.WriteLine(payload);

                            _signal.WaitOne(250);
                        }
                    }
                }
            }
            catch (TimeoutException)
            {
                _signal.WaitOne(500);
            }
            catch (IOException)
            {
                _signal.WaitOne(250);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
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

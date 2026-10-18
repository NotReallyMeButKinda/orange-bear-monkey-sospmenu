using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using MelonLoader;

namespace SocialSpaceModMenu
{
    /// <summary>
    /// Discord Rich Presence over the desktop client's local IPC pipe (\\.\pipe\discord-ipc-N): a handshake
    /// with an application id, then SET_ACTIVITY frames. Everything runs on a background thread so a missing
    /// or busy Discord never stalls the game; the main thread only hands over the latest wanted activity.
    /// Nothing leaves the machine except through Discord itself, and no space or instance ids are shared.
    /// </summary>
    internal sealed class DiscordRpc
    {
        private const int OpHandshake = 0, OpFrame = 1, OpClose = 2;

        private readonly string _appId;
        private readonly long _start = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        private readonly object _lock = new();
        private readonly AutoResetEvent _wake = new(false);
        private string _details, _state; // wanted
        private bool _dirty, _stop;
        private Thread _thread;

        public bool Connected { get; private set; }

        public DiscordRpc(string appId) => _appId = appId;

        public void Start()
        {
            if (_thread != null)
                return;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "SocialSpaceModMenu.DiscordRpc" };
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            _thread = null;
        }

        /// <summary>Sets the shown activity; cheap to call every frame (only changes are sent).</summary>
        public void Set(string details, string state)
        {
            lock (_lock)
            {
                if (details == _details && state == _state)
                    return;
                _details = details;
                _state = state;
                _dirty = true;
            }
            _wake.Set();
        }

        private void Run()
        {
            while (!_stop)
            {
                NamedPipeClientStream pipe = Connect();
                if (pipe == null)
                {
                    _wake.WaitOne(15000); // Discord not running; try again later
                    continue;
                }
                try
                {
                    using (pipe)
                    {
                        Write(pipe, OpHandshake, "{\"v\":1,\"client_id\":\"" + _appId + "\"}");
                        Read(pipe); // READY (or an error frame, then the pipe closes)
                        Connected = true;
                        lock (_lock)
                            _dirty = true;
                        while (!_stop)
                        {
                            string details, state;
                            bool send;
                            lock (_lock)
                            {
                                send = _dirty;
                                _dirty = false;
                                details = _details;
                                state = _state;
                            }
                            if (send)
                            {
                                Write(pipe, OpFrame, Activity(details, state));
                                Read(pipe);
                            }
                            _wake.WaitOne(5000);
                        }
                        Write(pipe, OpClose, "{}");
                    }
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is InvalidOperationException)
                {
                    // Discord closed or rejected us; reconnect after a pause
                }
                catch (Exception e)
                {
                    Melon<ModMenu>.Logger.Warning($"Discord presence: {e.Message}");
                }
                Connected = false;
                if (!_stop)
                    _wake.WaitOne(15000);
            }
        }

        private static NamedPipeClientStream Connect()
        {
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None);
                try
                {
                    pipe.Connect(200);
                    return pipe;
                }
                catch (Exception)
                {
                    pipe.Dispose();
                }
            }
            return null;
        }

        private string Activity(string details, string state)
        {
            string nonce = Guid.NewGuid().ToString();
            int pid = Environment.ProcessId;
            if (details == null)
                return "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + pid + "},\"nonce\":\"" + nonce + "\"}";
            var sb = new StringBuilder();
            sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":").Append(pid).Append(",\"activity\":{");
            sb.Append("\"details\":").Append(Json(details));
            if (!string.IsNullOrEmpty(state))
                sb.Append(",\"state\":").Append(Json(state));
            sb.Append(",\"timestamps\":{\"start\":").Append(_start).Append('}');
            sb.Append(",\"instance\":false}},\"nonce\":\"").Append(nonce).Append("\"}");
            return sb.ToString();
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s.Length > 120 ? s.Substring(0, 120) : s)
            {
                if (c == '"' || c == '\\')
                    sb.Append('\\').Append(c);
                else if (c < 0x20)
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                else
                    sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static void Write(Stream pipe, int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            pipe.Write(frame, 0, frame.Length);
            pipe.Flush();
        }

        private static void Read(Stream pipe)
        {
            byte[] header = ReadExactly(pipe, 8);
            int op = BitConverter.ToInt32(header, 0);
            int len = BitConverter.ToInt32(header, 4);
            if (len < 0 || len > 1 << 20)
                throw new IOException("bad frame");
            string body = Encoding.UTF8.GetString(ReadExactly(pipe, len));
            if (op == OpClose)
                throw new IOException("closed: " + body);
            if (body.Contains("\"evt\":\"ERROR\""))
                Melon<ModMenu>.Logger.Warning("Discord presence error: " + body);
        }

        private static byte[] ReadExactly(Stream s, int n)
        {
            var buf = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0)
                    throw new IOException("pipe closed");
                got += r;
            }
            return buf;
        }
    }
}

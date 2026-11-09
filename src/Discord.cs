// muzika — Discord Rich Presence over Discord's local IPC pipe (no external libraries).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Emopunk
{
    class DiscordRpc
    {
        readonly object gate = new object();
        readonly JavaScriptSerializer js = new JavaScriptSerializer();
        readonly Thread worker;
        readonly int pid = Process.GetCurrentProcess().Id;

        // wanted state (set from the UI thread)
        bool enabled; string appId = "", gif = "";
        string title = "", artist = ""; bool playing; double pos, dur; DateTime posAt = DateTime.UtcNow;
        bool hasTrack, dirty;
        volatile bool stop;

        // connection state (worker thread only)
        NamedPipeClientStream pipe;
        string connectedId;
        DateTime nextConnect = DateTime.MinValue, lastSend = DateTime.MinValue;
        string lastState;

        public Action<string> OnState;   // called on the worker thread

        public DiscordRpc()
        {
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        public void Configure(bool on, string id, string gifUrl)
        {
            lock (gate)
            {
                id = (id ?? "").Trim();
                bool idOk = id.Length >= 15 && id.Length <= 22;
                foreach (char c in id) if (c < '0' || c > '9') idOk = false;
                enabled = on && idOk;
                if (id != appId) nextConnect = DateTime.MinValue;
                appId = id;
                gif = (gifUrl ?? "").Trim();
                dirty = true;
                if (!on) State("off");
                else if (!idOk) State("paste the application id from discord.com/developers");
            }
        }

        public void SetTrack(string t, string a, bool isPlaying, double position, double duration)
        {
            lock (gate)
            {
                title = t ?? ""; artist = a ?? ""; playing = isPlaying;
                pos = position; dur = duration; posAt = DateTime.UtcNow;
                hasTrack = title.Length > 0; dirty = true;
            }
        }

        public void Shutdown()
        {
            stop = true;
            try { worker.Join(1500); } catch { }
        }

        void State(string s)
        {
            if (s == lastState) return;
            lastState = s;
            Action<string> cb = OnState;
            if (cb != null) try { cb(s); } catch { }
        }

        // ---------- worker ----------
        void Loop()
        {
            while (!stop)
            {
                Thread.Sleep(400);
                bool on; string id;
                lock (gate) { on = enabled; id = appId; }

                if (pipe != null && (!on || id != connectedId))
                {
                    try { SendActivity(null); } catch { }
                    Close();
                }
                if (!on) continue;

                if (pipe == null)
                {
                    if (DateTime.UtcNow < nextConnect) continue;
                    if (!Connect(id)) { nextConnect = DateTime.UtcNow.AddSeconds(15); continue; }
                    lock (gate) dirty = true;
                }

                bool send; lock (gate) send = dirty;
                if (send && (DateTime.UtcNow - lastSend).TotalSeconds >= 2.5)   // discord allows ~5 updates / 20 s
                {
                    Dictionary<string, object> act;
                    lock (gate) { act = hasTrack ? BuildActivity() : null; dirty = false; }
                    lastSend = DateTime.UtcNow;
                    try { SendActivity(act); }
                    catch { Close(); State("lost discord, retrying"); nextConnect = DateTime.UtcNow.AddSeconds(5); }
                }
            }
            if (pipe != null) { try { SendActivity(null); } catch { } Close(); }
        }

        static string Clip(string s, int max)
        {
            s = (s ?? "").Trim();
            if (s.Length < 2) s = (s + "  ").Substring(0, 2);
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }

        Dictionary<string, object> BuildActivity()
        {
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["type"] = 2;   // "listening to"
            a["details"] = Clip(title, 128);
            a["state"] = Clip(playing ? "by " + artist : "paused", 128);
            if (playing && dur > 0)
            {
                double p = pos + (DateTime.UtcNow - posAt).TotalSeconds;
                long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                long start = now - (long)(p * 1000);
                a["timestamps"] = new Dictionary<string, object> { { "start", start }, { "end", start + (long)(dur * 1000) } };
            }
            Dictionary<string, object> assets = new Dictionary<string, object>();
            if (gif.StartsWith("https://") || gif.StartsWith("http://")) { assets["large_image"] = gif; assets["large_text"] = "muzika"; }
            if (assets.Count > 0) a["assets"] = assets;
            a["buttons"] = new object[] { new Dictionary<string, object> { { "label", "get muzika" }, { "url", "https://t.me/emopunk" } } };
            return a;
        }

        void SendActivity(Dictionary<string, object> act)
        {
            if (pipe == null) return;
            Dictionary<string, object> args = new Dictionary<string, object>();
            args["pid"] = pid;
            if (act != null) args["activity"] = act;
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["cmd"] = "SET_ACTIVITY";
            msg["args"] = args;
            msg["nonce"] = Guid.NewGuid().ToString();
            Write(1, js.Serialize(msg));

            int op; string resp;
            if (!Read(out op, out resp, 4000)) throw new Exception("no reply");
            if (op == 2) throw new Exception("closed");
            if (resp.Contains("\"evt\":\"ERROR\""))
            {
                string m = Between(resp, "\"message\":\"", "\"");
                State("discord said: " + (m ?? "error"));
            }
            else if (act != null) State(lastUser != null ? "connected · " + lastUser : "connected");
        }

        string lastUser;

        bool Connect(string id)
        {
            NamedPipeClientStream p = null;
            for (int i = 0; i < 10 && p == null; i++)
            {
                try
                {
                    NamedPipeClientStream c = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                    c.Connect(250);
                    p = c;
                }
                catch { }
            }
            if (p == null) { State("discord is not running"); return false; }
            pipe = p;
            try
            {
                Write(0, "{\"v\":1,\"client_id\":\"" + id + "\"}");
                int op; string resp;
                if (!Read(out op, out resp, 4000)) throw new Exception("no handshake reply");
                if (op == 2 || !resp.Contains("READY"))
                {
                    string m = Between(resp, "\"message\":\"", "\"");
                    Close();
                    State("discord refused: " + (m ?? "check the application id"));
                    return false;
                }
                lastUser = Between(resp, "\"username\":\"", "\"");
                connectedId = id;
                State(lastUser != null ? "connected · " + lastUser : "connected");
                return true;
            }
            catch
            {
                Close();
                State("discord is not responding");
                return false;
            }
        }

        void Close()
        {
            try { if (pipe != null) pipe.Dispose(); } catch { }
            pipe = null; connectedId = null;
        }

        void Write(int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            Buffer.BlockCopy(body, 0, frame, 8, body.Length);
            pipe.Write(frame, 0, frame.Length);
            pipe.Flush();
        }

        bool ReadExact(byte[] buf, int timeoutMs)
        {
            int got = 0;
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (got < buf.Length)
            {
                int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return false;
                Task<int> t = pipe.ReadAsync(buf, got, buf.Length - got);
                if (!t.Wait(left)) return false;
                if (t.Result <= 0) return false;
                got += t.Result;
            }
            return true;
        }

        bool Read(out int op, out string json, int timeoutMs)
        {
            op = -1; json = "";
            byte[] head = new byte[8];
            if (!ReadExact(head, timeoutMs)) return false;
            op = BitConverter.ToInt32(head, 0);
            int len = BitConverter.ToInt32(head, 4);
            if (len < 0 || len > 1 << 20) return false;
            byte[] body = new byte[len];
            if (len > 0 && !ReadExact(body, timeoutMs)) return false;
            json = Encoding.UTF8.GetString(body);
            return true;
        }

        static string Between(string s, string a, string b)
        {
            if (s == null) return null;
            int i = s.IndexOf(a); if (i < 0) return null;
            i += a.Length;
            int j = s.IndexOf(b, i); if (j < 0) return null;
            return s.Substring(i, j - i);
        }
    }
}

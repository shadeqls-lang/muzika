// muzika — SoundCloud search, account and streaming (public web API). SoundCloud links only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Emopunk
{
    static class Log
    {
        public static string File_;
        static readonly object gate = new object();
        public static void Write(string text)
        {
            if (File_ == null) return;
            try
            {
                lock (gate)
                {
                    FileInfo fi = new FileInfo(File_);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(File_, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    static class Net
    {
        public const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

        static Net()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            // .net allows only 2 connections per server by default: that was throttling the parallel stream download
            ServicePointManager.DefaultConnectionLimit = 64;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        public static string Get(string url) { return Send("GET", url, null); }

        public static string Send(string method, string url, string auth)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method;
            if (!string.IsNullOrEmpty(auth)) r.Headers["Authorization"] = "OAuth " + auth;
            if (method == "PUT" || method == "POST") r.ContentLength = 0;
            r.UserAgent = UA;
            r.Accept = "application/json, text/html, */*";
            r.Timeout = 20000;
            r.ReadWriteTimeout = 20000;
            r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // download with retries; continues a broken download from where it stopped
        public static void Download(string url, string path, Action<long, long> progress)
        {
            string part = path + ".part";
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                    r.UserAgent = UA;
                    r.Timeout = 30000;
                    r.ReadWriteTimeout = 30000;
                    if (have > 0) r.AddRange(have);
                    using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                    {
                        bool partial = resp.StatusCode == HttpStatusCode.PartialContent;
                        if (!partial) have = 0;
                        long total = resp.ContentLength > 0 ? have + resp.ContentLength : -1;
                        using (Stream s = resp.GetResponseStream())
                        using (FileStream fs = new FileStream(part, partial ? FileMode.Append : FileMode.Create))
                        {
                            byte[] buf = new byte[81920];
                            int n; long done = have; DateTime last = DateTime.MinValue;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                fs.Write(buf, 0, n); done += n;
                                if ((DateTime.UtcNow - last).TotalMilliseconds > 300) { last = DateTime.UtcNow; if (progress != null) progress(done, total); }
                            }
                        }
                    }
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(part, path);
                    return;
                }
                catch (Exception)
                {
                    if (attempt == 7) throw;
                    Thread.Sleep(2500);
                }
            }
        }
    }

    // talks to api-v2.soundcloud.com the same way the website does
    class SoundCloud
    {
        readonly string idFile, authFile;
        string clientId;
        readonly object gate = new object();
        public string Token;          // soundcloud login (oauth_token cookie), kept encrypted on disk
        public string MeId;           // numeric user id of the logged in account
        public Action OnLoggedOut;

        public SoundCloud(string data)
        {
            idFile = Path.Combine(data, "sc_key.txt");
            authFile = Path.Combine(data, "sc_auth.bin");
            try { if (File.Exists(idFile)) clientId = File.ReadAllText(idFile).Trim(); } catch { }
            try
            {
                if (File.Exists(authFile))
                    Token = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                        File.ReadAllBytes(authFile), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch { Token = null; }
        }

        public void SetToken(string t)
        {
            Token = string.IsNullOrEmpty(t) ? null : t;
            MeId = null;
            try
            {
                if (Token == null) { if (File.Exists(authFile)) File.Delete(authFile); }
                else File.WriteAllBytes(authFile, System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(Token), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
            }
            catch { }
        }

        public string Key { get { lock (gate) { if (string.IsNullOrEmpty(clientId)) clientId = FetchKey(); return clientId; } } }

        // logged-in user; null when not logged in
        public string Me()
        {
            if (Token == null) return null;
            string me = Api("/me");
            MeId = TopId(me);
            return me;
        }

        string MeOrThrow()
        {
            if (Token == null) throw new Exception("log in to soundcloud first");
            if (MeId == null) Me();
            if (MeId == null) throw new Exception("couldn't read your account");
            return MeId;
        }

        public string Mine(string what)
        {
            string me = MeOrThrow();
            if (what == "likes") return Api("/users/" + me + "/track_likes?limit=50&linked_partitioning=1");
            if (what == "playlists") return Api("/users/" + me + "/playlists_without_albums?limit=50&linked_partitioning=1");
            if (what == "liked_playlists") return Api("/users/" + me + "/playlist_likes?limit=50&linked_partitioning=1");
            if (what == "feed") return Api("/stream?limit=40&linked_partitioning=1");
            throw new Exception("unknown list");
        }

        public string LikedIds()
        {
            MeOrThrow();
            return Api("/me/track_likes/ids?limit=5000");
        }

        public void Like(string trackId, bool on)
        {
            string me = MeOrThrow();
            if (!Regex.IsMatch(trackId ?? "", "^[0-9]+$")) throw new Exception("bad track");
            Api("/users/" + me + "/track_likes/" + trackId, on ? "PUT" : "DELETE");
        }

        static string UserId(SoundCloud self, string id)
        {
            if (id == "me") return self.MeOrThrow();
            if (!Regex.IsMatch(id ?? "", "^[0-9]+$")) throw new Exception("bad user");
            return id;
        }

        public string User(string id) { return Api("/users/" + UserId(this, id)); }

        public string UserList(string id, string what, out string kind)
        {
            string u = UserId(this, id);
            if (what == "likes") { kind = "likes"; return Api("/users/" + u + "/track_likes?limit=50&linked_partitioning=1"); }
            if (what == "playlists") { kind = "playlists"; return Api("/users/" + u + "/playlists_without_albums?limit=50&linked_partitioning=1"); }
            if (what == "reposts") { kind = "feed"; return Api("/stream/users/" + u + "/reposts?limit=50&linked_partitioning=1"); }
            kind = "usertracks"; return Api("/users/" + u + "/tracks?limit=50&linked_partitioning=1");
        }

        // radio: tracks soundcloud considers close to this one
        public string Related(string id)
        {
            string res = null;
            try { res = Api("/tracks/" + id + "/related?limit=40&linked_partitioning=1"); } catch (Exception ex) { Log.Write("radio related " + id + ": " + ex.Message); }
            if (res == null || !res.Contains("\"kind\":\"track\""))
            {
                try { res = Api("/stations/soundcloud:track-stations:" + id + "/tracks?limit=40"); } catch (Exception ex) { Log.Write("radio station " + id + ": " + ex.Message); if (res == null) throw; }
            }
            return res;
        }

        public string Track(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[0-9]+$")) throw new Exception("bad track");
            return Api("/tracks/" + id);
        }

        public string Playlist(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[0-9]+$")) throw new Exception("bad playlist");
            return Api("/playlists/" + id + "?representation=full");
        }

        public static bool IsScUrl(string u)
        {
            Uri uri;
            if (!Uri.TryCreate((u ?? "").Trim(), UriKind.Absolute, out uri)) return false;
            if (uri.Scheme != "https" && uri.Scheme != "http") return false;
            string h = uri.Host.ToLowerInvariant();
            return h == "soundcloud.com" || h == "www.soundcloud.com" || h == "m.soundcloud.com" || h == "on.soundcloud.com";
        }

        string FetchKey()
        {
            string html = Net.Get("https://soundcloud.com/");
            MatchCollection ms = Regex.Matches(html, "<script[^>]+src=\"(https://a-v2\\.sndcdn\\.com/assets/[^\"]+\\.js)\"");
            for (int i = ms.Count - 1; i >= 0; i--)
            {
                string js;
                try { js = Net.Get(ms[i].Groups[1].Value); } catch { continue; }
                Match m = Regex.Match(js, "client_id\\s*:\\s*\"([0-9a-zA-Z]{32})\"");
                if (m.Success)
                {
                    try { File.WriteAllText(idFile, m.Groups[1].Value); } catch { }
                    return m.Groups[1].Value;
                }
            }
            throw new Exception("couldn't reach soundcloud");
        }

        public string Api(string pathOrUrl) { return Api(pathOrUrl, "GET"); }

        public string Api(string pathOrUrl, string method)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string key;
                lock (gate)
                {
                    if (string.IsNullOrEmpty(clientId) || attempt == 1) clientId = FetchKey();
                    key = clientId;
                }
                string u = pathOrUrl.StartsWith("https://") ? pathOrUrl : "https://api-v2.soundcloud.com" + pathOrUrl;
                u = Regex.Replace(u, "([?&])client_id=[^&]*(&|$)", "$1").TrimEnd('&', '?');
                u += (u.Contains("?") ? "&" : "?") + "client_id=" + key;
                try { return Net.Send(method, u, Token); }
                catch (WebException ex)
                {
                    HttpWebResponse hr = ex.Response as HttpWebResponse;
                    int code = hr != null ? (int)hr.StatusCode : 0;
                    if (code == 401 && Token != null)
                    {
                        // login expired: forget it and carry on as a guest
                        SetToken(null);
                        Action cb = OnLoggedOut; if (cb != null) try { cb(); } catch { }
                        if (attempt == 0) continue;
                    }
                    if ((code == 401 || code == 403) && attempt == 0) continue;
                    if (code == 404) throw new Exception("not found on soundcloud");
                    throw new Exception(code > 0 ? "soundcloud answered " + code : "no connection to soundcloud");
                }
            }
            throw new Exception("soundcloud refused the request");
        }

        static readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        static string TopId(string json)
        {
            try
            {
                Dictionary<string, object> d = ser.DeserializeObject(json) as Dictionary<string, object>;
                object v; if (d != null && d.TryGetValue("id", out v) && v != null) return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        public string Search(string q)
        {
            return Api("/search/tracks?q=" + Uri.EscapeDataString(q) + "&limit=30&linked_partitioning=1");
        }

        public string More(string next)
        {
            if (!next.StartsWith("https://api-v2.soundcloud.com/")) throw new Exception("bad link");
            return Api(next);
        }

        public string Tracks(IEnumerable<string> ids)
        {
            string list = string.Join(",", ids.Where(x => Regex.IsMatch(x, "^[0-9]+$")).Take(50).ToArray());
            return Api("/tracks?ids=" + list);
        }

        // returns kind + raw json: track, playlist, likes, usertracks
        public string Open(string url, out string kind)
        {
            url = url.Trim();
            if (!IsScUrl(url)) throw new Exception("only soundcloud links");
            Match likes = Regex.Match(url, "^(https?://(?:www\\.|m\\.)?soundcloud\\.com/[^/?#]+)/likes/?(?:[?#].*)?$");
            string target = likes.Success ? likes.Groups[1].Value : url.Split('?')[0];
            string res = Api("/resolve?url=" + Uri.EscapeDataString(target));
            Match km = Regex.Match(res, "\"kind\":\"(\\w+)\"");
            string k = km.Success ? km.Groups[1].Value : "";
            if (k == "user")
            {
                string uid = TopId(res);
                if (uid == null) throw new Exception("couldn't read that profile");
                kind = likes.Success ? "likes" : "usertracks";
                return Api(likes.Success
                    ? "/users/" + uid + "/track_likes?limit=50&linked_partitioning=1"
                    : "/users/" + uid + "/tracks?limit=50&linked_partitioning=1");
            }
            kind = k == "playlist" || k == "system-playlist" ? "playlist" : k == "track" ? "track" : k;
            return res;
        }
    }

    // plays soundcloud tracks without saving them: fetches the stream into a small disk cache
    class ScStream
    {
        readonly SoundCloud sc;
        readonly string dir;
        public string Tools;          // folder with yt-dlp.exe (shared with the downloader)
        public string LastUrl;
        readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly Dictionary<string, ManualResetEvent> busy = new Dictionary<string, ManualResetEvent>();
        readonly Dictionary<string, string> errors = new Dictionary<string, string>();
        public long Limit = 500L * 1024 * 1024;   // set from the player settings

        public ScStream(SoundCloud s, string data)
        {
            sc = s;
            dir = Path.Combine(data, "cache");
            Directory.CreateDirectory(dir);
            foreach (string f in Directory.GetFiles(dir)) if (f.EndsWith(".part") || f.Contains(".dl.")) try { File.Delete(f); } catch { }
        }

        public string Cached(string scId)
        {
            foreach (string ext in new[] { ".mp3", ".m4a", ".aac", ".opus" })
            {
                string f = Path.Combine(dir, scId + ext);
                if (File.Exists(f)) { try { File.SetLastAccessTimeUtc(f, DateTime.UtcNow); } catch { } return f; }
            }
            return null;
        }

        public string LastError(string scId) { lock (busy) { string e; return errors.TryGetValue(scId, out e) ? e : null; } }

        // blocks until the track is in the cache; returns the file path or null
        public string Ensure(string scId)
        {
            string f = Cached(scId);
            if (f != null) return f;
            ManualResetEvent wait = null; bool mine = false;
            lock (busy)
            {
                if (!busy.TryGetValue(scId, out wait)) { wait = new ManualResetEvent(false); busy[scId] = wait; mine = true; errors.Remove(scId); }
            }
            if (!mine) { wait.WaitOne(120000); return Cached(scId); }
            string first = null;
            try { return Fetch(scId); }
            catch (Exception ex) { first = ex.Message; Log.Write("stream " + scId + ": direct failed: " + ex); }
            try { return FetchYtDlp(scId); }
            catch (Exception ex2)
            {
                Log.Write("stream " + scId + ": yt-dlp failed: " + ex2.Message);
                lock (busy) errors[scId] = first + " / " + ex2.Message;
                return null;
            }
            finally { lock (busy) busy.Remove(scId); wait.Set(); Trim(); }
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }

        // whole track as one file (used when the player can't take pieces): same pieces, glued together
        string Fetch(string scId)
        {
            Hls h = Resolve(scId, null, null, false);
            string ext = h.Mime.StartsWith("audio/mp4") ? ".m4a" : h.Mime == "audio/mpeg" ? ".mp3" : h.Mime == "audio/aac" ? ".aac" : null;
            if (ext == null) throw new Exception("stream format the player can't glue (" + h.Mime + ")");
            int n = h.Segs.Count; byte[][] data = new byte[n][]; byte[] init = h.Init != null ? Segment(scId, -1) : null;
            int nextIdx = -1; Exception fail = null;
            ThreadStart worker = delegate
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref nextIdx);
                    if (i >= n || fail != null) return;
                    try { data[i] = Segment(scId, i); } catch (Exception ex) { fail = ex; return; }
                }
            };
            List<Thread> pool = new List<Thread>();
            for (int k = 0; k < 6; k++) { Thread th = new Thread(worker); th.IsBackground = true; th.Start(); pool.Add(th); }
            foreach (Thread th in pool) th.Join();
            if (fail != null) throw new Exception("stream broke off: " + fail.Message);
            string target = Path.Combine(dir, scId + ext), part = target + ".part";
            using (FileStream fs = new FileStream(part, FileMode.Create))
            {
                if (init != null) fs.Write(init, 0, init.Length);
                foreach (byte[] b in data) fs.Write(b, 0, b.Length);
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(part, target);
            foreach (string f in Directory.GetFiles(dir, scId + ".s*")) try { File.Delete(f); } catch { }   // pieces no longer needed
            Log.Write("stream " + scId + ": glued " + n + " pieces (" + h.Mime + ")");
            return target;
        }

        // backup plan: let yt-dlp grab the stream as is (no conversion, so no ffmpeg needed)
        string FetchYtDlp(string scId)
        {
            if (string.IsNullOrEmpty(Tools)) throw new Exception("no tools folder");
            Directory.CreateDirectory(Tools);
            string yt = Path.Combine(Tools, "yt-dlp.exe");
            if (!File.Exists(yt)) Net.Download("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", yt, null);
            Dictionary<string, object> t = js.DeserializeObject(sc.Api("/tracks/" + scId)) as Dictionary<string, object>;
            string url = Str(t, "permalink_url");
            if (!SoundCloud.IsScUrl(url)) throw new Exception("no link for this track");
            string args = "-f \"bestaudio[ext=mp3]/bestaudio\" --no-playlist --no-part --no-cache-dir --no-warnings --encoding utf-8 --retries 5 " +
                          "-o \"" + Path.Combine(dir, scId + ".dl.%(ext)s") + "\" --print after_move:filepath ";
            string tok = sc.Token;
            if (!string.IsNullOrEmpty(tok) && Regex.IsMatch(tok, "^[0-9A-Za-z._-]+$")) args += "--username oauth --password " + tok + " ";
            args += "\"" + url + "\"";
            ProcessStartInfo psi = new ProcessStartInfo(yt, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Tools;
            string outp, err;
            using (Process p = Process.Start(psi))
            {
                System.Threading.Tasks.Task<string> eo = p.StandardError.ReadToEndAsync();
                outp = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } throw new Exception("yt-dlp timed out"); }
                err = eo.Result;
            }
            string got = null;
            foreach (string line in outp.Split('\n')) { string l = line.Trim(); if (l.Length > 0 && File.Exists(l)) got = l; }
            if (got == null)
            {
                Match em = Regex.Match(err ?? "", "ERROR: (.+)");
                throw new Exception(em.Success ? em.Groups[1].Value.Trim() : "yt-dlp gave nothing");
            }
            // finished: move under the real name (keep only extensions the player knows)
            string ext = Path.GetExtension(got).ToLowerInvariant();
            string to = Path.Combine(dir, scId + (ext == ".ogg" || ext == ".opus" ? ".opus" : ext == ".aac" || ext == ".mp4" || ext == ".m4a" ? ".m4a" : ".mp3"));
            if (File.Exists(to)) File.Delete(to);
            File.Move(got, to); got = to;
            Log.Write("stream " + scId + ": ok through yt-dlp (" + Path.GetFileName(got) + ")");
            return got;
        }

        // ---------- segment streaming: the player gets the track piece by piece and starts after the first one ----------
        public class Hls { public string Mime; public List<string> Segs = new List<string>(); public List<double> Durs = new List<double>(); public string Init; public DateTime At; }
        readonly Dictionary<string, Hls> hls = new Dictionary<string, Hls>();
        readonly Dictionary<string, object> rlocks = new Dictionary<string, object>();

        object RLock(string id) { lock (rlocks) { object o; if (!rlocks.TryGetValue(id, out o)) { o = new object(); rlocks[id] = o; } return o; } }

        // tc / auth come from the list the user is looking at, which saves one request to soundcloud
        public Hls Resolve(string scId, string tc, string auth, bool force)
        {
            lock (RLock(scId))
            {
                Hls h;
                lock (hls) if (!force && hls.TryGetValue(scId, out h) && (DateTime.UtcNow - h.At).TotalMinutes < 8) return h;
                // soundcloud dropped mp3 hls (it answers 404), so aac goes first; every candidate is tried in turn.
                // the link from the page is tried first, the full track info is only fetched when it fails
                string loc = null, m3u = null, mime = null; Exception last = null;
                Func<string, string, string, bool> tryOne = delegate(string url, string mt, string au)
                {
                    try
                    {
                        string u = url + (url.Contains("?") ? "&" : "?") + "client_id=" + sc.Key + (string.IsNullOrEmpty(au) ? "" : "&track_authorization=" + au);
                        string l2 = Str(js.DeserializeObject(Net.Send("GET", u, sc.Token)) as Dictionary<string, object>, "url");
                        if (l2.Length == 0) throw new Exception("no stream link");
                        string body = Net.Get(l2);
                        if (body.Contains("#EXT-X-KEY") && !body.Contains("METHOD=NONE")) throw new Exception("protected stream");
                        loc = l2; m3u = body; mime = mt; return true;
                    }
                    catch (Exception ex) { last = ex; Log.Write("resolve " + scId + ": " + url.Substring(url.LastIndexOf('/') + 1) + " failed: " + ex.Message); return false; }
                };
                bool ok = !force && !string.IsNullOrEmpty(tc) && tc.StartsWith("https://api-v2.soundcloud.com/media/") && tryOne(tc, tc.Contains("mp4") || tc.Contains("aac") ? "audio/mp4" : null, auth);
                if (!ok)
                {
                    Dictionary<string, object> t = js.DeserializeObject(sc.Api("/tracks/" + scId)) as Dictionary<string, object>;
                    if (t == null) throw new Exception("track not found");
                    string au2 = Str(t, "track_authorization");
                    foreach (string[] c in Transcodings(t, true))
                    {
                        if (c[0] == tc && !force) continue;
                        if (tryOne(c[0], c[1], au2)) { ok = true; break; }
                    }
                }
                if (!ok) throw last ?? new Exception("no stream");
                h = new Hls { At = DateTime.UtcNow };
                Uri baseUri = new Uri(loc);
                Match init = Regex.Match(m3u, "#EXT-X-MAP:URI=\"([^\"]+)\"");
                if (init.Success) h.Init = new Uri(baseUri, init.Groups[1].Value).ToString();
                // what the pieces really are: fmp4 (init map) / mpeg-ts / raw aac / mp3
                if (init.Success) h.Mime = "audio/mp4; codecs=\"mp4a.40.2\"";
                else if (Regex.IsMatch(m3u, "\\.ts(\\?|\\s)")) h.Mime = "video/mp2t; codecs=\"mp4a.40.2\"";
                else if (mime != null && mime.StartsWith("audio/mpeg")) h.Mime = "audio/mpeg";
                else if (Regex.IsMatch(m3u, "\\.(aac|adts)(\\?|\\s)")) h.Mime = "audio/aac";
                else h.Mime = mime != null && mime.Contains("mp4") ? "audio/mp4; codecs=\"mp4a.40.2\"" : "audio/mpeg";
                double dur = 0;
                foreach (string line in m3u.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.StartsWith("#EXTINF:")) { double.TryParse(l.Substring(8).Split(',')[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out dur); }
                    else if (l.Length > 0 && !l.StartsWith("#")) { h.Segs.Add(new Uri(baseUri, l).ToString()); h.Durs.Add(dur); dur = 0; }
                }
                if (h.Segs.Count == 0) throw new Exception("empty stream");
                lock (hls) hls[scId] = h;
                return h;
            }
        }

        // hls aac > hls mp3 > progressive mp3 > hls opus; previews (go+ snippets) last
        static List<string[]> Transcodings(Dictionary<string, object> t, bool hlsOnly)
        {
            List<KeyValuePair<int, string[]>> l = new List<KeyValuePair<int, string[]>>();
            object media; t.TryGetValue("media", out media);
            object trs = null; Dictionary<string, object> md = media as Dictionary<string, object>; if (md != null) md.TryGetValue("transcodings", out trs);
            System.Collections.IEnumerable list = trs as System.Collections.IEnumerable;
            if (list != null)
                foreach (object o in list)
                {
                    Dictionary<string, object> c = o as Dictionary<string, object>; if (c == null) continue;
                    object fo; c.TryGetValue("format", out fo); Dictionary<string, object> f = fo as Dictionary<string, object>;
                    string proto = Str(f, "protocol"), mt = Str(f, "mime_type");
                    int rank;
                    if (proto == "hls" && mt.Contains("mp4")) rank = 0;
                    else if (proto == "hls" && mt.StartsWith("audio/mpeg")) rank = 1;
                    else if (proto == "progressive" && mt.StartsWith("audio/mpeg") && !hlsOnly) rank = 2;
                    else if (proto == "hls" && mt.Contains("ogg") && !hlsOnly) rank = 3;
                    else continue;
                    if (Str(c, "snipped") == "True") rank += 10;
                    l.Add(new KeyValuePair<int, string[]>(rank, new[] { Str(c, "url"), mt, proto }));
                }
            return l.OrderBy(x => x.Key).Select(x => x.Value).ToList();
        }

        public string ResolveJson(string scId, string tc, string auth)
        {
            if (Cached(scId) != null) return "{\"id\":\"" + scId + "\",\"cached\":true}";
            Hls h = Resolve(scId, tc, auth, false);
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":\"").Append(scId).Append("\",\"cached\":false,\"mime\":").Append(js.Serialize(h.Mime)).Append(",\"init\":").Append(h.Init != null ? "true" : "false").Append(",\"durs\":[");
            for (int i = 0; i < h.Durs.Count; i++) { if (i > 0) sb.Append(','); sb.Append(h.Durs[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)); }
            sb.Append("]}");
            return sb.ToString();
        }

        // one piece of the track; pieces are kept on disk, so replays don't touch the network
        public byte[] Segment(string scId, int i)
        {
            string f = Path.Combine(dir, scId + (i < 0 ? ".sinit" : ".s" + i));
            if (File.Exists(f)) { try { File.SetLastAccessTimeUtc(f, DateTime.UtcNow); return File.ReadAllBytes(f); } catch { } }
            byte[] b = null;
            for (int attempt = 0; attempt < 3 && b == null; attempt++)
            {
                Hls h = Resolve(scId, null, null, attempt > 0);
                string url = i < 0 ? h.Init : (i < h.Segs.Count ? h.Segs[i] : null);
                if (url == null) throw new Exception("no such piece");
                try { b = Bytes(url); }
                catch (Exception ex) { Log.Write("segment " + scId + "/" + i + ": " + ex.Message); if (attempt == 2) throw; Thread.Sleep(300); }
            }
            try { string tmp = f + ".part"; File.WriteAllBytes(tmp, b); if (File.Exists(f)) File.Delete(f); File.Move(tmp, f); } catch { }
            return b;
        }

        // get ready before the click: resolve the stream and fetch the first pieces
        public void Warm(string scId, string tc, string auth, int pieces)
        {
            if (Cached(scId) != null) return;
            try
            {
                Hls h = Resolve(scId, tc, auth, false);
                if (h.Init != null) Segment(scId, -1);
                for (int i = 0; i < Math.Min(pieces, h.Segs.Count); i++) Segment(scId, i);
            }
            catch (Exception ex) { Log.Write("warm " + scId + ": " + ex.Message); }
            Trim();
        }

        static byte[] Bytes(string url)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = Net.UA; r.Timeout = 20000; r.ReadWriteTimeout = 20000;
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (MemoryStream m = new MemoryStream())
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }

        // keeps the cache under the limit, oldest listened first
        void Trim()
        {
            try
            {
                FileInfo[] files = new DirectoryInfo(dir).GetFiles().Where(f => !f.Name.EndsWith(".part") && !f.Name.Contains(".dl.")).OrderBy(f => f.LastAccessTimeUtc).ToArray();
                long total = files.Sum(f => f.Length);
                foreach (FileInfo f in files)
                {
                    if (total <= Limit) break;
                    try { total -= f.Length; f.Delete(); } catch { }
                }
            }
            catch { }
        }

        public void Clear()
        {
            foreach (string f in Directory.GetFiles(dir)) try { File.Delete(f); } catch { }
        }

        public long Size()
        {
            try { return new DirectoryInfo(dir).GetFiles().Sum(f => f.Length); } catch { return 0; }
        }

        public void TrimNow() { Trim(); }
    }

    // covers and avatars: fetched by the app (many connections at once) and kept on disk
    class ImgCache
    {
        readonly string dir;
        public ImgCache(string folder)
        {
            dir = folder; Directory.CreateDirectory(dir);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    FileInfo[] fs = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToArray();
                    long total = fs.Sum(f => f.Length);
                    foreach (FileInfo f in fs) { if (total < 300L * 1024 * 1024) break; total -= f.Length; try { f.Delete(); } catch { } }
                    foreach (FileInfo f in fs) if (f.Name.EndsWith(".part")) try { f.Delete(); } catch { }
                }
                catch { }
            });
        }
        public string Path_(string url)
        {
            using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
                StringBuilder sb = new StringBuilder(); for (int i = 0; i < 10; i++) sb.Append(h[i].ToString("x2"));
                return Path.Combine(dir, sb.ToString());
            }
        }
        public byte[] Get(string url)
        {
            string f = Path_(url);
            if (File.Exists(f)) return File.ReadAllBytes(f);
            byte[] b = null;
            for (int k = 0; k < 3 && b == null; k++)
            {
                try
                {
                    HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                    r.UserAgent = Net.UA; r.Timeout = 15000; r.ReadWriteTimeout = 15000;
                    using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream m = new MemoryStream()) { s.CopyTo(m); b = m.ToArray(); }
                }
                catch (WebException ex)
                {
                    HttpWebResponse hr = ex.Response as HttpWebResponse;
                    if (hr != null && (int)hr.StatusCode == 404) return null;
                    Thread.Sleep(300);
                }
            }
            if (b == null) return null;
            try { File.WriteAllBytes(f + ".part", b); if (File.Exists(f)) File.Delete(f); File.Move(f + ".part", f); } catch { }
            return b;
        }
    }

    // checks the github repo for a newer muzika.exe, downloads it in the background, swaps it in on restart/exit
    class Updater
    {
        readonly string cfgFile, exe, next;
        public string Repo = "shadeqls-lang/muzika";
        public bool Auto = true;
        public string NewVersion;
        public string Notes;                    // release notes of the downloaded version
        public string JustNotes, JustVer;       // notes of the version we just updated to
        public bool Ready { get { return NewVersion != null && File.Exists(next); } }
        public Action<string, string, double> OnState;
        int busy;

        public Updater(string data)
        {
            cfgFile = Path.Combine(data, "update.json");
            exe = System.Windows.Forms.Application.ExecutablePath; next = exe + ".new";
            try
            {
                if (File.Exists(cfgFile))
                {
                    Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(cfgFile)) as Dictionary<string, object>;
                    object v;
                    if (d != null && d.TryGetValue("repo", out v) && v != null && Regex.IsMatch(v.ToString(), "^[\\w.-]+/[\\w.-]+$")) Repo = v.ToString();
                    if (d != null && d.TryGetValue("auto", out v) && v is bool) Auto = (bool)v;
                    if (d != null && d.TryGetValue("ready", out v) && v != null && File.Exists(next) && Newer(v.ToString(), Program.Version)) NewVersion = v.ToString();
                    if (d != null && d.TryGetValue("notes", out v) && v != null) Notes = v.ToString();
                    if (d != null && d.TryGetValue("justNotes", out v) && v != null) JustNotes = v.ToString();
                    if (d != null && d.TryGetValue("justVer", out v) && v != null) JustVer = v.ToString();
                }
            }
            catch { }
            if (NewVersion == null) try { if (File.Exists(next)) File.Delete(next); } catch { }
        }

        void SaveCfg()
        {
            try { File.WriteAllText(cfgFile, new JavaScriptSerializer().Serialize(new { repo = Repo, auto = Auto, ready = NewVersion, notes = NewVersion != null ? Notes : null, justNotes = JustNotes, justVer = JustVer })); } catch { }
        }

        public void Configure(string repo, bool auto)
        {
            if (!string.IsNullOrEmpty(repo))
            {
                repo = Regex.Replace(repo.Trim(), "^https?://github\\.com/", "").Trim('/');
                if (Regex.IsMatch(repo, "^[\\w.-]+/[\\w.-]+$")) Repo = repo;
            }
            Auto = auto; SaveCfg();
            State(Ready ? "ready" : "idle", NewVersion, 0);
        }

        void State(string s, string v, double p) { Action<string, string, double> cb = OnState; if (cb != null) try { cb(s, v, p); } catch { } }

        static int[] Parse(string v)
        {
            MatchCollection ms = Regex.Matches(v ?? "", "\\d+");
            return ms.Cast<Match>().Select(m => int.Parse(m.Value)).ToArray();
        }
        public static bool Newer(string a, string b)
        {
            int[] x = Parse(a), y = Parse(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p > q;
            }
            return false;
        }

        // one quick check per launch, nothing while muzika is open
        public bool Start()
        {
            if (!Auto) return false;
            State("checking", null, 0);
            Thread t = new Thread(delegate() { Check(false); });
            t.IsBackground = true; t.Start();
            return true;
        }

        // the changelog was shown once, forget it
        public void SeenNotes() { if (JustNotes == null && JustVer == null) return; JustNotes = null; JustVer = null; SaveCfg(); }

        // notes of any released version (settings, "what's new")
        public string FetchNotes(string ver)
        {
            string rel = Net.Get("https://api.github.com/repos/" + Repo + "/releases/tags/v" + Regex.Replace(ver ?? "", "^[vV]", ""));
            Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(rel) as Dictionary<string, object>;
            object b; return d != null && d.TryGetValue("body", out b) && b != null ? b.ToString() : "";
        }

        public void CheckNow() { ThreadPool.QueueUserWorkItem(delegate { Check(true); }); }

        // finds: latest release with an .exe asset, or version.txt + muzika.exe in the repo itself
        void Check(bool manual)
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                if (Ready) { State("ready", NewVersion, 1); return; }
                State("checking", null, 0);
                string ver = null, url = null, notes = null;
                try
                {
                    string rel = Net.Get("https://api.github.com/repos/" + Repo + "/releases/latest");
                    Dictionary<string, object> d = new JavaScriptSerializer().DeserializeObject(rel) as Dictionary<string, object>;
                    object tag, assets, body; d.TryGetValue("tag_name", out tag); d.TryGetValue("assets", out assets);
                    if (d.TryGetValue("body", out body) && body != null) notes = body.ToString();
                    System.Collections.IEnumerable al = assets as System.Collections.IEnumerable;
                    if (tag != null && al != null)
                        foreach (object o in al)
                        {
                            Dictionary<string, object> a = o as Dictionary<string, object>; object n, u;
                            if (a != null && a.TryGetValue("name", out n) && n != null && n.ToString().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && a.TryGetValue("browser_download_url", out u))
                            { ver = tag.ToString(); url = u.ToString(); break; }
                        }
                }
                catch { }
                if (url == null)
                    foreach (string br in new[] { "main", "master" })
                    {
                        try
                        {
                            string v = Net.Get("https://raw.githubusercontent.com/" + Repo + "/" + br + "/version.txt").Trim();
                            if (Parse(v).Length > 0) { ver = v; url = "https://raw.githubusercontent.com/" + Repo + "/" + br + "/muzika.exe"; break; }
                        }
                        catch { }
                    }
                if (url == null) { State(manual ? "nothing" : "idle", null, 0); Log.Write("update: nothing found in " + Repo); return; }
                if (!Newer(ver, Program.Version)) { State(manual ? "latest" : "idle", ver, 0); return; }
                State("downloading", ver, 0);
                string tmp = next + ".dl";
                Net.Download(url, tmp, delegate(long done, long total) { State("downloading", ver, total > 0 ? (double)done / total : 0); });
                byte[] head = new byte[2];
                using (FileStream fs = File.OpenRead(tmp)) fs.Read(head, 0, 2);
                if (head[0] != 'M' || head[1] != 'Z' || new FileInfo(tmp).Length < 500000) { File.Delete(tmp); throw new Exception("downloaded file is not a program"); }
                if (File.Exists(next)) File.Delete(next);
                File.Move(tmp, next);
                NewVersion = Regex.Replace(ver, "^[vV]", ""); Notes = notes; SaveCfg();
                Log.Write("update: v" + NewVersion + " downloaded");
                State("ready", NewVersion, 1);
            }
            catch (Exception ex) { Log.Write("update: " + ex.Message); State("error", ex.Message, 0); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // a running exe can be renamed on windows: old one steps aside, new one takes its name
        public void Swap()
        {
            if (!Ready) return;
            string old = exe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(exe, old);
            try { File.Move(next, exe); }
            catch { File.Move(old, exe); throw; }
            JustVer = NewVersion; JustNotes = Notes;
            NewVersion = null; Notes = null; SaveCfg();
        }
    }
}

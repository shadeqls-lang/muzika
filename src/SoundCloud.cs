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
            foreach (string ext in new[] { ".mp3", ".m4a", ".opus" })
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

        string Fetch(string scId)
        {
            Dictionary<string, object> t = js.DeserializeObject(sc.Api("/tracks/" + scId)) as Dictionary<string, object>;
            if (t == null) throw new Exception("track not found");
            string auth = Str(t, "track_authorization");
            object media; t.TryGetValue("media", out media);
            Dictionary<string, object> md = media as Dictionary<string, object>;
            object trs = null; if (md != null) md.TryGetValue("transcodings", out trs);
            System.Collections.IEnumerable list = trs as System.Collections.IEnumerable;
            if (list == null) throw new Exception("no stream for this track");

            // pick: plain hls mp3 > progressive mp3 > hls aac > hls opus (encrypted go+ streams are skipped)
            Dictionary<string, object> best = null; int bestRank = 99; string ext = ".mp3";
            foreach (object o in list)
            {
                Dictionary<string, object> tc = o as Dictionary<string, object>;
                if (tc == null) continue;
                object fo; tc.TryGetValue("format", out fo);
                Dictionary<string, object> fmt = fo as Dictionary<string, object>;
                string proto = Str(fmt, "protocol"), mime = Str(fmt, "mime_type");
                int rank = 99; string e = ".mp3";
                if (proto == "hls" && mime.StartsWith("audio/mpeg")) rank = 0;
                else if (proto == "progressive" && mime.StartsWith("audio/mpeg")) rank = 1;
                else if (proto == "hls" && mime.Contains("mp4")) { rank = 2; e = ".m4a"; }
                else if (proto == "hls" && mime.Contains("ogg")) { rank = 3; e = ".opus"; }
                if (Str(tc, "snipped") == "True" && rank < 99) rank += 10;   // 30s preview only
                if (rank < bestRank) { bestRank = rank; best = tc; ext = e; }
            }
            if (best == null) throw new Exception("this track can't be streamed");
            Log.Write("stream " + scId + ": using rank " + bestRank + " " + Str(best, "url"));

            string u = Str(best, "url");
            u += (u.Contains("?") ? "&" : "?") + "client_id=" + sc.Key + (auth.Length > 0 ? "&track_authorization=" + auth : "");
            string loc = Str(js.DeserializeObject(Net.Send("GET", u, sc.Token)) as Dictionary<string, object>, "url");
            if (loc.Length == 0) throw new Exception("soundcloud gave no stream link");

            string target = Path.Combine(dir, scId + ext), part = target + ".part";
            int proto2 = bestRank % 10;
            if (proto2 == 1) Net.Download(loc, target, null);
            else
            {
                string m3u = Net.Get(loc);
                if (m3u.Contains("#EXT-X-KEY") && !m3u.Contains("METHOD=NONE")) throw new Exception("protected stream");
                Uri baseUri = new Uri(loc);
                List<string> segs = new List<string>();
                Match init = Regex.Match(m3u, "#EXT-X-MAP:URI=\"([^\"]+)\"");
                if (init.Success) segs.Add(new Uri(baseUri, init.Groups[1].Value).ToString());
                foreach (string line in m3u.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.Length > 0 && !l.StartsWith("#")) segs.Add(new Uri(baseUri, l).ToString());
                }
                if (segs.Count == 0) throw new Exception("empty stream");
                byte[][] data = new byte[segs.Count][];
                int nextIdx = -1; Exception fail = null;
                ThreadStart worker = delegate
                {
                    while (true)
                    {
                        int i = Interlocked.Increment(ref nextIdx);
                        if (i >= segs.Count || fail != null) return;
                        for (int tries = 0; ; tries++)
                        {
                            try { data[i] = Bytes(segs[i]); break; }
                            catch (Exception ex) { if (tries >= 4) { fail = ex; return; } Thread.Sleep(600); }
                        }
                    }
                };
                List<Thread> pool = new List<Thread>();
                for (int k = 0; k < 6; k++) { Thread th = new Thread(worker); th.IsBackground = true; th.Start(); pool.Add(th); }
                foreach (Thread th in pool) th.Join();
                if (fail != null) throw new Exception("stream broke off: " + fail.Message);
                using (FileStream fs = new FileStream(part, FileMode.Create))
                    foreach (byte[] b in data) fs.Write(b, 0, b.Length);
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
            }
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
}

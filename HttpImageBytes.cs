using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using WindBotDeckImage.Skia;

namespace EdoProDeckImageAndroid
{
    /// <summary>
    /// 폰에 없는 카드 이미지를 인터넷에서 받아 캐시에 저장한다.
    ///
    /// 이미지 결정 순서 (PC 판 v1.3 과 같다)
    ///   ① 캐시(&lt;앱 캐시&gt;/pics/&lt;카드ID&gt;.jpg)
    ///   ② 같은 ID      : YGOPRODeck cards → cards_small → cards_cropped
    ///   ③ cdb alias    : 재록·다른 일러의 **원본 ID** 로 같은 시도
    ///   ④ cdb 영문 이름 : YGOPRODeck 이름 검색 → 그 카드 이미지
    ///   ⑤ Yugipedia    : 문서 대표 이미지
    ///   · 404 는 즉시 다음 후보, 그 외 오류는 3회 재시도, HTML 오류 페이지는 거른다.
    ///   · 실패는 이번 실행에서만 기억한다(다음 실행에 다시 시도).
    /// </summary>
    public sealed class HttpImageBytes : ICardImageBytes
    {
        private static readonly string[] UrlTemplates =
        {
            "https://images.ygoprodeck.com/images/cards/{0}.jpg",
            "https://images.ygoprodeck.com/images/cards_small/{0}.jpg",
            "https://images.ygoprodeck.com/images/cards_cropped/{0}.jpg"
        };

        private readonly string _cacheDir;
        private readonly HashSet<int> _tried = new HashSet<int>();
        private int _downloaded;
        private int _failed;
        private int _fromAlias;
        private int _fromName;

        public int Downloaded { get { return _downloaded; } }
        public int Failed { get { return _failed; } }
        public int FromAlias { get { return _fromAlias; } }
        public int FromName { get { return _fromName; } }

        /// <summary>cdb 조회기(있으면 alias/이름 폴백 사용).</summary>
        public CdbLookup Cdb;

        /// <summary>실패도 앱 폴더 logs/app.log 에 남긴다(앱에서 경로를 넘겨준다).</summary>
        public string LogPath;
        private void FileLog(string msg)
        {
            if (string.IsNullOrEmpty(LogPath)) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath));
                System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public HttpImageBytes(string cacheDir)
        {
            _cacheDir = cacheDir;
            try { Directory.CreateDirectory(_cacheDir); } catch { }
        }

        public byte[] Get(int cardId)
        {
            string path = null;
            try { path = Path.Combine(_cacheDir, cardId + ".jpg"); } catch { }

            // ① 이미 받아 둔 것
            if (path != null)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        byte[] cached = File.ReadAllBytes(path);
                        if (cached != null && cached.Length > 512) return cached;
                    }
                }
                catch { }
            }

            // 이번 실행에서 이미 시도한 카드는 다시 시도하지 않는다
            lock (_tried)
            {
                if (_tried.Contains(cardId)) return null;
                _tried.Add(cardId);
            }

            // ② 같은 ID, ③ cdb alias(원본 ID)
            foreach (int id in ImageIds(cardId))
            {
                byte[] b = DownloadById(id, path);
                if (b != null)
                {
                    if (id != cardId) Interlocked.Increment(ref _fromAlias);
                    Interlocked.Increment(ref _downloaded);
                    Android.Util.Log.Info("AI-EDITOR", "img ok card=" + cardId + (id != cardId ? " (alias " + id + ")" : "") + " bytes=" + b.Length);
                    return b;
                }
            }

            // ④ cdb 영문 이름으로 YGOPRODeck 검색 → 그 카드 이미지
            foreach (string name in ImageNames(cardId))
            {
                int found = SearchYgoProDeckId(name);
                if (found <= 0 || found == cardId) continue;
                byte[] b = DownloadById(found, path);
                if (b != null)
                {
                    Interlocked.Increment(ref _fromName);
                    Interlocked.Increment(ref _downloaded);
                    Android.Util.Log.Info("AI-EDITOR", "img ok card=" + cardId + " (name '" + name + "' -> " + found + ")");
                    return b;
                }
            }

            // ⑤ Yugipedia
            foreach (string name in ImageNames(cardId))
            {
                string url = SearchYugipediaImage(name);
                if (string.IsNullOrEmpty(url)) continue;
                byte[] b = DownloadUrl(url, path);
                if (b != null)
                {
                    Interlocked.Increment(ref _fromName);
                    Interlocked.Increment(ref _downloaded);
                    Android.Util.Log.Info("AI-EDITOR", "img ok card=" + cardId + " (yugipedia '" + name + "')");
                    return b;
                }
            }

            Interlocked.Increment(ref _failed);
            if (_failed <= 5)
            {
                Android.Util.Log.Error("AI-EDITOR", "img FAIL card=" + cardId + NamesSuffix(cardId));
                FileLog("img FAIL card=" + cardId + NamesSuffix(cardId));
            }
            return null;
        }

        private string NamesSuffix(int cardId)
        {
            try
            {
                List<string> n = ImageNames(cardId);
                if (n.Count == 0) return "";
                return " (" + n[0] + ")";
            }
            catch { return ""; }
        }

        private List<int> ImageIds(int cardId)
        {
            var ids = new List<int> { cardId };
            try
            {
                if (Cdb != null)
                {
                    int alias = Cdb.AliasOf(cardId);
                    if (alias > 0 && !ids.Contains(alias)) ids.Add(alias);
                }
            }
            catch { }
            return ids;
        }

        private List<string> ImageNames(int cardId)
        {
            try { return Cdb == null ? new List<string>() : Cdb.EnglishNames(cardId); }
            catch { return new List<string>(); }
        }

        private byte[] DownloadById(int id, string cachePath)
        {
            foreach (string t in UrlTemplates)
            {
                byte[] b = DownloadUrl(string.Format(t, id), cachePath);
                if (b != null) return b;
            }
            return null;
        }

        /// <summary>URL 하나를 받아 캐시에 저장. 404 는 즉시 포기, 그 외 3회 재시도, HTML/빈 파일 거름.</summary>
        private byte[] DownloadUrl(string url, string cachePath)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 10000;
                    req.UserAgent = "EdoProDeckImage/1.8 (Android)";

                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        byte[] bytes = ms.ToArray();
                        if (bytes == null || bytes.Length <= 512) throw new Exception("empty image");
                        if (bytes[0] == (byte)'<') throw new Exception("html error page");
                        if (!string.IsNullOrEmpty(cachePath))
                        {
                            try { File.WriteAllBytes(cachePath, bytes); } catch { }
                        }
                        return bytes;
                    }
                }
                catch (WebException wex)
                {
                    var http = wex.Response as HttpWebResponse;
                    if (http != null && (int)http.StatusCode == 404) return null;   // 다음 후보
                    if (attempt < 2) Thread.Sleep(300 * (attempt + 1));
                }
                catch
                {
                    if (attempt < 2) Thread.Sleep(300 * (attempt + 1));
                }
            }
            return null;
        }

        // ── 이름 검색 ────────────────────────────────────────────
        private static readonly Dictionary<string, int> _nameIdCache = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> _yugiCache = new Dictionary<string, string>();
        private static readonly object _lookupLock = new object();

        private static string HttpGetString(string url)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 10000;
                    req.UserAgent = "EdoProDeckImage/1.8 (Android)";
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (var sr = new StreamReader(s, Encoding.UTF8))
                        return sr.ReadToEnd();
                }
                catch
                {
                    if (attempt == 0) Thread.Sleep(250);
                }
            }
            return null;
        }

        private static int SearchYgoProDeckId(string name)
        {
            lock (_lookupLock)
            {
                int v;
                if (_nameIdCache.TryGetValue(name, out v)) return v;
            }
            int found = 0;
            try
            {
                string url = "https://db.ygoprodeck.com/api/v7/cardinfo.php?fname=" +
                             Uri.EscapeDataString(name) + "&num=1&offset=0";
                string json = HttpGetString(url);
                if (!string.IsNullOrEmpty(json))
                {
                    Match m = Regex.Match(json, "\"id\"\\s*:\\s*(\\d+)");
                    if (m.Success) int.TryParse(m.Groups[1].Value, out found);
                }
            }
            catch { }
            lock (_lookupLock) { _nameIdCache[name] = found; }
            return found;
        }

        private static string SearchYugipediaImage(string name)
        {
            lock (_lookupLock)
            {
                string v;
                if (_yugiCache.TryGetValue(name, out v)) return v;
            }
            string found = null;
            try
            {
                string url = "https://yugipedia.com/api.php?action=query&titles=" + Uri.EscapeDataString(name)
                           + "&prop=pageimages&piprop=original&redirects=1&format=json";
                string json = HttpGetString(url);
                Match m = Regex.Match(json ?? "", "\"source\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success)
                {
                    found = m.Groups[1].Value.Replace("\\/", "/");
                }
                else
                {
                    string url2 = "https://yugipedia.com/api.php?action=query&generator=search&gsrsearch="
                                + Uri.EscapeDataString(name) + "&gsrlimit=1&prop=pageimages&piprop=original&format=json";
                    json = HttpGetString(url2);
                    m = Regex.Match(json ?? "", "\"source\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) found = m.Groups[1].Value.Replace("\\/", "/");
                }
            }
            catch { }
            lock (_lookupLock) { _yugiCache[name] = found; }
            return found;
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Threading;
using WindBotDeckImage.Skia;

namespace WindBotDeckImageAndroid
{
    /// <summary>
    /// 폰에 없는 카드 이미지를 인터넷(YGOPRODeck 이미지 CDN)에서 받아 캐시에 저장한다.
    ///   · EDOPro 안드로이드는 카드 이미지를 **화면에 띄울 때 그때그때 내려받아** pics 폴더에 쌓는다.
    ///     그래서 EDOPro 에서 그 덱을 한 번도 열지 않으면 pics 에 파일이 없다.
    ///   · 이 클래스가 그 빈 자리를 채운다(공식 카드 ID 기준, 커스텀 카드는 404 → 실패로 기록).
    ///   · 캐시: <앱 캐시>/pics/&lt;카드ID&gt;.jpg (한 번 받으면 재사용, 실패도 기억)
    /// </summary>
    public sealed class HttpImageBytes : ICardImageBytes
    {
        private const string UrlTemplate = "https://images.ygoprodeck.com/images/cards/{0}.jpg";

        private readonly string _cacheDir;
        private readonly System.Collections.Generic.HashSet<int> _tried = new System.Collections.Generic.HashSet<int>();
        private int _downloaded;
        private int _failed;

        public int Downloaded { get { return _downloaded; } }

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
        public int Failed { get { return _failed; } }

        public HttpImageBytes(string cacheDir)
        {
            _cacheDir = cacheDir;
            try { Directory.CreateDirectory(_cacheDir); } catch { }
        }

        public byte[] Get(int cardId)
        {
            string path = null;
            try { path = Path.Combine(_cacheDir, cardId + ".jpg"); } catch { }

            // 1) 이미 받아 둔 것
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

            // 2) 이번 실행에서 이미 시도한 카드는 다시 시도하지 않는다
            lock (_tried)
            {
                if (_tried.Contains(cardId)) return null;
                _tried.Add(cardId);
            }

            // 3) 내려받기
            try
            {
                string url = string.Format(UrlTemplate, cardId);
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 8000;
                req.ReadWriteTimeout = 8000;
                req.UserAgent = "WindBotDeckImage/1.4 (Android)";

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    byte[] bytes = ms.ToArray();
                    if (bytes.Length > 512)
                    {
                        if (path != null) { try { File.WriteAllBytes(path, bytes); } catch { } }
                        Interlocked.Increment(ref _downloaded);
                        Android.Util.Log.Info("AI-EDITOR", "img download ok card=" + cardId + " bytes=" + bytes.Length);
                        return bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                if (_failed <= 3)
                    Android.Util.Log.Error("AI-EDITOR", "img download FAIL card=" + cardId + " : " + ex.GetType().Name + " " + ex.Message);
                    FileLog("img download FAIL card=" + cardId + " : " + ex.GetType().Name + " " + ex.Message);
            }
            return null;
        }
    }
}

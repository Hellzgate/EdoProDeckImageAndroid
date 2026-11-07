using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using WindBotDeckImage.Skia;

namespace WindBotDeckImageAndroid
{
    /// <summary>
    /// 카드 이미지 **ZIP** 에서 &lt;카드ID&gt;.jpg/png 를 읽는다.
    ///   · SAF 폴더 선택이 막히는 기기(Android/data 접근 불가 등)를 위한 대안.
    ///   · PC 에서 pics 폴더를 zip 으로 만들어 폰에 넣으면 어떤 파일 관리자로도 고를 수 있다.
    ///   · ZipArchive 는 탐색 가능한 스트림이 필요해서 캐시 파일로 복사한 뒤 연다.
    /// </summary>
    public sealed class ZipImageBytes : ICardImageBytes, IDisposable
    {
        private readonly FileStream _file;
        private readonly ZipArchive _zip;
        private readonly Dictionary<int, ZipArchiveEntry> _map = new Dictionary<int, ZipArchiveEntry>();

        public string ZipName { get; private set; }
        public long ZipBytes { get; private set; }
        public int Count { get { return _map.Count; } }
        public readonly List<string> SampleNames = new List<string>();

        public ZipImageBytes(string cachePath, string displayName)
        {
            ZipName = displayName ?? "pics.zip";
            _file = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            ZipBytes = _file.Length;
            _zip = new ZipArchive(_file, ZipArchiveMode.Read);

            foreach (ZipArchiveEntry e in _zip.Entries)
            {
                if (e.Length == 0) continue;                     // 폴더 항목
                string name = e.FullName;
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                int dot = name.LastIndexOf('.');
                string baseName = dot > 0 ? name.Substring(0, dot) : name;

                int cardId;
                if (!int.TryParse(baseName, out cardId))
                {
                    if (SampleNames.Count < 6 && !string.IsNullOrEmpty(baseName)) SampleNames.Add(name);
                    continue;
                }
                if (_map.ContainsKey(cardId)) continue;
                _map[cardId] = e;
            }
        }

        public byte[] Get(int cardId)
        {
            ZipArchiveEntry e;
            if (!_map.TryGetValue(cardId, out e)) return null;
            try
            {
                using (Stream s = e.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        public string Describe()
        {
            string s = string.Format("{0} ({1:N1}MB) · 카드 {2}장", ZipName, ZipBytes / 1048576.0, _map.Count);
            if (_map.Count == 0 && SampleNames.Count > 0) s += "\r\n예: " + string.Join(", ", SampleNames.ToArray());
            return s;
        }

        public void Dispose()
        {
            try { if (_zip != null) _zip.Dispose(); } catch { }
            try { if (_file != null) _file.Dispose(); } catch { }
        }
    }
}

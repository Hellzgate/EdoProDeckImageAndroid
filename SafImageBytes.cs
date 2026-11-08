using System;
using System.Collections.Generic;
using System.IO;
using Android.Content;
using Android.Provider;
using WindBotDeckImage.Skia;

namespace EdoProDeckImageAndroid
{
    /// <summary>
    /// SAF(Storage Access Framework) 로 고른 폴더에서 &lt;카드ID&gt;.jpg/png 를 읽는다.
    ///   · Android 11+ 저장소 제한 때문에 경로 대신 트리 URI 를 쓴다.
    ///   · **하위 폴더까지** 훑는다(예: pics\official\10000010.jpg). 깊이 3, 항목 8만 개 제한.
    ///   · 인식 실패 원인을 알 수 있게 진단 정보(파일 수·폴더 수·이름 예시)를 남긴다.
    /// </summary>
    public sealed class SafImageBytes : ICardImageBytes
    {
        private const string ColDocumentId = "document_id";
        private const string ColDisplayName = "display_name";
        private const string ColMimeType = "mime_type";
        private const int MaxDepth = 3;
        private const int MaxEntries = 80000;

        private readonly ContentResolver _cr;
        private readonly Dictionary<int, Android.Net.Uri> _map = new Dictionary<int, Android.Net.Uri>();

        public string FolderName { get; private set; }
        public int Count { get { return _map.Count; } }

        // 진단용
        public int ScannedFiles;
        public int ScannedDirs;
        public int ScannedEntries;
        public readonly List<string> SampleNames = new List<string>();
        public bool Truncated;

        public SafImageBytes(ContentResolver cr, Android.Net.Uri treeUri)
        {
            _cr = cr;
            FolderName = "";

            string treeId = DocumentsContract.GetTreeDocumentId(treeUri);
            try
            {
                Android.Net.Uri docUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeId);
                using (var cur = _cr.Query(docUri, new[] { ColDisplayName }, null, null, null))
                    if (cur != null && cur.MoveToFirst()) FolderName = cur.GetString(0) ?? "";
            }
            catch { }

            Scan(treeUri, treeId, treeId, 0);
        }

        private void Scan(Android.Net.Uri treeUri, string rootId, string dirId, int depth)
        {
            if (depth > MaxDepth) return;

            Android.Net.Uri childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, dirId);
            var subDirs = new List<string>();

            try
            {
                using (var cur = _cr.Query(childrenUri, new[] { ColDocumentId, ColDisplayName, ColMimeType }, null, null, null))
                {
                    if (cur == null) return;
                    while (cur.MoveToNext())
                    {
                        if (++ScannedEntries > MaxEntries) { Truncated = true; return; }

                        string docId, name, mime;
                        try { docId = cur.GetString(0); name = cur.GetString(1); mime = cur.GetString(2); }
                        catch { continue; }
                        if (string.IsNullOrEmpty(docId) || string.IsNullOrEmpty(name)) continue;

                        bool isDir = string.Equals(mime, "vnd.android.document/directory", StringComparison.OrdinalIgnoreCase);
                        if (isDir)
                        {
                            ScannedDirs++;
                            if (!name.StartsWith(".") && docId != rootId) subDirs.Add(docId);
                            continue;
                        }

                        ScannedFiles++;
                        int dot = name.LastIndexOf('.');
                        string baseName = dot > 0 ? name.Substring(0, dot) : name;
                        int cardId;
                        if (!int.TryParse(baseName, out cardId))
                        {
                            if (SampleNames.Count < 6) SampleNames.Add(name);
                            continue;
                        }
                        if (_map.ContainsKey(cardId)) continue;

                        try { _map[cardId] = DocumentsContract.BuildDocumentUriUsingTree(treeUri, docId); }
                        catch { }
                    }
                }
            }
            catch { return; }

            foreach (string sub in subDirs)
                Scan(treeUri, rootId, sub, depth + 1);
        }

        public byte[] Get(int cardId)
        {
            Android.Net.Uri uri;
            if (!_map.TryGetValue(cardId, out uri)) return null;
            try
            {
                using (Stream s = _cr.OpenInputStream(uri))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        /// <summary>사용자에게 보여줄 한 줄 요약.</summary>
        public string Describe()
        {
            string folder = string.IsNullOrEmpty(FolderName) ? "(이름 없음)" : FolderName;
            string s = string.Format("{0} · 카드 {1}장 (파일 {2}개, 폴더 {3}개)", folder, _map.Count, ScannedFiles, ScannedDirs);
            if (Truncated) s += " · 일부만 검사";
            if (_map.Count == 0 && SampleNames.Count > 0) s += "\r\n예: " + string.Join(", ", SampleNames.ToArray());
            return s;
        }
    }
}

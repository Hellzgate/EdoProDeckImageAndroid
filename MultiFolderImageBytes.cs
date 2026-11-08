using System;
using System.Collections.Generic;
using System.IO;
using WindBotDeckImage.Skia;

namespace EdoProDeckImageAndroid
{
    /// <summary>
    /// 여러 pics 폴더를 순서대로 보는 이미지 소스.
    ///   · 폰에는 이미지가 <b>여러 곳</b>에 흩어져 있다(EDOPro/pics · Download/pics · ZIP 등).
    ///   · 앞 폴더가 우선이고, 없으면 다음 폴더를 본다.
    /// </summary>
    public sealed class MultiFolderImageBytes : ICardImageBytes
    {
        private readonly string[] _folders;

        public MultiFolderImageBytes(IEnumerable<string> folders)
        {
            var list = new List<string>();
            if (folders != null)
            {
                foreach (string f in folders)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    string t = f.Trim();
                    if (!list.Contains(t)) list.Add(t);
                }
            }
            _folders = list.ToArray();
        }

        public int FolderCount { get { return _folders.Length; } }
        public string FolderLabel
        {
            get
            {
                if (_folders.Length == 0) return "(없음)";
                if (_folders.Length == 1) return _folders[0];
                return _folders[0] + " (+" + (_folders.Length - 1) + "곳)";
            }
        }

        public byte[] Get(int cardId)
        {
            foreach (string dir in _folders)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string ext in new[] { ".jpg", ".png", ".jpeg" })
                {
                    string p = Path.Combine(dir, cardId.ToString() + ext);
                    if (!File.Exists(p)) continue;
                    try
                    {
                        byte[] b = File.ReadAllBytes(p);
                        if (b != null && b.Length > 64) return b;
                    }
                    catch { }
                }
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EdoProDeckImageAndroid
{
    /// <summary>
    /// 카드 DB(cdb) 조회 — 같은 카드의 **원본 ID(alias)** 와 **이름들**.
    ///   · 앱에 내장한 cards.cdb / cards-en.cdb (assets → files/cdb 로 1회 추출)
    ///   · 기기 EDOPro 폴더(/sdcard/EDOPro/expansions, repositories) 가 있으면 함께
    ///   · 결과는 files/cdb-cache.json 에 캐시(다음 실행부터 즉시).
    ///   이미지 폴백(④ 원본 ID, ⑤ 영문 이름)에 쓰인다.
    /// </summary>
    public sealed class CdbLookup : IDisposable
    {
        public sealed class Entry
        {
            public int Alias;
            public readonly List<string> Names = new List<string>();
        }

        private sealed class CacheFile
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, CacheEntry> Entries { get; set; } = new Dictionary<string, CacheEntry>();
        }

        private sealed class CacheEntry
        {
            public int Alias { get; set; }
            public List<string> Names { get; set; } = new List<string>();
        }

        private readonly List<string> _files = new List<string>();
        private readonly Dictionary<int, Entry> _cache = new Dictionary<int, Entry>();
        private readonly object _lock = new object();
        private readonly string _cachePath;
        private bool _dirty;

        private CdbLookup(string cachePath) { _cachePath = cachePath ?? ""; }

        public int CdbCount { get { return _files.Count; } }

        public static CdbLookup Create(IEnumerable<string> cdbFiles, string cachePath)
        {
            var me = new CdbLookup(cachePath);
            me.LoadCache();
            if (cdbFiles != null)
            {
                foreach (string f in cdbFiles)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    try { if (File.Exists(f)) me._files.Add(f); } catch { }
                }
            }
            return me;
        }

        /// <summary>기기에서 볼 수 있는 cdb 후보(있는 것만).</summary>
        public static List<string> FindDeviceCdb()
        {
            var files = new List<string>();
            var roots = new List<string>();
            Action<string> add = delegate (string dir)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (string r in roots)
                    if (string.Equals(r, dir, StringComparison.OrdinalIgnoreCase)) return;
                roots.Add(dir);
            };
            string[] candidates =
            {
                "/sdcard/EDOPro", "/storage/emulated/0/EDOPro", "/storage/emulated/0/Download/EDOPro",
                "/sdcard/Download/EDOPro"
            };
            foreach (string c in candidates) add(c);
            try
            {
                foreach (string d in Directory.GetDirectories("/storage"))
                {
                    if (d.IndexOf("emulated", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    add(d + "/EDOPro");
                }
            }
            catch { }

            foreach (string root in roots)
            {
                Collect(files, Path.Combine(root, "expansions"), false);
                Collect(files, Path.Combine(root, "repositories"), true);
            }
            return files;
        }

        private static void Collect(List<string> files, string dir, bool recursive)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.cdb",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                    if (!files.Contains(f)) files.Add(f);
            }
            catch { }
        }

        public Entry Get(int id)
        {
            lock (_lock)
            {
                Entry e;
                return _cache.TryGetValue(id, out e) ? e : null;
            }
        }

        /// <summary>cdb 마다 한 번씩 조회(없는 카드도 빈 항목으로 기억).</summary>
        public void Prefetch(IEnumerable<int> ids)
        {
            if (ids == null || _files.Count == 0) return;
            var want = new List<int>();
            lock (_lock)
            {
                foreach (int id in ids)
                {
                    if (id <= 0 || _cache.ContainsKey(id) || want.Contains(id)) continue;
                    want.Add(id);
                }
            }
            if (want.Count == 0) return;

            var found = new Dictionary<int, Entry>();
            var paramNames = new string[want.Count];
            for (int i = 0; i < want.Count; i++) paramNames[i] = "$p" + i;

            foreach (string file in _files)
            {
                try
                {
                    var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                    {
                        DataSource = file,
                        Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly
                    };
                    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString()))
                    {
                        conn.Open();
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText =
                                "SELECT t.id, t.name, IFNULL(d.alias,0) FROM texts t " +
                                "LEFT JOIN datas d ON d.id = t.id WHERE t.id IN (" +
                                string.Join(",", paramNames) + ")";
                            for (int i = 0; i < want.Count; i++)
                                cmd.Parameters.AddWithValue(paramNames[i], want[i]);
                            using (var rd = cmd.ExecuteReader())
                            {
                                while (rd.Read())
                                {
                                    int id = rd.GetInt32(0);
                                    Entry e;
                                    if (!found.TryGetValue(id, out e)) { e = new Entry(); found[id] = e; }
                                    if (!rd.IsDBNull(1))
                                    {
                                        string n = rd.GetString(1);
                                        if (!string.IsNullOrEmpty(n) && !e.Names.Contains(n)) e.Names.Add(n);
                                    }
                                    if (!rd.IsDBNull(2))
                                    {
                                        int al;
                                        try { al = rd.GetInt32(2); } catch { al = 0; }
                                        if (al > 0 && e.Alias <= 0) e.Alias = al;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            lock (_lock)
            {
                foreach (int id in want)
                {
                    Entry e;
                    if (found.TryGetValue(id, out e)) _cache[id] = e;
                    else if (!_cache.ContainsKey(id)) _cache[id] = new Entry();
                }
                _dirty = true;
            }
            Save();
        }

        private void Ensure(int id)
        {
            if (Get(id) != null) return;
            Prefetch(new[] { id });
        }

        public int AliasOf(int id)
        {
            Ensure(id);
            Entry e = Get(id);
            return e == null ? 0 : e.Alias;
        }

        /// <summary>이미지 검색용 이름(영문만) — 없으면 빈 목록.</summary>
        public List<string> EnglishNames(int id)
        {
            Ensure(id);
            var list = new List<string>();
            Entry e = Get(id);
            if (e == null) return list;
            lock (_lock)
            {
                foreach (string n in e.Names)
                {
                    if (string.IsNullOrWhiteSpace(n) || n.Length < 3) continue;
                    if (HasHangul(n)) continue;
                    if (!list.Contains(n)) list.Add(n);
                }
            }
            return list;
        }

        public string DisplayName(int id)
        {
            Ensure(id);
            Entry e = Get(id);
            if (e == null) return "";
            lock (_lock)
            {
                string other = null;
                foreach (string n in e.Names)
                {
                    if (string.IsNullOrWhiteSpace(n)) continue;
                    if (HasHangul(n)) return n;
                    if (other == null) other = n;
                }
                return other ?? "";
            }
        }

        public static bool HasHangul(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
                if (c >= 0xAC00 && c <= 0xD7A3) return true;
            return false;
        }

        private void LoadCache()
        {
            if (_cachePath.Length == 0 || !File.Exists(_cachePath)) return;
            try
            {
                CacheFile c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath));
                if (c == null || c.Version != 1 || c.Entries == null) return;
                lock (_lock)
                {
                    foreach (KeyValuePair<string, CacheEntry> kv in c.Entries)
                    {
                        int id;
                        if (!int.TryParse(kv.Key, out id)) continue;
                        var e = new Entry();
                        e.Alias = kv.Value.Alias;
                        if (kv.Value.Names != null)
                            foreach (string n in kv.Value.Names)
                                if (!string.IsNullOrEmpty(n) && !e.Names.Contains(n)) e.Names.Add(n);
                        _cache[id] = e;
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            if (_cachePath.Length == 0) return;
            CacheFile c = null;
            lock (_lock)
            {
                if (!_dirty) return;
                c = new CacheFile();
                foreach (KeyValuePair<int, Entry> kv in _cache)
                {
                    var ce = new CacheEntry { Alias = kv.Value.Alias };
                    ce.Names.AddRange(kv.Value.Names);
                    c.Entries[kv.Key.ToString()] = ce;
                }
            }
            try
            {
                string dir = Path.GetDirectoryName(_cachePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = _cachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(c));
                if (File.Exists(_cachePath)) File.Delete(_cachePath);
                File.Move(tmp, _cachePath);
                lock (_lock) { _dirty = false; }
            }
            catch { }
        }

        public void Dispose() { Save(); }
    }
}

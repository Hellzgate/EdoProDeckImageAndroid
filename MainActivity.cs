using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using WindBotDeckImage.Skia;
using SkiaImageStore = WindBotDeckImage.Skia.DeckImageSkiaRenderer.SkiaImageStore;

namespace EdoProDeckImageAndroid
{
    [Activity(Label = "EDOPro 덱 이미지", MainLauncher = true,
        Theme = "@android:style/Theme.Material.Light.DarkActionBar",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public sealed class MainActivity : Activity
    {
        private const int ReqDeck = 101;
        private const int ReqPics = 102;
        private const int ReqSave = 103;
        private const int ReqZip = 104;
        private const int ReqAllFiles = 105;

        private static readonly int[] ColsValues = { 6, 8, 10, 12 };
        private static readonly string[] ThemeValues = { "다크", "라이트" };

        private ISharedPreferences _prefs;
        private SkiaDeck _deck;
        private ICardImageBytes _images;
        private SkiaImageStore _store;
        private SkiaImageStore _retiredStore;
        private byte[] _savePng;
        private string _srcLabel;
        private ZipImageBytes _zipImages;
        private string _pendingPath;
        private bool _setupShown;
        private Bitmap _previewBmp;
        private string _deckName = "";
        private bool _busy;
        private CdbLookup _cdb;

        private TextView _status;
        private ImageView _preview;
        private ProgressBar _busyBar;
        private SeekBar _cardWidth;
        private TextView _cardWidthText;
        private Spinner _cols, _theme;
        private CheckBox _counts, _side;
        private EditText _title;
        private TextView _picsLabel;

        private Handler _ui;
        private bool _renderPending;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            _prefs = GetSharedPreferences("deckimage", FileCreationMode.Private);
            _ui = new Handler(Looper.MainLooper);

            SetContentView(BuildUi());
            LogMsg("start data=" + AppDataDir() + " cache=" + ImageCacheDir());
            _cdb = CreateCdb();
            if (_cdb != null) LogMsg("cdb files=" + _cdb.CdbCount);
            RestoreSettings();

            string pathPics = _prefs.GetString("pathPics", null);
            // 기본 경로 자동 인식 (EDOPro 안드로이드가 쓰는 위치) — 찾은 폴더를 **전부** 쓴다
            if (string.IsNullOrEmpty(pathPics) && HasAllFiles())
            {
                List<string> auto = FindImageFolders();
                if (auto.Count > 0) pathPics = string.Join(";", auto.ToArray());
            }
            string zipUri = _prefs.GetString("zipUri", null);
            string picsUri = _prefs.GetString("picsUri", null);
            if (!string.IsNullOrEmpty(pathPics) && HasAllFiles() && AnyFolderExists(pathPics))
                LoadPathImages(pathPics);
            else if (!string.IsNullOrEmpty(zipUri)) LoadZipAsync(Android.Net.Uri.Parse(zipUri));
            else if (!string.IsNullOrEmpty(picsUri))
            {
                var pu = Android.Net.Uri.Parse(picsUri);
                if (HasPersisted(pu)) LoadPicsAsync(pu, false);
                else
                {
                    Status("이미지 폴더 접근 권한이 만료되었습니다.\r\n[카드 이미지] 를 다시 눌러 폴더를 선택해 주세요. (또는 [이미지 ZIP])");
                    Android.Util.Log.Info("AI-EDITOR", "pics grant expired uri=" + picsUri + " grants=" + PersistedCount());
                }
            }

            string deckUri = _prefs.GetString("deckUri", null);
            if (!string.IsNullOrEmpty(deckUri)) LoadDeckAsync(Android.Net.Uri.Parse(deckUri), false);
            else Status("① [덱 열기] 로 .ydk 를 고르세요  →  ② [카드 이미지] 로 EDOPro pics 폴더");

            // ── 검증용(테스트 모드) ──
            //   adb 로 앱 전용 폴더(files/)에 덱·이미지를 넣고 자동 렌더 → files/<out> 저장.
            //   일반 실행에는 영향 없음(엑스트라가 있을 때만 동작).
            string testDeck = Intent != null ? Intent.GetStringExtra("testDeck") : null;
            if (!string.IsNullOrEmpty(testDeck))
                RunSelfTest(testDeck, Intent.GetStringExtra("testPics"),
                    Intent.GetStringExtra("testOut"), Intent.GetStringExtra("testZip"), Intent.GetStringExtra("testPath"));
        }

        private string BaseDir()
        {
            // adb 로 넣기 쉬운 외부 앱 폴더 우선, 없으면 내부 저장소
            Java.IO.File ext = GetExternalFilesDir(null);
            if (ext != null) return ext.AbsolutePath;
            return FilesDir.AbsolutePath;
        }

        private void RunSelfTest(string deckFile, string picsDir, string outFile, string zipFile, string pathDir)
        {
            if (string.IsNullOrEmpty(outFile)) outFile = "selftest.png";
            string baseDir = BaseDir();
            string picsPath = System.IO.Path.Combine(baseDir, string.IsNullOrEmpty(picsDir) ? "pics" : picsDir);
            string deckPath = System.IO.Path.Combine(baseDir, deckFile);
            string outPath = System.IO.Path.Combine(baseDir, outFile);

            Task.Run(delegate
            {
                string result;
                try
                {
                    string text = File.ReadAllText(deckPath, Encoding.UTF8);
                    SkiaDeck deck = DeckImageSkiaRenderer.ParseYdk(text, deckFile);
                    var opt = new SkiaRenderOptions
                    {
                        Title = "시흑양신 (단말 검증)",
                        Columns = 10,
                        CardWidth = 110,
                        Gap = 5,
                        Theme = "dark",
                        ShowCounts = false
                    };
                    // 검증용: adb --es testTitle "…" 로 제목/푸터를 바꿔 볼 수 있다(긴 제목 잘림 확인)
                    string tTitle = Intent != null ? Intent.GetStringExtra("testTitle") : null;
                    if (!string.IsNullOrEmpty(tTitle)) opt.Title = tTitle;
                    string tNote = Intent != null ? Intent.GetStringExtra("testNote") : null;
                    if (!string.IsNullOrEmpty(tNote)) opt.FooterNote = tNote;
                    SkiaRenderStats st;
                    byte[] png;
                    string srcInfo;
                    ICardImageBytes src;
                    if (!string.IsNullOrEmpty(pathDir))
                    {
                        src = new MultiFolderImageBytes(SplitPathList(pathDir));
                        srcInfo = "path=" + pathDir;
                    }
                    else if (!string.IsNullOrEmpty(zipFile))
                    {
                        src = new ZipImageBytes(System.IO.Path.Combine(baseDir, zipFile), zipFile);
                        srcInfo = "zip=" + zipFile;
                    }
                    else
                    {
                        src = new FolderImageBytes(picsPath);
                        srcInfo = "folder";
                    }

                    var ids = new HashSet<int>();
                    foreach (int id in deck.Main) ids.Add(id);
                    foreach (int id in deck.Extra) ids.Add(id);
                    foreach (int id in deck.Side) ids.Add(id);

                    var swPre = System.Diagnostics.Stopwatch.StartNew();
                    ICardImageBytes netSrc = null;
                    if (_prefs.GetBoolean("netImages", true))
                    {
                        var h = new HttpImageBytes(ImageCacheDir());
                        h.Cdb = _cdb;
                        netSrc = HttpImageLogger(h);
                    }
                    try { if (_cdb != null) _cdb.Prefetch(ids); } catch { }
                    using (var store = new SkiaImageStore(src, netSrc))
                    {
                        store.Preload(new List<int>(ids), null);
                        long preMs = swPre.ElapsedMilliseconds;

                        var sw1 = System.Diagnostics.Stopwatch.StartNew();
                        int w1, h1;
                        DeckImageSkiaRenderer.RenderRgba(deck, opt, store, out st, out w1, out h1);
                        long r1 = sw1.ElapsedMilliseconds;

                        var sw2 = System.Diagnostics.Stopwatch.StartNew();
                        int w2, h2;
                        DeckImageSkiaRenderer.RenderRgba(deck, opt, store, out st, out w2, out h2);
                        long r2 = sw2.ElapsedMilliseconds;

                        var sw3 = System.Diagnostics.Stopwatch.StartNew();
                        png = DeckImageSkiaRenderer.RenderToPng(deck, opt, store, out st);
                        long encMs = sw3.ElapsedMilliseconds;
                        var sw4 = System.Diagnostics.Stopwatch.StartNew();
                        using (Bitmap pb = BitmapFactory.DecodeByteArray(png, 0, png.Length))
                            if (pb != null) { }
                        long decMs = sw4.ElapsedMilliseconds;
                        srcInfo += string.Format(" | preload={0}ms(카드 {1}장, 인터넷 {2}장) render1={3}ms render2={4}ms png={5}+{6}ms", preMs, ids.Count, store.FromFallback, r1, r2, encMs, decMs);
                    }
                    File.WriteAllBytes(outPath, png);
                    result = string.Format("OK {0} | {1} | placed={2} | missing={3} | png={4}KB | cdb={5} | face={6} | {7}",
                        st.Width + "x" + st.Height, st.Summary, st.Placed, st.Missing.Count, png.Length / 1024,
                        _cdb == null ? 0 : _cdb.CdbCount,
                        DeckImageSkiaRenderer.TypefaceInfo(true), srcInfo);
                }
                catch (Exception ex)
                {
                    result = "FAIL " + ex;
                }
                Android.Util.Log.Info("AI-EDITOR", "selftest " + result);
                RunOnUiThread(delegate
                {
                    Status("검증: " + result);
                    Toast.MakeText(this, result, ToastLength.Long).Show();
                });
            });
        }

        // ══════════════════════════════════════════════════════
        //  화면
        // ══════════════════════════════════════════════════════
        // 2026-11-07: PC 판과 같은 다크 테마 토큰
        private static readonly Color UiBg = Color.ParseColor("#1E1F22");
        private static readonly Color UiPanel = Color.ParseColor("#26282C");
        private static readonly Color UiBox = Color.ParseColor("#2D2F34");
        private static readonly Color UiFg = Color.ParseColor("#E8EAEE");
        private static readonly Color UiSub = Color.ParseColor("#9EA4AE");
        private static readonly Color UiAccent = Color.ParseColor("#569CD6");

        /// <summary>상태바·액션바까지 다크로 (앱 화면과 톤을 맞춘다).</summary>
        private void ApplyWindowTheme()
        {
            try
            {
                Window.SetStatusBarColor(UiPanel);
                Window.SetNavigationBarColor(UiPanel);
                if (ActionBar != null)
                {
                    ActionBar.SetBackgroundDrawable(new Android.Graphics.Drawables.ColorDrawable(UiPanel));
                    var title = new Android.Text.SpannableString(ActionBar.Title ?? "EDOPro 덱 이미지");
                    title.SetSpan(new Android.Text.Style.ForegroundColorSpan(UiFg), 0, title.Length(),
                        Android.Text.SpanTypes.ExclusiveExclusive);
                    ActionBar.TitleFormatted = title;
                }
            }
            catch { }
        }

        private View BuildUi()
        {
            ApplyWindowTheme();
            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(UiBg);

            // 도구 버튼 줄
            var tools = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            tools.SetPadding(Dp(6), Dp(6), Dp(6), Dp(2));
            var btnDeck = MakeButton("덱 열기");
            btnDeck.Click += delegate { PickDeck(); };
            var btnPics = MakeButton("카드 이미지");
            btnPics.Click += delegate { PickPics(); };
            var btnZip = MakeButton("이미지 ZIP");
            btnZip.Click += delegate { PickZip(); };
            var btnSave = MakeButton("저장", true);
            btnSave.Click += delegate { SaveImage(); };
            tools.AddView(btnDeck);
            tools.AddView(btnPics);
            tools.AddView(btnZip);
            tools.AddView(btnSave);
            root.AddView(tools, Row(-2, -2));

            // 상태
            _status = new TextView(this) { TextSize = 12f };
            _status.SetTextColor(UiSub);
            _status.SetPadding(Dp(10), Dp(2), Dp(10), Dp(4));
            _status.SetMaxLines(3);
            root.AddView(_status, Row(-1, -2));

            // 옵션 줄 (가로 스크롤)
            var optRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            optRow.SetPadding(Dp(8), 0, Dp(8), Dp(4));
            optRow.SetGravity(GravityFlags.CenterVertical);

            optRow.AddView(Label("카드 폭"));
            _cardWidth = new SeekBar(this) { Max = 140 };   // 60~200
            _cardWidth.Progress = 50;                      // 110
            _cardWidth.LayoutParameters = new LinearLayout.LayoutParams(Dp(118), -2);
            _cardWidth.ProgressChanged += delegate { RequestRenderSoon(); UpdateWidthLabel(); };
            optRow.AddView(_cardWidth);
            _cardWidthText = Label("110");
            _cardWidthText.SetMinWidth(Dp(26));
            optRow.AddView(_cardWidthText);

            optRow.AddView(Label("열"));
            _cols = new Spinner(this);
            var colsAdapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem,
                new string[] { "6", "8", "10", "12" });
            colsAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _cols.Adapter = colsAdapter;
            _cols.SetSelection(2);
            _cols.LayoutParameters = new LinearLayout.LayoutParams(Dp(80), -2);
            _cols.Background = BoxDrawable();
            _cols.SetPadding(Dp(8), 0, Dp(4), 0);
            _cols.ItemSelected += delegate { RequestRenderSoon(); };
            optRow.AddView(_cols);

            var optScroll = new HorizontalScrollView(this);
            optScroll.AddView(optRow);
            root.AddView(optScroll, Row(-1, -2));

            // 체크 옵션 + 테마 줄 (좁은 화면에서 잘리지 않게 따로)
            var checksRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            checksRow.SetPadding(Dp(10), 0, Dp(8), Dp(2));
            checksRow.SetGravity(GravityFlags.CenterVertical);
            _counts = new CheckBox(this) { Text = "x2/x3 배지" };
            _counts.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(UiAccent);
            _counts.TextSize = 12f;
            _counts.SetTextColor(UiFg);
            _counts.CheckedChange += delegate { RequestRenderSoon(); };
            checksRow.AddView(_counts);

            _side = new CheckBox(this) { Text = "사이드", Checked = true };
            _side.ButtonTintList = Android.Content.Res.ColorStateList.ValueOf(UiAccent);
            _side.TextSize = 12f;
            _side.SetTextColor(UiFg);
            _side.CheckedChange += delegate { RequestRenderSoon(); };
            checksRow.AddView(_side);

            checksRow.AddView(Label("테마"));
            _theme = new Spinner(this);
            var themeAdapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerItem, ThemeValues);
            themeAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _theme.Adapter = themeAdapter;
            _theme.SetSelection(0);
            _theme.LayoutParameters = new LinearLayout.LayoutParams(Dp(92), -2);
            _theme.Background = BoxDrawable();
            _theme.SetPadding(Dp(8), 0, Dp(4), 0);
            _theme.ItemSelected += delegate { RequestRenderSoon(); };
            checksRow.AddView(_theme);

            root.AddView(checksRow, Row(-1, -2));

            // 제목 입력
            var titleRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            titleRow.SetPadding(Dp(8), 0, Dp(8), Dp(4));
            titleRow.SetGravity(GravityFlags.CenterVertical);
            titleRow.AddView(Label("제목"));
            _title = new EditText(this) { Hint = "(비우면 덱 파일명)" };
            _title.TextSize = 13f;
            _title.SetTextColor(UiFg);
            _title.SetHintTextColor(UiSub);
            _title.SetSingleLine(true);
            _title.LayoutParameters = new LinearLayout.LayoutParams(0, -2, 1f);
            _title.TextChanged += delegate { RequestRenderSoon(); };
            titleRow.AddView(_title);
            root.AddView(titleRow, Row(-1, -2));

            // 미리보기
            _preview = new ImageView(this);
            _preview.SetAdjustViewBounds(true);
            _preview.SetScaleType(ImageView.ScaleType.FitCenter);
            _preview.SetPadding(Dp(4), Dp(4), Dp(4), Dp(8));

            var previewScroll = new ScrollView(this);
            previewScroll.AddView(_preview, new FrameLayout.LayoutParams(-1, -2));
            root.AddView(previewScroll, new LinearLayout.LayoutParams(-1, 0, 1f));

            _busyBar = new ProgressBar(this) { Indeterminate = true, Visibility = ViewStates.Gone };
            _busyBar.LayoutParameters = new LinearLayout.LayoutParams(Dp(36), Dp(36)) { Gravity = GravityFlags.Center };
            root.AddView(_busyBar, Row(-1, -2));

            _picsLabel = new TextView(this) { TextSize = 11f };
            _picsLabel.SetTextColor(UiSub);
            _picsLabel.SetPadding(Dp(10), 0, Dp(6), Dp(6));
            _picsLabel.Text = "폴더가 인식되지 않으면 여기를 누르세요 (폴더 경로로 읽기)";
            _picsLabel.Click += delegate { ShowImageSetupDialog(); };
            root.AddView(_picsLabel, Row(-1, -2));

            return root;
        }

        private static LinearLayout.LayoutParams Row(int w, int h)
        {
            return new LinearLayout.LayoutParams(w, h);
        }

        /// <summary>입력칸 모양(둥근 사각형 + 테두리) — 스피너가 기본 밑줄로 보이던 것 교체.</summary>
        private Android.Graphics.Drawables.GradientDrawable BoxDrawable()
        {
            var g = new Android.Graphics.Drawables.GradientDrawable();
            g.SetColor(UiBox);
            g.SetCornerRadius(Dp(6));
            g.SetStroke(Dp(1), Color.ParseColor("#4A4D55"));
            return g;
        }

        private Button MakeButton(string text, bool primary = false)
        {
            var b = new Button(this) { Text = text };
            b.TextSize = 13f;
            b.SetMinWidth(0);
            b.SetMinimumWidth(0);
            b.SetAllCaps(false);
            b.SetTextColor(primary ? Color.White : UiFg);
            var bg = new Android.Graphics.Drawables.GradientDrawable();
            bg.SetColor(primary ? UiAccent : UiBox);
            bg.SetCornerRadius(Dp(8));
            bg.SetStroke(Dp(1), primary ? UiAccent : Color.ParseColor("#464A52"));
            b.Background = bg;            // SetBackground 확장은 AndroidX 필요 → 속성 사용
            b.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
            var lp = new LinearLayout.LayoutParams(-2, -2);
            lp.RightMargin = Dp(6);
            b.LayoutParameters = lp;
            return b;
        }

        private TextView Label(string text)
        {
            var t = new TextView(this) { Text = text };
            t.TextSize = 12f;
            t.SetTextColor(UiSub);
            t.SetPadding(Dp(2), 0, Dp(6), 0);
            return t;
        }

        private int Dp(int v)
        {
            return (int)Math.Round(v * Resources.DisplayMetrics.Density);
        }

        // ══════════════════════════════════════════════════════
        //  설정
        // ══════════════════════════════════════════════════════
        private void RestoreSettings()
        {
            _cardWidth.Progress = _prefs.GetInt("cardW", 110) - 60;
            int cols = _prefs.GetInt("cols", 10);
            int idx = Array.IndexOf(ColsValues, cols);
            _cols.SetSelection(idx >= 0 ? idx : 2);
            _theme.SetSelection(_prefs.GetInt("theme", 0));
            _counts.Checked = _prefs.GetBoolean("counts", false);
            _side.Checked = _prefs.GetBoolean("side", true);
            _title.Text = _prefs.GetString("title", "") ?? "";
            UpdateWidthLabel();
        }

        private void SaveSettings()
        {
            var e = _prefs.Edit();
            e.PutInt("cardW", CardWidth);
            e.PutInt("cols", ColsValues[Math.Max(0, _cols.SelectedItemPosition)]);
            e.PutInt("theme", Math.Max(0, _theme.SelectedItemPosition));
            e.PutBoolean("counts", _counts.Checked);
            e.PutBoolean("side", _side.Checked);
            e.PutString("title", _title.Text ?? "");
            e.Apply();
        }

        private int CardWidth { get { return 60 + _cardWidth.Progress; } }

        private void UpdateWidthLabel()
        {
            _cardWidthText.Text = CardWidth.ToString();
        }

        private SkiaRenderOptions BuildOptions()
        {
            return new SkiaRenderOptions
            {
                Title = (_title.Text ?? "").Trim(),
                Columns = ColsValues[Math.Max(0, Math.Min(ColsValues.Length - 1, _cols.SelectedItemPosition))],
                CardWidth = CardWidth,
                Gap = 5,
                Padding = 18,
                Theme = _theme.SelectedItemPosition == 1 ? "light" : "dark",
                ShowCounts = _counts.Checked,
                ShowSide = _side.Checked
            };
        }

        // ══════════════════════════════════════════════════════
        //  파일 선택
        // ══════════════════════════════════════════════════════
        // 카드 이미지/로그는 **앱 전용 폴더**(files)에 둔다
        //   /sdcard/Android/data/com.hellzgate.windbotdeckimage/files/{pics-cache, logs}
        private string AppDataDir()
        {
            try
            {
                Java.IO.File ext = GetExternalFilesDir(null);
                if (ext != null) return ext.AbsolutePath;
            }
            catch { }
            return FilesDir.AbsolutePath;
        }

        private string ImageCacheDir()
        {
            string d = System.IO.Path.Combine(AppDataDir(), "pics-cache");
            try { System.IO.Directory.CreateDirectory(d); } catch { }
            return d;
        }

        // ── 이미지 폴더 목록(';' 로 여러 곳) ─────────────────────
        private static List<string> SplitPathList(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string part in raw.Split(';'))
            {
                string t = part.Trim();
                if (t.Length == 0) continue;
                if (!list.Contains(t)) list.Add(t);
            }
            return list;
        }

        private static bool AnyFolderExists(string raw)
        {
            foreach (string p in SplitPathList(raw))
                if (System.IO.Directory.Exists(p)) return true;
            return false;
        }

        // ── 카드 DB(cdb) — 내장 + 기기 EDOPro ────────────────────
        /// <summary>앱에 내장한 cards.cdb/cards-en.cdb 를 files/cdb 로 1회 풀고, 기기 EDOPro cdb 도 모은다.</summary>
        private CdbLookup CreateCdb()
        {
            var files = new List<string>();
            try { files.AddRange(ExtractBundledCdb()); } catch { }
            try { if (HasAllFiles()) files.AddRange(CdbLookup.FindDeviceCdb()); } catch { }
            try
            {
                string cache = System.IO.Path.Combine(AppDataDir(), "cdb-cache.json");
                return CdbLookup.Create(files, cache);
            }
            catch { return null; }
        }

        private List<string> ExtractBundledCdb()
        {
            var list = new List<string>();
            string dir = System.IO.Path.Combine(AppDataDir(), "cdb");
            System.IO.Directory.CreateDirectory(dir);
            string stampPath = System.IO.Path.Combine(dir, ".stamp");
            string ver = "";
            try
            {
                var pi = PackageManager.GetPackageInfo(PackageName, 0);
                ver = pi.VersionName + "/" + pi.VersionCode;
            }
            catch { ver = "1"; }

            bool fresh = false;
            try { fresh = System.IO.File.Exists(stampPath) && System.IO.File.ReadAllText(stampPath).Trim() == ver; } catch { }

            string[] names = new string[0];
            try { names = Assets.List("carddata") ?? new string[0]; } catch { }
            foreach (string name in names)
            {
                if (!name.EndsWith(".cdb", StringComparison.OrdinalIgnoreCase)) continue;
                string dst = System.IO.Path.Combine(dir, name);
                if (!fresh)
                {
                    try
                    {
                        using (Stream s = Assets.Open("carddata/" + name))
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            byte[] bytes = ms.ToArray();
                            if (bytes.Length > 1024) System.IO.File.WriteAllBytes(dst, bytes);
                        }
                    }
                    catch (Exception ex) { LogMsg("asset cdb FAIL " + name + " : " + ex.Message); }
                }
                if (System.IO.File.Exists(dst)) list.Add(dst);
            }
            if (!fresh && list.Count > 0)
            {
                try { System.IO.File.WriteAllText(stampPath, ver); } catch { }
            }
            return list;
        }

        // 간단한 파일 로그(오류 위주). 1MB 넘으면 app.log.old 로 한 번 밀어 둔다.
        private void LogMsg(string message)
        {
            try
            {
                string dir = System.IO.Path.Combine(AppDataDir(), "logs");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "app.log");
                try
                {
                    var fi = new Java.IO.File(path);
                    if (fi.Exists() && fi.Length() > 1048576)
                        System.IO.File.Copy(path, path + ".old", true);
                }
                catch { }
                System.IO.File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n");
            }
            catch { }
        }

        private HttpImageBytes HttpImageLogger(HttpImageBytes h)
        {
            try { h.LogPath = System.IO.Path.Combine(AppDataDir(), "logs", "app.log"); } catch { }
            return h;
        }

        private int PersistedCount()
        {
            try { return ContentResolver.PersistedUriPermissions != null ? ContentResolver.PersistedUriPermissions.Count : 0; }
            catch { return -1; }
        }

        private bool HasPersisted(Android.Net.Uri uri)
        {
            if (uri == null) return false;
            try
            {
                foreach (var p in ContentResolver.PersistedUriPermissions)
                    if (p.Uri != null && p.Uri.Equals(uri) && p.IsReadPermission) return true;
            }
            catch { }
            return false;
        }

        // 파일 관리자를 여러 개 설치한 경우 "앱 선택" 창을 띄운다(서드파티 파일 관리자 선택용).
        private void StartPicker(Intent intent, int requestCode)
        {
            try
            {
                var handlers = PackageManager.QueryIntentActivities(intent, 0);
                if (handlers != null && handlers.Count > 1)
                    StartActivityForResult(Intent.CreateChooser(intent, "파일 관리자 선택"), requestCode);
                else
                    StartActivityForResult(intent, requestCode);
            }
            catch { StartActivityForResult(intent, requestCode); }
        }

        private void PickDeck()
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            StartPicker(intent, ReqDeck);
        }

        private void PickPics()
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            intent.PutExtra("android.content.extra.SHOW_ADVANCED", true);
            // chooser 로 감쌀 때 권한이 사라지지 않도록 플래그를 직접 넣는다
            intent.AddFlags(ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(ActivityFlags.GrantPersistableUriPermission);
            StartPicker(intent, ReqPics);
        }

        private void PickZip()
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("application/zip");
            intent.PutExtra(Intent.ExtraMimeTypes, new string[] { "application/zip", "application/x-zip-compressed", "*/*" });
            StartPicker(intent, ReqZip);
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (resultCode != Result.Ok || data == null) return;

            if (requestCode == ReqDeck)
            {
                Android.Net.Uri uri = data.Data;
                if (uri == null) return;
                try { ContentResolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission); } catch { }
                var e = _prefs.Edit(); e.PutString("deckUri", uri.ToString()); e.Apply();
                LoadDeckAsync(uri, true);
            }
            else if (requestCode == ReqPics)
            {
                Android.Net.Uri uri = data.Data;
                if (uri == null) return;
                try
                {
                    ActivityFlags takeFlags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
                    if ((int)takeFlags == 0) takeFlags = ActivityFlags.GrantReadUriPermission;
                    ContentResolver.TakePersistableUriPermission(uri, takeFlags);
                    Android.Util.Log.Info("AI-EDITOR", "pics grant ok flags=" + (int)takeFlags + " uri=" + uri);
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("AI-EDITOR", "pics grant FAIL " + ex.Message + " uri=" + uri);
                    LogMsg("pics grant FAIL " + ex.Message + " uri=" + uri);
                }
                Android.Util.Log.Info("AI-EDITOR", "persisted grants=" + PersistedCount() + " hasTree=" + HasPersisted(uri));
                var e = _prefs.Edit(); e.PutString("picsUri", uri.ToString()); e.Apply();
                LoadPicsAsync(uri, true);
            }
            else if (requestCode == ReqZip)
            {
                Android.Net.Uri uri = data.Data;
                if (uri == null) return;
                try { ContentResolver.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission); } catch { }
                var e = _prefs.Edit(); e.PutString("zipUri", uri.ToString()); e.Remove("picsUri"); e.Apply();
                LoadZipAsync(uri);
            }
            else if (requestCode == ReqSave)
            {
                Android.Net.Uri uri = data.Data;
                if (uri == null || _savePng == null) return;
                try
                {
                    using (Stream os = ContentResolver.OpenOutputStream(uri))
                    {
                        os.Write(_savePng, 0, _savePng.Length);
                        os.Flush();
                    }
                    Toast.MakeText(this, "저장했습니다", ToastLength.Short).Show();
                    Status("저장 완료: " + uri.LastPathSegment);
                }
                catch (Exception ex)
                {
                    Toast.MakeText(this, "저장 실패: " + ex.Message, ToastLength.Long).Show();
                }
            }
        }

        // ══════════════════════════════════════════════════════
        //  불러오기
        // ══════════════════════════════════════════════════════
        private void LoadDeckAsync(Android.Net.Uri uri, bool announce)
        {
            Status("덱 읽는 중…");
            Task.Run(delegate
            {
                try
                {
                    string text, name;
                    using (Stream s = ContentResolver.OpenInputStream(uri))
                    using (var sr = new StreamReader(s, Encoding.UTF8))
                        text = sr.ReadToEnd();
                    name = QueryDisplayName(uri);
                    if (string.IsNullOrEmpty(name)) name = uri.LastPathSegment ?? "deck.ydk";

                    SkiaDeck deck = DeckImageSkiaRenderer.ParseYdk(text, name);
                    RunOnUiThread(delegate
                    {
                        _deck = deck;
                        _deckName = System.IO.Path.GetFileNameWithoutExtension(name);
                        if (string.IsNullOrWhiteSpace(_title.Text)) _title.Text = _deckName;
                        Android.Util.Log.Info("AI-EDITOR", string.Format("deck loaded name={0} main={1} extra={2} side={3} uri={4}",
                            name, deck.Main.Count, deck.Extra.Count, deck.Side.Count, uri));
                        Status(string.Format("덱: {0} — 메인 {1} / 엑스트라 {2} / 사이드 {3}",
                            name, deck.Main.Count, deck.Extra.Count, deck.Side.Count));
                        RequestRenderSoon();
                        StartPreload();
                    });
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("AI-EDITOR", "deck read FAIL " + ex);
                    LogMsg("deck read FAIL " + ex);
                    RunOnUiThread(delegate
                    {
                        Status("덱을 읽지 못했습니다: " + ex.Message);
                        Toast.MakeText(this, "덱을 읽지 못했습니다.\r\n" + ex.Message, ToastLength.Long).Show();
                    });
                }
            });
        }

        private string QueryDisplayName(Android.Net.Uri uri)
        {
            try
            {
                using (var cur = ContentResolver.Query(uri, new[] { OpenableColumns.DisplayName }, null, null, null))
                    if (cur != null && cur.MoveToFirst()) return cur.GetString(0);
            }
            catch { }
            return null;
        }

        private void LoadPicsAsync(Android.Net.Uri treeUri, bool announce)
        {
            Status("이미지 폴더 목록을 읽는 중… (수만 개면 몇 초 걸립니다)");
            _picsLabel.Text = "";
            Task.Run(delegate
            {
                try
                {
                    var images = new SafImageBytes(ContentResolver, treeUri);
                    RunOnUiThread(delegate
                    {
                        Android.Util.Log.Info("AI-EDITOR", "pics ok cards=" + images.Count + " files=" + images.ScannedFiles
                            + " dirs=" + images.ScannedDirs + " folder=" + images.FolderName + " uri=" + treeUri);
                        SetImageSource(images, "이미지 폴더: " + images.Describe());
                        if (announce && images.Count == 0)
                            Toast.MakeText(this,
                                "이 폴더에서 <카드ID>.jpg 를 찾지 못했습니다.\r\n" + images.Describe() +
                                "\r\n\r\n· 권한이 만료됐다면 [카드 이미지] 를 다시 눌러 폴더를 재선택\r\n" +
                                "· 안 되면 폰의 Download 로 pics 폴더를 복사해 선택하거나 [이미지 ZIP] 사용",
                                ToastLength.Long).Show();
                        RequestRenderSoon();
                    });
                }
                catch (Exception ex)
                {
                    RunOnUiThread(delegate { Status("이미지 폴더를 읽지 못했습니다: " + ex.Message); });
                }
            });
        }

        // ── 경로 모드 (모든 파일 액세스) ──────────────────
        private bool HasAllFiles()
        {
            try
            {
                if ((int)Build.VERSION.SdkInt < 30) return true;
                return Android.OS.Environment.IsExternalStorageManager;
            }
            catch { return false; }
        }

        private void AskAllFiles()
        {
            try
            {
                var i = new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission);
                i.SetData(Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(i);
                Toast.MakeText(this, "이 앱에 '모든 파일 액세스' 를 허용한 뒤 돌아오세요", ToastLength.Long).Show();
            }
            catch (Exception ex)
            {
                Status("설정을 열 수 없습니다: " + ex.Message);
            }
        }

        private static string[] ImagePathCandidates()
        {
            var list = new List<string>();
            string ext = null;
            try { ext = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath; } catch { }
            if (!string.IsNullOrEmpty(ext))
            {
                list.Add(ext + "/EDOPro/pics");
                list.Add(ext + "/Download/pics");
                list.Add(ext + "/Pictures/pics");
                list.Add(ext + "/Download/EDOPro/pics");
                list.Add(ext + "/Android/data/io.github.edo9300.edopro/files/pics");
            }
            list.Add("/sdcard/EDOPro/pics");
            list.Add("/sdcard/Download/pics");
            list.Add("/sdcard/Pictures/pics");
            try
            {
                foreach (string d in System.IO.Directory.GetDirectories("/storage"))
                {
                    if (d.IndexOf("emulated", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    list.Add(d + "/EDOPro/pics");
                    list.Add(d + "/Download/pics");
                }
            }
            catch { }
            var uniq = new List<string>();
            foreach (string s in list) if (!uniq.Contains(s)) uniq.Add(s);
            return uniq.ToArray();
        }

        private static int CountJpg(string dir)
        {
            try
            {
                int n = 0;
                foreach (string f in System.IO.Directory.EnumerateFiles(dir, "*.jpg")) n++;
                foreach (string f in System.IO.Directory.EnumerateFiles(dir, "*.png")) n++;
                return n;
            }
            catch { return -1; }
        }

        private List<string> FindImageFolders()
        {
            var found = new List<string>();
            foreach (string p in ImagePathCandidates())
            {
                int n = CountJpg(p);
                if (n > 0) found.Add(p);
            }
            return found;
        }

        // 이미지가 없을 때: 자동 탐색 결과 / 권한 허용 / 폴더 직접 입력 / ZIP 을 한 창에서 안내
        private void ShowImageSetupDialog()
        {
            bool perm = HasAllFiles();
            var labels = new List<string>();
            var paths = new List<string>();

            if (perm)
            {
                foreach (string p in FindImageFolders())
                {
                    paths.Add(p);
                    labels.Add(string.Format("{0}  ({1}장)", p, CountJpg(p)));
                }
            }
            int iManual = labels.Count; labels.Add("폴더 경로 직접 입력...");
            int iAll = -1;
            if (paths.Count >= 2)
            {
                iAll = labels.Count;
                labels.Add(string.Format("찾은 폴더 {0}곳 모두 사용", paths.Count));
            }
            int iPerm = labels.Count; labels.Add(perm ? "모든 파일 액세스: 허용됨" : "모든 파일 액세스 허용 (권장)");
            int iZip = labels.Count; labels.Add("이미지 ZIP 으로 읽기");

            // 권한이 없으면: 안내 + 버튼 3개 (AlertDialog 에서 목록과 메시지는 함께 쓸 수 없다)
            if (!perm)
            {
                new AlertDialog.Builder(this)
                    .SetTitle("카드 이미지 찾기")
                    .SetMessage("카드 이미지를 읽으려면 이 앱에 '모든 파일 액세스' 권한이 필요합니다.\r\n" +
                                "허용하면 카드 이미지 폴더를 자동으로 찾습니다.\r\n\r\n" +
                                "권한 없이 하려면 [이미지 ZIP] 을 쓰세요.")
                    .SetPositiveButton("모든 파일 액세스", delegate { _pendingPath = "/sdcard/EDOPro/pics"; AskAllFiles(); })
                    .SetNeutralButton("이미지 ZIP", delegate { PickZip(); })
                    .SetNegativeButton("닫기", delegate { })
                    .Show();
                return;
            }

            if (paths.Count == 0)
            {
                new AlertDialog.Builder(this)
                    .SetTitle("카드 이미지 폴더를 찾지 못했습니다")
                    .SetMessage("이 기기에서 EDOPro 카드 이미지(pics) 폴더를 찾지 못했습니다.\r\n\r\n" +
                                "· EDOPro 를 켜서 이미지를 한 번 받아 두거나\r\n" +
                                "· PC 에서 pics 폴더를 ZIP 으로 만들어 옮긴 뒤 [이미지 ZIP] 을 쓰세요.")
                    .SetPositiveButton("폴더 직접 입력", delegate { PromptPathMode(); })
                    .SetNeutralButton("이미지 ZIP", delegate { PickZip(); })
                    .SetNegativeButton("닫기", delegate { })
                    .Show();
                return;
            }

            new AlertDialog.Builder(this)
                .SetTitle("카드 이미지 폴더 선택")
                .SetItems(labels.ToArray(), (s, e) =>
                {
                    int idx = e.Which;
                    if (idx < paths.Count) { LoadPathImages(paths[idx]); return; }
                    if (idx == iManual) { PromptPathMode(); return; }
                    if (idx == iAll) { LoadPathImages(paths); return; }
                    if (idx == iPerm) { _pendingPath = "/sdcard/EDOPro/pics"; AskAllFiles(); return; }
                    if (idx == iZip) { PickZip(); return; }
                })
                .SetNegativeButton("닫기", delegate { })
                .Show();
        }
        private void PromptPathMode()
        {
            var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
            layout.SetPadding(Dp(18), Dp(8), Dp(18), Dp(4));

            layout.AddView(new TextView(this) { Text = "카드 이미지 폴더의 실제 경로 (여러 폴더는 ; 로 구분)" });
            var input = new EditText(this);
            input.Text = _prefs.GetString("pathPics", "/sdcard/EDOPro/pics");
            layout.AddView(input);

            var info = new TextView(this) { TextSize = 11f };
            info.Text = "경로로 읽으려면 이 앱에 '모든 파일 액세스' 권한이 필요합니다.\n" +
                        "허용 후 [이 경로로 읽기] 를 다시 눌러 주세요.";
            layout.AddView(info);

            new AlertDialog.Builder(this)
                .SetTitle("폴더 경로로 읽기")
                .SetView(layout)
                .SetPositiveButton("이 경로로 읽기", delegate
                {
                    string path = (input.Text ?? "").Trim();
                    _prefs.Edit().PutString("pathPics", path).Apply();
                    if (HasAllFiles()) LoadPathImages(path);
                    else { _pendingPath = path; AskAllFiles(); }
                })
                .SetNeutralButton("모든 파일 액세스", delegate
                {
                    _pendingPath = (input.Text ?? "").Trim();
                    AskAllFiles();
                })
                .SetNegativeButton("취소", delegate { })
                .Show();
        }

        private void LoadPathImages(string path)
        {
            LoadPathImages(SplitPathList(path));
        }

        private void LoadPathImages(List<string> folders)
        {
            try
            {
                var real = new List<string>();
                foreach (string f in folders)
                    if (System.IO.Directory.Exists(f)) real.Add(f);

                if (real.Count == 0)
                {
                    string show = folders.Count > 0 ? string.Join(";", folders.ToArray()) : "(빈 경로)";
                    Status("폴더가 없습니다: " + show);
                    Toast.MakeText(this, "폴더가 없습니다: " + show, ToastLength.Long).Show();
                    return;
                }
                int jpg = 0, png = 0;
                foreach (string f in real)
                {
                    try { jpg += System.IO.Directory.GetFiles(f, "*.jpg").Length; } catch { }
                    try { png += System.IO.Directory.GetFiles(f, "*.png").Length; } catch { }
                }
                string raw = string.Join(";", real.ToArray());
                _prefs.Edit().PutString("pathPics", raw).Remove("picsUri").Remove("zipUri").Apply();
                Android.Util.Log.Info("AI-EDITOR", "path mode " + raw + " folders=" + real.Count + " jpg=" + jpg + " png=" + png);
                if (jpg + png == 0)
                    Toast.MakeText(this, "그 폴더에 이미지가 없습니다.", ToastLength.Long).Show();
                string label = real.Count == 1
                    ? string.Format("이미지: {0} (경로 모드) · jpg {1} / png {2}", real[0], jpg, png)
                    : string.Format("이미지: {0} (+{1}곳) · jpg {2} / png {3}", real[0], real.Count - 1, jpg, png);
                SetImageSource(new MultiFolderImageBytes(real), label);
            }
            catch (Exception ex)
            {
                Status("경로 읽기 실패: " + ex.Message);
                Android.Util.Log.Error("AI-EDITOR", "path FAIL " + ex);
                LogMsg("path FAIL " + ex);
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (!string.IsNullOrEmpty(_pendingPath) && HasAllFiles())
            {
                string path = _pendingPath;
                _pendingPath = null;
                var found = FindImageFolders();
                LoadPathImages(found.Count > 0 ? found : SplitPathList(path));
            }
        }

        private void LoadZipAsync(Android.Net.Uri uri)
        {
            Status("ZIP 여는 중… (큰 파일은 시간이 걸립니다)");
            _picsLabel.Text = "";
            Task.Run(delegate
            {
                string path = System.IO.Path.Combine(CacheDir.AbsolutePath, "pics.zip");
                RunOnUiThread(delegate { ShowBusy("이미지 ZIP 준비", 0, "ZIP 압축을 푸는 중… (큰 파일은 오래 걸립니다)"); });
                try
                {
                    using (Stream s = ContentResolver.OpenInputStream(uri))
                    {
                        if (s == null) throw new Exception("파일을 열 수 없습니다");
                        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                            s.CopyTo(fs);
                    }
                    var zip = new ZipImageBytes(path, QueryDisplayName(uri));
                    RunOnUiThread(delegate
                    {
                        HideBusy();
                        if (_zipImages != null) { _zipImages.Dispose(); _zipImages = null; }
                        _zipImages = zip;
                        Android.Util.Log.Info("AI-EDITOR", "zip ok cards=" + zip.Count + " name=" + zip.ZipName + " uri=" + uri);
                        if (zip.Count == 0)
                            Toast.MakeText(this, "ZIP 안에서 <카드ID>.jpg 를 찾지 못했습니다.\r\n" + zip.Describe(), ToastLength.Long).Show();
                        SetImageSource(zip, "이미지: " + zip.Describe());
                    });
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("AI-EDITOR", "zip FAIL " + ex);
                    LogMsg("zip FAIL " + ex);
                    RunOnUiThread(delegate { Status("ZIP 을 읽지 못했습니다: " + ex.Message); });
                }
            });
        }

        // 이미지 소스를 바꾸고 **미리 읽어 둔 뒤** 렌더 → 옵션 변경 시 디스크 재읽기 없음
        private void SetImageSource(ICardImageBytes src, string label)
        {
            var old = _store;
            ICardImageBytes fallback = null;
            if (_prefs.GetBoolean("netImages", true))
            {
                try
                {
                    var h = new HttpImageBytes(ImageCacheDir());
                    h.Cdb = _cdb;                       // alias(원본 ID) · 이름 폴백
                    fallback = HttpImageLogger(h);
                }
                catch { }
            }
            _store = new SkiaImageStore(src, fallback);
            _images = src;
            if (_retiredStore != null) { try { _retiredStore.Dispose(); } catch { } }
            _retiredStore = old;

            _picsLabel.Text = label;
            _srcLabel = label;
            RequestRenderSoon();
            StartPreload();
        }

        // 덱이 준비된 뒤: 미리 읽기 + 로컬에 없는 카드는 내려받기(진행률 창)
        private void StartPreload()
        {
            if (_deck == null || _store == null) return;
            var ids = new HashSet<int>();
            foreach (int id in _deck.Main) ids.Add(id);
            foreach (int id in _deck.Extra) ids.Add(id);
            foreach (int id in _deck.Side) ids.Add(id);
            var list = new List<int>(ids);
            SkiaImageStore store = _store;
            string label = _srcLabel ?? "이미지";

            // 로컬에 없는 카드 수 → 그만큼 인터넷에서 받는다(진행률 표시용)
            int localMissing = 0;
            foreach (int id in list) if (store.Get(id) == null) localMissing++;
            if (localMissing > 0 && store.HasFallback)
                ShowBusy("카드 이미지 준비", localMissing,
                    string.Format("이미지 내려받는 중…  0 / {0}", localMissing));

            Task.Run(delegate
            {
                try
                {
                    // cdb(원본 ID·이름) 정보를 먼저 채운다 — 없으면 아래 프리로드에서 폴백이 안 된다
                    try { if (_cdb != null) _cdb.Prefetch(list); } catch { }

                    int loaded = store.Preload(list, delegate (int done, int total)
                    {
                        RunOnUiThread(delegate
                        {
                            if (!ReferenceEquals(_store, store)) return;
                            _picsLabel.Text = label + string.Format(" · 이미지 준비 {0}/{1}{2}", done, total,
                                store.FromFallback > 0 ? " (내려받는 중)" : "");
                            if (_dlgBusy != null) UpdateBusy(store.FallbackAttempts, localMissing);
                        });
                    });
                    RunOnUiThread(delegate
                    {
                        HideBusy();
                        if (ReferenceEquals(_store, store))
                        {
                            string extra = store.FromFallback > 0 ? string.Format(" · 인터넷에서 {0}장 받음", store.FromFallback) : "";
                            _picsLabel.Text = label + string.Format(" · 준비 완료 ({0}장){1}", loaded, extra);
                        }
                        Android.Util.Log.Info("AI-EDITOR", "preload done loaded=" + loaded + " fromNet=" + store.FromFallback
                            + " tries=" + store.FallbackAttempts + " localMissing=" + localMissing);
                        RequestRenderSoon();
                    });
                }
                catch (Exception ex)
                {
                    Android.Util.Log.Error("AI-EDITOR", "preload FAIL " + ex.Message);
                    LogMsg("preload FAIL " + ex.Message);
                    RunOnUiThread(delegate { HideBusy(); });
                }
            });
        }

        // ── 로딩 창 (다운로드/압축 해제 진행률) ─────────────────
        private AlertDialog _dlgBusy;
        private ProgressBar _dlgBar;
        private TextView _dlgText;

        private void ShowBusy(string title, int total, string message)
        {
            HideBusy();
            var lay = new LinearLayout(this) { Orientation = Orientation.Vertical };
            lay.SetPadding(Dp(22), Dp(10), Dp(22), Dp(6));

            _dlgText = new TextView(this) { Text = message };
            _dlgText.TextSize = 14f;
            lay.AddView(_dlgText);

            _dlgBar = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal);
            _dlgBar.LayoutParameters = new LinearLayout.LayoutParams(-1, Dp(8));
            if (total > 0) { _dlgBar.Indeterminate = false; _dlgBar.Max = total; _dlgBar.Progress = 0; }
            else _dlgBar.Indeterminate = true;
            lay.AddView(_dlgBar);

            try
            {
                _dlgBusy = new AlertDialog.Builder(this)
                    .SetTitle(title)
                    .SetView(lay)
                    .SetCancelable(false)
                    .Show();
                Android.Util.Log.Info("AI-EDITOR", "busy show total=" + total);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("AI-EDITOR", "busy show FAIL " + ex.Message);
                LogMsg("busy show FAIL " + ex.Message);
            }
        }

        private void UpdateBusy(int done, int total)
        {
            if (_dlgBusy == null || !_dlgBusy.IsShowing) return;
            try
            {
                if (_dlgText != null)
                    _dlgText.Text = string.Format("이미지 내려받는 중…  {0} / {1}", done, total);
                if (_dlgBar != null && total > 0)
                {
                    if (_dlgBar.Max != total) _dlgBar.Max = total;
                    _dlgBar.Progress = Math.Min(done, total);
                }
            }
            catch { }
        }

        private void HideBusy()
        {
            var d = _dlgBusy;
            _dlgBusy = null; _dlgBar = null; _dlgText = null;
            if (d == null) return;
            try { if (d.IsShowing) d.Dismiss(); } catch { }
            try { d.Dispose(); } catch { }
            Android.Util.Log.Info("AI-EDITOR", "busy hide");
        }
        // ══════════════════════════════════════════════════════
        //  렌더
        // ══════════════════════════════════════════════════════
        private void RequestRenderSoon()
        {
            SaveSettings();
            if (_renderPending) return;
            _renderPending = true;
            _ui.PostDelayed(delegate
            {
                _renderPending = false;
                Render();
            }, 320);
        }

        private void Render()
        {
            if (_deck == null || _busy) return;
            _busy = true;
            _busyBar.Visibility = ViewStates.Visible;

            SkiaDeck deck = _deck;
            SkiaRenderOptions opt = BuildOptions();
            SkiaImageStore store = _store;

            Task.Run(delegate
            {
                try
                {
                    SkiaRenderStats st;
                    int w, h;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] rgba = DeckImageSkiaRenderer.RenderRgba(deck, opt, store, out st, out w, out h);
                    long ms = sw.ElapsedMilliseconds;
                    Android.Util.Log.Info("AI-EDITOR", "render ms=" + ms + " size=" + w + "x" + h + " placed=" + st.Placed);
                    RunOnUiThread(delegate { ShowPreview(rgba, w, h, st); });
                }
                catch (Exception ex)
                {
                    RunOnUiThread(delegate
                    {
                        _busy = false;
                        _busyBar.Visibility = ViewStates.Gone;
                        Status("렌더 실패: " + ex.Message);
                    });
                }
            });
        }

        // RGBA 픽셀을 그대로 Bitmap 으로 (PNG 인코딩/디코딩 왕복 제거 → 훨씬 빠름)
        private void ShowPreview(byte[] rgba, int w, int h, SkiaRenderStats st)
        {
            try
            {
                Bitmap bmp = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
                using (var buf = Java.Nio.ByteBuffer.Wrap(rgba))
                    bmp.CopyPixelsFromBuffer(buf);

                Bitmap old = _previewBmp;
                _preview.SetImageBitmap(bmp);
                _previewBmp = bmp;
                if (old != null && !ReferenceEquals(old, bmp)) old.Recycle();

                Status(st.Summary + (st.Missing.Count > 0 ? "   (EDOPro pics 에 없는 카드는 ?ID 로 표시)" : ""));
                // 이미지가 하나도 없으면 안내(자동 탐색/권한/ZIP)
                if (!_setupShown && st.Placed > 0 && st.MissingSlots >= st.Placed)
                {
                    _setupShown = true;
                    ShowImageSetupDialog();
                }
            }
            catch (Exception ex)
            {
                Status("미리보기 실패: " + ex.Message);
            }
            finally
            {
                _busy = false;
                _busyBar.Visibility = ViewStates.Gone;
            }
        }

        // ══════════════════════════════════════════════════════
        //  저장
        // ══════════════════════════════════════════════════════
        private void SaveImage()
        {
            if (_deck == null) { Toast.MakeText(this, "먼저 덱을 열어주세요", ToastLength.Short).Show(); return; }

            Status("저장용 이미지를 만드는 중…");
            SkiaDeck deck = _deck;
            SkiaRenderOptions opt = BuildOptions();
            SkiaImageStore store = _store;
            Task.Run(delegate
            {
                try
                {
                    SkiaRenderStats st;
                    byte[] png = DeckImageSkiaRenderer.RenderToPng(deck, opt, store, out st);
                    RunOnUiThread(delegate { _savePng = png; WriteSavedImage(); });
                }
                catch (Exception ex)
                {
                    RunOnUiThread(delegate { Status("저장 실패: " + ex.Message); });
                }
            });
        }

        private void WriteSavedImage()
        {
            if (_savePng == null) return;
            string name = "deck_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";

            if ((int)Build.VERSION.SdkInt >= 29)
            {
                try
                {
                    var values = new ContentValues();
                    values.Put("_display_name", name);
                    values.Put("mime_type", "image/png");
                    values.Put("relative_path", Android.OS.Environment.DirectoryPictures + "/WindBotDeckImage");

                    Android.Net.Uri uri = ContentResolver.Insert(MediaStore.Images.Media.ExternalContentUri, values);
                    if (uri == null) throw new Exception("MediaStore 등록 실패");
                    using (Stream os = ContentResolver.OpenOutputStream(uri))
                    {
                        os.Write(_savePng, 0, _savePng.Length);
                        os.Flush();
                    }
                    Toast.MakeText(this, "사진첩 Pictures/WindBotDeckImage 에 저장했습니다", ToastLength.Long).Show();
                    Status("저장 완료: Pictures/WindBotDeckImage/" + name);
                    return;
                }
                catch (Exception ex)
                {
                    Status("갤러리 저장 실패 → 저장 위치를 직접 선택합니다 (" + ex.Message + ")");
                }
            }

            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("image/png");
            intent.PutExtra(Intent.ExtraTitle, name);
            StartActivityForResult(intent, ReqSave);
        }

        private void Status(string text)
        {
            _status.Text = text;
        }

        protected override void OnDestroy()
        {
            try { HideBusy(); } catch { }
            try { if (_zipImages != null) { _zipImages.Dispose(); _zipImages = null; } } catch { }
            base.OnDestroy();
        }
    }
}

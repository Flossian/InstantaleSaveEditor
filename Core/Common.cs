// 共通部品: コーデック / JSON ヘルパ / JSON編集ダイアログ / 汎用オブジェクトフォーム
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace InstantaleSaveEditor
{
    // ---------------- ゲーム互換 JSON エンコーダ ----------------
    // ゲーム(= Python の json.dumps(ensure_ascii=False) 相当)と同じ最小エスケープだけを行う。
    // エスケープするのは " と \ と制御文字(U+0000–U+001F)のみ。それ以外（全角スペース U+3000、
    // 行区切り U+2028/U+2029、< > & など全ての非制御文字）は生のまま UTF-8 で出力する。
    // 標準の UnsafeRelaxedJsonEscaping は U+3000 等をエスケープしてしまい、セーブを編集なしで
    // 保存し直すだけでもバイト列がゲーム出力と食い違う。これを使ってバイト再現性を保つ。
    internal sealed class GameJsonEncoder : JavaScriptEncoder
    {
        public static readonly GameJsonEncoder Instance = new();

        public override int MaxOutputCharactersPerInputCharacter => 6;   // "\uXXXX"

        // エスケープが必要なのは " と \ と制御文字だけ。
        public override bool WillEncode(int unicodeScalar)
            => unicodeScalar == '"' || unicodeScalar == '\\' || unicodeScalar < 0x20;

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (int i = 0; i < textLength; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\\' || c < 0x20) return i;
            }
            return -1;
        }

        // WillEncode が true を返した文字（" \ 制御文字）だけがここへ来る。
        // 制御文字は \b \f \n \r \t の短縮形、その他は \u00XX（小文字）で出力する（Python と同形式）。
        public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            string s = unicodeScalar switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => "\\u" + unicodeScalar.ToString("x4"),
            };
            if (s.Length > bufferLength) { numberOfCharactersWritten = 0; return false; }
            for (int i = 0; i < s.Length; i++) buffer[i] = s[i];
            numberOfCharactersWritten = s.Length;
            return true;
        }
    }

    // ---------------- コーデック ----------------
    // セーブ/ワールドデータとアプリ内部表現(JSON)を相互変換する。
    internal static class Codec
    {
        private static readonly byte[] S =
            Convert.FromBase64String("SW5zdGFudGFsZV9TYXZlX0tleV8yMDI2");

        // ゲームが書き出すのと同じ形式: 最小化(空白なし) / UTF-8 / 非ASCIIをそのまま出す。
        // これと一致しないとバイト単位での再現性が崩れるため Encoder/WriteIndented を固定。
        // 標準の UnsafeRelaxedJsonEscaping は全角スペース U+3000 や U+2028/U+2029 をエスケープしてしまい
        // ゲーム出力とバイト単位で食い違うため、ゲーム(Python の ensure_ascii=False 相当)と同じ最小
        // エスケープを行う GameJsonEncoder を使う。
        public static readonly JsonSerializerOptions Compact = new()
        { Encoder = GameJsonEncoder.Instance, WriteIndented = false, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        // 確認用エクスポート向けの整形(インデント付き)出力。ゲームには使わない。
        public static readonly JsonSerializerOptions Pretty = new()
        { Encoder = GameJsonEncoder.Instance, WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        // バイト列を相互変換する（同じ処理を二度かければ元に戻る）。
        private static byte[] Transform(byte[] data)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) o[i] = (byte)(data[i] ^ S[i % S.Length]);
            return o;
        }
        // ファイルを読み込み、JSON オブジェクトへ変換する。
        // 通常はゲームの難読化ファイルだが、復号済み(エクスポート)JSON もそのまま読み込めるよう
        // 先に平文 JSON として解釈を試み、失敗したら XOR 復号する。
        public static JsonObject Load(string path)
        {
            var bytes = File.ReadAllBytes(path);
            // BOM を除いた先頭の非空白文字が '{' / '[' なら平文 JSON とみなす。
            if (LooksLikePlainJson(bytes))
            {
                try { return JsonNode.Parse(StripBom(Encoding.UTF8.GetString(bytes))).AsObject(); }
                catch { /* 平文解釈に失敗したら難読化として扱う */ }
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(Transform(bytes))).AsObject();
        }

        // UTF-8 BOM を含む先頭空白を読み飛ばし、最初の文字が JSON の開始記号かを判定。
        private static bool LooksLikePlainJson(byte[] bytes)
        {
            int i = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) i = 3;
            while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n')) i++;
            return i < bytes.Length && (bytes[i] == '{' || bytes[i] == '[');
        }

        private static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
        // JSON を最小化 UTF-8 にし、ゲームが読める形式（XOR 難読化）のバイト列へ変換する。
        // 保存とバックアップ前の差分判定で同じ出力を使うため、Save から分離している。
        public static byte[] Encode(JsonNode root)
            => Transform(Encoding.UTF8.GetBytes(root.ToJsonString(Compact)));
        // JSON を最小化 UTF-8 にしてファイルへ書き出す（ゲームが読める形式）。
        public static void Save(string path, JsonNode root)
            => File.WriteAllBytes(path, Encode(root));
    }

    // ---------------- JSON 値の安全な取得 ----------------
    // JsonObject から型を指定して値を取り出すヘルパ。キーが無い/型違い/null のときは既定値を返し、
    // 例外を投げない。GetValue<T>() は型不一致で例外になるため TryGetValue で包んでいる。
    internal static class J
    {
        // 文字列を取得（無ければ def）。
        public static string Str(JsonObject o, string k, string def = "")
            => o != null && o.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : def;
        // 整数を取得（無ければ def）。
        public static long Int(JsonObject o, string k, long def = 0)
            => o != null && o.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue<long>(out var l) ? l : def;
        // 真偽値を取得（無ければ def）。
        public static bool Bool(JsonObject o, string k, bool def = false)
            => o != null && o.TryGetPropertyValue(k, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
        // NPC が死亡扱いか（config.is_dead == true）。
        public static bool NpcIsDead(JsonObject npc) => Bool(Obj(npc, "config"), "is_dead");
        // 数値を取得（整数で入っていても double として返す。無ければ def）。
        public static double Dbl(JsonObject o, string k, double def = 0)
        {
            if (o != null && o.TryGetPropertyValue(k, out var n) && n is JsonValue v)
            { if (v.TryGetValue<double>(out var d)) return d; if (v.TryGetValue<long>(out var l)) return l; }
            return def;
        }
        // 子オブジェクト/子配列を取得（型違いや null は null を返す）。
        public static JsonObject Obj(JsonObject o, string k) => o != null && o.TryGetPropertyValue(k, out var n) ? n as JsonObject : null;
        public static JsonArray Arr(JsonObject o, string k) => o != null && o.TryGetPropertyValue(k, out var n) ? n as JsonArray : null;

        // 文字列配列を組み立てる。
        public static JsonArray StrArr(IEnumerable<string> items)
        {
            var a = new JsonArray();
            foreach (var s in items) a.Add(s);
            return a;
        }

        // JSON編集ボタン横などに出す短い要約表示（"null" / "[配列 n件]" / "{辞書 nキー}" / スカラ値）。
        public static string Preview(JsonNode v)
            => v is null ? "null"
             : v is JsonArray a ? I18n.T("preview.array", a.Count)
             : v is JsonObject ob ? I18n.T("preview.dict", ob.Count)
             : v.ToString();
    }

    // ---------------- 画像の原寸表示 ----------------
    // PictureBox にクリックハンドラを登録し、画像を別ウィンドウで原寸（スクロール付き）表示する。
    // NpcImagePanel / BackgroundImagePanel など複数のパネルから共通で使う。
    internal static class ImageViewer
    {
        public static void RegisterClickViewer(PictureBox pb, string title)
        {
            pb.Click += (_, _) =>
            {
                if (pb.Image == null) return;
                var img = (Image)pb.Image.Clone();
                var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
                var f = new Form
                {
                    Text = title + I18n.T("viewer.actualSizeSuffix"),
                    StartPosition = FormStartPosition.CenterScreen,
                    Width = Math.Min(img.Width + 40, area.Width - 40),
                    Height = Math.Min(img.Height + 60, area.Height - 40),
                };
                f.FormClosed += (_, _) => img.Dispose();
                var inner = new PictureBox { SizeMode = PictureBoxSizeMode.AutoSize, Image = img };
                var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                scroll.Controls.Add(inner);
                f.Controls.Add(scroll);
                f.Show();
            };
        }
    }

    // JSON編集ダイアログで編集中の値を保持する箱。Changed=編集されたか、Preview=一覧の表示ラベル。
    internal sealed class JsonHolder { public JsonNode Node; public bool Changed; public Label Preview; }

    // ---------------- 任意の JSON 値を編集するダイアログ ----------------
    // 配列/オブジェクト/null など、フォームで扱いにくい値を生の JSON テキストで編集する。
    // OK 時にパースして ResultNode に格納（"null" も有効な結果）。パース失敗時は閉じずに警告。
    internal sealed class JsonEditDialog : Form
    {
        private readonly TextBox _t;
        public JsonNode ResultNode { get; private set; }
        public JsonEditDialog(string title, JsonNode value)
        {
            Text = title; Width = 680; Height = 520; StartPosition = FormStartPosition.CenterParent;
            // 等幅フォントの複数行テキスト。初期値は整形した JSON（null は文字列 "null"）。
            _t = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10f),
                AcceptsReturn = true,
                AcceptsTab = true,
                Text = value is null ? "null" : value.ToJsonString(Codec.Pretty),
            };
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                // 入力テキストを JSON としてパース。成功すれば確定、失敗すれば閉じずにエラー表示。
                try { ResultNode = JsonNode.Parse(_t.Text); DialogResult = DialogResult.OK; Close(); }
                catch (JsonException ex) { MessageBox.Show(this, I18n.T("msg.jsonError") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            Controls.Add(_t); Controls.Add(bar);
        }
    }

    // ---------------- グリッド系コントロールの共通基底 ----------------
    // life_log / area_history / relationship の各 Grid が共有する部分をまとめる:
    //   ・DataGridView の生成設定（行追加削除なし・行ヘッダなし・Fill・単一選択・行選択）
    //   ・下端グリップによる高さリサイズ（CreateGrip）
    //   ・列・セル値のヘルパ（IntCol / ParseLong）
    // 各派生クラスは列定義・ダブルクリック編集・Bind/ToArray(ToObject) など固有処理だけを持つ。
    internal abstract class ResizableGridPanel : Panel
    {
        protected readonly DataGridView _grid = new()
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };

        private bool _drag;             // グリップをドラッグ中か
        private int _startY, _startH;   // ドラッグ開始時のカーソルY座標と高さ

        // 下端グリップを生成して返す。上下ドラッグでパネル全体の高さを変える。
        // Controls への追加順は呼び出し側が制御する（最後に追加すると最下端を確保する）。
        protected Panel CreateGrip(int minHeight = 120)
        {
            var grip = new Panel { Dock = DockStyle.Bottom, Height = 8, Cursor = Cursors.SizeNS, BackColor = SystemColors.ControlDark };
            grip.MouseDown += (_, _) => { _drag = true; _startY = Cursor.Position.Y; _startH = Height; };
            grip.MouseMove += (_, _) =>
            {
                if (!_drag) return;
                Height = Math.Max(minHeight, _startH + (Cursor.Position.Y - _startY));
                Parent?.PerformLayout();
                Parent?.Parent?.PerformLayout();
            };
            grip.MouseUp += (_, _) => { _drag = false; Parent?.PerformLayout(); Parent?.Parent?.PerformLayout(); };
            return grip;
        }

        // 整数欄向けの狭い列。
        protected static DataGridViewTextBoxColumn IntCol(string h, int weight = 20)
            => new() { HeaderText = h, FillWeight = weight };

        // セル値を long として読む。空・不正は 0。ロケール非依存で解釈する。
        protected static long ParseLong(DataGridViewRow r, int col)
            => long.TryParse(r.Cells[col].Value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
    }

    // ---------------- ComboBox のホイール誤操作ガード ----------------
    // WinForms はフォーカスの残った ComboBox にマウスホイールを届けるため、画面をスクロールした
    // つもりで値が変わってしまう。ドロップダウンが閉じている ComboBox へのホイールは値を変えず、
    // 包んでいるスクロール領域のスクロールへ回す（ドロップダウン展開中は従来通り候補移動に使える）。
    // Application.AddMessageFilter で登録し、アプリ内の全 ComboBox に一括で効く。
    internal sealed class ComboWheelGuard : IMessageFilter
    {
        private const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            // 宛先（フォーカス中コントロール）が閉じた ComboBox のときだけ横取りする。
            // DropDown スタイルは内部エディット子ウィンドウが宛先になるため FromChildHandle で親を引く。
            if (Control.FromChildHandle(m.HWnd) is not ComboBox cb || cb.DroppedDown) return false;

            // 包んでいるスクロール可能な親を代わりにスクロールする（ScrollableControl 標準と同じ delta ピクセル）。
            int delta = unchecked((short)((long)m.WParam >> 16));
            for (Control c = cb.Parent; c != null; c = c.Parent)
            {
                if (c is ScrollableControl sc && sc.VerticalScroll.Visible)
                {
                    var p = sc.AutoScrollPosition;
                    sc.AutoScrollPosition = new Point(-p.X, -p.Y - delta);
                    break;
                }
            }
            return true;   // ComboBox には届けない（値を変えない）
        }
    }

    // ---------------- 高さをドラッグで変えられるテキスト欄（幅はコンテナ幅に追従） ----------------
    // 呼び出し側で Dock=Top を指定すると幅は親いっぱいになり、ウィンドウ幅に連動する。
    // 下端のグリップを上下にドラッグすると高さが変わる。
    internal sealed class ResizableTextBox : Panel
    {
        public TextBox Box { get; }     // 実際の入力欄。呼び出し側は .Box.Text で読み書きする。
        private bool _drag;             // グリップをドラッグ中か
        private int _startY, _startH;   // ドラッグ開始時のカーソルY座標と高さ

        public ResizableTextBox(int width, int height)
        {
            Size = new Size(width, height);
            Box = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };
            // 下端の細い帯。これを上下にドラッグして高さを変える。
            var grip = new Panel { Dock = DockStyle.Bottom, Height = 8, Cursor = Cursors.SizeNS, BackColor = SystemColors.ControlDark };
            // 追加順が重要: Fill を先に、Bottom を後に入れると Bottom が先に下端を確保し Box が残りを埋める。
            Controls.Add(Box);     // Fill（先に追加 → 残り領域を埋める）
            Controls.Add(grip);    // Bottom（後に追加 → 先に下端を確保）

            // ドラッグ開始時の基準を記録。
            grip.MouseDown += (_, _) => { _drag = true; _startY = Cursor.Position.Y; _startH = Height; };
            grip.MouseMove += (_, _) =>
            {
                if (!_drag) return;
                // 高さのみ変更（最小44px）。幅は Dock=Top に任せて親=ウィンドウ幅に追従させる。
                Height = Math.Max(44, _startH + (Cursor.Position.Y - _startY));
                Parent?.PerformLayout();          // 親(セル/テーブル)に再レイアウトを促す
                Parent?.Parent?.PerformLayout();  // さらに上位(GroupBox等)も
            };
            grip.MouseUp += (_, _) => { _drag = false; Parent?.PerformLayout(); Parent?.Parent?.PerformLayout(); };
        }
    }

    // ---------------- 折りたたみグループ ----------------
    // 見出しボタンを押すと中身(Content)の表示/非表示を切り替えるパネル。
    // AutoSize なので中身の高さに追従し、Dock=Top で縦に積むと折りたたみで自動的に詰まる。
    internal sealed class CollapsibleGroup : Panel
    {
        private readonly Button _header;
        private readonly Panel _content;
        private readonly string _title;
        private bool _open;

        public Control Content => _content;   // 呼び出し側はここに中身コントロールを追加する

        public CollapsibleGroup(string title, bool open)
        {
            _title = title;
            Dock = DockStyle.Top; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0, 0, 0, 4);

            _content = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Visible = open };
            _header = new Button
            {
                Dock = DockStyle.Top,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = SystemColors.ControlLight,
            };
            _header.Font = new Font(_header.Font, FontStyle.Bold);
            _open = open;
            _header.Click += (_, _) => { _open = !_open; _content.Visible = _open; UpdateHeader(); };

            // Dock=Top は後から追加した方が上に来る。見出しを上、中身を下にする。
            Controls.Add(_content);
            Controls.Add(_header);
            UpdateHeader();
        }

        private void UpdateHeader() => _header.Text = (_open ? "▼ " : "▶ ") + _title;
    }

    // フォーム上の1項目とその編集ウィジェットの対応。Kind により使うウィジェットが決まる:
    //   bool→Chk / int,dbl,text,strlist→Tb / abilities→Sub(6つの能力値欄) /
    //   json→Holder / combo→Combo / lifelog→Life / relationship→Rel
    internal sealed class FieldRef { public string Name, Kind; public TextBox Tb; public CheckBox Chk; public JsonHolder Holder; public Dictionary<string, TextBox> Sub; public ComboBox Combo; public bool IdCombo; public LifeLogGrid Life; public RelationshipGrid Rel; }

    // 1つの JsonObject の各プロパティを自動でフォーム化する汎用コントロール。
    // 値の型ごとに最適なウィジェットを割り当て、編集はフォーカスアウト時に即モデルへ反映する。
    internal sealed class ObjectForm : UserControl
    {
        // この名前のフィールド、または60文字超の文字列は複数行のリサイズ枠で表示する。
        private static readonly HashSet<string> LongText = new()
        { "overview","structure_description","story","descriptions","description","profile","personality",
          "look_description","request_summary","client_statement","speech_style","combat_log","to_save_texts" };

        // 能力値(6項目)とその日本語表示。ability_scores / original_ability_scores を専用欄として埋め込む。
        private static readonly (string key, string jp)[] AbilityKeys =
        { ("strength","筋力"), ("dexterity","敏捷"), ("constitution","耐久"),
          ("intelligence","知力"), ("wisdom","判断力"), ("charisma","魅力") };

        // 対象フィールドが能力値オブジェクト(6キーすべてを持つ)かどうか。null や欠けた場合は false。
        private static bool IsAbilityObject(string field, JsonNode val)
        {
            if (field != "ability_scores" && field != "original_ability_scores") return false;
            if (val is not JsonObject o) return false;
            foreach (var (k, _) in AbilityKeys) if (!o.ContainsKey(k)) return false;
            return true;
        }

        private JsonObject _obj;                                          // 現在バインド中のオブジェクト
        private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true };  // スクロール領域
        private readonly List<FieldRef> _fields = new();                 // 生成した各項目の参照
        // フィールド名 → ComboBox ファクトリ。Bind 前に RegisterComboField で登録し Bind 後にクリアしないこと。
        // idPrefixed=true は "ID: 名前" 形式の表示から ID 部分だけを保存するコンボ（エリア/施設/NPC 参照）。
        private readonly Dictionary<string, (Func<string, ComboBox> factory, bool idPrefixed)> _comboFactories = new();

        // relationship の対象キー（"player" 以外は NPC ID 等）を見出し表示名へ変換する任意のフック。
        // 未設定/空文字を返す場合はキーをそのまま表示する。WorldTab が NPC 一覧を引いて名前を返す。
        public Func<string, string> RelationshipTargetNamer { get; set; }

        // party（["player", NPC ID...]）専用 GUI のためのフック。PartyNpcCandidates が設定されている時のみ有効。
        // PartyNpcNamer: NPC ID → 表示名（未解決なら null）。PartyNpcIsDead: その NPC が死亡扱いか。
        // PartyNpcCandidates: 追加候補に出す全 NPC の (ID, 名前, 死亡フラグ) 一覧。
        public Func<string, string> PartyNpcNamer { get; set; }
        public Func<string, bool> PartyNpcIsDead { get; set; }
        public Func<IEnumerable<(string id, string name, bool dead)>> PartyNpcCandidates { get; set; }

        // connections（接続先ID配列）専用 GUI のためのフック。ConnectionCandidates が設定されている時のみ有効。
        // ConnectionNamer: ID → 表示名（未解決なら null）。ConnectionCandidates: 追加候補の (ID, 名前) 一覧
        //（自分自身の除外は呼び出し側の責務）。WorldTab が area（他エリア）/facility（同一エリア内施設）を渡す。
        // ConnectionAdded / ConnectionRemoved: 追加・削除の確定後に相手 ID を通知する
        //（相手側の connections も書き換えて双方向を保つ等の後処理用）。
        public Func<string, string> ConnectionNamer { get; set; }
        public Func<IEnumerable<(string id, string name)>> ConnectionCandidates { get; set; }
        public Action<string> ConnectionAdded { get; set; }
        public Action<string> ConnectionRemoved { get; set; }

        // attributes 内の item_detail プルダウン候補を、現在の item_type から供給する任意フック。
        // 設定時は item_detail を item_type 連動のプルダウンとして表示する（ItemEditDialog が設定）。
        // 未設定なら item_detail は通常のテキスト欄になる。
        public Func<string, IReadOnlyList<string>> AttributeDetailOptions { get; set; }

        // attributes に表示すべき属性キー（item_detail を除く・表示順）を item_type から供給する任意フック。
        // 設定時、item_type 変更で ApplyAttributeSchema を呼ぶと attributes をこの集合へ作り替える
        // （既存値は保持・スキーマ外キーは削除・不足キーは 0 で追加）。空（未知の type）なら作り替えない。
        public Func<string, IReadOnlyList<string>> AttributeStatKeys { get; set; }

        // inventory（アイテム辞書）をグリッド＋ボタンの InventoryPanel で表示するか。
        // 既定は false（従来通り JSON 編集ボタン）。WorldTab が NPC 編集時に true にする。
        public bool InventoryGridEnabled { get; set; }

        // skills フィールドを一覧＋追加/編集/削除（SkillListPanel）にするか。
        // 既定は false（従来通り JSON 編集ボタン）。WorldTab が NPC 編集時に true にする。
        public bool SkillsGridEnabled { get; set; }

        // image_src フィールドを「プレビュー＋パス欄＋参照ボタン」にし、画像一覧から選べるようにするか。
        // 既定は false（従来通りのテキスト欄）。ItemEditDialog が true にする。
        public bool ImageSrcPickerEnabled { get; set; }

        // item_detail → 既定の image_src（相対パス）を供給する任意フック。
        // 設定時、item_type 変更や item_detail の選択で image_src を該当フォルダの先頭画像へ自動更新する。
        // ItemEditDialog が設定する。空文字を返せば image_src をクリアする。
        public Func<string, string> DefaultImageForDetail { get; set; }

        private ComboBox _itemDetailCombo;   // attributes 展開時に生成した item_detail プルダウン（item_type 連動更新用）
        private TextBox _imageSrcBox;        // image_src の入力欄（既定画像の自動反映先）
        private Action _imageSrcRefresh;     // image_src プレビューの再描画
        private bool _suppressDetailEvents;  // item_detail 候補の再構築中にイベント連動を抑止するフラグ
        private TableLayoutPanel _attrsInner;  // attributes 展開先の内側テーブル（作り替え時に差し替える）
        private TableLayoutPanel _attrsOuter;  // _attrsInner を載せている外側テーブル
        private int _attrsRow;                 // 外側テーブル上の attributes 行
        private JsonObject _attrsObj;          // 現在表示中の attributes オブジェクト

        public ObjectForm() { Controls.Add(_host); }

        // autoSize=true: 自身を内容に合わせて伸縮させる（外側でスクロールを用意する前提）。
        // 折りたたみグループの中身として複数並べる用途で使う。
        public ObjectForm(bool autoSize)
        {
            if (autoSize)
            {
                AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
                _host.Dock = DockStyle.Top; _host.AutoScroll = false;
                _host.AutoSize = true; _host.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }
            Controls.Add(_host);
        }

        // 表示をクリアし、バインドを解除する。
        public void Clear() { _obj = null; _host.Controls.Clear(); _fields.Clear(); }

        // 指定フィールドをプルダウン式にする。factory は現在値を受け取り ComboBox を返す。
        // 毎回の Bind 前に呼ぶか、あるいは常時登録しておくこと。
        // idPrefixed=true のとき、保存時に "ID: 名前" 形式のテキストから ID 部分だけを取り出す。
        // 既定 false は値そのものを保存する（category/job/rarity 等。":" を含む値も壊さない）。
        public void RegisterComboField(string fieldName, Func<string, ComboBox> factory, bool idPrefixed = false)
            => _comboFactories[fieldName] = (factory, idPrefixed);

        // 登録済みのすべての ComboBox ファクトリを削除する。
        public void ClearComboFields() => _comboFactories.Clear();

        // Bind 済みフィールドの ComboBox を取得する（連動プルダウンの配線に使う）。なければ null。
        public ComboBox GetCombo(string field)
            => _fields.FirstOrDefault(f => f.Kind == "combo" && f.Name == field)?.Combo;

        // 指定オブジェクトの各プロパティをフォーム化して表示する。
        // injectControl / injectAfterKey を指定すると、該当フィールドの直後にコントロールを差し込む。
        // reorder を指定すると、モデルの並びは変えずに表示順だけ move を after の直後へ移動する。
        // WinForms の DockStyle.Top は後から追加したコントロールが上に来るため、逆順で追加する。
        // keyOrder を指定すると、その順序・その集合のキーだけを表示する（obj に無いキーは無視）。
        // 同じ obj を別々のキー集合で複数のフォームに分割表示する（カテゴリ分け）用途で使う。
        public void Bind(JsonObject obj, Control injectControl = null, string injectAfterKey = null,
                         (string move, string after)? reorder = null, IEnumerable<string> keyOrder = null)
        {
            _obj = obj; _host.Controls.Clear(); _fields.Clear();
            _itemDetailCombo = null; _attrsInner = null; _attrsOuter = null; _attrsObj = null;
            _imageSrcBox = null; _imageSrcRefresh = null;
            if (obj == null) return;

            // 表示するキーの並び。keyOrder 指定時はその集合・順序、未指定なら obj の全キー。
            // reorder 指定時はモデルを変えずに表示順だけ入れ替える。
            var keys = keyOrder != null
                ? keyOrder.Where(obj.ContainsKey).ToList()
                : obj.Select(p => p.Key).ToList();
            if (reorder is { } rr && keys.Contains(rr.after) && keys.Remove(rr.move))
                keys.Insert(keys.IndexOf(rr.after) + 1, rr.move);

            bool useInject = injectControl != null && injectAfterKey != null;

            if (!useInject)
            {
                var t = NewTable();
                int row = 0;
                foreach (var k in keys) AddRow(t, row++, k, obj[k]);
                _host.Controls.Add(t);
                return;
            }

            // injectAfterKey の前後でテーブルを分割する。
            var t1 = NewTable(); // injectAfterKey までのフィールド
            var t2 = NewTable(); // それ以降のフィールド
            int r1 = 0, r2 = 0;
            bool past = false;

            foreach (var k in keys)
            {
                if (!past)
                {
                    AddRow(t1, r1++, k, obj[k]);
                    if (k == injectAfterKey) past = true;
                }
                else
                {
                    AddRow(t2, r2++, k, obj[k]);
                }
            }

            // 逆順追加: 最後に追加したものが上に来る。
            // 目的の表示順: [t1(上)] → [injectControl] → [t2(下)]
            if (r2 > 0) _host.Controls.Add(t2);
            injectControl.Dock = DockStyle.Top;
            _host.Controls.Add(injectControl);
            _host.Controls.Add(t1);
        }

        // 保存前の最終確定。各ウィジェットの値をモデルへ書き戻す。型エラーがあれば false。
        // 通常はフォーカスアウト時に反映済みだが、未確定のまま保存した場合の保険でもある。
        public bool Apply()
        {
            if (_obj == null) return true;
            foreach (var f in _fields)
            {
                switch (f.Kind)
                {
                    case "bool": _obj[f.Name] = f.Chk.Checked; break;
                    // 数値は表示（InvariantCulture）と同じ表記で解釈する。OS のロケールに依存させない。
                    case "int":
                        if (!long.TryParse(f.Tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv)) return Fail(f.Name, I18n.T("type.integer"));
                        _obj[f.Name] = lv; break;
                    case "dbl":
                        if (!double.TryParse(f.Tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)) return Fail(f.Name, I18n.T("type.number"));
                        _obj[f.Name] = dv; break;
                    case "text": _obj[f.Name] = f.Tb.Text; break;
                    case "strlist": _obj[f.Name] = ToStringArray(f.Tb.Text); break;
                    case "abilities":
                        CommitAbilities(f.Name, f.Sub);   // 各欄の入力をそのまま反映（非破壊。空欄は null、構造は維持）
                        break;
                    case "json": if (f.Holder.Changed) _obj[f.Name] = f.Holder.Node; break;
                    case "lifelog": _obj[f.Name] = f.Life.ToArray(); break;
                    case "relationship": _obj[f.Name] = f.Rel.ToObject(); break;
                    case "combo":
                        // ID プルダウン（"ID: 名前" 形式）のみ ID 部分を取り出す。
                        // 値プルダウン（category/job/rarity 等）は ":" を含む値を壊さないよう全文を保存する。
                        string raw = f.Combo.Text.Trim();
                        if (f.IdCombo)
                        {
                            int col = raw.IndexOf(':');
                            _obj[f.Name] = col > 0 ? raw[..col].Trim() : raw;
                        }
                        else _obj[f.Name] = raw;
                        break;
                }
            }
            return true;
        }

        // 型エラー時の共通メッセージ表示（false を返して呼び出し側で中断させる）。
        private static bool Fail(string field, string type)
        { MessageBox.Show(I18n.T("msg.typeError", field, type), I18n.T("title.typeError"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }

        // 2列(ラベル / 入力)の表を作る。左列は固定幅、右列は残り全部=ウィンドウ幅に追従。
        private TableLayoutPanel NewTable()
        {
            var t = new TableLayoutPanel
            { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(6) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        // フィールド名に対する表示用ラベル。未登録のフィールドはキー名をそのまま表示する。
        private static string LabelOf(string field) => field switch
        {
            "look" => I18n.T("label.look"),
            "status" => I18n.T("label.config.status"),
            "level_of_detail" => I18n.T("label.config.levelOfDetail"),
            "is_dead" => I18n.T("label.config.isDead"),
            "is_player" => I18n.T("label.config.isPlayer"),
            "difficulty_level" => I18n.T("label.config.difficultyLevel"),
            "attributes" => I18n.T("label.attributes"),
            "item_detail" => I18n.T("label.itemDetail"),
            "skills" => I18n.T("label.skills"),
            _ => field,
        };

        // 1プロパティ分の行を生成する。値の型・フィールド名に応じてウィジェットを振り分ける:
        //   能力値オブジェクト→6欄 / life_log→グリッド / relationship→グリッド / look→1行1タグ欄 /
        //   登録済みコンボ→プルダウン / bool→チェック / 整数・小数→数値欄 / 文字列→(長文はリサイズ枠) /
        //   文字列だけの辞書→キーごとの枠 / それ以外(配列・複雑な辞書・null)→JSON編集ボタン
        private void AddRow(TableLayoutPanel t, int row, string field, JsonNode val)
        {
            t.Controls.Add(new Label { Text = LabelOf(field), AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, row);
            if (IsAbilityObject(field, val)) { AddAbilityRow(t, row, field, (JsonObject)val); return; }
            // life_log は配列だが専用グリッドで表示・編集する。
            if (field == "life_log" && val is JsonArray la)
            {
                var grid = new LifeLogGrid { Dock = DockStyle.Top };
                grid.Bind(la);
                t.Controls.Add(grid, 1, row);
                _fields.Add(new FieldRef { Name = field, Kind = "lifelog", Life = grid });
                return;
            }
            // relationship（対象→{好感度/状態/関係/会話回数}の辞書）は専用の見やすい欄で表示する。
            if (field == "relationship" && IsRelationshipObject(val))
            { AddRelationshipRow(t, row, (JsonObject)val); return; }
            // look（外見タグの文字列配列）は「1行1タグ」の複数行欄で編集する。
            if (field == "look" && IsStringArray(val))
            { AddStringListRow(t, row, field, (JsonArray)val); return; }
            // party（メンバーID配列）はメンバーの追加/削除を GUI で行う専用欄で表示する。
            if (field == "party" && PartyNpcCandidates != null && val is JsonArray pa)
            { AddPartyRow(t, row, pa); return; }
            // connections（接続先ID配列）はフック設定時のみ "ID: 名前" 一覧＋追加/削除で表示する（area/facility 共通）。
            if (field == "connections" && ConnectionCandidates != null && val is JsonArray)
            { AddConnectionsRow(t, row); return; }
            // config（status / level_of_detail などのスカラ設定）は JSON 編集ボタンにせず、
            // 中身を項目ごとの欄に展開する（quest/story_quest/NPC/area の config 共通）。
            if (field == "config" && IsScalarMap(val))
            { AddConfigRow(t, row, (JsonObject)val); return; }
            // attributes（item_detail＋数値ステータス等のスカラ辞書）は config 同様に項目ごとの欄へ展開する。
            // item_detail は現在の item_type に応じた候補のプルダウンにする（空の attributes でも欄を出す）。
            if (field == "attributes" && val is JsonObject ao && (ao.Count == 0 || IsScalarMap(val)))
            { AddAttributesRow(t, row, ao); return; }
            // inventory（アイテム辞書）は専用のグリッド＋ボタン（プレイヤーと共通の InventoryPanel）で表示・編集する。
            // 有効時のみ。値は InventoryPanel が即時に辞書へ反映するため Apply() の対象外。
            if (field == "inventory" && InventoryGridEnabled && val is JsonObject invObj)
            { AddInventoryRow(t, row, invObj); return; }
            // skills（スキル名→スキルの辞書）は専用の一覧＋ボタン（プレイヤーと共通の SkillListPanel）で編集する。
            // 有効時のみ。値は SkillListPanel が即時に辞書へ反映するため Apply() の対象外。
            if (field == "skills" && SkillsGridEnabled && val is JsonObject skillObj)
            { AddSkillsRow(t, row, skillObj); return; }
            // image_src（画像の相対パス）は、画像一覧から選べるプレビュー付き欄で表示する（有効時のみ）。
            if (field == "image_src" && ImageSrcPickerEnabled && val is JsonValue)
            { AddImageSrcRow(t, row, field, val.ToString()); return; }
            // ComboBox ファクトリが登録されていれば優先して使う（文字列値のみ対象）。
            if (_comboFactories.TryGetValue(field, out var combo) && val is JsonValue)
            {
                var cb = combo.factory(val.ToString());
                cb.Dock = DockStyle.Fill;
                t.Controls.Add(cb, 1, row);
                _fields.Add(new FieldRef { Name = field, Kind = "combo", Combo = cb, IdCombo = combo.idPrefixed });
                return;
            }
            if (val is JsonValue jv)
            {
                if (jv.TryGetValue<bool>(out bool b))
                {
                    var c = new CheckBox { Checked = b, AutoSize = true };
                    string fld = field;
                    c.CheckedChanged += (_, _) => { if (_obj != null) _obj[fld] = c.Checked; };
                    t.Controls.Add(c, 1, row); _fields.Add(new FieldRef { Name = field, Kind = "bool", Chk = c });
                }
                else if (jv.TryGetValue<long>(out long lv))
                {
                    var tb = new TextBox { Text = lv.ToString(), Width = 160 };
                    var fr = new FieldRef { Name = field, Kind = "int", Tb = tb };
                    tb.Leave += (_, _) => CommitField(fr);
                    t.Controls.Add(tb, 1, row); _fields.Add(fr);
                }
                else if (jv.TryGetValue<double>(out double dv))
                {
                    var tb = new TextBox { Text = dv.ToString(CultureInfo.InvariantCulture), Width = 160 };
                    var fr = new FieldRef { Name = field, Kind = "dbl", Tb = tb };
                    tb.Leave += (_, _) => CommitField(fr);
                    t.Controls.Add(tb, 1, row); _fields.Add(fr);
                }
                else
                {
                    string s = jv.ToString();
                    bool multi = LongText.Contains(field) || s.Length > 60;
                    if (multi)
                    {
                        var rtb = new ResizableTextBox(520, 96) { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 8) };
                        rtb.Box.Text = s;
                        var fr = new FieldRef { Name = field, Kind = "text", Tb = rtb.Box };
                        rtb.Box.Leave += (_, _) => CommitField(fr);
                        t.Controls.Add(rtb, 1, row); _fields.Add(fr);
                    }
                    else
                    {
                        var tb = new TextBox { Text = s, Dock = DockStyle.Fill };
                        var fr = new FieldRef { Name = field, Kind = "text", Tb = tb };
                        tb.Leave += (_, _) => CommitField(fr);
                        t.Controls.Add(tb, 1, row); _fields.Add(fr);
                    }
                }
            }
            else if (val is JsonObject mo && mo.Count > 0 && mo.Count <= 12 && AllStrings(mo))
                AddStringMapRow(t, row, field, mo);
            else { AddJsonRow(t, row, field, val); }
        }

        // image_src を画像一覧ダイアログから選べる行（プレビュー＋パス欄＋参照ボタン）。
        // パス欄は通常の text フィールドとして登録し、Apply() でモデルへ書き戻す。
        // 参照ボタンは ImagePickerDialog を開き、選択結果（アセット先からの相対パス）を欄へ反映する。
        private void AddImageSrcRow(TableLayoutPanel t, int row, string field, string src)
        {
            var inner = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

            var pic = new PictureBox { Width = 48, Height = 48, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(2) };
            var tb = new TextBox { Text = src, Dock = DockStyle.Fill };
            var fr = new FieldRef { Name = field, Kind = "text", Tb = tb };
            var btn = new Button { Text = I18n.T("btn.browse"), Width = 88, Margin = new Padding(2) };

            void RefreshPreview()
            {
                var old = pic.Image;
                pic.Image = LoadAssetImage(Settings.Current?.GameAssetRoot, tb.Text);
                old?.Dispose();
            }
            tb.Leave += (_, _) => { CommitField(fr); RefreshPreview(); };
            btn.Click += (_, _) =>
            {
                string assetRoot = Settings.Current?.GameAssetRoot ?? "";
                if (string.IsNullOrEmpty(assetRoot))
                { MessageBox.Show(I18n.T("imgpick.noAssetRoot"), I18n.T("imgpick.title"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                using var dlg = new ImagePickerDialog(assetRoot, tb.Text);
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK && !string.IsNullOrEmpty(dlg.ResultPath))
                { tb.Text = dlg.ResultPath; CommitField(fr); RefreshPreview(); }
            };

            inner.Controls.Add(pic, 0, 0);
            inner.Controls.Add(tb, 1, 0);
            inner.Controls.Add(btn, 2, 0);
            t.Controls.Add(inner, 1, row);
            _fields.Add(fr);
            _imageSrcBox = tb; _imageSrcRefresh = RefreshPreview;   // item_detail 連動で書き換える対象
            RefreshPreview();
        }

        // item_detail に対応する既定画像を image_src 欄・モデル・プレビューへ反映する（フック設定時のみ）。
        private void ApplyDefaultImageForDetail(string detail)
        {
            if (DefaultImageForDetail == null || _imageSrcBox == null || _obj == null) return;
            string img = DefaultImageForDetail(detail) ?? "";
            _imageSrcBox.Text = img;
            _obj["image_src"] = img;
            _imageSrcRefresh?.Invoke();
        }

        // アセット先からの相対パスで画像を読み込む（プレビュー用）。解決不能・未設定・例外時は null。
        // 元PNGをロックしないよう ReadAllBytes→MemoryStream 経由で複製を返す。
        private static Image LoadAssetImage(string assetRoot, string rel)
        {
            if (string.IsNullOrEmpty(assetRoot) || string.IsNullOrEmpty(rel)) return null;
            try
            {
                string full = Path.Combine(assetRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return null;
                byte[] bytes = File.ReadAllBytes(full);
                using var ms = new MemoryStream(bytes);
                using var tmp = Image.FromStream(ms);
                return new Bitmap(tmp);
            }
            catch { return null; }
        }

        // 辞書の値がすべて文字列か（= キーごとのテキスト枠で表示できるか）。
        private static bool AllStrings(JsonObject o)
        {
            foreach (var kv in o)
                if (kv.Value is not JsonValue v || !v.TryGetValue<string>(out _)) return false;
            return true;
        }

        // 文字列だけの辞書(例: area の descriptions)を、キーごとのリサイズ枠で表示する。
        private void AddStringMapRow(TableLayoutPanel t, int row, string field, JsonObject map)
        {
            var inner = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;
            foreach (var kv in map)
            {
                inner.Controls.Add(new Label { Text = kv.Key, AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, 0, r);
                var rtb = new ResizableTextBox(480, 72) { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 8) };
                rtb.Box.Text = J.Str(map, kv.Key);
                string k = kv.Key;
                rtb.Box.Leave += (_, _) => { if (_obj != null) map[k] = rtb.Box.Text; };
                inner.Controls.Add(rtb, 1, r);
                r++;
            }
            t.Controls.Add(inner, 1, row);
        }

        // スカラ値（文字列/数値/真偽）だけの小さな辞書か（= 項目ごとの欄に展開できるか）。
        private static bool IsScalarMap(JsonNode val)
        {
            if (val is not JsonObject o || o.Count == 0 || o.Count > 12) return false;
            foreach (var kv in o) if (kv.Value is not JsonValue) return false;
            return true;
        }

        // config（スカラ設定の辞書）を、キーごとに型に応じた欄(チェック/数値/プルダウン/テキスト)で展開する。
        // 値は各ウィジェットの確定時に config オブジェクトへ直接書き戻すため Apply() の対象外。
        private void AddConfigRow(TableLayoutPanel t, int row, JsonObject cfg)
        {
            var inner = MakeScalarMapTable();
            int r = 0;
            foreach (var kv in cfg)
            {
                string k = kv.Key;
                inner.Controls.Add(new Label { Text = LabelOf(k), AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, 0, r);
                AddScalarValueCell(inner, r, cfg, k, v => MakeConfigStringBox(cfg, k, v));
                r++;
            }
            t.Controls.Add(inner, 1, row);
        }

        // attributes（item_detail＋数値ステータス等のスカラ辞書）を項目ごとの欄に展開する。
        // item_type 連動の作り替えに対応するため、外側テーブル・行・対象を控えて BuildAttributesInner で構築する。
        private void AddAttributesRow(TableLayoutPanel t, int row, JsonObject attrs)
        {
            _attrsOuter = t; _attrsRow = row; _attrsObj = attrs; _attrsInner = null;
            BuildAttributesInner();
        }

        // attributes 展開の内側テーブルを（再）構築する。既存があれば破棄して差し替える。
        // item_detail は item_type 連動のプルダウン（AttributeDetailOptions 設定時。空の attributes でも先頭に出す）、
        // 数値ステータス等は config と同じ型別の欄。値は各ウィジェットの確定時に attributes へ直接書き戻す。
        private void BuildAttributesInner()
        {
            if (_attrsOuter == null || _attrsObj == null) return;
            if (_attrsInner != null) { _attrsOuter.Controls.Remove(_attrsInner); _attrsInner.Dispose(); }
            _itemDetailCombo = null;
            var inner = MakeScalarMapTable();
            int r = 0;
            bool detailDropdown = AttributeDetailOptions != null;
            if (detailDropdown)
            {
                inner.Controls.Add(new Label { Text = LabelOf("item_detail"), AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, 0, r);
                var cb = new ComboBox
                { DropDownStyle = ComboBoxStyle.DropDown, Width = 220, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                cb.Text = J.Str(_attrsObj, "item_detail");
                cb.Leave += (_, _) => { if (_obj != null) _attrsObj["item_detail"] = cb.Text.Trim(); };
                _itemDetailCombo = cb;
                inner.Controls.Add(cb, 1, r);
                PopulateItemDetail();   // 初期候補を現在の item_type から流し込む
                // item_detail をユーザーが選び直したら image_src を該当画像へ更新する。
                // 構築・候補再構築中の自動発火は _suppressDetailEvents で抑止する（配線は PopulateItemDetail の後）。
                cb.SelectedIndexChanged += (_, _) =>
                {
                    if (_obj == null || _suppressDetailEvents) return;
                    string d = cb.Text.Trim();
                    _attrsObj["item_detail"] = d;
                    ApplyDefaultImageForDetail(d);
                };
                r++;
            }
            foreach (var kv in _attrsObj)
            {
                if (kv.Key == "item_detail" && detailDropdown) continue;   // 上で専用欄として出した
                inner.Controls.Add(new Label { Text = LabelOf(kv.Key), AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, 0, r);
                AddScalarValueCell(inner, r, _attrsObj, kv.Key, v => MakePlainStringBox(_attrsObj, kv.Key, v));
                r++;
            }
            _attrsInner = inner;
            _attrsOuter.Controls.Add(inner, 1, _attrsRow);
        }

        // item_type 変更時に attributes をスキーマ（AttributeStatKeys）へ合わせて作り替え、表示を更新する。
        // 既存値は保持、スキーマに無いキー（item_detail を除く）は削除、不足キーは 0 で追加する。
        // 表示順は [item_detail, スキーマのキー…]。item_detail が新 type の候補外ならクリアする。
        // スキーマ不明（候補が空＝未知の item_type）の場合は作り替えず、item_detail 候補のみ更新する。
        public void ApplyAttributeSchema()
        {
            if (_attrsObj == null || _obj == null) return;
            string type = J.Str(_obj, "item_type");
            var statKeys = AttributeStatKeys?.Invoke(type) ?? Array.Empty<string>();
            if (statKeys.Count == 0) { PopulateItemDetail(); return; }   // 未知の type は壊さない

            bool detailDropdown = AttributeDetailOptions != null;
            // item_type 変更時は item_detail を新 type の候補の先頭（プルダウン一番上）へ初期化する。
            // 旧 type 由来の item_detail は別カテゴリのため引き継がない。
            string detail = J.Str(_attrsObj, "item_detail");
            if (detailDropdown)
            {
                var opts = AttributeDetailOptions(type);
                detail = opts != null && opts.Count > 0 ? opts[0] : "";
            }

            // 新しい attributes を [item_detail, スキーマのキー…] の順で組み直す（既存値は複製で保持）。
            var rebuilt = new JsonObject();
            if (detailDropdown || _attrsObj.ContainsKey("item_detail"))
                rebuilt["item_detail"] = detail;
            foreach (var key in statKeys)
                rebuilt[key] = _attrsObj[key] is JsonValue v ? v.DeepClone() : JsonValue.Create(0L);

            _obj["attributes"] = rebuilt;
            _attrsObj = rebuilt;
            BuildAttributesInner();

            // item_detail の初期値に対応する画像を image_src へ反映する。
            ApplyDefaultImageForDetail(detail);
        }

        // inventory（アイテム辞書）を InventoryPanel（グリッド＋追加/編集/削除）で表示・編集する。
        // 追加/削除/編集は inv へ即時反映されるため FieldRef は登録しない（Apply() の対象外）。
        private void AddInventoryRow(TableLayoutPanel t, int row, JsonObject inv)
        {
            var panel = new InventoryPanel { Dock = DockStyle.Top };
            panel.Bind(inv);
            t.Controls.Add(panel, 1, row);
        }

        // skills（スキル辞書）を SkillListPanel（一覧＋追加/編集/削除。プレイヤーと共通）で表示・編集する。
        // 追加/削除/編集は辞書へ即時反映されるため FieldRef は登録しない（Apply() の対象外）。
        private void AddSkillsRow(TableLayoutPanel t, int row, JsonObject skills)
        {
            var panel = new SkillListPanel { Dock = DockStyle.Top };
            panel.Bind(skills);
            t.Controls.Add(panel, 1, row);
        }

        // ラベル列固定・値列可変の2列テーブル（config / attributes の展開で共用）。
        private static TableLayoutPanel MakeScalarMapTable()
        {
            var inner = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return inner;
        }

        // スカラ値1件分の編集ウィジェットを inner の値列(r行)に追加する。
        // bool→チェック / 整数・小数→数値欄（確定時に map へ直接反映） / それ以外→stringWidget(現在値)。
        private void AddScalarValueCell(TableLayoutPanel inner, int r, JsonObject map, string key, Func<string, Control> stringWidget)
        {
            var jv = map[key] as JsonValue;
            if (jv != null && jv.TryGetValue<bool>(out bool b))
            {
                var c = new CheckBox { Checked = b, AutoSize = true };
                c.CheckedChanged += (_, _) => { if (_obj != null) map[key] = c.Checked; };
                inner.Controls.Add(c, 1, r);
            }
            else if (jv != null && jv.TryGetValue<long>(out long lv))
            {
                var tb = new TextBox { Text = lv.ToString(), Width = 160 };
                tb.Leave += (_, _) => { if (_obj == null) return; if (long.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)) { map[key] = n; Ok(tb); } else tb.BackColor = Color.MistyRose; };
                inner.Controls.Add(tb, 1, r);
            }
            else if (jv != null && jv.TryGetValue<double>(out double dv))
            {
                var tb = new TextBox { Text = dv.ToString(CultureInfo.InvariantCulture), Width = 160 };
                tb.Leave += (_, _) => { if (_obj == null) return; if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) { map[key] = n; Ok(tb); } else tb.BackColor = Color.MistyRose; };
                inner.Controls.Add(tb, 1, r);
            }
            else
            {
                inner.Controls.Add(stringWidget(jv?.ToString() ?? ""), 1, r);
            }
        }

        // スカラ辞書の文字列値を編集する素のテキスト欄（フォーカスアウトで map へ反映）。
        private Control MakePlainStringBox(JsonObject map, string key, string val)
        {
            var tb = new TextBox { Text = val, Dock = DockStyle.Fill };
            tb.Leave += (_, _) => { if (_obj != null) map[key] = tb.Text; };
            return tb;
        }

        // item_detail プルダウンの候補を、現在の item_type に応じて入れ替える。現在値は候補外でも保持する。
        // item_type 変更時に ItemEditDialog から RefreshAttributeDetailOptions 経由で呼ぶ。
        public void RefreshAttributeDetailOptions() => PopulateItemDetail();

        private void PopulateItemDetail()
        {
            if (_itemDetailCombo == null || AttributeDetailOptions == null || _obj == null) return;
            var opts = AttributeDetailOptions(J.Str(_obj, "item_type")) ?? Array.Empty<string>();
            string cur = _itemDetailCombo.Text;
            // 候補の入れ替え・Text 再設定が SelectedIndexChanged を誘発し image_src を巻き込むのを抑止する。
            _suppressDetailEvents = true;
            try
            {
                _itemDetailCombo.BeginUpdate();
                _itemDetailCombo.Items.Clear();
                foreach (var o in opts) _itemDetailCombo.Items.Add(o);
                _itemDetailCombo.EndUpdate();
                _itemDetailCombo.Text = cur;   // 既存値は保持（候補外でも消さない）
            }
            finally { _suppressDetailEvents = false; }
        }

        // config の文字列値の欄。status は incomplete/completed の編集可プルダウン、その他はテキスト枠。
        private Control MakeConfigStringBox(JsonObject cfg, string key, string val)
        {
            if (key == "status")
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 200 };
                cb.Items.AddRange(new object[] { "incomplete", "completed" });
                cb.Text = val;
                cb.Leave += (_, _) => { if (_obj != null) cfg[key] = cb.Text.Trim(); };
                return cb;
            }
            var tb = new TextBox { Text = val, Dock = DockStyle.Fill };
            tb.Leave += (_, _) => { if (_obj != null) cfg[key] = tb.Text; };
            return tb;
        }

        // 値がすべて文字列の配列か（= 1行1タグの複数行欄で表示できるか）。空配列も対象とする。
        private static bool IsStringArray(JsonNode val)
        {
            if (val is not JsonArray a) return false;
            foreach (var n in a)
                if (n is not JsonValue v || !v.TryGetValue<string>(out _)) return false;
            return true;
        }

        // 文字列配列(look)を「1行1タグ」のリサイズ枠で表示する。保存は ToStringArray で配列へ戻す。
        private void AddStringListRow(TableLayoutPanel t, int row, string field, JsonArray arr)
        {
            var rtb = new ResizableTextBox(520, 110) { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 8) };
            rtb.Box.Text = string.Join(Environment.NewLine, arr.Select(n => n?.ToString() ?? ""));
            var fr = new FieldRef { Name = field, Kind = "strlist", Tb = rtb.Box };
            rtb.Box.Leave += (_, _) => { if (_obj != null) { _obj[field] = ToStringArray(rtb.Box.Text); Ok(rtb.Box); } };
            t.Controls.Add(rtb, 1, row);
            _fields.Add(fr);
        }

        // 複数行テキストを「1行1要素・前後空白除去・空行除外」で文字列配列へ変換する。
        private static JsonArray ToStringArray(string text)
        {
            var a = new JsonArray();
            foreach (var line in text.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length > 0) a.Add(s);
            }
            return a;
        }

        // relationship の各対象が持つ既知のキー。これ以外を含む場合は専用表示せず JSON 編集に回す。
        private static readonly HashSet<string> RelInnerKeys = new()
        { "affinity", "affinity_text", "relationship", "conversation_count" };

        // relationship が「対象→既知キーだけの辞書」の形か判定する。未知の形は false（JSON編集にフォールバック）。
        private static bool IsRelationshipObject(JsonNode val)
        {
            if (val is not JsonObject o || o.Count == 0) return false;
            foreach (var kv in o)
            {
                if (kv.Value is not JsonObject inner) return false;
                foreach (var ik in inner)
                    if (!RelInnerKeys.Contains(ik.Key)) return false;
            }
            return true;
        }

        // relationship を対象1行ずつのグリッドで表示する。player を先頭固定、対象が増えても自動で増える。
        // life_log と同様に編集はグリッド内に保持し、保存直前の Apply() で辞書へ書き戻す。
        private void AddRelationshipRow(TableLayoutPanel t, int row, JsonObject rel)
        {
            var grid = new RelationshipGrid { Dock = DockStyle.Top, TargetNamer = RelationshipTargetNamer };
            grid.Bind(rel);
            t.Controls.Add(grid, 1, row);
            _fields.Add(new FieldRef { Name = "relationship", Kind = "relationship", Rel = grid });
        }

        // フォーカスが外れた時点で 1 項目だけ検証して即反映する。
        // 不正値は赤くして書き込まない（保存時の Apply で最終的に弾く）。
        private void CommitField(FieldRef f)
        {
            if (_obj == null) return;
            switch (f.Kind)
            {
                // 数値は表示（InvariantCulture）と同じ表記で解釈する。OS のロケールに依存させない。
                case "int":
                    if (long.TryParse(f.Tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv)) { _obj[f.Name] = lv; Ok(f.Tb); } else Bad(f.Tb);
                    break;
                case "dbl":
                    if (double.TryParse(f.Tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)) { _obj[f.Name] = dv; Ok(f.Tb); } else Bad(f.Tb);
                    break;
                case "text":
                    _obj[f.Name] = f.Tb.Text; Ok(f.Tb);
                    break;
            }
        }

        private static void Ok(TextBox tb) => tb.BackColor = System.Drawing.SystemColors.Window;
        private static void Bad(TextBox tb) => tb.BackColor = Color.MistyRose;

        // 能力値6項目を 2列×3行の数値欄として表示する。各欄はフォーカスアウトで一括確定。
        private void AddAbilityRow(TableLayoutPanel t, int row, string field, JsonObject abil)
        {
            var inner = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, Dock = DockStyle.Fill }; // ラベル,欄,ラベル,欄
            var boxes = new Dictionary<string, TextBox>();
            int c = 0, r = 0;
            foreach (var (key, _) in AbilityKeys)
            {
                inner.Controls.Add(new Label { Text = $"{I18n.T("ability." + key)} ({key})", AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, c, r);
                // 数値が入っていれば表示、未設定(null 等)は空欄にする。
                // 未生成NPCの雛形は各能力値が null のため、これを "0" と表示して 0 と誤確定させない。
                string disp = "";
                if (abil[key] is JsonValue v)
                {
                    if (v.TryGetValue<long>(out long lvv)) disp = lvv.ToString();
                    else if (v.TryGetValue<double>(out double dvv)) disp = dvv.ToString(CultureInfo.InvariantCulture);
                }
                var tb = new TextBox { Width = 64, Text = disp };
                tb.Leave += (_, _) => CommitAbilities(field, boxes);   // どれか1つ離れたら6項目まとめて確定
                inner.Controls.Add(tb, c + 1, r);
                boxes[key] = tb;
                c += 2; if (c >= 4) { c = 0; r++; }   // 2項目で次の行へ
            }
            t.Controls.Add(inner, 1, row);
            _fields.Add(new FieldRef { Name = field, Kind = "abilities", Sub = boxes });
        }

        // 能力値6項目を一括で確定する。各欄の入力をそのまま反映する（0 や負値も有効な能力値として保持する）。
        // 空欄は JSON null（未設定）として書き戻し、ability_scores オブジェクトの構造（6キー）は常に維持する。
        //
        // 旧実装は「1つでも 0/空/不正なら ability_scores オブジェクト全体を null（未生成扱い）」にしていた。
        // 未生成NPCの雛形は ability_scores = {strength:null, ...}（各値 null の6キーオブジェクト）であり、
        // 旧実装ではツリー切替時の Apply() でこのオブジェクトごと null へ化け、ゲームの生成処理が
        // dict 参照（ability_scores[...] / .items()）で落ちていた。閲覧して切り替えるだけでも壊れる。
        // ここではオブジェクトを壊さず、各値を入力のまま（未入力は null）で書き戻すことで雛形を完全保持する。
        private void CommitAbilities(string field, Dictionary<string, TextBox> boxes)
        {
            if (_obj == null) return;
            // 既存の ability_scores オブジェクトを基に書き戻す（無ければ新規オブジェクト）。オブジェクトの null 化はしない。
            var result = _obj[field] is JsonObject cur ? (JsonObject)cur.DeepClone() : new JsonObject();
            foreach (var kv in boxes)
            {
                string txt = kv.Value.Text.Trim();
                if (txt.Length == 0) { result[kv.Key] = null; Ok(kv.Value); }      // 空欄は未設定(null)として保持
                else if (long.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv)) { result[kv.Key] = lv; Ok(kv.Value); }
                else if (double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv)) { result[kv.Key] = dv; Ok(kv.Value); }
                else Bad(kv.Value);                                                // 数値でない入力は反映しない（既存値を維持）
            }
            _obj[field] = result;
        }

        // party（["player", NPC ID...]）のメンバーを GUI で追加/削除する。
        // 各メンバーは1行で "ID: 名前" と「削除」ボタン（player は固定で削除不可）。
        // 末尾に未参加 NPC のプルダウン＋「追加」ボタンを置く。編集は即 _obj["party"] へ反映し一覧を作り直す。
        // 値は即反映のため FieldRef は登録せず、Apply() の対象にしない。
        private void AddPartyRow(TableLayoutPanel t, int row, JsonArray arr)
        {
            var outer = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };

            // 現在の party 配列（無ければ作って _obj に差し込む）。
            JsonArray Party() => (_obj?["party"] as JsonArray) ?? (JsonArray)(_obj != null ? (_obj["party"] = new JsonArray()) : new JsonArray());

            void Fill()
            {
                outer.Controls.Clear();
                var party = Party();
                var members = party.Select(n => n?.ToString() ?? "").ToList();

                foreach (var id in members)
                {
                    var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 2) };
                    if (id == "player")
                    {
                        line.Controls.Add(new Label { Text = I18n.T("party.player"), AutoSize = true, Padding = new Padding(2, 6, 8, 0) });
                    }
                    else
                    {
                        string name = PartyNpcNamer?.Invoke(id);
                        bool dead = PartyNpcIsDead?.Invoke(id) ?? false;
                        string txt = (name != null ? $"{id}: {name}" : I18n.T("party.unknownNpc", id)) + (dead ? I18n.T("suffix.dead") : "");
                        line.Controls.Add(new Label
                        { Text = txt, AutoSize = true, ForeColor = dead ? Color.Firebrick : SystemColors.ControlText, Padding = new Padding(2, 6, 8, 0) });
                        string cid = id;
                        var del = new Button { Text = I18n.T("btn.delete"), AutoSize = true };
                        del.Click += (_, _) =>
                        {
                            int idx = FindIndex(Party(), cid);
                            if (idx >= 0) Party().RemoveAt(idx);
                            Fill();
                        };
                        line.Controls.Add(del);
                    }
                    outer.Controls.Add(line);
                }

                // 追加行: まだ party にいない NPC をプルダウンで選んで「追加」。死亡 NPC は「（死亡）」付き。
                var addLine = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
                var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
                var candidates = (PartyNpcCandidates?.Invoke() ?? Enumerable.Empty<(string, string, bool)>())
                    .Where(c => !members.Contains(c.Item1))
                    .OrderBy(c => c.Item1.Length).ThenBy(c => c.Item1)
                    .ToList();
                foreach (var (cid, cname, dead) in candidates)
                    combo.Items.Add($"{cid}: {cname}" + (dead ? I18n.T("suffix.dead") : ""));
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;

                var add = new Button { Text = I18n.T("btn.add"), AutoSize = true, Enabled = combo.Items.Count > 0 };
                add.Click += (_, _) =>
                {
                    int idx = combo.SelectedIndex;
                    if (idx < 0 || idx >= candidates.Count) return;
                    var (id, cname, dead) = candidates[idx];
                    // 死亡 NPC の追加はゲームが壊れる恐れがあるため確認する。
                    if (dead && MessageBox.Show(this,
                            I18n.T("msg.addDeadConfirm", cname),
                            I18n.T("title.addDead"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                        return;
                    if (FindIndex(Party(), id) < 0) Party().Add(id);
                    Fill();
                };
                addLine.Controls.Add(new Label { Text = I18n.T("label.addColon"), AutoSize = true, Padding = new Padding(2, 6, 4, 0) });
                addLine.Controls.Add(combo);
                addLine.Controls.Add(add);
                outer.Controls.Add(addLine);
            }

            Fill();
            t.Controls.Add(outer, 1, row);
        }

        // connections（接続先ID配列）を、スキル一覧と同じ「一覧＋ボタンバー」形式で編集する。
        // 上段に候補プルダウン＋「追加」「削除」ボタン、下段に "ID: 名前" の ListBox。
        // 追加・削除は即 _obj["connections"] へ反映し、ConnectionAdded/Removed で相手側にも通知して双方向を保つ。
        // FieldRef は登録せず Apply() の対象にしない。
        private void AddConnectionsRow(TableLayoutPanel t, int row)
        {
            var panel = new Panel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            var list = new ListBox { Dock = DockStyle.Top, Height = 110 };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Margin = new Padding(2) };
            var add = new Button { Text = I18n.T("btn.add"), Width = 70, Margin = new Padding(2) };
            var del = new Button { Text = I18n.T("btn.delete"), Width = 70, Margin = new Padding(2) };

            // 現在の connections 配列（無ければ作って _obj に差し込む）。
            JsonArray Conns() => (_obj?["connections"] as JsonArray) ?? (JsonArray)(_obj != null ? (_obj["connections"] = new JsonArray()) : new JsonArray());
            var ids = new List<string>();                       // 一覧の行 → 接続先 ID
            var candidates = new List<(string id, string name)>();  // プルダウンの行 → 候補

            void Refresh()
            {
                list.Items.Clear(); ids.Clear();
                foreach (var n in Conns())
                {
                    string id = n?.ToString() ?? "";
                    ids.Add(id);
                    string name = ConnectionNamer?.Invoke(id);
                    list.Items.Add(name != null ? $"{id}: {name}" : I18n.T("conn.unknown", id));
                }
                combo.Items.Clear(); candidates.Clear();
                foreach (var c in (ConnectionCandidates?.Invoke() ?? Enumerable.Empty<(string, string)>())
                    .Where(c => !ids.Contains(c.Item1))
                    .OrderBy(c => c.Item1.Length).ThenBy(c => c.Item1))
                {
                    candidates.Add(c);
                    combo.Items.Add($"{c.Item1}: {c.Item2}");
                }
                if (combo.Items.Count > 0) combo.SelectedIndex = 0;
                add.Enabled = combo.Items.Count > 0;
            }

            add.Click += (_, _) =>
            {
                int i = combo.SelectedIndex;
                if (i < 0 || i >= candidates.Count) return;
                string id = candidates[i].id;
                if (FindIndex(Conns(), id) < 0) Conns().Add(id);
                ConnectionAdded?.Invoke(id);   // 相手側へも接続を張る（双方向化。WorldTab が実装）
                Refresh();
            };
            del.Click += (_, _) =>
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= ids.Count) return;
                string id = ids[i];
                int idx = FindIndex(Conns(), id);
                if (idx >= 0) Conns().RemoveAt(idx);
                ConnectionRemoved?.Invoke(id);   // 相手側の接続も外す（双方向化。WorldTab が実装）
                Refresh();
            };

            bar.Controls.Add(combo); bar.Controls.Add(add); bar.Controls.Add(del);
            // Dock=Top は後入れが上。下から: 一覧 → ボタン列。
            panel.Controls.Add(list);
            panel.Controls.Add(bar);
            Refresh();
            t.Controls.Add(panel, 1, row);
        }

        // JsonArray 内で文字列値が id に一致する最初の位置を返す（無ければ -1）。
        private static int FindIndex(JsonArray arr, string id)
        {
            for (int i = 0; i < arr.Count; i++)
                if ((arr[i]?.ToString() ?? "") == id) return i;
            return -1;
        }

        // 配列・複雑な辞書・null など、フォーム化しにくい値は「JSON編集...」ボタンで開く。
        // ダイアログ OK 時にその場でモデルへ反映する（保存を待たない）。
        private void AddJsonRow(TableLayoutPanel t, int row, string field, JsonNode val)
        {
            var holder = new JsonHolder { Node = val };
            var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var lbl = new Label { Text = J.Preview(val), AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 8, 0) };
            holder.Preview = lbl;
            var btn = new Button { Text = I18n.T("btn.jsonEdit"), Width = 100 };
            string captured = field;
            btn.Click += (_, _) =>
            {
                using var d = new JsonEditDialog(I18n.T("title.editField", captured), holder.Node);
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    holder.Node = d.ResultNode; holder.Changed = true; lbl.Text = J.Preview(holder.Node);
                    if (_obj != null) _obj[captured] = d.ResultNode;
                }
            };
            panel.Controls.Add(lbl); panel.Controls.Add(btn);
            t.Controls.Add(panel, 1, row);
            _fields.Add(new FieldRef { Name = field, Kind = "json", Holder = holder });
        }
    }

    // エリア/ノード の ComboBox 生成ヘルパ。
    // 表示形式 "ID: 名前"、出力は ID 部分のみ（Apply() 側で分割）。
    // フォーカスアウト時に純粋な数値を入力した場合は自動補完する。
    internal static class AreaComboHelper
    {
        // areas 辞書からエリア一覧の ComboBox を作る。
        public static ComboBox MakeAreaCombo(JsonObject areas, string currentVal)
        {
            var cb = MakeBase();
            if (areas != null)
                foreach (var kv in areas.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                {
                    string name = kv.Value is JsonObject o ? J.Str(o, "name", kv.Key) : kv.Key;
                    cb.Items.Add($"{kv.Key}: {name}");
                }
            AutoFill(cb, currentVal);
            cb.Leave += (_, _) => AutoFill(cb, cb.Text);
            return cb;
        }

        // areas 配下のノード一覧の ComboBox を作る。
        // matchArea が指定されていれば該当エリアのノードのみ、なければ全エリアのノードを列挙する。
        public static ComboBox MakeNodeCombo(JsonObject areas, string matchArea, string currentVal)
        {
            var cb = MakeBase();
            FillNodeItems(cb, areas, matchArea);
            AutoFill(cb, currentVal);
            cb.Leave += (_, _) => AutoFill(cb, cb.Text);
            return cb;
        }

        // 既存の ComboBox のノード一覧を作り直す（current_area 変更時の再構築に使う）。
        // matchArea が指定されていれば該当エリアのノードのみ、なければ全エリアのノードを列挙する。
        // 単一エリアに絞れた場合は "ID: 名前"、全エリア列挙時のみ末尾に [エリア名] を付ける。
        public static void FillNodeItems(ComboBox cb, JsonObject areas, string matchArea)
        {
            cb.Items.Clear();
            if (areas == null) return;
            bool single = !string.IsNullOrEmpty(matchArea) && areas[matchArea] is JsonObject;
            IEnumerable<KeyValuePair<string, JsonNode>> targets =
                single ? new[] { KeyValuePair.Create(matchArea, areas[matchArea]) } : areas;
            foreach (var akv in targets)
            {
                if (akv.Value is not JsonObject ao) continue;
                string areaName = J.Str(ao, "name", akv.Key);
                var nodes = J.Obj(ao, "nodes");
                if (nodes == null) continue;
                foreach (var nkv in nodes.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                {
                    string nodeName = nkv.Value is JsonObject no ? J.Str(no, "name", nkv.Key) : nkv.Key;
                    cb.Items.Add(single ? $"{nkv.Key}: {nodeName}" : $"{nkv.Key}: {nodeName} [{areaName}]");
                }
            }
        }

        // current_location の候補から除外する施設種別（入口/出口/区画ハブなど移動用の構造）。
        private static readonly HashSet<string> StructuralFacilityTypes = new() { "entrance", "exit", "ward" };

        // 指定エリア配下の施設一覧の ComboBox を作る（NPC の current_location 用）。
        public static ComboBox MakeFacilityCombo(JsonObject areas, string matchArea, string currentVal)
        {
            var cb = MakeBase();
            FillFacilityItems(cb, areas, matchArea, ExtractId(currentVal));
            AutoFill(cb, currentVal);
            cb.Leave += (_, _) => AutoFill(cb, cb.Text);
            return cb;
        }

        // 既存の ComboBox の施設一覧を作り直す（current_area 変更時の再構築に使う）。
        // matchArea のエリア配下（全ノード）の施設を列挙し、入口/出口/区画などの構造施設は除外する。
        // ただし ensureId に一致する施設は構造施設でも残す（現在設定中の値を消さず名前を表示するため）。
        // 単一エリアに絞れた場合は "ID: 名前"、全エリア列挙時のみ末尾に [エリア名] を付ける。
        public static void FillFacilityItems(ComboBox cb, JsonObject areas, string matchArea, string ensureId = null)
        {
            cb.Items.Clear();
            if (areas == null) return;
            bool single = !string.IsNullOrEmpty(matchArea) && areas[matchArea] is JsonObject;
            IEnumerable<KeyValuePair<string, JsonNode>> targets =
                single ? new[] { KeyValuePair.Create(matchArea, areas[matchArea]) } : areas;
            foreach (var akv in targets)
            {
                if (akv.Value is not JsonObject ao) continue;
                string areaName = J.Str(ao, "name", akv.Key);
                if (J.Obj(ao, "nodes") is not JsonObject nodes) continue;
                foreach (var nkv in nodes)
                {
                    if (J.Obj(nkv.Value as JsonObject, "facilities") is not JsonObject facs) continue;
                    foreach (var fkv in facs.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                    {
                        if (fkv.Value is not JsonObject fo) continue;
                        if (StructuralFacilityTypes.Contains(J.Str(fo, "facility_type")) && fkv.Key != ensureId) continue;
                        string fname = J.Str(fo, "name", fkv.Key);
                        cb.Items.Add(single ? $"{fkv.Key}: {fname}" : $"{fkv.Key}: {fname} [{areaName}]");
                    }
                }
            }
        }

        // テキストから ID 部分（":"の前）を抽出する。セパレータがなければテキスト全体を返す。
        public static string ExtractId(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            int col = text.IndexOf(':');
            return col > 0 ? text[..col].Trim() : text.Trim();
        }

        private static ComboBox MakeBase() => new()
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Dock = DockStyle.Fill,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems,
        };

        // テキストが純粋な ID なら対応する "ID: 名前" 形式へ補完する。
        private static void AutoFill(ComboBox cb, string val)
        {
            if (string.IsNullOrEmpty(val)) { cb.Text = ""; return; }
            string id = ExtractId(val);
            foreach (string item in cb.Items)
                if (item.Split(':')[0].Trim() == id) { cb.Text = item; return; }
            cb.Text = val;
        }
    }
}

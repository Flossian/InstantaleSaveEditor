// 共通部品: コーデック / JSON ヘルパ / JSON編集ダイアログ / 汎用オブジェクトフォーム
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // ---------------- コーデック ----------------
    // セーブ/ワールドデータとアプリ内部表現(JSON)を相互変換する。
    internal static class Codec
    {
        private static readonly byte[] S =
            Convert.FromBase64String("SW5zdGFudGFsZV9TYXZlX0tleV8yMDI2");

        // ゲームが書き出すのと同じ形式: 最小化(空白なし) / UTF-8 / 非ASCIIをそのまま出す。
        // これと一致しないとバイト単位での再現性が崩れるため Encoder/WriteIndented を固定。
        public static readonly JsonSerializerOptions Compact = new()
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };
        // 確認用エクスポート向けの整形(インデント付き)出力。ゲームには使わない。
        public static readonly JsonSerializerOptions Pretty = new()
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

        // バイト列を相互変換する（同じ処理を二度かければ元に戻る）。
        private static byte[] Transform(byte[] data)
        {
            var o = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) o[i] = (byte)(data[i] ^ S[i % S.Length]);
            return o;
        }
        // ファイルを読み込み、JSON オブジェクトへ変換する。
        public static JsonObject Load(string path)
            => JsonNode.Parse(Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path)))).AsObject();
        // JSON を最小化 UTF-8 にしてファイルへ書き出す（ゲームが読める形式）。
        public static void Save(string path, JsonNode root)
            => File.WriteAllBytes(path, Transform(Encoding.UTF8.GetBytes(root.ToJsonString(Compact))));
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

        // JSON編集ボタン横などに出す短い要約表示（"null" / "[配列 n件]" / "{辞書 nキー}" / スカラ値）。
        public static string Preview(JsonNode v)
            => v is null ? "null"
             : v is JsonArray a ? $"[配列 {a.Count}件]"
             : v is JsonObject ob ? $"{{辞書 {ob.Count}キー}}"
             : v.ToString();
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
            var ok = new Button { Text = "OK", Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = "キャンセル", Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                // 入力テキストを JSON としてパース。成功すれば確定、失敗すれば閉じずにエラー表示。
                try { ResultNode = JsonNode.Parse(_t.Text); DialogResult = DialogResult.OK; Close(); }
                catch (JsonException ex) { MessageBox.Show(this, "JSON エラー:\n" + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            Controls.Add(_t); Controls.Add(bar);
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

    // フォーム上の1項目とその編集ウィジェットの対応。Kind により使うウィジェットが決まる:
    //   bool→Chk / int,dbl,text→Tb / abilities→Sub(6つの能力値欄) / json→Holder / combo→Combo
    internal sealed class FieldRef { public string Name, Kind; public TextBox Tb; public CheckBox Chk; public JsonHolder Holder; public Dictionary<string, TextBox> Sub; public ComboBox Combo; public LifeLogGrid Life; }

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
        private readonly Dictionary<string, Func<string, ComboBox>> _comboFactories = new();

        public ObjectForm() { Controls.Add(_host); }

        // 表示をクリアし、バインドを解除する。
        public void Clear() { _obj = null; _host.Controls.Clear(); _fields.Clear(); }

        // 指定フィールドをプルダウン式にする。factory は現在値を受け取り ComboBox を返す。
        // 毎回の Bind 前に呼ぶか、あるいは常時登録しておくこと。
        public void RegisterComboField(string fieldName, Func<string, ComboBox> factory)
            => _comboFactories[fieldName] = factory;

        // 登録済みのすべての ComboBox ファクトリを削除する。
        public void ClearComboFields() => _comboFactories.Clear();

        // Bind 済みフィールドの ComboBox を取得する（連動プルダウンの配線に使う）。なければ null。
        public ComboBox GetCombo(string field)
            => _fields.FirstOrDefault(f => f.Kind == "combo" && f.Name == field)?.Combo;

        // 指定オブジェクトの各プロパティをフォーム化して表示する。
        // injectControl / injectAfterKey を指定すると、該当フィールドの直後にコントロールを差し込む。
        // WinForms の DockStyle.Top は後から追加したコントロールが上に来るため、逆順で追加する。
        public void Bind(JsonObject obj, Control injectControl = null, string injectAfterKey = null)
        {
            _obj = obj; _host.Controls.Clear(); _fields.Clear();
            if (obj == null) return;

            bool useInject = injectControl != null && injectAfterKey != null;

            if (!useInject)
            {
                var t = NewTable();
                int row = 0;
                foreach (var kv in obj) AddRow(t, row++, kv.Key, kv.Value);
                _host.Controls.Add(t);
                return;
            }

            // injectAfterKey の前後でテーブルを分割する。
            var t1 = NewTable(); // injectAfterKey までのフィールド
            var t2 = NewTable(); // それ以降のフィールド
            int r1 = 0, r2 = 0;
            bool past = false;

            foreach (var kv in obj)
            {
                if (!past)
                {
                    AddRow(t1, r1++, kv.Key, kv.Value);
                    if (kv.Key == injectAfterKey) past = true;
                }
                else
                {
                    AddRow(t2, r2++, kv.Key, kv.Value);
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
                    case "int":
                        if (!long.TryParse(f.Tb.Text, out long lv)) return Fail(f.Name, "整数");
                        _obj[f.Name] = lv; break;
                    case "dbl":
                        if (!double.TryParse(f.Tb.Text, out double dv)) return Fail(f.Name, "数値");
                        _obj[f.Name] = dv; break;
                    case "text": _obj[f.Name] = f.Tb.Text; break;
                    case "abilities":
                        CommitAbilities(f.Name, f.Sub);   // 0/空/不正があれば null に正規化
                        break;
                    case "json": if (f.Holder.Changed) _obj[f.Name] = f.Holder.Node; break;
                    case "lifelog": _obj[f.Name] = f.Life.ToArray(); break;
                    case "combo":
                        // "ID: 名前" 形式のテキストから ID 部分だけ取り出して保存する。
                        string raw = f.Combo.Text.Trim();
                        int col = raw.IndexOf(':');
                        _obj[f.Name] = col > 0 ? raw[..col].Trim() : raw;
                        break;
                }
            }
            return true;
        }

        // 型エラー時の共通メッセージ表示（false を返して呼び出し側で中断させる）。
        private static bool Fail(string field, string type)
        { MessageBox.Show(field + " は" + type + "で入力してください。", "型エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }

        // 2列(ラベル / 入力)の表を作る。左列は固定幅、右列は残り全部=ウィンドウ幅に追従。
        private TableLayoutPanel NewTable()
        {
            var t = new TableLayoutPanel
            { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(6) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }

        // 1プロパティ分の行を生成する。値の型に応じてウィジェットを振り分ける:
        //   能力値オブジェクト→6欄 / bool→チェック / 整数・小数→数値欄 / 文字列→(長文はリサイズ枠)
        //   文字列だけの辞書→キーごとの枠 / それ以外(配列・複雑な辞書・null)→JSON編集ボタン
        private void AddRow(TableLayoutPanel t, int row, string field, JsonNode val)
        {
            t.Controls.Add(new Label { Text = field, AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, row);
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
            // ComboBox ファクトリが登録されていれば優先して使う（文字列値のみ対象）。
            if (_comboFactories.TryGetValue(field, out var comboFactory) && val is JsonValue)
            {
                var cb = comboFactory(val.ToString());
                cb.Dock = DockStyle.Fill;
                t.Controls.Add(cb, 1, row);
                _fields.Add(new FieldRef { Name = field, Kind = "combo", Combo = cb });
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
                    var tb = new TextBox { Text = dv.ToString(), Width = 160 };
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

        // フォーカスが外れた時点で 1 項目だけ検証して即反映する。
        // 不正値は赤くして書き込まない（保存時の Apply で最終的に弾く）。
        private void CommitField(FieldRef f)
        {
            if (_obj == null) return;
            switch (f.Kind)
            {
                case "int":
                    if (long.TryParse(f.Tb.Text, out long lv)) { _obj[f.Name] = lv; Ok(f.Tb); } else Bad(f.Tb);
                    break;
                case "dbl":
                    if (double.TryParse(f.Tb.Text, out double dv)) { _obj[f.Name] = dv; Ok(f.Tb); } else Bad(f.Tb);
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
            foreach (var (key, jp) in AbilityKeys)
            {
                inner.Controls.Add(new Label { Text = $"{jp} ({key})", AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, c, r);
                double dv = J.Dbl(abil, key);
                bool isInt = abil[key] is JsonValue v && v.TryGetValue<long>(out _);   // 整数で入っているか
                var tb = new TextBox { Width = 64, Text = isInt ? ((long)dv).ToString() : dv.ToString() };
                tb.Leave += (_, _) => CommitAbilities(field, boxes);   // どれか1つ離れたら6項目まとめて確定
                inner.Controls.Add(tb, c + 1, r);
                boxes[key] = tb;
                c += 2; if (c >= 4) { c = 0; r++; }   // 2項目で次の行へ
            }
            t.Controls.Add(inner, 1, row);
            _fields.Add(new FieldRef { Name = field, Kind = "abilities", Sub = boxes });
        }

        // 能力値6項目を一括で確定する。
        // 全項目が正の数のときだけオブジェクトを書き込み、1つでも 0/空/不正があれば null（未生成扱い）。
        private void CommitAbilities(string field, Dictionary<string, TextBox> boxes)
        {
            if (_obj == null) return;
            var result = new JsonObject();
            bool allOk = true;
            foreach (var kv in boxes)
            {
                string txt = kv.Value.Text.Trim();
                bool ok;
                if (txt.Length == 0) ok = false;
                else if (txt.Contains('.')) { ok = double.TryParse(txt, out double dv) && dv > 0; if (ok) result[kv.Key] = dv; }
                else { ok = long.TryParse(txt, out long lv) && lv > 0; if (ok) result[kv.Key] = lv; }
                if (ok) Ok(kv.Value); else Bad(kv.Value);
                if (!ok) allOk = false;
            }
            _obj[field] = allOk ? (JsonNode)result : null;   // 0/空/不正があれば ability_scores は null
        }

        // 配列・複雑な辞書・null など、フォーム化しにくい値は「JSON編集...」ボタンで開く。
        // ダイアログ OK 時にその場でモデルへ反映する（保存を待たない）。
        private void AddJsonRow(TableLayoutPanel t, int row, string field, JsonNode val)
        {
            var holder = new JsonHolder { Node = val };
            var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var lbl = new Label { Text = J.Preview(val), AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 8, 0) };
            holder.Preview = lbl;
            var btn = new Button { Text = "JSON編集...", Width = 100 };
            string captured = field;
            btn.Click += (_, _) =>
            {
                using var d = new JsonEditDialog(captured + " を編集", holder.Node);
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

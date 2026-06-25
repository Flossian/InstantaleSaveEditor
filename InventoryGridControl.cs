// インベントリのグリッド式編集コントロール。
// ゲーム同様の格子にアイテムを footprint（width_slots×height_slots）で配置し、
// ドラッグ＆ドロップで移動／入れ替え、ダブルクリックで既存の ObjectForm 編集を開く。
// v1 スコープ: 移動・入れ替え・ダブルクリック編集のみ（回転・追加/削除はコントロール外で扱う）。
// モデル（inventory 辞書）は in-memory の JsonObject を直接参照し、移動時は grid_pos のみ書き換える。
// キー順・他フィールドは一切変更しないため、保存の round-trip を壊さない。
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class InventoryGridControl : Panel
    {
        // 1セルの一辺(px)。コントロール全体は Columns*CellSize × Rows*CellSize になる。
        private const int CellSize = 56;
        private const int Pad = 3;                 // アイテム描画時のセル内側パディング

        private JsonObject _inv;                   // inventory 辞書（モデル本体。直接書き換える）
        private int _cols = 4, _rows = 6;          // 設定から取り込むグリッド寸法
        private string _assetRoot = "";            // 設定の GameAssetRoot（image_src の解決基準）

        // 画像キャッシュ（image_src → Image。解決不能は null を格納して再試行を避ける）。
        private readonly Dictionary<string, Image> _imgCache = new(StringComparer.Ordinal);

        // セル占有マップ。_occ[col,row] = そのセルを占有するアイテムID（無ければ null）。
        private string[,] _occ;

        // 状態
        private string _selectedId;                // 単一選択中のアイテム（追加/削除/編集ボタンの対象）
        private string _hoverId;                   // ツールチップ表示中のアイテム
        private string _dragId;                    // ドラッグ中のアイテム（null=非ドラッグ）
        private Point _dragOffset;                 // 掴んだ位置のアイテム左上からのオフセット(px)
        private Point _mousePos;                   // 現在のマウス座標（クライアント）
        private Point _pressPos;                   // マウスダウン位置（ドラッグ判定の起点）
        private string _pressId;                   // マウスダウン時に掴んだアイテム
        private bool _dragging;                    // しきい値を越えてドラッグ開始したか

        private InvTooltip _tip;                   // 自前ツールチップ

        // ダブルクリックでアイテム編集を要求する（引数=アイテムID）。PlayerTab が ObjectForm を開く。
        public event Action<string> ItemActivated;
        // 選択が変わったとき（ボタンの有効/無効更新などに使う）。
        public event Action SelectionChanged;

        public InventoryGridControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = SystemColors.ControlDarkDark;
        }

        // 現在選択中のアイテムID（無ければ null）。
        public string SelectedId => _inv != null && _selectedId != null && _inv.ContainsKey(_selectedId) ? _selectedId : null;

        // inventory 辞書をバインドして再構築・再描画する。設定（寸法・アセット先）もここで取り込む。
        public void Bind(JsonObject inventory)
        {
            _inv = inventory;
            var s = Settings.Current;
            _cols = Math.Max(1, s.InventoryGridColumns);
            _rows = Math.Max(1, s.InventoryGridRows);
            _assetRoot = s.GameAssetRoot ?? "";

            // 選択が消えていればクリア。
            if (_selectedId != null && (_inv == null || !_inv.ContainsKey(_selectedId))) _selectedId = null;

            RebuildOccupancy();
            Size = new Size(_cols * CellSize + 1, _rows * CellSize + 1);
            Invalidate();
        }

        // データから占有マップを作り直す（クランプ後の範囲で埋める。重複は後勝ち＝描画は別途全件行う）。
        private void RebuildOccupancy()
        {
            _occ = new string[_cols, _rows];
            if (_inv == null) return;
            foreach (var kv in _inv)
            {
                if (kv.Value is not JsonObject) continue;
                var (x, y, w, h) = CellRect(kv.Key);
                for (int cx = x; cx < x + w && cx < _cols; cx++)
                    for (int cy = y; cy < y + h && cy < _rows; cy++)
                        if (cx >= 0 && cy >= 0) _occ[cx, cy] = kv.Key;
            }
        }

        // アイテムの占有セル範囲（グリッド内にクランプ済み）。x=列,y=行,w=幅,h=高さ（いずれも最低1）。
        private (int x, int y, int w, int h) CellRect(string id)
        {
            var o = _inv?[id] as JsonObject;
            int w = Math.Max(1, (int)J.Int(o, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(o, "height_slots", 1));
            var gp = J.Arr(o, "grid_pos");
            int x = 0, y = 0;
            if (gp != null && gp.Count >= 2)
            {
                x = (int)ParseInt(gp[0]);
                y = (int)ParseInt(gp[1]);
            }
            // 表示が破綻しないようグリッド内にクランプする（不整合データ対策）。
            w = Math.Min(w, _cols);
            h = Math.Min(h, _rows);
            x = Math.Clamp(x, 0, Math.Max(0, _cols - w));
            y = Math.Clamp(y, 0, Math.Max(0, _rows - h));
            // ゲームは grid_pos の y を下端起点とするため、画面（上端起点）では上下反転して扱う。
            // 以降の描画・占有・ドロップ判定はすべてこの画面座標で統一する。
            int screenY = _rows - h - y;
            return (x, screenY, w, h);
        }

        // grid_pos 要素を long として安全に読む。
        // JsonNode.Parse 由来は JsonElement 格納で long を取れるが、こちらが書き戻した int 格納値は
        // TryGetValue<long> が失敗するため、int/double/decimal/文字列も順に試す（これを怠ると 0 になる）。
        private static long ParseInt(JsonNode n)
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue<long>(out long l)) return l;
                if (v.TryGetValue<int>(out int i)) return i;
                if (v.TryGetValue<double>(out double d)) return (long)d;
                if (v.TryGetValue<decimal>(out decimal m)) return (long)m;
                if (v.TryGetValue<string>(out string s) && long.TryParse(s, out long ls)) return ls;
            }
            return 0;
        }

        // アイテムの「元の」サイズ（クランプ前。ドロップ判定の境界計算に使う）。最低1。
        private (int w, int h) RawSize(string id)
        {
            var o = _inv?[id] as JsonObject;
            return (Math.Max(1, (int)J.Int(o, "width_slots", 1)), Math.Max(1, (int)J.Int(o, "height_slots", 1)));
        }

        // ---------------- 描画 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(BackColor);
            if (_inv == null) return;

            // グリッド線。
            using (var pen = new Pen(Color.FromArgb(90, 90, 90)))
            {
                for (int c = 0; c <= _cols; c++) g.DrawLine(pen, c * CellSize, 0, c * CellSize, _rows * CellSize);
                for (int r = 0; r <= _rows; r++) g.DrawLine(pen, 0, r * CellSize, _cols * CellSize, r * CellSize);
            }

            // 各アイテム（ドラッグ中の本体はゴーストで描くのでここでは省く）。
            foreach (var kv in _inv)
            {
                if (kv.Value is not JsonObject) continue;
                if (_dragging && kv.Key == _dragId) continue;
                DrawItem(g, kv.Key, false);
            }

            // 選択枠。
            if (SelectedId != null && !(_dragging && SelectedId == _dragId))
            {
                var (x, y, w, h) = CellRect(SelectedId);
                using var pen = new Pen(Color.DeepSkyBlue, 2);
                g.DrawRectangle(pen, x * CellSize + 1, y * CellSize + 1, w * CellSize - 2, h * CellSize - 2);
            }

            // ドラッグ中: ドロップ先のハイライト＋ゴースト。
            if (_dragging && _dragId != null)
            {
                var (tx, ty, valid) = DropTarget();
                var (rw, rh) = RawSize(_dragId);
                int dw = Math.Min(rw, _cols), dh = Math.Min(rh, _rows);
                using (var br = new SolidBrush(Color.FromArgb(90, valid ? Color.LimeGreen : Color.Red)))
                    g.FillRectangle(br, tx * CellSize, ty * CellSize, dw * CellSize, dh * CellSize);

                // ゴースト（半透明）をカーソル追従で描く。
                var img = ResolveImage(_dragId);
                int gx = _mousePos.X - _dragOffset.X, gy = _mousePos.Y - _dragOffset.Y;
                var gr = new Rectangle(gx + Pad, gy + Pad, dw * CellSize - Pad * 2, dh * CellSize - Pad * 2);
                if (img != null) DrawImageFit(g, img, gr, 0.7f);
                else DrawPlaceholder(g, _dragId, gr, 0.7f);
            }
        }

        // 1アイテムを footprint 矩形に描く。
        private void DrawItem(Graphics g, string id, bool ghost)
        {
            var (x, y, w, h) = CellRect(id);
            var rect = new Rectangle(x * CellSize + Pad, y * CellSize + Pad, w * CellSize - Pad * 2, h * CellSize - Pad * 2);

            // rarity に応じた枠色（軽量なので常時描く）。
            var border = RarityColor(J.Str(_inv[id] as JsonObject, "rarity"));
            using (var bg = new SolidBrush(Color.FromArgb(40, 40, 40)))
                g.FillRectangle(bg, rect);

            var img = ResolveImage(id);
            if (img != null) DrawImageFit(g, img, rect, 1f);
            else DrawPlaceholder(g, id, rect, 1f);

            using var pen = new Pen(border, 2);
            g.DrawRectangle(pen, rect);
        }

        // 画像をアスペクト維持で矩形内に収めて描く（alpha<1 で半透明）。
        private static void DrawImageFit(Graphics g, Image img, Rectangle rect, float alpha)
        {
            double scale = Math.Min((double)rect.Width / img.Width, (double)rect.Height / img.Height);
            int dw = Math.Max(1, (int)(img.Width * scale)), dh = Math.Max(1, (int)(img.Height * scale));
            var dst = new Rectangle(rect.X + (rect.Width - dw) / 2, rect.Y + (rect.Height - dh) / 2, dw, dh);
            if (alpha >= 1f)
            {
                g.DrawImage(img, dst);
                return;
            }
            var cm = new ColorMatrix { Matrix33 = alpha };
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(cm);
            g.DrawImage(img, dst, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
        }

        // 画像が解決できないときのプレースホルダ（名前の頭文字）。
        private void DrawPlaceholder(Graphics g, string id, Rectangle rect, float alpha)
        {
            string name = J.Str(_inv[id] as JsonObject, "name", "?");
            string head = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1);
            int a = (int)(alpha * 255);
            using var br = new SolidBrush(Color.FromArgb(a, Color.Gainsboro));
            using var f = new Font(Font.FontFamily, Math.Max(8, rect.Height / 3), FontStyle.Bold);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(head, f, br, rect, sf);
        }

        // rarity → 枠色。未知は灰色。
        private static Color RarityColor(string rarity) => rarity switch
        {
            "mythic" => Color.OrangeRed,
            "legendary" => Color.Gold,
            "epic" => Color.MediumOrchid,
            "rare" => Color.DodgerBlue,
            "magical" => Color.MediumSeaGreen,   // magical は緑系
            _ => Color.Gray,
        };

        // ---------------- 画像解決・キャッシュ ----------------
        // image_src を GameAssetRoot 基準で解決して読み込む。File.ReadAllBytes→MemoryStream 経由で
        // 元PNGをロックしない。解決不能・未設定・例外時は null（プレースホルダ表示）。
        private Image ResolveImage(string id)
        {
            string src = J.Str(_inv[id] as JsonObject, "image_src");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(_assetRoot)) return null;
            if (_imgCache.TryGetValue(src, out var cached)) return cached;

            Image img = null;
            try
            {
                string rel = src.Replace('/', Path.DirectorySeparatorChar);
                string full = Path.Combine(_assetRoot, rel);
                if (File.Exists(full))
                {
                    byte[] bytes = File.ReadAllBytes(full);
                    using var ms = new MemoryStream(bytes);
                    using var tmp = Image.FromStream(ms);
                    img = new Bitmap(tmp);   // ストリーム/ファイルから切り離した複製を保持
                }
            }
            catch { img = null; }   // 壊れた画像でも描画継続
            _imgCache[src] = img;   // null も格納し再試行を避ける
            return img;
        }

        // ---------------- ヒットテスト ----------------
        // クライアント座標→セル。範囲外は (-1,-1)。
        private Point CellAt(Point p)
        {
            int c = p.X / CellSize, r = p.Y / CellSize;
            if (c < 0 || c >= _cols || r < 0 || r >= _rows) return new Point(-1, -1);
            return new Point(c, r);
        }

        // クライアント座標→アイテムID（無ければ null）。
        private string ItemAt(Point p)
        {
            var cell = CellAt(p);
            if (cell.X < 0) return null;
            return _occ[cell.X, cell.Y];
        }

        // ---------------- マウス操作 ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            HideTip();
            _pressPos = e.Location;
            _pressId = ItemAt(e.Location);
            // クリックで選択を更新。
            string newSel = _pressId;
            if (newSel != _selectedId) { _selectedId = newSel; SelectionChanged?.Invoke(); }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mousePos = e.Location;

            // ドラッグ開始判定（しきい値4px）。
            if (!_dragging && e.Button == MouseButtons.Left && _pressId != null)
            {
                if (Math.Abs(e.X - _pressPos.X) > 4 || Math.Abs(e.Y - _pressPos.Y) > 4)
                {
                    _dragId = _pressId;
                    var (x, y, _, _) = CellRect(_dragId);
                    _dragOffset = new Point(_pressPos.X - x * CellSize, _pressPos.Y - y * CellSize);
                    _dragging = true;
                    HideTip();
                }
            }

            if (_dragging) { Invalidate(); return; }

            // ホバー中のツールチップ更新。
            string id = ItemAt(e.Location);
            if (id != _hoverId)
            {
                _hoverId = id;
                if (id != null) ShowTip(id, e.Location);
                else HideTip();
            }
            else if (id != null)
            {
                MoveTip(e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragging)
            {
                try { PerformDrop(); }
                catch { /* 何があってもクラッシュさせない（スナップバック扱い） */ }
                _dragging = false; _dragId = null;
                RebuildOccupancy();
                Invalidate();
            }
            _pressId = null;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            HideTip();
            string id = ItemAt(e.Location);
            if (id != null) ItemActivated?.Invoke(id);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverId = null;
            HideTip();
        }

        // 現在のドロップ先（ドラッグ中アイテム左上が来るセル）と可否。
        private (int x, int y, bool valid) DropTarget()
        {
            var (rw, rh) = RawSize(_dragId);
            int w = Math.Min(rw, _cols), h = Math.Min(rh, _rows);
            int tx = (int)Math.Round((double)(_mousePos.X - _dragOffset.X) / CellSize);
            int ty = (int)Math.Round((double)(_mousePos.Y - _dragOffset.Y) / CellSize);
            tx = Math.Clamp(tx, 0, Math.Max(0, _cols - w));
            ty = Math.Clamp(ty, 0, Math.Max(0, _rows - h));
            return (tx, ty, DropValid(tx, ty));
        }

        // (tx,ty) へのドロップが成立するか（移動=空き、または入れ替え可能）を判定する。ハイライト色に使う。
        private bool DropValid(int tx, int ty)
        {
            var (xa, ya, _, _) = CellRect(_dragId);
            var occupy = OverlapAt(_dragId, tx, ty);
            if (occupy.Count == 0) return true;                                  // 空き → 移動可
            if (occupy.Count == 1) return CanPlaceExcluding(occupy[0], xa, ya, _dragId);  // 1個 → 入れ替え可否
            return false;                                                        // 2個以上 → 不可
        }

        // ドロップ確定処理（移動 or 入れ替え or スナップバック）。
        private void PerformDrop()
        {
            if (_dragId == null || _inv?[_dragId] is not JsonObject a) return;
            var (xa, ya, _, _) = CellRect(_dragId);
            var (rw, rh) = RawSize(_dragId);
            int w = Math.Min(rw, _cols), h = Math.Min(rh, _rows);

            int tx = (int)Math.Round((double)(_mousePos.X - _dragOffset.X) / CellSize);
            int ty = (int)Math.Round((double)(_mousePos.Y - _dragOffset.Y) / CellSize);
            tx = Math.Clamp(tx, 0, Math.Max(0, _cols - w));
            ty = Math.Clamp(ty, 0, Math.Max(0, _rows - h));
            if (tx == xa && ty == ya) return;   // 同じ位置=何もしない

            // ドロップ先 footprint に重なる A 以外のアイテム集合。
            var occupy = OverlapAt(_dragId, tx, ty);
            if (occupy.Count == 0)
            {
                SetPos(_dragId, tx, ty);   // 空き → 移動
            }
            else if (occupy.Count == 1)
            {
                // 入れ替え試行: B を A の元位置に置けるか（A を除いた占有で検証）。
                string bId = occupy[0];
                if (_inv[bId] is not JsonObject) return;
                if (CanPlaceExcluding(bId, xa, ya, _dragId))
                {
                    SetPos(_dragId, tx, ty);
                    SetPos(bId, xa, ya);
                }
                // 不可ならスナップバック（何もしない）。
            }
            // 2個以上 → スナップバック（何もしない）。
        }

        // (tx,ty) に id を置いたとき重なる「id 以外」のアイテム一覧（占有マップ参照）。
        private List<string> OverlapAt(string id, int tx, int ty)
        {
            var (rw, rh) = RawSize(id);
            int w = Math.Min(rw, _cols), h = Math.Min(rh, _rows);
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int cx = tx; cx < tx + w && cx < _cols; cx++)
                for (int cy = ty; cy < ty + h && cy < _rows; cy++)
                {
                    string occ = _occ[cx, cy];
                    if (occ != null && occ != id) set.Add(occ);
                }
            return set.ToList();
        }

        // id を (tx,ty) に置けるか（境界内＋except を除く他アイテムと非重複）を判定する。
        private bool CanPlaceExcluding(string id, int tx, int ty, string except)
        {
            var (rw, rh) = RawSize(id);
            int w = Math.Min(rw, _cols), h = Math.Min(rh, _rows);
            if (tx < 0 || ty < 0 || tx + w > _cols || ty + h > _rows) return false;
            for (int cx = tx; cx < tx + w; cx++)
                for (int cy = ty; cy < ty + h; cy++)
                {
                    string occ = _occ[cx, cy];
                    if (occ != null && occ != id && occ != except) return false;
                }
            return true;
        }

        // 画面座標(上端起点 sx,sy)を受け取り、ゲームの下端起点 y に変換して grid_pos を書き換える。
        // 他フィールド・キー順は一切触らない（round-trip 維持）。
        private void SetPos(string id, int sx, int sy)
        {
            if (_inv?[id] is not JsonObject o) return;
            var (_, rh) = RawSize(id);
            int h = Math.Min(rh, _rows);
            int modelY = Math.Max(0, _rows - h - sy);
            o["grid_pos"] = new JsonArray(sx, modelY);
        }

        // ---------------- ツールチップ ----------------
        private void ShowTip(string id, Point at)
        {
            if (_inv?[id] is not JsonObject o) return;
            _tip ??= new InvTooltip();
            _tip.SetContent(TipTitle(o), TipBody(o));
            MoveTip(at);
            if (!_tip.Visible) _tip.Show(this);
        }

        private void MoveTip(Point at)
        {
            if (_tip == null) return;
            var screen = PointToScreen(new Point(at.X + 18, at.Y + 18));
            _tip.Location = screen;
        }

        private void HideTip() { if (_tip != null && _tip.Visible) _tip.Hide(); }

        // ツールチップ見出し（アイテム名）。
        private static string TipTitle(JsonObject o) => J.Str(o, "name", I18n.T("label.unnamed"));

        // ツールチップ本文（種別・rarity・主要属性・説明）。
        private static string TipBody(JsonObject o)
        {
            var sb = new StringBuilder();
            string type = J.Str(o, "item_type");
            string detail = J.Str(J.Obj(o, "attributes"), "item_detail");
            string typeLine = string.Join(" / ", new[] { type, detail }.Where(s => !string.IsNullOrEmpty(s)));
            if (typeLine.Length > 0) sb.AppendLine(typeLine);
            string rarity = J.Str(o, "rarity");
            if (!string.IsNullOrEmpty(rarity)) sb.AppendLine("rarity: " + rarity);

            // 主要属性（攻撃力・防御力・売価があれば）。
            var attrs = J.Obj(o, "attributes");
            if (attrs != null)
                foreach (var key in new[] { "攻撃力", "防御力", "売価" })
                    if (attrs.ContainsKey(key)) sb.AppendLine($"{key}: {attrs[key]}");

            string desc = J.Str(o, "description");
            if (!string.IsNullOrEmpty(desc)) { sb.AppendLine(); sb.Append(desc); }
            return sb.ToString().TrimEnd();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var img in _imgCache.Values) img?.Dispose();
                _imgCache.Clear();
                _tip?.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------- 自前ツールチップ（小型ボーダレス Form・オーナードロー） ----------------
        // 名前を太字、本文を通常。説明は折り返し。フォーカスを奪わずに表示する。
        private sealed class InvTooltip : Form
        {
            private string _title = "", _body = "";
            private const int MaxWidth = 280;

            public InvTooltip()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                TopMost = true;
                BackColor = Color.FromArgb(250, 250, 230);
            }

            // 表示してもアクティブにしない（編集中フォーカスを奪わない）。
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                    return cp;
                }
            }

            // 内容を設定し、サイズを実測して決める。
            public void SetContent(string title, string body)
            {
                _title = title ?? "";
                _body = body ?? "";
                using var g = CreateGraphics();
                using var fB = new Font(Font, FontStyle.Bold);
                var tf = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                var ts = TextRenderer.MeasureText(g, _title, fB, new Size(MaxWidth, 0), tf);
                var bs = _body.Length > 0
                    ? TextRenderer.MeasureText(g, _body, Font, new Size(MaxWidth, 0), tf)
                    : Size.Empty;
                int w = Math.Min(MaxWidth, Math.Max(ts.Width, bs.Width)) + 16;
                int h = ts.Height + (bs.Height > 0 ? bs.Height + 6 : 0) + 12;
                Size = new Size(w, h);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                var tf = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
                using var fB = new Font(Font, FontStyle.Bold);
                var rect = new Rectangle(8, 6, Width - 16, Height - 12);
                var ts = TextRenderer.MeasureText(g, _title, fB, new Size(rect.Width, 0), tf);
                TextRenderer.DrawText(g, _title, fB, new Rectangle(rect.X, rect.Y, rect.Width, ts.Height), Color.Black, tf);
                if (_body.Length > 0)
                {
                    var br = new Rectangle(rect.X, rect.Y + ts.Height + 6, rect.Width, rect.Height - ts.Height - 6);
                    TextRenderer.DrawText(g, _body, Font, br, Color.FromArgb(40, 40, 40), tf);
                }
                using var pen = new Pen(Color.FromArgb(120, 120, 100));
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    // インベントリの「グリッド＋追加/編集/削除ボタン」をまとめた再利用パネル。
    // プレイヤー(PlayerTab)と NPC(WorldTab の ObjectForm 経由) の双方から使う。
    // inventory 辞書を直接編集し、追加/削除/編集のたびに InventoryChanged を発火する。
    internal sealed class InventoryPanel : Panel
    {
        private readonly InventoryGridControl _grid = new() { Location = new Point(0, 0) };
        private readonly Button _btnEdit, _btnDelete;
        private JsonObject _inv;

        // 追加/削除/編集でインベントリが変化したときに発火（装備コンボの再構築などに使う）。
        public event Action InventoryChanged;

        public InventoryPanel()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

            // グリッドはスクロールホストに載せる（行数次第で背が高くなるため）。
            var gridHost = new Panel { Dock = DockStyle.Top, Height = 360, AutoScroll = true };
            gridHost.Controls.Add(_grid);
            _grid.ItemActivated += id => EditItem(id);
            _grid.SelectionChanged += () => { if (_btnDelete != null) _btnDelete.Enabled = _grid.SelectedId != null; };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            Button Mk(string text, Action act)
            {
                var b = new Button { Text = text, Width = 70, Margin = new Padding(2) };
                b.Click += (_, _) => act();
                bar.Controls.Add(b);
                return b;
            }
            Mk(I18n.T("btn.add"), AddItem);
            _btnEdit = Mk(I18n.T("btn.edit"), () => { if (_grid.SelectedId != null) EditItem(_grid.SelectedId); });
            _btnDelete = Mk(I18n.T("btn.delete"), DeleteItem);
            _btnDelete.Enabled = false;

            // Dock=Top は後入れが上。ボタン列を上、グリッドを下に置く。
            Controls.Add(gridHost);
            Controls.Add(bar);
        }

        // inventory 辞書をバインドして表示を作り直す。
        public void Bind(JsonObject inventory)
        {
            _inv = inventory;
            _grid.Bind(inventory);
            if (_btnDelete != null) _btnDelete.Enabled = _grid.SelectedId != null;
        }

        // グリッドを作り直し、削除ボタンの有効状態を更新し、変更を通知する。
        private void Reload()
        {
            _grid.Bind(_inv);
            if (_btnDelete != null) _btnDelete.Enabled = _grid.SelectedId != null;
            InventoryChanged?.Invoke();
        }

        private void AddItem()
        {
            if (_inv == null) return;
            _inv[NextItemId(_inv)] = NewItemTemplate();
            Reload();
        }

        private void EditItem(string id)
        {
            if (_inv == null || id == null || _inv[id] is not JsonObject item) return;
            using var dlg = new ItemEditDialog(id, item);
            dlg.ShowDialog(FindForm());
            Reload();
        }

        private void DeleteItem()
        {
            string id = _grid.SelectedId;
            if (_inv == null || id == null) return;
            if (MessageBox.Show(I18n.T("msg.confirmDeleteItem", id), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _inv.Remove(id);
            Reload();
        }

        // inventory に追加する新ID（item_N の N=既存最大+1）。
        public static string NextItemId(JsonObject inv)
        {
            int max = -1;
            foreach (var kv in inv)
            {
                var s = kv.Key.StartsWith("item_") ? kv.Key.Substring(5) : kv.Key;
                if (int.TryParse(s, out int n) && n > max) max = n;
            }
            return "item_" + (max + 1);
        }

        // 新規アイテムの最小テンプレ（実アイテムと同じキー順。1×1・画像なしで開始）。
        public static JsonObject NewItemTemplate() => new()
        {
            ["name"] = I18n.T("player.newItem"),
            ["item_type"] = "material",
            ["attributes"] = new JsonObject(),
            ["description"] = "",
            ["value"] = 0L,
            ["rarity"] = "common",
            ["width_slots"] = 1L,
            ["height_slots"] = 1L,
            ["image_src"] = "",
        };
    }

    // インベントリ1アイテムを既存の ObjectForm で編集するダイアログ。
    // 編集は ObjectForm がフォーカスアウトで即モデルへ反映し、OK で Apply() により最終確定する。
    // item は inventory 辞書内の JsonObject 参照そのものなので、確定内容はそのまま辞書へ残る。
    internal sealed class ItemEditDialog : Form
    {
        private readonly ObjectForm _form = new();

        // rarity のプルダウン候補（低→高）。データ上の値は "mythic"。
        private static readonly string[] Rarities =
        { "common", "magical", "rare", "epic", "legendary", "mythic" };

        public ItemEditDialog(string id, JsonObject item)
        {
            Text = I18n.T("title.editField", id);
            Width = 560; Height = 600;
            StartPosition = FormStartPosition.CenterParent;

            // item_type を候補一覧（field_options.json の "item_type": weapon/wearable/… ）から選べるプルダウンにする。
            // attributes.item_detail とは別系統の上位種別。一覧外の既存値も失わないよう編集可能(DropDown)とする。
            _form.RegisterComboField("item_type", cur =>
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
                cb.Items.AddRange(FieldOptions.Get("item_type").ToArray());
                cb.Text = cur;
                return cb;
            });

            // rarity も候補一覧から選べるプルダウンにする（一覧外の既存値も保持するため編集可能）。
            _form.RegisterComboField("rarity", cur =>
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
                cb.Items.AddRange(Rarities);
                cb.Text = cur;
                return cb;
            });

            // attributes.item_detail の候補は現在の item_type 配下（field_options.json の "item_detail.<type>"）から供給する。
            _form.AttributeDetailOptions = type => FieldOptions.Get("item_detail." + (type ?? ""));

            // item_type ごとの attributes キー一覧（item_detail を除く・表示順）を供給する。
            // 例: weapon → ["攻撃力","売価"]。field_options.json の "attributes.<type>" で外部編集可能。
            _form.AttributeStatKeys = type => FieldOptions.Get("attributes." + (type ?? ""));

            _form.Dock = DockStyle.Fill;
            _form.Bind(item);

            // item_type を変えたら attributes を新しい種別のスキーマへ作り替え、item_detail 候補も連動させる。
            // combo の値は通常 Apply() でしか item へ書き戻らないため、ここで即 item_type へ反映する。
            // ・入力途中（TextChanged）は item_detail 候補だけ追従（非破壊）。
            // ・種別が確定（一覧選択 / フォーカスアウト）かつ実際に変化したときだけ attributes を作り替える。
            //   入力途中に attributes を消さないため、確定タイミングに限定する。
            var typeCombo = _form.GetCombo("item_type");
            if (typeCombo != null)
            {
                string lastType = J.Str(item, "item_type");
                void ApplySchema()
                {
                    string t = typeCombo.Text.Trim();
                    item["item_type"] = t;
                    if (t == lastType) return;
                    lastType = t;
                    _form.ApplyAttributeSchema();
                }
                typeCombo.TextChanged += (_, _) => { item["item_type"] = typeCombo.Text.Trim(); _form.RefreshAttributeDetailOptions(); };
                typeCombo.SelectedIndexChanged += (_, _) => ApplySchema();
                typeCombo.Leave += (_, _) => ApplySchema();
            }

            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => { if (!_form.Apply()) DialogResult = DialogResult.None; };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);

            Controls.Add(_form);
            Controls.Add(bar);
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}

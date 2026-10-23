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
        private int CellSize => CellSizeOverride ?? 56;
        private const int Pad = 3;                 // アイテム描画時のセル内側パディング

        private JsonObject _inv;                   // inventory 辞書（モデル本体。直接書き換える）
        private int _cols = 4, _rows = 6;          // 設定から取り込むグリッド寸法
        private string _assetKey = "";             // 画像の探し先（ゲーム導入先＋ModLoader）の組。変わったらキャッシュを捨てる

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
        // Ctrl+クリックでアイテムが選ばれたとき（引数=ID）。倉庫⇄インベントリの移動に使う。
        public event Action<string> ItemCtrlClicked;
        // ドラッグ中のアイテムをコントロール外で離したとき（引数=ID, スクリーン座標）。他グリッドへの移動に使う。
        public event Action<string, Point> ItemDraggedOut;

        // 設定の寸法ではなく明示寸法でグリッドを構成する場合に指定する（倉庫グリッド用）。null なら設定値。
        public Size? GridSizeOverride { get; set; }
        // セルの一辺(px)を変える（装備欄は小さく描く）。null なら既定の 56。
        public int? CellSizeOverride { get; set; }
        // 表示する品の絞り込み（false の品は描かず、マスも占めない）。null なら全件。
        // 装備欄 MOD の装備中の品を所持品のグリッドから外し、装備欄のグリッドにだけ出すのに使う。
        public Func<string, bool> ItemFilter { get; set; }
        // 品の位置（左上のマス。y は上から）を grid_pos の代わりに使う。null を返す品は grid_pos のまま。
        public Func<string, Point?> PositionOverride { get; set; }
        // ドラッグで動かせないようにする（選択・ツールチップ・ダブルクリック編集はできる）。
        public bool ReadOnlyLayout { get; set; }
        // ReadOnlyLayout でも、グリッドの外へドラッグして出すこと（ItemDraggedOut）だけは許す。中で離すと元へ戻る。
        public bool AllowDragOut { get; set; }
        // グリッドの上に描く枠（名前・マスの矩形。y は上から）。枠の外のマスは暗く塗る。
        // 枠の中の品は、縦横比を保った整数倍で枠いっぱいに広げて描く（装備欄 MOD の SCALE_TO_FIT と同じ）。
        public IReadOnlyList<(string label, Rectangle cells)> Regions { get; set; }

        // 絞り込みを通る品の鍵。
        private IEnumerable<string> VisibleIds()
        {
            if (_inv == null) yield break;
            foreach (var kv in _inv)
                if (kv.Value is JsonObject && (ItemFilter == null || ItemFilter(kv.Key))) yield return kv.Key;
        }

        public InventoryGridControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = SystemColors.ControlDarkDark;
        }

        // 現在選択中のアイテムID（無ければ null）。
        public string SelectedId => _inv != null && _selectedId != null && _inv.ContainsKey(_selectedId)
            && (ItemFilter == null || ItemFilter(_selectedId)) ? _selectedId : null;

        // 画像が表示できない状態か（アセット先未設定、または image_src があるのに解決できない）。
        // InventoryPanel が警告ラベルの表示可否に使う。Bind のたびに更新する。
        public bool ImagesUnavailable { get; private set; }

        // inventory 辞書をバインドして再構築・再描画する。設定（寸法・アセット先）もここで取り込む。
        public void Bind(JsonObject inventory)
        {
            _inv = inventory;
            var s = Settings.Current;
            _cols = Math.Max(1, s.InventoryGridColumns);
            _rows = Math.Max(1, s.InventoryGridRows);
            if (GridSizeOverride is Size gs) { _cols = Math.Max(1, gs.Width); _rows = Math.Max(1, gs.Height); }

            // 画像キャッシュの整理。アセット先が変わったら全部捨てる（旧ルート基準の画像を残さない）。
            // 同じルートなら解決失敗(null)だけ捨てて再試行させる（インポートや倉庫取出で
            // 画像が後から復元された場合に、プレースホルダ表示と警告が残らないように）。
            string newRoot = string.Join("|", ModLoaderLink.AssetRoots());
            if (!string.Equals(newRoot, _assetKey, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var img in _imgCache.Values) img?.Dispose();
                _imgCache.Clear();
            }
            else
            {
                foreach (var k in _imgCache.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
                    _imgCache.Remove(k);
            }
            _assetKey = newRoot;

            // 選択が消えていればクリア。
            if (_selectedId != null && (_inv == null || !_inv.ContainsKey(_selectedId))) _selectedId = null;

            RebuildOccupancy();
            ComputeImagesUnavailable();
            Size = new Size(_cols * CellSize + 1, _rows * CellSize + 1);
            Invalidate();
        }

        // 画像表示の可否を判定する。image_src を持つアイテムがあるのに解決できないものが
        // 1つでもあれば true（アセット先未設定・ファイル欠落の双方を含む）。
        private void ComputeImagesUnavailable()
        {
            ImagesUnavailable = false;
            foreach (var id in VisibleIds())
            {
                if (string.IsNullOrEmpty(J.Str(_inv[id] as JsonObject, "image_src"))) continue;
                if (ResolveImage(id) == null) { ImagesUnavailable = true; return; }
            }
        }

        // データから占有マップを作り直す（クランプ後の範囲で埋める。重複は後勝ち＝描画は別途全件行う）。
        private void RebuildOccupancy()
        {
            _occ = new string[_cols, _rows];
            foreach (var id in VisibleIds())
            {
                var (x, y, w, h) = CellRect(id);
                for (int cx = x; cx < x + w && cx < _cols; cx++)
                    for (int cy = y; cy < y + h && cy < _rows; cy++)
                        if (cx >= 0 && cy >= 0) _occ[cx, cy] = id;
            }
        }

        // アイテムの占有セル範囲（グリッド内にクランプ済み）。x=列,y=行,w=幅,h=高さ（いずれも最低1）。
        private (int x, int y, int w, int h) CellRect(string id)
        {
            var o = _inv?[id] as JsonObject;
            int w = Math.Max(1, (int)J.Int(o, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(o, "height_slots", 1));
            if (PositionOverride?.Invoke(id) is Point at)
            {
                // 上端起点の位置。枠の中なら枠いっぱいへ整数倍で広げる（縦横比は保つ）。
                var reg = Regions?.FirstOrDefault(r => r.cells.Contains(at)).cells ?? Rectangle.Empty;
                if (!reg.IsEmpty)
                {
                    int f = Math.Max(1, Math.Min((reg.Right - at.X) / w, (reg.Bottom - at.Y) / h));
                    w *= f; h *= f;
                }
                w = Math.Min(w, _cols); h = Math.Min(h, _rows);
                return (Math.Clamp(at.X, 0, Math.Max(0, _cols - w)), Math.Clamp(at.Y, 0, Math.Max(0, _rows - h)), w, h);
            }
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
                if (v.TryGetValue<string>(out string s) && long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long ls)) return ls;
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

            // 枠（装備欄の部位）。枠の外のマスは暗く塗り、枠線と名前を描く。
            if (Regions != null)
            {
                using (var dim = new SolidBrush(Color.FromArgb(30, 30, 30)))
                    for (int c = 0; c < _cols; c++)
                        for (int r = 0; r < _rows; r++)
                            if (!Regions.Any(x => x.cells.Contains(c, r)))
                                g.FillRectangle(dim, c * CellSize + 1, r * CellSize + 1, CellSize - 1, CellSize - 1);
                using var pen = new Pen(Color.Silver, 1.5f);
                using var f = new Font(Font.FontFamily, 7.5f);
                using var br = new SolidBrush(Color.FromArgb(150, Color.Gainsboro));
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
                foreach (var (label, cells) in Regions)
                {
                    var rc = new Rectangle(cells.X * CellSize + 1, cells.Y * CellSize + 1, cells.Width * CellSize - 2, cells.Height * CellSize - 2);
                    g.DrawRectangle(pen, rc);
                    g.DrawString(label, f, br, rc, sf);
                }
            }

            // 各アイテム（ドラッグ中の本体はゴーストで描くのでここでは省く）。
            foreach (var id in VisibleIds())
            {
                if (_dragging && id == _dragId) continue;
                DrawItem(g, id, false);
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

            DrawIdTag(g, id, rect);
        }

        // セル左上にアイテムID（item_2 等）を小さく表示する（背景半透明で可読性を確保）。
        private void DrawIdTag(Graphics g, string id, Rectangle rect)
        {
            using var f = new Font(Font.FontFamily, 7f, FontStyle.Regular);
            var sz = g.MeasureString(id, f);
            using (var bb = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                g.FillRectangle(bb, rect.X + 1, rect.Y + 1, sz.Width, sz.Height);
            using var br = new SolidBrush(Color.Gainsboro);
            g.DrawString(id, f, br, rect.X + 1, rect.Y + 1);
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
            "magical" => Color.DodgerBlue,       // ゲーム内準拠: magical は青系
            "rare" => Color.MediumSeaGreen,      // ゲーム内準拠: rare は緑系
            _ => Color.Gray,
        };

        // ---------------- 画像解決・キャッシュ ----------------
        // image_src をゲーム導入先（と ModLoader の絵の置き場）から解決して読み込む（ModLoaderLink.ResolveAsset）。
        // File.ReadAllBytes→MemoryStream 経由で元PNGをロックしない。解決不能・未設定・例外時は null（プレースホルダ表示）。
        private Image ResolveImage(string id)
        {
            string src = J.Str(_inv[id] as JsonObject, "image_src");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(_assetKey)) return null;
            if (_imgCache.TryGetValue(src, out var cached)) return cached;

            Image img = null;
            try
            {
                string full = ModLoaderLink.ResolveAsset(src);
                if (full != null)
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

        // スクリーン座標→セル。範囲外は (-1,-1)。他のグリッドから落とされた位置を調べるのに使う。
        public Point CellAtScreen(Point screen) => CellAt(PointToClient(screen));

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
            // Ctrl+クリック: つかんだアイテムを他グリッドへ移動する（選択・ドラッグは開始しない）。
            if ((ModifierKeys & Keys.Control) == Keys.Control && ItemCtrlClicked != null)
            {
                string hit = ItemAt(e.Location);
                if (hit != null) { ItemCtrlClicked(hit); return; }
            }
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

            // ドラッグ開始判定（しきい値4px）。並びを動かせないグリッドでは始めない。
            if (!_dragging && (!ReadOnlyLayout || AllowDragOut) && e.Button == MouseButtons.Left && _pressId != null)
            {
                if (Math.Abs(e.X - _pressPos.X) > 4 || Math.Abs(e.Y - _pressPos.Y) > 4)
                {
                    _dragId = _pressId;
                    var (x, y, _, _) = CellRect(_dragId);
                    _dragOffset = new Point(_pressPos.X - x * CellSize, _pressPos.Y - y * CellSize);
                    _dragging = true;
                    // OnMouseLeave と同じく _hoverId も落とす。残したままだとドラッグ後に
                    // 同じアイテム上へ戻ったとき「変化なし」と判定され、ツールチップが復帰しない。
                    HideTip(); _hoverId = null;
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
                // コントロール外で離した場合は他グリッドへの移動として通知する（内部ドロップはしない）。
                if (!ClientRectangle.Contains(e.Location) && ItemDraggedOut != null)
                {
                    string dragged = _dragId;
                    _dragging = false; _dragId = null;
                    try { ItemDraggedOut(dragged, PointToScreen(e.Location)); }
                    catch { /* 失敗してもクラッシュさせない（スナップバック扱い） */ }
                }
                else
                {
                    try { if (!ReadOnlyLayout) PerformDrop(); }
                    catch { /* 何があってもクラッシュさせない（スナップバック扱い） */ }
                    _dragging = false; _dragId = null;
                }
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
            _tip.SetContent(TipTitle(o, id), TipBody(o));
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

        // ツールチップ見出し（アイテム名＋ID タグ）。
        private static string TipTitle(JsonObject o, string id) => $"{J.Str(o, "name", I18n.T("label.unnamed"))}（{id}）";

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

    // インベントリの「グリッド＋追加/編集/削除ボタン」に、右側のアイテム倉庫グリッドを並べた再利用パネル。
    // プレイヤー(PlayerTab)と NPC(WorldTab の ObjectForm 経由) の双方から使う。
    // inventory 辞書を直接編集し、追加/削除/編集のたびに InventoryChanged を発火する。
    // 倉庫(ItemWarehouse)はツール側の保管箱で、キャラ個別倉庫(item\{world}\{char})と共有倉庫(item\shared)の2系統。
    // Ctrl+クリック（インベントリ→個別倉庫／倉庫→インベントリ）またはグリッド間ドラッグ（重なった倉庫へ振り分け）、
    // 各倉庫の「保存/取出」ボタンでインベントリ⇄倉庫を移動する（保存=zip 書き出し、取出=zip 削除＋画像復元）。
    internal sealed class InventoryPanel : Panel
    {
        private readonly InventoryGridControl _grid = new() { Location = new Point(0, 0) };
        private readonly InventoryGridControl _whChar = new() { Location = new Point(0, 0) };   // 個別倉庫グリッド（キャラ毎）
        private readonly InventoryGridControl _whShared = new() { Location = new Point(0, 0) }; // 共有倉庫グリッド（全キャラ共通）
        // 装備欄（ModLoader の 333_equipment_slots）。装備中の品をその部位に表示する（並べ替えはゲーム内で行う）。
        private readonly InventoryGridControl _equip = new()
        {
            Location = new Point(0, 0), CellSizeOverride = 34, ReadOnlyLayout = true,
            GridSizeOverride = new Size(ModLoaderLink.SlotCols, ModLoaderLink.SlotRows),
        };
        private readonly Panel _equipHost = new() { Width = 214, Height = 360, Margin = new Padding(0, 0, 6, 0), Visible = false };
        private readonly Label _lblEquip = new() { Dock = DockStyle.Top, Height = 18 };
        private Dictionary<string, Point> _equipped = new();
        private Panel _invHost, _charHost, _sharedHost, _midChar, _midShared;
        private Label _lblInv, _lblChar, _lblShared;
        private readonly Button _btnEdit, _btnDelete, _btnExport, _btnEquip, _btnUnequip;
        private readonly Button _btnToChar, _btnFromChar;     // 個別倉庫へ保存 / から取出
        private readonly Button _btnToShared, _btnFromShared; // 共有倉庫へ保存 / から取出
        private ItemWarehouse _charWh;   // キャラ個別倉庫（Bind 時に world/char から生成）
        // 画像が表示できないときに「画像パス未設定」を知らせる警告ラベル（最上段）。
        private readonly Label _warn = new()
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 36,
            ForeColor = Color.Firebrick,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(2, 0, 2, 0),
            Visible = false,
        };
        private JsonObject _inv;
        private readonly bool _warehouse;          // 倉庫機能を表示するか（プレイヤーのみ true）

        // 追加/削除/編集でインベントリが変化したときに発火（装備コンボの再構築などに使う）。
        public event Action InventoryChanged;
        // 装備欄の品を外したとき（InventoryChanged より先）。PlayerTab が武器・防具の選択をこの品から外すのに使う
        //（選択を残したまま InventoryChanged で書き戻すと、外した品がまた本体の装備になる）。
        public event Action<string> ItemUnequipped;

        // 装備欄 MOD の控えでの持ち主（主人公は名前、仲間は "npc:<id>"。ModLoaderLink.PlayerOwner / NpcOwner）。
        // 未設定なら装備欄は出さない。
        public string EquipOwner { get; set; }
        // 装備が1つも無くても装備欄を出す（主人公・パーティにいる仲間。装備欄 MOD が使えるときだけ）。
        public bool EquipAlways { get; set; }
        // 装備欄の操作で本体の装備（player_data.equipments）を選び直したとき。PlayerTab が武器・防具の欄を作り直す。
        public event Action EquipmentsChanged;

        // warehouseEnabled=true で右側にアイテム倉庫グリッドと移動ボタンを表示する（プレイヤー専用）。
        // false（既定）では従来どおりインベントリグリッドのみ。NPC 編集ではこちらを使う。
        public InventoryPanel(bool warehouseEnabled = false)
        {
            _warehouse = warehouseEnabled;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _grid.ItemActivated += id => EditItem(id);
            _grid.SelectionChanged += UpdateButtons;

            // 装備欄: 装備中の品は所持品のグリッドから外し、装備欄にだけ出す
            //（装備中の品の grid_pos は装備欄の座標で書かれるため、所持品に並べると重なる）。
            _grid.ItemFilter = id => !_equipped.ContainsKey(id);
            _equip.ItemFilter = id => _equipped.ContainsKey(id);
            _equip.PositionOverride = id => _equipped.TryGetValue(id, out var p) ? p : null;
            _equip.Regions = ModLoaderLink.SlotRegions.Select(r => (ModLoaderLink.SlotLabel(r.name), r.cells)).ToList();
            _equip.ItemActivated += id => EditItem(id);
            _equip.SelectionChanged += UpdateButtons;
            // 装備欄から所持品へドラッグすると外す（装備欄の中での付け替えはゲーム内で行う）。
            _equip.AllowDragOut = true;
            _equip.ItemDraggedOut += (id, pt) => { if (Over(_grid, pt)) Unequip(id); };
            // 所持品から装備欄へドラッグすると、落とした部位へ装備する（部位の外なら空いている合う部位へ）。
            _grid.ItemDraggedOut += (id, pt) => { if (_equipHost.Visible && Over(_equip, pt)) Equip(id, RegionAt(pt)); };
            _lblEquip.Text = I18n.T("inv.equipLabel");
            var equipGridHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            equipGridHost.Controls.Add(_equip);
            _equipHost.Controls.Add(equipGridHost);
            _equipHost.Controls.Add(_lblEquip);

            // 本体（グリッド領域）。倉庫有効時は [インベントリ][個別倉庫ボタン][個別倉庫][共有倉庫ボタン][共有倉庫] を横に並べる。
            Control body;
            if (_warehouse)
            {
                var invHost = _invHost = new Panel { Width = 250, Height = 354, AutoScroll = true, Margin = new Padding(0) };
                invHost.Controls.Add(_grid);
                // Ctrl+クリック: インベントリ→個別倉庫（既定）。ドラッグ: 重なった倉庫へ振り分け。
                _grid.ItemCtrlClicked += id => SaveToWarehouse(id, _charWh);
                _grid.ItemDraggedOut += (id, pt) =>
                {
                    if (Over(_whChar, pt)) SaveToWarehouse(id, _charWh);
                    else if (Over(_whShared, pt)) SaveToWarehouse(id, ItemWarehouse.Shared);
                };

                _whChar.GridSizeOverride = new Size(ItemWarehouse.Cols, ItemWarehouse.Rows);
                var charHost = _charHost = new Panel { Width = 356, Height = 354, AutoScroll = true, Margin = new Padding(0) };
                charHost.Controls.Add(_whChar);
                _whChar.SelectionChanged += UpdateButtons;
                _whChar.ItemCtrlClicked += id => RetrieveFromWarehouse(id, _charWh);
                _whChar.ItemDraggedOut += (id, pt) => { if (Over(_grid, pt)) RetrieveFromWarehouse(id, _charWh); };

                _whShared.GridSizeOverride = new Size(ItemWarehouse.Cols, ItemWarehouse.Rows);
                var sharedHost = _sharedHost = new Panel { Width = 356, Height = 354, AutoScroll = true, Margin = new Padding(0) };
                sharedHost.Controls.Add(_whShared);
                _whShared.SelectionChanged += UpdateButtons;
                _whShared.ItemCtrlClicked += id => RetrieveFromWarehouse(id, ItemWarehouse.Shared);
                _whShared.ItemDraggedOut += (id, pt) => { if (Over(_grid, pt)) RetrieveFromWarehouse(id, ItemWarehouse.Shared); };

                // 個別倉庫の移動ボタン列。
                var midChar = _midChar = new Panel { Width = 130, Height = 354, Margin = new Padding(0) };
                _btnToChar = new Button { Text = I18n.T("inv.toCharWarehouse"), Width = 120, Height = 32, Left = 5, Top = 130, Enabled = false };
                _btnFromChar = new Button { Text = I18n.T("inv.fromCharWarehouse"), Width = 120, Height = 32, Left = 5, Top = 180, Enabled = false };
                _btnToChar.Click += (_, _) => { if (_grid.SelectedId != null) SaveToWarehouse(_grid.SelectedId, _charWh); };
                _btnFromChar.Click += (_, _) => { if (_whChar.SelectedId != null) RetrieveFromWarehouse(_whChar.SelectedId, _charWh); };
                midChar.Controls.Add(_btnToChar); midChar.Controls.Add(_btnFromChar);

                // 共有倉庫の移動ボタン列。
                var midShared = _midShared = new Panel { Width = 130, Height = 354, Margin = new Padding(0) };
                _btnToShared = new Button { Text = I18n.T("inv.toSharedWarehouse"), Width = 120, Height = 32, Left = 5, Top = 130, Enabled = false };
                _btnFromShared = new Button { Text = I18n.T("inv.fromSharedWarehouse"), Width = 120, Height = 32, Left = 5, Top = 180, Enabled = false };
                _btnToShared.Click += (_, _) => { if (_grid.SelectedId != null) SaveToWarehouse(_grid.SelectedId, ItemWarehouse.Shared); };
                _btnFromShared.Click += (_, _) => { if (_whShared.SelectedId != null) RetrieveFromWarehouse(_whShared.SelectedId, ItemWarehouse.Shared); };
                midShared.Controls.Add(_btnToShared); midShared.Controls.Add(_btnFromShared);

                var row = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    Height = 360,
                    AutoSize = false,
                    WrapContents = false,
                    FlowDirection = FlowDirection.LeftToRight,
                };
                row.Controls.Add(_equipHost);
                row.Controls.Add(invHost);
                row.Controls.Add(midChar);
                row.Controls.Add(charHost);
                row.Controls.Add(midShared);
                row.Controls.Add(sharedHost);
                body = row;
            }
            else
            {
                // 倉庫なし: 従来どおりインベントリグリッドのみ（行数次第で背が高くなるためスクロールホストに載せる）。
                // 装備欄がある仲間は左に装備欄を並べる。
                var gridHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                gridHost.Controls.Add(_grid);
                var holder = new Panel { Dock = DockStyle.Top, Height = 360 };
                _equipHost.Dock = DockStyle.Left;
                holder.Controls.Add(gridHost);
                holder.Controls.Add(_equipHost);
                body = holder;
            }

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            Button Mk(string text, Action act, int width = 70)
            {
                var b = new Button { Text = text, Width = width, Margin = new Padding(2) };
                b.Click += (_, _) => act();
                bar.Controls.Add(b);
                return b;
            }
            Mk(I18n.T("btn.add"), AddItem);
            _btnEdit = Mk(I18n.T("btn.edit"), () => { if (_grid.SelectedId != null) EditItem(_grid.SelectedId); });
            _btnDelete = Mk(I18n.T("btn.delete"), DeleteItem);
            _btnExport = Mk(I18n.T("btn.export"), ExportItem, 96);
            Mk(I18n.T("inv.import"), ImportItem, 96);
            _btnEquip = Mk(I18n.T("inv.equip"), () => { if (_grid.SelectedId != null) Equip(_grid.SelectedId, null); }, 110);
            _btnUnequip = Mk(I18n.T("inv.unequip"), () => { if (_equip.SelectedId != null) Unequip(_equip.SelectedId); }, 110);
            _btnDelete.Enabled = false;
            _btnExport.Enabled = false;

            // Dock=Top は後入れが上。下から: 本体 → (見出し) → ボタン列 → 警告ラベル（最上段）。
            Controls.Add(body);
            if (_warehouse)
            {
                // グリッドの見出し（インベントリ / 個別倉庫 / 共有倉庫）。X はホスト幅の累積に合わせる。
                var header = new Panel { Dock = DockStyle.Top, Height = 18 };
                header.Controls.Add(_lblInv = new Label { Text = I18n.T("inv.inventoryLabel"), AutoSize = true, Left = 0, Top = 1 });
                header.Controls.Add(_lblChar = new Label { Text = I18n.T("inv.charWarehouseLabel"), AutoSize = true, Left = 380, Top = 1 });
                header.Controls.Add(_lblShared = new Label { Text = I18n.T("inv.sharedWarehouseLabel"), AutoSize = true, Left = 866, Top = 1 });
                Controls.Add(header);
            }
            Controls.Add(bar);
            Controls.Add(_warn);
        }

        // 装備欄 MOD の控えから、この持ち主の装備中の品（インベントリに居るものだけ）を読み直し、装備欄の表示可否と
        // 倉庫の見出しの位置を合わせる。ローダ未設定・控え無し・装備無しなら装備欄は出さない（全品を所持品に並べる）。
        private void RefreshEquipped()
        {
            var map = ModLoaderLink.EquippedOf(SaveRootProvider?.Invoke(), EquipOwner);
            _equipped = _inv == null ? new() : map.Where(kv => _inv[kv.Key] is JsonObject).ToDictionary(kv => kv.Key, kv => kv.Value);
            _equipHost.Visible = _equipped.Count > 0 || (EquipAlways && !string.IsNullOrEmpty(EquipOwner) && ModLoaderLink.EquipmentSlotsAvailable);
            if (_lblInv != null)
            {
                int x = _equipHost.Visible ? _equipHost.Width + _equipHost.Margin.Horizontal : 0;
                _lblInv.Left = x;
                x += _invHost.Width + _midChar.Width;
                _lblChar.Left = x;
                _lblShared.Left = x + _charHost.Width + _midShared.Width;
            }
        }

        // 装備欄の品を外して所持品へ戻す。所持品の空きへ置き、MOD の控えから外す（控えへの書き込みは保存時。ModLoaderLink.Unequip）。
        // 本体の equipments がこの品を指していれば外す。指したままだと、MOD が「本体が装備しているのに控えに無い品」として拾い直す。
        private void Unequip(string id)
        {
            if (_inv?[id] is not JsonObject item || !_equipped.ContainsKey(id)) return;
            int cols = Math.Max(1, Settings.Current?.InventoryGridColumns ?? 4);
            int rows = Math.Max(1, Settings.Current?.InventoryGridRows ?? 6);
            int w = Math.Max(1, (int)J.Int(item, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(item, "height_slots", 1));
            var pos = ItemPortability.TryFindFreeGridPosTopLeft(PlacementView(), w, h, cols, rows);
            if (pos == null)
            {
                MessageBox.Show(FindForm(), I18n.T("msg.unequip.noSpace", w, h), I18n.T("title.confirm"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var root = SaveRootProvider?.Invoke();
            if (!ModLoaderLink.Unequip(root, EquipOwner, id)) return;
            item["grid_pos"] = pos;
            ItemUnequipped?.Invoke(id);
            RecomputeGameEquipments(root);
            Reload();
        }

        // 所持品の品を装備欄へ入れる（333_equipment_slots と同じ決まり）。
        // region を指定すればその部位、null なら空いている合う部位（手と装飾は右・1つ目が先。専用の部位が埋まっていれば自由枠）。
        // 合う部位が全部埋まっていれば専用の部位（右手・1つ目の装飾が先）の品と入れ替え、居た品は所持品の空きへ戻す（空きが無ければ入れない）。
        // 控えへの書き込みは保存時（ModLoaderLink.Equip）。品の grid_pos は MOD と同じく装備欄の座標（y は下から）にする。
        private void Equip(string id, string region)
        {
            if (_inv?[id] is not JsonObject item || _equipped.ContainsKey(id)) return;
            string kind = ModLoaderLink.KindOf(item);
            if (kind == null) { Notice(I18n.T("msg.equip.notEquipment")); return; }
            int w = Math.Max(1, (int)J.Int(item, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(item, "height_slots", 1));
            bool Fits(Rectangle c) => w <= c.Width && h <= c.Height;
            string Occupant(Rectangle c) => _equipped.FirstOrDefault(kv => c.Contains(kv.Value)).Key;

            (string name, Rectangle cells) target = default;
            if (region != null)
            {
                target = ModLoaderLink.SlotRegions.First(r => r.name == region);
                if (!ModLoaderLink.Takes(region, kind)) { Notice(I18n.T("msg.equip.wrongSlot", ModLoaderLink.SlotLabel(region))); return; }
                if (!Fits(target.cells)) { Notice(I18n.T("msg.equip.tooBig", ModLoaderLink.SlotLabel(region), w, h)); return; }
            }
            else
            {
                var fit = ModLoaderLink.SlotRegions.Where(r => ModLoaderLink.Takes(r.name, kind) && Fits(r.cells)).ToList();
                var ordered = fit.Where(r => !ModLoaderLink.IsFreeRegion(r.name)).Concat(fit.Where(r => ModLoaderLink.IsFreeRegion(r.name))).ToList();
                target = ordered.FirstOrDefault(r => Occupant(r.cells) == null);
                if (target.name == null) target = ordered.FirstOrDefault();
                if (target.name == null) { Notice(I18n.T("msg.equip.noSlot", w, h)); return; }
            }

            // 部位に居た品は所持品の空きへ戻す（入れる品が空けたマスも使える）。
            string occupant = Occupant(target.cells);
            JsonArray occPos = null;
            if (occupant != null && _inv[occupant] is JsonObject occ)
            {
                var view = new JsonObject();
                foreach (var kv in PlacementView() ?? new JsonObject())
                    if (kv.Key != id) view[kv.Key] = kv.Value?.DeepClone();
                int cols = Math.Max(1, Settings.Current?.InventoryGridColumns ?? 4);
                int rows = Math.Max(1, Settings.Current?.InventoryGridRows ?? 6);
                int ow = Math.Max(1, (int)J.Int(occ, "width_slots", 1)), oh = Math.Max(1, (int)J.Int(occ, "height_slots", 1));
                occPos = ItemPortability.TryFindFreeGridPosTopLeft(view, ow, oh, cols, rows);
                if (occPos == null) { Notice(I18n.T("msg.equip.noSpaceForSwap", J.Str(occ, "name", occupant), ow, oh)); return; }
            }

            var root = SaveRootProvider?.Invoke();
            if (occupant != null)
            {
                if (!ModLoaderLink.Unequip(root, EquipOwner, occupant)) return;
                ((JsonObject)_inv[occupant])["grid_pos"] = occPos;
            }
            if (!ModLoaderLink.Equip(root, EquipOwner, id, target.cells.Location)) return;
            item["grid_pos"] = new JsonArray(target.cells.X, ModLoaderLink.SlotRows - target.cells.Y - h);
            RecomputeGameEquipments(root);
            Reload();
        }

        // 落とした位置の部位（部位の外・装備欄の外なら null）。
        private string RegionAt(Point screen)
        {
            var cell = _equip.CellAtScreen(screen);
            if (cell.X < 0) return null;
            return ModLoaderLink.SlotRegions.FirstOrDefault(r => r.cells.Contains(cell)).name;
        }

        // 本体の装備（player_data.equipments の weapon / wearable）を装備欄から選び直す（333_ と同じ選び方）。主人公のときだけ
        //（仲間の equipments はゲームが保存しない）。装備欄の品を本体が指したままだと、MOD が控えに無い品として拾い直すため。
        private void RecomputeGameEquipments(JsonObject root)
        {
            if (string.IsNullOrEmpty(EquipOwner) || EquipOwner != ModLoaderLink.PlayerOwner(root) || J.Obj(root, "player_data") is not JsonObject pd) return;
            var (weapon, wearable) = ModLoaderLink.ChooseGameEquipments(_inv, ModLoaderLink.EquippedOf(root, EquipOwner));
            if (pd["equipments"] is not JsonObject eq)
            {
                if (weapon == null && wearable == null) return;
                pd["equipments"] = eq = new JsonObject();
            }
            if (weapon != null) eq["weapon"] = weapon; else eq.Remove("weapon");
            if (wearable != null) eq["wearable"] = wearable; else eq.Remove("wearable");
            EquipmentsChanged?.Invoke();
        }

        private void Notice(string text)
            => MessageBox.Show(FindForm(), text, I18n.T("title.confirm"), MessageBoxButtons.OK, MessageBoxIcon.Information);

        // 空きマス探し用のインベントリ。装備欄の品は所持品のマスを使わない（grid_pos は装備欄の座標）ので除く。
        private JsonObject PlacementView()
        {
            if (_equipped.Count == 0 || _inv == null) return _inv;
            var view = new JsonObject();
            foreach (var kv in _inv)
                if (!_equipped.ContainsKey(kv.Key)) view[kv.Key] = kv.Value?.DeepClone();
            return view;
        }

        // スクリーン座標がグリッド c のクライアント領域内かどうか（外部ドロップの移動先判定）。
        private static bool Over(InventoryGridControl c, Point screenPt)
            => c.ClientRectangle.Contains(c.PointToClient(screenPt));

        // inventory 辞書をバインドして表示を作り直す。倉庫有効時は個別/共有の両倉庫も読み込んで表示する。
        // worldName/charName は個別倉庫（item\{world}\{char}）の場所決定に使う（倉庫無効時は無視）。
        public void Bind(JsonObject inventory, string worldName = null, string charName = null)
        {
            _inv = inventory;
            RefreshEquipped();
            _grid.Bind(inventory);
            _equip.Bind(inventory);
            if (_warehouse)
            {
                _charWh = new ItemWarehouse(ItemWarehouse.CharDir(worldName, charName));
                _charWh.EnsureLoaded();
                ItemWarehouse.Shared.EnsureLoaded();
                _whChar.Bind(_charWh.Items);
                _whShared.Bind(ItemWarehouse.Shared.Items);
            }
            UpdateWarning();
            UpdateButtons();
        }

        // 全グリッドを作り直し、ボタンの有効状態を更新し、変更を通知する（倉庫有効時のみ）。
        private void ReloadAll()
        {
            RefreshEquipped();
            _grid.Bind(_inv);
            _equip.Bind(_inv);
            _whChar.Bind(_charWh?.Items);
            _whShared.Bind(ItemWarehouse.Shared.Items);
            UpdateWarning();
            UpdateButtons();
            InventoryChanged?.Invoke();
        }

        // インベントリのみを作り直す（追加/削除/編集/インポート用。倉庫は変化しない）。
        private void Reload()
        {
            RefreshEquipped();
            _grid.Bind(_inv);
            _equip.Bind(_inv);
            UpdateWarning();
            UpdateButtons();
            InventoryChanged?.Invoke();
        }

        // 選択の有無・倉庫の利用可否で削除・エクスポート・各移動ボタンの有効状態を切り替える。
        private void UpdateButtons()
        {
            bool hasSel = _grid.SelectedId != null;
            if (_btnUnequip != null)
            {
                _btnEquip.Visible = _btnUnequip.Visible = _equipHost.Visible;
                _btnEquip.Enabled = hasSel && ModLoaderLink.KindOf(_inv?[_grid.SelectedId] as JsonObject) != null;
                _btnUnequip.Enabled = _equip.SelectedId != null;
            }
            if (_btnDelete != null) _btnDelete.Enabled = hasSel;
            if (_btnExport != null) _btnExport.Enabled = hasSel;
            if (_btnToChar != null) _btnToChar.Enabled = hasSel && (_charWh?.Available ?? false);
            if (_btnFromChar != null) _btnFromChar.Enabled = _whChar.SelectedId != null;
            if (_btnToShared != null) _btnToShared.Enabled = hasSel;
            if (_btnFromShared != null) _btnFromShared.Enabled = _whShared.SelectedId != null;
        }

        // インベントリのアイテムを倉庫へ移す（インベントリから除去し、指定倉庫へ追加＝zip 書き出し）。
        private void SaveToWarehouse(string id, ItemWarehouse wh)
        {
            if (_inv == null || id == null || wh == null || !wh.Available || _inv[id] is not JsonObject item) return;
            try { wh.Add(item, Settings.Current?.GameAssetRoot ?? ""); }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.warehouseSaveFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _inv.Remove(id);
            ReloadAll();
        }

        // 指定倉庫のアイテムをこのインベントリへ移す（倉庫から取り出し＝zip 削除・画像復元し、新 item ID で追加）。
        private void RetrieveFromWarehouse(string id, ItemWarehouse wh)
        {
            if (_inv == null || id == null || wh == null) return;
            string assetRoot = Settings.Current?.GameAssetRoot ?? "";
            JsonObject item;
            try { item = wh.TakeOut(id, assetRoot); }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.warehouseTakeFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (item == null) return;

            string newId = NewId();
            int cols = Math.Max(1, Settings.Current?.InventoryGridColumns ?? 4);
            int rows = Math.Max(1, Settings.Current?.InventoryGridRows ?? 6);
            int w = Math.Max(1, (int)J.Int(item, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(item, "height_slots", 1));
            item["grid_pos"] = ItemPortability.FindFreeGridPosTopLeft(PlacementView(), w, h, cols, rows);
            _inv[newId] = item;
            ReloadAll();
        }

        // グリッドの画像解決状況に応じて警告ラベルの表示/文言を更新する。
        private void UpdateWarning()
        {
            _warn.Text = I18n.T("inv.imageUnavailable");
            _warn.Visible = _grid.ImagesUnavailable;
        }

        // 新規アイテム（1×1 のテンプレ雛形）を空き位置へ追加する。
        // grid_pos を設定せずに保存するとゲーム側が読めず落ちるため、必ず空き位置を割り当てる。
        private void AddItem()
        {
            if (_inv == null) return;
            var item = NewItemTemplate();
            int cols = Math.Max(1, Settings.Current?.InventoryGridColumns ?? 4);
            int rows = Math.Max(1, Settings.Current?.InventoryGridRows ?? 6);
            item["grid_pos"] = ItemPortability.FindFreeGridPosTopLeft(PlacementView(), 1, 1, cols, rows);
            _inv[NewId()] = item;
            Reload();
        }

        // 指定アイテムを ItemEditDialog（複製を編集し OK 時のみ反映）で編集する。
        private void EditItem(string id)
        {
            if (_inv == null || id == null || _inv[id] is not JsonObject item) return;
            using var dlg = new ItemEditDialog(id, item);
            dlg.ShowDialog(FindForm());
            Reload();
        }

        // 選択中のアイテムを確認の上インベントリから削除する。
        private void DeleteItem()
        {
            string id = _grid.SelectedId;
            if (_inv == null || id == null) return;
            if (MessageBox.Show(I18n.T("msg.confirmDeleteItem", id), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _inv.Remove(id);
            Reload();
        }

        // 選択中のアイテムを JSON＋画像の zip としてエクスポートする。
        private void ExportItem()
        {
            string id = _grid.SelectedId;
            if (_inv == null || id == null || _inv[id] is not JsonObject item) return;
            string name = J.Str(item, "name", "item");
            using var dlg = new SaveFileDialog
            {
                Filter = I18n.T("filter.itemPackageSave"),
                DefaultExt = "zip",
                FileName = SafeFileName(name) + ".zip",
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { ItemPortability.Export(item, Settings.Current?.GameAssetRoot ?? "", id, dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.exportFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(I18n.T("msg.itemExported", name, dlg.FileName), I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // アイテムパッケージ(zip)を読み込み、新しい item ID としてこのインベントリへ追加する。
        private void ImportItem()
        {
            if (_inv == null) return;
            using var ofd = new OpenFileDialog
            {
                Filter = I18n.T("filter.itemPackage"),
                Title = I18n.T("title.selectItemPackage"),
            };
            if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;

            ItemPackage pkg;
            try { pkg = ItemPortability.ReadPackage(ofd.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.loadFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 画像を GameAssetRoot 配下へ復元（既存なら触らない）。失敗しても取り込み自体は続行する。
            string assetRoot = Settings.Current?.GameAssetRoot ?? "";
            try { ItemPortability.PlaceImage(assetRoot, pkg.ImageRelPath, pkg.ImageBytes); } catch { }

            var item = pkg.Item;
            string newId = NewId();
            int cols = Math.Max(1, Settings.Current?.InventoryGridColumns ?? 4);
            int rows = Math.Max(1, Settings.Current?.InventoryGridRows ?? 6);
            int w = Math.Max(1, (int)J.Int(item, "width_slots", 1));
            int h = Math.Max(1, (int)J.Int(item, "height_slots", 1));
            // 追加・倉庫取り出しと同じく画面の左上から詰める（zip インポートだけ下に置かれるのを防ぐ）。
            item["grid_pos"] = ItemPortability.FindFreeGridPosTopLeft(PlacementView(), w, h, cols, rows);

            _inv[newId] = item;
            Reload();
            MessageBox.Show(I18n.T("msg.itemImported", J.Str(item, "name", newId), newId), I18n.T("title.importDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ファイル名に使えない文字を '_' に置き換える（".." や予約デバイス名も潰す共通実装）。
        private static string SafeFileName(string s) => SafePath.FileName(s, "item");

        // ---------------- item ID の採番 ----------------
        // セーブのルート（index.item を含む）を返すフック。このパネルは対象インベントリしか
        // 持たないため、バインド元（PlayerTab / WorldTab→ObjectForm）が設定する。
        // 未設定・null 返しならインベントリ内 最大+1 の従来採番になる（ルートが無い用途の保険）。
        public Func<JsonObject> SaveRootProvider { get; set; }

        // 追加・インポート・倉庫取り出しが使う新 ID。
        private string NewId() => IssueItemId(SaveRootProvider?.Invoke(), _inv);

        // セーブ全体で一意な新規 item ID を発行する。ゲームは index.item をグローバルな採番
        // カウンタとして使う（払い出してからカウンタを進める）ため、同じ規則で採番して次の値を
        // 書き戻す。インベントリ内の最大+1 だけで決めると index.item が進まず、ゲームが同じ ID を
        // 配り直して既存アイテムが上書き消滅する（合成品のドラッグで KeyError クラッシュ）。
        // 使用中の ID はカウンタがデータとずれていても飛ばす。index.item が無い・数値でない場合は
        // セーブ全体の使用中最大+1 で採番し、index があれば次の値を書き戻す。
        public static string IssueItemId(JsonObject root, JsonObject inv)
        {
            if (root == null) return NextItemId(inv);
            var taken = TakenItemNumbers(root, inv);
            var index = J.Obj(root, "index");
            long cur = J.Int(index, "item", -1);
            if (cur < 0) cur = taken.Count == 0 ? 0 : taken.Max() + 1;
            while (taken.Contains(cur)) cur++;
            if (index != null) index["item"] = cur + 1;
            return "item_" + cur;
        }

        // キャラ（NPC / player_data 同形）のインベントリ全 item を index.item 方式で採番し直し、
        // equipments の参照 ID も追従させる。別ワールド由来の取込（NPC インポート）や同一セーブ内の
        // 複製（NPC 複製・プレイヤーの NPC 化）は元の ID をそのまま持ち込むため、採番し直さないと
        // index.item やほかのキャラの持ち物と衝突する余地が残る。
        public static void ReassignItemIds(JsonObject root, JsonObject character)
        {
            if (root == null || character == null || J.Obj(character, "inventory") is not JsonObject inv || inv.Count == 0) return;
            var entries = inv.ToList();
            inv.Clear();   // Clear で子ノードは親から外れるため、そのまま新キーで入れ直せる
            var map = new Dictionary<string, string>();
            foreach (var kv in entries)
            {
                string newId = IssueItemId(root, inv);
                map[kv.Key] = newId;
                inv[newId] = kv.Value;
            }
            if (J.Obj(character, "equipments") is JsonObject eq)
                foreach (var slot in eq.ToList())
                    if (slot.Value is JsonValue v && v.TryGetValue<string>(out string sid) && map.TryGetValue(sid, out string nid))
                        eq[slot.Key] = nid;
        }

        // セーブ全体（プレイヤー・全 NPC の inventory / equipments）で使用中の item 番号を集める。
        // inv は採番先のインベントリ。ルート未接続（取込・採番し直しの途中）でも数えるため明示的に渡す（無ければ null）。
        // IndexAudit も index.item の遅れの検査に使う。
        internal static HashSet<long> TakenItemNumbers(JsonObject root, JsonObject inv)
        {
            var taken = new HashSet<long>();
            AddItemNumbers(taken, inv);
            var pd = J.Obj(root, "player_data");
            AddItemNumbers(taken, J.Obj(pd, "inventory"));
            AddEquipNumbers(taken, J.Obj(pd, "equipments"));
            if (J.Obj(root, "npcs") is JsonObject npcs)
                foreach (var kv in npcs)
                {
                    if (kv.Value is not JsonObject npc) continue;
                    AddItemNumbers(taken, J.Obj(npc, "inventory"));
                    AddEquipNumbers(taken, J.Obj(npc, "equipments"));
                }
            return taken;
        }

        // インベントリのキー（"item_N" または素の数字。実データに素の "146" が混ざる例あり）から番号を集める。
        private static void AddItemNumbers(HashSet<long> taken, JsonObject dict)
        {
            if (dict == null) return;
            foreach (var kv in dict)
            {
                string s = kv.Key.StartsWith("item_") ? kv.Key.Substring(5) : kv.Key;
                if (long.TryParse(s, out long n)) taken.Add(n);
            }
        }

        // equipments の値（"item_N" のアイテム ID 参照）から番号を集める。
        private static void AddEquipNumbers(HashSet<long> taken, JsonObject eq)
        {
            if (eq == null) return;
            foreach (var kv in eq)
                if (kv.Value is JsonValue v && v.TryGetValue<string>(out string s)
                    && s.StartsWith("item_") && long.TryParse(s.Substring(5), out long n))
                    taken.Add(n);
        }

        // 指定インベントリ内だけを見る従来採番（item_N の N=既存最大+1）。セーブの index.item とは
        // 同期しないため、セーブへの追加には IssueItemId を使うこと。倉庫 zip 内の管理 ID など、
        // セーブと無関係な辞書の採番はこちら。
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

        // 新規アイテムのテンプレ（実アイテムと同じキー順。1×1で開始）。
        // item_detail は "treasure" を既定とし、attributes を type のスキーマ（ステータスキー=0）へ、
        // image_src を item_detail 該当フォルダの先頭画像へ初期化する。
        public static JsonObject NewItemTemplate()
        {
            const string type = "material";
            const string detail = "treasure";
            var attrs = new JsonObject { ["item_detail"] = detail };
            foreach (var k in FieldOptions.Get("attributes." + type)) attrs[k] = 0L;
            string image = ImagePickerDialog.LowestImageForDetail(Settings.Current?.GameAssetRoot ?? "", detail);
            return new()
            {
                ["name"] = I18n.T("player.newItem"),
                ["item_type"] = type,
                ["attributes"] = attrs,
                ["description"] = "",
                ["value"] = 0L,
                ["rarity"] = "common",
                ["skill"] = null,
                ["upgrade_level"] = 0L,
                ["width_slots"] = 1L,
                ["height_slots"] = 1L,
                ["image_src"] = image,
            };
        }
    }

    // インベントリ1アイテムを既存の ObjectForm で編集するダイアログ。
    // ObjectForm はフォーカスアウトで即モデルへ反映するため、編集は複製(_work)に対して行い、
    // OK 時のみ元アイテム(_item)へ書き戻す。キャンセル時は元アイテムに一切触れない。
    internal sealed class ItemEditDialog : Form
    {
        private readonly ObjectForm _form = new();
        private readonly JsonObject _item;   // 元（inventory 辞書内の実体）。OK 時のみ更新する
        private readonly JsonObject _work;   // 編集用の複製。ObjectForm はこちらへ即時反映する

        // rarity のプルダウン候補（低→高）。データ上の値は "mythic"。
        private static readonly string[] Rarities =
        { "common", "rare", "magical", "epic", "legendary", "mythic" };

        public ItemEditDialog(string id, JsonObject item)
        {
            _item = item;
            _work = (JsonObject)item.DeepClone();
            Text = I18n.T("title.editField", id);
            Width = 560; Height = 660;
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

            // image_src は画像一覧ダイアログ（ImagePickerDialog）から選べるようにする。
            _form.ImageSrcPickerEnabled = true;

            // item_type 変更・item_detail 選択時に、item_detail に対応する画像の先頭（最も若い番号）を image_src へ充てる。
            _form.DefaultImageForDetail = detail => ImagePickerDialog.LowestImageForDetail(Settings.Current?.GameAssetRoot ?? "", detail);

            _form.Dock = DockStyle.Fill;
            _form.Bind(_work);

            // item_type を変えたら attributes を新しい種別のスキーマへ作り替え、item_detail 候補も連動させる。
            // combo の値は通常 Apply() でしか書き戻らないため、ここで即 _work の item_type へ反映する。
            // ・入力途中（TextChanged）は item_detail 候補だけ追従（非破壊）。
            // ・種別が確定（一覧選択 / フォーカスアウト）かつ実際に変化したときだけ attributes を作り替える。
            //   入力途中に attributes を消さないため、確定タイミングに限定する。
            // consumable の品にはゲームの右クリック「消費」が出ない（出るのは healing_item だけ）。飲食物・薬を作るときに知らせる。
            var consumableNote = new Label
            {
                Text = I18n.T("item.consumableNote"), Dock = DockStyle.Top, AutoSize = false, Height = 36,
                ForeColor = Color.DarkGoldenrod, Padding = new Padding(4, 4, 4, 0),
            };
            void SyncNote() => consumableNote.Visible = J.Str(_work, "item_type") == "consumable";
            SyncNote();

            var typeCombo = _form.GetCombo("item_type");
            if (typeCombo != null)
            {
                string lastType = J.Str(_work, "item_type");
                void ApplySchema()
                {
                    string t = typeCombo.Text.Trim();
                    _work["item_type"] = t;
                    if (t == lastType) return;
                    lastType = t;
                    _form.ApplyAttributeSchema();
                }
                typeCombo.TextChanged += (_, _) => { _work["item_type"] = typeCombo.Text.Trim(); _form.RefreshAttributeDetailOptions(); SyncNote(); };
                typeCombo.SelectedIndexChanged += (_, _) => ApplySchema();
                typeCombo.Leave += (_, _) => ApplySchema();
            }

            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) =>
            {
                if (!_form.Apply()) { DialogResult = DialogResult.None; return; }
                // 複製の内容を元アイテムへ書き戻す（同一インスタンスを保ったまま中身だけ差し替える）。
                _item.Clear();
                foreach (var kv in _work) _item[kv.Key] = kv.Value?.DeepClone();
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);

            Controls.Add(_form);
            Controls.Add(consumableNote);
            Controls.Add(bar);
            AcceptButton = ok; CancelButton = cancel;
        }
    }

    // image_src を画像一覧から選ぶダイアログ。
    // 左にフォルダ一覧、右に選択フォルダ内のサムネイル一覧を表示し、ダブルクリック/OK で確定する。
    // 走査基準は GameAssetRoot 配下（既定 Assets/images/item_candidates_dark、無ければ Assets/images、それも無ければ直下）。
    // ResultPath に GameAssetRoot からの相対パス（/区切り。例 Assets/images/item_candidates_dark/accessory/1.png）を返す。
    internal sealed class ImagePickerDialog : Form
    {
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp" };

        private readonly string _assetRoot;
        private readonly string _itemsRoot;        // フォルダ一覧の走査基準
        private readonly string _preselect;        // 現在値（相対パス）。一致する画像を初期選択する
        private readonly List<(string root, string itemsDir)> _dirs;   // 画像の置き場（基準フォルダ, 品種フォルダの親）
        private readonly ListBox _folders = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly ListView _images = new() { Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false };
        private ImageList _thumbs;
        private readonly List<Image> _live = new();   // ImageList が参照する実体。遅延実体化されるため破棄まで保持する
        // 選んだ画像の拡大表示（ドット絵なので整数倍・最近傍で描く）と、その情報（名前・大きさ・置き場）。
        private readonly PixelPreview _preview = new() { Dock = DockStyle.Fill };
        private readonly Label _previewInfo = new() { Dock = DockStyle.Bottom, Height = 92, Padding = new Padding(2, 6, 2, 0), AutoEllipsis = true };
        private readonly Dictionary<string, (string full, bool mod)> _fileOf = new(StringComparer.OrdinalIgnoreCase);   // 相対パス → 実ファイル

        // 選択された画像の GameAssetRoot からの相対パス（未選択は null）。
        public string ResultPath { get; private set; }

        public ImagePickerDialog(string assetRoot, string current)
        {
            _assetRoot = assetRoot ?? "";
            _itemsRoot = ResolveItemsRoot(_assetRoot);
            _preselect = current;
            // ゲーム導入先に加え、ModLoader（装備画像 MOD）の絵の置き場も一覧に混ぜる。
            // 同じ品種のフォルダは1つにまとめ、絵の相対パスはどの置き場でもゲームと同じ形になる。
            _dirs = ModLoaderLink.ItemImageDirs().ToList();
            if (_dirs.Count == 0 && !string.IsNullOrEmpty(_itemsRoot)) _dirs.Add((_assetRoot, _itemsRoot));

            Text = I18n.T("imgpick.title");
            Width = 1180; Height = 640;
            StartPosition = FormStartPosition.CenterParent;

            // 左: 品種フォルダ / 中: サムネイル一覧 / 右: 選んだ画像の拡大と情報。
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            layout.Controls.Add(_folders, 0, 0);
            layout.Controls.Add(_images, 1, 0);
            var previewBox = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 0) };
            previewBox.Controls.Add(_preview);
            previewBox.Controls.Add(_previewInfo);
            layout.Controls.Add(previewBox, 2, 0);

            _folders.SelectedIndexChanged += (_, _) => LoadFolderImages();
            _images.SelectedIndexChanged += (_, _) => ShowPreview();
            // 今の image_src の画像を選んでおく。画面ができる前の選択は ListView に残らないので、出した後で選び直す。
            Shown += (_, _) => SelectPreselect();
            _images.ItemActivate += (_, _) => { if (TrySelect()) DialogResult = DialogResult.OK; };

            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => { if (!TrySelect()) DialogResult = DialogResult.None; };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);

            Controls.Add(layout);
            Controls.Add(bar);
            AcceptButton = ok; CancelButton = cancel;

            LoadFolders();
        }

        // 走査基準フォルダを決める。アイテム画像の標準的な配置を優先し、無ければ広げる。
        private static string ResolveItemsRoot(string assetRoot)
        {
            if (string.IsNullOrEmpty(assetRoot)) return assetRoot;
            foreach (var sub in new[] { Path.Combine("Assets", "images", "item_candidates_dark"), Path.Combine("Assets", "images") })
            {
                string p = Path.Combine(assetRoot, sub);
                if (Directory.Exists(p)) return p;
            }
            return assetRoot;
        }

        private static bool IsImage(string f) => ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant());

        // item_detail（=画像フォルダ名）に対応するフォルダ内で最も若い番号の画像の相対パスを返す。
        // 例: detail="plant" → "Assets/images/item_candidates_dark/plant/1.png"。見つからなければ空文字。
        public static string LowestImageForDetail(string assetRoot, string detail)
        {
            if (string.IsNullOrEmpty(assetRoot) || string.IsNullOrEmpty(detail)) return "";
            try
            {
                string dir = Path.Combine(assetRoot, "Assets", "images", "item_candidates_dark", detail);
                if (!Directory.Exists(dir)) return "";
                string best = null;
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (IsImage(f) && (best == null || NaturalCompare(f, best) < 0)) best = f;
                if (best == null) return "";
                return Path.GetRelativePath(assetRoot, best).Replace('\\', '/');
            }
            catch { return ""; }
        }

        // フォルダ直下に画像ファイルが1つでもあるか。
        private static bool HasImage(string dir)
        {
            try { foreach (var f in Directory.EnumerateFiles(dir)) if (IsImage(f)) return true; }
            catch { /* アクセス不可は無視 */ }
            return false;
        }

        // 画像を含むフォルダを itemsRoot からの相対名で一覧化し、現在値のフォルダを初期選択する。
        private void LoadFolders()
        {
            var rel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, items) in _dirs)
            {
                try
                {
                    if (!Directory.Exists(items)) continue;
                    if (HasImage(items)) rel.Add(".");   // 直下にも画像があれば先頭に
                    foreach (var dir in Directory.EnumerateDirectories(items, "*", SearchOption.AllDirectories))
                        if (HasImage(dir)) rel.Add(Path.GetRelativePath(items, dir).Replace('\\', '/'));
                }
                catch { /* 走査失敗時はその置き場を飛ばす */ }
            }
            var relList = rel.ToList();
            relList.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var r in relList) _folders.Items.Add(r);

            int sel = 0;
            if (!string.IsNullOrEmpty(_preselect))
            {
                try
                {
                    string curDir = Path.GetDirectoryName(Path.Combine(_assetRoot, _preselect.Replace('/', Path.DirectorySeparatorChar))) ?? "";
                    string curRel = Path.GetRelativePath(_itemsRoot, curDir).Replace('\\', '/');
                    int idx = _folders.Items.IndexOf(curRel);
                    if (idx >= 0) sel = idx;
                }
                catch { /* 解決失敗時は先頭 */ }
            }
            if (_folders.Items.Count > 0) _folders.SelectedIndex = sel;   // LoadFolderImages を誘発
        }

        // 選択フォルダ内の画像をサムネイルで一覧化する。現在値に一致する画像があれば選択して可視化する。
        private void LoadFolderImages()
        {
            if (_folders.SelectedItem is not string relFolder) return;

            UseWaitCursor = true;
            _images.BeginUpdate();
            try
            {
                _images.Items.Clear();
                var oldThumbs = _thumbs;
                var oldLive = _live.ToList();
                _live.Clear();
                _thumbs = new ImageList { ImageSize = new Size(56, 56), ColorDepth = ColorDepth.Depth32Bit };
                _images.LargeImageList = _thumbs;

                // 置き場ごとの同じ品種フォルダの絵を集める。相対パスが同じ絵は先の置き場（ゲーム）を採る（ゲームと同じ順）。
                var files = new List<(string full, string rel, bool mod)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (root, items) in _dirs)
                {
                    string dir = relFolder == "." ? items : Path.Combine(items, relFolder.Replace('/', Path.DirectorySeparatorChar));
                    var here = new List<string>();
                    try { if (Directory.Exists(dir)) foreach (var f in Directory.EnumerateFiles(dir)) if (IsImage(f)) here.Add(f); }
                    catch { /* アクセス不可は空 */ }
                    here.Sort(NaturalCompare);
                    foreach (var f in here)
                    {
                        string relPath = RelFrom(root, f);
                        if (seen.Add(relPath)) files.Add((f, relPath, ModLoaderLink.IsModRoot(root)));
                    }
                }

                _fileOf.Clear();
                int i = 0;
                foreach (var (f, relPath, mod) in files)
                {
                    _fileOf[relPath] = (f, mod);
                    string key = i.ToString();
                    var thumb = MakeThumb(f, 56);
                    if (thumb != null) { _thumbs.Images.Add(key, thumb); _live.Add(thumb); }   // 破棄は次回再構築/Dispose で
                    string label = Path.GetFileNameWithoutExtension(f) + (mod ? " [MOD]" : "");
                    _images.Items.Add(new ListViewItem(label) { ImageKey = thumb != null ? key : null, Tag = relPath });
                    i++;
                }
                oldThumbs?.Dispose();
                foreach (var im in oldLive) im.Dispose();

                if (!string.IsNullOrEmpty(_preselect))
                    foreach (ListViewItem it in _images.Items)
                        if (string.Equals(it.Tag as string, _preselect, StringComparison.OrdinalIgnoreCase))
                        { it.Selected = true; it.EnsureVisible(); break; }
            }
            finally { _images.EndUpdate(); UseWaitCursor = false; }
            ShowPreview();   // フォルダを替えたら拡大表示も合わせる（選択が無ければ空にする）
        }

        // ファイル名（数字なら数値順、その他は文字列順）で比較する。1,2,10... を期待通りに並べる。
        private static int NaturalCompare(string a, string b)
        {
            string na = Path.GetFileNameWithoutExtension(a), nb = Path.GetFileNameWithoutExtension(b);
            if (int.TryParse(na, out int va) && int.TryParse(nb, out int vb)) return va.CompareTo(vb);
            return string.Compare(na, nb, StringComparison.OrdinalIgnoreCase);
        }

        // size×size の正方サムネイル（アスペクト維持・中央寄せ）を生成する。読み込み失敗時は null。
        // 返した Bitmap は ImageList が遅延実体化するまで保持が必要なため、呼び出し側で破棄管理する。
        private static Image MakeThumb(string path, int size)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                using var full = Image.FromStream(ms);
                var bmp = new Bitmap(size, size);
                using var g = Graphics.FromImage(bmp);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                double scale = Math.Min((double)size / full.Width, (double)size / full.Height);
                int w = Math.Max(1, (int)(full.Width * scale)), h = Math.Max(1, (int)(full.Height * scale));
                g.DrawImage(full, (size - w) / 2, (size - h) / 2, w, h);
                return bmp;
            }
            catch { return null; }
        }

        // フルパス→置き場（基準フォルダ）からの相対パス（/区切り）。どの置き場でも image_src と同じ形になる。
        private static string RelFrom(string root, string full)
        {
            try { return Path.GetRelativePath(root, full).Replace('\\', '/'); }
            catch { return full.Replace('\\', '/'); }
        }

        // 今の image_src（_preselect）の画像を選び、見える位置へ送って拡大表示する。
        private void SelectPreselect()
        {
            if (!string.IsNullOrEmpty(_preselect))
                foreach (ListViewItem it in _images.Items)
                    if (string.Equals(it.Tag as string, _preselect, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Selected = true;
                        it.Focused = true;
                        it.EnsureVisible();
                        break;
                    }
            ShowPreview();
        }

        // 選んだ画像を右に大きく出す。名前・相対パス・大きさ（ピクセル）・置き場（ゲーム／MOD）も添える。
        private void ShowPreview()
        {
            if (_images.SelectedItems.Count == 0 || _images.SelectedItems[0].Tag is not string rel || !_fileOf.TryGetValue(rel, out var file))
            {
                _preview.SetImage(null);
                _previewInfo.Text = "";
                return;
            }
            Image img = null;
            try
            {
                using var ms = new MemoryStream(File.ReadAllBytes(file.full));
                using var tmp = Image.FromStream(ms);
                img = new Bitmap(tmp);   // ファイルを掴まないよう複製を持つ
            }
            catch { }
            _preview.SetImage(img);
            _previewInfo.Text = I18n.T("imgpick.previewInfo",
                Path.GetFileName(file.full),
                img != null ? $"{img.Width}×{img.Height}" : "-",
                I18n.T(file.mod ? "imgpick.sourceMod" : "imgpick.sourceGame"),
                rel);
        }

        // 現在の選択を ResultPath へ確定する。未選択なら false。
        private bool TrySelect()
        {
            if (_images.SelectedItems.Count == 0) return false;
            ResultPath = _images.SelectedItems[0].Tag as string;
            return !string.IsNullOrEmpty(ResultPath);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _thumbs?.Dispose();
                foreach (var im in _live) im.Dispose();
                _live.Clear();
                _preview.SetImage(null);
            }
            base.Dispose(disposing);
        }
    }

    // ドット絵の拡大表示。枠に収まる最大の整数倍で、最近傍補間（ぼかさない）で中央に描く。
    // 絵の後ろは市松模様（透明な部分が分かるように）。
    internal sealed class PixelPreview : Control
    {
        private Image _img;

        public PixelPreview()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(40, 40, 40);
        }

        // 表示する画像を差し替える（前の画像は破棄する。渡した画像の破棄もこちらが受け持つ）。
        public void SetImage(Image img)
        {
            _img?.Dispose();
            _img = img;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (_img == null) return;
            int avail = Math.Min(ClientSize.Width, ClientSize.Height) - 8;
            int scale = Math.Max(1, Math.Min(avail / Math.Max(1, _img.Width), avail / Math.Max(1, _img.Height)));
            int w = _img.Width * scale, h = _img.Height * scale;
            var dst = new Rectangle((ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            // 市松模様の地
            using (var a = new SolidBrush(Color.FromArgb(70, 70, 70)))
            using (var b = new SolidBrush(Color.FromArgb(55, 55, 55)))
            {
                const int cell = 8;
                for (int y = dst.Top; y < dst.Bottom; y += cell)
                    for (int x = dst.Left; x < dst.Right; x += cell)
                        g.FillRectangle((((x - dst.Left) / cell + (y - dst.Top) / cell) % 2 == 0) ? a : b,
                            x, y, Math.Min(cell, dst.Right - x), Math.Min(cell, dst.Bottom - y));
            }
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_img, dst);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _img?.Dispose(); _img = null; }
            base.Dispose(disposing);
        }
    }
}

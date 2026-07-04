// NPC選択時に look_description の直下へ差し込む画像パネル。
// 顔画像 + 立ち絵3種の表示、顔画像の取込み・矩形切り抜き指定を提供する。
// 立ち絵の「使用する」はゲームが読む reduced_color_image.png へ反映する。
using System.Drawing.Imaging;

namespace InstantaleSaveEditor
{
    internal sealed class NpcImagePanel : Panel
    {
        // ---------------- サイズ定数 ----------------
        private const int FaceW = 120, FaceH = 160;
        private const int StandW = 162, StandH = 324;
        private const int ColGap = 10;

        // 立ち絵の定義。インデックス0がゲーム使用ファイル(reduced_color_image.png)。
        // 末尾(ExternalIdx)は外部から取り込んだ任意画像を保持する枠。
        private static readonly string[] StandFiles =
            { "reduced_color_image.png", "pixelated_image_original.png", "no_bg_image.png", "external_image.png" };
        // 立ち絵ラベルの i18n キー（表示時に T() で解決する）。
        private static readonly string[] StandLabelKeys =
            { "npc.stand.reduced", "npc.stand.pixelated", "npc.stand.original", "npc.stand.external" };
        private static readonly int StandCount = StandFiles.Length;
        // 外部読み込み枠のインデックス（取込ボタンを持つ）。
        private static readonly int ExternalIdx = StandFiles.Length - 1;

        // 元の縮小色を退避する固定バックアップ名。縮小色枠はこれを参照する。
        private const string BackupFile = "reduced_color_image_bk.png";

        // ---------------- ウィジェット ----------------
        private readonly PictureBox _pbFace = MakePb(FaceW, FaceH);
        private readonly PictureBox[] _pbStand;
        private readonly Button[] _useBtns;

        // ---------------- 状態 ----------------
        private int _activeIdx = 0;
        private string _charDir;

        public NpcImagePanel()
        {
            _pbStand = new PictureBox[StandCount];
            _useBtns = new Button[StandCount];
            for (int i = 0; i < StandCount; i++)
                _pbStand[i] = MakePb(StandW, StandH);
            Height = 22 + StandH + 34 + 16;
            BackColor = SystemColors.Control;
            Padding = new Padding(8, 6, 8, 6);
            Build();
        }

        private static PictureBox MakePb(int w, int h) => new()
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = w, Height = h,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(220, 220, 220),
            Cursor = Cursors.Hand,
        };

        private void Build()
        {
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
            };
            flow.Controls.Add(BuildFaceColumn());
            for (int i = 0; i < StandCount; i++)
                flow.Controls.Add(BuildStandColumn(i));
            Controls.Add(flow);
        }

        private Panel BuildFaceColumn()
        {
            const int btnY = 22 + FaceH + 4;          // 顔画像の直下
            int useY = 22 + StandH + 4;               // 立ち絵列の「使用する」と同じ高さ
            var p = new Panel
            {
                Width = FaceW + 4,
                Height = useY + 30,
                Margin = new Padding(0, 0, ColGap, 0),
            };
            p.Controls.Add(new Label { Text = I18n.T("npc.face"), AutoSize = true, Location = new Point(0, 2) });
            _pbFace.Location = new Point(0, 22);
            ImageViewer.RegisterClickViewer(_pbFace, I18n.T("npc.face"));
            p.Controls.Add(_pbFace);

            // 顔画像指定は顔画像の直下。
            var btnCrop = new Button { Text = I18n.T("npc.cropFace"), Width = FaceW, Height = 26, Location = new Point(0, btnY) };
            btnCrop.Click += (_, _) => CropFace();
            p.Controls.Add(btnCrop);

            // 下部に取込み系を縦に並べ、最下段を「使用する」の高さに揃える。
            var btnImport = new Button { Text = I18n.T("npc.import"), Width = FaceW, Height = 26, Location = new Point(0, useY - 60) };
            btnImport.Click += (_, _) => ImportFace();
            p.Controls.Add(btnImport);

            var btnStandImport = new Button { Text = I18n.T("npc.importStand"), Width = FaceW, Height = 26, Location = new Point(0, useY - 30) };
            btnStandImport.Click += (_, _) => ImportExternal();
            p.Controls.Add(btnStandImport);

            var btnFolder = new Button { Text = I18n.T("npc.openFolder"), Width = FaceW, Height = 26, Location = new Point(0, useY) };
            btnFolder.Click += (_, _) => OpenCharDir();
            p.Controls.Add(btnFolder);

            return p;
        }

        // 画像が収められているキャラクターフォルダをエクスプローラーで開く。
        private void OpenCharDir()
        {
            if (string.IsNullOrEmpty(_charDir) || !Directory.Exists(_charDir)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _charDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private Panel BuildStandColumn(int idx)
        {
            const int btnY = 22 + StandH + 4;
            var p = new Panel
            {
                Width = StandW + 4,
                Height = btnY + 30,
                Margin = new Padding(0, 0, ColGap, 0),
            };
            p.Controls.Add(new Label { Text = I18n.T(StandLabelKeys[idx]), AutoSize = true, Location = new Point(0, 2) });
            _pbStand[idx].Location = new Point(0, 22);
            int cap = idx;
            ImageViewer.RegisterClickViewer(_pbStand[idx], I18n.T(StandLabelKeys[idx]));
            p.Controls.Add(_pbStand[idx]);

            var btn = new Button { Text = I18n.T("npc.use"), Width = StandW, Height = 26, Location = new Point(0, btnY) };
            // 外部読み込み列は画像が取り込まれるまで押せない（既定で無効、UpdateButtonStyles が確定させる）。
            if (idx == ExternalIdx) btn.Enabled = false;
            btn.Click += (_, _) => SetActive(cap);
            _useBtns[idx] = btn;
            p.Controls.Add(btn);

            return p;
        }

        // アクティブな立ち絵を切り替え、ボタン外観で示す。
        // idx0(縮小色)は元の reduced_color_image.png へ復元、idx1以上はその画像を反映する。
        private void SetActive(int idx)
        {
            _activeIdx = idx;
            UpdateButtonStyles();
            if (string.IsNullOrEmpty(_charDir)) return;
            if (idx == 0) RestoreReducedColor();
            else ApplyToReducedColor(idx);
        }

        private void UpdateButtonStyles()
        {
            for (int i = 0; i < StandCount; i++)
            {
                if (_useBtns[i] == null) continue;
                _useBtns[i].BackColor = i == _activeIdx ? SystemColors.Highlight : SystemColors.Control;
                _useBtns[i].ForeColor = i == _activeIdx ? SystemColors.HighlightText : SystemColors.ControlText;
                _useBtns[i].FlatStyle = i == _activeIdx ? FlatStyle.Flat : FlatStyle.Standard;
            }
            // 外部読み込み枠はプレビューに画像が読めている時だけ「使用する」を押せる。
            if (_useBtns[ExternalIdx] != null)
                _useBtns[ExternalIdx].Enabled = _pbStand[ExternalIdx].Image != null;
        }

        // 指定立ち絵を reduced_color_image.png に反映する。
        // 元の縮小色は初回のみバックアップへ退避し、原本→ピクセル化等の連続切替でも失わない。
        private void ApplyToReducedColor(int idx)
        {
            string reduced = Path.Combine(_charDir, "reduced_color_image.png");
            string backup  = Path.Combine(_charDir, BackupFile);
            string source  = Path.Combine(_charDir, StandFiles[idx]);
            if (!File.Exists(source)) { MessageBox.Show(I18n.T("msg.imageNotFound"), I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            try
            {
                // 元の縮小色を一度だけ退避（既にバックアップがあれば上書きしない）
                if (File.Exists(reduced) && !File.Exists(backup))
                    File.Copy(reduced, backup);
                File.Copy(source, reduced, overwrite: true);
                // 縮小色枠はバックアップ（元の縮小色）を表示する
                LoadPb(_pbStand[0], StandDisplayPath(0));
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.applyFailed") + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 縮小色「使用する」: バックアップした元の reduced_color_image.png に復元する。
        private void RestoreReducedColor()
        {
            string reduced = Path.Combine(_charDir, "reduced_color_image.png");
            string backup  = Path.Combine(_charDir, BackupFile);
            if (!File.Exists(backup)) return; // 未変更なら何もしない
            try
            {
                File.Copy(backup, reduced, overwrite: true);
                LoadPb(_pbStand[0], backup);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.restoreFailed") + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 立ち絵サムネの表示元パス。縮小色(idx0)はバックアップがあればそれを参照する。
        private string StandDisplayPath(int idx)
        {
            if (string.IsNullOrEmpty(_charDir)) return null;
            if (idx == 0)
            {
                string backup = Path.Combine(_charDir, BackupFile);
                return File.Exists(backup) ? backup : Path.Combine(_charDir, StandFiles[0]);
            }
            return Path.Combine(_charDir, StandFiles[idx]);
        }

        // NPC切替時: キャラクターフォルダから4枚の画像を読み込む。
        // 「使用する」の状態は即時反映済みの reduced_color_image.png から判定して復元する。
        public void LoadImages(string charDir)
        {
            _charDir = charDir;
            LoadPb(_pbFace, charDir != null ? Path.Combine(charDir, "face_image.png") : null);
            for (int i = 0; i < StandCount; i++)
                LoadPb(_pbStand[i], StandDisplayPath(i));
            _activeIdx = DetectActiveIndex();
            UpdateButtonStyles();
        }

        // 現在 reduced_color_image.png に反映されている立ち絵のインデックスを判定する。
        // バックアップが無い（未変更）なら 0。実体を立ち絵ファイルとバイト比較して一致する枠を返す。
        private int DetectActiveIndex()
        {
            if (string.IsNullOrEmpty(_charDir)) return 0;
            string reduced = Path.Combine(_charDir, "reduced_color_image.png");
            string backup  = Path.Combine(_charDir, BackupFile);
            if (!File.Exists(backup) || !File.Exists(reduced)) return 0;
            try
            {
                byte[] cur = File.ReadAllBytes(reduced);
                for (int i = 1; i < StandFiles.Length; i++)   // idx0(縮小色)は比較不要
                {
                    string p = Path.Combine(_charDir, StandFiles[i]);
                    if (File.Exists(p) && cur.AsSpan().SequenceEqual(File.ReadAllBytes(p))) return i;
                }
            }
            catch { }
            return 0;
        }

        // ファイルをバイト列経由で読み込み、GDI+ のファイルロックを回避する。
        private static void LoadPb(PictureBox pb, string path)
        {
            pb.Image?.Dispose();
            pb.Image = null;
            if (path == null || !File.Exists(path)) return;
            try { pb.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); }
            catch { }
        }

        private void ImportFace()
        {
            if (string.IsNullOrEmpty(_charDir)) return;
            using var dlg = new OpenFileDialog
            {
                Title = I18n.T("npc.selectFace"),
                Filter = I18n.T("filter.image"),
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            string dest = Path.Combine(_charDir, "face_image.png");
            try
            {
                using var img = Image.FromFile(dlg.FileName);
                img.Save(dest, ImageFormat.Png);
                LoadPb(_pbFace, dest);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.importFailed") + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // 外部読み込み「取込」: 指定画像を external_image.png として取り込み、外部枠に表示する。
        // ゲームへの反映は別途その列の「使用する」で行う（取込のみではゲーム使用ファイルに触れない）。
        private void ImportExternal()
        {
            if (string.IsNullOrEmpty(_charDir)) return;
            using var dlg = new OpenFileDialog
            {
                Title = I18n.T("npc.selectStand"),
                Filter = I18n.T("filter.image"),
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            string dest = Path.Combine(_charDir, StandFiles[ExternalIdx]);
            try
            {
                using (var img = Image.FromFile(dlg.FileName))
                    img.Save(dest, ImageFormat.Png);
                LoadPb(_pbStand[ExternalIdx], dest);
                UpdateButtonStyles(); // 取込済みになったので「使用する」を有効化

            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.importFailed") + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CropFace()
        {
            if (string.IsNullOrEmpty(_charDir)) return;
            string src  = Path.Combine(_charDir, StandFiles[_activeIdx]);
            if (!File.Exists(src)) { MessageBox.Show(I18n.T("msg.standNotFound"), I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string dest = Path.Combine(_charDir, "face_image.png");
            try
            {
                using var bmp = new Bitmap(src);
                using var dlg = new FaceCropDialog(bmp);
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK && !dlg.CropRect.IsEmpty)
                {
                    using var crop = bmp.Clone(dlg.CropRect, bmp.PixelFormat);
                    crop.Save(dest, ImageFormat.Png);
                    LoadPb(_pbFace, dest);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.cropSaveFailed") + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // 画像上でドラッグ矩形を選択し、選択領域を CropRect として返すダイアログ。
    // 画像を原寸（スケール1:1）で表示し、スクロールで全体を確認できる。
    internal sealed class FaceCropDialog : Form
    {
        public Rectangle CropRect { get; private set; } = Rectangle.Empty;

        private readonly Bitmap _src;
        private Rectangle _sel = Rectangle.Empty;
        private Point _dragStart;
        private bool _dragging;
        private readonly Panel _canvas;

        public FaceCropDialog(Bitmap src)
        {
            _src = src;
            Text = I18n.T("npc.cropTitle");
            MinimizeBox = false; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
            Width  = Math.Min(src.Width  + 40, area.Width  - 40);
            Height = Math.Min(src.Height + 80, area.Height - 40);

            // キャンバスは原寸サイズ。スクロールで全体を確認できる。
            _canvas = new Panel { Width = src.Width, Height = src.Height, Cursor = Cursors.Cross };
            _canvas.Paint     += OnPaint;
            _canvas.MouseDown += (_, e) => { _dragging = true; _dragStart = e.Location; _sel = Rectangle.Empty; };
            _canvas.MouseMove += (_, e) => { if (_dragging) { _sel = NormRect(_dragStart, e.Location); _canvas.Invalidate(); } };
            _canvas.MouseUp   += (_, e) => { _dragging = false; _sel = NormRect(_dragStart, e.Location); _canvas.Invalidate(); };

            var scrollArea = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            scrollArea.Controls.Add(_canvas);

            var btnOk     = new Button { Text = I18n.T("btn.ok"),     Width = 80,  Dock = DockStyle.Right, DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = I18n.T("btn.cancel"), Width = 90, Dock = DockStyle.Right, DialogResult = DialogResult.Cancel };
            btnOk.Click += (_, _) =>
            {
                if (_sel.Width > 0 && _sel.Height > 0)
                    CropRect = Rectangle.Intersect(_sel, new Rectangle(0, 0, _src.Width, _src.Height));
            };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bar.Controls.Add(btnOk); bar.Controls.Add(btnCancel);

            Controls.Add(scrollArea); Controls.Add(bar);
            AcceptButton = btnOk; CancelButton = btnCancel;
        }

        private void OnPaint(object sender, PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(_src, Point.Empty);
            if (_sel.Width > 0 && _sel.Height > 0)
            {
                using var pen = new Pen(Color.Red, 2);
                e.Graphics.DrawRectangle(pen, _sel);
                using var br = new SolidBrush(Color.FromArgb(40, 255, 0, 0));
                e.Graphics.FillRectangle(br, _sel);
            }
        }

        private static Rectangle NormRect(Point a, Point b)
            => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
                   Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
    }
}

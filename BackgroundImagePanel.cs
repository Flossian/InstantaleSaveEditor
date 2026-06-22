// facility(ツリー末端のノード)選択時に、対応する背景画像を表示するパネル。
// 画像は worlds/{ワールド名}/backgrounds/{facility名}/image.png に配置されている。
// description 直後に差し込み、クリックで原寸表示する。
namespace InstantaleSaveEditor
{
    internal sealed class BackgroundImagePanel : Panel
    {
        private const int ThumbW = 480, ThumbH = 240;

        private readonly PictureBox _pb = new()
        {
            SizeMode = PictureBoxSizeMode.Zoom,
            Width = ThumbW, Height = ThumbH,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(220, 220, 220),
            Cursor = Cursors.Hand,
            Location = new Point(0, 22),
        };
        private readonly Label _caption = new() { AutoSize = true, Location = new Point(0, 2) };

        public BackgroundImagePanel()
        {
            Height = 22 + ThumbH + 8;
            BackColor = SystemColors.Control;
            Padding = new Padding(8, 6, 8, 6);
            _caption.Text = I18n.T("bg.caption");
            Controls.Add(_caption);
            Controls.Add(_pb);
            ImageViewer.RegisterClickViewer(_pb, I18n.T("bg.caption"));
        }

        // facility 名から backgrounds/{名前}/image.png を探して表示する。
        // 画像が無ければ枠を空にし、キャプションに「(なし)」を出す。
        public void LoadImage(string worldDir, string facilityName)
        {
            string path = null;
            if (!string.IsNullOrEmpty(worldDir) && !string.IsNullOrEmpty(facilityName))
            {
                string p = Path.Combine(worldDir, "backgrounds", facilityName, "image.png");
                if (File.Exists(p)) path = p;
            }
            _pb.Image?.Dispose();
            _pb.Image = null;
            if (path != null)
            {
                try { _pb.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); } catch { }
            }
            _caption.Text = _pb.Image != null ? I18n.T("bg.caption") : I18n.T("bg.captionNone");
        }
    }
}

// 多言語対応の中核。表示文言を外部/埋め込みのフラット JSON から解決する。
// - 言語ファイル: exe 隣の lang\<code>.json（settings.json と同じディレクトリ基準）
// - 既定言語: 埋め込み基底（ja/en）＋外部オーバーレイ（外部があればその値で上書き）
// - 解決順: activeDict[key] → jaDict[key] → key そのもの（未訳・欠落でも破綻しない）
// 既存のロジック・レイアウト・セーブ挙動は変更せず、文言の供給元のみを担う。
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class I18n
    {
        private static Dictionary<string, string> _active = new(StringComparer.Ordinal);  // 現在の言語辞書（埋め込み＋外部マージ）
        private static Dictionary<string, string> _ja = new(StringComparer.Ordinal);       // フォールバック用の ja 辞書
        private static string _code = "ja";                                                // 現在の言語コード

        // 言語が切り替わったときに発火する。開いているフォームは Localize() を再実行して即時反映する。
        public static event Action LanguageChanged;

        // 現在の言語コード。
        public static string CurrentCode => _code;

        // exe 隣の lang ディレクトリ。単一ファイル発行では Assembly.Location が空になるため ProcessPath を使う。
        public static string LangDir()
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "lang");
        }

        // 言語ファイルのフルパス。
        private static string LangPath(string code) => Path.Combine(LangDir(), code + ".json");

        // ---------------- 初期化 ----------------

        // 起動時に1回、最初のフォーム生成前に呼ぶ。初回書き出し → 辞書構築 → 言語確定。
        public static void Init(string code)
        {
            EnsureExternalSeed();             // lang\ja.json / en.json が無ければ埋め込みから書き出す
            _ja = BuildDict("ja");            // フォールバック辞書を先に用意
            SetLanguage(code);
        }

        // 言語を切り替えて辞書を再構築し、LanguageChanged を発火する。
        public static void SetLanguage(string code)
        {
            _code = string.IsNullOrWhiteSpace(code) ? "ja" : code.Trim();
            _active = BuildDict(_code);
            try { LanguageChanged?.Invoke(); } catch { /* 一部フォームの再描画失敗で全体を巻き込まない */ }
        }

        // ---------------- 解決 ----------------

        // キーを現在言語で解決する。activeDict → jaDict → key の順。
        public static string T(string key)
        {
            if (key == null) return "";
            if (_active != null && _active.TryGetValue(key, out var v)) return v;
            if (_ja != null && _ja.TryGetValue(key, out var j)) return j;
            return key;   // 未訳・欠落でもキー文字列を返して破綻させない
        }

        // キーの訳が存在する場合のみ返す（activeDict → jaDict。どちらにも無ければ null）。
        // 「訳があれば使い、無ければ別の表示にフォールバックする」用途（ObjectForm のフィールド名等）。
        public static string Opt(string key)
        {
            if (key == null) return null;
            if (_active != null && _active.TryGetValue(key, out var v)) return v;
            if (_ja != null && _ja.TryGetValue(key, out var j)) return j;
            return null;
        }

        // 引数付き解決。翻訳側は {0}{1}... を使う。書式不正時は素の文言を返す。
        public static string T(string key, params object[] args)
        {
            string s = T(key);
            if (args == null || args.Length == 0) return s;
            try { return string.Format(s, args); }
            catch { return s; }
        }

        // ---------------- 言語一覧 ----------------

        // 選択可能な言語の (コード, 表示名) 一覧。埋め込み(ja/en) ∪ lang\*.json を統合する。
        // 表示名は外部の "_meta.name" を優先、無ければ埋め込み、無ければコードそのもの。
        public static List<(string code, string name)> GetAvailableLanguages()
        {
            var codes = new List<string>();
            void AddCode(string c) { if (!string.IsNullOrWhiteSpace(c) && !codes.Contains(c)) codes.Add(c); }

            foreach (var c in EmbeddedCodes()) AddCode(c);   // 埋め込み（ja/en）を先頭に
            try
            {
                if (Directory.Exists(LangDir()))
                    foreach (var f in Directory.GetFiles(LangDir(), "*.json"))
                        AddCode(Path.GetFileNameWithoutExtension(f));
            }
            catch { /* 走査失敗時は埋め込みのみ */ }

            var list = new List<(string, string)>();
            foreach (var c in codes)
            {
                var d = BuildDict(c);
                string name = d != null && d.TryGetValue("_meta.name", out var n) && !string.IsNullOrWhiteSpace(n) ? n : c;
                list.Add((c, name));
            }
            return list;
        }

        // ---------------- 内部: 辞書構築 ----------------

        // 指定コードの辞書 = 埋め込み（あれば）に 外部 lang\<code>.json を上書きマージ。
        private static Dictionary<string, string> BuildDict(string code)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            MergeInto(dict, LoadEmbedded(code));     // 基底
            MergeInto(dict, LoadExternalFile(code)); // 外部で上書き
            return dict;
        }

        // src の各キーを dest へ上書きコピーする（null は無視）。
        private static void MergeInto(Dictionary<string, string> dest, Dictionary<string, string> src)
        {
            if (src == null) return;
            foreach (var kv in src) dest[kv.Key] = kv.Value;
        }

        // フラット JSON（キー→文字列）をパースする。文字列以外の値は無視する。例外時は null。
        private static Dictionary<string, string> ParseFlat(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                if (JsonNode.Parse(json) is not JsonObject obj) return null;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in obj)
                    if (kv.Value is JsonValue jv && jv.TryGetValue<string>(out var s))
                        d[kv.Key] = s;
                return d;
            }
            catch { return null; }
        }

        // 埋め込みリソースから言語辞書を読む（無ければ null）。
        private static Dictionary<string, string> LoadEmbedded(string code)
        {
            try
            {
                var asm = typeof(I18n).Assembly;
                string suffix = "lang." + code + ".json";
                string name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using var st = asm.GetManifestResourceStream(name);
                if (st == null) return null;
                using var sr = new StreamReader(st, Encoding.UTF8);
                return ParseFlat(sr.ReadToEnd());
            }
            catch { return null; }
        }

        // 外部 lang\<code>.json を読む（無ければ null）。
        private static Dictionary<string, string> LoadExternalFile(string code)
        {
            try
            {
                string p = LangPath(code);
                if (!File.Exists(p)) return null;
                return ParseFlat(File.ReadAllText(p, Encoding.UTF8));
            }
            catch { return null; }
        }

        // 埋め込まれている言語コードの一覧（lang.<code>.json リソース名から抽出）。
        private static IEnumerable<string> EmbeddedCodes()
        {
            var result = new List<string>();
            try
            {
                foreach (var n in typeof(I18n).Assembly.GetManifestResourceNames())
                {
                    // 末尾が "lang.<code>.json" のものからコード部分を取り出す。
                    const string head = "lang.";
                    const string tail = ".json";
                    if (!n.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) continue;
                    int idx = n.LastIndexOf(head, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;
                    string code = n.Substring(idx + head.Length, n.Length - tail.Length - (idx + head.Length));
                    if (!string.IsNullOrWhiteSpace(code) && !code.Contains('.')) result.Add(code);
                }
            }
            catch { /* 失敗時は既定の ja/en にフォールバック */ }
            if (result.Count == 0) { result.Add("ja"); result.Add("en"); }
            // ja を先頭にする（既定言語）。
            result.Sort((a, b) => a == "ja" ? -1 : b == "ja" ? 1 : string.CompareOrdinal(a, b));
            return result;
        }

        // 初回書き出し: lang\ が無ければ作成し、ja.json / en.json が無ければ埋め込みから書き出す。
        // 既存ファイルは上書きしない。書込不可でも致命的にせず埋め込みのみで継続する。
        private static void EnsureExternalSeed()
        {
            try
            {
                Directory.CreateDirectory(LangDir());
                foreach (var code in EmbeddedCodes())
                    SeedOne(code);
            }
            catch { /* lang\ 作成失敗（書込不可など）でも埋め込みで継続 */ }
        }

        // 1言語分のテンプレートを書き出す（既存があれば触らない）。埋め込み原文をそのまま整形して保存する。
        private static void SeedOne(string code)
        {
            try
            {
                string p = LangPath(code);
                if (File.Exists(p)) return;
                var asm = typeof(I18n).Assembly;
                string suffix = "lang." + code + ".json";
                string name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (name == null) return;
                using var st = asm.GetManifestResourceStream(name);
                if (st == null) return;
                using var sr = new StreamReader(st, Encoding.UTF8);
                File.WriteAllText(p, sr.ReadToEnd(), new UTF8Encoding(false));
            }
            catch { /* 1言語の書き出し失敗は無視して継続 */ }
        }
    }
}

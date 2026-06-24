// 列挙的フィールド（NPC の category / job など）の候補テンプレートを供給する。
// 候補値はソースに直書きせず、JSON から読む:
//   - 既定: 埋め込みリソース field_options.json（サンプルデータから抽出した固定候補）
//   - 上書き: exe 隣の外部 field_options.json（あればフィールド単位で差し替え）
// { "フィールド名": ["候補1", "候補2", ...], ... } 形式。外部ファイルを編集すれば
// 候補の追加や他フィールドへの汎用化ができる（再ビルド不要）。
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class FieldOptions
    {
        private static Dictionary<string, string[]> _map;   // フィールド名 → 候補配列（初回アクセス時に構築）

        // exe 隣の外部ファイルパス。単一ファイル発行では Assembly.Location が空のため ProcessPath を使う。
        private static string ExternalPath()
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "field_options.json");
        }

        // 指定フィールドの候補一覧。未定義なら空配列（呼び出し側は自由入力として扱える）。
        public static IReadOnlyList<string> Get(string field)
        {
            _map ??= Build();
            return _map.TryGetValue(field, out var v) ? v : Array.Empty<string>();
        }

        // 埋め込み既定に外部ファイルをフィールド単位で上書きマージして候補表を作る。
        private static Dictionary<string, string[]> Build()
        {
            EnsureExternalSeed();
            var dict = new Dictionary<string, string[]>(StringComparer.Ordinal);
            MergeInto(dict, Parse(LoadEmbedded()));   // 既定
            MergeInto(dict, Parse(LoadExternal()));   // 外部で上書き
            return dict;
        }

        private static void MergeInto(Dictionary<string, string[]> dest, Dictionary<string, string[]> src)
        {
            if (src == null) return;
            foreach (var kv in src) dest[kv.Key] = kv.Value;
        }

        // { フィールド名: [文字列...] } をパースする。配列でない値・非文字列要素は無視。例外時は null。
        private static Dictionary<string, string[]> Parse(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;
                if (JsonNode.Parse(json) is not JsonObject obj) return null;
                var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var kv in obj)
                {
                    if (kv.Value is not JsonArray arr) continue;
                    var list = new List<string>();
                    foreach (var n in arr)
                        if (n is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                            list.Add(s);
                    d[kv.Key] = list.ToArray();
                }
                return d;
            }
            catch { return null; }
        }

        // 埋め込みリソース field_options.json を読む（無ければ null）。
        private static string LoadEmbedded()
        {
            try
            {
                var asm = typeof(FieldOptions).Assembly;
                string name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("field_options.json", StringComparison.OrdinalIgnoreCase));
                if (name == null) return null;
                using var st = asm.GetManifestResourceStream(name);
                if (st == null) return null;
                using var sr = new StreamReader(st, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // exe 隣の外部 field_options.json を読む（無ければ null）。
        private static string LoadExternal()
        {
            try
            {
                string p = ExternalPath();
                return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
            }
            catch { return null; }
        }

        // 外部ファイルが無ければ埋め込み既定を書き出す（編集して候補を拡張できるように）。既存は触らない。
        private static void EnsureExternalSeed()
        {
            try
            {
                string p = ExternalPath();
                if (File.Exists(p)) return;
                string seed = LoadEmbedded();
                if (seed != null) File.WriteAllText(p, seed, new UTF8Encoding(false));
            }
            catch { /* 書込不可でも埋め込みで継続 */ }
        }
    }
}

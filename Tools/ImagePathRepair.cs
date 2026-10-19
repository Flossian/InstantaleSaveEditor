// 立ち絵のパス（image_src）の張り替え。
//
// image_src は書いた PC の絶対パスで入っているため、別の PC で作られた世界を持ってくると
// 他人のユーザーフォルダを指したまま画像が見つからない（パーティ欄の顔が白い枠になる等）。
// ゲーム自身は、その人物に話しかけたとき（会話の始まり）に手元の場所へ繋ぎ直すだけで、
// 読み込み時にまとめて直すことはしない。ここでは全 NPC とプレイヤーをまとめて張り替える。
//
// 張り替え先は、元のパスの「\worlds\」より後ろを手元の worlds フォルダへ繋いだもの。
// それで見つからなければ、手元の characters\{名前}\ に同じファイル名があればそれを使う。
// どちらも無いパスは書き換えない（存在しないパスを別の存在しないパスに変えても意味がないため）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class ImagePathRepair
    {
        public readonly record struct Result(int Fixed, int Unresolved, int Characters);

        // worldDir: 手元の worlds\{スロット}\（WorldTab.ResolveWorldDir の結果）。
        public static Result Run(JsonObject root, string worldDir)
        {
            int fixedCount = 0, unresolved = 0, chars = 0;
            if (root == null || string.IsNullOrEmpty(worldDir)) return new Result(0, 0, 0);
            string worldsRoot = Path.GetDirectoryName(worldDir);
            string charsDir = Path.Combine(worldDir, "characters");

            void Repair(JsonObject character)
            {
                if (character == null) return;
                string name = J.Str(character, "name");
                int before = fixedCount;
                switch (character["image_src"])
                {
                    case JsonObject src:
                        foreach (var key in src.Select(p => p.Key).ToList())
                            if (Fix(src[key], name) is string np) { src[key] = np; fixedCount++; }
                        break;
                    case JsonValue v:
                        if (Fix(v, name) is string single) { character["image_src"] = single; fixedCount++; }
                        break;
                }
                if (fixedCount > before) chars++;
            }

            // 張り替え先のパス。張り替え不要・見つからないときは null（見つからないものは unresolved に数える）。
            string Fix(JsonNode node, string name)
            {
                if (node is not JsonValue v || !v.TryGetValue<string>(out var path) || string.IsNullOrEmpty(path)) return null;
                if (!Path.IsPathRooted(path) || File.Exists(path)) return null;
                string candidate = null;
                int i = path.IndexOf(@"\worlds\", StringComparison.OrdinalIgnoreCase);
                if (i < 0) i = path.IndexOf("/worlds/", StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && worldsRoot != null)
                {
                    string tail = path.Substring(i + 8).Replace('/', Path.DirectorySeparatorChar);
                    string c = SafePath.ResolveUnder(worldsRoot, tail);
                    if (c != null && File.Exists(c)) candidate = c;
                }
                if (candidate == null && name.Length > 0)
                {
                    string c = Path.Combine(SafePath.GameDir(charsDir, name), Path.GetFileName(path));
                    if (File.Exists(c)) candidate = c;
                }
                if (candidate == null) unresolved++;
                return candidate;
            }

            if (J.Obj(root, "npcs") is JsonObject npcs)
                foreach (var kv in npcs) Repair(kv.Value as JsonObject);
            Repair(J.Obj(root, "player_data"));
            return new Result(fixedCount, unresolved, chars);
        }
    }
}

// ワールド素データ（worlds\{スロット}\world_data.json）との同期。
//
// ゲームは同じ世界の素データを savedata.json と world_data.json の2つに持ち、レコードの追加は
// 両方へ書く。一方で店の売買や未生成エリアへの到着では world_data.json 側を id で引くため、
// このエディタで savedata.json にだけ足したエリア・施設・NPC は、そこで KeyError を起こして
// ゲームのスレッドが静かに止まる（画面が変わらないまま固まる）。
//
// 対処は「2つのファイルを同じ内容にする」ではない。素のセーブにも差はあり（ゲームが依頼で作った
// ダンジョンや、遊びの中で増えた NPC は savedata にだけ入ることがある）、それは正常な状態のため。
// ここでは次の2つだけを扱う:
//   A. 保存時の同期（MainForm.SaveFile）: 読み込み後にこのエディタが足したレコードを world 側へ写し、
//      消したレコードを world 側からも消し、index（採番カウンタ）を両側の大きい方へ揃える。
//   B. 突き合わせツール（MainForm.SyncWorld）: 既に savedata にだけあるレコードを一覧から選んで写す。
// 写すのは areas / nodes / facilities / npcs と、写した施設の主（owner）・free 施設のプログラム、
// そして新レコードを指すように書き換わった既存レコードの項目（隣接エリアの connections 等）だけ。
// player_data / game_variables / quests / story_quests / world_data には触らない。
// 既存レコードの項目の不揃いも揃えない（ゲーム自身が不揃いを持っており、揃えると形が変わる）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // レコード id の集合（所在つき）。読み込み時点の控え／差分／写す対象の指定に共用する。
    internal sealed class IdSet
    {
        public enum Kind { Area, Node, Facility, Npc }

        public readonly HashSet<string> Areas = new();
        public readonly Dictionary<string, string> Nodes = new();                       // node id → area id
        public readonly Dictionary<string, (string area, string node)> Facilities = new(); // facility id → 所在
        public readonly HashSet<string> Npcs = new();

        public bool IsEmpty => Count == 0;
        public int Count => Areas.Count + Nodes.Count + Facilities.Count + Npcs.Count;

        public bool Has(Kind k, string id) => k switch
        {
            Kind.Area => Areas.Contains(id),
            Kind.Node => Nodes.ContainsKey(id),
            Kind.Facility => Facilities.ContainsKey(id),
            _ => Npcs.Contains(id),
        };

        // root（savedata / world_data）から全レコードの id と所在を控える。
        public static IdSet Capture(JsonObject root)
        {
            var s = new IdSet();
            if (root == null) return s;
            if (J.Obj(root, "areas") is JsonObject areas)
                foreach (var akv in areas)
                {
                    s.Areas.Add(akv.Key);
                    if (J.Obj(akv.Value as JsonObject, "nodes") is not JsonObject nodes) continue;
                    foreach (var nkv in nodes)
                    {
                        s.Nodes[nkv.Key] = akv.Key;
                        if (J.Obj(nkv.Value as JsonObject, "facilities") is not JsonObject facs) continue;
                        foreach (var fkv in facs) s.Facilities[fkv.Key] = (akv.Key, nkv.Key);
                    }
                }
            if (J.Obj(root, "npcs") is JsonObject npcs)
                foreach (var kv in npcs) s.Npcs.Add(kv.Key);
            return s;
        }

        // 条件を満たす要素だけを（所在ごと）抜き出した新しい集合。
        public IdSet Where(Func<Kind, string, bool> pred)
        {
            var r = new IdSet();
            foreach (var id in Areas) if (pred(Kind.Area, id)) r.Areas.Add(id);
            foreach (var kv in Nodes) if (pred(Kind.Node, kv.Key)) r.Nodes[kv.Key] = kv.Value;
            foreach (var kv in Facilities) if (pred(Kind.Facility, kv.Key)) r.Facilities[kv.Key] = kv.Value;
            foreach (var id in Npcs) if (pred(Kind.Npc, id)) r.Npcs.Add(id);
            return r;
        }
    }

    internal static class WorldSync
    {
        // 同期の結果件数。
        public readonly struct Result
        {
            public readonly int Areas, Nodes, Facilities, Npcs, Programs;   // world 側へ写した数
            public readonly int Removed;                                   // world 側から消した数
            public readonly int Skipped;                                   // 写し先のエリア／ノードが world 側に無く写せなかった数
            public readonly bool WorldIndexChanged, SaveIndexChanged;
            public Result(int areas, int nodes, int facilities, int npcs, int programs, int removed, int skipped, bool wIdx, bool sIdx)
            { Areas = areas; Nodes = nodes; Facilities = facilities; Npcs = npcs; Programs = programs; Removed = removed; Skipped = skipped; WorldIndexChanged = wIdx; SaveIndexChanged = sIdx; }
            public int Copied => Areas + Nodes + Facilities + Npcs;
            public bool WorldChanged => Copied + Programs + Removed > 0 || WorldIndexChanged;
        }

        // savedata.json のパスから相方の world_data.json のパスを返す（見つからなければ null）。
        // world_data.json 自体を開いている場合も null（相方は要らない）。
        public static string PartnerPath(string savePath)
        {
            if (string.IsNullOrEmpty(savePath)) return null;
            if (Path.GetFileName(savePath).Equals("world_data.json", StringComparison.OrdinalIgnoreCase)) return null;
            string dir = WorldTab.ResolveWorldDir(savePath);
            if (dir == null) return null;
            string p = Path.Combine(dir, "world_data.json");
            return File.Exists(p) ? p : null;
        }

        // saves\{スロット}\savedata.json の形のパスか。相方が無いときに警告を出すのはこの形のときだけ
        //（「名前を付けて保存」で外へ置いた複製では鳴らさない）。
        public static bool IsSlotSavePath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !Path.GetFileName(path).Equals("savedata.json", StringComparison.OrdinalIgnoreCase)) return false;
                string savesDir = Path.GetDirectoryName(Path.GetDirectoryName(path));
                return savesDir != null && Path.GetFileName(savesDir).Equals("saves", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------------- 差分 ----------------

        // 読み込み後にこのエディタが足したもの: いま save にあり、読み込み時の save に無く、world にも無い id。
        public static IdSet Added(JsonObject save, IdSet saveBase, JsonObject world)
        {
            var w = IdSet.Capture(world);
            return IdSet.Capture(save).Where((k, id) => !(saveBase?.Has(k, id) ?? false) && !w.Has(k, id));
        }

        // このエディタで消したもの: 読み込み時の save にあり、world には残っていて、いま save に無い id。
        // 所在は消す側（world）のものを使う。
        public static IdSet Removed(JsonObject save, IdSet saveBase, JsonObject world)
        {
            if (saveBase == null) return new IdSet();
            var now = IdSet.Capture(save);
            return IdSet.Capture(world).Where((k, id) => saveBase.Has(k, id) && !now.Has(k, id));
        }

        // save にだけあるもの（突き合わせツールの候補）。
        public static IdSet SaveOnly(JsonObject save, JsonObject world)
        {
            var w = IdSet.Capture(world);
            return IdSet.Capture(save).Where((k, id) => !w.Has(k, id));
        }

        // ---------------- 同期本体 ----------------

        // add を save → world へ写し、remove を world から消し、index を揃える。save は index 以外変更しない。
        public static Result Apply(JsonObject save, JsonObject world, IdSet add, IdSet remove)
        {
            int nArea = 0, nNode = 0, nFac = 0, nNpc = 0, nProg = 0, nRemoved = 0, nSkipped = 0;
            var sAreas = J.Obj(save, "areas");
            var wAreas = J.Obj(world, "areas");
            var sNpcs = J.Obj(save, "npcs");
            var wNpcs = J.Obj(world, "npcs");

            // 写した施設（エリア／ノードごと写した分も含む）。主とプログラムの追従に使う。
            var copiedFacs = new List<JsonObject>();
            var newNodes = new HashSet<string>();
            var newFacs = new HashSet<string>();
            var newNpcs = new HashSet<string>();

            // 1) エリア（配下のノード・施設ごと。項目も並びも変えない）
            if (sAreas != null && wAreas != null)
                foreach (var id in add.Areas)
                    if (sAreas[id] is JsonObject a && !wAreas.ContainsKey(id))
                    {
                        wAreas[id] = a.DeepClone();
                        nArea++;
                        foreach (var (nid, node) in EnumNodes(a))
                        {
                            newNodes.Add(nid);
                            foreach (var (fid, fo) in EnumFacilities(node)) { newFacs.Add(fid); copiedFacs.Add(fo); }
                        }
                    }

            // 2) ノード（既存エリアの下に足したもの）
            foreach (var (nid, aid) in add.Nodes)
            {
                if (add.Areas.Contains(aid)) continue;   // エリアごと写し済み
                var sn = J.Obj(J.Obj(sAreas?[aid] as JsonObject, "nodes"), nid);
                var wn = J.Obj(wAreas?[aid] as JsonObject, "nodes");
                if (sn == null || wn == null) { nSkipped++; continue; }
                if (wn.ContainsKey(nid)) continue;
                wn[nid] = sn.DeepClone();
                nNode++;
                newNodes.Add(nid);
                foreach (var (fid, fo) in EnumFacilities(sn)) { newFacs.Add(fid); copiedFacs.Add(fo); }
            }

            // 3) 施設（既存ノードの下に足したもの）
            foreach (var (fid, (aid, nid)) in add.Facilities)
            {
                if (add.Areas.Contains(aid) || add.Nodes.ContainsKey(nid)) continue;
                var sf = J.Obj(J.Obj(J.Obj(J.Obj(sAreas?[aid] as JsonObject, "nodes"), nid), "facilities"), fid);
                var wnode = J.Obj(J.Obj(wAreas?[aid] as JsonObject, "nodes"), nid);
                if (sf == null || wnode == null) { nSkipped++; continue; }
                if (wnode["facilities"] is not JsonObject wf) wnode["facilities"] = wf = new JsonObject();
                if (wf.ContainsKey(fid)) continue;
                wf[fid] = sf.DeepClone();
                nFac++;
                newFacs.Add(fid);
                copiedFacs.Add(sf);
            }

            // 4) NPC（指定分＋写した施設の主。主が world 側に無いと店が開けない）
            var npcIds = new List<string>(add.Npcs);
            foreach (var fo in copiedFacs)
            {
                string owner = J.Str(fo, "owner");
                if (!string.IsNullOrEmpty(owner)) npcIds.Add(owner);
            }
            if (sNpcs != null && wNpcs != null)
                foreach (var id in npcIds.Distinct())
                    if (sNpcs[id] is JsonObject n && !wNpcs.ContainsKey(id))
                    {
                        wNpcs[id] = n.DeepClone();
                        nNpc++;
                        newNpcs.Add(id);
                    }

            // 5) 写した free 施設が指すプログラム
            foreach (var fo in copiedFacs)
            {
                string pid = FreeFacilityProgram.ProgramIdOf(fo);
                if (string.IsNullOrEmpty(pid) || FreeFacilityProgram.Programs(save)?[pid] is not JsonNode prog) continue;
                var wp = FreeFacilityProgram.Ensure(world);
                if (wp == null || wp.ContainsKey(pid)) continue;
                wp[pid] = prog.DeepClone();
                nProg++;
            }

            // 6) 逆参照: 新レコードを指すように書き換わった既存レコードの項目を world 側にも当てる。
            //    配列は丸ごと写さず、新 id だけを足す（world 側にだけあるものを消さないため）。
            if (nArea + nNode + nFac + nNpc > 0)
                ApplyBackRefs(sAreas, wAreas, add.Areas, newNodes, newFacs, newNpcs);

            // 7) 削除（WorldTab.Delete と同じ参照整理を world 側にも当てる）
            var orphanPrograms = new List<string>();
            foreach (var id in remove.Areas)
            {
                if (wAreas?[id] is not JsonObject a) continue;
                foreach (var (_, node) in EnumNodes(a))
                    foreach (var (_, fo) in EnumFacilities(node)) orphanPrograms.Add(FreeFacilityProgram.ProgramIdOf(fo));
                wAreas.Remove(id);
                nRemoved++;
                foreach (var kv in wAreas) if (kv.Value is JsonObject o) RemoveFromArray(J.Arr(o, "connections"), id);
                DetachAreaRefs(wNpcs, id);
            }
            foreach (var (nid, aid) in remove.Nodes)
            {
                if (remove.Areas.Contains(aid)) continue;
                var wa = wAreas?[aid] as JsonObject;
                if (J.Obj(wa, "nodes") is not JsonObject wn || wn[nid] is not JsonObject node) continue;
                var facIds = new List<string>();
                foreach (var (fid, fo) in EnumFacilities(node)) { facIds.Add(fid); orphanPrograms.Add(FreeFacilityProgram.ProgramIdOf(fo)); }
                wn.Remove(nid);
                nRemoved++;
                if (J.Str(wa, "entrance_node") == nid) wa["entrance_node"] = null;
                DetachFacilityRefs(wa, wNpcs, aid, facIds);
            }
            foreach (var (fid, (aid, nid)) in remove.Facilities)
            {
                if (remove.Areas.Contains(aid) || remove.Nodes.ContainsKey(nid)) continue;
                var wa = wAreas?[aid] as JsonObject;
                if (J.Obj(J.Obj(J.Obj(wa, "nodes"), nid), "facilities") is not JsonObject wf || wf[fid] is not JsonObject fo) continue;
                orphanPrograms.Add(FreeFacilityProgram.ProgramIdOf(fo));
                wf.Remove(fid);
                nRemoved++;
                foreach (var (_, node) in EnumNodes(wa))
                    foreach (var (_, peer) in EnumFacilities(node)) RemoveFromArray(J.Arr(peer, "connections"), fid);
                DetachFacilityRefs(wa, wNpcs, aid, new[] { fid });
            }
            foreach (var id in remove.Npcs)
            {
                if (wNpcs == null || !wNpcs.ContainsKey(id)) continue;
                wNpcs.Remove(id);
                nRemoved++;
                if (wAreas != null)
                    foreach (var kv in wAreas)
                    {
                        if (kv.Value is not JsonObject a) continue;
                        RemoveFromArray(J.Arr(a, "resident_npcs"), id);
                        RemoveFromArray(J.Arr(a, "adventurer_npcs"), id);
                        foreach (var (_, node) in EnumNodes(a))
                            foreach (var (_, fo) in EnumFacilities(node))
                                if (J.Str(fo, "owner") == id) fo["owner"] = null;
                    }
            }
            PruneOrphanPrograms(world, orphanPrograms);

            // 8) index（採番カウンタ）: キーごとに両側の大きい方へ寄せる。
            //    片側だけ進むと、遅れた側から採番したときに別のものへ同じ id が振られる。
            var (wIdx, sIdx) = AlignIndex(save, world);

            return new Result(nArea, nNode, nFac, nNpc, nProg, nRemoved, nSkipped, wIdx, sIdx);
        }

        // 新レコードを指す既存レコードの項目を world 側へ当てる。
        private static void ApplyBackRefs(JsonObject sAreas, JsonObject wAreas,
            HashSet<string> newAreas, HashSet<string> newNodes, HashSet<string> newFacs, HashSet<string> newNpcs)
        {
            if (sAreas == null || wAreas == null) return;
            foreach (var akv in wAreas)
            {
                if (newAreas.Contains(akv.Key)) continue;   // 写したばかりの複製なので触る必要がない
                if (akv.Value is not JsonObject wa || sAreas[akv.Key] is not JsonObject sa) continue;

                AddMissing(sa, wa, "connections", newAreas);          // 隣接エリア（SetAreaConnectionHooks が双方向に書く）
                AddMissing(sa, wa, "resident_npcs", newNpcs);         // 住民／冒険者（CreateNpc / NpcImportDialog）
                AddMissing(sa, wa, "adventurer_npcs", newNpcs);
                if (newNodes.Contains(J.Str(sa, "entrance_node")) && J.Str(wa, "entrance_node") != J.Str(sa, "entrance_node"))
                    wa["entrance_node"] = J.Str(sa, "entrance_node");

                foreach (var (nid, wn) in EnumNodes(wa))
                {
                    if (newNodes.Contains(nid) || J.Obj(J.Obj(sa, "nodes"), nid) is not JsonObject sn) continue;
                    string ef = J.Str(sn, "entrance_facility");
                    if (newFacs.Contains(ef) && J.Str(wn, "entrance_facility") != ef) wn["entrance_facility"] = ef;   // 入口施設
                    foreach (var (fid, wf) in EnumFacilities(wn))
                    {
                        if (newFacs.Contains(fid) || J.Obj(J.Obj(sn, "facilities"), fid) is not JsonObject sf) continue;
                        AddMissing(sf, wf, "connections", newFacs);   // 接続先施設（FacilityImportDialog / SetFacilityConnectionHooks）
                        string owner = J.Str(sf, "owner");
                        if (newNpcs.Contains(owner) && J.Str(wf, "owner") != owner) wf["owner"] = owner;   // 主
                    }
                }
            }
        }

        // src[key] の配列にあって dst[key] に無い id のうち、wanted に含まれるものだけを dst へ足す。
        private static void AddMissing(JsonObject src, JsonObject dst, string key, HashSet<string> wanted)
        {
            if (J.Arr(src, key) is not JsonArray sa) return;
            JsonArray da = J.Arr(dst, key);
            foreach (var n in sa)
            {
                string id = n?.ToString() ?? "";
                if (!wanted.Contains(id)) continue;
                if (da == null) dst[key] = da = new JsonArray();
                if (!da.Any(x => (x?.ToString() ?? "") == id)) da.Add(id);
            }
        }

        // 消したエリアへの参照を外す（NPC の現在地・初期配置）。
        private static void DetachAreaRefs(JsonObject npcs, string areaId)
        {
            if (npcs == null) return;
            foreach (var kv in npcs)
            {
                if (kv.Value is not JsonObject n) continue;
                if (J.Str(n, "current_area") == areaId) { n["current_area"] = null; n["current_location"] = null; }
                if (J.Obj(n, "initial_location") is JsonObject il && J.Str(il, "area") == areaId) { il["area"] = null; il["facility"] = null; }
            }
        }

        // 消した施設への参照を外す（同エリア NPC の現在地/初期配置・ノードの入口施設）。
        private static void DetachFacilityRefs(JsonObject area, JsonObject npcs, string areaId, IEnumerable<string> facIds)
        {
            var set = new HashSet<string>(facIds.Where(s => !string.IsNullOrEmpty(s)));
            if (set.Count == 0) return;
            if (npcs != null)
                foreach (var kv in npcs)
                {
                    if (kv.Value is not JsonObject n) continue;
                    if (J.Str(n, "current_area") == areaId && set.Contains(J.Str(n, "current_location"))) n["current_location"] = null;
                    if (J.Obj(n, "initial_location") is JsonObject il && J.Str(il, "area") == areaId && set.Contains(J.Str(il, "facility")))
                        il["facility"] = null;
                }
            foreach (var (_, node) in EnumNodes(area))
                if (set.Contains(J.Str(node, "entrance_facility"))) node["entrance_facility"] = null;
        }

        // 消した施設が指していたプログラムのうち、world 側のどの施設からも参照されなくなったものを消す。
        private static void PruneOrphanPrograms(JsonObject world, List<string> candidates)
        {
            var ids = new HashSet<string>(candidates.Where(s => !string.IsNullOrEmpty(s)));
            if (ids.Count == 0 || FreeFacilityProgram.Programs(world) is not JsonObject progs) return;
            if (J.Obj(world, "areas") is JsonObject areas)
                foreach (var akv in areas)
                    foreach (var (_, node) in EnumNodes(akv.Value as JsonObject))
                        foreach (var (_, fo) in EnumFacilities(node)) ids.Remove(FreeFacilityProgram.ProgramIdOf(fo));
            foreach (var id in ids) progs.Remove(id);
        }

        // index の各キーを両側の大きい方へ揃える。片側にしか無いキーはもう片側へ写す。
        // 戻り値: (world 側を変えたか, save 側を変えたか)。
        private static (bool world, bool save) AlignIndex(JsonObject save, JsonObject world)
        {
            var si = J.Obj(save, "index");
            var wi = J.Obj(world, "index");
            if (si == null || wi == null) return (false, false);
            bool w = false, s = false;
            foreach (var key in si.Select(p => p.Key).Union(wi.Select(p => p.Key)).ToList())
            {
                long a = J.Int(si, key, -1), b = J.Int(wi, key, -1);
                long m = Math.Max(a, b);
                if (m < 0) continue;   // どちらも数値でない
                if (a != m) { si[key] = m; s = true; }
                if (b != m) { wi[key] = m; w = true; }
            }
            return (w, s);
        }

        // ---------------- 列挙・配列操作 ----------------

        public static IEnumerable<(string id, JsonObject node)> EnumNodes(JsonObject area)
        {
            if (J.Obj(area, "nodes") is not JsonObject nodes) yield break;
            foreach (var kv in nodes) if (kv.Value is JsonObject n) yield return (kv.Key, n);
        }

        public static IEnumerable<(string id, JsonObject fo)> EnumFacilities(JsonObject node)
        {
            if (J.Obj(node, "facilities") is not JsonObject facs) yield break;
            foreach (var kv in facs) if (kv.Value is JsonObject f) yield return (kv.Key, f);
        }

        private static void RemoveFromArray(JsonArray arr, string id)
        {
            if (arr == null) return;
            for (int i = arr.Count - 1; i >= 0; i--)
                if ((arr[i]?.ToString() ?? "") == id) arr.RemoveAt(i);
        }

        // 「ゲームが依頼で作ったダンジョン」の形か（size=dungeon で dungeon_location 施設を持つ）。
        // 突き合わせツールの既定で外す判定に使う。エディタで作ったダンジョンも同形になるため、
        // 判定は厳密ではない（必要なら一覧で手動でチェックする）。
        public static bool LooksLikeGameDungeon(JsonObject area)
        {
            if (J.Str(area, "size") != "dungeon") return false;
            foreach (var (_, node) in EnumNodes(area))
                foreach (var (_, fo) in EnumFacilities(node))
                    if (J.Str(fo, "facility_type") == "dungeon_location") return true;
            return false;
        }
    }

    // ---------------- 突き合わせツールのダイアログ ----------------
    // save にだけあるエリア・ノード・施設・NPC を一覧にして選ばせる。
    // 既定: ゲームが依頼で作ったダンジョン（size=dungeon＋dungeon_location）は外し、それ以外は全て選ぶ。
    // save にだけあるエリアの配下のノード・施設は一覧に出さない（エリアごと写す／エリアが無ければ写せない）。
    internal sealed class WorldSyncDialog : Form
    {
        private readonly CheckedListBox _list = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        private readonly List<(IdSet.Kind kind, string id)> _items = new();
        private readonly IdSet _cand;

        public IdSet Selected { get; private set; }

        public WorldSyncDialog(JsonObject save, IdSet candidates, string worldPath)
        {
            _cand = candidates;
            Text = I18n.T("title.worldSync");
            Width = 640; Height = 520;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;

            var intro = new Label
            {
                Dock = DockStyle.Top, AutoSize = false, Height = 88, Padding = new Padding(10, 8, 10, 0),
                Text = I18n.T("worldsync.intro", worldPath ?? ""),
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var ok = new Button { Text = I18n.T("worldsync.copy"), Width = 100 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Width = 90, DialogResult = DialogResult.Cancel };
            var all = new Button { Text = I18n.T("worldsync.selectAll"), Width = 100 };
            var none = new Button { Text = I18n.T("worldsync.selectNone"), Width = 100 };
            all.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true); };
            none.Click += (_, _) => { for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, false); };
            ok.Click += (_, _) => { Selected = Collect(); DialogResult = DialogResult.OK; Close(); };
            bar.Controls.AddRange(new Control[] { ok, cancel, none, all });
            AcceptButton = ok; CancelButton = cancel;

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 4) };
            body.Controls.Add(_list);
            Controls.Add(body);
            Controls.Add(intro);
            Controls.Add(bar);

            Fill(save);
        }

        private void Fill(JsonObject save)
        {
            var areas = J.Obj(save, "areas");
            var npcs = J.Obj(save, "npcs");
            string Name(JsonObject o, string id) => J.Str(o, "name", id);

            foreach (var id in Order(_cand.Areas))
            {
                var a = areas?[id] as JsonObject;
                string label = $"[{I18n.T("worldsync.kind.area")}] {id}: {Name(a, id)} ({J.Str(a, "size")})";
                Add(IdSet.Kind.Area, id, label, !WorldSync.LooksLikeGameDungeon(a));
            }
            foreach (var id in Order(_cand.Nodes.Keys))
            {
                string aid = _cand.Nodes[id];
                if (_cand.Areas.Contains(aid)) continue;
                var a = areas?[aid] as JsonObject;
                var n = J.Obj(J.Obj(a, "nodes"), id);
                Add(IdSet.Kind.Node, id, $"[{I18n.T("worldsync.kind.node")}] {id}: {Name(n, id)}  ← {Name(a, aid)}", true);
            }
            foreach (var id in Order(_cand.Facilities.Keys))
            {
                var (aid, nid) = _cand.Facilities[id];
                if (_cand.Areas.Contains(aid) || _cand.Nodes.ContainsKey(nid)) continue;
                var a = areas?[aid] as JsonObject;
                var f = J.Obj(J.Obj(J.Obj(J.Obj(a, "nodes"), nid), "facilities"), id);
                Add(IdSet.Kind.Facility, id, $"[{I18n.T("worldsync.kind.facility")}] {id}: {Name(f, id)} ({J.Str(f, "facility_type")})  ← {Name(a, aid)}", true);
            }
            foreach (var id in Order(_cand.Npcs))
            {
                var n = npcs?[id] as JsonObject;
                Add(IdSet.Kind.Npc, id, $"[{I18n.T("worldsync.kind.npc")}] {id}: {Name(n, id)}", true);
            }
        }

        private void Add(IdSet.Kind kind, string id, string label, bool check)
        {
            _items.Add((kind, id));
            _list.Items.Add(label, check);
        }

        // 数値 id は数値順、それ以外は文字列順。
        private static IEnumerable<string> Order(IEnumerable<string> ids)
            => ids.OrderBy(s => long.TryParse(s, out var v) ? v : long.MaxValue).ThenBy(s => s, StringComparer.Ordinal);

        private IdSet Collect()
        {
            var r = new IdSet();
            for (int i = 0; i < _items.Count; i++)
            {
                if (!_list.GetItemChecked(i)) continue;
                var (kind, id) = _items[i];
                switch (kind)
                {
                    case IdSet.Kind.Area: r.Areas.Add(id); break;
                    case IdSet.Kind.Node: r.Nodes[id] = _cand.Nodes[id]; break;
                    case IdSet.Kind.Facility: r.Facilities[id] = _cand.Facilities[id]; break;
                    default: r.Npcs.Add(id); break;
                }
            }
            return r;
        }
    }
}

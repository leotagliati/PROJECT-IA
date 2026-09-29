# Relatório do grafo de nós de uma arena, lido direto do .prefab (sem abrir o Unity).
#
# Uso:  python tools/graph_report.py Assets/Prefabs/NodeTraining2.prefab
#
# Mostra, a partir do YAML do prefab (posições no MUNDO resolvidas pela hierarquia):
#   - altura dos nós e da coluna do corpo, e a laje de cada Wall_01_Door_Hole;
#   - vão livre de cada porta (pilares de todas as portas) e colliders de parede no vão;
#   - pedaços do grafo (tudo tem que ser 1) e portas sem ligação atravessando;
#   - densidade: grau, comprimento de aresta, vizinho mais perto, nós a < 2 m de outro.
# Limites: só BoxCollider na raiz do prefab-fonte entra na checagem de vão (MeshCollider de
# mobília é contado e ignorado); a geometria da porta (pilares) é a do kit horror-game-floor.
import re, sys, math, glob, os
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREFAB = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "Assets", "Prefabs", "NodeTraining.prefab")

text = open(PREFAB, encoding="utf-8").read()
docs = re.split(r"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$", text, flags=re.M)
objs = {}
for i in range(1, len(docs), 4):
    cls, fid, stripped, body = docs[i], docs[i + 1], docs[i + 2], docs[i + 3]
    objs[fid] = (cls, bool(stripped), body)

def vec(body, key, n):
    m = re.search(key + r": \{([^}]*)\}", body)
    if not m: return None
    d = dict(kv.split(": ") for kv in m.group(1).split(", "))
    return [float(d[c]) for c in "xyzw"[:n]]

def fileid(body, key):
    m = re.search(key + r": \{fileID: (-?\d+)", body)
    return m.group(1) if m else None

# Transform do prefab-fonte (raiz) por guid: posição/rotação/escala padrão da raiz.
guid_path = {}
for meta in glob.glob(os.path.join(ROOT, "Assets", "**", "*.prefab.meta"), recursive=True):
    g = re.search(r"guid: (\w+)", open(meta, encoding="utf-8", errors="ignore").read())
    if g: guid_path[g.group(1)] = meta[:-5]

def source_root(guid):
    p = guid_path.get(guid)
    if not p: return None
    t = open(p, encoding="utf-8", errors="ignore").read()
    ds = re.split(r"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$", t, flags=re.M)
    for i in range(1, len(ds), 4):
        if ds[i] == "4" and re.search(r"m_Father: \{fileID: 0\}", ds[i + 3]):
            b = ds[i + 3]
            return (vec(b, "m_LocalPosition", 3), vec(b, "m_LocalRotation", 4), vec(b, "m_LocalScale", 3), ds[i + 1])
    return None

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return [aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz]

def qrot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    # v' = q v q*
    ix = w*vx + y*vz - z*vy; iy = w*vy + z*vx - x*vz; iz = w*vz + x*vy - y*vx; iw = -x*vx - y*vy - z*vz
    return [ix*w + iw*-x + iy*-z - iz*-y, iy*w + iw*-y + iz*-x - ix*-z, iz*w + iw*-z + ix*-y - iy*-x]

# Transforms locais: regulares e raízes de PrefabInstance (com overrides).
local = {}   # transform fid -> (pos, rot, scale, parent fid)
inst_root = {}  # prefab instance fid -> (root transform fid local key, name)
names = {}
for fid, (cls, stripped, body) in objs.items():
    if cls == "4" and not stripped:
        local[fid] = (vec(body, "m_LocalPosition", 3), vec(body, "m_LocalRotation", 4), vec(body, "m_LocalScale", 3), fileid(body, "m_Father"))
    if cls == "1001":
        guid = re.search(r"m_SourcePrefab: \{fileID: \d+, guid: (\w+)", body)
        src = source_root(guid.group(1)) if guid else None
        if not src: continue
        pos, rot, scl, root_fid = [list(src[0]), list(src[1]), list(src[2]), src[3]]
        name = ""
        for m in re.finditer(r"- target: \{fileID: (-?\d+)[^}]*\}\s*propertyPath: (\S+)\s*value: ([^\n]*)", body):
            tgt, path, val = m.groups()
            if path == "m_Name": name = val.strip()
            if tgt != root_fid: continue
            for key, arr in (("m_LocalPosition", pos), ("m_LocalRotation", rot), ("m_LocalScale", scl)):
                if path.startswith(key + "."):
                    arr["xyzw".index(path[-1])] = float(val)
        parent = fileid(body, "m_TransformParent")
        inst_root[fid] = (pos, rot, scl, parent, name)

# Transform "stripped" aponta para a raiz de uma instância (caso comum): mapeia.
stripped_to_inst = {}
for fid, (cls, stripped, body) in objs.items():
    if cls == "4" and stripped:
        stripped_to_inst[fid] = fileid(body, "m_PrefabInstance")

def world(pos, rot, scl, parent, depth=0):
    if parent in (None, "0") or depth > 40:
        return pos, rot, scl
    if parent in local:
        ppos, prot, pscl = world(*local[parent], depth=depth + 1)
    elif parent in stripped_to_inst and stripped_to_inst[parent] in inst_root:
        p = inst_root[stripped_to_inst[parent]]
        ppos, prot, pscl = world(p[0], p[1], p[2], p[3], depth + 1)
    else:
        return None
    s = [pscl[i] * pos[i] for i in range(3)]
    r = qrot(prot, s)
    return [ppos[i] + r[i] for i in range(3)], qmul(prot, rot), [pscl[i] * scl[i] for i in range(3)]

# NavNodes: GameObjects com o script NavNode -> seus transforms.
navnode_guid = re.search(r"guid: (\w+)", open(os.path.join(ROOT, "Assets", "Scripts", "Graph", "NavNode.cs.meta")).read()).group(1)
node_go = set()
for fid, (cls, stripped, body) in objs.items():
    if cls == "114" and navnode_guid in body:
        node_go.add(fileid(body, "m_GameObject"))
node_y = []
for fid, (cls, stripped, body) in objs.items():
    if cls == "4" and not stripped and fileid(body, "m_GameObject") in node_go:
        w = world(*local[fid])
        if w: node_y.append(w[0][1])

doors = []
for fid, (pos, rot, scl, parent, name) in inst_root.items():
    if "Door_Hole" in name:
        w = world(pos, rot, scl, parent)
        doors.append((name, w))

node_y.sort()
print(f"nós: {len(node_y)}  altura (mundo) min {node_y[0]:.3f}  mediana {node_y[len(node_y)//2]:.3f}  max {node_y[-1]:.3f}")
plane = node_y[len(node_y)//2]
bottom = plane - 1.6
print(f"coluna do corpo começa em y = {bottom:.3f} (mediana dos nós - 1.6)")
print(f"portas: {len(doors)}")
# Laje da porta: BoxCollider local center 0, tamanho z 0.003 (eixo z local vira o vertical com a rotação -90 em X)
for name, w in sorted(doors, key=lambda d: d[0]):
    if not w:
        print(f"  {name}: sem transform resolvido"); continue
    pos, rot, scl = w
    up = qrot(rot, [0, 0, 1])
    half = abs(0.003 * scl[2] / 2 * up[1])
    top = pos[1] + half
    print(f"  {name:28s} pivô y {pos[1]:7.3f}  escala {scl[0]:.0f}/{scl[1]:.0f}/{scl[2]:.0f}  topo da laje {top:6.3f}  "
          f"{'BLOQUEIA' if top > bottom else 'livre'} ({(top - bottom) * 100:+.0f} cm)  xz ({pos[0]:.1f}, {pos[2]:.1f})")

# ---- Vãos reais: pilares de TODAS as portas, projetados no chão (xz) ----
PILLARS = [(-0.0082, 0.0083), (0.0082, 0.0083)]   # centro local (x, y) dos dois BoxCollider de pilar
PSIZE = (0.0037, 0.0037)                           # tamanho local (x, y)
rects = []   # (door name, cx, cz, hx, hz) em mundo, alinhado a eixo (rotações de 90 graus)
info = []
for name, w in doors:
    pos, rot, scl = w
    ax = qrot(rot, [1, 0, 0]); ay = qrot(rot, [0, 1, 0])
    for (cx, cy) in PILLARS:
        off = [ax[i] * cx * scl[0] + ay[i] * cy * scl[1] for i in range(3)]
        hx = abs(ax[0]) * PSIZE[0] * scl[0] / 2 + abs(ay[0]) * PSIZE[1] * scl[1] / 2
        hz = abs(ax[2]) * PSIZE[0] * scl[0] / 2 + abs(ay[2]) * PSIZE[1] * scl[1] / 2
        rects.append((name, pos[0] + off[0], pos[2] + off[2], hx, hz))
    info.append((name, pos, ax))

print("\nvão livre de cada porta (entre pilares, contando pilares das OUTRAS portas); o corpo precisa de 1.70 m")
for name, pos, ax in sorted(info, key=lambda d: d[0]):
    along_x = abs(ax[0]) > 0.5
    c = pos[0] if along_x else pos[2]
    other = pos[2] if along_x else pos[0]
    lo, hi = c - 1.27, c + 1.27
    blocks = []
    for (n, rx, rz, hx, hz) in rects:
        rc, rh, ro, roh = (rx, hx, rz, hz) if along_x else (rz, hz, rx, hx)
        if abs(ro - other) > roh + 0.3:      # outro plano de parede
            continue
        a, b = rc - rh, rc + rh
        if b <= lo or a >= hi: continue
        blocks.append((max(a, lo), min(b, hi), n))
    blocks.sort()
    gaps, cur = [], lo
    for a, b, n in blocks:
        if a > cur: gaps.append(a - cur)
        cur = max(cur, b)
    if hi > cur: gaps.append(hi - cur)
    widest = max(gaps) if gaps else 0
    who = sorted({n for _, _, n in blocks if n != name})
    print(f"  {name:28s} parede ao longo de {'x' if along_x else 'z'}  maior vão {widest:4.2f} m  "
          f"{'OK' if widest >= 1.70 else 'FECHADA'}{'  pilar de: ' + ', '.join(who) if who else ''}")

# ---- Quem ocupa o vão? Todos os BoxColliders (raiz do prefab-fonte) de instâncias na layer Wall ----
WALL_LAYER = 3
src_cache = {}
def source_info(guid):
    if guid in src_cache: return src_cache[guid]
    p = guid_path.get(guid); res = None
    if p:
        t = open(p, encoding="utf-8", errors="ignore").read()
        ds = re.split(r"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$", t, flags=re.M)
        ob = {ds[i+1]: (ds[i], ds[i+3]) for i in range(1, len(ds), 4)}
        root_t = next((f for f, (c, b) in ob.items() if c == "4" and "m_Father: {fileID: 0}" in b), None)
        root_go = fileid(ob[root_t][1], "m_GameObject") if root_t else None
        layer = int(re.search(r"m_Layer: (\d+)", ob[root_go][1]).group(1)) if root_go in ob else 0
        boxes, other = [], []
        for f, (c, b) in ob.items():
            if c == "65" and fileid(b, "m_GameObject") == root_go and "m_IsTrigger: 0" in b:
                boxes.append((f, vec(b, "m_Size", 3), vec(b, "m_Center", 3), "m_Enabled: 1" in b))
            elif c in ("64", "65", "135", "136") and fileid(b, "m_GameObject") != root_go:
                other.append(c)
            elif c in ("64", "135", "136") :
                other.append(c)
        res = (layer, boxes, other, os.path.basename(p))
    src_cache[guid] = res
    return res

boxes_world = []   # (name, minx, maxx, miny, maxy, minz, maxz)
skipped = defaultdict(int)
for fid, (cls, stripped, body) in objs.items():
    if cls != "1001": continue
    g = re.search(r"m_SourcePrefab: \{fileID: \d+, guid: (\w+)", body)
    si = source_info(g.group(1)) if g else None
    if not si or fid not in inst_root: continue
    layer, boxes, other, src = si
    mods = re.findall(r"- target: \{fileID: (-?\d+)[^}]*\}\s*propertyPath: (\S+)\s*value: ([^\n]*)", body)
    for tgt, path, val in mods:
        if path == "m_Layer": layer = int(val)
    if layer != WALL_LAYER: continue
    if "m_IsActive" in body and re.search(r"propertyPath: m_IsActive\s*value: 0", body): continue
    if other: skipped[src] += 1
    pos, rot, scl, parent, name = inst_root[fid]
    w = world(pos, rot, scl, parent)
    if not w: continue
    wp, wr, ws = w
    for (bf, size, center, enabled) in boxes:
        for tgt, path, val in mods:
            if tgt == bf and path == "m_Enabled": enabled = val.strip() == "1"
        if not enabled: continue
        c = qrot(wr, [center[i] * ws[i] for i in range(3)])
        cw = [wp[i] + c[i] for i in range(3)]
        half = [0, 0, 0]
        for i, axis in enumerate(([1,0,0],[0,1,0],[0,0,1])):
            a = qrot(wr, axis)
            for k in range(3):
                half[k] += abs(a[k]) * abs(size[i] * ws[i]) / 2
        boxes_world.append((name or src, cw[0]-half[0], cw[0]+half[0], cw[1]-half[1], cw[1]+half[1], cw[2]-half[2], cw[2]+half[2]))

top = plane + 1.8
print(f"\ncolliders de caixa na layer Wall: {len(boxes_world)}; instâncias com collider não-caixa (não analisadas): {dict(skipped)}")
print("o que invade o VÃO de cada porta (faixa 2.54 m x espessura da parede, altura da coluna do corpo):")
bad = 0
for name, w in sorted(doors, key=lambda d: d[0]):
    pos, rot, scl = w
    ax = qrot(rot, [1, 0, 0]); ay = qrot(rot, [0, 1, 0])
    c = [pos[i] + ay[i] * 0.0083 * scl[1] for i in range(3)]
    hx = abs(ax[0]) * 1.27 + abs(ay[0]) * 0.18
    hz = abs(ax[2]) * 1.27 + abs(ay[2]) * 0.18
    hits = []
    for (n, x0, x1, y0, y1, z0, z1) in boxes_world:
        if n == name: continue
        T = 0.02
        if x1 <= c[0]-hx+T or x0 >= c[0]+hx-T or z1 <= c[2]-hz+T or z0 >= c[2]+hz-T or y1 <= bottom or y0 >= top: continue
        hits.append(f"{n} (y {y0:.2f}..{y1:.2f})")
    if hits:
        bad += 1
        print(f"  {name:28s} em ({c[0]:.1f}, {c[2]:.1f}): {', '.join(hits)}")
print(f"portas com o vão ocupado: {bad} de {len(doors)}")

# ---- O grafo salvo: nós, ligações, pedaços e portas atravessadas ----
comp_by_go = {}   # NavNode component fid -> gameobject fid
node_info = {}    # component fid -> dict
tr_by_go = {fileid(b, "m_GameObject"): f for f, (c, s, b) in objs.items() if c == "4" and not s}
for fid, (cls, stripped, body) in objs.items():
    if cls == "114" and navnode_guid in body:
        go = fileid(body, "m_GameObject")
        nb = re.search(r"_neighbors:\s*\n((?:\s*- \{fileID: -?\d+\}\s*\n)*)", body)
        neigh = re.findall(r"fileID: (-?\d+)", nb.group(1)) if nb else []
        kind = re.search(r"_kind: (\d+)", body)
        w = world(*local[tr_by_go[go]]) if go in tr_by_go else None
        name = re.search(r"m_Name: ([^\n]*)", objs[go][2]).group(1) if go in objs else "?"
        wt = re.search(r"_explorationWeight: ([-\d.e]+)", body)
        node_info[fid] = dict(pos=w[0] if w else None, neigh=neigh, primary=(kind is None or kind.group(1) == "0"), name=name,
                              weight=float(wt.group(1)) if wt else 1.0)

edges = set()
for f, n in node_info.items():
    for o in n["neigh"]:
        if o in node_info and o != f:
            edges.add(tuple(sorted((f, o))))
adj = defaultdict(set)
for a, b in edges:
    adj[a].add(b); adj[b].add(a)
seen, comps = set(), []
for f in node_info:
    if f in seen: continue
    stack, comp = [f], []
    seen.add(f)
    while stack:
        x = stack.pop(); comp.append(x)
        for y in adj[x]:
            if y not in seen: seen.add(y); stack.append(y)
    comps.append(comp)
comps.sort(key=len, reverse=True)
print(f"\nGRAFO SALVO: {len(node_info)} nós, {len(edges)} ligações, {len(comps)} pedaço(s): {[len(c) for c in comps]}")
for c in comps[1:]:
    print("  pedaço solto:", ", ".join(f"{node_info[x]['name']} ({node_info[x]['pos'][0]:.1f}, {node_info[x]['pos'][2]:.1f})" for x in c[:6]))

def cross(p, q, a, b):
    def o(u, v, w): return (v[0]-u[0])*(w[1]-u[1]) - (v[1]-u[1])*(w[0]-u[0])
    return o(p, q, a) * o(p, q, b) < 0 and o(a, b, p) * o(a, b, q) < 0

print("portas SEM nenhuma ligação atravessando:")
none = 0
for name, w in sorted(doors, key=lambda d: d[0]):
    pos, rot, scl = w
    ax = qrot(rot, [1, 0, 0]); ay = qrot(rot, [0, 1, 0])
    c = [pos[i] + ay[i] * 0.0083 * scl[1] for i in range(3)]
    a = (c[0] - ax[0] * 1.27, c[2] - ax[2] * 1.27); b = (c[0] + ax[0] * 1.27, c[2] + ax[2] * 1.27)
    hit = any(cross((node_info[e0]['pos'][0], node_info[e0]['pos'][2]), (node_info[e1]['pos'][0], node_info[e1]['pos'][2]), a, b)
              for e0, e1 in edges)
    # Ou o NÓ DA PORTA (a menos de 1 m do meio do vão) com vizinhos dos dois lados da parede: a
    # aresta que termina nele só encosta na linha da porta, e o teste de cruzamento não a conta.
    # Mesma regra do NavGraphPlacer.DoorCrossed.
    if not hit:
        normal = (-ax[2], ax[0])
        side = lambda f: (node_info[f]['pos'][0] - c[0]) * normal[0] + (node_info[f]['pos'][2] - c[2]) * normal[1]
        dn = min(node_info, key=lambda f: math.dist((node_info[f]['pos'][0], node_info[f]['pos'][2]), (c[0], c[2])))
        if math.dist((node_info[dn]['pos'][0], node_info[dn]['pos'][2]), (c[0], c[2])) < 1.0:
            sides = [side(f) for f in adj[dn]]
            hit = any(v > 0.5 for v in sides) and any(v < -0.5 for v in sides)
    if not hit:
        none += 1
        near = min((math.dist((n['pos'][0], n['pos'][2]), (c[0], c[2])), n['name']) for n in node_info.values())
        print(f"  {name:28s} em ({c[0]:.1f}, {c[2]:.1f}); nó mais perto: {near[1]} a {near[0]:.1f} m")
print(f"{none} de {len(doors)} portas sem ligação passando por elas")
doors_missing = none

# ---- Densidade do grafo salvo ----
import statistics as st
pos = {f: (n['pos'][0], n['pos'][2]) for f, n in node_info.items() if n['pos']}
prim = sum(1 for n in node_info.values() if n['primary'])
deg = [len(adj[f]) for f in node_info]
lens = [math.dist(pos[a], pos[b]) for a, b in edges]
nn = [min(math.dist(pos[f], pos[g]) for g in pos if g != f) for f in pos]
xs = [p[0] for p in pos.values()]; zs = [p[1] for p in pos.values()]
radius_over = {}
for fid, (cls, s, body) in objs.items():
    if cls == "114" and navnode_guid in body:
        m = re.search(r"_radiusOverride: ([-\d.e]+)", body)
        radius_over[fid] = float(m.group(1)) if m else 0.0
print(f"\nDENSIDADE: {len(node_info)} nós ({prim} primários, {len(node_info)-prim} auxiliares), {len(edges)} ligações")
print(f"  grau: médio {st.mean(deg):.2f}, mediano {st.median(deg)}, máx {max(deg)}; nós com grau>=5: {sum(d>=5 for d in deg)}")
print(f"  aresta (m): min {min(lens):.2f}, mediana {st.median(lens):.2f}, máx {max(lens):.2f}; arestas < 3 m: {sum(l<3 for l in lens)}")
print(f"  vizinho mais perto (m): min {min(nn):.2f}, mediana {st.median(nn):.2f}; nós a < 2 m de outro: {sum(d<2 for d in nn)}")
ro = [v for v in radius_over.values() if v > 0]
print(f"  raios com override: {len(ro)} (mediana {st.median(ro) if ro else 0:.2f}); extensão do mapa ~{max(xs)-min(xs):.0f} x {max(zs)-min(zs):.0f} m")

# Auxiliar colado num primário (era a origem de 38 das 53 arestas curtas do NodeTraining2) e
# ligações redundantes: A–B quando existe C ligado aos dois com as duas pernas mais curtas.
prims = [f for f in pos if node_info[f]['primary']]
hug = sum(1 for f in pos if not node_info[f]['primary'] and prims and min(math.dist(pos[f], pos[g]) for g in prims) < 3)
tri = sum(1 for a, b in edges for c in adj[a] & adj[b]) // 3
rng = sum(1 for a, b in edges if any(max(math.dist(pos[a], pos[c]), math.dist(pos[c], pos[b])) < math.dist(pos[a], pos[b]) for c in adj[a] & adj[b]))
wsum = sum(n['weight'] for n in node_info.values() if n['primary'])
print(f"  auxiliares a < 3 m de um primário: {hug}; triângulos: {tri}; ligações redundantes (atalho por vizinho comum): {rng}")
print(f"  soma dos pesos dos primários: {wsum:.2f}")

# ---- Critérios de aceite do grafo gerado (docs/graph/handoff-densidade-do-grafo.md) ----
# A cobertura do chão não sai daqui: rode tools/graph_coverage.py.
checks = [
    ("1 pedaço", len(comps) == 1, f"{len(comps)}"),
    ("toda porta atravessada", doors_missing == 0, f"{len(doors) - doors_missing}/{len(doors)}"),
    ("nenhuma aresta < 3 m", min(lens) >= 3, f"mín {min(lens):.2f} m"),
    ("nenhum nó a < 3 m de outro", min(nn) >= 3, f"mín {min(nn):.2f} m"),
    ("grau médio <= 2.6", st.mean(deg) <= 2.6, f"{st.mean(deg):.2f}"),
    ("grau máximo <= 6", max(deg) <= 6, f"{max(deg)}"),
    ("no máximo 3 nós com grau >= 5", sum(d >= 5 for d in deg) <= 3, f"{sum(d >= 5 for d in deg)}"),
    ("100 a 140 nós", 100 <= len(node_info) <= 140, f"{len(node_info)}"),
    ("20 a 30 primários", 20 <= prim <= 30, f"{prim}"),
    ("soma dos pesos = 23", abs(wsum - 23) < 0.05, f"{wsum:.2f}"),
]
print()
print("ACEITE (grafo):")
for label, ok, value in checks:
    print(f"  [{'ok' if ok else 'X '}] {label:32s} {value}")

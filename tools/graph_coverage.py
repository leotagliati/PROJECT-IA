# Cobertura do chão de um grafo SALVO, medida sem abrir o Unity.
#
# Uso:  python tools/graph_coverage.py Assets/Prefabs/NodeTraining2.prefab
#
# É uma RÉPLICA offline do mapa do chão do NavGraphPlacer (WalkGrid + CoverageMap), para conferir
# o critério de aceite de cobertura depois de salvar o prefab. Lê do .prefab:
#   - todos os BoxCollider ATIVOS na layer de parede, inclusive filhos e prefabs aninhados
#     (paredes, batentes, mobília) — o placer usa a física; aqui cada caixa é rasterizada;
#   - os nós (posição, papel, raio efetivo) e os parâmetros do NavGraph e do NavGraphPlacer.
# E aplica as MESMAS regras do jogo e do placer:
#   - andável = a coluna do corpo (cápsula de raio _linkClearance, de _bodyBottom a _bodyTop em
#     relação à altura dos nós) não encosta em caixa nenhuma, e a célula se liga a um nó;
#   - área de chegada = no raio (Chebyshev no quadrado) E visível do centro na altura dos nós;
#   - vence o centro mais perto; OK = o vencedor alcança a célula por dentro da área;
#   - profundidade = distância andando do chão ruim até o chão OK mais perto.
# Conferida contra o NodeTraining2 de 23/09/2026: 99.84% OK (o placer disse ~100%) e a réplica
# do PlacePrimaries dá os mesmos 59 primários. Diferenças de décimos de ponto vêm da rasterização.
# Requer numpy.
import re, sys, os, math, glob, heapq, collections
import numpy as np

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREFAB = sys.argv[1] if len(sys.argv) > 1 else os.path.join("Assets", "Prefabs", "NodeTraining2.prefab")
if not os.path.isabs(PREFAB):
    PREFAB = os.path.join(ROOT, PREFAB)

SPLIT = re.compile(r"^--- !u!(\d+) &(-?\d+)( stripped)?\s*$", re.M)
MOD = re.compile(r"- target: \{fileID: (-?\d+)[^}]*\}\s*propertyPath: (\S+)\s*value: ([^\n]*)")

guid_path = {}
for meta in glob.glob(os.path.join(ROOT, "Assets", "**", "*.prefab.meta"), recursive=True):
    g = re.search(r"guid: (\w+)", open(meta, encoding="utf-8", errors="ignore").read())
    if g:
        guid_path[g.group(1)] = meta[:-5]

_parsed = {}
def parse(path):
    if path not in _parsed:
        ds = SPLIT.split(open(path, encoding="utf-8", errors="ignore").read())
        _parsed[path] = {ds[i + 1]: (ds[i], bool(ds[i + 2]), ds[i + 3]) for i in range(1, len(ds), 4)}
    return _parsed[path]

def vec(body, key, n):
    m = re.search(key + r": \{([^}]*)\}", body)
    if not m:
        return None
    d = dict(kv.split(": ") for kv in m.group(1).split(", "))
    return [float(d[c]) for c in "xyzw"[:n]]

def fid(body, key):
    m = re.search(key + r": \{fileID: (-?\d+)", body)
    return m.group(1) if m else None

def field(body, key, default):
    m = re.search(r"\n  " + key + r": ([^\n]*)", body)
    return m.group(1).strip() if m else default

def trs(p, q, s):
    x, y, z, w = q
    r = np.array([[1 - 2*(y*y + z*z), 2*(x*y - z*w), 2*(x*z + y*w)],
                  [2*(x*y + z*w), 1 - 2*(x*x + z*z), 2*(y*z - x*w)],
                  [2*(x*z - y*w), 2*(y*z + x*w), 1 - 2*(x*x + y*y)]])
    m = np.eye(4)
    m[:3, :3] = r @ np.diag(s)
    m[:3, 3] = p
    return m

def overridden(v, mods, key, n):
    v = list(v)
    for i, c in enumerate("xyzw"[:n]):
        if f"{key}.{c}" in mods:
            v[i] = float(mods[f"{key}.{c}"])
    return v

# ================================================================================
# Caixas de parede do prefab (com prefabs aninhados e overrides)
# ================================================================================

def collect_boxes(path, parent, mods, wall_layers, out):
    objs = parse(path)
    local, go_of = {}, {}
    for f, (c, st, b) in objs.items():
        if c == "4" and not st:
            m = mods.get(f, {})
            go_of[f] = fid(b, "m_GameObject")
            local[f] = (overridden(vec(b, "m_LocalPosition", 3), m, "m_LocalPosition", 3),
                        overridden(vec(b, "m_LocalRotation", 4), m, "m_LocalRotation", 4),
                        overridden(vec(b, "m_LocalScale", 3), m, "m_LocalScale", 3),
                        fid(b, "m_Father"))
    tr_of_go = {g: t for t, g in go_of.items()}
    stripped = {f: (fid(b, "m_PrefabInstance"), fid(b, "m_CorrespondingSourceObject"))
                for f, (c, st, b) in objs.items() if c == "4" and st}
    instances = {}
    for f, (c, st, b) in objs.items():
        if c == "1001":
            g = re.search(r"m_SourcePrefab: \{fileID: \d+, guid: (\w+)", b)
            m = collections.defaultdict(dict)
            for t, prop, val in MOD.findall(b):
                m[t][prop] = val.strip()
            instances[f] = (guid_path.get(g.group(1)) if g else None, fid(b, "m_TransformParent"), m)

    cache = {}
    def world(t):
        if t in (None, "0"):
            return parent
        if t not in cache:
            if t in local:
                p, q, s, father = local[t]
                cache[t] = world(father) @ trs(p, q, s)
            elif t in stripped:
                cache[t] = instance_world(*stripped[t])
            else:
                cache[t] = parent
        return cache[t]

    def instance_world(inst, source_obj):
        src, tparent, m = instances[inst]
        sobjs = parse(src) if src else {}
        def chain(t):
            if t in (None, "0") or t not in sobjs or sobjs[t][0] != "4" or sobjs[t][1]:
                return world(tparent)
            bb = sobjs[t][2]
            mm = m.get(t, {})
            return chain(fid(bb, "m_Father")) @ trs(overridden(vec(bb, "m_LocalPosition", 3), mm, "m_LocalPosition", 3),
                                                     overridden(vec(bb, "m_LocalRotation", 4), mm, "m_LocalRotation", 4),
                                                     overridden(vec(bb, "m_LocalScale", 3), mm, "m_LocalScale", 3))
        return chain(source_obj)

    def go_active(go):
        b = objs.get(go, ("", False, ""))[2]
        m = re.search(r"m_IsActive: (\d)", b)
        return mods.get(go, {}).get("m_IsActive", m.group(1) if m else "1") == "1"

    inst_active = {}
    def chain_active(t):
        while t not in (None, "0"):
            if t in local:
                if not go_active(go_of[t]):
                    return False
                t = local[t][3]
            elif t in stripped:
                return inst_active.get(stripped[t][0], True)
            else:
                return True
        return True

    for f, (src, tparent, m) in instances.items():
        active = src is not None and chain_active(tparent)
        if active:
            sobjs = parse(src)
            root_go = next((fid(bb, "m_GameObject") for t, (c, st, bb) in sobjs.items()
                            if c == "4" and not st and "m_Father: {fileID: 0}" in bb), None)
            if root_go and m.get(root_go, {}).get("m_IsActive") == "0":
                active = False
        inst_active[f] = active
        if active:
            collect_boxes(src, world(tparent), m, wall_layers, out)

    for f, (c, st, b) in objs.items():
        if c != "65" or st:
            continue
        go = fid(b, "m_GameObject")
        m = mods.get(f, {})
        if m.get("m_Enabled", "1" if "m_Enabled: 1" in b else "0") != "1":
            continue
        if m.get("m_IsTrigger", "1" if "m_IsTrigger: 1" in b else "0") == "1":
            continue
        if go not in objs or go not in tr_of_go:
            continue
        gb = objs[go][2]
        layer = int(mods.get(go, {}).get("m_Layer", re.search(r"m_Layer: (\d+)", gb).group(1)))
        t = tr_of_go[go]
        if layer not in wall_layers or not go_active(go) or not chain_active(local[t][3]):
            continue
        size = overridden(vec(b, "m_Size", 3), m, "m_Size", 3)
        center = overridden(vec(b, "m_Center", 3), m, "m_Center", 3)
        w = world(t)
        corners = [(w @ np.array([center[0] + sx*size[0], center[1] + sy*size[1], center[2] + sz*size[2], 1.0]))[:3]
                   for sx in (-0.5, 0.5) for sy in (-0.5, 0.5) for sz in (-0.5, 0.5)]
        out.append(np.array(corners))

def hull(points):
    pts = sorted(set((round(p[0], 5), round(p[1], 5)) for p in points))
    if len(pts) <= 2:
        return pts
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]

def polygon_distance(px, pz, poly):
    inside = np.ones(px.shape, bool)
    best = np.full(px.shape, np.inf)
    for i in range(len(poly)):
        ax, az = poly[i]
        bx, bz = poly[(i + 1) % len(poly)]
        ex, ez = bx - ax, bz - az
        t = np.clip(((px - ax) * ex + (pz - az) * ez) / max(ex*ex + ez*ez, 1e-12), 0, 1)
        best = np.minimum(best, np.hypot(px - (ax + t*ex), pz - (az + t*ez)))
        inside &= (ex * (pz - az) - ez * (px - ax)) >= 0
    return np.where(inside, 0.0, best)

# ================================================================================
# Grafo e parâmetros
# ================================================================================

objs = parse(PREFAB)
navnode_guid = re.search(r"guid: (\w+)", open(os.path.join(ROOT, "Assets", "Scripts", "Graph", "NavNode.cs.meta")).read()).group(1)
graph_body = next((b for c, st, b in objs.values() if c == "114" and "_nodeShape" in b), "")
placer_body = next((b for c, st, b in objs.values() if c == "114" and "_primaryWeightBudget" in b), "")

wall_mask = int(re.search(r"_wallLayer:\s*\n\s*serializedVersion: \d+\s*\n\s*m_Bits: (\d+)", graph_body).group(1)) if graph_body else 8
WALL_LAYERS = {i for i in range(32) if wall_mask & (1 << i)}
SQUARE = field(graph_body, "_nodeShape", "1") == "1"
PRIMARY_R = float(field(graph_body, "_defaultPrimaryRadius", "1.8"))
AUX_R = float(field(graph_body, "_defaultAuxiliaryRadius", "3"))
LINK = float(field(graph_body, "_linkClearance", "0.85"))
BOTTOM = float(field(graph_body, "_bodyBottom", "-1.6"))
TOP = float(field(graph_body, "_bodyTop", "1.8"))
CLIP = field(graph_body, "_areasStopAtWalls", "1") == "1"
STEP = float(field(placer_body, "_generationStep", "0.3"))
TARGET = float(field(placer_body, "_coverageTarget", "0.97"))
MAX_DEPTH = float(field(placer_body, "_maxHoleDepth", "1"))
MAX_AUX_R = float(field(placer_body, "_maxAuxiliaryRadius", "8"))

local, tr_by_go = {}, {}
for f, (c, st, b) in objs.items():
    if c == "4" and not st:
        local[f] = (vec(b, "m_LocalPosition", 3), vec(b, "m_LocalRotation", 4), vec(b, "m_LocalScale", 3), fid(b, "m_Father"))
        tr_by_go[fid(b, "m_GameObject")] = f
def node_world(t):
    if t in (None, "0") or t not in local:
        return np.eye(4)
    p, q, s, father = local[t]
    return node_world(father) @ trs(p, q, s)

nodes = {}
for f, (c, st, b) in objs.items():
    if c == "114" and navnode_guid in b:
        w = node_world(tr_by_go[fid(b, "m_GameObject")])
        primary = field(b, "_kind", "0") == "0"
        over = float(field(b, "_radiusOverride", "0"))
        nodes[f] = dict(x=w[0, 3], y=w[1, 3], z=w[2, 3], primary=primary,
                        r=over if over > 0 else (PRIMARY_R if primary else AUX_R))
# Ordem do NavGraph._nodes (desempate do FindNodeAt).
order = re.findall(r"- \{fileID: (-?\d+)\}", graph_body.split("_nodes:")[1].split("_makeLinksBidirectional")[0]) if graph_body else []
node_list = [nodes[f] for f in order if f in nodes] + [n for f, n in nodes.items() if f not in order]
heights = sorted(n["y"] for n in node_list)
Y = heights[len(heights) // 2]

# ================================================================================
# Mapa do chão
# ================================================================================

boxes = []
collect_boxes(PREFAB, np.eye(4), {}, WALL_LAYERS, boxes)
allc = np.concatenate(boxes)
MINX, MINZ = allc[:, 0].min(), allc[:, 2].min()
SX = int(math.ceil((allc[:, 0].max() - MINX) / STEP)) + 1
SZ = int(math.ceil((allc[:, 2].max() - MINZ) / STEP)) + 1
free = np.ones((SZ, SX), bool)
sight = np.zeros((SZ, SX), bool)
cyl_lo, cyl_hi = Y + BOTTOM + LINK, Y + TOP - LINK
for corners in boxes:
    ylo, yhi = corners[:, 1].min(), corners[:, 1].max()
    poly = hull([(c[0], c[2]) for c in corners])
    if len(poly) < 3:
        continue
    # Alcance horizontal da cápsula na altura da caixa (semiesferas nas pontas).
    if yhi >= cyl_lo and ylo <= cyl_hi:
        reach = LINK
    elif yhi < cyl_lo:
        reach = math.sqrt(LINK*LINK - (cyl_lo - yhi)**2) if cyl_lo - yhi < LINK else -1
    else:
        reach = math.sqrt(LINK*LINK - (ylo - cyl_hi)**2) if ylo - cyl_hi < LINK else -1
    xs = [p[0] for p in poly]; zs = [p[1] for p in poly]
    pad = max(reach, 0) + STEP
    x0 = max(0, int((min(xs) - pad - MINX) / STEP)); x1 = min(SX - 1, int(math.ceil((max(xs) + pad - MINX) / STEP)))
    z0 = max(0, int((min(zs) - pad - MINZ) / STEP)); z1 = min(SZ - 1, int(math.ceil((max(zs) + pad - MINZ) / STEP)))
    if x1 < x0 or z1 < z0:
        continue
    X, Z = np.meshgrid(MINX + np.arange(x0, x1 + 1) * STEP, MINZ + np.arange(z0, z1 + 1) * STEP)
    d = polygon_distance(X, Z, poly)
    if reach >= 0:
        free[z0:z1 + 1, x0:x1 + 1] &= ~(d < reach)
    # O que bloqueia a VISÃO na altura dos nós (a caixinha fina do placer).
    if yhi >= Y - 0.05 and ylo <= Y + 0.05:
        sight[z0:z1 + 1, x0:x1 + 1] |= d <= STEP * 0.5

def snap(mask, x, z, max_distance):
    cx = int(round((x - MINX) / STEP)); cz = int(round((z - MINZ) / STEP))
    r = int(math.ceil(max_distance / STEP))
    best, best_d = None, 1e18
    for zz in range(cz - r, cz + r + 1):
        for xx in range(cx - r, cx + r + 1):
            if 0 <= zz < SZ and 0 <= xx < SX and mask[zz, xx]:
                d = (xx - cx)**2 + (zz - cz)**2
                if d < best_d:
                    best, best_d = (zz, xx), d
    return best if best is not None and best_d <= r*r else None

walk = np.zeros_like(free)
queue = collections.deque()
for n in node_list:
    c = snap(free, n["x"], n["z"], 2.0)
    if c and not walk[c]:
        walk[c] = True; queue.append(c)
while queue:
    z, x = queue.popleft()
    for dz in (-1, 0, 1):
        for dx in (-1, 0, 1):
            a, b = z + dz, x + dx
            if 0 <= a < SZ and 0 <= b < SX and free[a, b] and not walk[a, b]:
                walk[a, b] = True; queue.append((a, b))

# ================================================================================
# Cobertura (mesma regra do NavGraph.FindNodeAt e do CoverageMap)
# ================================================================================

VR = int(math.ceil(max([MAX_AUX_R, PRIMARY_R, AUX_R] + [n["r"] for n in node_list]) / STEP)) + 1

def visibility(cz, cx):
    size = 2 * VR + 1
    vis = np.zeros((size, size), bool)
    vis[VR, VR] = True
    if not CLIP:
        vis[:] = True
        return vis
    def march(tx, tz):
        steps = max(abs(tx), abs(tz)) * 2
        for s in range(1, steps + 1):
            dx = round(tx * s / steps); dz = round(tz * s / steps)
            x, z = cx + dx, cz + dz
            if not (0 <= x < SX and 0 <= z < SZ) or sight[z, x]:
                return
            vis[dz + VR, dx + VR] = True
    for side in range(-VR, VR + 1):
        march(side, -VR); march(side, VR); march(-VR, side); march(VR, side)
    return vis

def area_distance(ax, az, bx, bz):
    return max(abs(ax - bx), abs(az - bz)) if SQUARE else math.hypot(ax - bx, az - bz)

winner = {}          # célula -> (distância, nó)
reach_sets = []
for i, n in enumerate(node_list):
    reach_sets.append(set())
    origin = snap(walk, n["x"], n["z"], STEP * 1.5)
    if origin is None:
        continue
    oz, ox = origin
    vis = visibility(oz, ox)
    r = n["r"]
    x0 = max(0, int(math.floor((n["x"] - r - MINX) / STEP))); x1 = min(SX - 1, int(math.ceil((n["x"] + r - MINX) / STEP)))
    z0 = max(0, int(math.floor((n["z"] - r - MINZ) / STEP))); z1 = min(SZ - 1, int(math.ceil((n["z"] + r - MINZ) / STEP)))
    area = {}
    for z in range(z0, z1 + 1):
        for x in range(x0, x1 + 1):
            if not walk[z, x]:
                continue
            d = area_distance(n["x"], n["z"], MINX + x * STEP, MINZ + z * STEP)
            if d > r or not (abs(x - ox) <= VR and abs(z - oz) <= VR and vis[z - oz + VR, x - ox + VR]):
                continue
            area[(z, x)] = d
            if d < winner.get((z, x), (float("inf"), -1))[0]:
                winner[(z, x)] = (d, i)
    if area_distance(n["x"], n["z"], MINX + ox * STEP, MINZ + oz * STEP) > r:
        continue
    seen, stack = {origin}, [origin]
    while stack:
        z, x = stack.pop()
        for dz in (-1, 0, 1):
            for dx in (-1, 0, 1):
                c = (z + dz, x + dx)
                if c in area and c not in seen:
                    seen.add(c); stack.append(c)
    reach_sets[i] = seen

state = {}   # 1 OK, 2 nó errado, 3 sem nó
for z, x in zip(*np.nonzero(walk)):
    w = winner.get((z, x))
    state[(z, x)] = 3 if w is None else (1 if (z, x) in reach_sets[w[1]] else 2)

# Profundidade: Dijkstra a partir do chão OK, só por chão ruim.
depth = {}
heap = []
diag = STEP * math.sqrt(2)
for c, s in state.items():
    if s == 1:
        continue
    z, x = c
    best = min((STEP if dz == 0 or dx == 0 else diag for dz in (-1, 0, 1) for dx in (-1, 0, 1)
                if (dz or dx) and state.get((z + dz, x + dx)) == 1), default=None)
    if best is not None:
        depth[c] = best; heapq.heappush(heap, (best, c))
while heap:
    d, c = heapq.heappop(heap)
    if d > depth.get(c, float("inf")):
        continue
    z, x = c
    for dz in (-1, 0, 1):
        for dx in (-1, 0, 1):
            n = (z + dz, x + dx)
            if (dz or dx) and state.get(n, 1) != 1:
                nd = d + (STEP if dz == 0 or dx == 0 else diag)
                if nd < depth.get(n, float("inf")):
                    depth[n] = nd; heapq.heappush(heap, (nd, n))
bad = [c for c, s in state.items() if s != 1]
deepest = max((depth.get(c, float("inf")) for c in bad), default=0.0)

total = len(state)
count = collections.Counter(state.values())
ok = count[1] / total
print(f"{os.path.basename(PREFAB)}: {len(node_list)} nós, chão andável {total * STEP * STEP:.0f} m² "
      f"({len(boxes)} caixas de parede/móvel)")
print(f"  OK {ok:.2%}   nó errado {count[2] / total:.2%}   sem nó {count[3] / total:.2%}")
print(f"  chão ruim a até {deepest:.2f} m do chão OK")
print()
print("ACEITE (cobertura):")
for label, passed, value in ((f"chão OK >= {TARGET:.0%}", ok >= TARGET, f"{ok:.2%}"),
                             (f"chão ruim a até {MAX_DEPTH:.1f} m do OK", deepest <= MAX_DEPTH + 1e-3, f"{deepest:.2f} m")):
    print(f"  [{'ok' if passed else 'X '}] {label:32s} {value}")

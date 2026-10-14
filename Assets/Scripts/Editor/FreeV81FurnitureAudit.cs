using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts.Free;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Diagnóstico (12/10) da "V8.1 - Mobiliada": como o monstro sente os móveis. Lê a 1ª arena e escreve em
/// Logs/furniture_audit.txt: altura dos raios, do olho e da cápsula; máscaras de layer dos componentes do monstro;
/// por móvel com colisor, layer, altura (base/topo acima do piso), se os raios passam por cima e se o NavMesh
/// passa por baixo/através dele (o radar só enxerga o NavMesh). Só lê; não salva nada.
/// Batchmode: -executeMethod FreeV81FurnitureAudit.Run
/// </summary>
public static class FreeV81FurnitureAudit
{
    private const string Scene = "Assets/Scenes/Arthur/V8.1 - Mobiliada.unity";
    private const string Output = "Logs/furniture_audit.txt";

    [MenuItem("PROJECT-IA/v8.1: auditar móveis (diagnóstico)")]
    public static void Run()
    {
        // Abre outra cena: no editor, pergunta antes de descartar o que está aberto e não salvo.
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var r = new StringBuilder();
        try
        {
            Audit(r);
        }
        catch (System.Exception e)
        {
            r.AppendLine("ERRO: " + e);
        }

        System.IO.Directory.CreateDirectory("Logs");
        System.IO.File.WriteAllText(Output, r.ToString());
        Debug.Log($"[FreeV81FurnitureAudit] escrito em {Output}");
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    private static void Audit(StringBuilder r)
    {
        EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
        FreeArenaController arena = Object.FindObjectsByType<FreeArenaController>(FindObjectsSortMode.InstanceID)
            .OrderBy(a => a.transform.position.x).First();
        FreeExplorerManager agent = arena.GetComponentInChildren<FreeExplorerManager>(true);
        FreeMap map = arena.GetComponentInChildren<FreeMap>(true);

        float floorY = float.NaN;
        if (map != null)
        {
            var floorProp = typeof(FreeMap).GetProperty("FloorY");
            if (floorProp != null)
                floorY = (float)floorProp.GetValue(map);
        }

        r.AppendLine($"arena '{arena.name}' em {arena.transform.position}");

        // ---------------- monstro ----------------
        var capsule = agent.GetComponent<CapsuleCollider>();
        Bounds cb = capsule != null ? capsule.bounds : default;
        if (float.IsNaN(floorY))
            floorY = cb.min.y;
        r.AppendLine($"piso y={floorY:0.00} (alturas abaixo são acima do piso)");
        if (capsule != null)
            r.AppendLine($"monstro '{agent.name}': cápsula de {cb.min.y - floorY:0.00} a {cb.max.y - floorY:0.00} m, raio no mundo ~{cb.extents.x:0.00} m");

        foreach (RayPerceptionSensorComponent3D rays in agent.GetComponentsInChildren<RayPerceptionSensorComponent3D>(true))
        {
            float y = rays.transform.position.y - floorY;
            r.AppendLine($"raios '{rays.SensorName}' em '{rays.name}' (ativo={rays.isActiveAndEnabled}): altura {y:0.00} m " +
                         $"(+{rays.StartVerticalOffset:0.00}/+{rays.EndVerticalOffset:0.00}), esfera r={rays.SphereCastRadius:0.00}, " +
                         $"comprimento {rays.RayLength}, {rays.RaysPerDirection}x2+1 raios em ±{rays.MaxRayDegrees}°, máscara {MaskNames(rays.RayLayerMask)}");
            r.AppendLine($"   -> a esfera cobre de {y - rays.SphereCastRadius:0.00} a {y + rays.SphereCastRadius:0.00} m acima do piso");
        }

        r.AppendLine("máscaras e alturas nos componentes do monstro:");
        foreach (MonoBehaviour mb in agent.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null)
                continue;
            var so = new SerializedObject(mb);
            SerializedProperty p = so.GetIterator();
            var bits = new List<string>();
            while (p.NextVisible(true))
            {
                if (p.propertyType == SerializedPropertyType.LayerMask)
                    bits.Add($"{p.name}={MaskNames(p.intValue)}");
                else if (p.propertyType == SerializedPropertyType.Float &&
                         (p.name.ToLower().Contains("eye") || p.name.ToLower().Contains("height") || p.name.ToLower().Contains("view")))
                    bits.Add($"{p.name}={p.floatValue:0.##}");
            }
            if (bits.Count > 0)
                r.AppendLine($"  {mb.GetType().Name}: {string.Join(", ", bits)}");
        }

        // ---------------- móveis ----------------
        Transform furniture = FindDeep(arena.transform, "Map_Objects");
        if (furniture == null)
        {
            r.AppendLine("SEM Map_Objects na arena");
            return;
        }

        float rayY = agent.GetComponentsInChildren<RayPerceptionSensorComponent3D>(true)
            .Where(x => x.isActiveAndEnabled).Select(x => x.transform.position.y - floorY).DefaultIfEmpty(float.NaN).First();
        float rayR = agent.GetComponentsInChildren<RayPerceptionSensorComponent3D>(true)
            .Where(x => x.isActiveAndEnabled).Select(x => x.SphereCastRadius).DefaultIfEmpty(0f).First();
        float bodyTop = capsule != null ? cb.max.y - floorY : 3f;
        float bodyBottom = capsule != null ? cb.min.y - floorY : 0f;

        var rows = new List<(string name, int layer, string type, float bottom, float top, Vector2 size, bool navUnder, bool rayMiss, bool bodyHits)>();
        foreach (Collider c in furniture.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy)
                continue;
            Bounds b = c.bounds;
            float bottom = b.min.y - floorY, top = b.max.y - floorY;
            bool bodyHits = bottom < bodyTop && top > bodyBottom + 0.05f;
            bool rayMiss = !float.IsNaN(rayY) && (top < rayY - rayR || bottom > rayY + rayR);

            // NavMesh no centro da pegada do móvel, na altura do piso: achou a < 0.15 m na horizontal = o NavMesh
            // passa por baixo/através (o radar não vê o móvel).
            var probe = new Vector3(b.center.x, floorY, b.center.z);
            bool navUnder = NavMesh.SamplePosition(probe, out NavMeshHit hit, 0.5f, NavMesh.AllAreas)
                            && new Vector2(hit.position.x - probe.x, hit.position.z - probe.z).magnitude < 0.15f;
            rows.Add((PathOf(c.transform, furniture), c.gameObject.layer, c.GetType().Name, bottom, top,
                new Vector2(b.size.x, b.size.z), navUnder, rayMiss, bodyHits));
        }

        r.AppendLine();
        r.AppendLine($"MÓVEIS com colisor sólido: {rows.Count}");
        foreach (var g in rows.GroupBy(x => x.layer))
            r.AppendLine($"  layer {LayerMask.LayerToName(g.Key)}: {g.Count()}");
        var hitters = rows.Where(x => x.bodyHits && x.size.x * x.size.y > 0.05f).ToList();
        r.AppendLine($"que o corpo do monstro pode bater (altura cruza a cápsula, pegada > 0.05 m²): {hitters.Count}");
        r.AppendLine($"  desses, os RAIOS passam por cima/baixo (invisíveis ao sensor de parede): {hitters.Count(x => x.rayMiss)}");
        r.AppendLine($"  desses, o NAVMESH passa por baixo/através (invisíveis ao radar): {hitters.Count(x => x.navUnder)}");
        r.AppendLine($"  desses, invisíveis aos DOIS: {hitters.Count(x => x.rayMiss && x.navUnder)}");
        r.AppendLine($"  desses, na layer Default (a visão atravessa): {hitters.Count(x => x.layer == 0)}");

        r.AppendLine();
        r.AppendLine("alturas dos que o corpo bate (topo acima do piso):");
        foreach (var bucket in new[] { (0f, 0.5f), (0.5f, 1f), (1f, 1.5f), (1.5f, 2.5f), (2.5f, 99f) })
            r.AppendLine($"  topo {bucket.Item1:0.0}-{bucket.Item2:0.0} m: {hitters.Count(x => x.top >= bucket.Item1 && x.top < bucket.Item2)}" +
                         $" (raios não veem: {hitters.Count(x => x.top >= bucket.Item1 && x.top < bucket.Item2 && x.rayMiss)}," +
                         $" NavMesh passa: {hitters.Count(x => x.top >= bucket.Item1 && x.top < bucket.Item2 && x.navUnder)})");

        r.AppendLine();
        r.AppendLine("LISTA (camada | tipo | base-topo m | pegada m | raios? | navmesh?):");
        foreach (var x in hitters.OrderBy(x => x.name))
            r.AppendLine($"  {LayerMask.LayerToName(x.layer),-8} {x.type,-15} {x.bottom,5:0.00}-{x.top,5:0.00}  {x.size.x:0.0}x{x.size.y:0.0}" +
                         $"  {(x.rayMiss ? "RAIO NÃO VÊ" : "raio vê     ")}  {(x.navUnder ? "NAVMESH PASSA" : "navmesh corta")}  {x.name}");

        // Peças grandes sem colisor (o monstro atravessa; não batem, mas tapam visão do jogador no jogo).
        int noCollider = 0;
        foreach (MeshRenderer mr in furniture.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (mr.GetComponentInParent<Collider>() != null || mr.GetComponentInChildren<Collider>() != null)
                continue;
            if (mr.bounds.size.y >= 0.4f)
                noCollider++;
        }
        r.AppendLine();
        r.AppendLine($"renderers >= 0.4 m sem colisor nenhum (o monstro atravessa): {noCollider}");
    }

    // Os móveis com colisor em que o NavMesh passa por dentro: compara colisor x malha, olha o bake e a original.
    public static void Suspects()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        string[] names = { "coffee_machine", "Bookcase_01", "whiteboard", "Kitchen_stoves" };
        var r = new StringBuilder();
        try
        {
            EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
            FreeArenaController arena = Object.FindObjectsByType<FreeArenaController>(FindObjectsSortMode.InstanceID)
                .OrderBy(a => a.transform.position.x).First();
            var surface = arena.GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            r.AppendLine($"NavMeshSurface: collect={surface.collectObjects} geometry={surface.useGeometry} layers={MaskNames(surface.layerMask)} " +
                         $"agentType={surface.agentTypeID} data={(surface.navMeshData != null ? surface.navMeshData.name : "null")}");
            var settings = NavMesh.GetSettingsByID(surface.agentTypeID);
            r.AppendLine($"agente do bake: raio {settings.agentRadius:0.00} altura {settings.agentHeight:0.00} degrau {settings.agentClimb:0.00}");
            Transform furniture = FindDeep(arena.transform, "Map_Objects");
            foreach (Transform t in furniture.GetComponentsInChildren<Transform>(true))
            {
                if (!names.Any(n => t.name.StartsWith(n)) || t.parent == null || names.Any(n => t.parent.name.StartsWith(n)))
                    continue;
                r.AppendLine();
                r.AppendLine($"== {PathOf(t, furniture)}  ativo={t.gameObject.activeInHierarchy} layer={LayerMask.LayerToName(t.gameObject.layer)} " +
                             $"escala={t.lossyScale} prefab={(PrefabUtility.IsPartOfPrefabInstance(t) ? AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject)) : "-")}");
                Bounds? mesh = null;
                foreach (Renderer rd in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (mesh == null) mesh = rd.bounds;
                    else { Bounds b = mesh.Value; b.Encapsulate(rd.bounds); mesh = b; }
                }
                if (mesh != null)
                    r.AppendLine($"   malha: centro {mesh.Value.center} tamanho {mesh.Value.size}");
                foreach (Collider c in t.GetComponentsInChildren<Collider>(true))
                {
                    bool added = PrefabUtility.IsAddedComponentOverride(c);
                    r.AppendLine($"   colisor {c.GetType().Name} em '{c.name}' (layer {LayerMask.LayerToName(c.gameObject.layer)}, trigger={c.isTrigger}, " +
                                 $"enabled={c.enabled}, override adicionado={added}): centro {c.bounds.center} tamanho {c.bounds.size}");
                }
                foreach (var mod in t.GetComponentsInParent<Unity.AI.Navigation.NavMeshModifier>(true))
                    r.AppendLine($"   NavMeshModifier em '{mod.name}': ignoreFromBuild={mod.ignoreFromBuild} overrideArea={mod.overrideArea} area={mod.area}");
                if (mesh != null)
                {
                    var probe = new Vector3(mesh.Value.center.x, mesh.Value.min.y, mesh.Value.center.z);
                    bool on = NavMesh.SamplePosition(probe, out NavMeshHit hit, 1f, NavMesh.AllAreas);
                    r.AppendLine($"   NavMesh mais perto do centro da malha: {(on ? hit.position.ToString() + $" a {Vector3.Distance(hit.position, probe):0.00} m" : "nenhum a 1 m")}");
                }
            }
        }
        catch (System.Exception e)
        {
            r.AppendLine("ERRO: " + e);
        }

        System.IO.File.WriteAllText("Logs/furniture_suspects.txt", r.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    // Reassa o NavMesh da 1ª arena SÓ NA MEMÓRIA (não salva cena nem asset) e conta, entre os móveis em que o corpo
    // bate, onde o NavMesh passa por dentro antes e depois: diz se o asset assado está velho.
    public static void BakeTest()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var r = new StringBuilder();
        try
        {
            EditorSceneManager.OpenScene(Scene, OpenSceneMode.Single);
            FreeArenaController arena = Object.FindObjectsByType<FreeArenaController>(FindObjectsSortMode.InstanceID)
                .OrderBy(a => a.transform.position.x).First();
            var surface = arena.GetComponent<Unity.AI.Navigation.NavMeshSurface>();
            Transform furniture = FindDeep(arena.transform, "Map_Objects");
            float floorY = arena.GetComponentInChildren<FreeMap>(true).FloorY;

            List<string> Through()
            {
                var list = new List<string>();
                foreach (Collider c in furniture.GetComponentsInChildren<Collider>(true))
                {
                    if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy || c.bounds.size.x * c.bounds.size.z < 0.05f)
                        continue;
                    Bounds b = c.bounds;
                    var probe = new Vector3(b.center.x, floorY, b.center.z);
                    if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 0.5f, NavMesh.AllAreas)
                        && new Vector2(hit.position.x - probe.x, hit.position.z - probe.z).magnitude < 0.15f
                        && Mathf.Abs(hit.position.y - floorY) < 0.3f)
                        list.Add(PathOf(c.transform, furniture));
                }
                return list;
            }

            List<string> before = Through();
            r.AppendLine($"ANTES (asset salvo): NavMesh no piso dentro de {before.Count} móvel(is): {string.Join(", ", before)}");

            // A física acha o colisor no centro da pegada, a 0.5 e a 1.5 m do piso? E qual é a caixa real (local)?
            Physics.SyncTransforms();
            foreach (Collider c in furniture.GetComponentsInChildren<Collider>(true))
            {
                if (!before.Contains(PathOf(c.transform, furniture)))
                    continue;
                Bounds b = c.bounds;
                string Hits(float h) => string.Join("+", Physics.OverlapBox(new Vector3(b.center.x, floorY + h, b.center.z),
                    Vector3.one * 0.05f, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore).Select(x => x.name));
                string local = c is BoxCollider box ? $"box local centro {box.center} tamanho {box.size}" : c.GetType().Name;
                r.AppendLine($"  {PathOf(c.transform, furniture)}: {local}; rot {c.transform.eulerAngles}; " +
                             $"física a 0.5 m: [{Hits(0.5f)}] a 1.5 m: [{Hits(1.5f)}]");
            }

            // Como o FreeV9Setup: atores desligados no bake (senão viram buraco). Só nas outras arenas o NavMesh
            // antigo continua, mas a 250 m daqui não interfere na amostra.
            var actors = arena.GetComponentsInChildren<FreeExplorerManager>(true).Select(x => x.gameObject)
                .Concat(arena.GetComponentsInChildren<FreeHider>(true).Select(x => x.gameObject))
                .Concat(arena.GetComponentsInChildren<PlayerMovement>(true).Select(x => x.gameObject))
                .Where(g => g.activeSelf).ToList();
            foreach (GameObject g in actors) g.SetActive(false);
            surface.BuildNavMesh();
            foreach (GameObject g in actors) g.SetActive(true);

            List<string> after = Through();
            r.AppendLine($"DEPOIS (reassado agora, só na memória): {after.Count}: {string.Join(", ", after)}");
        }
        catch (System.Exception e)
        {
            r.AppendLine("ERRO: " + e);
        }

        System.IO.File.WriteAllText("Logs/furniture_baketest.txt", r.ToString());
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    // Por sala do FreeMap: quantos pontos de visão ficam DENTRO de colisor (nunca serão vistos), quantos não têm
    // caminho NavMesh desde a sala 0, e o grupo de móveis mais perto do centro (o nome da sala na TaskFurniture).
    // Sala "vista" = 90% dos pontos (FreeExplorationMemory._roomSeenThreshold): mais de 10% impossível = nunca conta.
    [MenuItem("PROJECT-IA/v8.1: auditar salas (diagnóstico)")]
    public static void Rooms()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        WriteRoomReport(Scene, "Logs/furniture_rooms.txt");
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    /// <summary>
    /// O relatório por sala da 1ª arena de <paramref name="scene"/> em <paramref name="output"/>. Abre a cena sem
    /// perguntar (quem chama salva antes). Também usado pela v8.2. Devolve quantas salas nunca chegam a 90%.
    /// </summary>
    internal static int WriteRoomReport(string scene, string output)
    {
        var r = new StringBuilder();
        int hopeless = 0;
        try
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            FreeArenaController arena = Object.FindObjectsByType<FreeArenaController>(FindObjectsSortMode.InstanceID)
                .OrderBy(a => a.transform.position.x).First();
            FreeMap map = arena.GetComponentInChildren<FreeMap>(true);
            Physics.SyncTransforms();
            Transform furniture = FindDeep(arena.transform, "Map_Objects");
            var groups = furniture.Cast<Transform>().ToList();
            int mask = LayerMask.GetMask("Wall", "Obstacle", "Door");
            var path = new NavMeshPath();
            // RoomPoints não monta o mapa sozinho; RoomCount monta (EnsureBuilt).
            if (map.RoomCount == 0)
                throw new System.Exception("FreeMap sem salas (NavMesh não carregado?)");
            Vector3 origin = map.PointGround(map.RoomPoints(0)[0]);

            r.AppendLine($"{map.RoomCount} salas, {map.PointCount} pontos, {map.DoorCount} portas; ponto de visão a {map.PointHeight:0.0} m do piso");
            for (int room = 0; room < map.RoomCount; room++)
            {
                IReadOnlyList<int> points = map.RoomPoints(room);
                int inside = 0, unreachable = 0;
                var insideNames = new Dictionary<string, int>();
                foreach (int p in points)
                {
                    Collider[] hits = Physics.OverlapSphere(map.PointTarget(p), 0.05f, mask, QueryTriggerInteraction.Ignore);
                    if (hits.Length > 0)
                    {
                        inside++;
                        string n = hits[0].name;
                        insideNames[n] = insideNames.TryGetValue(n, out int k) ? k + 1 : 1;
                    }
                    if (!NavMesh.CalculatePath(origin, map.PointGround(p), NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                        unreachable++;
                }

                Vector3 c = map.RoomCentroid(room);
                // O grupo da TaskFurniture (filho do Map_Objects) com mais colisores perto dos pontos desta sala.
                var votes = new Dictionary<string, int>();
                foreach (int p in points)
                    foreach (Collider h in Physics.OverlapSphere(map.PointGround(p), 2.5f, mask, QueryTriggerInteraction.Ignore))
                    {
                        Transform g = h.transform;
                        while (g.parent != null && g.parent != furniture) g = g.parent;
                        if (g.parent == furniture)
                            votes[g.name] = votes.TryGetValue(g.name, out int v) ? v + 1 : 1;
                    }
                string near = string.Join("/", votes.OrderByDescending(x => x.Value).Take(2).Select(x => x.Key));
                int furnitureCount = new HashSet<Collider>(points.SelectMany(p => Physics.OverlapSphere(map.PointGround(p), 2.5f,
                    LayerMask.GetMask("Obstacle"), QueryTriggerInteraction.Ignore))).Count;
                near += $" ({furnitureCount} móveis)";
                float bad = points.Count > 0 ? (float)(inside + unreachable) / points.Count : 0f;
                if (bad > 0.1f)
                    hopeless++;
                r.AppendLine($"S{room:00} {points.Count,4} pts  dentro de colisor {inside,3}  sem caminho {unreachable,3}  " +
                             $"({bad:P0} impossíveis{(bad > 0.1f ? "  <<< NUNCA CHEGA A 90%" : "")})  portas {map.DoorsOfRoom(room).Count}  " +
                             $"centro ({c.x:0},{c.z:0}) perto de '{near}'" +
                             (insideNames.Count > 0 ? "  em: " + string.Join(", ", insideNames.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key}×{x.Value}")) : ""));
            }
        }
        catch (System.Exception e)
        {
            r.AppendLine("ERRO: " + e);
        }

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
        System.IO.File.WriteAllText(output, r.ToString());
        return hopeless;
    }

    private static string MaskNames(int mask)
    {
        if (mask == 0) return "Nothing";
        if (mask == ~0) return "Everything";
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0)
                names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? $"#{i}" : LayerMask.LayerToName(i));
        return $"{mask}[{string.Join("+", names)}]";
    }

    private static Transform FindDeep(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name)
                return t;
        return null;
    }

    private static string PathOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (Transform x = t; x != null && x != root; x = x.parent)
            parts.Add(x.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}

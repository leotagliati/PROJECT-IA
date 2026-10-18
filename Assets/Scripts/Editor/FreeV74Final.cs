using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// v7.4: transforma o "Mapa Final" da "Cena Final - Mapa" em arena de treino (prefab, NavMesh, bordas tampadas, cena de treino
/// e build em Builds/V7.4), herdando o cérebro da v7.3. Refazer ao mexer no mapa ou no código. O jogo não é tocado.
/// </summary>
public static class FreeV74Final
{
    // 18/10: o mapa novo do Arthur (Mapa 1); a "Cena Final - Mapa" antiga ficou sem o NavMesh dela.
    private const string SourceScene = "Assets/Scenes/Arthur/Cena Final - Mapa 1.unity";
    private const string SourceRoot = "Mapa Final";
    private const string HiderSourcePrefab = "Assets/Prefabs/Arenas/V8.2/Map - V8.prefab";
    private const string HiderName = "Hider";
    private const string ArenaFolder = "Assets/Prefabs/Arenas/V7.4";
    private const string ArenaPrefabPath = ArenaFolder + "/Arena - Cena Final.prefab";
    private const string NavMeshAssetPath = ArenaFolder + "/Arena - Cena Final NavMesh.asset";
    private const string TrainingScene = "Assets/Scenes/Arthur/v8.3/Cena Final - Treino.unity";
    private const string BuildPath = "Builds/V7.4/PROJECT-IA.exe";
    private const string RoomReport = "Logs/v7.4_rooms.txt";
    private const string BlockerGroup = "Bloqueios de borda (treino)";
    private const string EdgeDump = "Logs/v7.4_navmesh.txt";

    // Fração mínima da área da maior peça do NavMesh para contar como chão (tampo de móvel fica abaixo).
    private const float MainFloorFraction = 0.05f;
    private const string PlayerTag = "Goal";
    private const int Arenas = 5;

    private static readonly string[] IrrelevantChildren = { "Garrafas" };

    // Folga entre arenas em X; maior que a visão travada (35 m).
    private const float ArenaGap = 60f;

    [MenuItem("PROJECT-IA/v7.4: preparar Cena Final, cena de treino e build")]
    public static void PrepareAll() => Prepare(build: true);

    [MenuItem("PROJECT-IA/v7.4: preparar Cena Final e cena de treino (sem build)")]
    public static void PrepareWithoutBuild() => Prepare(build: false);

    private static void Prepare(bool build)
    {
        if (!CanRun())
            return;

        float spacing = 0f;
        bool ok = EnsurePrefab() && ConfigurePrefab() && BakeSealAndReport(out spacing)
                  && SetUpTrainingScene(spacing) && (!build || Build());

        if (ok)
        {
            int hopeless = FreeV81FurnitureAudit.WriteRoomReport(TrainingScene, RoomReport);
            if (hopeless < 0)
                Debug.LogError($"[FreeV74Final] o relatório de salas falhou (veja {RoomReport}): NavMesh carregado?");
            else
                Debug.Log($"[FreeV74Final] salas: {RoomReport}" + (hopeless > 0
                    ? $" — {hopeless} sala(s) com mais de 10% dos pontos impossíveis (nunca chegam a 90%)."
                    : " — nenhuma sala impossível."));
        }

        Debug.Log(ok ? "[FreeV74Final] PRONTO." : "[FreeV74Final] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    private static bool CanRun()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV74Final] saia do Play antes.");
            return false;
        }

        return Application.isBatchMode || EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
    }

    // Só cria se não existir (o prefab pode ganhar ajustes à mão); a cena de jogo é fechada sem salvar.
    // Recriado toda vez: a Cena Final é a fonte do mapa, e o prefab não guarda ajuste à mão.
    private static bool EnsurePrefab()
    {
        if (!AssetDatabase.IsValidFolder(ArenaFolder))
            AssetDatabase.CreateFolder(Path.GetDirectoryName(ArenaFolder).Replace('\\', '/'), Path.GetFileName(ArenaFolder));

        Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == SourceRoot);
        if (root == null)
        {
            Debug.LogError($"[FreeV74Final] {SourceScene} sem o objeto raiz '{SourceRoot}'.");
            return false;
        }

        try
        {
            if (root.GetComponentInChildren<FreeHider>(true) == null && !BringHider(scene, root.transform))
                return false;

            PrefabUtility.SaveAsPrefabAsset(root, ArenaPrefabPath, out bool saved);
            if (!saved)
                Debug.LogError($"[FreeV74Final] não consegui salvar {ArenaPrefabPath}.");
            else
                Debug.Log($"[FreeV74Final] {ArenaPrefabPath} criado a partir do '{SourceRoot}' da {SourceScene}.");
            return saved;
        }
        finally
        {
            // Descarta a presa e o Map - V8 desempacotado: a cena de jogo não muda no disco.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    // A Hider do Map - V8; desempacotado só um nível para ela manter os overrides.
    private static bool BringHider(Scene scene, Transform arenaRoot)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(HiderSourcePrefab);
        if (source == null)
        {
            Debug.LogError($"[FreeV74Final] falta {HiderSourcePrefab} (de onde vem a presa).");
            return false;
        }

        var map = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
        PrefabUtility.UnpackPrefabInstance(map, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
        Transform hider = map.transform.Find(HiderName);
        if (hider == null || hider.GetComponent<FreeHider>() == null)
        {
            Object.DestroyImmediate(map);
            Debug.LogError($"[FreeV74Final] o {HiderSourcePrefab} sem o filho '{HiderName}' com FreeHider.");
            return false;
        }

        hider.SetParent(arenaRoot, false);
        hider.localPosition = Vector3.zero;
        Object.DestroyImmediate(map);
        Debug.Log($"[FreeV74Final] presa '{HiderName}' trazida do Map - V8.");
        return true;
    }

    private static bool ConfigurePrefab()
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(ArenaPrefabPath);
        try
        {
            foreach (string name in IrrelevantChildren)
            {
                Transform child = contents.transform.Find(name);
                if (child == null)
                    continue;

                Object.DestroyImmediate(child.gameObject);
                Debug.Log($"[FreeV74Final] '{name}' tirado do prefab de treino.");
            }

            if (!ConfigureForTraining(contents))
                return false;

            PrefabUtility.SaveAsPrefabAsset(contents, ArenaPrefabPath);
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    private static bool ConfigureForTraining(GameObject arenaRoot)
    {
        FreeArenaController arena = arenaRoot.GetComponentInChildren<FreeArenaController>(true);
        FreeExplorerManager agent = arenaRoot.GetComponentInChildren<FreeExplorerManager>(true);
        FreeHider hider = arenaRoot.GetComponentInChildren<FreeHider>(true);
        Transform player = FindPlayer(arenaRoot.transform);
        if (arena == null || agent == null || hider == null)
        {
            Debug.LogError("[FreeV74Final] a arena precisa de FreeArenaController, monstro (FreeExplorerManager) e presa " +
                           "(FreeHider). Sem presa: apague o prefab e rode de novo (ela vem do Map - V8 na criação).");
            return false;
        }

        var arenaSettings = new SerializedObject(arena);
        arenaSettings.FindProperty("_gameMode").boolValue = false;
        arenaSettings.FindProperty("_hider").objectReferenceValue = hider;
        arenaSettings.ApplyModifiedPropertiesWithoutUndo();

        agent.gameObject.SetActive(true);
        var agentSettings = new SerializedObject(agent);
        agentSettings.FindProperty("_keySensor").boolValue = true;
        // O treino usa sempre a planta atual; a cena de jogo pode estar na antiga por causa do .onnx dela.
        agentSettings.FindProperty("_planShowsEntered").boolValue = true;
        agentSettings.ApplyModifiedPropertiesWithoutUndo();
        var behavior = new SerializedObject(agent.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>());
        behavior.FindProperty("m_Model").objectReferenceValue = null;
        behavior.FindProperty("m_BehaviorType").intValue = 0;
        behavior.FindProperty("m_BrainParameters.VectorObservationSize").intValue = FreeObservations.Size;
        behavior.ApplyModifiedPropertiesWithoutUndo();

        // A presa usa o olho do monstro para escolher esconderijo.
        GraphHiderPerception perception = agent.GetComponentInChildren<GraphHiderPerception>(true);
        if (perception != null)
        {
            float eye = new SerializedObject(perception).FindProperty("_eyeHeight").floatValue;
            var hiderSettings = new SerializedObject(hider);
            hiderSettings.FindProperty("_seekerEyeHeight").floatValue = eye;
            hiderSettings.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[FreeV74Final] olho do monstro {eye:0.00} m (presa usa o mesmo para se esconder).");
        }

        hider.gameObject.SetActive(true);
        if (player != null)
            player.gameObject.SetActive(false);
        CopyPlayerTrail(player, hider);
        return true;
    }

    private static void CopyPlayerTrail(Transform player, FreeHider hider)
    {
        FreeFootprintTrail source = player != null ? player.GetComponentInChildren<FreeFootprintTrail>(true) : null;
        if (source == null)
        {
            // Sem componente no jogador, o jogo usa o padrão do código; só difere se a presa tiver um próprio.
            if (hider.GetComponent<FreeFootprintTrail>() != null)
                Debug.LogWarning("[FreeV74Final] o jogador do Mapa Final não tem FreeFootprintTrail e a presa tem: o rastro do treino difere do jogo.");
            return;
        }

        FreeFootprintTrail target = hider.GetComponent<FreeFootprintTrail>();
        if (target == null)
            target = hider.gameObject.AddComponent<FreeFootprintTrail>();

        var from = new SerializedObject(source);
        var to = new SerializedObject(target);
        SerializedProperty property = from.GetIterator();
        property.NextVisible(true);
        while (property.NextVisible(false))
        {
            if (property.name != "m_Script")
                to.CopyFromSerializedProperty(property);
        }

        to.ApplyModifiedPropertiesWithoutUndo();
    }

    private static Transform FindPlayer(Transform root)
    {
        foreach (PlayerMovement movement in root.GetComponentsInChildren<PlayerMovement>(true))
        {
            Transform candidate = movement.transform;
            while (candidate != null && !candidate.CompareTag(PlayerTag) && candidate != root)
                candidate = candidate.parent;
            if (candidate != null && candidate.CompareTag(PlayerTag))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<GameObject> Actors(Transform root)
    {
        Transform player = FindPlayer(root);
        return root.GetComponentsInChildren<FreeExplorerManager>(true).Select(x => x.gameObject)
            .Concat(root.GetComponentsInChildren<FreeHider>(true).Select(x => x.gameObject))
            .Concat(player != null ? new[] { player.gameObject } : new GameObject[0])
            .Distinct();
    }

    private static bool BakeSealAndReport(out float spacing)
    {
        spacing = 0f;
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        NavMeshSurface surface = instance.GetComponent<NavMeshSurface>();
        if (surface == null)
        {
            Debug.LogError($"[FreeV74Final] {ArenaPrefabPath} sem NavMeshSurface na raiz.");
            return false;
        }

        // Bloqueios velhos fora: a detecção precisa ver a borda como ela é.
        Transform oldGroup = instance.transform.Find(BlockerGroup);
        if (oldGroup != null)
            Object.DestroyImmediate(oldGroup.gameObject);

        List<GameObject> actors = Actors(instance.transform).Where(g => g.activeSelf).ToList();
        foreach (GameObject actor in actors)
            actor.SetActive(false);

        List<Blocker> blockers;
        try
        {
            surface.BuildNavMesh();
            if (surface.navMeshData == null)
            {
                Debug.LogError("[FreeV74Final] o bake não gerou NavMesh (nenhum colisor de piso na arena?).");
                return false;
            }

            Physics.SyncTransforms();
            float radius = NavMesh.GetSettingsByID(surface.agentTypeID).agentRadius;
            blockers = FindOpenEdges(radius);
        }
        finally
        {
            foreach (GameObject actor in actors)
                actor.SetActive(true);
        }

        // COPIA por cima do asset existente: apagar e recriar deixou o prefab sem NavMesh no build.
        var data = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshAssetPath);
        if (data != null)
        {
            EditorUtility.CopySerialized(surface.navMeshData, data);
            EditorUtility.SetDirty(data);
        }
        else
        {
            AssetDatabase.CreateAsset(surface.navMeshData, NavMeshAssetPath);
        }

        AssetDatabase.SaveAssets();
        data = AssetDatabase.LoadAssetAtPath<NavMeshData>(NavMeshAssetPath);
        if (data == null || data.sourceBounds.size.sqrMagnitude < 1f)
        {
            Debug.LogError($"[FreeV74Final] {NavMeshAssetPath} não carregou depois do bake (direito > Reimport e rode de novo).");
            return false;
        }

        surface.navMeshData = data;

        GameObject contents = PrefabUtility.LoadPrefabContents(ArenaPrefabPath);
        try
        {
            NavMeshSurface prefabSurface = contents.GetComponent<NavMeshSurface>();
            prefabSurface.navMeshData = data;
            prefabSurface.enabled = true;

            Transform group = contents.transform.Find(BlockerGroup);
            if (group != null)
                Object.DestroyImmediate(group.gameObject);
            if (blockers.Count > 0)
                CreateBlockers(contents.transform, instance.transform, blockers);

            PrefabUtility.SaveAsPrefabAsset(contents, ArenaPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }

        GameObject saved = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        NavMeshSurface savedSurface = saved != null ? saved.GetComponent<NavMeshSurface>() : null;
        if (savedSurface == null || savedSurface.navMeshData != data || !savedSurface.enabled)
        {
            Debug.LogError($"[FreeV74Final] o {ArenaPrefabPath} não ficou com o NavMesh {NavMeshAssetPath} ligado.");
            return false;
        }

        if (blockers.Count == 0)
            Debug.Log("[FreeV74Final] bordas: o chão do mapa termina em parede em toda a volta, nenhum bloqueio.");
        else
            Debug.LogWarning($"[FreeV74Final] {blockers.Count} borda(s) aberta(s) tampada(s) com bloqueio invisível (layer Wall) " +
                             $"no grupo '{BlockerGroup}':\n  " + string.Join("\n  ",
                                 blockers.Select(b => $"({b.Center.x:0.0}, {b.Center.y:0.0}, {b.Center.z:0.0}) largura {b.Width:0.0} m")));

        surface.enabled = true;
        FreeMap map = instance.GetComponentInChildren<FreeMap>(true);
        if (map != null)
            map.GenerateAndReport();
        ReportSeeThroughCover(instance);

        Bounds bounds = data.sourceBounds;
        spacing = Mathf.Ceil(Mathf.Max(bounds.size.x, bounds.size.z) + ArenaGap);
        Debug.Log($"[FreeV74Final] NavMesh ({bounds.size.x:0} x {bounds.size.z:0} m) em {NavMeshAssetPath}, ligado no {ArenaPrefabPath}.");
        return true;
    }

    private struct Blocker
    {
        public Vector3 Center;   // no chão, na linha da borda (mundo)
        public Vector3 Outward;  // horizontal, saindo do NavMesh
        public float Width;
    }

    // Borda aberta = aresta de um triângulo só, sem parede nem chão do outro lado. Amostra a cada 0.5 m.
    private static List<Blocker> FindOpenEdges(float agentRadius)
    {
        NavMeshTriangulation tri = NavMesh.CalculateTriangulation();
        Vector3[] vertices = tri.vertices;
        int[] indices = tri.indices;

        // Os tiles do NavMesh repetem vértices na emenda.
        var weld = new Dictionary<Vector3Int, int>();
        var ids = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var key = Vector3Int.RoundToInt(vertices[i] * 20f);
            if (!weld.TryGetValue(key, out int id))
                weld[key] = id = weld.Count;
            ids[i] = id;
        }

        int triangles = indices.Length / 3;
        var parent = Enumerable.Range(0, triangles).ToArray();
        int Root(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        var edges = new Dictionary<(int, int), (int count, int a, int b, Vector3 centroid, int triangle)>();
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector3 centroid = (vertices[indices[t]] + vertices[indices[t + 1]] + vertices[indices[t + 2]]) / 3f;
            for (int e = 0; e < 3; e++)
            {
                int a = indices[t + e], b = indices[t + (e + 1) % 3];
                var key = ids[a] < ids[b] ? (ids[a], ids[b]) : (ids[b], ids[a]);
                if (edges.TryGetValue(key, out var old))
                {
                    parent[Root(old.triangle)] = Root(t / 3);
                    edges[key] = (old.count + 1, old.a, old.b, old.centroid, old.triangle);
                }
                else
                {
                    edges[key] = (1, a, b, centroid, t / 3);
                }
            }
        }

        // Tampo de mesa e de armário sai andável no bake, mas ninguém chega lá: bloquear só taparia a visão.
        var area = new Dictionary<int, float>();
        for (int t = 0; t < triangles; t++)
        {
            Vector3 p0 = vertices[indices[t * 3]], p1 = vertices[indices[t * 3 + 1]], p2 = vertices[indices[t * 3 + 2]];
            int root = Root(t);
            area[root] = (area.TryGetValue(root, out float sum) ? sum : 0f) + Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
        }

        float largest = area.Values.DefaultIfEmpty(0f).Max();
        var floors = new HashSet<int>(area.Where(kv => kv.Value >= largest * MainFloorFraction).Select(kv => kv.Key));

        float reach = agentRadius + 1f;
        float beyond = agentRadius + 0.6f;
        var open = new List<(Vector3 point, Vector3 outward)>();
        foreach (var edge in edges.Values.Where(e => e.count == 1 && floors.Contains(Root(e.triangle))))
        {
            Vector3 a = vertices[edge.a], b = vertices[edge.b];
            Vector3 along = b - a;
            along.y = 0f;
            if (along.sqrMagnitude < 1e-4f)
                continue;

            Vector3 outward = Vector3.Cross(Vector3.up, along).normalized;
            Vector3 mid = (a + b) * 0.5f;
            if (Vector3.Dot(outward, edge.centroid - mid) > 0f)
                outward = -outward;

            int samples = Mathf.Max(1, Mathf.CeilToInt(along.magnitude / 0.5f));
            for (int s = 0; s < samples; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, (s + 0.5f) / samples);
                bool wall = Physics.Raycast(p + Vector3.up * 0.5f, outward, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                            || Physics.Raycast(p + Vector3.up * 1.5f, outward, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (wall)
                    continue;

                bool floor = false;
                foreach (float d in new[] { beyond, beyond + 0.6f, beyond + 1.4f })
                    floor |= Physics.Raycast(p + outward * d + Vector3.up * 2f, Vector3.down, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (!floor)
                    open.Add((p, outward));
            }
        }

        WriteEdgeDump(vertices, indices, t => Root(t), floors, open.Select(o => o.point));

        var blockers = new List<Blocker>();
        var used = new bool[open.Count];
        for (int i = 0; i < open.Count; i++)
        {
            if (used[i])
                continue;

            var cluster = new List<int> { i };
            used[i] = true;
            for (int c = 0; c < cluster.Count; c++)
            {
                for (int j = 0; j < open.Count; j++)
                {
                    if (used[j] || Vector3.Distance(open[cluster[c]].point, open[j].point) > 1.5f
                        || Vector3.Dot(open[cluster[c]].outward, open[j].outward) < 0.7f)
                        continue;

                    used[j] = true;
                    cluster.Add(j);
                }
            }

            Vector3 outward = cluster.Aggregate(Vector3.zero, (sum, k) => sum + open[k].outward).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, outward);
            float min = cluster.Min(k => Vector3.Dot(open[k].point, side));
            float max = cluster.Max(k => Vector3.Dot(open[k].point, side));
            Vector3 center = cluster.Aggregate(Vector3.zero, (sum, k) => sum + open[k].point) / cluster.Count;
            center += side * ((min + max) * 0.5f - Vector3.Dot(center, side));
            blockers.Add(new Blocker { Center = center, Outward = outward, Width = max - min + 1f });
        }

        return blockers;
    }

    // Logs/v7.4_navmesh.txt: triângulos (T) e amostras abertas (O), para conferir os bloqueios fora do Unity.
    private static void WriteEdgeDump(Vector3[] vertices, int[] indices, System.Func<int, int> component,
                                      HashSet<int> floors, IEnumerable<Vector3> open)
    {
        var text = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int t = 0; t < indices.Length / 3; t++)
        {
            Vector3 a = vertices[indices[t * 3]], b = vertices[indices[t * 3 + 1]], c = vertices[indices[t * 3 + 2]];
            text.AppendLine(string.Format(inv, "T {0:0.00} {1:0.00} {2:0.00} {3:0.00} {4:0.00} {5:0.00} {6:0.00} {7}",
                a.x, a.z, b.x, b.z, c.x, c.z, (a.y + b.y + c.y) / 3f, floors.Contains(component(t)) ? 1 : 0));
        }

        foreach (Vector3 p in open)
            text.AppendLine(string.Format(inv, "O {0:0.00} {1:0.00} {2:0.00}", p.x, p.y, p.z));

        Directory.CreateDirectory("Logs");
        File.WriteAllText(EdgeDump, text.ToString());
    }

    // Caixa na layer Wall logo depois da borda aberta: o monstro a vê e a presa não passa.
    private static void CreateBlockers(Transform prefabRoot, Transform instanceRoot, List<Blocker> blockers)
    {
        var group = new GameObject(BlockerGroup);
        group.transform.SetParent(prefabRoot, false);
        int wall = LayerMask.NameToLayer("Wall");
        for (int i = 0; i < blockers.Count; i++)
        {
            Blocker b = blockers[i];
            var box = new GameObject($"Bloqueio {i}") { layer = wall };
            box.transform.SetParent(group.transform, false);
            Vector3 world = b.Center + b.Outward * 0.6f + Vector3.up * 3f;
            box.transform.localPosition = instanceRoot.InverseTransformPoint(world);
            box.transform.localRotation = Quaternion.Inverse(instanceRoot.rotation) * Quaternion.LookRotation(b.Outward, Vector3.up);
            var collider = box.AddComponent<BoxCollider>();
            collider.size = new Vector3(b.Width, 6f, 0.5f);
        }
    }

    private static void ReportSeeThroughCover(GameObject arenaRoot)
    {
        FreeArenaController arena = arenaRoot.GetComponentInChildren<FreeArenaController>(true);
        if (arena == null)
            return;

        int mask = arena.WallLayer.value;
        var actors = new HashSet<Transform>(Actors(arenaRoot.transform).Select(g => g.transform));
        var leaks = new List<string>();
        Physics.SyncTransforms();
        foreach (Collider collider in arenaRoot.GetComponentsInChildren<Collider>(true))
        {
            if (collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy
                || (mask & (1 << collider.gameObject.layer)) != 0 || collider.bounds.size.y < 0.5f
                || actors.Any(a => collider.transform.IsChildOf(a)))
                continue;

            leaks.Add($"{collider.name} (layer {LayerMask.LayerToName(collider.gameObject.layer)}, {collider.bounds.size.y:0.0} m)");
        }

        if (leaks.Count == 0)
            Debug.Log("[FreeV74Final] esconderijos: todo colisor com altura de móvel está numa layer que tapa a visão.");
        else
            Debug.LogWarning($"[FreeV74Final] {leaks.Count} colisor(es) que a visão do monstro ATRAVESSA (ponha na layer " +
                             $"Obstacle ou Wall para virar esconderijo):\n  {string.Join("\n  ", leaks.Take(40))}" +
                             (leaks.Count > 40 ? $"\n  ... e mais {leaks.Count - 40}" : ""));
    }

    private static bool SetUpTrainingScene(float spacing)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        for (int i = 0; i < Arenas; i++)
        {
            var arena = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            arena.name = $"Arena - Cena Final ({i})";
            arena.transform.position = prefab.transform.position + new Vector3(i * spacing, 0f, 0f);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TrainingScene));
        if (!EditorSceneManager.SaveScene(scene, TrainingScene))
        {
            Debug.LogError($"[FreeV74Final] não consegui salvar {TrainingScene}.");
            return false;
        }

        if (NavMesh.CalculateTriangulation().vertices.Length == 0)
        {
            Debug.LogError($"[FreeV74Final] {TrainingScene} sem NavMesh carregado: o build não sai. Reimporte " +
                           $"{NavMeshAssetPath} (direito > Reimport) e rode o menu de novo.");
            return false;
        }

        Debug.Log($"[FreeV74Final] {TrainingScene}: {Arenas} arena(s), {spacing:0} m entre elas.");
        return true;
    }

    private static bool Build()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { TrainingScene },
            locationPathName = BuildPath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[FreeV74Final] build: {report.summary.result}, {report.summary.totalErrors} erro(s), {report.summary.outputPath}");
        return report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0;
    }
}

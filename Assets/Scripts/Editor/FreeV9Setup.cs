using System.Collections.Generic;
using System.Text;
using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using Unity.AI.Navigation;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// A arena da v9 com o mapa MOBILIADO da "TaskFurniture", pelo menu PROJECT-IA ou em batchmode:
///   "v9: montar Arena - V9 a partir da TaskFurniture"
///     0. Refaz a cópia corrigida "V9 - TaskFurniture" (FreeV9Furniture: colisor nos móveis grandes, layer Obstacle).
///        A TaskFurniture original nunca é tocada; tudo daqui para baixo lê a CÓPIA.
///     1. Abre a cópia (só lê; nunca é salva aqui) e uma instância da "Arena - V8" (o molde: monstro, presa,
///        PlayerDummy, NavMeshSurface, FreeMap, FreeArenaController e os 2 NavDoorMarker do anel S24/S26). A
///        TaskFurniture é o MESMO escritório da Arena - V8 (644 das 699 paredes no mesmo lugar), com ~55 paredes
///        mudadas e os móveis novos por sala em Constraints/Map_Objects. Por isso o mapa vem INTEIRO de lá:
///        Constraints (paredes + móveis), Floors e Corners são MOVIDOS para a arena; do molde sai o mapa antigo (e o
///        teto: sem gráfico no treino, e o NavMesh assava em cima dele).
///     2. LAYERS: móvel na Default vira Obstacle (a visão do monstro só bate em Wall/Obstacle/Door; sem isso ela
///        atravessava cadeira, mesa e gaveteiro).
///     3. Copia os pontos do KeySpawner para "KeySpots" (as chaves do treino saem dos mesmos pontos do jogo).
///     4. Assa o NavMesh (monstro, presa e jogador desligados no bake, senão viram buraco no chão) e grava o
///        NavMeshData num asset ao lado do prefab.
///     5. Salva Assets/Prefabs/Arena - V9.prefab, monta a "V9 - Training" com 5 cópias afastadas e roda o relatório
///        do FreeMap (pontos, salas, portas, VAZAMENTOS) na 1ª.
/// Mover (e não copiar) mantém as peças como instâncias de prefab: copiar desempacotaria milhares delas e o prefab
/// passaria dos 100 MB do GitHub. Por isso a cópia não é salva aqui: no fim a cena de treino abre por cima, e em
/// qualquer falha ela é reaberta do disco, sem as mudanças.
/// Os móveis usam modelos .blend: o Unity que rodar isto (e qualquer build) precisa do Blender instalado.
///     6. O monstro do prefab vira o da v9: sensor das chaves ligado, sem .onnx antigo (o da v8.1 não roda no
///        cérebro novo) e o formato do cérebro gravado (vetor 200, 4 contínuas + ramos 3/7/13).
///   "v9: montar tudo e build"  os passos acima e o build da V9 - Training em Builds/V9/PROJECT-IA.exe.
/// Linha de comando (Unity fechado):
///   Unity.exe -batchmode -quit -projectPath . -executeMethod FreeV9Setup.BuildArena -logFile v9setup.log
///   Unity.exe -batchmode -quit -projectPath . -executeMethod FreeV9Setup.BuildAll -logFile v9build.log
///
/// O que conferir depois (Console, ou o log):
///   - "[FreeV9Setup]" com o resumo: grupos movidos, layers trocadas, pontos de chave, NavMesh assado;
///   - a linha "mapa v8 gerado": nº de pontos, salas e portas, e "0 vazamento(s)"; cada VAZAMENTO é um vão sem
///     batente que junta duas salas e precisa de um NavDoorMarker (avise o Claude: marcador posto à mão no prefab
///     some quando o menu roda de novo);
///   - salas além de 32 ou portas além de 8 por sala aparecem marcadas no relatório (a v9 aumenta esses tetos).
/// </summary>
public static class FreeV9Setup
{
    // A cópia corrigida (FreeV9Furniture), não a original.
    private const string SourceScene = FreeV9Furniture.FixedScene;
    private const string KeySpawnerName = "KeySpawner";
    private const string TemplatePrefabPath = "Assets/Prefabs/Arena - V8.prefab";
    private const string ArenaPrefabPath = "Assets/Prefabs/Arena - V9.prefab";
    private const string NavMeshDataPath = "Assets/Prefabs/Arena - V9 NavMesh.asset";
    private const string ArenaName = "Arena - V9";
    private const string TrainingScene = "Assets/Scenes/Arthur/V9 - Training.unity";
    private const string KeySpotsName = "KeySpots";
    private const string BuildPath = "Builds/V9/PROJECT-IA.exe";

    // O objeto do mapa na TaskFurniture é o que tem este filho (paredes + móveis).
    private const string ConstraintsName = "Constraints";

    // O que vem da TaskFurniture: paredes e móveis, piso, quinas.
    private static readonly string[] MapParts = { ConstraintsName, "Floors", "Corners" };

    // Grupo dos móveis dentro do Constraints: o que estiver na Default dentro dele vai para Obstacle.
    private const string FurnitureGroup = "Map_Objects";

    // 5 arenas, como na v8. Afastadas bem mais que o mapa (~116 x 102 m): o corte do som em linha reta
    // (FreeTrackSense) e a visão não podem alcançar a arena vizinha.
    private const int TrainingArenas = 5;
    private const float ArenaSpacing = 250f;

    [MenuItem("PROJECT-IA/v9: montar Arena - V9 a partir da TaskFurniture")]
    public static void BuildArena()
    {
        bool ok = FreeV9Furniture.CreateFixedCopy() && Run();
        Debug.Log(ok ? "[FreeV9Setup] PRONTO." : "[FreeV9Setup] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    [MenuItem("PROJECT-IA/v9: montar tudo e build")]
    public static void BuildAll()
    {
        bool ok = FreeV9Furniture.CreateFixedCopy() && Run() && Build();
        Debug.Log(ok ? "[FreeV9Setup] PRONTO (arena + build)." : "[FreeV9Setup] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    // Build da cena de treino. "Succeeded" com erro logado no meio (o 11/10 da v8.1: NavMeshPath no inicializador de
    // campo) também é falha: o número de erros conta.
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
        Debug.Log($"[FreeV9Setup] build: {report.summary.result}, {report.summary.totalErrors} erro(s), {report.summary.outputPath}");
        return report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0;
    }

    [MenuItem("PROJECT-IA/v9: abrir a cena de treino")]
    public static void OpenTrainingScene() => EditorSceneManager.OpenScene(TrainingScene, OpenSceneMode.Single);

    private static bool Run()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV9Setup] saia do Play antes de montar a arena.");
            return false;
        }

        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return false;

        GameObject template = AssetDatabase.LoadAssetAtPath<GameObject>(TemplatePrefabPath);
        if (template == null)
        {
            Debug.LogError($"[FreeV9Setup] falta o molde {TemplatePrefabPath}.");
            return false;
        }

        Scene source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        GameObject map = FindMapRoot(source);
        if (map == null)
        {
            Debug.LogError($"[FreeV9Setup] nenhum objeto na raiz de {SourceScene} com o filho '{ConstraintsName}'.");
            return false;
        }

        bool ok = false;
        try
        {
            var report = new StringBuilder();
            report.AppendLine($"  mapa: '{map.name}' da {SourceScene}");
            GameObject arena = CreateArena(template, map, source, report);
            if (arena == null)
                return false;

            CopyKeySpots(FindRoot(source, KeySpawnerName), arena.transform, report);
            if (!SetUpBrain(arena, report))
                return false;

            if (!Bake(arena, report))
                return false;

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(arena, ArenaPrefabPath, out bool saved);
            if (!saved || prefab == null)
            {
                Debug.LogError($"[FreeV9Setup] não consegui salvar {ArenaPrefabPath}.");
                return false;
            }

            Debug.Log($"[FreeV9Setup] {ArenaPrefabPath} criado:\n{report}");
            ok = BuildTrainingScene(prefab);
            return ok;
        }
        finally
        {
            if (!ok)
                EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        }
    }

    // Instância do molde, sem o mapa antigo, com o mapa da TaskFurniture. Fica na cena aberta só até virar prefab.
    private static GameObject CreateArena(GameObject template, GameObject map, Scene scene, StringBuilder report)
    {
        // Só a raiz é desempacotada: presa e jogador continuam instâncias dos prefabs deles, como na Arena - V8.
        var arena = (GameObject)PrefabUtility.InstantiatePrefab(template, scene);
        PrefabUtility.UnpackPrefabInstance(arena, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
        arena.name = ArenaName;

        // Mesma pose da raiz do mapa: as peças movidas (com a posição do mundo) caem com a mesma posição LOCAL de
        // lá, e os NavDoorMarker do molde (em posição local) continuam no vão certo.
        arena.transform.SetPositionAndRotation(map.transform.position, map.transform.rotation);

        // Do molde ficam atores e os marcadores de porta; o resto é o mapa antigo (piso, teto, quinas, Constraints).
        int removed = 0;
        for (int i = arena.transform.childCount - 1; i >= 0; i--)
        {
            GameObject child = arena.transform.GetChild(i).gameObject;
            if (IsActor(child) || child.GetComponent<NavDoorMarker>() != null)
                continue;

            Object.DestroyImmediate(child);
            removed++;
        }

        report.AppendLine($"  molde: {removed} objeto(s) do mapa antigo removido(s), atores e NavDoorMarker mantidos");

        int moved = 0, toObstacle = 0;
        foreach (string part in MapParts)
        {
            Transform child = map.transform.Find(part);
            if (child == null)
            {
                report.AppendLine($"  ATENÇÃO: '{part}' não existe em '{map.name}'");
                continue;
            }

            if (PrefabUtility.IsPartOfPrefabInstance(child.gameObject) && !PrefabUtility.IsOutermostPrefabInstanceRoot(child.gameObject))
            {
                Debug.LogError($"[FreeV9Setup] '{part}' é peça de um prefab e não pode ser movido; desempacote '{map.name}' na TaskFurniture.");
                return null;
            }

            child.SetParent(arena.transform, true);
            moved++;

            Transform furniture = child.Find(FurnitureGroup);
            if (furniture != null)
                toObstacle += SetLayerWhere(furniture.gameObject, from: 0, to: LayerMask.NameToLayer("Obstacle"));
        }

        report.AppendLine($"  {moved} parte(s) do mapa movida(s) para a arena ({string.Join(", ", MapParts)})");
        report.AppendLine($"  layers: {toObstacle} móvel(is) com colisor Default -> Obstacle");
        return arena;
    }

    // O monstro da v9: sensor das chaves ligado, sem Model (o .onnx da v8.1 tem outro formato e quebraria o Play), e
    // o formato do cérebro gravado no prefab (o Manager também acerta no Awake, mas avisando).
    private static bool SetUpBrain(GameObject arena, StringBuilder report)
    {
        FreeExplorerManager agent = arena.GetComponentInChildren<FreeExplorerManager>(true);
        BehaviorParameters behavior = agent != null ? agent.GetComponent<BehaviorParameters>() : null;
        if (agent == null || behavior == null)
        {
            Debug.LogError("[FreeV9Setup] o molde não tem o monstro (FreeExplorerManager + BehaviorParameters).");
            return false;
        }

        var settings = new SerializedObject(agent);
        settings.FindProperty("_keySensor").boolValue = true;
        settings.ApplyModifiedPropertiesWithoutUndo();

        behavior.Model = null;
        FreeExplorerManager.EnforceBrainShape(behavior, warn: false, agent);
        EditorUtility.SetDirty(behavior);
        report.AppendLine($"  monstro: sensor das chaves ligado, sem Model, vetor {behavior.BrainParameters.VectorObservationSize}, " +
                          $"ações {behavior.BrainParameters.ActionSpec.NumContinuousActions} contínuas + ramos " +
                          $"{string.Join("/", behavior.BrainParameters.ActionSpec.BranchSizes)}");
        return true;
    }

    // Monstro, presa e jogador: o que tem agente, presa ou movimento do jogador.
    private static bool IsActor(GameObject go) =>
        go.GetComponentInChildren<Agent>(true) != null
        || go.GetComponentInChildren<FreeHider>(true) != null
        || go.GetComponentInChildren<PlayerMovement>(true) != null;

    // Só objetos com colisor sólido mudam: é ele que a visão e o NavMesh enxergam. Devolve quantos mudaram.
    private static int SetLayerWhere(GameObject root, int from, int to)
    {
        int changed = 0;
        foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
        {
            if (collider.isTrigger || collider.gameObject.layer != from)
                continue;

            collider.gameObject.layer = to;
            changed++;
        }

        return changed;
    }

    // Os pontos do KeySpawner (os filhos do filho "SpawnPoints", ou os filhos diretos) viram vazios em "KeySpots".
    private static void CopyKeySpots(GameObject keySpawner, Transform arena, StringBuilder report)
    {
        if (keySpawner == null)
        {
            report.AppendLine($"  ATENÇÃO: sem '{KeySpawnerName}' na cena: a arena fica sem pontos de chave");
            return;
        }

        Transform container = keySpawner.transform.Find("SpawnPoints") ?? keySpawner.transform;
        var spots = new GameObject(KeySpotsName).transform;
        spots.SetParent(arena, false);
        foreach (Transform point in container)
        {
            var spot = new GameObject(point.name).transform;
            spot.SetParent(spots, false);
            spot.position = point.position;
        }

        report.AppendLine($"  {spots.childCount} ponto(s) de chave copiado(s) do {KeySpawnerName} para '{KeySpotsName}'");
    }

    private static bool Bake(GameObject arena, StringBuilder report)
    {
        NavMeshSurface surface = arena.GetComponent<NavMeshSurface>();
        if (surface == null)
        {
            Debug.LogError("[FreeV9Setup] o molde não tem NavMeshSurface na raiz.");
            return false;
        }

        // Atores desligados no bake: o colisor deles viraria um buraco no NavMesh onde estavam.
        var actors = new List<GameObject>();
        foreach (Transform child in arena.transform)
        {
            if (IsActor(child.gameObject) && child.gameObject.activeSelf)
            {
                actors.Add(child.gameObject);
                child.gameObject.SetActive(false);
            }
        }

        surface.BuildNavMesh();
        foreach (GameObject actor in actors)
            actor.SetActive(true);

        if (surface.navMeshData == null)
        {
            Debug.LogError("[FreeV9Setup] o bake não gerou NavMesh (há piso com colisor na arena?).");
            return false;
        }

        if (AssetDatabase.LoadAssetAtPath<Object>(NavMeshDataPath) != null)
            AssetDatabase.DeleteAsset(NavMeshDataPath);
        AssetDatabase.CreateAsset(surface.navMeshData, NavMeshDataPath);
        AssetDatabase.SaveAssets();

        Bounds bounds = surface.navMeshData.sourceBounds;
        report.AppendLine($"  NavMesh assado ({bounds.size.x:0} x {bounds.size.z:0} m) e salvo em {NavMeshDataPath}");
        return true;
    }

    // Cena nova com as 5 arenas lado a lado e o relatório do mapa da 1ª.
    private static bool BuildTrainingScene(GameObject prefab)
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        FreeMap first = null;
        for (int i = 0; i < TrainingArenas; i++)
        {
            var arena = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            arena.name = $"{ArenaName} ({i})";
            arena.transform.position = new Vector3(i * ArenaSpacing, 0f, 0f);
            if (first == null)
                first = arena.GetComponentInChildren<FreeMap>(true);
        }

        if (!EditorSceneManager.SaveScene(scene, TrainingScene))
        {
            Debug.LogError($"[FreeV9Setup] não consegui salvar {TrainingScene}.");
            return false;
        }

        Debug.Log($"[FreeV9Setup] {TrainingScene}: {TrainingArenas} arena(s), {ArenaSpacing:0} m entre elas.");

        // O NavMeshSurface das instâncias já carregou o NavMesh (roda no editor): o FreeMap consegue gerar o mapa.
        if (first != null)
            first.GenerateAndReport();
        return true;
    }

    private static GameObject FindMapRoot(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.transform.Find(ConstraintsName) != null)
                return root;
        }

        return null;
    }

    private static GameObject FindRoot(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name)
                return root;
        }

        return null;
    }
}

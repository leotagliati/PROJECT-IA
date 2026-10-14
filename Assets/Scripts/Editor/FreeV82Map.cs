using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.Scripts.Free;
using Unity.AI.Navigation;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// v8.2 (13/10): o mapa editado à mão (prefab "Map - V8", da cena "Map - V8 seeker": a arena da v8.1 com os móveis
/// acertados, cubículos abertos e esconderijos) vira arena de TREINO e de TESTE, com o cérebro da v8.1.
/// O "Arena - V8.2" é uma VARIANTE do Map - V8 em estado de treino: mexer no Map - V8 vale para os dois; o que se mexe
/// no Arena - V8.2 (um móvel a mais, uma cadeira no lugar) fica só nele e NÃO é apagado por este menu.
///   "v8.2: preparar mapa, cenas e build"   faz tudo, em ordem:
///     1. cria o Arena - V8.2 a partir do Map - V8 da cena, SÓ se ele ainda não existir;
///     2. põe o prefab em estado de TREINO (monstro ligado sem .onnx, presa ligada, jogador desligado, sem modo de jogo).
///        O rastro do JOGADOR é copiado para a presa: o cérebro treina com o mesmo rastro que vai encontrar no jogo (o
///        FreeExplorerManager só cria o rastro com os padrões do código quando o alvo não tem um);
///     3. assa o NavMesh numa instância do prefab, com monstro, presa e jogador desligados (o colisor deles viraria
///        buraco), grava em Arena - V8.2 NavMesh.asset e liga no prefab. Asset PRÓPRIO: o Map - V8 veio apontando para o
///        da Arena - V9, e assar por cima dele mudaria o mapa do treino da v8.1/v9. O Bake do Inspector numa cena grava
///        o asset na pasta da cena e não desliga os atores: use este menu;
///     4. relatório do FreeMap no Console (salas, portas, vazamentos) e os ESCONDERIJOS que a visão atravessa: colisor
///        de móvel fora das layers de parede da arena (Wall/Obstacle/Door) não tapa a visão do monstro nem conta para a
///        presa escolher esconderijo;
///     5. "V8.2 - Training" (5 arenas) e o build em Builds/V8.2 (a versão "sem build" pula o build);
///     6. "V8.2 - Teste" (1 arena em modo de jogo, o .onnx padrão) e o relatório por sala em Logs/v8.2_rooms.txt.
///   "v8.2: cena de teste (.onnx selecionado)"  refaz só a cena de teste com o modelo selecionado no Project.
/// Mexeu no Map - V8 ou no Arena - V8.2: rode o primeiro menu de novo (o NavMesh e o build saem velhos sem ele).
/// </summary>
public static class FreeV82Map
{
    private const string SourceScene = "Assets/Scenes/Map - V8 seeker.unity";
    private const string SourceRoot = "Map - V8";
    private const string NavMeshAssetPath = "Assets/Prefabs/Arena - V8.2 NavMesh.asset";
    private const string ArenaPrefabPath = "Assets/Prefabs/Arena - V8.2.prefab";
    private const string TrainingScene = "Assets/Scenes/Arthur/V8.2 - Training.unity";
    private const string TestScene = "Assets/Scenes/Arthur/V8.2 - Teste.unity";
    private const string BuildPath = "Builds/V8.2/PROJECT-IA.exe";
    private const string DefaultModel = "Assets/FreeExplorer_v8.1M_35M.onnx";
    private const string RoomReport = "Logs/v8.2_rooms.txt";
    private const string PlayerTag = "Goal";
    private const int Arenas = 5;
    private const float Spacing = 250f;

    // Treino com o rastro do jogador (true) ou com o padrão do código na presa (false: 12 s correndo, 6 s andando).
    private static readonly bool HiderUsesPlayerTrail = true;

    [MenuItem("PROJECT-IA/v8.2: preparar mapa, cenas e build")]
    public static void PrepareAll() => Prepare(build: true);

    [MenuItem("PROJECT-IA/v8.2: preparar mapa e cenas (sem build)")]
    public static void PrepareWithoutBuild() => Prepare(build: false);

    [MenuItem("PROJECT-IA/v8.2: cena de teste (.onnx selecionado)")]
    public static void TestFromSelection()
    {
        if (!CanRun())
            return;

        string path = Selection.assetGUIDs.Select(AssetDatabase.GUIDToAssetPath)
            .FirstOrDefault(p => p.EndsWith(".onnx", System.StringComparison.OrdinalIgnoreCase));
        ModelAsset model = path != null ? AssetDatabase.LoadAssetAtPath<ModelAsset>(path) : null;
        if (model == null)
        {
            Debug.LogError("[FreeV82Map] selecione um .onnx na janela Project antes.");
            return;
        }

        if (AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath) == null)
        {
            Debug.LogError($"[FreeV82Map] falta {ArenaPrefabPath}: rode antes \"v8.2: preparar mapa...\".");
            return;
        }

        FreeV81FurnishedTest.BuildTestScene(model, path, ArenaPrefabPath, TestScene, "Arena - V8.2 (teste)");
    }

    private static void Prepare(bool build)
    {
        if (!CanRun())
            return;

        bool ok = EnsurePrefab() && ConfigurePrefab() && BakeAndReport() && SetUpTrainingScene() && (!build || Build());
        if (ok)
        {
            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(DefaultModel);
            ok = FreeV81FurnishedTest.BuildTestScene(model, DefaultModel, ArenaPrefabPath, TestScene, "Arena - V8.2 (teste)");
        }

        if (ok)
        {
            int hopeless = FreeV81FurnitureAudit.WriteRoomReport(TrainingScene, RoomReport);
            Debug.Log($"[FreeV82Map] salas: {RoomReport}" + (hopeless > 0
                ? $" — {hopeless} sala(s) com mais de 10% dos pontos impossíveis (nunca chegam a 90%)."
                : " — nenhuma sala impossível."));
            EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single);
        }

        Debug.Log(ok ? "[FreeV82Map] PRONTO." : "[FreeV82Map] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    private static bool CanRun()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Map] saia do Play antes.");
            return false;
        }

        return Application.isBatchMode || EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
    }

    // ================================================================================
    // 1-4. Prefab, estado de treino, NavMesh e relatórios
    // ================================================================================

    // Só cria se não existir: o Arena - V8.2 pode ter edições à mão que este menu não pode apagar. Com o Map - V8 da
    // cena sendo instância do prefab Map - V8, o salvo é uma variante dele.
    private static bool EnsurePrefab()
    {
        if (AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath) != null)
            return true;

        Scene scene = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        GameObject root = scene.GetRootGameObjects().FirstOrDefault(g => g.name == SourceRoot);
        if (root == null)
        {
            Debug.LogError($"[FreeV82Map] {SourceScene} sem o objeto raiz '{SourceRoot}'.");
            return false;
        }

        PrefabUtility.SaveAsPrefabAsset(root, ArenaPrefabPath, out bool saved);
        if (!saved)
            Debug.LogError($"[FreeV82Map] não consegui salvar {ArenaPrefabPath}.");
        else
            Debug.Log($"[FreeV82Map] {ArenaPrefabPath} criado a partir do '{SourceRoot}' da {SourceScene}.");
        return saved;
    }

    private static bool ConfigurePrefab()
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(ArenaPrefabPath);
        try
        {
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

    // O bake roda numa instância em cena (como o FreeV9Setup) e o resultado vai para o prefab pelo LoadPrefabContents.
    private static bool BakeAndReport()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        NavMeshSurface surface = instance.GetComponent<NavMeshSurface>();
        if (surface == null)
        {
            Debug.LogError($"[FreeV82Map] {ArenaPrefabPath} sem NavMeshSurface na raiz.");
            return false;
        }

        // Atores desligados no bake (senão a cápsula deles vira buraco no NavMesh).
        List<GameObject> actors = Actors(instance.transform).Where(g => g.activeSelf).ToList();
        foreach (GameObject actor in actors)
            actor.SetActive(false);
        try
        {
            surface.BuildNavMesh();
        }
        finally
        {
            foreach (GameObject actor in actors)
                actor.SetActive(true);
        }

        if (surface.navMeshData == null)
        {
            Debug.LogError("[FreeV82Map] o bake não gerou NavMesh (nenhum colisor de piso na arena?).");
            return false;
        }

        // O BuildNavMesh trocou a referência por um NavMesh novo, só na memória: o asset que estava ligado (o da
        // Arena - V9, ou um da pasta de uma cena) fica intacto.
        AssetDatabase.DeleteAsset(NavMeshAssetPath);
        AssetDatabase.CreateAsset(surface.navMeshData, NavMeshAssetPath);
        AssetDatabase.SaveAssets();
        var data = AssetDatabase.LoadAssetAtPath<UnityEngine.AI.NavMeshData>(NavMeshAssetPath);

        GameObject contents = PrefabUtility.LoadPrefabContents(ArenaPrefabPath);
        try
        {
            NavMeshSurface prefabSurface = contents.GetComponent<NavMeshSurface>();
            prefabSurface.navMeshData = data;
            prefabSurface.enabled = true;
            PrefabUtility.SaveAsPrefabAsset(contents, ArenaPrefabPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }

        // Relatórios na instância (o NavMesh dela está carregado).
        surface.enabled = true;
        FreeMap map = instance.GetComponentInChildren<FreeMap>(true);
        if (map != null)
            map.GenerateAndReport();
        ReportSeeThroughCover(instance);

        Bounds bounds = data.sourceBounds;
        Debug.Log($"[FreeV82Map] NavMesh ({bounds.size.x:0} x {bounds.size.z:0} m) em {NavMeshAssetPath}, ligado no {ArenaPrefabPath}.");
        return true;
    }

    // Móvel com colisor que a visão do monstro atravessa: fora das layers de parede da arena (as da visão, dos raios e
    // da escolha de esconderijo da presa). Só os que têm altura para esconder alguém (>= 0.5 m); piso e teto são finos.
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
            Debug.Log("[FreeV82Map] esconderijos: todo colisor com altura de móvel está numa layer que tapa a visão.");
        else
            Debug.LogWarning($"[FreeV82Map] {leaks.Count} colisor(es) que a visão do monstro ATRAVESSA (ponha na layer " +
                             $"Obstacle ou Wall para virar esconderijo):\n  {string.Join("\n  ", leaks.Take(40))}" +
                             (leaks.Count > 40 ? $"\n  ... e mais {leaks.Count - 40}" : ""));
    }

    private static IEnumerable<GameObject> Actors(Transform root)
    {
        Transform player = FindPlayer(root);
        return root.GetComponentsInChildren<FreeExplorerManager>(true).Select(x => x.gameObject)
            .Concat(root.GetComponentsInChildren<FreeHider>(true).Select(x => x.gameObject))
            .Concat(player != null ? new[] { player.gameObject } : new GameObject[0])
            .Distinct();
    }

    private static bool ConfigureForTraining(GameObject arenaRoot)
    {
        FreeArenaController arena = arenaRoot.GetComponentInChildren<FreeArenaController>(true);
        FreeExplorerManager agent = arenaRoot.GetComponentInChildren<FreeExplorerManager>(true);
        FreeHider hider = arenaRoot.GetComponentInChildren<FreeHider>(true);
        Transform player = FindPlayer(arenaRoot.transform);
        if (arena == null || agent == null || hider == null)
        {
            Debug.LogError("[FreeV82Map] o Map - V8 precisa de FreeArenaController, monstro (FreeExplorerManager) e presa (FreeHider).");
            return false;
        }

        var arenaSettings = new SerializedObject(arena);
        arenaSettings.FindProperty("_gameMode").boolValue = false;
        arenaSettings.ApplyModifiedPropertiesWithoutUndo();

        // Sem .onnx (com o trainer ele não é usado, e um .onnx de outro formato quebraria o Play solto) e com o vetor
        // certo já salvo (senão cada agente avisa "VectorObservationSize estava 200" no Awake).
        agent.gameObject.SetActive(true);
        var agentSettings = new SerializedObject(agent);
        agentSettings.FindProperty("_keySensor").boolValue = true;
        agentSettings.ApplyModifiedPropertiesWithoutUndo();
        var behavior = new SerializedObject(agent.GetComponent<Unity.MLAgents.Policies.BehaviorParameters>());
        behavior.FindProperty("m_Model").objectReferenceValue = null;
        behavior.FindProperty("m_BehaviorType").intValue = 0;
        behavior.FindProperty("m_BrainParameters.VectorObservationSize").intValue = FreeObservations.Size;
        behavior.ApplyModifiedPropertiesWithoutUndo();

        hider.gameObject.SetActive(true);
        if (player != null)
            player.gameObject.SetActive(false);

        if (HiderUsesPlayerTrail)
            CopyPlayerTrail(player, hider);

        return true;
    }

    // Os campos do FreeFootprintTrail do jogador vão para o da presa (criado se faltar).
    private static void CopyPlayerTrail(Transform player, FreeHider hider)
    {
        FreeFootprintTrail source = player != null ? player.GetComponentInChildren<FreeFootprintTrail>(true) : null;
        if (source == null)
        {
            Debug.LogWarning("[FreeV82Map] o jogador do Map - V8 não tem FreeFootprintTrail: a presa treina com o rastro padrão do código.");
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
        Debug.Log("[FreeV82Map] presa de treino com o rastro do jogador do Map - V8.");
    }

    // O jogador: o objeto com a tag Goal que tem (ou contém) o PlayerMovement.
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

    // ================================================================================
    // 4. Cena de treino e build
    // ================================================================================

    private static bool SetUpTrainingScene()
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        for (int i = 0; i < Arenas; i++)
        {
            var arena = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            arena.name = $"Arena - V8.2 ({i})";
            arena.transform.position = new Vector3(i * Spacing, 0f, 0f);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(TrainingScene));
        if (!EditorSceneManager.SaveScene(scene, TrainingScene))
        {
            Debug.LogError($"[FreeV82Map] não consegui salvar {TrainingScene}.");
            return false;
        }

        Debug.Log($"[FreeV82Map] {TrainingScene}: {Arenas} arena(s), {Spacing:0} m entre elas.");
        return true;
    }

    // "Succeeded" com erro logado no meio também é falha (como no FreeV81Furnished).
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
        Debug.Log($"[FreeV82Map] build: {report.summary.result}, {report.summary.totalErrors} erro(s), {report.summary.outputPath}");
        return report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0;
    }
}

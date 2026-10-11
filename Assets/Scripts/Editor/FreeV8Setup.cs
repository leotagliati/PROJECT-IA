using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

/// <summary>
/// A arena da v8 SÓ COM NAVMESH (prefab "Arena - V8") e as cenas dela, pelo menu PROJECT-IA ou em batchmode:
///   1. "v8: criar prefab sem nós e montar a cena de treino": pega a 1ª arena antiga da "V8 - Training" (instância
///      do NodeTraining - V6 Training com o monstro da v8), desempacota, converte os nós Porta sem batente em
///      NavDoorMarker, troca o GraphHider pelo FreeHider, passa chão/parede/tag para o FreeArenaController e APAGA o
///      grafo: NavNode, NavGraph, NavBlockedArea, GraphArenaController, o monstro da v5 desligado. Salva como
///      Assets/Prefabs/Arena - V8.prefab e põe uma instância dele no lugar de cada arena da cena.
///   2. "v8: montar cena de treino e build": o passo 1 (se ainda não feito) e o build em Builds/V8/PROJECT-IA.exe.
///   2b. "v8.1: montar cena com chaves e build": copia a V8 - Training para "V8.1 - Training" com o sensor das chaves
///      ligado nos monstros (o cérebro muda: os .onnx do v8.0 não rodam nela) e builda em Builds/V8.1/PROJECT-IA.exe,
///      sem tocar no build da v8.0. Batchmode: -executeMethod FreeV8Setup.RunKeys.
///   3. "v8: montar cena de teste (jogador + modelo)": a "V8 - Test" com uma Arena - V8 em modo de jogo, o .onnx de
///      TestModelPath e o PlayerDummy (a versão antiga, com nós, fica em "V8 - Test (com nós)").
/// Linha de comando (Unity fechado):
///   Unity.exe -batchmode -quit -projectPath . -executeMethod FreeV8Setup.Run -logFile setup.log
///
/// VALIDAR NO UNITY (cena V8 - Training aberta):
///   "v8: preparar cérebro do treino"   grava o checkpoint mais novo do run em Assets/FreeExplorer_v8_watch.onnx
///                                      (tools/v8_watch_onnx.py, pelo Python do conda mlagents)
///   Play, e então:
///   "v8 (em Play): dirigir com WASD"   só a 1ª arena fica ligada, o monstro obedece o WASD, painel na tela
///   "v8 (em Play): assistir o cérebro" o mesmo, com o .onnx acima (determinístico)
///   Nada disso fica salvo: o Stop desfaz.
/// </summary>
public static class FreeV8Setup
{
    private const string TrainingScene = "Assets/Scenes/Arthur/V8 - Training.unity";
    private const string ArenaPrefabPath = "Assets/Prefabs/Arena - V8.prefab";
    private const string ArenaName = "Arena - V8";
    private const string BuildPath = "Builds/V8/PROJECT-IA.exe";
    private const string KeysScene = "Assets/Scenes/Arthur/V8.1 - Training.unity";
    private const string KeysBuildPath = "Builds/V8.1/PROJECT-IA.exe";
    private const string WatchModelPath = "Assets/FreeExplorer_v8_watch.onnx";

    // O Python do ambiente conda mlagents nesta máquina; sem ele, o "python" do PATH.
    private const string CondaPython = "C:/Users/55119/miniconda3/envs/mlagents/python.exe";

    // Componentes do mundo de NÓS (v5/v6) que saem da arena. O corpo do monstro (GraphLocomotion, visão do alvo,
    // animação, raios) e o NavDoorMarker ficam: não dependem do grafo.
    private static readonly System.Type[] GraphComponents =
    {
        typeof(NavNode), typeof(NavBlockedArea), typeof(NavGraphPlacer), typeof(NavGraph), typeof(GraphArenaController),
        typeof(GraphHider), typeof(GraphPlayerTarget), typeof(GraphPingSystem), typeof(GraphSuspicionMap),
        typeof(GraphExplorationMemory), typeof(GraphRewardSystem),
    };

    [MenuItem("PROJECT-IA/v8: montar cena de treino e build")]
    public static void Run()
    {
        bool ok = SetUpTrainingScene() && Build();
        Debug.Log(ok ? "[FreeV8Setup] PRONTO." : "[FreeV8Setup] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    [MenuItem("PROJECT-IA/v8.1: montar cena com chaves e build")]
    public static void RunKeys()
    {
        bool ok = SetUpTrainingScene() && SetUpKeysScene() && Build(KeysScene, KeysBuildPath);
        Debug.Log(ok ? "[FreeV8Setup] PRONTO (v8.1)." : "[FreeV8Setup] FALHOU (v8.1, veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    // A cena da v8.1 é a de treino da v8 com o sensor das chaves ligado em todo monstro (refeita a cada vez, para
    // seguir a V8 - Training).
    private static bool SetUpKeysScene()
    {
        if (File.Exists(KeysScene))
            AssetDatabase.DeleteAsset(KeysScene);
        if (!AssetDatabase.CopyAsset(TrainingScene, KeysScene))
        {
            Debug.LogError($"[FreeV8Setup] não consegui copiar {TrainingScene} para {KeysScene}.");
            return false;
        }

        Scene scene = EditorSceneManager.OpenScene(KeysScene, OpenSceneMode.Single);
        int agents = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (FreeExplorerManager agent in root.GetComponentsInChildren<FreeExplorerManager>(true))
            {
                var settings = new SerializedObject(agent);
                settings.FindProperty("_keySensor").boolValue = true;
                settings.ApplyModifiedPropertiesWithoutUndo();
                agents++;
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Debug.Log($"[FreeV8Setup] {KeysScene}: sensor das chaves ligado em {agents} monstro(s).");
        return saved && agents > 0;
    }

    [MenuItem("PROJECT-IA/v8: criar prefab sem nós e montar a cena de treino")]
    public static void RunSceneOnly() => SetUpTrainingScene();

    // Abre a cena de treino da v8 (Unity.exe -projectPath . -executeMethod FreeV8Setup.OpenScene).
    [MenuItem("PROJECT-IA/v8: abrir a cena de treino")]
    public static void OpenScene() => EditorSceneManager.OpenScene(TrainingScene, OpenSceneMode.Single);

    // ================================================================================
    // Prefab sem nós e cena de treino
    // ================================================================================

    // Abre a V8 - Training, cria o prefab a partir da 1ª arena antiga (se ainda não existe) e troca toda arena que
    // não é instância dele por uma instância, no mesmo lugar. Salva a cena.
    private static bool SetUpTrainingScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV8Setup] saia do Play antes de montar a cena.");
            return false;
        }

        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return false;

        Scene scene = EditorSceneManager.OpenScene(TrainingScene, OpenSceneMode.Single);
        List<GameObject> arenas = ArenaRoots(scene);
        if (arenas.Count == 0)
        {
            Debug.LogError($"[FreeV8Setup] nenhuma arena (FreeArenaController) em {TrainingScene}.");
            return false;
        }

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        if (prefab == null)
        {
            GameObject template = arenas.Find(a => a.GetComponentInChildren<NavGraph>(true) != null);
            if (template == null)
            {
                Debug.LogError($"[FreeV8Setup] sem {ArenaPrefabPath} e sem arena antiga (com NavGraph) na cena para converter.");
                return false;
            }

            prefab = CreateArenaPrefab(template);
            if (prefab == null)
                return false;
        }

        int replaced = 0;
        foreach (GameObject arena in arenas)
        {
            if (arena == null || IsInstanceOf(arena, ArenaPrefabPath))
                continue;

            ReplaceWithPrefab(arena, prefab, scene);
            replaced++;
        }

        int total = ArenaRoots(scene).Count;
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
        {
            Debug.LogError("[FreeV8Setup] não consegui salvar a cena de treino.");
            return false;
        }

        Debug.Log($"[FreeV8Setup] {TrainingScene}: {total} arena(s) do {ArenaName} ({replaced} trocada(s) agora).");
        return true;
    }

    // Converte a arena antiga NA CENA (desempacota, tira o grafo, põe a presa do NavMesh), salva como prefab e deixa a
    // arena da cena ligada a ele. Null se algo falhou (a cena não é salva: reabra-a para desfazer).
    private static GameObject CreateArenaPrefab(GameObject arena)
    {
        if (PrefabUtility.IsPartOfPrefabInstance(arena))
            PrefabUtility.UnpackPrefabInstance(arena, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);

        var report = new System.Text.StringBuilder();
        FreeArenaController free = arena.GetComponent<FreeArenaController>();
        FreeMap map = arena.GetComponentInChildren<FreeMap>(true);
        GraphArenaController graphArena = arena.GetComponentInChildren<GraphArenaController>(true);
        NavGraph graph = arena.GetComponentInChildren<NavGraph>(true);
        GraphHider oldHider = arena.GetComponentInChildren<GraphHider>(true);
        if (free == null || map == null || oldHider == null)
        {
            Debug.LogError($"[FreeV8Setup] {arena.name}: falta FreeArenaController, FreeMap ou GraphHider para converter.", arena);
            return null;
        }

        // 1. Os cortes de sala que só existiam como nó Porta viram NavDoorMarker (antes de apagar os nós).
        int markers = map.ConvertDoorNodesToMarkers();
        report.AppendLine($"  {markers} nó(s) Porta sem batente -> NavDoorMarker");

        // 2. O que o GraphArenaController e o NavGraph sabiam e a arena da v8 precisa: chão, parede, tag do jogador.
        var arenaSettings = new SerializedObject(free);
        if (graph != null)
            arenaSettings.FindProperty("_wallLayer").intValue = graph.WallLayer.value;
        if (graphArena != null)
        {
            var old = new SerializedObject(graphArena);
            arenaSettings.FindProperty("_floorRenderer").objectReferenceValue = old.FindProperty("_floorRenderer").objectReferenceValue;
            arenaSettings.FindProperty("_playerTag").stringValue = old.FindProperty("_playerTag").stringValue;
        }

        // 3. A presa: FreeHider no lugar do GraphHider, no mesmo objeto (modelo, colisor, animação ficam).
        GameObject hiderObject = oldHider.gameObject;
        FreeHider hider = hiderObject.GetComponent<FreeHider>();
        if (hider == null)
            hider = hiderObject.AddComponent<FreeHider>();
        arenaSettings.FindProperty("_hider").objectReferenceValue = hider;
        arenaSettings.FindProperty("_gameMode").boolValue = false;
        arenaSettings.ApplyModifiedPropertiesWithoutUndo();

        // 4. O monstro da v5 (desligado ao lado do da v8 desde a montagem antiga).
        int monsters = 0;
        foreach (GraphExplorerManager oldAgent in arena.GetComponentsInChildren<GraphExplorerManager>(true))
        {
            if (TryDestroyObject(oldAgent.gameObject, report))
                monsters++;
        }

        report.AppendLine($"  {monsters} monstro(s) da v5 apagado(s)");

        // 5. O grafo: componentes do mundo de nós, e os objetos que ficaram vazios por causa disso.
        foreach (System.Type type in GraphComponents)
        {
            int removed = 0, emptied = 0;
            foreach (Component component in arena.GetComponentsInChildren(type, true))
            {
                if (component == null)
                    continue;

                GameObject owner = component.gameObject;
                if (!TryDestroyComponent(component, report))
                    continue;

                removed++;
                emptied += PruneEmpty(owner.transform, arena.transform);
            }

            if (removed > 0)
                report.AppendLine($"  {removed} {type.Name} removido(s), {emptied} objeto(s) vazio(s) apagado(s)");
        }

        // Nada do mundo de nós pode sobrar: a arena da v8 não tem NavGraph nem nó.
        foreach (System.Type type in GraphComponents)
        {
            Component[] left = arena.GetComponentsInChildren(type, true);
            if (left.Length > 0)
                report.AppendLine($"  ATENÇÃO: sobrou {left.Length} {type.Name} (peça de prefab aninhado; desligue à mão)");
        }

        arena.name = ArenaName;
        GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(arena, ArenaPrefabPath, InteractionMode.AutomatedAction, out bool saved);
        if (!saved || prefab == null)
        {
            Debug.LogError($"[FreeV8Setup] não consegui salvar {ArenaPrefabPath}.", arena);
            return null;
        }

        Debug.Log($"[FreeV8Setup] {ArenaPrefabPath} criado (sem nós, só NavMesh):\n{report}" +
                  "Confira o mapa no FreeMap ⋮ \"Gerar e relatar (v8)\": as mesmas salas e portas de antes.", prefab);
        return prefab;
    }

    // Arena = objeto com FreeArenaController (fica na raiz da arena), na ordem do nome.
    private static List<GameObject> ArenaRoots(Scene scene)
    {
        var arenas = new List<GameObject>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (FreeArenaController arena in root.GetComponentsInChildren<FreeArenaController>(true))
                arenas.Add(arena.gameObject);
        }

        arenas.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
        return arenas;
    }

    private static bool IsInstanceOf(GameObject go, string prefabPath) =>
        PrefabUtility.IsAnyPrefabInstanceRoot(go) && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath;

    // Instância do prefab no lugar (pai, posição, rotação, ordem) da arena antiga, que é apagada.
    private static GameObject ReplaceWithPrefab(GameObject old, GameObject prefab, Scene scene)
    {
        Transform parent = old.transform.parent;
        Vector3 position = old.transform.localPosition;
        Quaternion rotation = old.transform.localRotation;
        int sibling = old.transform.GetSiblingIndex();
        bool active = old.activeSelf;
        Object.DestroyImmediate(old);

        var arena = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        arena.transform.SetParent(parent, false);
        arena.transform.localPosition = position;
        arena.transform.localRotation = rotation;
        arena.transform.SetSiblingIndex(sibling);
        arena.SetActive(active);
        return arena;
    }

    // Peça de prefab aninhado não se apaga por script: o componente é desligado no lugar.
    private static bool TryDestroyComponent(Component component, System.Text.StringBuilder report)
    {
        try
        {
            Object.DestroyImmediate(component);
            return true;
        }
        catch (System.Exception e)
        {
            if (component is Behaviour behaviour)
                behaviour.enabled = false;
            report.AppendLine($"  não deu para remover {component.GetType().Name} de '{component.name}' (desligado): {e.Message}");
            return false;
        }
    }

    private static bool TryDestroyObject(GameObject go, System.Text.StringBuilder report)
    {
        try
        {
            Object.DestroyImmediate(go);
            return true;
        }
        catch (System.Exception e)
        {
            go.SetActive(false);
            report.AppendLine($"  não deu para apagar '{go.name}' (desligado): {e.Message}");
            return false;
        }
    }

    // Sobe apagando objetos que ficaram vazios (só Transform, sem filhos), sem tocar na raiz nem em peça de prefab
    // aninhado. Devolve quantos apagou.
    private static int PruneEmpty(Transform t, Transform root)
    {
        int removed = 0;
        while (t != null && t != root && t.childCount == 0 && t.GetComponents<Component>().Length == 1
               && !PrefabUtility.IsPartOfPrefabInstance(t.gameObject))
        {
            Transform parent = t.parent;
            Object.DestroyImmediate(t.gameObject);
            removed++;
            t = parent;
        }

        return removed;
    }

    private static bool Build() => Build(TrainingScene, BuildPath);

    private static bool Build(string scenePath, string buildPath)
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { scenePath },
            locationPathName = buildPath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[FreeV8Setup] build: {report.summary.result}, {report.summary.totalErrors} erro(s), {report.summary.outputPath}");
        return report.summary.result == BuildResult.Succeeded;
    }

    // ================================================================================
    // Validar no Unity
    // ================================================================================

    [MenuItem("PROJECT-IA/v8: preparar cérebro do treino (checkpoint mais novo)")]
    public static void PrepareWatchModel()
    {
        var start = new ProcessStartInfo(File.Exists(CondaPython) ? CondaPython : "python", "tools/v8_watch_onnx.py")
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using (Process process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Debug.LogError("[FreeV8Setup] não deu para preparar o cérebro: " + output);
                return;
            }

            Debug.Log("[FreeV8Setup] " + output.Trim());
        }

        AssetDatabase.ImportAsset(WatchModelPath, ImportAssetOptions.ForceUpdate);
    }

    [MenuItem("PROJECT-IA/v8 (em Play): dirigir com WASD")]
    public static void DriveInPlay() => Watch(useModel: false);

    [MenuItem("PROJECT-IA/v8 (em Play): assistir o cérebro")]
    public static void WatchInPlay() => Watch(useModel: true);

    [MenuItem("PROJECT-IA/v8 (em Play): dirigir com WASD", true)]
    [MenuItem("PROJECT-IA/v8 (em Play): assistir o cérebro", true)]
    private static bool CanWatch() => Application.isPlaying;

    // Liga só a 1ª arena (as outras saem: sem trainer, todo agente sem modelo obedeceria o WASD), põe o agente dela
    // em WASD ou no .onnx, liga o painel e seleciona agente + mapa (gizmos de pontos, portas e cone na aba Scene).
    private static void Watch(bool useModel)
    {
        ModelAsset model = null;
        if (useModel)
        {
            model = AssetDatabase.LoadAssetAtPath<ModelAsset>(WatchModelPath);
            if (model == null)
            {
                Debug.LogError($"[FreeV8Setup] sem {WatchModelPath}: rode antes \"v8: preparar cérebro do treino\" (fora do Play).");
                return;
            }
        }

        FreeArenaController[] arenas = Object.FindObjectsByType<FreeArenaController>(FindObjectsSortMode.None);
        System.Array.Sort(arenas, (a, b) => string.CompareOrdinal(a.name, b.name));
        FreeExplorerManager agent = null;
        foreach (FreeArenaController arena in arenas)
        {
            FreeExplorerManager candidate = arena.GetComponentInChildren<FreeExplorerManager>();
            if (agent == null && candidate != null)
            {
                agent = candidate;
                continue;
            }

            arena.gameObject.SetActive(false);
        }

        if (agent == null)
        {
            Debug.LogError("[FreeV8Setup] nenhum agente da v8 ativo na cena (abra a V8 - Training).");
            return;
        }

        BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
        if (useModel)
        {
            // Determinístico ANTES do tipo: só o setter do tipo recria a política.
            behavior.DeterministicInference = true;
            behavior.BehaviorType = BehaviorType.InferenceOnly;
            agent.SetModel(FreeExplorerManager.BehaviorNameV8, model);
        }
        else
        {
            behavior.BehaviorType = BehaviorType.HeuristicOnly;
        }

        agent.SetDebugHud(true);
        agent.EndEpisode();

        FreeArenaController watched = agent.GetComponentInParent<FreeArenaController>();
        FreeMap map = watched.Map;
        Selection.objects = map != null ? new Object[] { agent.gameObject, map.gameObject } : new Object[] { agent.gameObject };
        if (SceneView.lastActiveSceneView != null)
            SceneView.lastActiveSceneView.FrameSelected();

        Debug.Log($"[FreeV8Setup] {(useModel ? "assistindo o cérebro" : "dirigindo com WASD")} em '{agent.name}' " +
                  $"({watched.name}). Painel na Game view, gizmos na Scene.");
    }

    // ================================================================================
    // Cena de teste: jogar contra o modelo
    // ================================================================================

    private const string TestScene = "Assets/Scenes/Arthur/V8 - Test.unity";

    // A versão da cena de teste de antes do prefab sem nós, guardada na primeira montagem.
    private const string TestSceneBackup = "Assets/Scenes/Arthur/V8 - Test (com nós).unity";

    // O modelo promovido para o teste (results/<run>/FreeExplorer.onnx juntado com o .onnx.data num arquivo só).
    // Trocou de checkpoint: grave o novo aqui e rode o menu de novo.
    private const string TestModelPath = "Assets/FreeExplorer_v8.0_9.5M.onnx";

    // A tag do jogador no FreeArenaController (_playerTag): o PlayerDummy já vem com ela.
    private const string PlayerTag = "Goal";

    /// <summary>
    /// Deixa a "V8 - Test" pronta para JOGAR contra o modelo, numa Arena - V8 (sem nós), salvando a cena: troca a arena
    /// da cena por uma instância do prefab (a cena antiga fica em TestSceneBackup), liga o modo de jogo do
    /// FreeArenaController (alvo = jogador, sem currículo, sem timeout), põe o .onnx no monstro em inferência
    /// determinística, desliga a presa de treino, liga o PlayerDummy e deixa só a câmera e o AudioListener dele. Sem
    /// GameManager na cena, pegar o jogador só faz o monstro renascer longe dele.
    /// </summary>
    [MenuItem("PROJECT-IA/v8: montar cena de teste (jogador + modelo)")]
    public static void SetUpTestScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV8Setup] saia do Play antes de montar a cena de teste.");
            return;
        }

        ModelAsset model = AssetDatabase.LoadAssetAtPath<ModelAsset>(TestModelPath);
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        if (model == null || prefab == null)
        {
            Debug.LogError($"[FreeV8Setup] falta {(model == null ? TestModelPath : ArenaPrefabPath)} " +
                           "(o prefab sai do menu \"v8: criar prefab sem nós e montar a cena de treino\").");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        if (File.Exists(TestScene) && !File.Exists(TestSceneBackup))
            AssetDatabase.CopyAsset(TestScene, TestSceneBackup);

        Scene scene = File.Exists(TestScene)
            ? EditorSceneManager.OpenScene(TestScene, OpenSceneMode.Single)
            : EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // A arena antiga (com nós, ou qualquer arena que não seja a do prefab) sai; entra uma Arena - V8 no lugar dela.
        var report = new System.Text.StringBuilder();
        bool hasArena = false;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            bool isArena = root.GetComponentInChildren<FreeArenaController>(true) != null
                           || root.GetComponentInChildren<GraphArenaController>(true) != null;
            if (!isArena)
                continue;

            if (IsInstanceOf(root, ArenaPrefabPath) && !hasArena)
            {
                hasArena = true;
                continue;
            }

            if (!hasArena)
            {
                string oldName = root.name;
                ReplaceWithPrefab(root, prefab, scene).name = ArenaName;
                hasArena = true;
                report.AppendLine($"  '{oldName}' trocada por uma {ArenaName}");
            }
            else
            {
                Object.DestroyImmediate(root);
            }
        }

        if (!hasArena)
        {
            ((GameObject)PrefabUtility.InstantiatePrefab(prefab, scene)).name = ArenaName;
            report.AppendLine($"  {ArenaName} criada");
        }

        Transform player = null;
        foreach (FreeArenaController arena in Object.FindObjectsByType<FreeArenaController>(FindObjectsSortMode.None))
        {
            FreeExplorerManager agent = arena.GetComponentInChildren<FreeExplorerManager>(true);
            if (agent == null)
            {
                report.AppendLine($"  {arena.name}: PULADA (sem monstro da v8)");
                continue;
            }

            var arenaObject = new SerializedObject(arena);
            arenaObject.FindProperty("_gameMode").boolValue = true;
            arenaObject.ApplyModifiedProperties();

            BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
            Undo.RecordObject(behavior, "Montar cena de teste v8");
            behavior.Model = model;
            behavior.DeterministicInference = true;
            behavior.BehaviorType = BehaviorType.InferenceOnly;
            PrefabUtility.RecordPrefabInstancePropertyModifications(behavior);
            SetActive(agent.gameObject, true);

            foreach (FreeHider hider in arena.GetComponentsInChildren<FreeHider>(true))
                SetActive(hider.gameObject, false);

            // O jogador da arena: o objeto com a tag e o PlayerMovement (desligado na cena de treino).
            foreach (PlayerMovement movement in arena.GetComponentsInChildren<PlayerMovement>(true))
            {
                Transform candidate = movement.transform;
                while (candidate != null && !candidate.CompareTag(PlayerTag) && candidate != arena.transform)
                    candidate = candidate.parent;

                if (candidate == null || !candidate.CompareTag(PlayerTag))
                    continue;

                if (player == null)
                {
                    player = candidate;
                    SetActive(player.gameObject, true);
                }
                else
                {
                    // Jogador de outra arena: um só na cena (dois PlayerInputProvider ouviriam o mesmo WASD).
                    SetActive(candidate.gameObject, false);
                }
            }

            report.AppendLine($"  {arena.name}: modo de jogo, '{agent.name}' com {model.name} (inferência determinística)");
        }

        if (player == null)
        {
            Debug.LogError($"[FreeV8Setup] nenhum jogador (PlayerMovement com a tag {PlayerTag}) na arena da cena de teste.");
            return;
        }

        // Só a câmera e o ouvido do jogador: uma câmera de cena de treino na frente tapava a Game view.
        foreach (Camera camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            if (camera.enabled && !camera.transform.IsChildOf(player))
            {
                Undo.RecordObject(camera, "Montar cena de teste v8");
                camera.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(camera);
                report.AppendLine($"  câmera '{camera.name}' desligada");
            }
        }

        foreach (AudioListener listener in Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
        {
            if (listener.enabled && !listener.transform.IsChildOf(player))
            {
                Undo.RecordObject(listener, "Montar cena de teste v8");
                listener.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(listener);
                report.AppendLine($"  AudioListener '{listener.name}' desligado");
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, TestScene);

        string manager = Object.FindFirstObjectByType<GameManager>() != null
            ? "com GameManager: pegar = derrota"
            : "sem GameManager: pegar = o monstro renasce longe";
        Debug.Log($"[FreeV8Setup] cena de teste pronta e salva (jogador '{player.name}', {manager}):\n{report}");
    }

    private static void SetActive(GameObject target, bool active)
    {
        if (target.activeSelf == active)
            return;

        Undo.RecordObject(target, "Montar cena de teste v8");
        target.SetActive(active);
        PrefabUtility.RecordPrefabInstancePropertyModifications(target);
    }
}

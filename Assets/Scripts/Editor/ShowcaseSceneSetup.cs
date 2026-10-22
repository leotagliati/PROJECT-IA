using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using Assets.Scripts.Seeker;
using Assets.Scripts.Showcase;
using Unity.AI.Navigation;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Vitrine para gravar: uma arena por versão do monstro, lado a lado, cada uma com o .onnx dela em inferência e a presa
/// de treino (sem trainer valem os _default* das arenas, que são de caça). Só entram os cérebros que o código atual
/// alimenta: vetor 188 na linha Graph (v5.3 e v6.0), 126 na Free (v7.x) e 38 no Seeker (_legacyObservations).
///   "Vitrine: montar cena com todos os modelos"  refaz a cena do zero
///   "Vitrine: testar (Play, visita cada arena, fotos)"   relatório e fotos de cada arena em Logs/Vitrine/
/// </summary>
[InitializeOnLoad]
public static class ShowcaseSceneSetup
{
    private const string SceneFile = "Assets/Scenes/Arthur/Vitrine/Vitrine - Todos os modelos.unity";
    private const string Brains = "Assets/Brains/FreeExplorer/";
    private const string OldBrains = "Assets/Brains/_Antigos/";
    private const string ReportFolder = "Logs/Vitrine"; // Temp/ some quando o Unity fecha

    // Mapas da época do treino, tirados do git (o prefab de hoje foi refeito depois): v7.4.3 = arena de 18/10 com as
    // Walls de 17/10 (antes das paredes do tutorial); v7.4.4 = Cena Final - Mapa 1 (a498c6a).
    private const string HistoricalArenas = "Assets/Prefabs/Arenas/Vitrine/";
    private const string NavMeshFolder = "Assets/Scenes/Arthur/Vitrine/NavMesh/";
    private const string PhotoFolder = "Assets/Scenes/Arthur/Vitrine/Fotos/";

    // Folga entre arenas: longe o bastante para nenhum NavMesh.SamplePosition cair na arena do lado.
    private const float Gap = 60f;

    private enum Line
    {
        Seeker,
        Graph,
        Free,

        // Código da época recriado em Assets/Legacy/<versão>/ (tipos em namespace próprio): configuração genérica.
        Legacy,
    }

    private sealed class Spec
    {
        public string Version;
        public string Title;
        public string Detail;
        public string Prefab;

        // Cena de treino da época (Legacy): a vitrine leva a primeira arena dela.
        public string SourceScene;
        public string Model;
        public Line Line;
        public bool KeySensor = true;
        public bool PlanShowsEntered;

        // Campos serializados da lição em que o cérebro estava (sem trainer valem os _default* da arena).
        public (string component, string field, float value)[] Overrides = { };
    }

    private const string LegacyRoot = "Assets/Legacy/";

    // Valores da lição em que o cérebro foi salvo (results/<run>/configuration.yaml + training_status.json), nos
    // _default* do GraphArenaController da época.
    private static (string, string, float)[] Lesson(params (string field, float value)[] values) =>
        values.Select(v => ("GraphArenaController", "_default" + v.field, v.value)).ToArray();

    private static readonly Spec[] Specs =
    {
        Seeker("v1", "SeekerV1.onnx"),
        Seeker("v2", "SeekerV2.onnx"),
        Seeker("v3", "SeekerV3.onnx"),
        new()
        {
            Version = "v4.0", Title = "Primeiro GraphExplorer: grafo de nós com seta", Line = Line.Legacy,
            Detail = "Mapa Nodes. Explora nós colocados à mão, guiado pela seta (dica de para onde ir). Código de 08/09.\n" +
                     "GraphExplorerV1.onnx",
            SourceScene = LegacyRoot + "v4.0/Scenes/Arthur/Nodes.unity",
            Model = OldBrains + "GraphExplorerV1.onnx",
            Overrides = Lesson(("CoverageTarget", 0.9f), ("FrontierHint", 1f)),
        },
        new()
        {
            Version = "v4.3.1", Title = "Sem seta, do zero, no Node_4", Line = Line.Legacy,
            Detail = "Primeiro run válido no Node_4: nunca passou da 1ª lição, cobertura ~13%. Código de 27/09.\n" +
                     "GraphExplorer_node4_noarrow_9M.onnx",
            SourceScene = LegacyRoot + "v4.3.1/Scenes/Arthur/Node_4.unity",
            Model = OldBrains + "GraphExplorer_node4_noarrow_9M.onnx",
            // O Node_4 guardado tem raios 3 por lado/70°; o run usou os do NodeTraining4 (4/160°, obs_0 = 27).
            Overrides = Lesson(("CoverageTarget", 0.3f), ("FrontierHint", 0f))
                .Append(("RayPerceptionSensorComponent3D", "m_RaysPerDirection", 4f))
                .Append(("RayPerceptionSensorComponent3D", "m_MaxRayDegrees", 160f)).ToArray(),
        },
        new()
        {
            Version = "v4.3.6 E2", Title = "Em etapas: explorar tirando a seta", Line = Line.Legacy,
            Detail = "Movimento livre (andar e olhar separados); a seta vai sumindo. Parou em ~42% de cobertura. Código de 28/09.\n" +
                     "GraphExplorer_node4_e2_04_187k.onnx",
            SourceScene = LegacyRoot + "v4.3.6/Scenes/Arthur/Node_4.unity",
            Model = OldBrains + "GraphExplorer_node4_e2_04_187k.onnx",
            Overrides = Lesson(("CoverageTarget", 0.9f), ("FrontierHint", 0f), ("PrevisitedFraction", 0.5f), ("WeightJitter", 0.5f), ("SteerAssist", 0.7f)),
        },
        new()
        {
            Version = "v4.3.6 E3", Title = "Em etapas: achar o hider parado", Line = Line.Legacy,
            Detail = "Nasce o mapa de suspeita (onde a presa pode estar). Pega o hider em ~56% dos episódios. Código de 28/09.\n" +
                     "GraphExplorer_node4_e3_01_748k.onnx",
            SourceScene = LegacyRoot + "v4.3.6/Scenes/Arthur/Node_4.unity",
            Model = OldBrains + "GraphExplorer_node4_e3_01_748k.onnx",
            Overrides = Lesson(("CoverageTarget", 1.1f), ("FrontierHint", 0f), ("WeightJitter", 0.5f), ("PingRewardScale", 0f),
                ("DiscoveryRewardScale", 0.3f), ("SteerAssist", 0.7f), ("HiderMode", 1f), ("HiderNoise", 1f)),
        },
        new()
        {
            Version = "v5.0", Title = "Salas e portas, sem seta", Line = Line.Legacy,
            Detail = "Mapa Node_5: toda sala vale igual, porta perde valor ao repetir. ~70% das salas, pega ~2/3. Código de 02/10.\n" +
                     "GraphExplorer_v4.0.onnx",
            SourceScene = LegacyRoot + "v5.0/Scenes/Arthur/Node_5.unity",
            Model = OldBrains + "GraphExplorer_v4.0.onnx",
            Overrides = Lesson(("CoverageTarget", 1.1f), ("SteerAssist", 0.3f), ("HiderMode", 3f), ("HiderSpeed", 2.2f), ("HiderNoise", 0.3f),
                ("DiscoveryRewardScale", 0.5f), ("PingRewardScale", 0f), ("VisionExplores", 1f), ("HiderLoose", 1f)),
        },
        new()
        {
            Version = "v5.1", Title = "Planta de salas", Line = Line.Legacy,
            Detail = "Sensor com as 26 salas; ver a sala conta como explorar. 76–83% das salas, pega 83%. Código de 03/10.\n" +
                     "GraphExplorer_v4.1.onnx",
            SourceScene = LegacyRoot + "v5.1/Scenes/Arthur/Node_5.unity",
            Model = OldBrains + "GraphExplorer_v4.1.onnx",
            Overrides = Lesson(("CoverageTarget", 1.1f), ("SteerAssist", 0.3f), ("HiderMode", 3f), ("HiderSpeed", 2.2f), ("HiderNoise", 0.4f),
                ("DiscoveryRewardScale", 1f), ("PingRewardScale", 0f), ("VisionExplores", 1f), ("HiderLoose", 0f)),
        },
        new()
        {
            Version = "v5.1.2", Title = "Caça herdando a v5.1", Line = Line.Legacy,
            Detail = "Presa sem pausa que foge a 18 m, de 4 a 8 m/s. Vê ~89%, pega ~72%. Código de 03/10.\n" +
                     "GraphExplorer_v4.3.onnx",
            SourceScene = LegacyRoot + "v5.1/Scenes/Arthur/Node_5.unity",
            Model = OldBrains + "GraphExplorer_v4.3.onnx",
            Overrides = Lesson(("CoverageTarget", 1.1f), ("SteerAssist", 0.3f), ("HiderMode", 3f), ("HiderSpeed", 4f), ("HiderNoise", 0.4f),
                ("DiscoveryRewardScale", 0.5f), ("PingRewardScale", 0.4f), ("VisionExplores", 1f), ("HiderLoose", 0f)),
        },
        new()
        {
            Version = "v5.3", Title = "Salas e portas sobre o grafo de nós", Line = Line.Graph,
            Detail = "Mapa Node_5 (26 salas). Sem seta: explora por salas, mapa de suspeita e caça. 50M do zero.\n" +
                     "GraphExplorer_05_50M.onnx",
            Prefab = "Assets/Prefabs/Arenas/_Antigos/NodeTraining - V5 training.prefab",
            Model = OldBrains + "GraphExplorer_05_50M.onnx",
        },
        new()
        {
            Version = "v6.0", Title = "Grafo de nós + NavMesh na perseguição", Line = Line.Graph,
            Detail = "Caça herdando a v5.3: rota NavMesh só vendo o alvo, rastro de 3 s, pressa. 8M.\n" +
                     "GraphExplorer_v5.2.onnx",
            Prefab = "Assets/Prefabs/Arenas/_Antigos/NodeTraining - V6 Training.prefab",
            Model = OldBrains + "GraphExplorer_v5.2.onnx",
        },
        new()
        {
            Version = "v7.0", Title = "Explorador livre, do zero", Line = Line.Free, KeySensor = false,
            Detail = "Sem nós: pontos de visão gerados pelo NavMesh que ele precisa VER e portas achadas sozinhas. 9.5M.\n" +
                     "Mapa Node_5; no treino a presa ainda andava de nó em nó. FreeExplorer_v8.0_9.5M.onnx",
            Prefab = "Assets/Prefabs/Arenas/V8/Arena - V8.prefab",
            Model = Brains + "FreeExplorer_v8.0_9.5M.onnx",
        },
        new()
        {
            Version = "v7.1", Title = "Livre, currículo completo com chaves", Line = Line.Free,
            Detail = "Escritório mobiliado; presa que foge, se esconde e leva chaves para escapar. 35M do zero.\n" +
                     "FreeExplorer_v8.1M_35M.onnx",
            Prefab = "Assets/Prefabs/Arena - V9.prefab",
            Model = Brains + "FreeExplorer_v8.1M_35M.onnx",
        },
        new()
        {
            Version = "v7.2", Title = "O cérebro da v7.1 no Map - V8", Line = Line.Free,
            Detail = "Números do jogo (visão 80°/30 m, perseguição 9 m/s); a presa se esconde onde ele menos olha. 10M.\n" +
                     "FreeExplorer_v8.2_10M.onnx",
            Prefab = "Assets/Prefabs/Arenas/V8.2/Arena - V8.2.prefab",
            Model = Brains + "FreeExplorer_v8.2_10M.onnx",
        },
        new()
        {
            Version = "v7.3.1", Title = "Quebrar a ronda fixa (horizonte maior)", Line = Line.Free,
            Detail = "Map - V8. Ponto pouco visto paga mais e gamma 0.999 (~100 s à frente). 11.46M.\n" +
                     "FreeExplorer_v7.3.1_11.5M.onnx",
            Prefab = "Assets/Prefabs/Arenas/V8.2/Arena - V8.2.prefab",
            Model = Brains + "FreeExplorer_v7.3.1_11.5M.onnx",
        },
        new()
        {
            Version = "v7.4.3", Title = "No mapa do jogo: entrar em toda sala", Line = Line.Free,
            Detail = "Cena Final (21 salas), monstro a 5.5/6.5/8 m/s, presa com a corrida do jogador. 7.5M.\n" +
                     "FreeExplorer_v7.4.3_7.5M.onnx",
            Prefab = HistoricalArenas + "Arena - Cena Final (v7.4.3).prefab",
            Model = Brains + "FreeExplorer_v7.4.3_7.5M.onnx",
        },
        new()
        {
            Version = "v7.4.4", Title = "No mapa do jogo: planta com as salas visitadas", Line = Line.Free,
            PlanShowsEntered = true,
            Detail = "O cérebro do jogo hoje: a planta marca em que salas ele já entrou. 15M.\n" +
                     "FreeExplorer_v7.4.4_15M.onnx",
            Prefab = HistoricalArenas + "Arena - Cena Final (v7.4.4).prefab",
            Model = Brains + "FreeExplorer_v7.4.4_15M.onnx",
        },
    };

    private static Spec Seeker(string version, string model) => new()
    {
        Version = version, Title = "Seeker: perseguir com memória em grade", Line = Line.Seeker,
        Detail = $"Map_8, presa scriptada que anda em cardinais (HiderAgent). Modelo {model}.",
        Prefab = "Assets/Prefabs/Environment Map8.prefab",
        Model = "Assets/" + model,
        // O HiderAgent.prefab está com _moveSpeed 0 desde 3e8aae9; na vitrine a presa anda (era 2 antes).
        Overrides = new[] { ("HiderAgent", "_moveSpeed", 2f) },
    };

    [MenuItem("PROJECT-IA/Vitrine: montar cena com todos os modelos")]
    public static void Build()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[Vitrine] saia do Play antes.");
            return;
        }

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            if (SceneManager.GetSceneAt(i).isDirty)
            {
                Debug.LogError($"[Vitrine] {SceneManager.GetSceneAt(i).path} tem mudança não salva: salve ou descarte antes.");
                return;
            }
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SetupLighting(scene);
        Camera camera = CreateCamera(scene);

        int wallLayer = LayerMask.NameToLayer("Wall");
        var directorObject = new GameObject("Vitrine");
        SceneManager.MoveGameObjectToScene(directorObject, scene);
        var director = directorObject.AddComponent<ShowcaseDirector>();
        var directorSettings = new SerializedObject(director);
        directorSettings.FindProperty("_camera").objectReferenceValue = camera;
        directorSettings.FindProperty("_wallLayer").intValue = 1 << wallLayer;
        SerializedProperty entries = directorSettings.FindProperty("_entries");
        entries.ClearArray();

        float cursor = 0f;
        var summary = new StringBuilder();
        foreach (Spec spec in Specs)
        {
            var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(spec.Model);
            GameObject arena = model == null ? null
                : spec.SourceScene != null ? TakeArenaFromScene(spec.SourceScene, scene)
                : InstantiateArena(spec.Prefab, scene);
            if (arena == null)
            {
                Debug.LogError($"[Vitrine] {spec.Version}: falta {(model == null ? spec.Model : spec.SourceScene ?? spec.Prefab)} " +
                               "(ou a cena não tem agente); ficou de fora.");
                continue;
            }

            arena.name = $"{spec.Version} — {spec.Title}";

            if (!Configure(spec, arena, model, out Transform agent, out Transform target))
            {
                Object.DestroyImmediate(arena);
                continue;
            }

            Bounds bounds = MeasureArena(arena, wallLayer);
            arena.transform.position += new Vector3(cursor - bounds.min.x, 0f, -bounds.center.z);
            cursor += bounds.size.x + Gap;

            int index = entries.arraySize;
            string navMesh = RebakeNavMesh(arena, agent, target, $"{index + 1:00}_{spec.Version}");
            bounds = MeasureArena(arena, wallLayer);
            GameObject photo = CreatePhoto(arena, agent, target, bounds, $"{index + 1:00}_{spec.Version}", directorObject.transform);

            // Salva desligada (menos a primeira): as 18 com física ao mesmo tempo derrubam o PhysX.
            arena.SetActive(index == 0);
            Persist(arena);
            photo.SetActive(index != 0);

            entries.InsertArrayElementAtIndex(index);
            SerializedProperty entry = entries.GetArrayElementAtIndex(index);
            entry.FindPropertyRelative("version").stringValue = spec.Version;
            entry.FindPropertyRelative("title").stringValue = spec.Title;
            entry.FindPropertyRelative("detail").stringValue = spec.Detail;
            entry.FindPropertyRelative("arena").objectReferenceValue = arena.transform;
            entry.FindPropertyRelative("agent").objectReferenceValue = agent;
            entry.FindPropertyRelative("target").objectReferenceValue = target;
            entry.FindPropertyRelative("bounds").boundsValue = bounds;
            entry.FindPropertyRelative("photo").objectReferenceValue = photo;
            summary.AppendLine($"  {index + 1,2}. {spec.Version,-7} {Path.GetFileName(spec.Model)} em " +
                               $"{Path.GetFileNameWithoutExtension(spec.SourceScene ?? spec.Prefab)} " +
                               $"({bounds.size.x:0} x {bounds.size.z:0} m){navMesh}");
        }

        directorSettings.ApplyModifiedPropertiesWithoutUndo();

        Directory.CreateDirectory(Path.GetDirectoryName(SceneFile));
        if (!EditorSceneManager.SaveScene(scene, SceneFile))
        {
            Debug.LogError($"[Vitrine] não consegui salvar {SceneFile}.");
            return;
        }

        Debug.Log($"[Vitrine] {SceneFile} pronta com {entries.arraySize} arenas:\n{summary}" +
                  "Play e F1 para as teclas (←/→ arena, Tab câmera, G todas).");
    }

    // Reassa o NavMesh de cada arena na cena ABERTA, sem refazê-la: guarda o que foi ajustado à mão (paredes, móveis).
    // O bake manual do NavMeshSurface pega monstro e presa como obstáculo (Collect Children, todas as layers); aqui
    // eles ficam fora, como na montagem.
    [MenuItem("PROJECT-IA/Vitrine: reassar NavMesh (mantém a cena)")]
    public static void RebakeOpenScene()
    {
        ShowcaseDirector director = Object.FindAnyObjectByType<ShowcaseDirector>();
        Scene scene = SceneManager.GetActiveScene();
        if (Application.isPlaying || director == null || scene.path != SceneFile)
        {
            Debug.LogError($"[Vitrine] abra {SceneFile} (fora do Play) antes de reassar.");
            return;
        }

        var summary = new StringBuilder();
        for (int i = 0; i < director.Entries.Count; i++)
        {
            ShowcaseDirector.Entry entry = director.Entries[i];
            if (entry.arena == null || entry.arena.GetComponent<NavMeshSurface>() == null)
                continue;

            // Desligada, a arena não entrega nada ao bake.
            GameObject arena = entry.arena.gameObject;
            bool wasActive = arena.activeSelf;
            arena.SetActive(true);
            try
            {
                summary.AppendLine($"  {entry.version}{RebakeNavMesh(arena, entry.agent, entry.target, $"{i + 1:00}_{entry.version}")}");
            }
            finally
            {
                arena.SetActive(wasActive);
            }
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Vitrine] NavMesh reassado na cena aberta (monstro, presa e bloqueios fora):\n{summary}" +
                  "Rode \"Vitrine: build para o estande\" para o build levar isso.");
    }

    private const string PlayerPath = "Builds/Vitrine/Vitrine.exe";

    // Só a vitrine, para o PC do estande (ciclo automático; as teclas continuam valendo).
    [MenuItem("PROJECT-IA/Vitrine: build para o estande (Builds/Vitrine)")]
    public static void BuildForBooth() => BuildBoothPlayer();

    // Linha de comando: Unity.exe -batchmode -quit -projectPath <projeto> -executeMethod ShowcaseSceneSetup.BatchBuild
    public static void BatchBuild() => EditorApplication.Exit(BuildBoothPlayer() ? 0 : 1);

    private static bool BuildBoothPlayer()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[Vitrine] saia do Play antes do build.");
            return false;
        }

        UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { SceneFile },
            locationPathName = PlayerPath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
        });

        bool ok = report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded;
        if (ok)
            Debug.Log($"[Vitrine] build pronto: {PlayerPath} ({report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime.TotalSeconds:0} s).");
        else
            Debug.LogError($"[Vitrine] build falhou ({report.summary.result}, {report.summary.totalErrors} erro(s)): veja o Console.");
        return ok;
    }

    private static GameObject InstantiateArena(string prefabPath, Scene scene)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        return prefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene) : null;
    }

    // A cena de treino da época tem várias cópias da arena: a primeira raiz com agente vira um prefab ao lado da cena (uma
    // vez só) e a vitrine instancia esse prefab. Mover o objeto da cena aberta para a vitrine (MoveGameObjectToScene) e
    // fechar a de origem derrubou o Unity 4 vezes em 21/10 (PhysX e Enlighten com referências da cena fechada).
    private static GameObject TakeArenaFromScene(string scenePath, Scene into)
    {
        string prefabPath = Path.GetDirectoryName(scenePath).Replace('\\', '/') + "/Arena (vitrine).prefab";
        if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                return null;

            Scene source = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                GameObject root = source.GetRootGameObjects().FirstOrDefault(r => r.GetComponentInChildren<Agent>(true) != null);
                if (root == null)
                    return null;
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                EditorSceneManager.CloseScene(source, true);
            }
        }

        return InstantiateArena(prefabPath, into);
    }

    private static bool Configure(Spec spec, GameObject arena, ModelAsset model, out Transform agentTransform, out Transform target)
    {
        Agent agent;
        target = null;
        switch (spec.Line)
        {
            case Line.Legacy:
                agent = arena.GetComponentInChildren<Agent>(true);
                // Nome exato: "contém Hider" pegava o GraphHiderPerception, que fica no corpo do monstro.
                MonoBehaviour[] behaviours = arena.GetComponentsInChildren<MonoBehaviour>(true);
                MonoBehaviour hider = behaviours.FirstOrDefault(c => c != null && c.GetType().Name == "GraphHider");
                if (hider == null)
                    hider = behaviours.FirstOrDefault(c => c != null && c.GetType().Name == "HiderAgent");
                target = TransformOf(hider);
                break;
            case Line.Seeker:
                agent = arena.GetComponentInChildren<SeekerManager>(true);
                target = TransformOf(arena.GetComponentInChildren<HiderAgent>(true));
                if (agent != null)
                {
                    var settings = new SerializedObject(agent);
                    settings.FindProperty("_legacyObservations").boolValue = true;
                    settings.FindProperty("_mode").enumValueIndex = (int)SeekerMode.Training;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                    agent.GetComponent<BehaviorParameters>().BrainParameters.VectorObservationSize = SeekerManager.LegacyObservationCount;
                    Persist(agent.GetComponent<BehaviorParameters>());
                }

                // Metade dos episódios com labirinto, como a lição Mixed do seeker_curriculum.
                Component seekerArena = arena.GetComponentInChildren<SeekerArenaController>(true);
                if (seekerArena != null)
                {
                    var arenaSettings = new SerializedObject(seekerArena);
                    arenaSettings.FindProperty("_defaultMazeChance").floatValue = 0.5f;
                    arenaSettings.ApplyModifiedPropertiesWithoutUndo();
                }
                break;
            case Line.Graph:
                agent = arena.GetComponentInChildren<GraphExplorerManager>(true);
                target = TransformOf(arena.GetComponentInChildren<GraphHider>(true));
                SetBool(arena.GetComponentInChildren<GraphArenaController>(true), "_gameMode", false);
                break;
            default:
                agent = arena.GetComponentInChildren<FreeExplorerManager>(true);
                target = TransformOf(arena.GetComponentInChildren<FreeHider>(true));
                SetBool(arena.GetComponentInChildren<FreeArenaController>(true), "_gameMode", false);
                if (agent != null)
                {
                    var settings = new SerializedObject(agent);
                    settings.FindProperty("_keySensor").boolValue = spec.KeySensor;
                    settings.FindProperty("_planShowsEntered").boolValue = spec.PlanShowsEntered;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
                break;
        }

        agentTransform = agent != null ? agent.transform : null;
        if (agent == null)
        {
            Debug.LogError($"[Vitrine] {spec.Version}: {spec.Prefab} sem o agente da linha {spec.Line}; ficou de fora.");
            return false;
        }

        agent.gameObject.SetActive(true);
        Persist(agent.gameObject);
        if (target != null)
        {
            target.gameObject.SetActive(true);
            Persist(target.gameObject);
        }

        BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
        behavior.Model = model;
        behavior.BehaviorType = BehaviorType.InferenceOnly;
        Persist(behavior);

        foreach ((string component, string field, float value) in spec.Overrides)
            Override(arena, component, field, value, spec.Version);

        // Jogador, câmeras e ouvidos que vierem no prefab saem: quem filma e ouve é a câmera da vitrine.
        foreach (PlayerMovement player in arena.GetComponentsInChildren<PlayerMovement>(true))
        {
            player.gameObject.SetActive(false);
            Persist(player.gameObject);
        }
        foreach (Camera camera in arena.GetComponentsInChildren<Camera>(true))
        {
            camera.enabled = false;
            Persist(camera);
        }
        foreach (AudioListener listener in arena.GetComponentsInChildren<AudioListener>(true))
        {
            listener.enabled = false;
            Persist(listener);
        }

        // Teto esconderia o mapa visto de cima; só o desenho sai, o collider fica.
        foreach (Transform child in arena.GetComponentsInChildren<Transform>(true))
        {
            string lower = child.name.ToLowerInvariant();
            if (lower.Contains("ceil") || lower.Contains("teto") || lower.Contains("forro") || lower.Contains("roof"))
            {
                foreach (Renderer renderer in child.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = false;
                    Persist(renderer);
                }
            }
        }

        return true;
    }

    // NavMesh refeito em cima dos objetos que a arena tem AGORA (os assets dos prefabs foram assados antes de móveis e
    // paredes mudarem). Como nos menus de treino: monstro e presa fora, e os bloqueios de borda da v7.4 também (o
    // FreeV74Final assa antes de criá-los). Só os filhos da arena entram (CollectObjects = Children).
    private static string RebakeNavMesh(GameObject arena, Transform agent, Transform target, string assetName)
    {
        var surface = arena.GetComponent<NavMeshSurface>();
        if (surface == null)
            return "";

        var hidden = new List<GameObject>();
        void Hide(Transform t)
        {
            if (t != null && t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
                hidden.Add(t.gameObject);
            }
        }

        Hide(agent);
        Hide(target);
        Hide(arena.transform.Find("Bloqueios de borda (treino)"));
        try
        {
            Physics.SyncTransforms();
            surface.BuildNavMesh();
        }
        finally
        {
            foreach (GameObject g in hidden)
                g.SetActive(true);
        }

        if (surface.navMeshData == null)
        {
            Debug.LogError($"[Vitrine] {arena.name}: o bake não gerou NavMesh.");
            return ", NavMesh FALHOU";
        }

        Directory.CreateDirectory(NavMeshFolder);
        string path = NavMeshFolder + assetName + ".asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(surface.navMeshData, path);
        surface.navMeshData = AssetDatabase.LoadAssetAtPath<NavMeshData>(path);
        Persist(surface);
        return ", NavMesh reassado";
    }

    // Foto de cima da arena num quadro logo abaixo do chão dela: é o que a vista Todas mostra das arenas desligadas.
    private static GameObject CreatePhoto(GameObject arena, Transform agent, Transform target, Bounds bounds, string name, Transform parent)
    {
        const int width = 1024;
        int height = Mathf.Clamp(Mathf.RoundToInt(width * bounds.size.z / bounds.size.x), 64, 2048);
        var cameraObject = new GameObject("Câmera da foto") { hideFlags = HideFlags.HideAndDontSave };
        var camera = cameraObject.AddComponent<Camera>();
        var texture = new RenderTexture(width, height, 24);
        var image = new Texture2D(width, height, TextureFormat.RGB24, false);
        var hidden = new List<GameObject>();
        foreach (Transform body in new[] { agent, target })
        {
            if (body != null && body.gameObject.activeSelf)
            {
                body.gameObject.SetActive(false);
                hidden.Add(body.gameObject);
            }
        }

        try
        {
            camera.orthographic = true;
            camera.aspect = (float)width / height;
            camera.orthographicSize = bounds.extents.z;
            camera.transform.SetPositionAndRotation(new Vector3(bounds.center.x, bounds.max.y + 50f, bounds.center.z),
                Quaternion.Euler(90f, 0f, 0f));
            camera.farClipPlane = 500f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.07f, 0.07f, 0.08f);
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            image.Apply();
            Directory.CreateDirectory(PhotoFolder);
            File.WriteAllBytes(PhotoFolder + name + ".png", image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = null;
            camera.targetTexture = null;
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(cameraObject);
            foreach (GameObject g in hidden)
                g.SetActive(true);
        }

        AssetDatabase.ImportAsset(PhotoFolder + name + ".png");
        var photoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(PhotoFolder + name + ".png");
        string materialPath = PhotoFolder + name + ".mat";
        AssetDatabase.DeleteAsset(materialPath);
        var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        material.SetTexture("_BaseMap", photoTexture);
        AssetDatabase.CreateAsset(material, materialPath);

        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = $"Foto {name}";
        Object.DestroyImmediate(quad.GetComponent<Collider>());
        quad.transform.SetParent(parent, false);
        quad.transform.SetPositionAndRotation(new Vector3(bounds.center.x, bounds.min.y - 0.6f, bounds.center.z), Quaternion.Euler(90f, 0f, 0f));
        quad.transform.localScale = new Vector3(bounds.size.x, bounds.size.z, 1f);
        var renderer = quad.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        return quad;
    }

    private static Transform TransformOf(Component component) => component != null ? component.transform : null;

    // Mudança feita direto no componente (sem SerializedObject) não vira override de prefab e some ao salvar a cena.
    private static void Persist(Object target)
    {
        if (PrefabUtility.IsPartOfPrefabInstance(target))
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
        else
            EditorUtility.SetDirty(target);
    }

    // Por NOME do tipo: os tipos das versões antigas moram em namespaces Legacy_* que o Editor não referencia.
    private static void Override(GameObject arena, string component, string field, float value, string version)
    {
        MonoBehaviour target = arena.GetComponentsInChildren<MonoBehaviour>(true)
            .FirstOrDefault(c => c != null && c.GetType().Name == component);
        SerializedProperty property = target != null ? new SerializedObject(target).FindProperty(field) : null;
        if (property == null)
        {
            Debug.LogError($"[Vitrine] {version}: {component}.{field} não encontrado.");
            return;
        }

        switch (property.propertyType)
        {
            case SerializedPropertyType.Boolean:
                property.boolValue = value >= 0.5f;
                break;
            case SerializedPropertyType.Integer:
            case SerializedPropertyType.Enum:
                property.intValue = Mathf.RoundToInt(value);
                break;
            default:
                property.floatValue = value;
                break;
        }

        property.serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void SetBool(Object component, string property, bool value)
    {
        if (component == null)
            return;

        var settings = new SerializedObject(component);
        settings.FindProperty(property).boolValue = value;
        settings.ApplyModifiedPropertiesWithoutUndo();
    }

    // Pelas paredes: o chão e planos de fora são bem maiores que a arena.
    private static Bounds MeasureArena(GameObject arena, int wallLayer)
    {
        Renderer[] renderers = arena.GetComponentsInChildren<Renderer>();
        Renderer[] walls = renderers.Where(r => r.gameObject.layer == wallLayer).ToArray();
        Renderer[] source = walls.Length > 0 ? walls : renderers;
        if (source.Length == 0)
            return new Bounds(arena.transform.position, Vector3.one * 20f);

        Bounds bounds = source[0].bounds;
        foreach (Renderer renderer in source)
            bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    private static void SetupLighting(Scene scene)
    {
        var lightObject = new GameObject("Luz da vitrine", typeof(Light));
        SceneManager.MoveGameObjectToScene(lightObject, scene);
        lightObject.transform.rotation = Quaternion.Euler(60f, -30f, 0f);
        var light = lightObject.GetComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.2f;
        light.shadows = LightShadows.Soft;

        RenderSettings.fog = false;
        RenderSettings.skybox = null;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientSkyColor = new Color(0.7f, 0.7f, 0.7f);
        RenderSettings.ambientIntensity = 1f;
    }

    private static Camera CreateCamera(Scene scene)
    {
        var cameraObject = new GameObject("Câmera da vitrine", typeof(Camera), typeof(AudioListener)) { tag = "MainCamera" };
        SceneManager.MoveGameObjectToScene(cameraObject, scene);
        var camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.07f, 0.07f, 0.08f);
        camera.farClipPlane = 3000f;
        camera.fieldOfView = 55f;
        return camera;
    }

    // ------------------------------------------------------------------ teste
    // Visita arena por arena (só a da câmera fica ligada), volta à primeira para ver se ela anda depois de religada,
    // fotografa a vista "todas" e mede o 5x.

    private const string TestFlag = "Vitrine.Test";
    private const string TestStart = "Vitrine.TestStart";
    private const float SettleSeconds = 2f;
    private const float PhotoBeforeEnd = 1.5f;
    private const float FastSeconds = 6f;

    // O mesmo tempo de tela do ciclo do estande.
    private static float VisitSecondsOf(string version) => ShowcaseDirector.TourSeconds(version);

    private static readonly Dictionary<string, int> Logs = new();
    private static int _slot = -1;
    private static bool _slotPhotographed;
    private static float[] _visitDistance;
    private static float[] _targetDistance;
    private static string[] _targetInfo;
    private static Vector3 _lastPosition;
    private static Vector3 _lastTargetPosition;

    // Travada: o cérebro manda andar e o corpo não sai (preso), manda parar (escolha), ou troca de sentido sem parar.
    private sealed class Motion
    {
        public float Time;
        public float Idle;
        public float Stuck;
        public int Flips;
        public float StuckRun;
        public float WindowTime;
        public float WindowDistance;
        public float WindowMove;
        public readonly List<string> Spots = new();
    }

    private const float MoveCommand = 0.3f;
    private const float StuckSpeed = 0.5f;
    private const float StuckSpotSeconds = 1f;
    private const float StuckWindow = 0.5f;
    private static Motion[] _motion;
    private static Vector2 _lastMove;
    private static float _lastGameTime;
    private static bool _fast;
    private static float _fastGameStart;
    private static float _fastRealStart;

    static ShowcaseSceneSetup()
    {
        EditorApplication.update += TestTick;
        Application.logMessageReceived += CaptureLog;
    }

    [MenuItem("PROJECT-IA/Vitrine: testar (Play, visita cada arena, fotos)")]
    public static void Test()
    {
        if (Application.isPlaying || SceneManager.GetActiveScene().path != SceneFile)
        {
            Debug.LogError($"[Vitrine] abra {SceneFile} (fora do Play) antes de testar.");
            return;
        }

        Logs.Clear();
        Movers.Clear();
        _slot = -1;
        _visitDistance = null;
        _targetDistance = null;
        _targetInfo = null;
        _motion = null;
        _fast = false;
        SessionState.SetBool(TestFlag, true);
        SessionState.SetFloat(TestStart, -1f);
        EditorApplication.isPlaying = true;
    }

    private const string BatchFlag = "Vitrine.Batch";

    // Com o Unity fechado: Unity.exe -batchmode -projectPath <projeto> -executeMethod ShowcaseSceneSetup.BatchTest
    // [-vitrineRebuild] -logFile <log>. Sem -quit: o Editor fica de pé para o Play e sai sozinho depois do relatório.
    public static void BatchTest()
    {
        SessionState.SetBool(BatchFlag, true);
        if (Environment.GetCommandLineArgs().Contains("-vitrineRebuild"))
            Build();
        else
            EditorSceneManager.OpenScene(SceneFile);

        Test();
        if (!SessionState.GetBool(TestFlag, false))
            EditorApplication.Exit(1);
    }

    private static void CaptureLog(string message, string stackTrace, LogType type)
    {
        if (!SessionState.GetBool(TestFlag, false) || type == LogType.Log)
            return;

        string key = $"[{type}] {message.Split('\n')[0]}";
        Logs[key] = Logs.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static void TestTick()
    {
        if (!SessionState.GetBool(TestFlag, false) || !EditorApplication.isPlaying)
            return;

        ShowcaseDirector director = Object.FindAnyObjectByType<ShowcaseDirector>();
        if (director == null)
            return;

        // O teste escolhe as arenas; o ciclo do estande trocaria por cima.
        director.AutoTour = false;
        if (_slot < 0 && !director.IsReady)
            return;

        float start = SessionState.GetFloat(TestStart, -1f);
        if (start < 0f)
        {
            SessionState.SetFloat(TestStart, Time.realtimeSinceStartup);
            return;
        }

        IReadOnlyList<ShowcaseDirector.Entry> entries = director.Entries;
        int count = entries.Count;
        _visitDistance ??= new float[count + 1];
        _targetDistance ??= new float[count + 1];
        _targetInfo ??= new string[count];
        if (_motion == null)
        {
            _motion = new Motion[count + 1];
            for (int i = 0; i <= count; i++)
                _motion[i] = new Motion();
        }

        TrackMovers(entries);

        float t = Time.realtimeSinceStartup - start - SettleSeconds;
        if (t < 0f)
            return;

        // Visitas: uma por arena e mais uma de volta à primeira.
        int slot = 0;
        float slotStart = 0f;
        while (slot <= count && t >= slotStart + VisitSecondsOf(entries[slot < count ? slot : 0].version))
        {
            slotStart += VisitSecondsOf(entries[slot < count ? slot : 0].version);
            slot++;
        }

        if (slot <= count)
        {
            int arena = slot < count ? slot : 0;
            if (slot != _slot)
            {
                _slot = slot;
                _slotPhotographed = false;
                director.Select(arena, ShowcaseDirector.View.Follow);
                director.ApplyView();
                _lastPosition = AgentPosition(entries[arena]);
                _lastTargetPosition = TargetPosition(entries[arena]);
                _lastGameTime = Time.time;
                _lastMove = Vector2.zero;
                return;
            }

            Vector3 position = AgentPosition(entries[arena]);
            float step = Vector3.Distance(position, _lastPosition);
            if (step < 5f)
                _visitDistance[slot] += step; // salto maior é respawn
            TrackMotion(_motion[slot], entries[arena], position, step);
            _lastPosition = position;

            Vector3 targetPosition = TargetPosition(entries[arena]);
            float targetStep = Vector3.Distance(targetPosition, _lastTargetPosition);
            if (targetStep < 5f)
                _targetDistance[slot] += targetStep;
            _lastTargetPosition = targetPosition;

            if (!_slotPhotographed && slot < count && t - slotStart > VisitSecondsOf(entries[arena].version) - PhotoBeforeEnd)
            {
                _slotPhotographed = true;
                _targetInfo[arena] = DescribeTarget(entries[arena]);
                Photograph(director, arena);
            }

            return;
        }

        if (!_fast)
        {
            _fast = true;
            PhotographAll(director);
            director.Select(0, ShowcaseDirector.View.Follow);
            Time.timeScale = 5f;
            _fastGameStart = Time.time;
            _fastRealStart = Time.realtimeSinceStartup;
            return;
        }

        if (Time.realtimeSinceStartup - _fastRealStart < FastSeconds)
            return;

        WriteReport(director);
        SessionState.SetBool(TestFlag, false);
        EditorApplication.isPlaying = false;
        if (SessionState.GetBool(BatchFlag, false))
            EditorApplication.Exit(0);
    }

    private static string DescribeMotion(Motion m)
    {
        if (m.Time <= 0f)
            return "movimento: sem amostra (sem ação contínua ou agente desligado)";

        return $"movimento em {m.Time:0} s de jogo: parado por escolha {100f * m.Idle / m.Time:0}%, " +
               $"preso (manda andar e não sai) {100f * m.Stuck / m.Time:0}%, troca de sentido {m.Flips / m.Time:0.0}/s";
    }

    private static Vector3 AgentPosition(ShowcaseDirector.Entry entry) =>
        entry.agent != null ? entry.agent.position : Vector3.zero;

    // As duas primeiras ações contínuas são o andar em todas as linhas (Seeker, Graph, Free e as Legacy).
    private static void TrackMotion(Motion motion, ShowcaseDirector.Entry entry, Vector3 position, float step)
    {
        float dt = Time.time - _lastGameTime;
        _lastGameTime = Time.time;
        Agent agent = entry.agent != null ? entry.agent.GetComponent<Agent>() : null;
        if (dt <= 0f || step >= 5f || agent == null || !agent.isActiveAndEnabled)
            return;

        var actions = agent.GetStoredActionBuffers().ContinuousActions;
        if (actions.Length < 2)
            return;

        var move = Vector2.ClampMagnitude(new Vector2(actions[0], actions[1]), 1f);
        motion.Time += dt;
        if (move.magnitude < 0.1f)
            motion.Idle += dt;

        // Em janela: o corpo só anda no step de física, e o update do Editor roda várias vezes entre dois steps.
        motion.WindowTime += dt;
        motion.WindowDistance += step;
        motion.WindowMove += move.magnitude * dt;
        if (motion.WindowTime >= StuckWindow)
        {
            float window = motion.WindowTime;
            bool stuck = motion.WindowMove / window > MoveCommand && motion.WindowDistance / window < StuckSpeed;
            motion.WindowTime = motion.WindowDistance = motion.WindowMove = 0f;
            if (stuck)
            {
                motion.Stuck += window;
                motion.StuckRun += window;
                if (motion.StuckRun >= StuckSpotSeconds && motion.StuckRun - window < StuckSpotSeconds && motion.Spots.Count < 4)
                    motion.Spots.Add(DescribeSpot(entry, position));
            }
            else
            {
                motion.StuckRun = 0f;
            }
        }

        if (move.magnitude > MoveCommand && _lastMove.magnitude > MoveCommand && Vector2.Dot(move.normalized, _lastMove.normalized) < -0.5f)
            motion.Flips++;
        if (move.magnitude > MoveCommand)
            _lastMove = move;
    }

    // Onde travou: posição local na arena e o collider mais perto que não é dele (parede, móvel, batente).
    private static string DescribeSpot(ShowcaseDirector.Entry entry, Vector3 position)
    {
        Vector3 local = entry.arena != null ? entry.arena.InverseTransformPoint(position) : position;
        Collider nearest = null;
        float best = float.MaxValue;
        foreach (Collider c in Physics.OverlapSphere(position, 1.6f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (entry.agent != null && c.transform.IsChildOf(entry.agent))
                continue;
            float d = Vector3.Distance(c.ClosestPoint(position), position);
            if (d < best)
            {
                best = d;
                nearest = c;
            }
        }

        string near = nearest != null
            ? $"{PathOf(nearest.transform, entry.arena)} ({LayerMask.LayerToName(nearest.gameObject.layer)}, {best:0.0} m)"
            : "nada em 1.6 m";
        return $"({local.x:0.0}, {local.z:0.0}) perto de {near}";
    }

    private static Vector3 TargetPosition(ShowcaseDirector.Entry entry) =>
        entry.target != null ? entry.target.position : Vector3.zero;

    // Presa marcada certa? (dentro do monstro = marcador no lugar errado), ligada, e com corpo que a câmera desenha.
    private static string DescribeTarget(ShowcaseDirector.Entry entry)
    {
        Transform target = entry.target;
        if (target == null)
            return "presa: NENHUMA";

        string path = PathOf(target, entry.arena);
        if (entry.agent != null && (target.IsChildOf(entry.agent) || entry.agent.IsChildOf(target)))
            return $"presa: {path} — É O PRÓPRIO MONSTRO";
        if (!target.gameObject.activeInHierarchy)
            return $"presa: {path} desligada (versão sem presa?)";

        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        int drawn = renderers.Count(r => r.enabled && r.gameObject.activeInHierarchy && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly
                                         && (ViewMask & (1 << r.gameObject.layer)) != 0);
        Bounds body = default;
        bool any = false;
        foreach (Renderer r in renderers.Where(r => r.enabled && r.gameObject.activeInHierarchy))
        {
            if (!any)
                body = r.bounds;
            else
                body.Encapsulate(r.bounds);
            any = true;
        }

        string size = any ? $", corpo {body.size.x:0.0}x{body.size.y:0.0}x{body.size.z:0.0} m" : "";
        return $"presa: {path}, {drawn} de {renderers.Length} renderers desenhados{size}" + (drawn == 0 ? " — INVISÍVEL" : "");
    }

    private static int ViewMask
    {
        get
        {
            ShowcaseDirector director = Object.FindAnyObjectByType<ShowcaseDirector>();
            return director != null && director.ViewCamera != null ? director.ViewCamera.cullingMask : ~0;
        }
    }

    // Tudo que se mexe na arena (Animator, Rigidbody, NavMeshAgent), para achar corpo a mais andando além do monstro e da presa.
    private static readonly Dictionary<Transform, (int arena, Vector3 last, float travelled)> Movers = new();
    private static readonly HashSet<string> InvalidBodies = new();

    private static void TrackMovers(IReadOnlyList<ShowcaseDirector.Entry> entries)
    {
        if (Movers.Count == 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].arena == null)
                    continue;
                IEnumerable<Component> movers = entries[i].arena.GetComponentsInChildren<Animator>(true)
                    .Concat<Component>(entries[i].arena.GetComponentsInChildren<Rigidbody>(true))
                    .Concat(entries[i].arena.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true));
                foreach (Component mover in movers)
                    Movers[mover.transform] = (i, mover.transform.position, 0f);
            }

            return;
        }

        foreach (Transform mover in Movers.Keys.ToList())
        {
            if (mover == null)
                continue;
            (int arena, Vector3 last, float travelled) = Movers[mover];

            // Corpo com posição inválida corrompe o PhysX (crashes de 21/10 no heap dele): registra quem e onde.
            Vector3 p = mover.position;
            if ((float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) || p.sqrMagnitude > 1e12f)
                && InvalidBodies.Add($"{entries[arena].version}: {PathOf(mover, entries[arena].arena)} em {p}"))
                Debug.LogError($"[Vitrine] posição inválida: {entries[arena].version} {PathOf(mover, entries[arena].arena)} {p}");
            float step = Vector3.Distance(mover.position, last);
            Movers[mover] = (arena, mover.position, travelled + (step < 5f ? step : 0f));
        }
    }

    private static string PathOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (Transform c = t; c != null && c != root; c = c.parent)
            parts.Insert(0, c.name);
        return string.Join("/", parts);
    }

    private static void Photograph(ShowcaseDirector director, int index)
    {
        ShowcaseDirector.Entry entry = director.Entries[index];
        string prefix = $"{index + 1:00}_{entry.version}";
        foreach (ShowcaseDirector.View shot in new[] { ShowcaseDirector.View.Map, ShowcaseDirector.View.Follow })
        {
            director.Select(index, shot);
            director.ApplyView();
            Shoot(director.ViewCamera, $"{prefix}_{shot}");
        }

        // A presa de perto, para conferir o modelo dela.
        if (entry.target != null)
        {
            Camera camera = director.ViewCamera;
            camera.orthographic = false;
            Vector3 eye = entry.target.position - entry.target.forward * 5f + Vector3.up * 4f;
            camera.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(entry.target.position + Vector3.up - eye));
            Shoot(camera, $"{prefix}_Presa");
        }

        director.Select(index, ShowcaseDirector.View.Follow);
    }

    private static void PhotographAll(ShowcaseDirector director)
    {
        director.Select(0, ShowcaseDirector.View.All);
        director.ApplyView();
        Shoot(director.ViewCamera, "00_todas");
    }

    private static void Shoot(Camera camera, string name)
    {
        Directory.CreateDirectory(ReportFolder);
        var texture = new RenderTexture(1280, 720, 24);
        var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        RenderTexture previous = camera.targetTexture;
        try
        {
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(ReportFolder, name + ".jpg"), image.EncodeToJPG(80));
        }
        finally
        {
            RenderTexture.active = null;
            camera.targetTexture = previous;
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(image);
        }
    }

    private static void WriteReport(ShowcaseDirector director)
    {
        var report = new StringBuilder($"Vitrine — teste ({DateTime.Now:dd/MM HH:mm}), por arena 3.5 s (v1–v4), 6 s (v5–v6), 8 s (v7+), só a da câmera ligada\n");
        IReadOnlyList<ShowcaseDirector.Entry> entries = director.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            Transform agentTransform = entries[i].agent;
            Agent agent = agentTransform != null ? agentTransform.GetComponent<Agent>() : null;
            BehaviorParameters behavior = agent != null ? agent.GetComponent<BehaviorParameters>() : null;
            report.AppendLine($"{i + 1,2}. {entries[i].version,-9} modelo {(behavior != null && behavior.Model != null ? behavior.Model.name : "-")}, " +
                              $"cérebro {director.BrainStatus(i) ?? "não conferido"}, " +
                              $"monstro andou {_visitDistance[i]:0} m, presa andou {_targetDistance[i]:0} m");
            report.AppendLine($"      {_targetInfo[i] ?? "presa: não examinada"}");
            report.AppendLine($"      {DescribeMotion(_motion[i])}");
            foreach (string spot in _motion[i].Spots)
                report.AppendLine($"        travou > {StuckSpotSeconds:0} s em {spot}");
        }

        report.AppendLine($"De volta à {entries[0].version} (religada): monstro andou {_visitDistance[entries.Count]:0} m, " +
                          $"presa {_targetDistance[entries.Count]:0} m.");
        float real = Time.realtimeSinceStartup - _fastRealStart;
        if (real > 0f)
            report.AppendLine($"A 5x: {(Time.time - _fastGameStart) / real:0.0}x de verdade em {real:0} s.");

        report.AppendLine("O que andou mais de 3 m em cada arena:");
        foreach (var group in Movers.Where(m => m.Key != null && m.Value.travelled > 3f).GroupBy(m => m.Value.arena).OrderBy(g => g.Key))
        {
            report.AppendLine($"  {entries[group.Key].version}:");
            foreach (var mover in group.OrderByDescending(m => m.Value.travelled))
                report.AppendLine($"    {mover.Value.travelled,6:0} m  {PathOf(mover.Key, entries[group.Key].arena)}");
        }

        Movers.Clear();
        report.AppendLine(InvalidBodies.Count == 0 ? "Posições inválidas (NaN): nenhuma." : "POSIÇÕES INVÁLIDAS:");
        foreach (string body in InvalidBodies)
            report.AppendLine("  " + body);
        InvalidBodies.Clear();
        report.AppendLine(Logs.Count == 0 ? "Console: nenhum aviso nem erro." : "Console (avisos e erros, com contagem):");
        foreach (KeyValuePair<string, int> log in Logs.OrderByDescending(l => l.Value))
            report.AppendLine($"  {log.Value,4}x {log.Key}");

        Directory.CreateDirectory(ReportFolder);
        File.WriteAllText(Path.Combine(ReportFolder, "relatorio.txt"), report.ToString());
        Debug.Log($"[Vitrine] teste pronto: {ReportFolder}/relatorio.txt e as fotos ao lado.\n{report}");
    }
}

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
/// Monta a cena de treino da v8 e o build, sem abrir o editor (batchmode), ou pelo menu PROJECT-IA:
///   1. copia "V6 - Training" para "V8 - Training" (se ainda não existe);
///   2. em cada arena da cena (as 5 instâncias do NodeTraining - V6 Training, com os overrides da cena, como o hider
///      ligado), põe o FreeArenaController e roda "v8: montar arena livre" (cópia do monstro com o cérebro da v8);
///   3. builda só essa cena em Builds/V8/PROJECT-IA.exe.
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
    private const string SourceScene = "Assets/Scenes/Arthur/V6 - Training.unity";
    private const string TargetScene = "Assets/Scenes/Arthur/V8 - Training.unity";
    private const string BuildPath = "Builds/V8/PROJECT-IA.exe";
    private const string WatchModelPath = "Assets/FreeExplorer_v8_watch.onnx";

    // O Python do ambiente conda mlagents nesta máquina; sem ele, o "python" do PATH.
    private const string CondaPython = "C:/Users/55119/miniconda3/envs/mlagents/python.exe";

    [MenuItem("PROJECT-IA/v8: montar cena de treino e build")]
    public static void Run()
    {
        bool ok = SetUpScene() && Build();
        Debug.Log(ok ? "[FreeV8Setup] PRONTO." : "[FreeV8Setup] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    [MenuItem("PROJECT-IA/v8: só montar a cena")]
    public static void RunSceneOnly() => SetUpScene();

    // Abre a cena de treino da v8 (Unity.exe -projectPath . -executeMethod FreeV8Setup.OpenScene).
    [MenuItem("PROJECT-IA/v8: abrir a cena de treino")]
    public static void OpenScene() => EditorSceneManager.OpenScene(TargetScene, OpenSceneMode.Single);

    private static bool SetUpScene()
    {
        if (!File.Exists(TargetScene))
        {
            if (!AssetDatabase.CopyAsset(SourceScene, TargetScene))
            {
                Debug.LogError($"[FreeV8Setup] não consegui copiar {SourceScene}.");
                return false;
            }
        }

        Scene scene = EditorSceneManager.OpenScene(TargetScene, OpenSceneMode.Single);
        int arenas = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (GraphArenaController graphArena in root.GetComponentsInChildren<GraphArenaController>(true))
            {
                FreeArenaController arena = graphArena.GetComponent<FreeArenaController>();
                if (arena == null)
                    arena = graphArena.gameObject.AddComponent<FreeArenaController>();

                Debug.Log($"[FreeV8Setup] {graphArena.name}: {arena.SetUpForV8()}");
                arenas++;
            }
        }

        if (arenas == 0)
        {
            Debug.LogError("[FreeV8Setup] nenhuma arena (GraphArenaController) na cena.");
            return false;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
        {
            Debug.LogError("[FreeV8Setup] não consegui salvar a cena.");
            return false;
        }

        Debug.Log($"[FreeV8Setup] {arenas} arena(s) montada(s) em {TargetScene}.");
        return true;
    }

    private static bool Build()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { TargetScene },
            locationPathName = BuildPath,
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
}

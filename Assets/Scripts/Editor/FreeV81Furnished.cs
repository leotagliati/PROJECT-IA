using System.IO;
using Assets.Scripts.Free;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// A v8.1 no MAPA MOBILIADO (12/10): a mesma "Arena - V9" da v9 (TaskFurniture com colisor e layer certos nos móveis,
/// NavMesh assado, os pontos de chave do jogo em KeySpots), com o CÉREBRO DA v8.1. Existe nas duas branches de
/// propósito: vai da feature/v9-seeker para a feature/v8-livre junto com o prefab, sem depender de nada da v9.
///   "v8.1: cena mobiliada e build"  monta a "V8.1 - Mobiliada" com 5 Arena - V9 e builda em Builds/V8.1M.
/// O prefab sai do FreeV9Setup (só na v9). Na v8.1 ele roda como está: o Manager acerta o formato do cérebro no Awake
/// (vetor 126, 4 contínuas; um aviso por agente) e ignora os campos da v9. Cabe no cérebro da v8.1: 28 salas (teto 32),
/// até 4 portas por sala (teto 8), 3 chaves (teto 4).
/// Retomar o v8.1 no mapa novo = o mesmo run com --env=Builds/V8.1M/PROJECT-IA.exe e --resume (o currículo continua
/// onde está; a política se ajusta aos móveis).
/// Precisa dos modelos .blend importados: Blender instalado e associado no PC que abre o Unity.
/// </summary>
public static class FreeV81Furnished
{
    private const string ArenaPrefabPath = "Assets/Prefabs/Arena - V9.prefab";
    private const string SceneFile = "Assets/Scenes/Arthur/V8.1/V8.1 - Mobiliada.unity";
    private const string BuildPath = "Builds/V8.1M/PROJECT-IA.exe";
    private const int Arenas = 5;
    private const float Spacing = 250f;

    [MenuItem("PROJECT-IA/v8.1: cena mobiliada e build")]
    public static void SetUpAndBuild()
    {
        bool ok = SetUpScene() && Build();
        Debug.Log(ok ? "[FreeV81Furnished] PRONTO." : "[FreeV81Furnished] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    private static bool SetUpScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV81Furnished] saia do Play antes.");
            return false;
        }

        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return false;

        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        if (prefab == null)
        {
            Debug.LogError($"[FreeV81Furnished] falta {ArenaPrefabPath} (traga da feature/v9-seeker com o NavMesh asset).");
            return false;
        }

        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        for (int i = 0; i < Arenas; i++)
        {
            var arena = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            arena.name = $"Arena - V8.1 mobiliada ({i})";
            arena.transform.position = new Vector3(i * Spacing, 0f, 0f);

            // Treino: sensor das chaves (o cérebro da v8.1 tem) e sem .onnx no monstro (com o trainer ele não é usado,
            // e um .onnx de outro formato quebraria o Play solto).
            foreach (FreeExplorerManager agent in arena.GetComponentsInChildren<FreeExplorerManager>(true))
            {
                var settings = new SerializedObject(agent);
                settings.FindProperty("_keySensor").boolValue = true;
                settings.ApplyModifiedPropertiesWithoutUndo();
                BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
                if (behavior != null)
                    behavior.Model = null;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SceneFile));
        if (!EditorSceneManager.SaveScene(scene, SceneFile))
        {
            Debug.LogError($"[FreeV81Furnished] não consegui salvar {SceneFile}.");
            return false;
        }

        Debug.Log($"[FreeV81Furnished] {SceneFile}: {Arenas} arena(s) mobiliada(s), {Spacing:0} m entre elas.");
        return true;
    }

    // "Succeeded" com erro logado no meio também é falha (o 11/10 da v8.1: NavMeshPath no inicializador de campo).
    private static bool Build()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { SceneFile },
            locationPathName = BuildPath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"[FreeV81Furnished] build: {report.summary.result}, {report.summary.totalErrors} erro(s), {report.summary.outputPath}");
        return report.summary.result == BuildResult.Succeeded && report.summary.totalErrors == 0;
    }
}

using System.IO;
using System.Linq;
using Assets.Scripts.Free;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// JOGAR contra a v8.1 no MAPA MOBILIADO (12/10). Monta a "V8.1 - Mobiliada Teste" com UMA "Arena - V9" em modo de
/// jogo (alvo = o PlayerDummy da arena, sem currículo, sem presa de treino), o sensor das chaves ligado (o cérebro
/// da v8.1 tem a entrada) e o .onnx em inferência (sorteada ou não: _gameModeDeterministic do FreeExplorerManager).
/// Sem GameManager na cena, pegar o jogador só faz o monstro renascer longe.
///   "...(checkpoint mais novo)"   copia o FreeExplorer-N.onnx de maior N do run para Assets/FreeExplorer_v8.1M_teste.onnx
///                                 (o treino pode estar rodando: o checkpoint já gravado não muda mais).
///   "...(.onnx selecionado)"      usa o .onnx selecionado na janela Project (um modelo promovido, por exemplo).
/// Refaz a cena do zero toda vez: rode de novo para trocar de checkpoint.
/// </summary>
public static class FreeV81FurnishedTest
{
    private const string ArenaPrefabPath = "Assets/Prefabs/Arena - V9.prefab";
    private const string SceneFile = "Assets/Scenes/Arthur/V8.1 - Mobiliada Teste.unity";
    private const string RunFolder = "results/v8.1_zero_01/FreeExplorer";
    private const string CopiedModel = "Assets/FreeExplorer_v8.1M_teste.onnx";
    private const string PlayerTag = "Goal";

    [MenuItem("PROJECT-IA/v8.1: cena de teste mobiliada (checkpoint mais novo)")]
    public static void FromNewestCheckpoint()
    {
        if (!CanRun())
            return;

        // FreeExplorer-<step>.onnx: o de maior step. O FreeExplorer.onnx da raiz do run só sai no fim do treino.
        string newest = Directory.Exists(RunFolder)
            ? Directory.GetFiles(RunFolder, "FreeExplorer-*.onnx")
                .OrderByDescending(f => long.TryParse(Path.GetFileNameWithoutExtension(f).Substring("FreeExplorer-".Length), out long s) ? s : -1)
                .FirstOrDefault()
            : null;
        if (newest == null)
        {
            Debug.LogError($"[FreeV81FurnishedTest] nenhum checkpoint em {RunFolder}.");
            return;
        }

        // Export com pesos externos (torch >= 2.9 sem o dynamo=False no ML-Agents): o .onnx sozinho não tem pesos.
        if (File.Exists(newest + ".data"))
        {
            Debug.LogError($"[FreeV81FurnishedTest] {newest} tem os pesos em .onnx.data: junte num arquivo só antes " +
                           "(onnx.save_model(..., save_as_external_data=False)).");
            return;
        }

        File.Copy(newest, CopiedModel, true);
        AssetDatabase.ImportAsset(CopiedModel, ImportAssetOptions.ForceUpdate);
        Build(AssetDatabase.LoadAssetAtPath<ModelAsset>(CopiedModel), Path.GetFileName(newest));
    }

    [MenuItem("PROJECT-IA/v8.1: cena de teste mobiliada (.onnx selecionado)")]
    public static void FromSelection()
    {
        if (!CanRun())
            return;

        if (!(Selection.activeObject is ModelAsset model))
        {
            Debug.LogError("[FreeV81FurnishedTest] selecione um .onnx na janela Project antes.");
            return;
        }

        Build(model, AssetDatabase.GetAssetPath(model));
    }

    private static void CreateScreen(Scene scene, RenderTexture texture)
    {
        var canvasObject = new GameObject("Canvas (câmera do jogador)", typeof(RectTransform), typeof(Canvas),
            typeof(UnityEngine.UI.CanvasScaler));
        SceneManager.MoveGameObjectToScene(canvasObject, scene);
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        var view = new GameObject("CameraView", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
        view.transform.SetParent(canvasObject.transform, false);
        var rect = (RectTransform)view.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var image = view.GetComponent<UnityEngine.UI.RawImage>();
        image.texture = texture;
        image.raycastTarget = false;
    }

    private static bool CanRun()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV81FurnishedTest] saia do Play antes.");
            return false;
        }

        return Application.isBatchMode || EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
    }

    private static void Build(ModelAsset model, string source)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        if (model == null || prefab == null)
        {
            Debug.LogError($"[FreeV81FurnishedTest] falta {(model == null ? "o modelo" : ArenaPrefabPath)}.");
            return;
        }

        // Luz e câmera padrão: a câmera sai abaixo (só a do jogador fica), a luz ajuda a enxergar o mapa no teste.
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        var arenaObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        arenaObject.name = "Arena - V8.1 mobiliada (teste)";

        FreeArenaController arena = arenaObject.GetComponentInChildren<FreeArenaController>(true);
        FreeExplorerManager agent = arenaObject.GetComponentInChildren<FreeExplorerManager>(true);
        if (arena == null || agent == null)
        {
            Debug.LogError("[FreeV81FurnishedTest] o prefab não tem FreeArenaController ou monstro da v8.");
            return;
        }

        var arenaSettings = new SerializedObject(arena);
        arenaSettings.FindProperty("_gameMode").boolValue = true;
        arenaSettings.ApplyModifiedPropertiesWithoutUndo();

        var agentSettings = new SerializedObject(agent);
        agentSettings.FindProperty("_keySensor").boolValue = true;
        agentSettings.ApplyModifiedPropertiesWithoutUndo();

        BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
        // Determinístico ou não decide o FreeExplorerManager no Awake (_gameModeDeterministic).
        behavior.Model = model;
        behavior.BehaviorType = BehaviorType.InferenceOnly;
        agent.gameObject.SetActive(true);

        foreach (FreeHider hider in arenaObject.GetComponentsInChildren<FreeHider>(true))
            hider.gameObject.SetActive(false);

        // O jogador: o objeto com a tag Goal que tem (ou contém) o PlayerMovement.
        Transform player = null;
        foreach (PlayerMovement movement in arenaObject.GetComponentsInChildren<PlayerMovement>(true))
        {
            Transform candidate = movement.transform;
            while (candidate != null && !candidate.CompareTag(PlayerTag) && candidate != arenaObject.transform)
                candidate = candidate.parent;
            if (candidate != null && candidate.CompareTag(PlayerTag))
            {
                player = candidate;
                break;
            }
        }

        if (player == null)
        {
            Debug.LogError($"[FreeV81FurnishedTest] nenhum jogador (PlayerMovement com a tag {PlayerTag}) na arena.");
            return;
        }

        player.gameObject.SetActive(true);
        foreach (Camera camera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (!camera.transform.IsChildOf(player))
                camera.enabled = false;
        foreach (AudioListener listener in Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (!listener.transform.IsChildOf(player))
                listener.enabled = false;

        // A câmera do jogador desenha numa RenderTexture pequena (o pixelado); quem põe isso na tela é uma RawImage
        // num Canvas da cena (o "CameraView" da V8 - Test). Sem ela a Game view fica preta.
        Camera playerCamera = player.GetComponentsInChildren<Camera>(true).FirstOrDefault(c => c.targetTexture != null);
        if (playerCamera != null)
            CreateScreen(scene, playerCamera.targetTexture);

        Directory.CreateDirectory(Path.GetDirectoryName(SceneFile));
        if (!EditorSceneManager.SaveScene(scene, SceneFile))
        {
            Debug.LogError($"[FreeV81FurnishedTest] não consegui salvar {SceneFile}.");
            return;
        }

        Debug.Log($"[FreeV81FurnishedTest] {SceneFile} pronta: monstro com {source} (sensor das chaves)," +
                  $"jogador '{player.name}'. Aperte Play.");
    }
}

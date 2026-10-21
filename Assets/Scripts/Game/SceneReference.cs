using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Cena escolhida arrastando o asset no Inspector, em vez de nome ou índice digitados. Renomear ou mover a cena não
/// quebra (o caminho é relido do asset a cada serialização, inclusive no build), e reordenar o Build Settings também
/// não: o índice é procurado pelo caminho na hora de carregar.
///
/// Por que não o índice direto: inserir ou reordenar cenas no Build Settings faz o mesmo número abrir outra cena,
/// sem erro nenhum. Aqui, cena fora do Build Settings vira erro no Console.
/// </summary>
[Serializable]
public class SceneReference : ISerializationCallbackReceiver
{
#if UNITY_EDITOR
    [SerializeField] private UnityEditor.SceneAsset sceneAsset;
#endif

    // O que o player usa: o build não tem SceneAsset, só este caminho.
    [SerializeField, HideInInspector] private string scenePath;

    public string Path => scenePath;

    public bool IsSet => !string.IsNullOrEmpty(scenePath);

    /// <summary>-1 se a cena não está (ou está desligada) no Build Settings.</summary>
    public int BuildIndex => IsSet ? SceneUtility.GetBuildIndexByScenePath(scenePath) : -1;

    public void Load()
    {
        if (CanLoad())
            SceneManager.LoadScene(BuildIndex);
#if UNITY_EDITOR
        else if (IsSet)
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scenePath, new LoadSceneParameters(LoadSceneMode.Single));
#endif
    }

    /// <summary>Null se a cena não pode ser carregada; quem chama aborta a transição.</summary>
    public AsyncOperation LoadAsync()
    {
        if (CanLoad())
            return SceneManager.LoadSceneAsync(BuildIndex);
#if UNITY_EDITOR
        // Cena de teste fora do Build Settings: no Editor ainda dá para abrir pelo caminho (como o GameManager faz).
        if (IsSet)
            return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(scenePath, new LoadSceneParameters(LoadSceneMode.Single));
#endif
        return null;
    }

    private bool CanLoad()
    {
        if (!IsSet)
        {
            Debug.LogError("[SceneReference] Nenhuma cena atribuída no Inspector.");
            return false;
        }

        if (BuildIndex >= 0)
            return true;

        Debug.LogError($"[SceneReference] '{scenePath}' não está ligada no Build Settings — no build ela não abre.");
        return false;
    }

    public void OnBeforeSerialize()
    {
#if UNITY_EDITOR
        // Relido a cada serialização: renomear ou mover a cena atualiza o caminho sem tocar no componente.
        scenePath = sceneAsset != null ? UnityEditor.AssetDatabase.GetAssetPath(sceneAsset) : string.Empty;
#endif
    }

    public void OnAfterDeserialize() { }

    public override string ToString() => scenePath;
}

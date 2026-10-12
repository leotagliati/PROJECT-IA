using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Por que está ROSA (ou branco)? Varre os renderers das cenas abertas e agrupa por material: vazio (null), shader
/// de erro/não suportado, shader que não é do URP (pipeline antigo = rosa no URP) e URP sem textura (branco).
/// Escreve em Logs/materials_audit.txt e resume no Console. Só lê.
/// </summary>
public static class SceneMaterialAudit
{
    [MenuItem("PROJECT-IA/Diagnóstico: materiais rosa/brancos na cena aberta")]
    public static void Run()
    {
        var groups = new Dictionary<string, List<string>>();
        void Add(string key, string who)
        {
            if (!groups.TryGetValue(key, out List<string> list))
                groups[key] = list = new List<string>();
            list.Add(who);
        }

        int renderers = 0;
        for (int s = 0; s < SceneManager.sceneCount; s++)
        {
            Scene scene = SceneManager.GetSceneAt(s);
            if (!scene.isLoaded)
                continue;

            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                    continue;
                renderers++;
                string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(renderer.gameObject);
                string who = $"{Path(renderer.transform)}{(string.IsNullOrEmpty(source) ? "" : $"  [{source}]")}";

                Material[] materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material m = materials[i];
                    if (m == null)
                    {
                        Add($"VAZIO (null) — slot {i}", who);
                        continue;
                    }

                    string where = AssetDatabase.GetAssetPath(m);
                    Shader shader = m.shader;
                    string shaderName = shader != null ? shader.name : "(sem shader)";
                    if (shader == null || shaderName == "Hidden/InternalErrorShader" || !shader.isSupported)
                        Add($"SHADER COM ERRO '{shaderName}' — material '{m.name}' ({where})", who);
                    else if (!shaderName.StartsWith("Universal Render Pipeline") && !shaderName.StartsWith("Shader Graphs")
                             && !shaderName.StartsWith("Sprites") && !shaderName.StartsWith("UI/") && !shaderName.StartsWith("TextMeshPro"))
                        Add($"SHADER FORA DO URP '{shaderName}' — material '{m.name}' ({where})", who);
                    else if (m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") == null && m.mainTexture == null)
                        Add($"sem textura (branco) — material '{m.name}' ({where})", who);
                }
            }
        }

        var report = new StringBuilder($"{renderers} renderers nas cenas abertas\n");
        foreach (var g in groups.OrderBy(g => g.Key.StartsWith("sem textura")).ThenByDescending(g => g.Value.Count))
        {
            report.AppendLine($"\n{g.Value.Count,4}x  {g.Key}");
            foreach (string who in g.Value.Distinct().Take(5))
                report.AppendLine($"        {who}");
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText("Logs/materials_audit.txt", report.ToString());
        Debug.Log("[SceneMaterialAudit] " + string.Join("\n", report.ToString().Split('\n').Take(40)) +
                  "\n(completo em Logs/materials_audit.txt)");
    }

    // Batchmode: reimporta os modelos do pacote office (.blend e .fbx), abre a TaskFurniture e roda o diagnóstico.
    // Modelo não reimporta sozinho quando a textura que ele procurava aparece (12/10: textures movida para
    // Assets/Prefabs/Textures, onde o caminho do .blend e a busca "Textures" do .fbx a encontram).
    public static void ReimportOfficeAndAudit()
    {
        string[] folders = { "Assets/Prefabs/Import_Assets/OFFICE", "Assets/Prefabs/Import_Assets/psx_office_source" };
        string[] models = AssetDatabase.FindAssets("t:Model", folders).Select(AssetDatabase.GUIDToAssetPath).ToArray();
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (string model in models)
                AssetDatabase.ImportAsset(model, ImportAssetOptions.ForceUpdate);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }

        Debug.Log($"[SceneMaterialAudit] {models.Length} modelo(s) reimportado(s)");
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/TaskFurniture.unity");
        Run();
        if (Application.isBatchMode)
            EditorApplication.Exit(0);
    }

    private static string Path(Transform t)
    {
        var parts = new List<string>();
        for (Transform x = t; x != null; x = x.parent)
            parts.Add(x.name);
        parts.Reverse();
        return string.Join("/", parts.Skip(Mathf.Max(0, parts.Count - 4)));
    }
}

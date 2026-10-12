using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Móveis da v9: uma CÓPIA da TaskFurniture com colisor e layer certos, sem tocar na original (que é da main e o
/// time edita). Menu "PROJECT-IA/v9: copiar TaskFurniture e corrigir móveis"; o "v9: montar Arena - V9" chama isto
/// antes, sempre, para a cópia seguir a original.
///
/// Por quê (levantamento de 12/10 em Constraints/Map_Objects): os .fbx dos móveis vieram com "Generate Colliders"
/// desligado, então 95 móveis GRANDES não tinham colisor nenhum (o Archive inteiro: 84 arquivos de gaveta; mais
/// cafeteiras, poltrona, sofá, cadeiras) e a visão e o NavMesh os atravessavam; e 147 móveis com colisor estavam na
/// layer Default, que a visão do monstro (Wall/Obstacle/Door) também atravessa.
///   1. Peça grande sem colisor (nem nela, nem acima dela até o Map_Objects) ganha um BoxCollider do tamanho da
///      malha. "Grande" = 0.4 m de altura ou mais e o nome não é de item de mesa (papel, mouse, caneca...): esses
///      ficam sem colisor de propósito, não devem tapar visão nem caminho. Estante vira caixa cheia (tapa a visão
///      entre as prateleiras): simples e do lado seguro.
///   2. Todo colisor sólido de móvel na Default vai para Obstacle.
/// Em instância de prefab ou de .fbx o colisor e a layer entram como override: nada é desempacotado. Tags não
/// mudam: a v8/v9 reconhece parede e móvel pela LAYER; a única tag que o código usa é Goal (jogador e presa).
/// Precisa das malhas importadas (os .blend dependem do Blender instalado e associado): peça sem malha é listada
/// no Console e fica sem colisor.
/// </summary>
public static class FreeV9Furniture
{
    public const string OriginalScene = "Assets/Scenes/TaskFurniture.unity";
    public const string FixedScene = "Assets/Scenes/Arthur/V9 - TaskFurniture.unity";

    private const string ConstraintsName = "Constraints";
    private const string FurnitureGroup = "Map_Objects";

    // Abaixo disto (m de altura no mundo) a peça não tapa a visão (olho a 2.5 m, pontos a 1.2 m) nem atrapalha o
    // caminho de um jeito que importe.
    private const float MinHeight = 0.4f;

    // Itens de mesa: ficam sem colisor mesmo se altos (luminária, monitor). Comparado com o nome em minúsculas.
    private static readonly string[] DeskProps =
    {
        "paper", "mouse", "keyboard", "pen", "mug", "clock", "phone", "desklamp", "ball", "pizza", "frame",
        "monitor", "desktop", "computer_tower",
    };

    [MenuItem("PROJECT-IA/v9: copiar TaskFurniture e corrigir móveis")]
    public static void FixMenu()
    {
        bool ok = CreateFixedCopy();
        Debug.Log(ok ? "[FreeV9Furniture] PRONTO." : "[FreeV9Furniture] FALHOU (veja acima).");
        if (Application.isBatchMode)
            EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>Refaz a cópia corrigida a partir da original. False se algo falhou (a original nunca é tocada).</summary>
    public static bool CreateFixedCopy()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV9Furniture] saia do Play antes.");
            return false;
        }

        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return false;

        // Refeita do zero toda vez: a cópia segue o que o time mudar na original.
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(FixedScene) != null)
            AssetDatabase.DeleteAsset(FixedScene);
        if (!AssetDatabase.CopyAsset(OriginalScene, FixedScene))
        {
            Debug.LogError($"[FreeV9Furniture] não consegui copiar {OriginalScene} para {FixedScene}.");
            return false;
        }

        Scene scene = EditorSceneManager.OpenScene(FixedScene, OpenSceneMode.Single);
        Transform furniture = FindFurniture(scene);
        if (furniture == null)
        {
            Debug.LogError($"[FreeV9Furniture] sem {ConstraintsName}/{FurnitureGroup} em {FixedScene}.");
            return false;
        }

        var added = new List<string>();
        var noMesh = new List<string>();
        int deskProps = 0, low = 0;
        var seen = new HashSet<GameObject>();
        foreach (MeshRenderer renderer in furniture.GetComponentsInChildren<MeshRenderer>(true))
        {
            GameObject piece = PieceOf(renderer.gameObject);
            if (!seen.Add(piece) || HasSolidCollider(piece.transform, furniture))
                continue;

            if (IsDeskProp(piece.name))
            {
                deskProps++;
                continue;
            }

            if (!TryLocalBounds(piece.transform, out Bounds local, out float height))
            {
                noMesh.Add(PathOf(piece.transform, furniture));
                continue;
            }

            if (height < MinHeight)
            {
                low++;
                continue;
            }

            var box = piece.AddComponent<BoxCollider>();
            box.center = local.center;
            box.size = local.size;
            added.Add($"{PathOf(piece.transform, furniture)} ({height:0.0} m)");
        }

        int obstacle = LayerMask.NameToLayer("Obstacle");
        int relayered = 0;
        foreach (Collider collider in furniture.GetComponentsInChildren<Collider>(true))
        {
            if (collider.isTrigger || collider.gameObject.layer != 0)
                continue;

            collider.gameObject.layer = obstacle;
            relayered++;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
        {
            Debug.LogError($"[FreeV9Furniture] não consegui salvar {FixedScene}.");
            return false;
        }

        var report = new StringBuilder();
        report.AppendLine($"[FreeV9Furniture] {FixedScene} (cópia de {OriginalScene}, que não foi tocada):");
        report.AppendLine($"  {added.Count} BoxCollider(s) novo(s) em móvel grande sem colisor");
        report.AppendLine($"  {relayered} colisor(es) de móvel Default -> Obstacle");
        report.AppendLine($"  sem colisor de propósito: {deskProps} item(ns) de mesa, {low} peça(s) baixa(s) (< {MinHeight} m)");
        foreach (IGrouping<string, string> room in added.GroupBy(RoomOf).OrderBy(g => g.Key))
            report.AppendLine($"    {room.Key}: {string.Join(", ", room.Select(p => p.Substring(p.IndexOf('/') + 1)))}");
        Debug.Log(report.ToString());

        if (noMesh.Count > 0)
        {
            Debug.LogWarning($"[FreeV9Furniture] {noMesh.Count} peça(s) SEM MALHA importada (o .blend não converteu? confira o " +
                             $"Blender) ficaram sem colisor:\n  {string.Join("\n  ", noMesh)}");
        }

        return true;
    }

    private static Transform FindFurniture(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            Transform constraints = root.transform.Find(ConstraintsName);
            Transform furniture = constraints != null ? constraints.Find(FurnitureGroup) : null;
            if (furniture != null)
                return furniture;
        }

        return null;
    }

    // A peça é a instância de prefab inteira (um .fbx com várias malhas é um móvel só); objeto solto é ele mesmo.
    private static GameObject PieceOf(GameObject go) =>
        PrefabUtility.IsPartOfPrefabInstance(go) ? PrefabUtility.GetOutermostPrefabInstanceRoot(go) : go;

    // Colisor sólido na peça (ou dentro dela) ou num pai até o Map_Objects (mesa com o colisor no objeto de cima).
    private static bool HasSolidCollider(Transform piece, Transform furniture)
    {
        if (piece.GetComponentsInChildren<Collider>(true).Any(c => !c.isTrigger))
            return true;

        for (Transform t = piece.parent; t != null && t != furniture; t = t.parent)
        {
            if (t.GetComponents<Collider>().Any(c => !c.isTrigger))
                return true;
        }

        return false;
    }

    private static bool IsDeskProp(string name)
    {
        string lower = name.ToLowerInvariant();
        return DeskProps.Any(lower.Contains);
    }

    // Caixa das malhas da peça no espaço LOCAL dela (é o espaço do BoxCollider) e a altura no mundo.
    private static bool TryLocalBounds(Transform piece, out Bounds local, out float height)
    {
        local = default;
        height = 0f;
        bool any = false;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        foreach (MeshFilter filter in piece.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null)
                continue;

            Bounds b = mesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                Vector3 world = filter.transform.TransformPoint(corner);
                Vector3 inPiece = piece.InverseTransformPoint(world);
                if (!any)
                    local = new Bounds(inPiece, Vector3.zero);
                else
                    local.Encapsulate(inPiece);
                any = true;
                minY = Mathf.Min(minY, world.y);
                maxY = Mathf.Max(maxY, world.y);
            }
        }

        height = any ? maxY - minY : 0f;
        return any;
    }

    private static string PathOf(Transform t, Transform furniture)
    {
        var parts = new List<string>();
        for (; t != null && t != furniture; t = t.parent)
            parts.Insert(0, t.name);
        return string.Join("/", parts);
    }

    private static string RoomOf(string path) => path.Contains('/') ? path.Substring(0, path.IndexOf('/')) : path;
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Assets.Scripts.Free;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// Quais salas o CORPO do monstro alcança: assa, numa cena vazia, um NavMesh temporário com o raio da cápsula dele (o
/// do mapa usa o do agente padrão, menor) e lista as salas sem caminho e a largura das portas. Grava Logs/v7.4_body.txt.
/// </summary>
public static class FreeV74BodyCheck
{
    private const string ArenaPrefabPath = "Assets/Prefabs/Arenas/V7.4/Arena - Cena Final.prefab";
    private const string Output = "Logs/v7.4_body.txt";

    [MenuItem("PROJECT-IA/v7.4: teste de passagem do corpo do monstro")]
    public static void Run()
    {
        if (Application.isPlaying || SceneManager.GetActiveScene().isDirty)
        {
            Debug.LogError("[FreeV74Body] saia do Play e salve a cena antes.");
            return;
        }

        var r = new StringBuilder();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var arena = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath), scene);
        FreeMap map = arena.GetComponentInChildren<FreeMap>(true);
        FreeExplorerManager seeker = arena.GetComponentInChildren<FreeExplorerManager>(true);
        CapsuleCollider body = seeker.GetComponent<CapsuleCollider>();
        float bodyRadius = body.radius * Mathf.Max(seeker.transform.lossyScale.x, seeker.transform.lossyScale.z);
        float navRadius = NavMesh.GetSettingsByID(0).agentRadius;
        r.AppendLine($"raio do corpo do monstro {bodyRadius:0.00} m, raio do NavMesh do mapa {navRadius:0.00} m");

        map.EnsureBuilt();
        int start = Enumerable.Range(0, map.RoomCount).OrderByDescending(x => map.RoomPoints(x).Count).First();

        // Atores fora do bake (a cápsula deles viraria buraco), como no FreeV74Final.
        var actors = arena.GetComponentsInChildren<FreeExplorerManager>(true).Select(x => x.gameObject)
            .Concat(arena.GetComponentsInChildren<FreeHider>(true).Select(x => x.gameObject))
            .Concat(arena.GetComponentsInChildren<PlayerMovement>(true).Select(x => x.gameObject)).ToList();
        foreach (GameObject a in actors)
            a.SetActive(false);

        var sources = new List<NavMeshBuildSource>();
        NavMeshBuilder.CollectSources(arena.transform, ~0, NavMeshCollectGeometry.PhysicsColliders, 0,
            new List<NavMeshBuildMarkup>(), sources);

        foreach (float radius in new[] { navRadius, bodyRadius, bodyRadius + 0.1f })
        {
            // O NavMesh do prefab sai do caminho: só o de teste fica carregado.
            foreach (var surface in arena.GetComponentsInChildren<Unity.AI.Navigation.NavMeshSurface>(true))
                surface.enabled = false;
            NavMeshBuildSettings settings = NavMesh.GetSettingsByID(0);
            settings.agentRadius = radius;
            NavMeshData data = NavMeshBuilder.BuildNavMeshData(settings, sources,
                new Bounds(arena.transform.position, Vector3.one * 400f), arena.transform.position, arena.transform.rotation);
            NavMeshDataInstance instance = NavMesh.AddNavMeshData(data);

            var blocked = new List<string>();
            var path = new NavMeshPath();
            Vector3 from = Snap(map.PointGround(map.RoomPoints(start)[0]));
            for (int room = 0; room < map.RoomCount; room++)
            {
                IReadOnlyList<int> points = map.RoomPoints(room);
                int reachable = points.Count(p => NavMesh.CalculatePath(from, Snap(map.PointGround(p)), NavMesh.AllAreas, path)
                                                  && path.status == NavMeshPathStatus.PathComplete);
                if (reachable < points.Count)
                    blocked.Add($"S{room:00} {reachable}/{points.Count} pontos");
            }

            r.AppendLine($"\nNavMesh com raio {radius:0.00}: " + (blocked.Count == 0 ? "todas as salas alcançáveis" :
                $"{blocked.Count} sala(s) com pontos sem caminho:\n  " + string.Join("\n  ", blocked)));
            NavMesh.RemoveNavMeshData(instance);
            Object.DestroyImmediate(data);
        }

        r.AppendLine("\nportas (largura do vão) por sala:");
        for (int room = 0; room < map.RoomCount; room++)
        {
            var widths = Enumerable.Range(0, map.DoorCount).Select(map.GetDoor)
                .Where(d => d.RoomNegative == room || d.RoomPositive == room)
                .Select(d => $"{d.HalfWidth * 2f:0.00} ({d.Source})");
            r.AppendLine($"  S{room:00}: {string.Join(", ", widths)}");
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText(Output, r.ToString());
        Debug.Log($"[FreeV74Body] {Output}\n{r}");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    }

    private static Vector3 Snap(Vector3 point)
    {
        return NavMesh.SamplePosition(point, out NavMeshHit hit, 2f, NavMesh.AllAreas) ? hit.position : point;
    }
}

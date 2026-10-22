using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Assets.Scripts.Showcase;
using Unity.AI.Navigation;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Confere, na cena da vitrine aberta (fora do Play), se o NavMesh de cada arena bate com os objetos dela: foto de cima
/// com o NavMesh pintado e, por arena, a fração do NavMesh dentro de objeto, em cima de móvel e o chão livre sem
/// NavMesh. Lista também todos os agentes de cada arena. Saída em Temp/Vitrine/navmesh.txt e navmesh_NN_*.jpg.
/// </summary>
public static class ShowcaseNavMeshCheck
{
    private const string Folder = "Temp/Vitrine";

    // Folga do corpo no teste de "chão livre": o NavMesh já é erodido pelo raio do agente perto de parede e móvel.
    private const float BodyClearance = 1f;

    [MenuItem("PROJECT-IA/Vitrine: conferir NavMesh e agentes")]
    public static void Check()
    {
        ShowcaseDirector director = Object.FindAnyObjectByType<ShowcaseDirector>();
        if (Application.isPlaying || director == null)
        {
            Debug.LogError("[Vitrine] abra a cena da vitrine, fora do Play, antes de conferir.");
            return;
        }

        int wallLayer = LayerMask.NameToLayer("Wall");
        var report = new StringBuilder("Vitrine — NavMesh x objetos, por arena\n");
        IReadOnlyList<ShowcaseDirector.Entry> entries = director.Entries;

        // As arenas ficam salvas desligadas: liga uma por vez (o NavMesh dela só carrega ligada) e volta como estava.
        bool[] wasActive = entries.Select(e => e.arena != null && e.arena.gameObject.activeSelf).ToArray();
        Directory.CreateDirectory(Folder);
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ShowcaseDirector.Entry entry = entries[i];
                if (entry.arena == null)
                    continue;

                for (int j = 0; j < entries.Count; j++)
                    if (entries[j].arena != null)
                        entries[j].arena.gameObject.SetActive(j == i);

                Physics.SyncTransforms();
                NavMeshTriangulation triangulation = NavMesh.CalculateTriangulation();
                GameObject overlay = CreateOverlay(triangulation);
                try
                {
                    Bounds bounds = MeasureArena(entry.arena.gameObject, wallLayer);
                    report.AppendLine($"\n{i + 1,2}. {entry.version} ({entry.arena.name})");
                    DescribeArena(entry.arena, report);
                    Measure(triangulation, bounds, entry.arena, report);
                    Photograph(bounds, Path.Combine(Folder, $"navmesh_{i + 1:00}_{entry.version}.jpg"));
                }
                finally
                {
                    Object.DestroyImmediate(overlay.GetComponent<MeshFilter>().sharedMesh);
                    Object.DestroyImmediate(overlay.GetComponent<MeshRenderer>().sharedMaterial);
                    Object.DestroyImmediate(overlay);
                }
            }
        }
        finally
        {
            for (int j = 0; j < entries.Count; j++)
                if (entries[j].arena != null)
                    entries[j].arena.gameObject.SetActive(wasActive[j]);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(director.gameObject.scene);
        }

        File.WriteAllText(Path.Combine(Folder, "navmesh.txt"), report.ToString());
        Debug.Log($"[Vitrine] {Folder}/navmesh.txt e as fotos navmesh_*.jpg prontos.");
    }

    private static void DescribeArena(Transform arena, StringBuilder report)
    {
        foreach (NavMeshSurface surface in arena.GetComponentsInChildren<NavMeshSurface>(true))
        {
            string data = surface.navMeshData != null ? AssetDatabase.GetAssetPath(surface.navMeshData) : "SEM DADOS";
            report.AppendLine($"    NavMeshSurface '{surface.name}' ({(surface.isActiveAndEnabled ? "ligado" : "DESLIGADO")}): {data}");
        }

        foreach (Agent agent in arena.GetComponentsInChildren<Agent>(true))
        {
            BehaviorParameters behavior = agent.GetComponent<BehaviorParameters>();
            report.AppendLine($"    agente '{agent.name}' ({agent.GetType().Name}, {(agent.gameObject.activeInHierarchy ? "ativo" : "inativo")}): " +
                              $"{(behavior != null ? behavior.BehaviorName : "-")}, modelo " +
                              $"{(behavior != null && behavior.Model != null ? behavior.Model.name : "nenhum")}");
        }
    }

    private static void Measure(NavMeshTriangulation triangulation, Bounds bounds, Transform arena, StringBuilder report)
    {
        var own = new HashSet<Collider>(arena.GetComponentsInChildren<Collider>());
        var heights = new List<float>();
        var centroids = new List<(Vector3 point, float area)>();
        int[] indices = triangulation.indices;
        Vector3[] vertices = triangulation.vertices;
        for (int t = 0; t < indices.Length; t += 3)
        {
            Vector3 a = vertices[indices[t]], b = vertices[indices[t + 1]], c = vertices[indices[t + 2]];
            Vector3 centroid = (a + b + c) / 3f;
            if (centroid.x < bounds.min.x || centroid.x > bounds.max.x || centroid.z < bounds.min.z || centroid.z > bounds.max.z)
                continue;

            float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            centroids.Add((centroid, area));
            heights.Add(centroid.y);
        }

        if (centroids.Count == 0)
        {
            report.AppendLine("    NavMesh: NENHUM triângulo dentro da arena");
            return;
        }

        heights.Sort();
        float floor = heights[heights.Count / 2];
        float total = 0f, inside = 0f, elevated = 0f;
        foreach ((Vector3 point, float area) in centroids)
        {
            total += area;
            if (point.y - floor > 0.5f)
                elevated += area;
            else if (Physics.CheckCapsule(point + Vector3.up * 0.4f, point + Vector3.up * 1.4f, 0.15f, ~0, QueryTriggerInteraction.Ignore))
                inside += area;
        }

        // Chão livre (nada até 2 m, longe de parede e móvel) sem NavMesh a menos de 0.6 m.
        int free = 0, missing = 0;
        for (float x = bounds.min.x + 0.5f; x < bounds.max.x; x += 1f)
        {
            for (float z = bounds.min.z + 0.5f; z < bounds.max.z; z += 1f)
            {
                RaycastHit[] hits = Physics.RaycastAll(new Vector3(x, floor + 0.3f, z), Vector3.down, 1f, ~0, QueryTriggerInteraction.Ignore);
                if (!hits.Any(h => h.normal.y > 0.9f && own.Contains(h.collider)))
                    continue;

                Vector3 ground = new(x, floor, z);
                if (Physics.CheckCapsule(ground + Vector3.up * (BodyClearance + 0.15f), ground + Vector3.up * 2f, BodyClearance,
                        ~0, QueryTriggerInteraction.Ignore))
                    continue;

                free++;
                if (!NavMesh.SamplePosition(ground, out _, 0.6f, NavMesh.AllAreas))
                    missing++;
            }
        }

        report.AppendLine($"    NavMesh: {total:0} m², {100f * inside / total:0.0}% dentro de objeto, {100f * elevated / total:0.0}% acima do chão " +
                          $"(tampo de móvel), chão livre sem NavMesh {(free > 0 ? 100f * missing / free : 0f):0.0}% de {free} m²");
    }

    private static GameObject CreateOverlay(NavMeshTriangulation triangulation)
    {
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(triangulation.vertices.Select(v => v + Vector3.up * 0.08f).ToArray());
        mesh.SetTriangles(triangulation.indices, 0);
        mesh.RecalculateNormals();

        var overlay = new GameObject("NavMesh (conferência)") { hideFlags = HideFlags.HideAndDontSave };
        overlay.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = overlay.AddComponent<MeshRenderer>();
        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
        var material = new Material(unlit) { hideFlags = HideFlags.HideAndDontSave };
        material.SetColor("_BaseColor", new Color(0.1f, 0.9f, 0.6f));
        material.SetFloat("_Cull", 0f);
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return overlay;
    }

    private static void Photograph(Bounds bounds, string file)
    {
        var cameraObject = new GameObject("Câmera (conferência)") { hideFlags = HideFlags.HideAndDontSave };
        var camera = cameraObject.AddComponent<Camera>();
        var texture = new RenderTexture(1600, 1200, 24);
        var image = new Texture2D(1600, 1200, TextureFormat.RGB24, false);
        try
        {
            camera.orthographic = true;
            camera.aspect = 1600f / 1200f;
            camera.orthographicSize = Mathf.Max(bounds.extents.z, bounds.extents.x / camera.aspect) * 1.05f;
            camera.transform.SetPositionAndRotation(new Vector3(bounds.center.x, bounds.max.y + 50f, bounds.center.z),
                Quaternion.Euler(90f, 0f, 0f));
            camera.farClipPlane = 500f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            image.ReadPixels(new Rect(0, 0, 1600, 1200), 0, 0);
            image.Apply();
            File.WriteAllBytes(file, image.EncodeToJPG(85));
        }
        finally
        {
            RenderTexture.active = null;
            camera.targetTexture = null;
            Object.DestroyImmediate(texture);
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(cameraObject);
        }
    }

    private static Bounds MeasureArena(GameObject arena, int wallLayer)
    {
        Renderer[] renderers = arena.GetComponentsInChildren<Renderer>();
        Renderer[] walls = renderers.Where(r => r.gameObject.layer == wallLayer).ToArray();
        Renderer[] source = walls.Length > 0 ? walls : renderers;
        Bounds bounds = source[0].bounds;
        foreach (Renderer renderer in source)
            bounds.Encapsulate(renderer.bounds);
        return bounds;
    }
}

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Assets.Scripts.Free;
using Assets.Scripts.Graph;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

/// <summary>
/// v7.4 (17/10): números para acertar o AGACHAR do jogador contra a visão do monstro, medidos na "Cena Final - Mapa".
/// A visão do alvo mira nas bounds do colisor do jogador (GraphHiderPerception.CountVisibleSamples, grade 4x3 do corpo):
/// é a altura do CharacterController agachado, e não a da câmera, que decide se o móvel esconde. A câmera só precisa
/// ficar DENTRO dele (acima do colisor, o jogador espia por cima do móvel sem que o monstro o veja).
/// Grava Logs/v7.4_crouch.txt: chão, olho do monstro, jogador em pé/agachado (colisor e câmera, a câmera agachada pela
/// amostra do clipe Crouch-* no osso que o SpineLook segue), a presa de treino e as alturas dos móveis que tapam a visão.
/// A cena é aberta em modo aditivo e fechada sem salvar (a amostra do clipe mexe nos ossos).
/// </summary>
public static class FreeV74CrouchReport
{
    private const string GameScene = "Assets/Scenes/Cena Final - Mapa.unity";
    private const string ArenaRoot = "Mapa Final";
    private const string ArenaPrefabPath = "Assets/Prefabs/Arenas/V7.4/Arena - Cena Final.prefab";
    private const string Output = "Logs/v7.4_crouch.txt";
    private const string FollowBoneName = "spine.004";

    [MenuItem("PROJECT-IA/v7.4: relatório do agachar (jogador x olho do monstro)")]
    public static void Run()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV74Crouch] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetSceneByPath(GameScene);
        bool opened = false;
        if (scene.isLoaded)
        {
            if (scene.isDirty)
            {
                Debug.LogError($"[FreeV74Crouch] {GameScene} aberta com mudança não salva: salve antes (a medida mexe nos ossos).");
                return;
            }
        }
        else
        {
            scene = EditorSceneManager.OpenScene(GameScene, OpenSceneMode.Additive);
            opened = true;
        }

        var r = new StringBuilder();
        try
        {
            Measure(scene, r);
        }
        finally
        {
            if (opened)
                EditorSceneManager.CloseScene(scene, true);
            else
                EditorSceneManager.OpenScene(GameScene, OpenSceneMode.Single); // descarta a pose amostrada
        }

        Directory.CreateDirectory("Logs");
        File.WriteAllText(Output, r.ToString());
        Debug.Log($"[FreeV74Crouch] {Output}\n{r}");
    }

    private static void Measure(Scene scene, StringBuilder r)
    {
        GameObject arena = scene.GetRootGameObjects().FirstOrDefault(g => g.name == ArenaRoot);
        PlayerMovement player = arena != null ? arena.GetComponentInChildren<PlayerMovement>(true) : null;
        FreeExplorerManager seeker = arena != null ? arena.GetComponentInChildren<FreeExplorerManager>(true) : null;
        if (player == null || seeker == null)
        {
            r.AppendLine($"'{ArenaRoot}' sem jogador (PlayerMovement) ou monstro (FreeExplorerManager).");
            return;
        }

        Physics.SyncTransforms();
        float floor = Floor(player.transform.position);
        r.AppendLine($"chão sob o jogador: y = {floor:0.00} (alturas abaixo são acima do chão, em metros do mundo)");

        // Monstro: o olho sai da raiz dele + _eyeHeight. A raiz pode não estar no chão na cena.
        GraphHiderPerception perception = seeker.GetComponentInChildren<GraphHiderPerception>(true);
        float eyeHeight = perception != null ? new SerializedObject(perception).FindProperty("_eyeHeight").floatValue : 0f;
        float seekerFloor = Floor(seeker.transform.position);
        r.AppendLine($"monstro: olho a {seeker.transform.position.y + eyeHeight - seekerFloor:0.00} m do chão " +
                     $"(_eyeHeight {eyeHeight:0.00}, raiz a {seeker.transform.position.y - seekerFloor:0.00} do chão)");

        // Jogador: colisor em pé e agachado como o PlayerMovement aplica (Clamp(crouchHeight, 2 x raio, em pé)).
        var controller = player.GetComponent<CharacterController>();
        float scaleY = player.transform.lossyScale.y;
        float scaleXZ = Mathf.Max(player.transform.lossyScale.x, player.transform.lossyScale.z);
        var movement = new SerializedObject(player);
        float crouchField = movement.FindProperty("crouchHeight").floatValue;
        float crouchLocal = Mathf.Clamp(crouchField, controller.radius * 2f, controller.height);
        Vector3 centerWorld = player.transform.TransformPoint(controller.center);
        float bottom = centerWorld.y - controller.height * 0.5f * scaleY;
        float rootToBottom = player.transform.position.y - bottom;
        r.AppendLine($"jogador: escala {scaleY:0.##}, raio {controller.radius:0.00} local = {controller.radius * scaleXZ:0.00} m");
        r.AppendLine($"  colisor em pé: {controller.height:0.00} local = {controller.height * scaleY:0.00} m");
        r.AppendLine($"  colisor agachado: crouchHeight {crouchField:0.00} -> {crouchLocal:0.00} local = {crouchLocal * scaleY:0.00} m" +
                     (crouchLocal > crouchField + 1e-3f ? $"  (TRAVADO no mínimo 2 x raio = {controller.radius * 2f:0.00} local)" : ""));

        // Câmera: em pé pela posição salva; agachada pela amostra do clipe Crouch-* no osso que o SpineLook segue (a
        // câmera anda junto com ele) menos o crouchCameraDrop do CameraJuice.
        Camera camera = player.GetComponentInChildren<Camera>(true);
        Animator animator = player.GetComponentInChildren<Animator>(true);
        Transform bone = animator != null ? FindDeep(animator.transform, FollowBoneName) : null;
        CameraJuice juice = player.GetComponentInChildren<CameraJuice>(true);
        float cameraDrop = juice != null ? new SerializedObject(juice).FindProperty("crouchCameraDrop").floatValue : 0f;
        float cameraParentScale = camera != null && camera.transform.parent != null ? camera.transform.parent.lossyScale.y : 1f;
        if (camera == null || bone == null || animator.runtimeAnimatorController == null)
        {
            r.AppendLine("  câmera: sem câmera, osso spine.004 ou Animator no jogador");
        }
        else
        {
            AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
            AnimationClip stand = clips.FirstOrDefault(c => c.name.Contains("Idle") && !c.name.Contains("Crouch") && !c.name.Contains("Item"));
            AnimationClip crouch = clips.FirstOrDefault(c => c.name.Contains("Crouch") && c.name.Contains("Idle"))
                                   ?? clips.FirstOrDefault(c => c.name.Contains("Crouch"));
            float cameraOffset = camera.transform.position.y - bone.position.y; // pose salva = de pé
            float standBone = Sample(animator, stand, bone);
            float crouchBone = Sample(animator, crouch, bone);
            float standCamera = standBone + cameraOffset - bottom;
            float crouchCamera = crouchBone + cameraOffset - bottom - cameraDrop * cameraParentScale;
            r.AppendLine($"  câmera em pé ({(stand != null ? stand.name : "pose salva")}): {standCamera:0.00} m");
            r.AppendLine($"  câmera agachada ({(crouch != null ? crouch.name : "sem clipe Crouch")}): {crouchCamera:0.00} m " +
                         $"(clipe desce {standBone - crouchBone:0.00} m, crouchCameraDrop {cameraDrop:0.00} local x escala {cameraParentScale:0.##})");
            r.AppendLine($"  topo do colisor agachado - câmera agachada: {crouchLocal * scaleY - crouchCamera:+0.00;-0.00} m " +
                         "(negativo = a câmera fica ACIMA do corpo que o monstro enxerga)");
        }

        r.AppendLine($"  (raiz do jogador a {rootToBottom:0.00} m acima da base do colisor)");

        // A presa que o cérebro treina (prefab de treino): o mesmo critério de "escondido" tem de valer para o jogador.
        var arenaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        FreeHider hider = arenaPrefab != null ? arenaPrefab.GetComponentInChildren<FreeHider>(true) : null;
        CapsuleCollider hiderBody = hider != null ? hider.GetComponent<CapsuleCollider>() : null;
        if (hiderBody != null)
        {
            float fraction = new SerializedObject(hider).FindProperty("_crouchHeightFraction").floatValue;
            float hs = hider.transform.lossyScale.y;
            r.AppendLine($"presa de treino: em pé {hiderBody.height * hs:0.00} m, agachada {hiderBody.height * hs * fraction:0.00} m, " +
                         $"raio {hiderBody.radius * Mathf.Max(hider.transform.lossyScale.x, hider.transform.lossyScale.z):0.00} m");
        }

        // Móveis que tapam a visão (layers de parede da arena), pela altura do topo acima do chão.
        FreeArenaController arenaController = arena.GetComponent<FreeArenaController>();
        int mask = arenaController != null ? arenaController.WallLayer.value : LayerMask.GetMask("Wall", "Obstacle", "Door");
        var tops = new List<float>();
        foreach (Collider c in arena.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger || !c.enabled || !c.gameObject.activeInHierarchy || (mask & (1 << c.gameObject.layer)) == 0)
                continue;

            float top = c.bounds.max.y - floor;
            float size = Mathf.Max(c.bounds.size.x, c.bounds.size.z);
            if (top > 0.3f && top < 3.5f && size < 6f)
                tops.Add(top);
        }

        r.AppendLine($"móveis que tapam a visão ({tops.Count} colisores com topo entre 0.3 e 3.5 m), por faixa do topo:");
        foreach (var group in tops.GroupBy(t => Mathf.Floor(t / 0.25f) * 0.25f).OrderBy(g => g.Key))
            r.AppendLine($"  {group.Key:0.00}-{group.Key + 0.25f:0.00} m: {group.Count()}");
    }

    private static float Sample(Animator animator, AnimationClip clip, Transform bone)
    {
        if (clip != null)
            clip.SampleAnimation(animator.gameObject, 0f);
        return bone.position.y;
    }

    private static float Floor(Vector3 position)
    {
        return NavMesh.SamplePosition(position, out NavMeshHit hit, 6f, NavMesh.AllAreas) ? hit.position.y
            : Physics.Raycast(position + Vector3.up * 0.1f, Vector3.down, out RaycastHit ray, 10f, ~0, QueryTriggerInteraction.Ignore)
                ? ray.point.y : position.y;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        return root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
    }
}

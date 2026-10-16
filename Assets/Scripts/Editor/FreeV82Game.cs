using System.Collections.Generic;
using System.IO;
using System.Linq;
using Assets.Scripts.Free;
using Unity.InferenceEngine;
using Unity.MLAgents.Policies;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// v8.2 (15/10): o monstro do Map - V8 no JOGO de verdade. Monta a "Cena Final V8" juntando:
///   - a Arena - V8.2 em modo de jogo, com o .onnx do 10M do v8.2_mapa_05 (o mapa em que ele treinou);
///   - da PauseMenu (a cena de jogo mais completa da main, 15/10; a Cena FInal não tem pause nem tutorial): Canvas
///     (câmera do jogador, mira, stamina, game over, vitória, vinheta, pause, tutorial), GameManager (músicas, PostFX
///     de visto, pause, loading), EventSystem, Post-process Volume, poeira, HintSystem, KeySpawner e a chave do tutorial;
///   - o PlayerDummy DA MAIN, com os overrides que ele tem na PauseMenu (o whiteFade da vitória, o shake com dica), no
///     lugar do jogador do Map - V8 (o "PlayerDummy - V8.2" é uma cópia solta do jogador antigo: sem o modelo novo, sem
///     a lanterna nova, sem poeira e vapor). O jogador do Map - V8 fica desligado na cena;
///   - a iluminação da cena (névoa, ambiente, skybox) da PauseMenu.
/// O KeySpawner sorteia as chaves nos KeySpots do Map - V8 (os mesmos do treino), e não nos pontos do mapa do Leo.
/// PLACEHOLDERS (o mapa do Leo é outro, 180 x 92 m; o Map - V8 tem 116 x 102 m): as portas do início (tutorial e porta
/// final, com o gatilho que INICIA A PARTIDA, a chave do tutorial) entram na mesma posição em relação ao jogador que
/// têm na PauseMenu, e a saída (WinTrigger + FogWall) no KeySpot mais longe do jogador. Arraste os dois grupos para os
/// vãos do Map - V8. Sem passar pelo gatilho da porta final o GameManager fica em Preparing e o monstro NÃO SE MEXE
/// (GameManager ⋮ → "Iniciar partida" força no Play).
/// Também cria o fluxo duplicado: "MainMenu - V8" (cópia do MainMenu cujo Play carrega a Cena Final V8), as duas no
/// FIM do Build Settings (os índices do time não mudam).
///   "v8.2: jogo integrado (cena + menu)"     monta a cena SÓ se ela não existe (mantém o que se arrastou à mão) e
///                                             refaz o menu e o Build Settings;
///   "v8.2: jogo integrado — pôr o teto"       só PREENCHE o teto (grupo Ceiling) onde ainda não há placa, na arena
///                                             e sobre todo piso fora dela (Entrada, Saida), no topo das paredes de cada grupo;
///   "v8.2: jogo integrado — um jogador só"    fica o jogador da raiz, no lugar do outro (com Undo);
///   "v8.2: jogo integrado — portas (tutorial e gatilho)" a Tutorial Door que abre e o Trigger que COMEÇA A PARTIDA;
///   "v8.2: jogo integrado — garrafas"         espalha garrafas de arremesso (o barulho delas o monstro ouve);
///   "v8.2: jogo integrado — chave do tutorial" põe a Key3 da sala inicial na Entrada (onde ela fica na PauseMenu);
///   "v8.2: jogo integrado — ajustar layers"   portas → Door, batentes → Wall, móveis em Default → Obstacle, fora da
///                                             arena (a visão do monstro só respeita essas três);
///   "v8.2: jogo integrado — refazer a cena"   apaga os ajustes da cena e monta de novo.
/// TETO: o Map - V8 não tem teto (as placas CeilingTile dele são o chão); a cena do jogo ganha um, só ela.
/// </summary>
public static class FreeV82Game
{
    private const string SourceScene = "Assets/Scenes/PauseMenu.unity";
    private const string MenuScene = "Assets/Scenes/MainMenu.unity";
    private const string ArenaPrefabPath = "Assets/Prefabs/Arenas/V8.2/Arena - V8.2.prefab";
    private const string ModelPath = "Assets/Brains/FreeExplorer/FreeExplorer_v8.2_10M.onnx";
    private const string GameScene = "Assets/Scenes/Cena Final V8.unity";
    private const string GameMenuScene = "Assets/Scenes/MainMenu - V8.unity";
    private const string PlayerTag = "Goal";
    private const string CeilingTilePath = "Assets/Prefabs/CeilingTile.prefab";
    private const string FloorTilePath = "Assets/Prefabs/Import_Assets/horror-game-floor-generator-free/source/FloorTile.prefab";
    // O grupo do teto da cena (o mesmo nome do mapa do Leo, que o Arthur estendeu sobre a Entrada e a Saida). O
    // "Teto (só no jogo)" é o nome antigo deste menu: apagado ao rodar, era duplicata do Ceiling.
    private const string CeilingGroup = "Ceiling";
    private const string LegacyCeilingGroup = "Teto (só no jogo)";

    // Pé-direito sem parede para medir: o do mapa do Leo (piso em 0, placas do teto em 5.75 m).
    private const float FallbackCeilingHeight = 5.75f;

    private const string FinalScene = "Assets/Scenes/Cena FInal.unity";
    private const string TutorialKeyPath = "Assets/Prefabs/Key3.prefab";
    private const string TutorialKeyName = "Key3 (tutorial)";
    private const string EntranceName = "Entrada";
    private const string BottlePath = "Assets/Prefabs/Bottle.prefab";
    private const string BottleGroup = "Garrafas";

    // Quantas garrafas e o tamanho (a da Cena FInal é o prefab x3).
    private const int BottleCount = 12;
    private const float BottleScale = 3f;
    private const string TutorialDoorName = "Tutorial Door";
    private const string DoorLeafName = "Wooden Door_009.001";
    private const string DoorFrameName = "Wooden Doorframe_009.002";
    private const string StartTriggerName = "Gatilho da caça (porta trancada)";
    private const string BrightLightName = "Luz clara (ver o mapa)";

    // Raízes da PauseMenu que vêm inteiras. O Environment fica (é o mapa do Leo e o Seeker antigo).
    private static readonly string[] SystemRoots =
        { "Canvas", "GameManager", "EventSystem", "Post-process Volume", "DustParticleSystem", "HintSystem", "KeySpawner" };

    // Do Environment (e a chave do tutorial, que é raiz): o começo e o fim da partida, que dependem do mapa.
    private static readonly string[] StartPieces = { "Tutorial Door", "Final Door", "Key3" };
    private static readonly string[] ExitPieces = { "WinTrigger", "FogWall" };

    [MenuItem("PROJECT-IA/v8.2: jogo integrado (cena + menu)")]
    public static void Integrate() => Run(rebuild: false);

    // Só o teto, na cena do jogo aberta: não refaz a cena (as portas e a saída arrastadas à mão ficam).
    [MenuItem("PROJECT-IA/v8.2: jogo integrado — pôr o teto")]
    public static void AddCeilingToOpenScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        if (scene.path != GameScene)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            scene = EditorSceneManager.OpenScene(GameScene, OpenSceneMode.Single);
        }

        FreeArenaController arena = scene.GetRootGameObjects()
            .Select(g => g.GetComponentInChildren<FreeArenaController>(true)).FirstOrDefault(a => a != null);
        if (arena == null || !BuildCeiling(scene, arena.gameObject))
            return;

        EditorSceneManager.SaveScene(scene);
    }

    // ================================================================================
    // Layers das peças FORA da arena (Entrada, Saida, portas, móveis postos à mão)
    // ================================================================================

    // A visão do monstro, os raios do sensor e a escolha de esconderijo só respeitam Wall/Obstacle/Door: peça em
    // Default é transparente para ele (ele te vê através de porta fechada ou de armário). A ARENA não muda: as layers
    // dela são as do treino, as que o cérebro conhece. Regras, só em colisor sólido (trigger fica):
    //   - folha de porta (debaixo de um SwingDoor, ou "Wooden Door" solta) → Door. Fechada tapa a visão; aberta deixa
    //     ver pelo vão;
    //   - batente ("Doorframe") → Wall;
    //   - o resto em Default com 0.5 m ou mais de altura (armário, estante, FogWall) → Obstacle. Piso, teto e cadeado
    //     são finos e ficam.
    // O jogador e o que está debaixo dele ficam. As três layers colidem com o jogador (matriz de física), e a interação
    // continua: o raio dela testa todas as layers e só PAREDE (Wall) bloqueia.
    [MenuItem("PROJECT-IA/v8.2: jogo integrado — ajustar layers")]
    public static void AdjustLayersInOpenScene()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        GameObject arena = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponentInChildren<FreeArenaController>(true) != null);
        if (arena == null)
        {
            Debug.LogError($"[FreeV82Game] {scene.name} sem arena (FreeArenaController): abra a Cena Final V8.");
            return;
        }

        int wall = LayerMask.NameToLayer("Wall"), obstacle = LayerMask.NameToLayer("Obstacle"), door = LayerMask.NameToLayer("Door");
        if (wall < 0 || obstacle < 0 || door < 0)
        {
            Debug.LogError("[FreeV82Game] faltam as layers Wall, Obstacle ou Door no TagManager.");
            return;
        }

        var changes = new Dictionary<string, int>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root == arena)
                continue;

            foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
            {
                GameObject go = collider.gameObject;
                if (collider.isTrigger || go.GetComponentInParent<PlayerMovement>(true) != null)
                    continue;

                int target;
                if (go.GetComponentInParent<SwingDoor>(true) != null || (go.name.StartsWith("Wooden Door") && !go.name.Contains("frame")))
                    target = door;
                else if (go.name.Contains("Doorframe"))
                    target = wall;
                else if (go.layer == 0 && collider.bounds.size.y >= 0.5f)
                    target = obstacle;
                else
                    continue;

                if (go.layer == target)
                    continue;

                string key = $"{LayerMask.LayerToName(go.layer)} → {LayerMask.LayerToName(target)}";
                changes[key] = changes.TryGetValue(key, out int n) ? n + 1 : 1;
                go.layer = target;
                Record(go);
            }
        }

        WarnExtraPlayers(scene);
        if (changes.Count == 0)
        {
            Debug.Log("[FreeV82Game] layers: nada a mudar fora da arena.");
            return;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[FreeV82Game] layers fora da arena: {string.Join(", ", changes.Select(c => $"{c.Value}x {c.Key}"))}. Ctrl+S para gravar.");
    }

    // Dois jogadores ativos = duas câmeras na mesma RenderTexture, dois ouvidos, e o monstro caça o que a tag Goal
    // achar primeiro. Só avisa: qual fica é escolha de quem montou a cena.
    private static void WarnExtraPlayers(Scene scene)
    {
        List<GameObject> players = scene.GetRootGameObjects()
            .SelectMany(g => g.GetComponentsInChildren<PlayerMovement>(false))
            .Select(m => m.gameObject).Where(g => g.activeInHierarchy).Distinct().ToList();
        if (players.Count > 1)
            Debug.LogWarning($"[FreeV82Game] {players.Count} jogadores ATIVOS na cena " +
                             $"({string.Join(", ", players.Select(p => p.transform.parent != null ? $"{p.transform.parent.name}/{p.name}" : p.name))}): " +
                             "desligue os que sobram.", players[0]);
    }

    // ================================================================================
    // Um jogador só
    // ================================================================================

    // Dois jogadores ativos: duas câmeras na mesma RenderTexture, dois ouvidos, e o monstro caça o que a tag Goal achar
    // primeiro. Fica o da RAIZ (o que veio da PauseMenu, com o whiteFade da vitória ligado no Canvas; uma duplicata
    // perde essa ligação) e ele vai para o lugar do outro (o da Entrada é o ponto de nascer). Com Undo: Ctrl+Z desfaz.
    [MenuItem("PROJECT-IA/v8.2: jogo integrado — um jogador só")]
    public static void KeepOnePlayer()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        GameObject arena = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponentInChildren<FreeArenaController>(true) != null);
        List<Transform> players = scene.GetRootGameObjects().Where(g => g != arena)
            .Select(g => g.transform).SelectMany(PlayersUnder).Distinct().ToList();
        if (players.Count <= 1)
        {
            Debug.Log($"[FreeV82Game] {players.Count} jogador ativo na cena (fora da arena): nada a fazer.");
            return;
        }

        List<Transform> roots = players.Where(p => p.parent == null).ToList();
        if (roots.Count != 1 || players.Count != 2)
        {
            Debug.LogError($"[FreeV82Game] {players.Count} jogadores ativos ({string.Join(", ", players.Select(PathOf))}): " +
                           "esperava um na raiz e um no lugar de nascer. Desligue na mão os que sobram.");
            return;
        }

        Transform keeper = roots[0];
        Transform spawn = players.First(p => p != keeper);
        string removed = PathOf(spawn);
        Undo.RecordObject(keeper, "Um jogador só");
        keeper.SetPositionAndRotation(spawn.position, spawn.rotation);
        Record(keeper);
        Undo.DestroyObjectImmediate(spawn.gameObject);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = keeper.gameObject;
        Debug.Log($"[FreeV82Game] fica '{keeper.name}' (raiz, com a tela de vitória ligada), no lugar de '{removed}', que saiu. " +
                  "Ctrl+Z desfaz; Ctrl+S grava.", keeper);
    }

    // Jogadores ATIVOS: o objeto com a tag Goal que tem (ou contém) o PlayerMovement.
    private static IEnumerable<Transform> PlayersUnder(Transform root)
    {
        foreach (PlayerMovement movement in root.GetComponentsInChildren<PlayerMovement>(false))
        {
            Transform candidate = movement.transform;
            while (candidate != null && !candidate.CompareTag(PlayerTag))
                candidate = candidate.parent;
            if (candidate != null && candidate.gameObject.activeInHierarchy)
                yield return candidate;
        }
    }

    private static string PathOf(Transform t) => t.parent != null ? $"{t.parent.name}/{t.name}" : t.name;

    // ================================================================================
    // Portas: a do tutorial e o gatilho que começa a caça
    // ================================================================================

    // Na PauseMenu cada porta é: pai (Tutorial Door / Final Door) → modelo "Wooden Door" → "Hinge" (SwingDoor + Lockable +
    // cadeados) → folha. A Tutorial Door tem 1 cadeado (a chave do tutorial); a Final Door, 3 (as chaves do KeySpawner) e
    // o "Trigger" que chama GameManager.StartMainLoop e o sinal "porta_principal" do tutorial. SEM ESSE GATILHO A PARTIDA
    // NÃO COMEÇA e o monstro fica parado (ele só caça em Playing).
    // Na Cena Final V8 a Entrada trouxe a folha e o batente SOLTOS (sem o Hinge: não abre) e a porta trancada ficou sem o
    // pai Final Door (e sem o Trigger). Este botão:
    //   1. traz a Tutorial Door inteira, encaixada onde está a folha solta da Entrada, e DESLIGA a folha e o batente
    //      soltos (não apaga);
    //   2. traz o Trigger da Final Door para a mesma posição em relação à porta trancada (a do Lockable de 3 cadeados);
    //   3. reaponta o que apontava para objetos da PauseMenu (GameManager, HintSystem) para os de mesmo nome daqui.
    // Rodar de novo refaz o gatilho e mantém a porta do tutorial que já estiver na Entrada (apague-a para refazer).
    [MenuItem("PROJECT-IA/v8.2: jogo integrado — portas (tutorial e gatilho)")]
    public static void FixDoors()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        GameObject entrance = scene.GetRootGameObjects().FirstOrDefault(g => g.name == EntranceName);
        if (entrance == null || !File.Exists(SourceScene))
        {
            Debug.LogError(entrance == null
                ? $"[FreeV82Game] {scene.name} sem o grupo '{EntranceName}' na raiz: abra a Cena Final V8."
                : $"[FreeV82Game] falta {SourceScene}.");
            return;
        }

        // A folha solta da Entrada (filha direta; a de dentro de um Hinge é de porta que já funciona). Ligada ou não: este
        // botão a desliga depois de encaixar a porta, e ela continua sendo o molde se a porta precisar ser refeita.
        Transform looseLeaf = entrance.transform.Cast<Transform>().FirstOrDefault(t => t.name == DoorLeafName);
        Transform lockedDoor = LockedDoorOf(scene);

        foreach (GameObject old in scene.GetRootGameObjects().Where(g => g.name == StartTriggerName).ToList())
            Object.DestroyImmediate(old);
        // Porta do tutorial já posta (rodada anterior) fica: a folha solta que servia de molde está desligada.
        Transform existingDoor = entrance.transform.Cast<Transform>().FirstOrDefault(t => t.name == TutorialDoorName);

        Scene source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
        var report = new List<string>();
        try
        {
            GameObject environment = source.GetRootGameObjects().FirstOrDefault(g => g.name == "Environment");
            Transform tutorialDoor = environment != null ? environment.transform.Find(TutorialDoorName) : null;
            DoorTrigger trigger = source.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<DoorTrigger>(true)).FirstOrDefault();
            Transform sourceLocked = LockedDoorOf(source);

            // 1. Porta do tutorial no lugar da folha solta.
            Transform sourceLeaf = tutorialDoor != null
                ? tutorialDoor.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == DoorLeafName)
                : null;
            if (existingDoor != null)
                report.Add("porta do tutorial: já está na Entrada (mantida)");
            else if (looseLeaf == null)
                report.Add($"porta do tutorial: a Entrada não tem a folha solta '{DoorLeafName}' para servir de molde");
            else if (sourceLeaf == null)
                report.Add($"porta do tutorial: {SourceScene} sem a '{TutorialDoorName}' com a folha");
            else
            {
                Quaternion turn = looseLeaf.rotation * Quaternion.Inverse(sourceLeaf.rotation);
                Vector3 position = looseLeaf.position + turn * (tutorialDoor.position - sourceLeaf.position);
                Quaternion rotation = turn * tutorialDoor.rotation;
                MoveToScene(tutorialDoor.gameObject, scene);
                tutorialDoor.SetPositionAndRotation(position, rotation);
                tutorialDoor.SetParent(entrance.transform, true);
                Record(tutorialDoor);
                int remapped = RemapToScene(tutorialDoor.gameObject, source, scene);

                // Desliga as peças soltas que ela substitui (a folha e o batente filhos diretos da Entrada).
                var loose = entrance.transform.Cast<Transform>()
                    .Where(t => t != tutorialDoor && (t.name == DoorLeafName || t.name == DoorFrameName)).ToList();
                foreach (Transform piece in loose)
                {
                    piece.gameObject.SetActive(false);
                    Record(piece.gameObject);
                }

                report.Add($"porta do tutorial: no lugar da folha solta ({string.Join(", ", loose.Select(t => t.name))} desligados), " +
                           $"{remapped} referência(s) reapontada(s)");
            }

            // 2. Gatilho da caça, na mesma posição em relação à porta trancada.
            if (trigger == null || sourceLocked == null)
                report.Add($"gatilho: {SourceScene} sem o DoorTrigger ou sem a porta de 3 cadeados");
            else if (lockedDoor == null)
                report.Add("gatilho: esta cena não tem porta com 3 cadeados (Lockable) para servir de referência");
            else
            {
                Vector3 local = Quaternion.Inverse(sourceLocked.rotation) * (trigger.transform.position - sourceLocked.position);
                Quaternion localRotation = Quaternion.Inverse(sourceLocked.rotation) * trigger.transform.rotation;
                Vector3 size = trigger.transform.lossyScale;
                Transform moved = trigger.transform;
                MoveToScene(moved.gameObject, scene);
                moved.name = StartTriggerName;
                moved.SetPositionAndRotation(lockedDoor.position + lockedDoor.rotation * local, lockedDoor.rotation * localRotation);
                moved.localScale = size;
                int remapped = RemapToScene(moved.gameObject, source, scene);
                report.Add($"gatilho: '{StartTriggerName}' na frente da porta trancada '{lockedDoor.name}', {remapped} referência(s) reapontada(s)");
            }
        }
        finally
        {
            EditorSceneManager.CloseScene(source, true);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Debug.Log($"[FreeV82Game] portas — {string.Join("; ", report)}. Confira na Scene e dê Ctrl+S.");
    }

    // A porta trancada da saída: o PAI do Hinge que tem o Lockable de 3 cadeados (o modelo "Wooden Door"). Pelo pai e
    // não pela raiz do prefab: o Hinge foi ADICIONADO dentro da instância do modelo, e objeto adicionado não é "parte
    // do prefab" (GetNearestPrefabInstanceRoot devolve null; foi o que fez o gatilho falhar na 1ª versão).
    private static Transform LockedDoorOf(Scene scene)
    {
        Lockable lockable = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Lockable>(true))
            .FirstOrDefault(l => LockCount(l) >= 3);
        if (lockable == null)
            return null;
        return lockable.transform.parent != null ? lockable.transform.parent : lockable.transform;
    }

    private static int LockCount(Lockable lockable)
    {
        SerializedProperty locks = new SerializedObject(lockable).FindProperty("locks");
        return locks != null ? locks.arraySize : 0;
    }

    // Referência que aponta para um objeto da cena de ORIGEM vai para o de mesmo caminho (raiz pelo nome + caminho
    // dentro dela) e mesmo tipo na cena nova; sem par, fica vazia e entra no aviso. Tem de rodar com a origem ainda
    // aberta (fechada, a referência vira null e não dá mais para saber o que era).
    private static int RemapToScene(GameObject moved, Scene source, Scene target)
    {
        int count = 0;
        var missing = new List<string>();
        foreach (Component component in moved.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component is Transform)
                continue;

            var settings = new SerializedObject(component);
            SerializedProperty property = settings.GetIterator();
            bool changed = false;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference)
                    continue;

                Object value = property.objectReferenceValue;
                GameObject owner = value is Component referenced ? referenced.gameObject : value as GameObject;
                if (owner == null || owner.scene != source)
                    continue;

                Object counterpart = Counterpart(value, owner, target);
                if (counterpart == null)
                    missing.Add($"{component.GetType().Name}.{property.propertyPath} → {owner.name}");
                property.objectReferenceValue = counterpart;
                changed = true;
                count++;
            }

            if (changed)
                settings.ApplyModifiedPropertiesWithoutUndo();
        }

        if (missing.Count > 0)
            Debug.LogWarning($"[FreeV82Game] {moved.name}: sem par nesta cena (ficaram vazias): {string.Join(", ", missing)}", moved);
        return count;
    }

    private static Object Counterpart(Object value, GameObject owner, Scene target)
    {
        Transform root = owner.transform.root;
        string path = owner.transform == root ? "" : AnimationUtility.CalculateTransformPath(owner.transform, root);
        GameObject targetRoot = target.GetRootGameObjects().FirstOrDefault(g => g.name == root.name);
        Transform found = targetRoot == null ? null : path.Length == 0 ? targetRoot.transform : targetRoot.transform.Find(path);
        if (found == null)
            return null;

        return value is GameObject ? (Object)found.gameObject : found.GetComponent(value.GetType());
    }

    // ================================================================================
    // Garrafas (arremesso que o monstro OUVE)
    // ================================================================================

    // A garrafa (Bottle: ThrowableItem) é pega com Interagir e jogada pelo PlayerThrow do jogador; ao bater toca o som e
    // chama NoiseEvents.Emit (25 m pelo caminho; quebrar = 25, batida fraca = metade). O FreeTrackSense do monstro escuta
    // esse evento como escuta os passos e a chave do treino: vira pista, Alerta, e ele vai conferir (ignora só vendo o
    // jogador). A Cena Final V8 não tinha nenhuma. Espalha BottleCount pelos KeySpots do Map - V8 (superfícies de apoiar
    // objeto, as mesmas das chaves), cada uma no KeySpot mais longe das já postas, e apoia na superfície de baixo.
    // Rodar de novo troca o grupo "Garrafas".
    [MenuItem("PROJECT-IA/v8.2: jogo integrado — garrafas")]
    public static void PlaceBottles()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        GameObject arena = scene.GetRootGameObjects().FirstOrDefault(g => g.GetComponentInChildren<FreeArenaController>(true) != null);
        var bottlePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BottlePath);
        Transform[] spots = arena != null ? FindKeySpots(arena.transform) : new Transform[0];
        if (arena == null || bottlePrefab == null || spots.Length == 0)
        {
            Debug.LogError(arena == null ? $"[FreeV82Game] {scene.name} sem arena: abra a Cena Final V8."
                : bottlePrefab == null ? $"[FreeV82Game] falta {BottlePath}." : "[FreeV82Game] o Map - V8 sem KeySpots.");
            return;
        }

        foreach (GameObject old in scene.GetRootGameObjects().Where(g => g.name == BottleGroup).ToList())
            Object.DestroyImmediate(old);

        // Pontos bem espalhados: começa num ao acaso e cada próximo é o mais longe de todos os já escolhidos.
        var chosen = new List<Vector3> { spots[UnityEngine.Random.Range(0, spots.Length)].position };
        while (chosen.Count < Mathf.Min(BottleCount, spots.Length))
        {
            Vector3 next = spots.Select(t => t.position)
                .OrderByDescending(p => chosen.Min(c => (p - c).sqrMagnitude)).First();
            chosen.Add(next);
        }

        var group = new GameObject(BottleGroup);
        SceneManager.MoveGameObjectToScene(group, scene);
        Physics.SyncTransforms();
        foreach (Vector3 spot in chosen)
        {
            Vector3 position = spot;
            if (Physics.Raycast(spot + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
                position = hit.point + Vector3.up * 0.05f;

            var bottle = (GameObject)PrefabUtility.InstantiatePrefab(bottlePrefab, scene);
            bottle.transform.SetParent(group.transform, false);
            bottle.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            bottle.transform.localScale = Vector3.one * BottleScale;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = group;
        Debug.Log($"[FreeV82Game] {chosen.Count} garrafa(s) espalhadas pelos KeySpots no grupo '{BottleGroup}'. " +
                  "Confira na Scene (mova à vontade) e dê Ctrl+S.", group);
    }

    // ================================================================================
    // Chave do tutorial
    // ================================================================================

    // A chave da sala inicial (a "Key3" solta na PauseMenu; as 3 do KeySpawner são outras, para os cadeados da saída).
    // A Entrada é a sala inicial do mapa do Leo trazida para cá: a chave vai para o mesmo lugar dela, pelo deslocamento
    // MEDIANO entre os objetos de mesmo nome nas duas cenas (resiste a uma peça mexida à mão), e desce até a superfície
    // de baixo se o móvel em que ela estava não veio. O prefab já traz o HintSignalEmitter ("chave_coletada"): o
    // tutorial anda sozinho. Rodar de novo troca a chave anterior.
    [MenuItem("PROJECT-IA/v8.2: jogo integrado — chave do tutorial")]
    public static void PlaceTutorialKey()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        Scene scene = SceneManager.GetActiveScene();
        GameObject entrance = scene.GetRootGameObjects().FirstOrDefault(g => g.name == EntranceName);
        var keyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TutorialKeyPath);
        if (entrance == null || keyPrefab == null || !File.Exists(SourceScene))
        {
            Debug.LogError(entrance == null
                ? $"[FreeV82Game] {scene.name} sem o grupo '{EntranceName}' na raiz: abra a Cena Final V8."
                : $"[FreeV82Game] falta {(keyPrefab == null ? TutorialKeyPath : SourceScene)}.");
            return;
        }

        // Lê da PauseMenu e fecha antes de mexer: os colisores dela atrapalhariam o raio que apoia a chave.
        Scene source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
        Vector3 sourcePosition;
        Quaternion sourceRotation;
        Vector3 offset;
        int matches;
        try
        {
            GameObject sourceKey = source.GetRootGameObjects().FirstOrDefault(g => g.name == "Key3");
            if (sourceKey == null)
            {
                Debug.LogError($"[FreeV82Game] {SourceScene} sem a 'Key3' na raiz.");
                return;
            }

            sourcePosition = sourceKey.transform.position;
            sourceRotation = sourceKey.transform.rotation;

            // Só nomes que aparecem UMA vez na PauseMenu ("CeilingTile (812)", "CabinetShort (3)"): par sem ambiguidade.
            Dictionary<string, Transform> byName = source.GetRootGameObjects()
                .SelectMany(g => g.GetComponentsInChildren<Transform>(true))
                .GroupBy(t => t.name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
            List<Vector3> deltas = entrance.GetComponentsInChildren<Transform>(true)
                .Where(t => t != entrance.transform && byName.ContainsKey(t.name) && !IsStructural(t.name))
                .Select(t => t.position - byName[t.name].position).ToList();
            matches = deltas.Count;
            if (matches == 0)
            {
                Debug.LogError($"[FreeV82Game] nada na '{EntranceName}' com o nome de um objeto da PauseMenu: não sei onde fica a sala inicial.");
                return;
            }

            offset = new Vector3(Median(deltas.Select(d => d.x)), Median(deltas.Select(d => d.y)), Median(deltas.Select(d => d.z)));
        }
        finally
        {
            EditorSceneManager.CloseScene(source, true);
        }

        foreach (Transform old in entrance.GetComponentsInChildren<Transform>(true).Where(t => t.name == TutorialKeyName).ToList())
            Object.DestroyImmediate(old.gameObject);

        // Apoia: com superfície a até 0.25 m abaixo fica onde está; senão desce até a primeira de baixo (o chão, se o
        // móvel não veio).
        Vector3 position = sourcePosition + offset;
        Physics.SyncTransforms();
        if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 10f, ~0, QueryTriggerInteraction.Ignore)
            && hit.point.y < position.y - 0.25f)
            position.y = hit.point.y + 0.03f;

        var key = (GameObject)PrefabUtility.InstantiatePrefab(keyPrefab, scene);
        key.name = TutorialKeyName;
        key.transform.SetPositionAndRotation(position, sourceRotation);
        key.transform.SetParent(entrance.transform, true);
        EditorSceneManager.MarkSceneDirty(scene);
        Selection.activeGameObject = key;
        Debug.Log($"[FreeV82Game] {TutorialKeyName} na {EntranceName} em {position} (deslocamento {offset} pela mediana de " +
                  $"{matches} objeto(s) iguais aos da PauseMenu). Confira na Scene e dê Ctrl+S.", key);
    }

    // Placa de piso/teto e parede: o nome ("CeilingTile (812)") se repete entre as cenas em peças que NÃO são a mesma
    // (a Entrada foi remontada), e jogou a chave a 50 m da sala. Só móveis e objetos soltos servem de par.
    private static bool IsStructural(string name) =>
        name.Contains("Tile") || name.Contains("Wal") || name.Contains("Door_Hole");

    private static float Median(IEnumerable<float> values)
    {
        List<float> sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    // ================================================================================
    // Luz: os dois botões agem na cena ABERTA (qualquer uma) e só a marcam como modificada (Ctrl+S para gravar)
    // ================================================================================

    // A atmosfera da Cena FInal: ambiente cinza-escuro chapado, névoa exponencial densa quase preta, sem sol nem
    // reflexo. Lida do arquivo da Cena FInal a cada clique (pega o que o time mudar lá). As 7 lâmpadas dela ficam: estão
    // nas coordenadas do mapa do Leo. Desliga a luz clara, se houver.
    [MenuItem("PROJECT-IA/Luz: a da Cena FInal")]
    public static void FinalLighting()
    {
        if (!CanEditLighting() || !File.Exists(FinalScene))
        {
            if (!File.Exists(FinalScene))
                Debug.LogError($"[FreeV82Game] falta {FinalScene}.");
            return;
        }

        Scene target = SceneManager.GetActiveScene();
        if (target.path == FinalScene)
        {
            Debug.Log("[FreeV82Game] esta já é a Cena FInal.");
            return;
        }

        Scene final = EditorSceneManager.OpenScene(FinalScene, OpenSceneMode.Additive);
        SceneManager.SetActiveScene(final);
        RenderCopy lighting = RenderCopy.FromActive();
        SceneManager.SetActiveScene(target);
        EditorSceneManager.CloseScene(final, true);

        lighting.Apply();
        SetBrightLight(target, false);
        EditorSceneManager.MarkSceneDirty(target);
        Debug.Log($"[FreeV82Game] {target.name}: luz da Cena FInal (névoa {RenderSettings.fogDensity:0.000}, ambiente " +
                  $"{RenderSettings.ambientSkyColor.r:0.000}). Ctrl+S para gravar.");
    }

    // Para ENXERGAR o mapa (posicionar portas, conferir o teto, ver o monstro andar): ambiente claro chapado, sem
    // névoa, e uma luz direcional branca. A luz fica na cena, desligada pelo botão da Cena FInal.
    [MenuItem("PROJECT-IA/Luz: clara (ver o mapa)")]
    public static void BrightLighting()
    {
        if (!CanEditLighting())
            return;

        Scene target = SceneManager.GetActiveScene();
        RenderSettings.fog = false;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientSkyColor = new Color(0.7f, 0.7f, 0.7f);
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.reflectionIntensity = 1f;
        SetBrightLight(target, true);
        EditorSceneManager.MarkSceneDirty(target);
        Debug.Log($"[FreeV82Game] {target.name}: luz clara. \"Luz: a da Cena FInal\" volta ao escuro. Ctrl+S para gravar.");
    }

    private static bool CanEditLighting()
    {
        if (!Application.isPlaying)
            return true;

        Debug.LogError("[FreeV82Game] saia do Play antes (luz mudada no Play não fica).");
        return false;
    }

    // A direcional da luz clara: criada na 1ª vez, depois só liga e desliga.
    private static void SetBrightLight(Scene scene, bool on)
    {
        GameObject existing = scene.GetRootGameObjects().FirstOrDefault(g => g.name == BrightLightName);
        if (existing == null)
        {
            if (!on)
                return;

            existing = new GameObject(BrightLightName, typeof(Light));
            SceneManager.MoveGameObjectToScene(existing, scene);
            existing.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light light = existing.GetComponent<Light>();
            light.type = LightType.Directional;
            light.color = Color.white;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
        }

        existing.SetActive(on);
    }

    [MenuItem("PROJECT-IA/v8.2: jogo integrado — refazer a cena")]
    public static void Rebuild()
    {
        if (File.Exists(GameScene) && !EditorUtility.DisplayDialog("Refazer a Cena Final V8",
                "A cena é montada de novo e o que foi arrastado à mão nela (portas, saída, luzes) se perde.", "Refazer", "Cancelar"))
            return;

        Run(rebuild: true);
    }

    private static void Run(bool rebuild)
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[FreeV82Game] saia do Play antes.");
            return;
        }

        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        bool ok = (!rebuild && File.Exists(GameScene)) || BuildGameScene();
        if (ok)
            ok = SetUpMenuFlow();

        if (ok)
            EditorSceneManager.OpenScene(GameScene, OpenSceneMode.Single);
        Debug.Log(ok ? "[FreeV82Game] PRONTO: abra a MainMenu - V8 e aperte Play (ou Play direto na Cena Final V8)."
                     : "[FreeV82Game] FALHOU (veja acima).");
    }

    // ================================================================================
    // A cena do jogo
    // ================================================================================

    private static bool BuildGameScene()
    {
        var model = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelPath);
        var arenaPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ArenaPrefabPath);
        if (model == null || arenaPrefab == null || !File.Exists(SourceScene))
        {
            Debug.LogError($"[FreeV82Game] falta {(model == null ? ModelPath : arenaPrefab == null ? ArenaPrefabPath : SourceScene)}.");
            return false;
        }

        Scene source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Single);
        RenderCopy lighting = RenderCopy.FromActive();
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        lighting.Apply();

        try
        {
            if (!Assemble(source, scene, arenaPrefab, model))
                return false;
        }
        finally
        {
            // A PauseMenu foi desmontada só na memória: fecha sem salvar.
            EditorSceneManager.CloseScene(source, true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(GameScene));
        if (!EditorSceneManager.SaveScene(scene, GameScene))
        {
            Debug.LogError($"[FreeV82Game] não consegui salvar {GameScene}.");
            return false;
        }

        Debug.Log($"[FreeV82Game] {GameScene} montada.");
        return true;
    }

    private static bool Assemble(Scene source, Scene scene, GameObject arenaPrefab, ModelAsset model)
    {
        GameObject[] roots = source.GetRootGameObjects();
        GameObject environment = roots.FirstOrDefault(g => g.name == "Environment");
        Transform sourcePlayer = environment != null ? FindPlayer(environment.transform) : null;
        if (sourcePlayer == null)
        {
            Debug.LogError($"[FreeV82Game] {SourceScene} sem Environment com o PlayerDummy (tag {PlayerTag}).");
            return false;
        }

        // Arena em modo de jogo. O jogador dela só dá a posição de nascer e fica desligado.
        var arenaObject = (GameObject)PrefabUtility.InstantiatePrefab(arenaPrefab, scene);
        arenaObject.name = "Arena - V8.2 (jogo)";
        Transform arenaPlayer = FindPlayer(arenaObject.transform);
        if (arenaPlayer == null || !ConfigureArena(arenaObject, model))
            return false;

        Vector3 spawn = arenaPlayer.position;
        Quaternion spawnRotation = arenaPlayer.rotation;
        arenaPlayer.gameObject.SetActive(false);
        Record(arenaPlayer.gameObject);

        // Tudo que for movido mantém a posição em relação ao jogador da PauseMenu (o começo da partida).
        Vector3 offset = spawn - sourcePlayer.position;

        foreach (string name in SystemRoots)
        {
            GameObject root = roots.FirstOrDefault(g => g.name == name);
            if (root == null)
                Debug.LogWarning($"[FreeV82Game] {SourceScene} sem '{name}' na raiz: a cena sai sem ele.");
            else
                MoveToScene(root, scene);
        }

        MoveToScene(sourcePlayer.gameObject, scene);
        sourcePlayer.SetPositionAndRotation(spawn, spawnRotation);
        Record(sourcePlayer);

        GameObject start = Group(scene, "Início do jogo (PLACEHOLDER - mova as portas para os vãos do mapa)", spawn);
        foreach (string name in StartPieces)
            MovePiece(Find(roots, environment, name), scene, start.transform, offset);

        // Saída no KeySpot mais longe do jogador (o canto oposto do mapa), na altura relativa ao jogador.
        Transform[] keySpots = FindKeySpots(arenaObject.transform);
        Transform winTrigger = Find(roots, environment, "WinTrigger");
        Vector3 exitOffset = offset;
        if (winTrigger != null && keySpots.Length > 0)
        {
            Vector3 far = keySpots.OrderByDescending(p => (p.position - spawn).sqrMagnitude).First().position;
            exitOffset = new Vector3(far.x - winTrigger.position.x, offset.y, far.z - winTrigger.position.z);
        }

        GameObject exit = Group(scene, "Saída (PLACEHOLDER - mova para a saída do mapa)", winTrigger != null ? winTrigger.position + exitOffset : spawn);
        foreach (string name in ExitPieces)
            MovePiece(Find(roots, environment, name), scene, exit.transform, exitOffset);

        PointKeySpawner(scene, keySpots);
        KeepOnlyPlayerCamera(scene, sourcePlayer);
        BuildCeiling(scene, arenaObject);
        return true;
    }

    // TETO: o Map - V8 não tem (as 2414 placas CeilingTile dele são o CHÃO, renomeadas FloorTile; o treino nunca
    // precisou de teto). Fica na CENA, fora da arena: o prefab de treino, o NavMesh e o build continuam sem teto (o
    // CeilingTile é layer Default, que nenhuma visão nem raio do monstro testa). Cobre:
    //   - a ARENA: uma placa em cima de cada placa do chão, no topo das paredes dela (mediana: uma peça escalada fora
    //     do padrão não levanta o teto do mapa todo);
    //   - todo PISO fora dela (Entrada, Saida, o que mais se montar à mão): FloorTile, e CeilingTile na altura do piso
    //     (o vão de porta), preenchido com placas do tamanho da CeilingTile no topo das paredes DO MESMO GRUPO.
    // Só PREENCHE: onde já há placa de teto (posta à mão, ou de uma rodada anterior) não põe outra (duas no mesmo lugar
    // piscam: z-fighting). Grupo de piso sem parede fica de fora (não há como saber a altura).
    private static bool BuildCeiling(Scene scene, GameObject arenaObject)
    {
        var tilePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CeilingTilePath);
        var floorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FloorTilePath);
        if (tilePrefab == null)
        {
            Debug.LogError($"[FreeV82Game] falta {CeilingTilePath}: a cena fica sem teto.");
            return false;
        }

        List<Transform> floor = InstancesOf(arenaObject.transform, tilePrefab).ToList();
        if (floor.Count == 0)
        {
            Debug.LogError("[FreeV82Game] nenhuma placa de chão (CeilingTile) na arena: a cena fica sem teto.");
            return false;
        }

        float floorY = floor.Average(t => t.position.y);
        float top = WallTop(arenaObject.transform, out _) ?? floorY + FallbackCeilingHeight;
        float rise = top - floorY;

        int legacy = 0;
        foreach (GameObject old in scene.GetRootGameObjects().Where(g => g.name == LegacyCeilingGroup).ToList())
        {
            legacy += old.transform.childCount;
            Object.DestroyImmediate(old);
        }

        // Tamanho da placa no plano (o passo do preenchimento fora da arena) e o índice do que já tem teto.
        Bounds tileBounds = RendererBounds(floor[0]) ?? new Bounds(floor[0].position, new Vector3(2f, 0f, 2f));
        float step = Mathf.Max(0.5f, Mathf.Min(tileBounds.size.x, tileBounds.size.z));
        var covered = new TileIndex(step);
        foreach (GameObject root in scene.GetRootGameObjects().Where(g => g != arenaObject))
            foreach (Transform t in InstancesOf(root.transform, tilePrefab))
                if (t.position.y > floorY + rise * 0.5f)
                    covered.Add(t.position);

        GameObject group = scene.GetRootGameObjects().FirstOrDefault(g => g.name == CeilingGroup);
        StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(floor[0].gameObject);
        Quaternion rotation = floor[0].rotation;
        Vector3 scale = floor[0].lossyScale;

        bool Place(Vector3 position)
        {
            if (covered.Covers(position))
                return false;

            if (group == null)
            {
                group = new GameObject(CeilingGroup);
                SceneManager.MoveGameObjectToScene(group, scene);
                GameObjectUtility.SetStaticEditorFlags(group, flags);
            }

            var ceiling = (GameObject)PrefabUtility.InstantiatePrefab(tilePrefab, scene);
            ceiling.transform.SetParent(group.transform, false);
            ceiling.transform.SetPositionAndRotation(position, rotation);
            ceiling.transform.localScale = scale;
            GameObjectUtility.SetStaticEditorFlags(ceiling, flags);
            covered.Add(position);
            return true;
        }

        int arenaAdded = floor.Count(tile => Place(tile.position + Vector3.up * rise));

        // Fora da arena, grupo por grupo (cada um com o próprio pé-direito).
        var outside = new List<string>();
        foreach (GameObject root in scene.GetRootGameObjects().ToList())
        {
            if (root == arenaObject || root == group || root.name == CeilingGroup)
                continue;

            float? groupTop = WallTop(root.transform, out float wallBase);
            if (groupTop == null)
                continue;

            List<Transform> pieces = InstancesOf(root.transform, tilePrefab).Where(t => t.position.y < wallBase + 0.5f)
                .Concat(floorPrefab != null ? InstancesOf(root.transform, floorPrefab) : Enumerable.Empty<Transform>())
                .ToList();
            int added = 0;
            foreach (Transform piece in pieces)
            {
                Bounds? bounds = RendererBounds(piece);
                if (bounds == null)
                    continue;

                Bounds b = bounds.Value;
                for (float x = b.min.x + step * 0.5f; x < b.max.x - 0.01f; x += step)
                    for (float z = b.min.z + step * 0.5f; z < b.max.z - 0.01f; z += step)
                        if (Place(new Vector3(x, groupTop.Value, z)))
                            added++;
            }

            if (pieces.Count > 0)
                outside.Add($"{root.name}: {added} nova(s) a y {groupTop.Value:0.00} sobre {pieces.Count} peça(s) de piso");
        }

        Debug.Log($"[FreeV82Game] teto da arena: {arenaAdded} placa(s) nova(s) a {rise:0.00} m do chão, {floor.Count - arenaAdded} já coberta(s)" +
                  (legacy > 0 ? $"; apagado o grupo antigo \"{LegacyCeilingGroup}\" ({legacy} placas duplicadas)." : ".") +
                  (outside.Count > 0 ? $"\nFora da arena — {string.Join("; ", outside)}." : ""));
        return true;
    }

    // Raízes de instância do prefab (aninhadas também) debaixo de root.
    private static IEnumerable<Transform> InstancesOf(Transform root, GameObject prefab) =>
        root.GetComponentsInChildren<Transform>(true)
            .Where(t => PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject) == t.gameObject
                        && PrefabUtility.GetCorrespondingObjectFromOriginalSource(t.gameObject) == prefab);

    // Topo das paredes debaixo de root (renderers com "Wal" no nome: Wall_0x, Wal_Corner, Door_Hole), pela mediana, e a
    // base delas (o nível do piso). Null sem parede.
    private static float? WallTop(Transform root, out float wallBase)
    {
        Renderer[] walls = root.GetComponentsInChildren<Renderer>(true)
            .Where(r => r.name.Contains("Wal") && !r.name.Contains("Tile")).ToArray();
        wallBase = walls.Length > 0 ? walls.Min(r => r.bounds.min.y) : 0f;
        return walls.Length > 0 ? walls.Select(r => r.bounds.max.y).OrderBy(y => y).ElementAt(walls.Length / 2) : (float?)null;
    }

    private static Bounds? RendererBounds(Transform piece)
    {
        Renderer[] renderers = piece.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
            return null;

        Bounds bounds = renderers[0].bounds;
        foreach (Renderer renderer in renderers.Skip(1))
            bounds.Encapsulate(renderer.bounds);
        return bounds;
    }

    // Placas de teto no plano: "coberto" = há uma placa a menos de meia placa (em X/Z) do ponto. Por célula do tamanho
    // da placa, olhando as 9 em volta: as grades da arena e as postas à mão não precisam estar alinhadas.
    private sealed class TileIndex
    {
        private readonly float _step;
        private readonly Dictionary<Vector2Int, List<Vector2>> _cells = new Dictionary<Vector2Int, List<Vector2>>();

        public TileIndex(float step) => _step = step;

        public void Add(Vector3 position)
        {
            Vector2Int cell = Cell(position);
            if (!_cells.TryGetValue(cell, out List<Vector2> list))
                _cells[cell] = list = new List<Vector2>();
            list.Add(new Vector2(position.x, position.z));
        }

        public bool Covers(Vector3 position)
        {
            var point = new Vector2(position.x, position.z);
            Vector2Int cell = Cell(position);
            float limit = _step * 0.5f * (_step * 0.5f);
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (_cells.TryGetValue(new Vector2Int(cell.x + dx, cell.y + dz), out List<Vector2> list)
                        && list.Any(p => (p - point).sqrMagnitude < limit))
                        return true;
            return false;
        }

        private Vector2Int Cell(Vector3 position) =>
            new Vector2Int(Mathf.FloorToInt(position.x / _step), Mathf.FloorToInt(position.z / _step));
    }

    // Os mesmos ajustes da cena de teste (FreeV81FurnishedTest.ConfigureAndSave): modo de jogo, sensor das chaves,
    // o .onnx em inferência, presa de treino desligada.
    private static bool ConfigureArena(GameObject arenaObject, ModelAsset model)
    {
        FreeArenaController arena = arenaObject.GetComponentInChildren<FreeArenaController>(true);
        FreeExplorerManager agent = arenaObject.GetComponentInChildren<FreeExplorerManager>(true);
        if (arena == null || agent == null)
        {
            Debug.LogError($"[FreeV82Game] {ArenaPrefabPath} sem FreeArenaController ou monstro (FreeExplorerManager).");
            return false;
        }

        var arenaSettings = new SerializedObject(arena);
        arenaSettings.FindProperty("_gameMode").boolValue = true;
        arenaSettings.ApplyModifiedPropertiesWithoutUndo();

        var agentSettings = new SerializedObject(agent);
        agentSettings.FindProperty("_keySensor").boolValue = true;
        agentSettings.ApplyModifiedPropertiesWithoutUndo();

        var behavior = new SerializedObject(agent.GetComponent<BehaviorParameters>());
        behavior.FindProperty("m_Model").objectReferenceValue = model;
        behavior.FindProperty("m_BehaviorType").intValue = (int)BehaviorType.InferenceOnly;
        behavior.ApplyModifiedPropertiesWithoutUndo();
        agent.gameObject.SetActive(true);
        Record(agent.gameObject);

        foreach (FreeHider hider in arenaObject.GetComponentsInChildren<FreeHider>(true))
        {
            hider.gameObject.SetActive(false);
            Record(hider.gameObject);
        }

        return true;
    }

    // Os pontos do mapa do Leo saem; as chaves nascem nos KeySpots do Map - V8, os mesmos em que a presa do treino as busca.
    private static void PointKeySpawner(Scene scene, Transform[] keySpots)
    {
        KeySpawner spawner = scene.GetRootGameObjects().Select(g => g.GetComponentInChildren<KeySpawner>(true)).FirstOrDefault(s => s != null);
        if (spawner == null)
            return;

        if (keySpots.Length == 0)
        {
            Debug.LogWarning("[FreeV82Game] o Map - V8 sem KeySpots: o KeySpawner fica com os pontos do mapa do Leo.");
            return;
        }

        foreach (Transform child in spawner.transform.Cast<Transform>().ToList())
            Object.DestroyImmediate(child.gameObject);

        var settings = new SerializedObject(spawner);
        SerializedProperty points = settings.FindProperty("spawnPoints");
        points.arraySize = keySpots.Length;
        for (int i = 0; i < keySpots.Length; i++)
            points.GetArrayElementAtIndex(i).objectReferenceValue = keySpots[i];
        settings.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[FreeV82Game] KeySpawner com os {keySpots.Length} KeySpots do Map - V8.");
    }

    // A câmera do jogador desenha na RenderTexture que o CameraView do Canvas mostra; qualquer outra câmera ou ouvido
    // (a arena traz os de teste) brigaria com ela.
    private static void KeepOnlyPlayerCamera(Scene scene, Transform player)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
            {
                if (camera.transform.IsChildOf(player))
                    continue;
                camera.enabled = false;
                Record(camera);
            }

            foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
            {
                if (listener.transform.IsChildOf(player))
                    continue;
                listener.enabled = false;
                Record(listener);
            }
        }
    }

    private static Transform Find(GameObject[] roots, GameObject environment, string name)
    {
        GameObject root = roots.FirstOrDefault(g => g != null && g.name == name);
        if (root != null)
            return root.transform;

        Transform child = environment.transform.Find(name);
        if (child == null)
            Debug.LogWarning($"[FreeV82Game] {SourceScene} sem '{name}': a cena sai sem ele.");
        return child;
    }

    // Raiz da cena nova (a cena tem de ser a mesma antes do SetParent).
    private static void MoveToScene(GameObject go, Scene scene)
    {
        go.transform.SetParent(null, true);
        SceneManager.MoveGameObjectToScene(go, scene);
    }

    private static void MovePiece(Transform piece, Scene scene, Transform group, Vector3 offset)
    {
        if (piece == null)
            return;

        MoveToScene(piece.gameObject, scene);
        piece.position += offset;
        piece.SetParent(group, true);
        Record(piece);
    }

    // Mudança por código numa instância de prefab só vira override salvo se for registrada; sem isto ela volta ao
    // valor do prefab ao salvar e reabrir a cena.
    private static void Record(Object target)
    {
        if (PrefabUtility.IsPartOfPrefabInstance(target))
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
    }

    private static GameObject Group(Scene scene, string name, Vector3 position)
    {
        var group = new GameObject(name);
        SceneManager.MoveGameObjectToScene(group, scene);
        group.transform.position = position;
        return group;
    }

    private static Transform[] FindKeySpots(Transform arena)
    {
        Transform spots = arena.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "KeySpots");
        return spots != null ? spots.Cast<Transform>().ToArray() : new Transform[0];
    }

    // O jogador: o objeto com a tag Goal que tem (ou contém) o PlayerMovement.
    private static Transform FindPlayer(Transform root)
    {
        foreach (PlayerMovement movement in root.GetComponentsInChildren<PlayerMovement>(true))
        {
            Transform candidate = movement.transform;
            while (candidate != null && !candidate.CompareTag(PlayerTag) && candidate != root)
                candidate = candidate.parent;
            if (candidate != null && candidate.CompareTag(PlayerTag))
                return candidate;
        }

        return null;
    }

    // ================================================================================
    // O menu duplicado e o Build Settings
    // ================================================================================

    // Cópia do MainMenu só se ainda não existe (a que existe pode ter ajuste à mão); o índice do jogo é acertado sempre. As duas cenas
    // vão para o FIM do Build Settings: o índice que o MainMenu do time usa (Cena FInal) continua o mesmo.
    private static bool SetUpMenuFlow()
    {
        if (!File.Exists(MenuScene))
        {
            Debug.LogError($"[FreeV82Game] falta {MenuScene}.");
            return false;
        }

        // Unity 6: com um Build Profile ativo (os de treino: V5/V6/V8 - Training), o Play do Editor e o
        // EditorBuildSettings.scenes usam a lista DELE (só a cena de treino). O menu carrega o jogo por número e dava
        // "build index 2 couldn't be loaded"; e gravar aqui mexeria na lista do perfil de treino. Só na lista da
        // plataforma (a compartilhada, a do time).
        var activeProfile = UnityEditor.Build.Profile.BuildProfile.GetActiveBuildProfile();
        if (activeProfile != null)
        {
            Debug.LogError($"[FreeV82Game] o Build Profile '{activeProfile.name}' está ativo: o Play usa a lista de cenas DELE. " +
                           "Em File > Build Profiles, selecione a plataforma (Windows) e clique em Switch Profile; depois rode este menu de novo.");
            return false;
        }

        // Já existe: fica (pode ter ajuste à mão); só o número da cena do jogo é acertado abaixo.
        if (!File.Exists(GameMenuScene) && !AssetDatabase.CopyAsset(MenuScene, GameMenuScene))
        {
            Debug.LogError($"[FreeV82Game] não consegui copiar {MenuScene} para {GameMenuScene}.");
            return false;
        }

        List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes
            .Where(s => s.path != GameMenuScene && s.path != GameScene).ToList();
        scenes.Add(new EditorBuildSettingsScene(GameMenuScene, true));
        scenes.Add(new EditorBuildSettingsScene(GameScene, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        AssetDatabase.SaveAssets(); // grava o ProjectSettings/EditorBuildSettings.asset já (senão só no "Save Project")

        Scene menu = EditorSceneManager.OpenScene(GameMenuScene, OpenSceneMode.Single);
        mainMenu[] menus = Object.FindObjectsByType<mainMenu>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (menus.Length == 0)
        {
            Debug.LogError($"[FreeV82Game] {GameMenuScene} sem o componente mainMenu.");
            return false;
        }

        foreach (mainMenu component in menus)
        {
            var settings = new SerializedObject(component);
            settings.FindProperty("_gameScene").stringValue = Path.GetFileNameWithoutExtension(GameScene);
            settings.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.SaveScene(menu);
        Debug.Log($"[FreeV82Game] {GameMenuScene}: o Play carrega a Cena Final V8.");
        return true;
    }

    // ================================================================================
    // Iluminação da cena (RenderSettings é por cena: sem copiar, a cena nova sai com o céu e a névoa padrão)
    // ================================================================================

    private struct RenderCopy
    {
        private bool _fog;
        private FogMode _fogMode;
        private Color _fogColor;
        private float _fogDensity, _fogStart, _fogEnd;
        private AmbientMode _ambientMode;
        private Color _ambientSky, _ambientEquator, _ambientGround;
        private float _ambientIntensity;
        private Material _skybox;
        private float _reflectionIntensity;
        private DefaultReflectionMode _reflectionMode;
        private Color _subtractiveShadow;

        public static RenderCopy FromActive() => new RenderCopy
        {
            _fog = RenderSettings.fog,
            _fogMode = RenderSettings.fogMode,
            _fogColor = RenderSettings.fogColor,
            _fogDensity = RenderSettings.fogDensity,
            _fogStart = RenderSettings.fogStartDistance,
            _fogEnd = RenderSettings.fogEndDistance,
            _ambientMode = RenderSettings.ambientMode,
            _ambientSky = RenderSettings.ambientSkyColor,
            _ambientEquator = RenderSettings.ambientEquatorColor,
            _ambientGround = RenderSettings.ambientGroundColor,
            _ambientIntensity = RenderSettings.ambientIntensity,
            _skybox = RenderSettings.skybox,
            _reflectionIntensity = RenderSettings.reflectionIntensity,
            _reflectionMode = RenderSettings.defaultReflectionMode,
            _subtractiveShadow = RenderSettings.subtractiveShadowColor,
        };

        public void Apply()
        {
            RenderSettings.fog = _fog;
            RenderSettings.fogMode = _fogMode;
            RenderSettings.fogColor = _fogColor;
            RenderSettings.fogDensity = _fogDensity;
            RenderSettings.fogStartDistance = _fogStart;
            RenderSettings.fogEndDistance = _fogEnd;
            RenderSettings.ambientMode = _ambientMode;
            RenderSettings.ambientSkyColor = _ambientSky;
            RenderSettings.ambientEquatorColor = _ambientEquator;
            RenderSettings.ambientGroundColor = _ambientGround;
            RenderSettings.ambientIntensity = _ambientIntensity;
            RenderSettings.skybox = _skybox;
            RenderSettings.reflectionIntensity = _reflectionIntensity;
            RenderSettings.defaultReflectionMode = _reflectionMode;
            RenderSettings.subtractiveShadowColor = _subtractiveShadow;
        }
    }
}

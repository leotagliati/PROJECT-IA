using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-10)]
public class SpineLook : MonoBehaviour
{
    [Header("Ossos")]
    [Tooltip("Osso que recebe o pitch do mouse. Vazio: procura um filho pelo nome abaixo.")]
    [SerializeField] private Transform spineBone;

    [SerializeField] private string spineBoneName = "spine.002";

    [Tooltip("Osso que a câmera acompanha. Precisa ser descendente do osso da coluna. Vazio: procura pelo nome abaixo.")]
    [SerializeField] private Transform followBone;

    // Naming do Rigify: spine.004 é o pescoço, spine.005/006 a cabeça. O "Neck" que também
    // existe no FBX não está na cadeia da coluna — seguir ele deixa a câmera sem pitch.
    [SerializeField] private string followBoneName = "spine.004";

    [Header("Agachar")]
    [Tooltip("Osso que desce ao agachar: a bacia, raiz da cadeia. Vazio: procura pelo nome abaixo.")]
    [SerializeField] private Transform hipsBone;

    // Rigify: "spine" é a bacia; thigh.L/R e spine.001 pendem dele, então descer este osso
    // desce o corpo inteiro.
    [SerializeField] private string hipsBoneName = "spine";

    [Tooltip("Quanto a bacia desce com o agachamento completo, em metros. Sem IK de perna os pés afundam no chão nessa mesma medida.")]
    [SerializeField, Min(0f)] private float crouchBodyDrop = 0.45f;

    [Header("Torção de strafe")]
    [Tooltip("Quanto a bacia pode girar para o rumo do movimento, em graus. Andar de lado puro (90°) para aqui; o resto vira pé deslizando, que incomoda menos que o tronco torcido demais.")]
    [SerializeField, Range(0f, 80f)] private float maxStrafeTwist = 50f;

    [Tooltip("Tempo de acomodação da torção. Trocar de A para D direto sem isto estala o quadril.")]
    [SerializeField, Min(0.01f)] private float strafeTwistSmoothTime = 0.15f;

    [Header("Peek")]
    [Tooltip("Vazio: procura no mesmo objeto. Sem ele, o peek fica todo na câmera, como antes.")]
    [SerializeField] private ShoulderPeek shoulderPeek;

    [Tooltip("Ossos da coluna, do quadril para cima. Repartem a inclinada do peek e a contra-torção do strafe. Vazio: procura pelos nomes abaixo. Mais ossos = arco mais suave.")]
    [SerializeField] private Transform[] peekSpineChain;

    // A bacia ("spine") fica de fora: ela é a raiz das pernas, e inclinar ali arrasta as coxas
    // e descola os pés do chão. O arco começa logo acima dela.
    [SerializeField] private string[] peekSpineChainNames = { "spine.001", "spine.002", "spine.003" };

    [Tooltip("Ângulo TOTAL da inclinada do corpo, em graus, repartido entre os ossos da cadeia. É o tamanho do arco, independente do roll de câmera do ShoulderPeek.")]
    [SerializeField, Range(0f, 60f)] private float peekBodyLean = 24f;

    [Tooltip("Quanto a cabeça se endireita contra o arco. 1 = fica de pé (olhar continua reto); 0 = deita junto com o tronco.")]
    [SerializeField, Range(0f, 1f)] private float peekHeadLevel = 1f;

    [Tooltip("Osso que endireita a cabeça. Vazio: o próprio osso que a câmera segue.")]
    [SerializeField] private Transform headBone;

    [Header("Referências")]
    [SerializeField] private PlayerCamera playerCamera;

    [SerializeField] private PlayerMovement movement;

    [SerializeField] private Camera targetCamera;

    private Transform cameraTransform;

    // Offset da câmera em relação ao osso seguido, medidos na pose de descanso do prefab.
    // Rig do Blender chega com eixos locais imprevisíveis (Y ao longo do osso, X/Z
    // trocados) e escala não-unitária; em vez de descobrir a convenção, mede-se uma vez a
    // diferença e reaplica-se todo frame. Só rotação, sem escala do osso, de propósito.
    private Vector3 offsetInBone;
    private Quaternion boneToCamera;

    // A câmera só herda o endireitar da cabeça se ele acontecer no osso que ela segue ou
    // acima dele. Apontar headBone para a cabeça de verdade (abaixo do pescoço) endireita o
    // modelo sem mexer na câmera — é uma escolha válida, e o desconto precisa saber.
    private bool cameraInheritsHeadLevel;

    private bool canStrafeTwist;

    private float strafeTwistTarget;
    private float strafeTwistVelocity;

    private Vector3 strafeTwistDrift;

    private struct BoneOffset
    {
        public Transform Bone;
        public Quaternion Base;
        public Quaternion Applied;
    }

    private readonly List<BoneOffset> touchedBones = new List<BoneOffset>(8);

    /// <summary>Pitch aplicado ao osso neste frame, em graus. Positivo = curvado para frente.</summary>
    public float CurrentBend { get; private set; }

    /// <summary>
    /// Posição da câmera no espaço da raiz do player, sem bob/dip/lean. É a base que o
    /// CameraJuice usa no lugar da localPosition fixa do prefab.
    /// </summary>
    public Vector3 AnchorLocalPosition { get; private set; }

    /// <summary>
    /// Quanto a bacia desceu neste frame, em metros. Já está dentro de
    /// <see cref="AnchorLocalPosition"/>; o CameraJuice desconta isto do drop dele.
    /// </summary>
    public float CrouchDrop { get; private set; }

    public float StrafeTwist { get; private set; }

    /// <summary>
    /// Quanto o arco do corpo já deslocou a câmera para o lado, em metros, no eixo do player
    /// (negativo = esquerda). O ShoulderPeek desloca só o que faltar para o lean dele.
    /// </summary>
    public float PeekLateralApplied { get; private set; }

    /// <summary>
    /// Roll do peek que os ossos já entregaram à câmera, em graus e na convenção do
    /// ShoulderPeek. Com a cabeça totalmente de pé isto é ~0: o corpo inclina, a vista não.
    /// </summary>
    public float PeekRollApplied { get; private set; }

    private void Awake()
    {
        if (playerCamera == null)
            playerCamera = GetComponent<PlayerCamera>();

        if (targetCamera == null)
            targetCamera = GetComponentInChildren<Camera>();

        if (movement == null)
            movement = GetComponent<PlayerMovement>();

        if (shoulderPeek == null)
            shoulderPeek = GetComponent<ShoulderPeek>();

        if (spineBone == null && !string.IsNullOrEmpty(spineBoneName))
            spineBone = FindDeep(transform, spineBoneName);

        if (followBone == null && !string.IsNullOrEmpty(followBoneName))
            followBone = FindDeep(transform, followBoneName);

        if (hipsBone == null && !string.IsNullOrEmpty(hipsBoneName))
            hipsBone = FindDeep(transform, hipsBoneName);

        // Sem bacia o agachar continua funcionando: o CameraJuice recebe CrouchDrop = 0 e
        // desce a câmera sozinho, como antes.
        if (hipsBone == null)
            Debug.LogWarning($"{nameof(SpineLook)}: osso '{hipsBoneName}' não encontrado; o corpo não desce ao agachar nem torce no strafe.", this);

        if (spineBone == null || targetCamera == null)
        {
            Debug.LogError($"{nameof(SpineLook)}: osso '{spineBoneName}' ou câmera não encontrados.", this);
            enabled = false;
            return;
        }

        // O osso seguido tem que descer da coluna, senão a dobra não chega nele e a câmera
        // fica sem pitch, com o corpo se mexendo sozinho. Sem pescoço válido, a coluna mesma
        // serve de âncora: o arco fica mais curto, mas existe.
        if (followBone == null || !followBone.IsChildOf(spineBone))
        {
            if (followBone != null)
                Debug.LogWarning($"{nameof(SpineLook)}: '{followBone.name}' não é descendente de '{spineBone.name}'; usando a coluna como âncora.", this);

            followBone = spineBone;
        }

        if (peekSpineChain == null || peekSpineChain.Length == 0)
            peekSpineChain = FindBones(peekSpineChainNames);

        // Buraco no array do Inspector quebraria a repartição: o ângulo é dividido pelo
        // número de ossos, e um nulo pulado deixaria a soma curta — na torção, isso é a
        // câmera girando junto com a bacia.
        peekSpineChain = System.Array.FindAll(peekSpineChain, bone => bone != null);

        canStrafeTwist = hipsBone != null &&
                         peekSpineChain.Length > 0 &&
                         peekSpineChain[0].IsChildOf(hipsBone) &&
                         followBone.IsChildOf(peekSpineChain[peekSpineChain.Length - 1]);

        if (hipsBone != null && !canStrafeTwist)
            Debug.LogWarning($"{nameof(SpineLook)}: a cadeia da coluna não liga '{hipsBone.name}' a '{followBone.name}'; torção de strafe desligada.", this);

        if (headBone == null)
            headBone = followBone;

        cameraInheritsHeadLevel = headBone == followBone || followBone.IsChildOf(headBone);

        cameraTransform = targetCamera.transform;

        // Awake roda antes da primeira avaliação do Animator: os ossos ainda estão na pose
        // do prefab, que é a referência de "corpo ereto", e a câmera está onde o prefab a
        // deixou. A rotação da raiz é o olhar reto, sem pitch.
        Quaternion inverseBone = Quaternion.Inverse(followBone.rotation);
        offsetInBone = inverseBone * (cameraTransform.position - followBone.position);
        boneToCamera = inverseBone * transform.rotation;

        AnchorLocalPosition = cameraTransform.localPosition;
    }

    private void OnDisable()
    {
        // Desligado (ex.: PlayerCaughtSequence), ninguém mais desfaz: ossos sem curva de
        // animação ficariam torcidos/inclinados para sempre.
        RestoreBones();
    }

    private void LateUpdate()
    {
        if (playerCamera == null)
            return;

        RestoreBones();

        CurrentBend = playerCamera.Pitch;

        // Bacia antes da coluna: o pescoço herda a descida e a câmera vai junto. Em mundo,
        // no up da raiz — o rig chega com eixos locais do Blender. CrouchAmount já vem
        // suavizado do PlayerMovement (é a transição da própria cápsula).
        CrouchDrop = hipsBone != null && movement != null ? crouchBodyDrop * movement.CrouchAmount : 0f;

        if (CrouchDrop > 0f)
            hipsBone.position -= transform.up * CrouchDrop;

        // Torção antes do peek e do pitch: as duas giram em torno de eixos da RAIZ, e com a
        // orientação do pescoço já devolvida pela contra-torção elas saem iguais com ou sem
        // strafe.
        ApplyStrafeTwist();

        ApplyPeekLean();

        // Gira em torno do eixo lateral do player, em mundo: o transform.right da raiz já
        // carrega o yaw certo. Pré-multiplicado para a dobra somar à pose da animação em
        // vez de substituí-la — é essa soma que faz o balanço do passo chegar na câmera.
        RotateBone(spineBone, Quaternion.AngleAxis(CurrentBend, transform.right));

        CommitBones();

        Vector3 worldPosition = followBone.position + followBone.rotation * offsetInBone - strafeTwistDrift;

        cameraTransform.SetPositionAndRotation(worldPosition, followBone.rotation * boneToCamera);

        AnchorLocalPosition = cameraTransform.localPosition;
    }

    /// <summary>
    /// Gira a bacia para o rumo do movimento e devolve o ângulo pela coluna, em fatias iguais.
    /// A orientação do pescoço sai igual à de antes; o desvio de posição que sobra fica em
    /// <see cref="strafeTwistDrift"/> para a câmera descontar.
    /// </summary>
    private void ApplyStrafeTwist()
    {
        strafeTwistDrift = Vector3.zero;

        if (!canStrafeTwist)
        {
            StrafeTwist = 0f;
            return;
        }

        strafeTwistTarget = ResolveStrafeTwistTarget();
        StrafeTwist = Mathf.SmoothDamp(StrafeTwist, strafeTwistTarget, ref strafeTwistVelocity, strafeTwistSmoothTime);

        if (Mathf.Abs(StrafeTwist) < 0.01f)
            return;

        Vector3 before = followBone.position;
        Vector3 up = transform.up;

        // Positivo em torno do up = horário visto de cima = frente virando para a direita,
        // mesmo sinal do MoveInput.x.
        RotateBone(hipsBone, Quaternion.AngleAxis(StrafeTwist, up));

        Quaternion counter = Quaternion.AngleAxis(-StrafeTwist / peekSpineChain.Length, up);

        foreach (Transform bone in peekSpineChain)
            RotateBone(bone, counter);

        strafeTwistDrift = followBone.position - before;
    }

    /// <summary>
    /// Ângulo que põe as pernas no eixo do movimento. Para trás, o clipe continua sendo de
    /// andar para frente: virar a bacia 180° seria absurdo, então o ângulo é dobrado para o
    /// lado oposto — ré-direita vira frente-esquerda, a passada fica no plano certo, só que
    /// com a animação no sentido contrário até existir um clipe de ré.
    /// </summary>
    private float ResolveStrafeTwistTarget()
    {
        if (movement == null)
            return 0f;

        switch (movement.CurrentState)
        {
            case PlayerState.Walking:
            case PlayerState.Running:
            case PlayerState.CrouchWalking:
                break;

            // No ar a animação congela no passo em curso (ver PlayerMovement): mantém a pose
            // em vez de desfazer a torção no meio do pulo.
            case PlayerState.Jumping:
                return strafeTwistTarget;

            default:
                return 0f;
        }

        Vector2 input = movement.MoveInput;

        if (input.sqrMagnitude < 0.01f)
            return 0f;

        // Folga no lado de trás: com stick, lateral puro oscila em volta de y = 0, e cada
        // cruzamento trocaria +max por -max.
        float radians = input.y > -0.2f
            ? Mathf.Atan2(input.x, input.y)
            : Mathf.Atan2(-input.x, -input.y);

        return Mathf.Clamp(radians * Mathf.Rad2Deg, -maxStrafeTwist, maxStrafeTwist);
    }

    /// <summary>
    /// Reparte a inclinada entre os ossos da cadeia e endireita a cabeça. Mede, no caminho,
    /// quanto o arco deslocou o pescoço para o lado — é o que o ShoulderPeek desconta do
    /// deslocamento de câmera dele, para o total continuar sendo o leanDistance de lá.
    /// </summary>
    private void ApplyPeekLean()
    {
        PeekLateralApplied = 0f;
        PeekRollApplied = 0f;

        if (shoulderPeek == null || peekSpineChain.Length == 0)
            return;

        // LeanAmount já vem suavizado e reduzido quando uma parede corta a espiada: o corpo
        // inclina exatamente o quanto a câmera conseguiu sair.
        float amount = shoulderPeek.LeanAmount;
        if (Mathf.Abs(amount) < 0.001f)
            return;

        float total = amount * peekBodyLean;
        float perBone = total / peekSpineChain.Length;

        // Antes de tocar nos ossos: a referência para medir o desvio lateral do arco.
        Vector3 before = followBone.position;

        // Em torno do eixo frontal do player, em mundo. Sinal igual ao do ShoulderPeek
        // (direita = negativo), então LeanAmount positivo tomba o corpo para a direita.
        Quaternion step = Quaternion.AngleAxis(-perBone, transform.forward);

        foreach (Transform bone in peekSpineChain)
            RotateBone(bone, step);

        // A cabeça desfaz o acumulado para continuar de pé. É o oposto de girar a cabeça: sem
        // isto ela deita junto com o arco e o olhar sai torto.
        float headCorrection = total * peekHeadLevel;

        if (!Mathf.Approximately(headCorrection, 0f))
            RotateBone(headBone, Quaternion.AngleAxis(headCorrection, transform.forward));

        PeekLateralApplied = Vector3.Dot(followBone.position - before, transform.right);
        PeekRollApplied = -total + (cameraInheritsHeadLevel ? headCorrection : 0f);
    }

    /// <summary>
    /// Pré-multiplica uma rotação de mundo no osso e, no primeiro toque do frame, guarda a
    /// pose de antes para <see cref="RestoreBones"/>. Toques seguintes no mesmo osso (peek
    /// por cima da torção, pitch por cima dos dois) só somam.
    /// </summary>
    private void RotateBone(Transform bone, Quaternion worldDelta)
    {
        if (bone == null)
            return;

        if (IndexOfTouched(bone) < 0)
            touchedBones.Add(new BoneOffset { Bone = bone, Base = bone.localRotation });

        bone.rotation = worldDelta * bone.rotation;
    }

    /// <summary>Anota onde cada osso ficou, depois da última camada do frame.</summary>
    private void CommitBones()
    {
        for (int i = 0; i < touchedBones.Count; i++)
        {
            BoneOffset entry = touchedBones[i];
            entry.Applied = entry.Bone.localRotation;
            touchedBones[i] = entry;
        }
    }

    /// <summary>
    /// Devolve os ossos à pose de antes das rotações do frame passado. Só mexe no osso que
    /// continua exatamente onde o deixamos: se o Animator reescreveu (o osso tem curva de
    /// animação), a pose nova é a boa e desfazer por cima dela é que estragaria.
    /// </summary>
    private void RestoreBones()
    {
        foreach (BoneOffset entry in touchedBones)
        {
            if (entry.Bone != null && Quaternion.Angle(entry.Bone.localRotation, entry.Applied) < 0.01f)
                entry.Bone.localRotation = entry.Base;
        }

        touchedBones.Clear();
    }

    private int IndexOfTouched(Transform bone)
    {
        for (int i = 0; i < touchedBones.Count; i++)
        {
            if (touchedBones[i].Bone == bone)
                return i;
        }

        return -1;
    }

    private Transform[] FindBones(string[] names)
    {
        if (names == null)
            return new Transform[0];

        var found = new List<Transform>(names.Length);

        foreach (string name in names)
        {
            Transform bone = string.IsNullOrEmpty(name) ? null : FindDeep(transform, name);

            if (bone != null)
                found.Add(bone);
            else
                Debug.LogWarning($"{nameof(SpineLook)}: osso '{name}' não encontrado; o arco do peek fica mais curto.", this);
        }

        return found.ToArray();
    }

    private static Transform FindDeep(Transform root, string name)
    {
        // Transform.Find só desce um nível; o osso mora fundo na hierarquia do rig.
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == name)
                return child;
        }

        return null;
    }
}

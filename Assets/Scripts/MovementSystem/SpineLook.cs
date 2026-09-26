using UnityEngine;

/// <summary>
/// Põe a câmera "no pescoço" do modelo, em duas etapas por frame:
///   1. dobra o spine.002 com o pitch do mouse. O Animator escreve a pose dos ossos no
///      Update, então isso só sobrevive em LateUpdate;
///   2. a câmera acompanha o osso do pescoço em posição E rotação, com o mesmo offset que
///      tinha em relação a ele na pose de descanso — como se fosse filha dele.
///
/// O resultado é o arco: olhar para baixo curva o tronco, o pescoço vai para frente e para
/// baixo, e a câmera vai junto, em vez de só girar parada no lugar. O balanço da
/// caminhada/corrida também entra na visão (é o pescoço que balança). Se somado ao head
/// bob procedural do CameraJuice ficar demais, baixe os *Bob Amount* lá.
///
/// O agachar entra pelo mesmo caminho: a bacia desce (<see cref="crouchBodyDrop"/>), o
/// pescoço vai junto e a câmera segue. O CameraJuice aplica só o que faltar para o drop
/// de câmera dele — com os dois iguais, o corpo é quem leva a câmera para baixo.
///
/// A inclinada do peek é o mesmo princípio, no eixo frontal: o ângulo é REPARTIDO entre
/// vários ossos da coluna (<see cref="peekSpineChain"/>), de baixo para cima. Cada junta
/// dobra pouco e o desvio se acumula, então o topo do tronco viaja para o lado — é isso que
/// desenha o arco em C. Pôr o ângulo inteiro num osso só não curva nada: gira peito, pescoço
/// e cabeça como um bloco, e o que se vê é a cabeça rodando. A cabeça, essa, fica de pé
/// (<see cref="peekHeadLevel"/>), como a de quem espia sem deitar o olhar.
///
/// A inclinada DESFAZ a si mesma antes de reaplicar, a cada frame. O pitch pode somar direto
/// na pose porque o Animator reescreve spine.002 todo Update; ossos SEM curva de animação
/// (spine.001, spine.003) ninguém reescreve, e aí pré-multiplicar todo frame vira rotação
/// acumulada — o tronco dá a volta completa em poucos segundos. Ver <see cref="RestoreChain"/>.
///
/// Não parenta a câmera no osso de verdade: PlayerCamera, ShoulderPeek e CameraJuice
/// escrevem a transform dela assumindo a raiz do player como pai, e o rig chega com escala
/// e eixos locais do Blender. Em vez disso, este componente escreve a posição e a rotação
/// em mundo, e expõe a posição como âncora (<see cref="AnchorLocalPosition"/>) para o
/// CameraJuice somar bob/dip/crouch em cima.
///
/// Ordem -10: antes do CameraJuice (0), que parte da âncora, e do ShoulderPeek (50), que
/// pré-multiplica o yaw do peek sobre a rotação escrita aqui. A rotação que o PlayerCamera
/// põe na câmera no Update vira fallback para quando este componente está desligado.
/// </summary>
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

    [Header("Peek")]
    [Tooltip("Vazio: procura no mesmo objeto. Sem ele, o peek fica todo na câmera, como antes.")]
    [SerializeField] private ShoulderPeek shoulderPeek;

    [Tooltip("Ossos que repartem a inclinada, do quadril para cima. Vazio: procura pelos nomes abaixo. Mais ossos = arco mais suave.")]
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

    // Para desfazer a inclinada do frame anterior: onde o osso estava antes de ela ser
    // aplicada, e onde ela o deixou. Se o osso ainda está onde deixamos, o Animator não
    // reescreveu — e é nosso o trabalho de devolver a pose antes de aplicar de novo.
    //
    // Em rotação LOCAL, e não em mundo: a de mundo muda sozinha quando o player vira com o
    // mouse, a comparação nunca bateria e o acúmulo voltaria justamente enquanto se gira.
    private Quaternion[] peekBaseRotation;
    private Quaternion[] peekAppliedRotation;
    private Quaternion headBaseRotation;
    private Quaternion headAppliedRotation;
    private bool peekApplied;

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
            Debug.LogWarning($"{nameof(SpineLook)}: osso '{hipsBoneName}' não encontrado; o corpo não desce ao agachar.", this);

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

        peekBaseRotation = new Quaternion[peekSpineChain.Length];
        peekAppliedRotation = new Quaternion[peekSpineChain.Length];

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

    private void LateUpdate()
    {
        if (playerCamera == null)
            return;

        CurrentBend = playerCamera.Pitch;

        // Bacia antes da coluna: o pescoço herda a descida e a câmera vai junto. Em mundo,
        // no up da raiz — o rig chega com eixos locais do Blender. CrouchAmount já vem
        // suavizado do PlayerMovement (é a transição da própria cápsula).
        CrouchDrop = hipsBone != null && movement != null ? crouchBodyDrop * movement.CrouchAmount : 0f;

        if (CrouchDrop > 0f)
            hipsBone.position -= transform.up * CrouchDrop;

        ApplyPeekLean();

        // Gira em torno do eixo lateral do player, em mundo: o transform.right da raiz já
        // carrega o yaw certo. Pré-multiplicado para a dobra somar à pose da animação em
        // vez de substituí-la — é essa soma que faz o balanço do passo chegar na câmera.
        spineBone.rotation = Quaternion.AngleAxis(CurrentBend, transform.right) * spineBone.rotation;

        Vector3 worldPosition = followBone.position + followBone.rotation * offsetInBone;

        cameraTransform.SetPositionAndRotation(worldPosition, followBone.rotation * boneToCamera);

        AnchorLocalPosition = cameraTransform.localPosition;
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

        if (shoulderPeek == null || peekSpineChain == null || peekSpineChain.Length == 0)
            return;

        RestoreChain();

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

        for (int i = 0; i < peekSpineChain.Length; i++)
        {
            Transform bone = peekSpineChain[i];
            if (bone == null)
                continue;

            peekBaseRotation[i] = bone.localRotation;
            bone.rotation = step * bone.rotation;
            peekAppliedRotation[i] = bone.localRotation;
        }

        // A cabeça desfaz o acumulado para continuar de pé. É o oposto de girar a cabeça: sem
        // isto ela deita junto com o arco e o olhar sai torto.
        float headCorrection = total * peekHeadLevel;

        if (headBone != null && !Mathf.Approximately(headCorrection, 0f))
        {
            headBaseRotation = headBone.localRotation;
            headBone.rotation = Quaternion.AngleAxis(headCorrection, transform.forward) * headBone.rotation;
            headAppliedRotation = headBone.localRotation;
        }
        else
        {
            headBaseRotation = headAppliedRotation = headBone != null ? headBone.localRotation : Quaternion.identity;
        }

        peekApplied = true;

        PeekLateralApplied = Vector3.Dot(followBone.position - before, transform.right);
        PeekRollApplied = -total + (cameraInheritsHeadLevel ? headCorrection : 0f);
    }

    /// <summary>
    /// Devolve os ossos à pose de antes da inclinada do frame passado. Só mexe no osso que
    /// continua exatamente onde o deixamos: se o Animator reescreveu (o osso tem curva de
    /// animação), a pose nova é a boa e desfazer por cima dela é que estragaria. Sem isto, osso
    /// sem animação acumula a inclinada frame após frame e o tronco roda sem parar.
    /// </summary>
    private void RestoreChain()
    {
        if (!peekApplied)
            return;

        peekApplied = false;

        for (int i = 0; i < peekSpineChain.Length; i++)
        {
            Transform bone = peekSpineChain[i];

            if (bone != null && Quaternion.Angle(bone.localRotation, peekAppliedRotation[i]) < 0.01f)
                bone.localRotation = peekBaseRotation[i];
        }

        if (headBone != null && Quaternion.Angle(headBone.localRotation, headAppliedRotation) < 0.01f)
            headBone.localRotation = headBaseRotation;
    }

    private Transform[] FindBones(string[] names)
    {
        if (names == null)
            return new Transform[0];

        var found = new System.Collections.Generic.List<Transform>(names.Length);

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

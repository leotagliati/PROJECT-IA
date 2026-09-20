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

    private void Awake()
    {
        if (playerCamera == null)
            playerCamera = GetComponent<PlayerCamera>();

        if (targetCamera == null)
            targetCamera = GetComponentInChildren<Camera>();

        if (movement == null)
            movement = GetComponent<PlayerMovement>();

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

        // Gira em torno do eixo lateral do player, em mundo: o transform.right da raiz já
        // carrega o yaw certo. Pré-multiplicado para a dobra somar à pose da animação em
        // vez de substituí-la — é essa soma que faz o balanço do passo chegar na câmera.
        spineBone.rotation = Quaternion.AngleAxis(CurrentBend, transform.right) * spineBone.rotation;

        Vector3 worldPosition = followBone.position + followBone.rotation * offsetInBone;

        cameraTransform.SetPositionAndRotation(worldPosition, followBone.rotation * boneToCamera);

        AnchorLocalPosition = cameraTransform.localPosition;
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

using UnityEngine;

/// <summary>
/// Fôlego da corrida. Gasta enquanto o <see cref="PlayerMovement"/> está em Running e volta
/// depois de um tempo sem correr. Quem decide se pode correr é o PlayerMovement, perguntando
/// <see cref="CanSprint"/>; este componente só faz a conta.
///
/// Zerar deixa o jogador exausto até recuperar <see cref="recoverThreshold"/>. Sem isso, com a
/// tecla segurada, a barra ficaria oscilando em torno de zero: um frame recupera, o próximo
/// corre e gasta, e a corrida vira um liga-desliga visível na câmera e nos passos.
/// </summary>
public class PlayerStamina : MonoBehaviour
{
    [SerializeField] private PlayerMovement movement;

    [Tooltip("Segundos de corrida contínua com a barra cheia.")]
    [SerializeField, Min(0.1f)] private float sprintDuration = 5f;

    [Tooltip("Segundos para encher do zero, depois que a recuperação começa.")]
    [SerializeField, Min(0.1f)] private float refillDuration = 6f;

    [Tooltip("Espera depois de parar de correr até começar a recuperar.")]
    [SerializeField, Min(0f)] private float regenDelay = 1f;

    [Tooltip("Fração da barra que precisa voltar depois de zerar para poder correr de novo.")]
    [SerializeField, Range(0f, 1f)] private float recoverThreshold = 0.3f;

    private float _stamina = 1f;
    private float _lastSprintTime = float.NegativeInfinity;

    /// <summary>0 = vazia, 1 = cheia.</summary>
    public float Normalized => _stamina;

    /// <summary>Zerou e ainda não recuperou o mínimo.</summary>
    public bool IsExhausted { get; private set; }

    public bool CanSprint => !IsExhausted;

    public float RecoverThreshold => recoverThreshold;

    private void Awake()
    {
        if (movement == null)
            movement = GetComponent<PlayerMovement>();

        if (movement == null)
        {
            Debug.LogWarning($"{name}: sem PlayerMovement — stamina desligada.", this);
            enabled = false;
        }
    }

    private void Update()
    {
        // Só no chão: no ar a velocidade já cai para a de caminhada (PlayerMovement), então não
        // há corrida para cobrar. PlayerMovement desligado (captura) congela o estado em
        // Running, e sem o isActiveAndEnabled a barra seguiria descendo na tela de morte.
        bool running = movement.isActiveAndEnabled && movement.CurrentState == PlayerState.Running;

        if (running)
        {
            _lastSprintTime = Time.time;
            _stamina = Mathf.MoveTowards(_stamina, 0f, Time.deltaTime / sprintDuration);

            if (_stamina <= 0f)
                IsExhausted = true;
        }
        else if (Time.time - _lastSprintTime >= regenDelay)
        {
            _stamina = Mathf.MoveTowards(_stamina, 1f, Time.deltaTime / refillDuration);

            if (IsExhausted && _stamina >= recoverThreshold)
                IsExhausted = false;
        }
    }
}

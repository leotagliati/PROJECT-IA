using UnityEngine;

/// <summary>
/// Volume que mostra uma dica quando o jogador entra ("A porta está trancada. Procure as
/// *chaves*"). O texto usa a marcação do <see cref="HintController"/>.
/// </summary>
[RequireComponent(typeof(Collider))]
public class HintTrigger : MonoBehaviour
{
    [Tooltip("Vazio = procura na cena.")]
    [SerializeField] private HintController controller;

    [TextArea(2, 4)]
    [SerializeField] private string text;

    [Tooltip("Segundos na tela. 0 = enquanto o jogador estiver dentro do volume.")]
    [SerializeField, Min(0f)] private float duration = 4f;

    [Tooltip("Dispara só na primeira entrada (por carregamento de cena).")]
    [SerializeField] private bool once = true;

    private int inside;
    private bool fired;
    private HintHandle handle;

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void Awake()
    {
        if (controller == null)
            controller = FindFirstObjectByType<HintController>();

        if (controller == null)
        {
            Debug.LogError($"{name}: sem {nameof(HintController)} na cena.", this);
            enabled = false;
        }
    }

    // Contagem, e não bool: o CharacterController e os colliders filhos do player entram
    // separados, e sair de um não é sair do volume.
    private void OnTriggerEnter(Collider other)
    {
        if (!enabled || other.GetComponentInParent<PlayerMovement>() == null)
            return;

        if (++inside > 1 || (once && fired))
            return;

        fired = true;

        // Reentrar antes de uma dica temporizada acabar não empilha uma segunda cópia.
        if (!controller.IsActive(handle))
            handle = controller.Show(text, duration);
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.GetComponentInParent<PlayerMovement>() == null)
            return;

        inside = Mathf.Max(0, inside - 1);

        if (inside == 0 && duration <= 0f && controller != null)
            controller.Hide(handle);
    }

    private void OnDisable()
    {
        inside = 0;

        // Só a dica "enquanto dentro" depende do volume; a temporizada termina sozinha.
        if (duration <= 0f && controller != null)
            controller.Hide(handle);
    }
}

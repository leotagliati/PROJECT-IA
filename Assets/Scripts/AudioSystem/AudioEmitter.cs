using UnityEngine;

/// <summary>
/// Dispara um som do <see cref="AudioSystem"/> numa posição arbitrária, sem precisar escrever
/// código: arrasta o componente num GameObject vazio, escolhe o id e o modo.
///
/// A posição é a do próprio objeto (o que dá pra arrastar no editor e ouvir o som andar pelo
/// mapa), ou um X/Z digitado à mão quando o ponto é fixo e conhecido.
/// </summary>
public class AudioEmitter : MonoBehaviour
{
    public enum TriggerMode
    {
        Manual,      // só quando alguém chamar Emit() — ou pelo menu de contexto, no play mode
        OnStart,
        Repeating,
        OnTriggerEnterHere,   // precisa de um Collider com IsTrigger neste objeto
    }

    [Header("-----Som-----")]
    [SerializeField] private string _soundId = "beep";
    [SerializeField, Range(0f, 1f)] private float _volumeScale = 1f;

    [Header("-----Posição-----")]
    // Ligado: usa a posição deste objeto. Desligado: usa o X/Z abaixo, na altura padrão do AudioSystem.
    [SerializeField] private bool _useTransformPosition = true;
    [SerializeField] private float _x;
    [SerializeField] private float _z;

    // Se o objeto se mexe enquanto o som toca (plataforma, drone), a fonte acompanha.
    [SerializeField] private bool _followThisObject;

    [Header("-----Disparo-----")]
    [SerializeField] private TriggerMode _mode = TriggerMode.Manual;
    [SerializeField] private float _interval = 3f;
    [SerializeField] private string _triggerTag = "Player";

    private float _nextEmitTime;

    private void Start()
    {
        if (_mode == TriggerMode.OnStart)
            Emit();

        _nextEmitTime = Time.time + _interval;
    }

    private void Update()
    {
        if (_mode != TriggerMode.Repeating || Time.time < _nextEmitTime)
            return;

        _nextEmitTime = Time.time + _interval;
        Emit();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_mode != TriggerMode.OnTriggerEnterHere)
            return;

        if (!string.IsNullOrEmpty(_triggerTag) && !other.CompareTag(_triggerTag))
            return;

        Emit();
    }

    /// <summary>Ponto de entrada público: chame de qualquer script, UnityEvent ou botão de UI.</summary>
    [ContextMenu("Emit")]
    public void Emit()
    {
        if (_followThisObject)
        {
            AudioSystem.PlayFollowing(_soundId, transform, _volumeScale);
            return;
        }

        if (_useTransformPosition)
        {
            AudioSystem.PlayAt(_soundId, transform.position, _volumeScale);
            return;
        }

        AudioSystem.Play(_soundId, _x, _z, _volumeScale);
    }

    private void OnDrawGizmos()
    {
        Vector3 position = _useTransformPosition
            ? transform.position
            : new Vector3(_x, transform.position.y, _z);

        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(position, 0.3f);
        Gizmos.DrawLine(position, position + Vector3.up);
    }
}

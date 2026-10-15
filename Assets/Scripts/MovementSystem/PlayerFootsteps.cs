using UnityEngine;

/// <summary>
/// Relay dos Animation Events de passo dos clipes Walk/Run do Player.fbx. Só existe porque o
/// Unity procura o método do evento no MESMO GameObject do Animator, e quem decide o passo é o
/// <see cref="PlayerMovement"/>. Não precisa estar no prefab: o PlayerMovement adiciona no Awake.
/// </summary>
public class PlayerFootsteps : MonoBehaviour
{
    private PlayerMovement movement;

    private void Awake()
    {
        movement = GetComponentInParent<PlayerMovement>();
    }

    /// <summary>
    /// Nome chamado pelo Animation Event. Mantenha igual ao dos eventos no importer — o Unity
    /// não avisa em compile time se descasar, só em runtime.
    /// </summary>
    public void OnFootstep(AnimationEvent evt)
    {
        if (movement != null)
            movement.OnAnimationFootstep(evt);
    }
}

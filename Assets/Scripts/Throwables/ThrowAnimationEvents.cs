using UnityEngine;

/// <summary>
/// Animation Events só chamam métodos no GameObject do Animator. Fica nele e repassa ao
/// <see cref="PlayerThrow"/>, que mora na raiz do player.
/// </summary>
[RequireComponent(typeof(Animator))]
public class ThrowAnimationEvents : MonoBehaviour
{
    [SerializeField] private PlayerThrow thrower;

    private void Awake()
    {
        if (thrower == null)
            thrower = GetComponentInParent<PlayerThrow>();
    }

    // Nome usado no evento do clipe de Release.
    public void ThrowRelease()
    {
        if (thrower != null)
            thrower.ThrowRelease();
    }
}

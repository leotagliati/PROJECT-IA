using UnityEngine;

[RequireComponent(typeof(Collider))]
public class EscapeTrigger : MonoBehaviour
{
    private bool fired;

    private void Reset()
    {
        GetComponent<Collider>().isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (fired || other.GetComponentInParent<PlayerMovement>() == null)
            return;

        fired = true;

        if (GameManager.Current != null)
            GameManager.Current.PlayerEscaped();
        else
            Debug.LogError($"{name}: sem GameManager na cena — a fuga não termina o jogo.", this);
    }
}

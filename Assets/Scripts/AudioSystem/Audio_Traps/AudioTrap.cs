using UnityEngine;
using Assets.Scripts.Seeker;

public class AudioTrap : MonoBehaviour
{
    [Header("Referências")]
    [SerializeField] private Transform hunterTransform;
    [SerializeField] private float hunterEarRange = 10f;
    [SerializeField] private float alertCooldown = 0.5f;

    [Header("Configurações de Trigger")]
    [SerializeField] private string targetTag = "Goal";
    [SerializeField] private float movementThreshold = 0.1f;

    private Collider trapCollider;
    private float lastAlertTime;
    private SeekerPerceptionSystem seekerPerception;

    void Start()
    {
        trapCollider = GetComponent<Collider>();
        if (trapCollider != null)
        {
            trapCollider.isTrigger = true;
        }
        else
        {
            Debug.LogError("No Collider found on " + gameObject.name);
        }

        if (hunterTransform == null)
        {
            GameObject hunter = GameObject.FindWithTag("Hunter");
            if (hunter != null)
            {
                hunterTransform = hunter.transform;
                seekerPerception = hunter.GetComponentInChildren<SeekerPerceptionSystem>();
                if (seekerPerception == null)
                    seekerPerception = hunter.GetComponent<SeekerPerceptionSystem>();
            }
            else
            {
                Debug.LogWarning("Nenhum objeto com a tag 'Hunter' foi encontrado na cena.");
            }
        }
        else
        {
            seekerPerception = hunterTransform.GetComponentInChildren<SeekerPerceptionSystem>();
            if (seekerPerception == null)
                seekerPerception = hunterTransform.GetComponent<SeekerPerceptionSystem>();
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(targetTag))
        {
            CheckAndAlert(other);
        }
    }

    void OnTriggerStay(Collider other)
    {
        if (other.CompareTag(targetTag))
        {
            CheckAndAlert(other);
        }
    }

    private void CheckAndAlert(Collider other)
    {
        if (!IsMoving(other))
            return;

        if (Time.time - lastAlertTime < alertCooldown)
            return;

        if (hunterTransform != null && !IsHunterInRange(other.transform.position))
            return;

        lastAlertTime = Time.time;
        AlertHunter(other.transform.position);
    }

    private bool IsMoving(Collider other)
    {
        PlayerMovement playerMovement = other.GetComponent<PlayerMovement>();
        if (playerMovement != null)
            return playerMovement.IsMoving;

        Rigidbody rb = other.GetComponent<Rigidbody>();
        if (rb != null)
            return rb.linearVelocity.magnitude > movementThreshold;

        return false;
    }

    private bool IsHunterInRange(Vector3 targetPosition)
    {
        return Vector3.Distance(hunterTransform.position, targetPosition) <= hunterEarRange;
    }

    private void AlertHunter(Vector3 targetPosition)
    {
        Debug.Log("Hunter hears the target at " + targetPosition);

    
        if (seekerPerception != null)
        {
           // seekerPerception.SetHeardHiderPosition(targetPosition);
        }
    }
}
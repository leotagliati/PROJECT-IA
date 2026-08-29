using UnityEngine;

public class AudioTrap : MonoBehaviour
{
    [SerializeField] private Transform hunterTransform;
    [SerializeField] private float hunterEarRange = 10f;
    [SerializeField] private float alertCooldown = 0.01f;

    private Collider trapCollider;
    private PlayerMovement playerMovement;
    private float lastAlertTime;

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

         GameObject hunter = GameObject.FindWithTag("Hunter");

        if (hunter != null)
        {
            hunterTransform = hunter.transform;
            Debug.Log("Transform do Hunter obtido");
        }
        else
        {
            Debug.LogWarning("Nenhum objeto com a tag 'Hunter' foi encontrado na cena.");
        }

    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            playerMovement = other.GetComponent<PlayerMovement>();
            CheckPlayerMovement(other.transform.position);
            AlertHunter(other.transform.position);
        }
    }

    void OnTriggerStay(Collider other)
    {
        if (other.CompareTag("Player"))
        {

            if (playerMovement == null)
                playerMovement = other.GetComponent<PlayerMovement>();

            CheckPlayerMovement(other.transform.position);
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            playerMovement = null;
        }
    }

    private void CheckPlayerMovement(Vector3 playerPosition)
    {
        if (playerMovement == null)
            return;

        if (playerMovement.IsMoving) 
        {
   
            if (hunterTransform != null && IsHunterInRange(playerPosition))
            {
                if (Time.time - lastAlertTime >= alertCooldown)
                {
                    lastAlertTime = Time.time;
                    AlertHunter(playerPosition);
                }
            }
        }
       
    }

    private bool IsHunterInRange(Vector3 playerPosition)
    {
      
        return Vector3.Distance(hunterTransform.position, playerPosition) <= hunterEarRange;
    }

    private void AlertHunter(Vector3 playerPosition)
    {
        Debug.Log("Hunter hears the player at " + playerPosition);
    }
}
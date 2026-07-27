using UnityEngine;

public class HiderAgent : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 2f;
    [SerializeField] private Transform[] _spawnPoints;
    [SerializeField] private LayerMask _wallLayer;

    [SerializeField] private float rayDistance = 1f;

    private Rigidbody _rigidbody;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
    }

    public void Spawn()
    {
        Transform point = _spawnPoints[Random.Range(0, _spawnPoints.Length)];

        _rigidbody.position = point.position;
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
    }

    private void FixedUpdate()
    {
        _rigidbody.MovePosition(_rigidbody.position + _moveSpeed * Time.fixedDeltaTime * transform.forward);
    }

    private void Update()
    {
        VerifyChangeDirection();
    }

    private void VerifyChangeDirection()
    {
        if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, rayDistance, _wallLayer))
        {
            TurnAwayRandomDirection();
        }
    }

    private void TurnAwayRandomDirection()
    {
        var randInt = Random.Range(0, 2);

        switch (randInt)
        {
            case 0:
                transform.Rotate(Vector3.up, 90f);
                break;
            case 1:
                transform.Rotate(Vector3.up, -90f);
                break;
            case 2:
                transform.Rotate(Vector3.up, 180f);
                break;
            default:
                break;
        }
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawRay(transform.position, transform.forward * rayDistance);
    }
}

using UnityEngine;

public class HiderAgent : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 2f;
    [SerializeField] private float _minTurnAngle = 90f;
    [SerializeField] private float _maxTurnAngle = 270f;
    [SerializeField] private Transform[] _spawnPoints;

    [SerializeField] private float _detectionRange = 1f;
    [SerializeField] private float _raycastSpread = 0.3f;
    [SerializeField] private LayerMask _wallLayer;
    [SerializeField] private float _originHeightOffset = 0.1f;

    private Rigidbody _rigidbody;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
    }

    public void Spawn()
    {
        Transform point = _spawnPoints[Random.Range(0, _spawnPoints.Length)];

        _rigidbody.position = point.position;
        _rigidbody.rotation = point.rotation;
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
    }

    private void FixedUpdate()
    {
        if (IsWallAhead())
        {
            transform.Rotate(0f, Random.Range(_minTurnAngle, _maxTurnAngle), 0f);
        }

        _rigidbody.MovePosition(_rigidbody.position + transform.forward * _moveSpeed * Time.fixedDeltaTime);
    }

    private bool IsWallAhead()
    {
        Vector3 origin = transform.position + Vector3.up * _originHeightOffset;
        Vector3 leftOrigin = origin - transform.right * _raycastSpread;
        Vector3 rightOrigin = origin + transform.right * _raycastSpread;

        return Physics.Raycast(leftOrigin, transform.forward, _detectionRange, _wallLayer)
            || Physics.Raycast(rightOrigin, transform.forward, _detectionRange, _wallLayer);
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position + Vector3.up * _originHeightOffset;
        Vector3 leftOrigin = origin - transform.right * _raycastSpread;
        Vector3 rightOrigin = origin + transform.right * _raycastSpread;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(leftOrigin, leftOrigin + transform.forward * _detectionRange);
        Gizmos.DrawLine(rightOrigin, rightOrigin + transform.forward * _detectionRange);
    }
}

using UnityEngine;
using UnityEngine.Serialization;

public class HiderAgent : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 2f;
    [SerializeField] private float _turnSpeed = 360f;

    [FormerlySerializedAs("rayDistance")]
    [SerializeField] private float _probeDistance = 1f;
    [SerializeField] private LayerMask _wallLayer;
    [SerializeField] private float _probeHeightOffset = 0.1f;

    // Steps seguidos alinhado e sem sair do lugar antes de assumir que a física prendeu o
    // corpo. Precisa ser maior que a duração de uma curva de 90 graus.
    [SerializeField] private int _stuckStepsThreshold = 20;

    [SerializeField] private Transform[] _spawnPoints;

    private Rigidbody _rigidbody;
    private int _headingIndex;
    private Vector3 _lastPosition;
    private int _stuckSteps;

    private static readonly Vector3[] Cardinals =
    {
        new Vector3( 0f, 0f,  1f),
        new Vector3( 1f, 0f,  0f),
        new Vector3( 0f, 0f, -1f),
        new Vector3(-1f, 0f,  0f),
    };

    // Fração da velocidade nominal abaixo da qual o step conta como "não saiu do lugar".
    private const float StuckSpeedFraction = 0.25f;

    // Alinhamento a partir do qual o corpo já deveria estar andando a plena velocidade.
    private const float AlignedThreshold = 0.9f;

    private Vector3 Heading => Cardinals[_headingIndex];

    private int LeftIndex => (_headingIndex + 3) % Cardinals.Length;

    private int RightIndex => (_headingIndex + 1) % Cardinals.Length;

    private int BackIndex => (_headingIndex + 2) % Cardinals.Length;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _headingIndex = NearestCardinal(transform.forward);
        _lastPosition = transform.position;
    }

    public void Spawn()
    {
        if (_spawnPoints != null && _spawnPoints.Length > 0)
            _rigidbody.position = _spawnPoints[Random.Range(0, _spawnPoints.Length)].position;

        _headingIndex = Random.Range(0, Cardinals.Length);
        _rigidbody.rotation = Quaternion.LookRotation(Heading, Vector3.up);

        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;

        _lastPosition = _rigidbody.position;
        _stuckSteps = 0;
    }

    private void FixedUpdate()
    {
        if (IsWedged() || Clearance(Heading) < _probeDistance)
            _headingIndex = PickNewHeading();

        SteerTowardsHeading();
        MoveForward();
    }

    /// <summary>
    /// Prefere virar a dar meia-volta: reverter faz o hider refazer o próprio caminho e, num
    /// corredor, oscilar entre as duas pontas. A meia-volta fica reservada para beco sem saída.
    /// </summary>
    private int PickNewHeading()
    {
        float leftClearance = Clearance(Cardinals[LeftIndex]);
        float rightClearance = Clearance(Cardinals[RightIndex]);

        bool leftFree = leftClearance >= _probeDistance;
        bool rightFree = rightClearance >= _probeDistance;

        if (leftFree && rightFree)
            return Random.value < 0.5f ? LeftIndex : RightIndex;

        if (leftFree)
            return LeftIndex;

        if (rightFree)
            return RightIndex;

        if (Clearance(Cardinals[BackIndex]) >= _probeDistance)
            return BackIndex;

        // Cercado nos quatro lados: vai para o lado de maior folga, para ao menos se descolar
        // da parede em vez de continuar empurrando a mesma.
        return leftClearance >= rightClearance ? LeftIndex : RightIndex;
    }

    /// <summary>
    /// Detecta o travamento que os raios não veem: o corpo encaixado numa quina, onde a
    /// direção à frente lê como livre mas o collider não passa. Só conta steps em que ele já
    /// está alinhado com o rumo, senão uma curva normal — em que ele desacelera de propósito —
    /// seria confundida com travamento.
    /// </summary>
    private bool IsWedged()
    {
        Vector3 position = _rigidbody.position;
        float travelled = Vector3.Distance(position, _lastPosition);
        _lastPosition = position;

        Vector3 forward = _rigidbody.rotation * Vector3.forward;
        bool shouldBeMoving = Vector3.Dot(forward, Heading) > AlignedThreshold;

        float expected = _moveSpeed * Time.fixedDeltaTime * StuckSpeedFraction;
        if (!shouldBeMoving || travelled >= expected)
        {
            _stuckSteps = 0;
            return false;
        }

        if (++_stuckSteps < _stuckStepsThreshold)
            return false;

        _stuckSteps = 0;
        return true;
    }

    private void SteerTowardsHeading()
    {
        Quaternion target = Quaternion.LookRotation(Heading, Vector3.up);
        Quaternion next = Quaternion.RotateTowards(_rigidbody.rotation, target, _turnSpeed * Time.fixedDeltaTime);

        _rigidbody.MoveRotation(next);
    }

    private void MoveForward()
    {
        Vector3 forward = _rigidbody.rotation * Vector3.forward;

        float alignment = Vector3.Dot(forward, Heading);
        if (alignment <= 0f)
            return;

        _rigidbody.MovePosition(_rigidbody.position + _moveSpeed * alignment * Time.fixedDeltaTime * forward);
    }

    private float Clearance(Vector3 direction)
    {
        Vector3 origin = transform.position + Vector3.up * _probeHeightOffset;

        return Physics.Raycast(origin, direction, out RaycastHit hit, _probeDistance, _wallLayer)
            ? hit.distance
            : _probeDistance;
    }

    private static int NearestCardinal(Vector3 direction)
    {
        int best = 0;
        float bestDot = float.NegativeInfinity;

        for (int i = 0; i < Cardinals.Length; i++)
        {
            float dot = Vector3.Dot(direction, Cardinals[i]);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }

        return best;
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 origin = transform.position + Vector3.up * _probeHeightOffset;
        int headingIndex = Application.isPlaying ? _headingIndex : NearestCardinal(transform.forward);

        for (int i = 0; i < Cardinals.Length; i++)
        {
            bool free = !Application.isPlaying || Clearance(Cardinals[i]) >= _probeDistance;

            // Rumo atual em amarelo; os demais eixos em verde se livres, vermelho se fechados.
            Gizmos.color = i == headingIndex ? Color.yellow : (free ? Color.green : Color.red);
            Gizmos.DrawRay(origin, Cardinals[i] * _probeDistance);
        }
    }
}

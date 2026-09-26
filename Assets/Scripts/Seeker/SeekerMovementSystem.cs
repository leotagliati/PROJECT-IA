using UnityEngine;

namespace Assets.Scripts.Seeker
{
    public class SeekerMovementSystem : MonoBehaviour
    {
        [SerializeField] private Rigidbody _rigidbody;
        [SerializeField] private float _moveSpeed = 5f;
        [SerializeField] private float _turnSpeed = 720f;

        // Aceleração em m/s². 0 = velocidade instantânea (o comportamento antigo, que o Seeker
        // continua usando): cada ação vira 5 m/s na hora, e o corpo arranca, para e inverte
        // como um joystick digital. Com 20, sair do zero até 5 m/s leva 0.25 s e inverter de
        // sentido leva 0.5 s — ele passa a fazer CURVA em vez de quina, e o cone de visão (que
        // segue o corpo) varre o ambiente em vez de piscar. O GraphExplorer usa 20 no prefab.
        //
        // Efeito no treino: a política não consegue mais corrigir rota num único step, então
        // raspar parede fica um pouco mais comum no começo. Com Decision Period 5 (0.1 s por
        // decisão) a inércia de 0.25 s é da ordem de 2–3 decisões: o bastante para suavizar,
        // curto o bastante para a rede ainda sentir o efeito da própria ação.
        [SerializeField, Min(0f)] private float _acceleration = 0f;

        private Vector3 _velocity;

        public void Awake()
        {
            if (_rigidbody == null)
                _rigidbody = this.transform.parent.GetComponent<Rigidbody>();
        }

        public void Move(Vector3 direction)
        {
            Vector3 flat = new(direction.x, 0f, direction.z);

            if (_acceleration <= 0f)
            {
                MoveInstant(flat);
                return;
            }

            // Com inércia: a ação é a velocidade DESEJADA; a real persegue ela com aceleração
            // limitada. Ação zero também é um alvo (freia), por isso não sai cedo como o antigo.
            Vector3 desired = _moveSpeed * Vector3.ClampMagnitude(flat, 1f);
            _velocity = Vector3.MoveTowards(_velocity, desired, _acceleration * Time.fixedDeltaTime);

            if (_velocity.sqrMagnitude < 1e-6f)
                return;

            _rigidbody.MovePosition(_rigidbody.position + _velocity * Time.fixedDeltaTime);

            // Olha para onde ANDA (a velocidade real), não para onde a ação manda: é o que faz a
            // cabeça acompanhar a curva em vez de virar antes do corpo.
            Quaternion target = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(_rigidbody.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            _rigidbody.MoveRotation(next);
        }

        private void MoveInstant(Vector3 flat)
        {
            if (flat.sqrMagnitude < 1e-6f)
                return;

            Vector3 clamped = Vector3.ClampMagnitude(flat, 1f);

            Vector3 movement = _moveSpeed * Time.fixedDeltaTime * clamped;
            _rigidbody.MovePosition(_rigidbody.position + movement);

            Quaternion target = Quaternion.LookRotation(flat.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(_rigidbody.rotation, target, _turnSpeed * Time.fixedDeltaTime);

            _rigidbody.MoveRotation(next);
        }

        public void ResetMovement()
        {
            _velocity = Vector3.zero;
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
    }
}
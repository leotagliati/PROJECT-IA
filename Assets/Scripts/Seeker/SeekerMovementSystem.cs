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
        // segue o corpo) varre o ambiente em vez de piscar.
        //
        // O GraphExplorer usa 6 m/s com 15 m/s² no prefab: 0.4 s e ~1.2 m para parar. Histórico:
        //   20 m/s, 20 m/s² — 1 s e ~10 m para parar, 2 m por decisão num corpo de raio 0.69 m:
        //     frear antes da parede era impossível (43% do episódio encostado, node4_patrol_02);
        //   6 m/s, 40 m/s² — 0.15 s para parar, mas o corpo virava um joystick digital: a ação
        //     sorteada a cada 0.1 s mudava a velocidade quase inteira, e o passeio aleatório do
        //     começo do treino ficava tremendo no lugar. Nunca saía da sala de spawn, então nunca
        //     via uma recompensa positiva e aprendia a ficar parado (node4_search_03: cobertura
        //     2% -> 0.2%). O Arthur também achou rápido demais no WASD.
        // Com 15, cada decisão muda no máximo 1.5 m/s: o corpo tem MOMENTO, o passeio aleatório
        // anda em linha por mais tempo e atravessa portas, e o movimento fica mais natural. A
        // frenagem maior é coberta pelo steering (alcance = frenagem + margem).
        [SerializeField, Min(0f)] private float _acceleration = 0f;

        // Abaixo desta velocidade (m/s) o corpo NÃO gira. Parado ou encostado na parede a
        // velocidade fica quase zero e troca de direção a cada step — e o LookRotation de um
        // vetor minúsculo virava o corpo de um lado para o outro: o "beyblade" no lugar. Só
        // visual para os raios (presos ao mundo), mas o cone de visão da caça segue o corpo.
        [SerializeField, Min(0f)] private float _minTurnSpeed = 0.5f;

        [Header("-----Olhar (só com Move(direção, olhar))-----")]
        // Fator de velocidade andando TOTALMENTE de costas para onde o corpo olha. De frente e de
        // lado é 1; entre o lado e as costas cai linear até este valor. Sem custo nenhum, olhar e
        // andar seriam independentes e a política não teria motivo para olhar para onde vai;
        // com 0.6, olhar para trás é uma ESCOLHA que custa (vigiar porta, recuar olhando o hider).
        [SerializeField, Range(0.1f, 1f)] private float _backwardSpeedFactor = 0.6f;

        // Abaixo deste módulo o olhar pedido é "não mexa": o corpo mantém a direção atual. É o
        // jeito de a política dizer "continuo olhando para lá" sem precisar repetir o vetor.
        [SerializeField, Range(0f, 1f)] private float _lookDeadzone = 0.1f;

        [Header("-----Steering assistido-----")]
        // Folga (m) somada à distância de frenagem no alcance do SphereCast. A frenagem sozinha
        // (v²/2a = 1.2 m a 6 m/s com 15 m/s²) só enxerga a parede quando já não dá para fazer
        // curva; +0.5 m dá umas 2 decisões de antecedência.
        [SerializeField, Min(0f)] private float _steerMargin = 0.5f;

        // Troca o material do CapsuleCollider por um sem atrito no Awake (ver lá o porquê).
        [SerializeField] private bool _frictionlessBody = true;

        // Trava a posição Y e desliga a gravidade no Awake (ver lá o porquê). Só para mapa de um andar.
        [SerializeField] private bool _lockHeight = true;

        private Vector3 _velocity;
        private CapsuleCollider _capsule;

        // Força do assist (0..1) e máscara de parede: vêm do agente a cada episódio
        // (ConfigureSteering), porque o assist é parâmetro de currículo e a parede é do NavGraph.
        // 0 = sem assist, que é o que o Seeker antigo continua usando.
        private float _steerAssist;
        private LayerMask _wallLayer;

        public void Awake()
        {
            if (_rigidbody == null)
                _rigidbody = this.transform.parent.GetComponent<Rigidbody>();

            // Contínua: com Discrete, um corpo empurrado contra a quina step após step podia
            // terminar do outro lado da parede. Custa pouco (um agente por arena).
            _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // Rotação TODA travada para a física: só quem gira o corpo é o MoveRotation daqui
            // (olhar ou direção do movimento). O prefab travava só X e Z — e cada batida em parede
            // dava um torque em Y que, com Angular Drag 0.05, deixava o corpo rodando por segundos:
            // o "beyblade" físico, que nenhuma recompensa conseguia corrigir porque não vinha da ação.
            _rigidbody.freezeRotation = true;
            _rigidbody.angularVelocity = Vector3.zero;

            // ALTURA travada: o mapa tem um andar só, e numa batida a física às vezes jogava o
            // corpo para CIMA (a cápsula arredondada subia na quina/batente, ou o solver desgrudava
            // ele da parede na vertical) — e como a velocidade Y era preservada, ele saía voando.
            // Travado, o corpo fica na altura do spawn (nó + _nodeSpawnHeightOffset, ~a altura de
            // repouso) e a gravidade não serve para nada.
            if (_lockHeight)
            {
                _rigidbody.constraints |= RigidbodyConstraints.FreezePositionY;
                _rigidbody.useGravity = false;
            }

            _capsule = _rigidbody.GetComponent<CapsuleCollider>();

            // Corpo SEM atrito: com o material padrão (atrito 0.6), encostar na parede em diagonal
            // "agarrava" o corpo nela em vez de deslizar — o steering tirava a componente contra a
            // parede e o atrito comia a componente ao longo dela. Minimum: o atrito do par vira 0
            // qualquer que seja o material da parede. Criado aqui, e não como asset, para não
            // depender de wiring no prefab.
            if (_frictionlessBody && _capsule != null)
            {
                _capsule.sharedMaterial = new PhysicsMaterial("SemAtrito")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
        }

        /// <summary>
        /// Liga o steering assistido. <paramref name="assist"/> 0..1: fração da componente da
        /// velocidade CONTRA a parede que é removida quando ela está encostada (1 = desliza).
        /// </summary>
        public void ConfigureSteering(float assist, LayerMask walls)
        {
            _steerAssist = Mathf.Clamp01(assist);
            _wallLayer = walls;
        }

        // Dirige por VELOCIDADE, não por MovePosition. Num Rigidbody não-cinemático o MovePosition
        // teleporta sem varrer o caminho: o corpo entra na parede e o depenetrador empurra para
        // o lado mais curto — que numa quina ou parede fina às vezes é FORA do mapa (o agente
        // "clipava" para fora). Com velocidade, o solver para o corpo na superfície. O Y fica
        // com a gravidade.
        private void SetHorizontalVelocity(Vector3 horizontal)
        {
            // Com a altura travada, Y é sempre zero. Sem trava, nunca PARA CIMA: a gravidade pode
            // puxar, mas um empurrão de colisão não vira voo.
            float vertical = _lockHeight ? 0f : Mathf.Min(0f, _rigidbody.linearVelocity.y);
            _rigidbody.linearVelocity = new Vector3(horizontal.x, vertical, horizontal.z);
        }

        public void Move(Vector3 direction)
        {
            Vector3 flat = new(direction.x, 0f, direction.z);

            if (_acceleration <= 0f)
            {
                MoveInstant(flat);
                return;
            }

            AccelerateTowards(Steer(_moveSpeed * Vector3.ClampMagnitude(flat, 1f)));

            if (_velocity.sqrMagnitude < _minTurnSpeed * _minTurnSpeed)
                return;

            // Olha para onde ANDA (a velocidade real), não para onde a ação manda: é o que faz a
            // cabeça acompanhar a curva em vez de virar antes do corpo.
            Quaternion target = Quaternion.LookRotation(_velocity.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(_rigidbody.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            _rigidbody.MoveRotation(next);
        }

        /// <summary>
        /// Andar e olhar SEPARADOS (GraphExplorer): o corpo — e o cone de visão, que segue o
        /// corpo — gira para <paramref name="look"/>, não para onde anda. Andar de costas para o
        /// olhar custa velocidade (_backwardSpeedFactor). Olhar abaixo de _lookDeadzone mantém a
        /// direção atual. Exige _acceleration > 0 (o modo instantâneo é só do Seeker antigo).
        /// </summary>
        public void Move(Vector3 direction, Vector3 look)
        {
            Vector3 flat = Vector3.ClampMagnitude(new Vector3(direction.x, 0f, direction.z), 1f);

            // Pelo forward ATUAL, não pelo olhar pedido: virar primeiro e andar depois é o que
            // devolve a velocidade cheia — o custo é o tempo de giro, que é o que queremos cobrar.
            Vector3 forward = _rigidbody.rotation * Vector3.forward;
            forward.y = 0f;
            float speedFactor = 1f;
            if (flat.sqrMagnitude > 1e-6f && forward.sqrMagnitude > 1e-6f)
            {
                float alignment = Vector3.Dot(forward.normalized, flat.normalized);
                if (alignment < 0f)
                    speedFactor = Mathf.Lerp(1f, _backwardSpeedFactor, -alignment);
            }

            AccelerateTowards(Steer(_moveSpeed * speedFactor * flat));

            Vector3 flatLook = new(look.x, 0f, look.z);
            if (flatLook.magnitude < _lookDeadzone)
                return;

            Quaternion target = Quaternion.LookRotation(flatLook.normalized, Vector3.up);
            Quaternion next = Quaternion.RotateTowards(_rigidbody.rotation, target, _turnSpeed * Time.fixedDeltaTime);
            _rigidbody.MoveRotation(next);
        }

        // Com inércia: a ação é a velocidade DESEJADA; a real persegue ela com aceleração
        // limitada. Ação zero também é um alvo (freia), por isso não sai cedo como o antigo.
        private void AccelerateTowards(Vector3 desired)
        {
            _velocity = Vector3.MoveTowards(_velocity, desired, _acceleration * Time.fixedDeltaTime);

            // A velocidade do corpo persiste entre steps: ação zero tem que frear explicitamente.
            SetHorizontalVelocity(_velocity);
        }

        /// <summary>
        /// STEERING ASSISTIDO: tira da velocidade desejada a componente que vai CONTRA a parede à
        /// frente, na proporção assist x proximidade (0 no limite do alcance, 1 encostado). Com
        /// assist 1 o corpo desliza pela parede em vez de bater; com 0 a velocidade passa intacta.
        ///
        /// Por que existe: o node4_patrol_02 passou 43% do episódio encostado — aprender a não
        /// bater comia milhões de steps que deviam ir para a estratégia. O currículo (steer_assist)
        /// começa em 1 e desce, e a rede assume o controle fino aos poucos.
        ///
        /// SphereCast do tamanho do corpo, alcance = frenagem na velocidade atual + _steerMargin.
        /// Raio a 90% do corpo: um SphereCast que JÁ nasce dentro da parede não a acusa, e
        /// encostado é justamente quando mais precisamos dele. Duas iterações, para a quina: a
        /// primeira desliza pela parede A, a segunda impede que o deslize entre na parede B.
        /// </summary>
        private Vector3 Steer(Vector3 desired)
        {
            if (_steerAssist <= 0f || _wallLayer.value == 0 || _capsule == null || _acceleration <= 0f)
                return desired;

            Vector3 origin = _capsule.bounds.center;
            Vector3 scale = _capsule.transform.lossyScale;
            float radius = 0.9f * _capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            float speed = _velocity.magnitude;
            float range = speed * speed / (2f * _acceleration) + _steerMargin;

            for (int iteration = 0; iteration < 2; iteration++)
            {
                if (desired.sqrMagnitude < 1e-6f)
                    break;

                if (!Physics.SphereCast(origin, radius, desired.normalized, out RaycastHit hit, range,
                        _wallLayer, QueryTriggerInteraction.Ignore))
                    break;

                Vector3 normal = new(hit.normal.x, 0f, hit.normal.z);
                if (normal.sqrMagnitude < 1e-6f)
                    break;
                normal.Normalize();

                float into = -Vector3.Dot(desired, normal);
                if (into <= 0f)
                    break;

                float closeness = 1f - Mathf.Clamp01(hit.distance / range);
                desired += normal * (into * _steerAssist * closeness);
            }

            return desired;
        }

        private void MoveInstant(Vector3 flat)
        {
            if (flat.sqrMagnitude < 1e-6f)
            {
                SetHorizontalVelocity(Vector3.zero);
                return;
            }

            Vector3 clamped = Vector3.ClampMagnitude(flat, 1f);

            SetHorizontalVelocity(_moveSpeed * clamped);

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
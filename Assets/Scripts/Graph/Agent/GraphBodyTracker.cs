using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Sinais do PRÓPRIO corpo do seeker a cada step: contato com PAREDE (paredes e móveis) e com PORTA (as peças
    /// Door_Hole, que custam menos), BATIDAS (início de contato, com janela recente para o custo escalonado),
    /// mudança de ação/olhar (suavidade) e tempo parado.
    /// Classe simples, não componente: o OnCollisionStay chega no GameObject do Manager, que repassa aqui
    /// (<see cref="MarkContact"/>); o resto é estado entre steps que não é de nenhum outro sistema.
    ///
    /// Ciclo por step (OnActionReceived): <see cref="BeginStep"/> antes de montar o contexto (a batida e a
    /// mudança de ação DESTE step entram na conta dele) e <see cref="EndStep"/> depois de cobrar.
    /// </summary>
    public class GraphBodyTracker
    {
        // Janela da "batida seguida", em steps de física. 250 = 5 s.
        private const int HitWindowSteps = 250;

        // Steps SEM contato para o próximo contato contar como batida nova; sem isso, raspar a parede
        // (contato pisca a cada step) viraria dezenas de batidas.
        private const int HitDebounceSteps = 10;

        // Abaixo disto (m/s) conta como parado (Movement/IdleFraction). Só medição: parar é permitido; a
        // métrica pega o ótimo local "fico quieto e não perco nada".
        private const float IdleSpeed = 0.5f;

        private readonly Queue<int> _recentHitSteps = new Queue<int>();
        private int _step;
        private int _stepsWithoutContact;

        private Vector2 _lastMove;
        private Vector2 _lastLook;
        private bool _hasLastAction;

        // Métricas do episódio.
        private int _contactSteps;
        private int _doorContactSteps;

        // Batente (peça Door_Hole, pelo NOME): só métrica, não muda custo. Desde 06/10 a porta é layer Wall e paga
        // como parede, então o IsTouchingDoor (layer Door) não pega mais nada; isto mede quanto ele raspa a porta.
        private bool _touchingDoorFrame;
        private int _doorFrameSteps;
        private int _doorFrameHits;
        private int _stepsWithoutDoorFrame;
        private int _idleSteps;
        private int _episodeHits;
        private float _moveChangeSum;
        private float _lookChangeSum;
        private int _decisionCount;

        /// <summary>Encostado em PAREDE (paredes e móveis) no último step de física: custo cheio.</summary>
        public bool IsTouchingWall { get; private set; }

        /// <summary>Encostado em PORTA (layer Door, batente) no último step de física: custo reduzido.</summary>
        public bool IsTouchingDoor { get; private set; }

        /// <summary>Encostado em qualquer parede, porta incluída (observação [8]).</summary>
        public bool IsTouchingAnyWall => IsTouchingWall || IsTouchingDoor;

        /// <summary>Batidas NOVAS neste step (0 ou 1).</summary>
        public int HitsThisStep { get; private set; }

        /// <summary>A batida deste step foi só em porta (nenhuma parede junto): paga o custo reduzido.</summary>
        public bool HitIsDoorOnly { get; private set; }

        /// <summary>Batidas dentro da janela, incluindo a deste step.</summary>
        public int RecentHits => _recentHitSteps.Count;

        /// <summary>|Δandar|² neste step (zero entre decisões: a ação se repete).</summary>
        public float MoveChangeSq { get; private set; }

        /// <summary>|Δolhar|² neste step.</summary>
        public float LookChangeSq { get; private set; }

        public void ResetEpisode()
        {
            IsTouchingWall = false;
            IsTouchingDoor = false;
            HitIsDoorOnly = false;
            _recentHitSteps.Clear();
            _step = 0;
            _stepsWithoutContact = HitDebounceSteps;
            HitsThisStep = 0;
            _hasLastAction = false;
            MoveChangeSq = 0f;
            LookChangeSq = 0f;
            _contactSteps = 0;
            _doorContactSteps = 0;
            _touchingDoorFrame = false;
            _doorFrameSteps = 0;
            _doorFrameHits = 0;
            _stepsWithoutDoorFrame = HitDebounceSteps;
            _idleSteps = 0;
            _episodeHits = 0;
            _moveChangeSum = 0f;
            _lookChangeSum = 0f;
            _decisionCount = 0;
        }

        /// <summary>
        /// Do OnCollisionStay: só marca (ele dispara por collider a cada step; quem cobra é o step, uma vez, mesmo
        /// tocando três paredes numa quina).
        /// </summary>
        public void MarkContact(bool door)
        {
            if (door)
                IsTouchingDoor = true;
            else
                IsTouchingWall = true;
        }

        /// <summary>Do OnCollisionStay, além do MarkContact: o collider é um batente (Door_Hole). Só métrica.</summary>
        public void MarkDoorFrame() => _touchingDoorFrame = true;

        /// <summary>Início do step: batida (pelo contato do step de física que acabou) e mudança de ação.</summary>
        public void BeginStep(Vector2 move, Vector2 look)
        {
            // Batida = início de contato com parede OU porta; se só a porta foi tocada, ela custa o reduzido.
            StepHits(IsTouchingAnyWall);
            HitIsDoorOnly = HitsThisStep > 0 && !IsTouchingWall;

            MoveChangeSq = _hasLastAction ? (move - _lastMove).sqrMagnitude : 0f;
            LookChangeSq = _hasLastAction ? (look - _lastLook).sqrMagnitude : 0f;
            if (_hasLastAction && (MoveChangeSq > 0f || LookChangeSq > 0f))
            {
                _moveChangeSum += MoveChangeSq;
                _lookChangeSum += LookChangeSq;
                _decisionCount++;
            }

            _lastMove = move;
            _lastLook = look;
            _hasLastAction = true;
        }

        /// <summary>Fim do step, depois de cobrar: conta contato e tempo parado e consome as flags de contato.</summary>
        public void EndStep(Rigidbody body)
        {
            if (IsTouchingWall)
                _contactSteps++;
            else if (IsTouchingDoor)
                _doorContactSteps++;

            // Batente: steps encostado e batidas (início de contato, com o mesmo debounce das paredes).
            if (_touchingDoorFrame)
            {
                _doorFrameSteps++;
                if (_stepsWithoutDoorFrame >= HitDebounceSteps)
                    _doorFrameHits++;
                _stepsWithoutDoorFrame = 0;
            }
            else
            {
                _stepsWithoutDoorFrame++;
            }

            _touchingDoorFrame = false;

            if (body != null)
            {
                Vector3 velocity = body.linearVelocity;
                if (velocity.x * velocity.x + velocity.z * velocity.z < IdleSpeed * IdleSpeed)
                    _idleSteps++;
            }

            IsTouchingWall = false;
            IsTouchingDoor = false;
        }

        private void StepHits(bool touching)
        {
            _step++;
            HitsThisStep = 0;

            while (_recentHitSteps.Count > 0 && _step - _recentHitSteps.Peek() > HitWindowSteps)
                _recentHitSteps.Dequeue();

            if (!touching)
            {
                _stepsWithoutContact++;
                return;
            }

            if (_stepsWithoutContact >= HitDebounceSteps)
            {
                HitsThisStep = 1;
                _episodeHits++;
                _recentHitSteps.Enqueue(_step);
            }

            _stepsWithoutContact = 0;
        }

        /// <summary>Métricas do corpo (Exploration/WallContactFraction, DoorContactFraction, WallHits, Movement/IdleFraction, *Jitter).</summary>
        public void RecordStats(StatsRecorder stats, int elapsedSteps)
        {
            if (elapsedSteps > 0)
            {
                stats.Add("Exploration/WallContactFraction", (float)_contactSteps / elapsedSteps);
                // Batente pelo nome (Door_Hole), não pela layer Door (vazia desde 06/10).
                stats.Add("Exploration/DoorContactFraction", (float)_doorFrameSteps / elapsedSteps);
                stats.Add("Movement/IdleFraction", (float)_idleSteps / elapsedSteps);
            }

            stats.Add("Exploration/WallHits", _episodeHits);
            stats.Add("Exploration/DoorHits", _doorFrameHits);

            if (_decisionCount > 0)
            {
                stats.Add("Movement/ActionJitter", _moveChangeSum / _decisionCount);
                stats.Add("Movement/LookJitter", _lookChangeSum / _decisionCount);
            }
        }
    }
}

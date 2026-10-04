using System;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Fôlego da corrida, no MESMO modelo do PlayerStamina do jogador (main, PlayerDummy): o tanque cheio
    /// dura <see cref="_seconds"/> de corrida; parou de correr, espera <see cref="_regenDelay"/> e enche do
    /// zero em <see cref="_refillSeconds"/>. Esvaziou: só volta a correr com <see cref="_minFractionToRun"/>
    /// do tanque (senão a corrida pisca a cada step). Usado pelo hider (GraphHider), que faz o papel do jogador:
    /// "o hider tem mais fôlego" é só um número maior. O seeker não tem fôlego: a velocidade dele vem do estado de
    /// alerta (GraphLocomotion).
    /// </summary>
    [Serializable]
    public class GraphStamina
    {
        // Segundos de corrida com o tanque cheio. 10 = PlayerStamina.sprintDuration do PlayerDummy. 0 = nunca corre.
        [SerializeField, Min(0f)] private float _seconds = 10f;

        // Segundos para encher do zero, depois que a recuperação começa (PlayerStamina.refillDuration).
        [SerializeField, Min(0.1f)] private float _refillSeconds = 6f;

        // Espera, em segundos, entre parar de correr e começar a recuperar (PlayerStamina.regenDelay).
        [SerializeField, Min(0f)] private float _regenDelay = 1f;

        // Fração do tanque para voltar a correr depois de esvaziar (PlayerStamina.recoverThreshold).
        [SerializeField, Range(0f, 1f)] private float _minFractionToRun = 0.3f;

        private float _capacity;
        private float _left;
        private float _sinceRun;
        private bool _exhausted;

        /// <summary>0..1 do tanque (0 sem tanque).</summary>
        public float Fraction => _capacity > 0f ? _left / _capacity : 0f;

        /// <summary>Esvaziou e ainda não recuperou o mínimo para correr.</summary>
        public bool Exhausted => _exhausted;

        /// <summary>Enche o tanque. <paramref name="seconds"/> &lt;= 0 usa o do Inspector.</summary>
        public void Reset(float seconds = 0f)
        {
            _capacity = seconds > 0f ? seconds : _seconds;
            _left = _capacity;
            _sinceRun = _regenDelay;
            _exhausted = false;
        }

        /// <summary>Um step: devolve se CORRE neste step (quer correr e pode) e gasta ou recupera.</summary>
        public bool Step(bool wantsToRun, float deltaTime)
        {
            bool running = wantsToRun && !_exhausted && _left > 0f;
            if (running)
            {
                _sinceRun = 0f;
                _left = Mathf.Max(0f, _left - deltaTime);
                if (_left <= 0f)
                    _exhausted = true;
                return true;
            }

            _sinceRun += deltaTime;
            if (_sinceRun >= _regenDelay)
                _left = Mathf.Min(_capacity, _left + _capacity / _refillSeconds * deltaTime);

            if (_exhausted && _left >= _minFractionToRun * _capacity)
                _exhausted = false;

            return false;
        }
    }
}

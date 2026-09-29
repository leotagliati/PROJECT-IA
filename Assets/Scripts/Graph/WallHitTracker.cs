using System.Collections.Generic;

namespace Assets.Scripts.Graph
{
    /// <summary>
    /// Conta BATIDAS em parede (início de um contato, não cada step encostado) e quantas houve
    /// numa janela recente. É o estado que a punição escalonada precisa: bater uma vez é
    /// acidente; bater três vezes em cinco segundos é o agente ricocheteando pelo corredor —
    /// e cada batida seguida passa a custar mais (GraphRewardSystem._wallHitPenalty).
    ///
    /// Classe simples, e não MonoBehaviour: quem recebe o OnCollisionStay é o GameObject do
    /// Rigidbody (o do Manager); aqui só mora a memória entre steps, fora do Manager.
    /// </summary>
    public class WallHitTracker
    {
        // Janela da "batida seguida", em steps de física. 250 = 5 s.
        private readonly int _windowSteps;

        // Steps SEM contato para o próximo contato contar como batida nova. Sem isto, deslizar
        // raspando a parede (o contato pisca a cada step) viraria dezenas de "batidas".
        private readonly int _debounceSteps;

        private readonly Queue<int> _recentHitSteps = new Queue<int>();
        private int _step;
        private int _stepsWithoutContact;

        public WallHitTracker(int windowSteps = 250, int debounceSteps = 10)
        {
            _windowSteps = windowSteps;
            _debounceSteps = debounceSteps;
        }

        /// <summary>Batidas NOVAS neste step (0 ou 1).</summary>
        public int HitsThisStep { get; private set; }

        /// <summary>Batidas dentro da janela, incluindo a deste step.</summary>
        public int RecentHits => _recentHitSteps.Count;

        /// <summary>Batidas no episódio inteiro (métrica).</summary>
        public int EpisodeHits { get; private set; }

        public void Reset()
        {
            _recentHitSteps.Clear();
            _step = 0;
            _stepsWithoutContact = _debounceSteps;
            HitsThisStep = 0;
            EpisodeHits = 0;
        }

        /// <summary>Um step de física: estava encostado em parede?</summary>
        public void Step(bool touching)
        {
            _step++;
            HitsThisStep = 0;

            while (_recentHitSteps.Count > 0 && _step - _recentHitSteps.Peek() > _windowSteps)
                _recentHitSteps.Dequeue();

            if (!touching)
            {
                _stepsWithoutContact++;
                return;
            }

            if (_stepsWithoutContact >= _debounceSteps)
            {
                HitsThisStep = 1;
                EpisodeHits++;
                _recentHitSteps.Enqueue(_step);
            }

            _stepsWithoutContact = 0;
        }
    }
}

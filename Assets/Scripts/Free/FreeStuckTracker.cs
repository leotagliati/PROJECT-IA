namespace Assets.Scripts.Free
{
    /// <summary>
    /// PRESO NA PAREDE: encostado em parede, pedindo para andar e quase parado, por mais de GraceSteps seguidos. É o
    /// "empacou na quina / no batente" que o custo de contato sozinho não separa de "raspou a parede andando".
    /// Classe simples (o contato chega pelo OnCollisionStay do Manager, via GraphBodyTracker); um por agente.
    /// </summary>
    public class FreeStuckTracker
    {
        // Abaixo disto (m/s), pedindo para andar, não está saindo do lugar. A patrulha anda a 7.
        private const float StuckSpeed = 1f;

        // 25 steps = 0.5 s: virar contra a parede numa curva dura menos que isso e não é "preso".
        private const int GraceSteps = 25;

        private int _run;
        private int _stuckSteps;
        private int _steps;

        /// <summary>Preso neste step (já passou da carência).</summary>
        public bool IsStuck => _run > GraceSteps;

        /// <summary>Quão preso, 0..1 (1 = 2 s ou mais), para a observação.</summary>
        public float Level => _run <= 0 ? 0f : System.Math.Min(1f, _run / 100f);

        /// <summary>Fração do episódio presa (Movement/StuckFraction).</summary>
        public float StuckFraction => _steps > 0 ? (float)_stuckSteps / _steps : 0f;

        public void ResetEpisode()
        {
            _run = 0;
            _stuckSteps = 0;
            _steps = 0;
        }

        /// <param name="touchingWall">Encostado em parede no último step de física.</param>
        /// <param name="wantsToMove">A ação pede para andar (acima da zona morta).</param>
        /// <param name="speed">Velocidade real no plano (m/s), depois da física.</param>
        public void Tick(bool touchingWall, bool wantsToMove, float speed)
        {
            _steps++;
            if (touchingWall && wantsToMove && speed < StuckSpeed)
                _run++;
            else
                _run = 0;

            if (IsStuck)
                _stuckSteps++;
        }
    }
}

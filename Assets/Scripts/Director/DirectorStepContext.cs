using Assets.Scripts.Free;

namespace Assets.Scripts.Director
{
    /// <summary>Tudo que a recompensa do diretor precisa saber de um step de física. Montado pelo DirectorAgent.</summary>
    public struct DirectorStepContext
    {
        public float DeltaSeconds;

        // Close call contado neste step, quantos já houve (contando este) e segundos desde o anterior.
        public bool CloseCall;
        public int CloseCallCount;
        public float SecondsSincePreviousCloseCall;

        // Segundos seguidos com a tensão baixa, e a tensão agora (0..1).
        public float BoredSeconds;
        public float Tension;

        // Intervenções feitas neste step.
        public bool Attracted;
        public bool Decoyed;
        public bool Highlighted;

        // Quanto as alavancas da IA estão fora do normal agora (|escala - 1|).
        public float SpeedOffset;
        public float ViewOffset;
        public float StaminaOffset;
        public float FootstepOffset;

        // Habilidade VERDADEIRA do jogador do treino (0 ruim .. 1 bom; 0.5 sem perfil). Só a recompensa vê: o diretor
        // tem que deduzir pelo jogo, como faria com um jogador de verdade.
        public float PlayerSkill;

        // Fim do episódio (do monstro), com o motivo e a duração.
        public bool Ended;
        public FreeEpisodeOutcome Outcome;
        public float ElapsedSeconds;
    }
}

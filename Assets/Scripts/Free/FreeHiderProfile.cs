using UnityEngine;

namespace Assets.Scripts.Free
{
    public enum FreeHiderStyle { Balanced, Bold, Timid, Fearful }

    /// <summary>
    /// O JOGADOR que a presa imita numa partida (treino do diretor, player_profiles): HABILIDADE de 0 a 1 (ruim, médio,
    /// bom) e ESTILO. Tudo multiplica ou substitui os números do FreeHider; Default deixa a presa como sempre foi.
    /// </summary>
    public struct FreeHiderProfile
    {
        public bool Active;
        public float Skill;
        public FreeHiderStyle Style;

        // Percebe o monstro (raio e vista da fuga) e a que distância sai de fininho do esconderijo com ele chegando
        // (o bom sai cedo e em silêncio; o ruim espera ser achado).
        public float AwarenessScale;
        public float HiddenNerveScale;

        // Fuga: bônus de sair da vista, pontos sorteados e replanejar.
        public float EvasionScale;

        // Corre só exposta ou com o monstro perto, e guarda reserva.
        public bool SavesStamina;

        // Anda agachada (silenciosa) com o monstro a menos disto (m), o alcance do barulho do passo; 0 = nunca.
        public float SneakRadius;

        // Passeio: chance de ir à chave, de ir a esconderijo, tamanho das pausas.
        public float KeySeekChance;
        public float HideChance;
        public float PauseScale;

        // Depois de despistar: chance de se esconder (contra este monstro esconder é perigoso: o bom se esconde menos).
        public float HideAfterFleeChance;
        public float HideTimeScale;

        // Esconderijos comparados (mais = escolhe melhor).
        public float HideSearchScale;

        // Fôlego útil: quem joga bem administra a corrida.
        public float StaminaScale;

        // Escolha da chave: quanto pesa ela estar longe do monstro (m de caminho por m de distância dele; 0 = a mais perto).
        public float KeyThreatWeight;

        // Conhece a chave a até esta distância (m), ou vendo-a de perto; o resto ela tem de achar (ou o diretor destaca).
        public float KeyKnowRadius;

        // EXPERT: o jogador que ganharia fácil (chaves rápido, longe do monstro quase sempre). É com ele que o diretor
        // aprende a ajudar a IA.
        public bool Expert;

        public string SkillLabel => Expert ? "Expert" : Skill < 1f / 3f ? "Ruim" : Skill < 2f / 3f ? "Medio" : "Bom";

        // Fração das partidas com o jogador Expert.
        private const float ExpertChance = 0.2f;

        public static FreeHiderProfile Sample()
        {
            if (Random.value < ExpertChance)
                return SampleExpert();

            float skill = Random.value;
            var style = (FreeHiderStyle)Random.Range(0, 4);
            var profile = new FreeHiderProfile
            {
                Active = true,
                Skill = skill,
                Style = style,
                AwarenessScale = Mathf.Lerp(0.4f, 2.4f, skill),
                HiddenNerveScale = Mathf.Lerp(0.8f, 2.4f, skill),
                EvasionScale = Mathf.Lerp(0.4f, 3f, skill),
                SavesStamina = skill > 0.5f || Random.value < skill * 0.5f,
                SneakRadius = Mathf.Lerp(0f, 9f, skill),
                KeySeekChance = 0.7f,
                HideChance = 0.5f,
                PauseScale = 1f,
                HideAfterFleeChance = Mathf.Lerp(0.9f, 0.25f, skill),
                HideTimeScale = 1f,
                HideSearchScale = Mathf.Lerp(0.4f, 2f, skill),
                StaminaScale = Mathf.Lerp(0.7f, 1.3f, skill),
                KeyThreatWeight = Mathf.Lerp(0f, 0.6f, skill),
                KeyKnowRadius = Mathf.Lerp(8f, 25f, skill),
            };

            switch (style)
            {
                // Vai direto às chaves, se esconde pouco e deixa o monstro chegar perto antes de fugir.
                case FreeHiderStyle.Bold:
                    profile.KeySeekChance = 0.95f;
                    profile.HideChance = 0.2f;
                    profile.PauseScale = 0.4f;
                    profile.AwarenessScale *= 0.75f;
                    profile.HiddenNerveScale *= 0.6f;
                    profile.HideAfterFleeChance *= 0.6f;
                    profile.HideTimeScale = 0.6f;
                    break;

                // Esconde muito, para muito, demora com as chaves.
                case FreeHiderStyle.Timid:
                    profile.KeySeekChance = 0.35f;
                    profile.HideChance = 0.85f;
                    profile.PauseScale = 2f;
                    profile.HideAfterFleeChance = 1f;
                    profile.HideTimeScale = 1.6f;
                    profile.SneakRadius = Mathf.Max(profile.SneakRadius, 9f);
                    break;

                // Foge de longe, sai do esconderijo cedo e queima o fôlego.
                case FreeHiderStyle.Fearful:
                    profile.AwarenessScale *= 1.5f;
                    profile.HiddenNerveScale *= 2f;
                    profile.SavesStamina = false;
                    profile.HideAfterFleeChance *= 0.7f;
                    profile.HideTimeScale = 0.7f;
                    break;
            }

            return profile;
        }

        // Vai direto às chaves pela que está longe do monstro, percebe ele de muito longe e se afasta andando, sem pausa
        // e sem se esconder, e corre só quando precisa.
        private static FreeHiderProfile SampleExpert() => new FreeHiderProfile
        {
            Active = true,
            Expert = true,
            Skill = 1f,
            Style = FreeHiderStyle.Balanced,
            AwarenessScale = 3f,
            HiddenNerveScale = 3f,
            EvasionScale = 3f,
            SavesStamina = true,
            SneakRadius = 9f,
            KeySeekChance = 1f,
            HideChance = 0.05f,
            PauseScale = 0.1f,
            HideAfterFleeChance = 0.05f,
            HideTimeScale = 0.5f,
            HideSearchScale = 2f,
            StaminaScale = 1.5f,
            KeyThreatWeight = 1.2f,
            KeyKnowRadius = 1000f,
        };
    }
}

using System;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// BARULHO que o monstro da v8 pode ouvir. Quem faz barulho chama <see cref="Emit"/> com o ponto e o raio; cada
    /// monstro (<see cref="FreeTrackSense"/>) decide se ouviu: o raio é medido PELO NAVMESH (o som contorna parede e
    /// passa por porta, não atravessa o mapa em linha reta), e o que ele ouve vira PISTA, a mesma da pegada: Alerta,
    /// foco na caça e a observação [30..35]. Por isso som não muda o cérebro: um .onnx treinado sem som roda com ele.
    ///   TREINO  o alvo correndo (FreeFootprintTrail: a audição de corrida da v5).
    ///   JOGO    o jogador correndo, e o que o jogo quiser: armadilha, item jogado (chame Emit no impacto).
    /// Som não diz QUEM fez: um item jogado longe atrai o monstro para lá como se fosse o jogador.
    /// </summary>
    public static class FreeNoise
    {
        /// <summary>Ponto do som e raio (m, pelo NavMesh) em que ele é ouvido.</summary>
        public static event Action<Vector3, float> Emitted;

        /// <summary>Faz barulho em <paramref name="position"/>, ouvido a até <paramref name="radius"/> m pelo NavMesh.</summary>
        public static void Emit(Vector3 position, float radius)
        {
            if (radius > 0f)
                Emitted?.Invoke(position, radius);
        }

        // Com o domain reload desligado nas Play Mode Options, o evento estático sobrevive ao Stop com ouvintes mortos.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => Emitted = null;
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Free
{
    /// <summary>
    /// Os ACONTECIMENTOS recentes que o monstro da v9 percebeu, para o BufferSensor "Events" (FreeObservations). É a
    /// lista genérica que deixa mecânica nova entrar sem mudar o cérebro: um tipo novo é só um índice a mais no one-hot
    /// (tipos reservados já existem com zero), e o treino continua com --resume e uma lição nova.
    /// Guarda os _capacity mais novos e esquece o que passou de _maxAgeSeconds. Classe simples, uma por agente.
    /// </summary>
    public class FreeEventLog
    {
        /// <summary>
        /// Tipos (o índice do one-hot na observação). 0..2 em uso na v9; 3..11 RESERVADOS: o nome é só a ideia do
        /// brainstorm de 12/10, nada os emite ainda. Não reordene: o índice é o contrato com os .onnx.
        /// </summary>
        public enum Type
        {
            Noise = 0,          // som ouvido (FreeNoise: passo correndo, armadilha, item jogado)
            KeyTaken = 1,       // soube que uma chave foi levada (FreeKeyField, com atraso)
            CloneSaw = 2,       // o clone viu o alvo (FreeCloneAbility)
            Stun = 3,           // reservado: o jogador atordoou o monstro
            DoorOpened = 4,     // reservado: porta abriu
            DoorClosed = 5,     // reservado: porta fechou
            Trap = 6,           // reservado: armadilha disparou (se um dia for diferente de som)
            ThrownItem = 7,     // reservado: item jogado caiu
            Reserved8 = 8,
            Reserved9 = 9,
            Reserved10 = 10,
            Reserved11 = 11,
        }

        public const int TypeCount = 12;

        public struct Entry
        {
            public Type Type;
            public Vector3 Position;
            public float Time;
            public float Strength;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly int _capacity;
        private readonly float _maxAgeSeconds;

        public FreeEventLog(int capacity, float maxAgeSeconds)
        {
            _capacity = Mathf.Max(1, capacity);
            _maxAgeSeconds = Mathf.Max(0.1f, maxAgeSeconds);
        }

        public float MaxAgeSeconds => _maxAgeSeconds;

        public int Count => _entries.Count;

        /// <summary>A i-ésima, da MAIS NOVA (0) para a mais velha.</summary>
        public Entry GetNewest(int i) => _entries[_entries.Count - 1 - i];

        public void Clear() => _entries.Clear();

        public void Add(Type type, Vector3 position, float strength = 1f)
        {
            _entries.Add(new Entry { Type = type, Position = position, Time = UnityEngine.Time.time, Strength = Mathf.Clamp01(strength) });
            if (_entries.Count > _capacity)
                _entries.RemoveAt(0);
        }

        /// <summary>Esquece o que passou da idade máxima. Chamar uma vez por decisão, antes de observar.</summary>
        public void Prune()
        {
            float oldest = UnityEngine.Time.time - _maxAgeSeconds;
            while (_entries.Count > 0 && _entries[0].Time < oldest)
                _entries.RemoveAt(0);
        }
    }
}

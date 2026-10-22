using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Uma conversa: falas em ordem, cada uma com quem fala. Texto fora da cena, reaproveitável.</summary>
[CreateAssetMenu(fileName = "Dialogue", menuName = "Dialogue/Dialogue")]
public class DialogueAsset : ScriptableObject
{
    [Serializable]
    public struct Line
    {
        [Tooltip("Vazio = sem nome na caixa.")]
        public string speaker;

        [TextArea(2, 5)]
        public string text;

        [Tooltip("Entrada da AudioLibrary tocada quando esta fala começa (um barulho ao fundo, por exemplo). Vazio = sem som.")]
        public string soundId;
    }

    [SerializeField] private Line[] lines;

    public IReadOnlyList<Line> Lines => lines ?? Array.Empty<Line>();
}

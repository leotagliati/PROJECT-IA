using System;
using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
    // Evento disparado quando o número de chaves muda
    public static event Action<int> OnKeyCountChanged;

    [SerializeField] private int keyCount = 0;
    public int KeyCount => keyCount;

    public void AddKey(int amount = 1)
    {
        keyCount += amount;
        OnKeyCountChanged?.Invoke(keyCount);
    }

    public bool TryUseKey(int amount = 1)
    {
        if (keyCount >= amount)
        {
            keyCount -= amount;
            OnKeyCountChanged?.Invoke(keyCount);
            return true;
        }
        return false;
    }
}
using System.Collections.Generic;
using UnityEngine;

public class KeyInventoryUI : MonoBehaviour
{
    [SerializeField] private GameObject keyIconPrefab;
    [SerializeField] private Transform iconsContainer;

    private readonly List<GameObject> spawnedIcons = new List<GameObject>();

    private void Awake()
    {
        if (iconsContainer == null)
            iconsContainer = transform;
    }

    private void OnEnable()
    {
        PlayerInventory.OnKeyCountChanged += UpdateKeyDisplay;
    }

    private void OnDisable()
    {
        PlayerInventory.OnKeyCountChanged -= UpdateKeyDisplay;
    }

    private void UpdateKeyDisplay(int totalKeys)
    {
        // Se pegou mais chaves do que tem ícones na tela, instancia novos ícones
        while (spawnedIcons.Count < totalKeys)
        {
            GameObject newIcon = Instantiate(keyIconPrefab, iconsContainer);
            spawnedIcons.Add(newIcon);
        }

        // Se gastou chaves, remove ícones excedentes
        while (spawnedIcons.Count > totalKeys)
        {
            int lastIndex = spawnedIcons.Count - 1;
            Destroy(spawnedIcons[lastIndex]);
            spawnedIcons.RemoveAt(lastIndex);
        }
    }
}
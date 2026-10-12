using UnityEngine;

/// <summary>Disco e anel gerados em runtime, com borda suavizada, para UI sem asset.</summary>
public static class UIShapes
{
    private const int Size = 128;

    private static Sprite disc;
    private static Sprite ring;

    public static Sprite Disc => disc != null ? disc : disc = Build(0f);

    public static Sprite Ring => ring != null ? ring : ring = Build(0.78f);

    private static Sprite Build(float innerRatio)
    {
        var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.DontSave
        };

        float outer = Size * 0.5f;
        float inner = outer * innerRatio;
        var pixels = new Color32[Size * Size];

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float dx = x + 0.5f - outer;
                float dy = y + 0.5f - outer;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                float alpha = Mathf.Clamp01(outer - d);
                if (innerRatio > 0f)
                    alpha = Mathf.Min(alpha, Mathf.Clamp01(d - inner));

                pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply(false, true);

        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.DontSave;
        return sprite;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        disc = null;
        ring = null;
    }
}

using UnityEngine;

public class MouseController : MonoBehaviour
{
    private bool isLocked = true;

    void Start()
    {
        SetCursorState(true);
    }

    void Update()
    {
        // Toggle cursor when pressing the Escape key
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            isLocked = !isLocked;
            SetCursorState(isLocked);
        }
    }

    void SetCursorState(bool locked)
    {
        Cursor.visible = !locked;
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
    }
}

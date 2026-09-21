using UnityEngine;
using UnityEngine.EventSystems;

public sealed class GlobeOrbitController : MonoBehaviour
{
    [SerializeField] private Transform worldRoot;
    [SerializeField] private Camera worldCamera;
    [SerializeField] private float dragSensitivity = 0.18f;
    [SerializeField] private float zoomSensitivity = 0.8f;
    [SerializeField] private float minimumDistance = 6.4f;
    [SerializeField] private float maximumDistance = 11.5f;

    private Vector2 lastPointerPosition;
    private Vector2 spinVelocity;
    private float cameraDistance = 8.8f;
    private bool isDragging;

    public void Configure(Transform targetWorld, Camera targetCamera)
    {
        worldRoot = targetWorld;
        worldCamera = targetCamera;
        cameraDistance = Mathf.Abs(targetCamera.transform.position.z);
    }

    private void Update()
    {
        if (worldRoot == null || worldCamera == null) return;

        HandleMouse();
        HandleTouches();

        if (!isDragging && spinVelocity.sqrMagnitude > 0.001f)
        {
            RotateWorld(spinVelocity * Time.unscaledDeltaTime * 60f);
            spinVelocity = Vector2.Lerp(spinVelocity, Vector2.zero, 4.5f * Time.unscaledDeltaTime);
        }

        worldCamera.transform.position = Vector3.back * cameraDistance;
        worldCamera.transform.LookAt(Vector3.zero, Vector3.up);
    }

    private void HandleMouse()
    {
        if (Input.touchCount > 0) return;

        if (Input.GetMouseButtonDown(0) && !IsPointerOverUi())
        {
            isDragging = true;
            lastPointerPosition = Input.mousePosition;
            spinVelocity = Vector2.zero;
        }

        if (Input.GetMouseButton(0) && isDragging)
        {
            Vector2 currentPosition = Input.mousePosition;
            Vector2 delta = currentPosition - lastPointerPosition;
            lastPointerPosition = currentPosition;
            RotateWorld(delta);
            spinVelocity = delta;
        }

        if (Input.GetMouseButtonUp(0)) isDragging = false;

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > 0.01f)
            cameraDistance = Mathf.Clamp(cameraDistance - scroll * zoomSensitivity, minimumDistance, maximumDistance);
    }

    private void HandleTouches()
    {
        if (Input.touchCount == 0) return;

        if (Input.touchCount == 1)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began && !IsPointerOverUi(touch.fingerId))
            {
                isDragging = true;
                lastPointerPosition = touch.position;
                spinVelocity = Vector2.zero;
            }
            else if (touch.phase == TouchPhase.Moved && isDragging)
            {
                Vector2 delta = touch.position - lastPointerPosition;
                lastPointerPosition = touch.position;
                RotateWorld(delta);
                spinVelocity = delta;
            }
            else if (touch.phase is TouchPhase.Ended or TouchPhase.Canceled)
            {
                isDragging = false;
            }
            return;
        }

        isDragging = false;
        Touch first = Input.GetTouch(0);
        Touch second = Input.GetTouch(1);
        Vector2 firstPrevious = first.position - first.deltaPosition;
        Vector2 secondPrevious = second.position - second.deltaPosition;
        float previousDistance = Vector2.Distance(firstPrevious, secondPrevious);
        float currentDistance = Vector2.Distance(first.position, second.position);
        float pinchDelta = currentDistance - previousDistance;
        cameraDistance = Mathf.Clamp(
            cameraDistance - pinchDelta * 0.012f,
            minimumDistance,
            maximumDistance);
    }

    private void RotateWorld(Vector2 pointerDelta)
    {
        worldRoot.Rotate(Vector3.up, -pointerDelta.x * dragSensitivity, Space.World);
        worldRoot.Rotate(worldCamera.transform.right, pointerDelta.y * dragSensitivity, Space.World);
    }

    private static bool IsPointerOverUi(int pointerId = -1)
    {
        if (EventSystem.current == null) return false;
        return pointerId < 0
            ? EventSystem.current.IsPointerOverGameObject()
            : EventSystem.current.IsPointerOverGameObject(pointerId);
    }
}

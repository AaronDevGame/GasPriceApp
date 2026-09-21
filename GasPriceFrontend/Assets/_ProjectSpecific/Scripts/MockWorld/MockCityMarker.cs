using TMPro;
using UnityEngine;

public sealed class MockCityMarker : MonoBehaviour
{
    private const float LabelFacingThreshold = 0.08f;

    private MockCityFuelPrice cityData;
    private Camera worldCamera;
    private MeshRenderer[] markerRenderers;
    private TMP_Text label;
    private Canvas labelCanvas;
    private Material markerMaterial;

    public void Configure(
        MockCityFuelPrice data,
        Camera targetCamera,
        Material sharedMarkerMaterial,
        TMP_Text markerLabel,
        Canvas markerCanvas)
    {
        cityData = data;
        worldCamera = targetCamera;
        markerRenderers = GetComponentsInChildren<MeshRenderer>();
        markerMaterial = new Material(sharedMarkerMaterial);
        foreach (MeshRenderer renderer in markerRenderers)
            renderer.sharedMaterial = markerMaterial;
        label = markerLabel;
        labelCanvas = markerCanvas;
    }

    public void Refresh(MockFuelType fuelType, float minimumPrice, float maximumPrice)
    {
        float price = cityData.GetPrice(fuelType);
        float normalizedPrice = Mathf.InverseLerp(minimumPrice, maximumPrice, price);
        Color markerColor = FuelPriceWorldBootstrap.EvaluatePriceColor(normalizedPrice);
        markerMaterial.color = markerColor;
        markerMaterial.SetColor("_EmissionColor", markerColor * 1.7f);
        label.text = $"<b>{cityData.city}</b>\n<color=#EAF6FF>₱{price:0.00}/L</color>";
    }

    private void LateUpdate()
    {
        if (worldCamera == null || labelCanvas == null) return;

        Vector3 surfaceNormal = transform.position.normalized;
        Vector3 directionToCamera = (worldCamera.transform.position - transform.position).normalized;
        bool isVisible = Vector3.Dot(surfaceNormal, directionToCamera) > LabelFacingThreshold;

        labelCanvas.enabled = isVisible;
        if (markerRenderers != null)
        {
            foreach (MeshRenderer renderer in markerRenderers)
                renderer.enabled = isVisible;
        }
        if (isVisible) labelCanvas.transform.rotation = worldCamera.transform.rotation;
    }

    private void OnDestroy()
    {
        if (markerMaterial != null) Destroy(markerMaterial);
    }
}

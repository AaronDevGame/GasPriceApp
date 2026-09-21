using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class FuelPriceWorldBootstrap : MonoBehaviour
{
    private const float GlobeRadius = 3f;
    private const float PhilippinesCenterLatitude = 11.8f;
    private const float PhilippinesCenterLongitude = 122.4f;
    private const float GeographicExaggeration = 3.4f;

    private static readonly Color OceanColor = new(0.025f, 0.13f, 0.21f);
    private static readonly Color LandColor = new(0.12f, 0.38f, 0.32f);
    private static readonly Color AccentColor = new(0.18f, 0.82f, 0.91f);
    private static readonly Color TextColor = new(0.91f, 0.97f, 1f);

    private readonly List<MockCityMarker> markers = new();
    private readonly Dictionary<MockFuelType, SelectorVisual> selectorVisuals = new();

    private Transform worldRoot;
    private Camera worldCamera;
    private Material markerMaterial;
    private MockFuelType selectedFuelType = MockFuelType.Diesel;

    private readonly struct SelectorVisual
    {
        public SelectorVisual(Image background, TMP_Text label)
        {
            Background = background;
            Label = label;
        }

        public Image Background { get; }
        public TMP_Text Label { get; }
    }

    private void Awake()
    {
        DisableApiSampleCanvas();
        ConfigureScene();
        BuildWorld();
        BuildInterface();
        SelectFuel(MockFuelType.Diesel);
    }

    private static void DisableApiSampleCanvas()
    {
        GameObject legacyCanvas = GameObject.Find("Canvas");
        if (legacyCanvas != null) legacyCanvas.SetActive(false);
    }

    private void ConfigureScene()
    {
        worldCamera = Camera.main;
        if (worldCamera == null)
        {
            GameObject cameraObject = new("Main Camera");
            cameraObject.tag = "MainCamera";
            worldCamera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
        }

        worldCamera.transform.position = Vector3.back * 8.8f;
        worldCamera.transform.LookAt(Vector3.zero);
        worldCamera.fieldOfView = 48f;
        worldCamera.nearClipPlane = 0.1f;
        worldCamera.farClipPlane = 100f;
        worldCamera.clearFlags = CameraClearFlags.SolidColor;
        worldCamera.backgroundColor = new Color(0.009f, 0.025f, 0.055f);

        Light sceneLight = FindFirstObjectByType<Light>();
        if (sceneLight != null)
        {
            sceneLight.transform.rotation = Quaternion.Euler(28f, -42f, 8f);
            sceneLight.color = new Color(0.79f, 0.91f, 1f);
            sceneLight.intensity = 1.35f;
            sceneLight.shadows = LightShadows.Soft;
        }

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.18f, 0.27f, 0.38f);
        RenderSettings.ambientEquatorColor = new Color(0.06f, 0.13f, 0.2f);
        RenderSettings.ambientGroundColor = new Color(0.015f, 0.035f, 0.055f);
    }

    private void BuildWorld()
    {
        worldRoot = new GameObject("Mock Fuel Price World").transform;

        Material oceanMaterial = CreateLitMaterial(OceanColor, 0.12f, 0.82f);
        GameObject ocean = CreatePrimitive(PrimitiveType.Sphere, "Ocean", worldRoot);
        ocean.transform.localScale = Vector3.one * GlobeRadius * 2f;
        ocean.GetComponent<MeshRenderer>().sharedMaterial = oceanMaterial;

        BuildAtmosphere();
        BuildGrid();
        BuildPhilippineIslands();
        BuildOrbitDecoration();
        BuildMarkers();

        worldRoot.rotation = Quaternion.Euler(-4f, -6f, 1.5f);

        GlobeOrbitController controller = gameObject.AddComponent<GlobeOrbitController>();
        controller.Configure(worldRoot, worldCamera);
    }

    private void BuildAtmosphere()
    {
        GameObject atmosphere = CreatePrimitive(PrimitiveType.Sphere, "Atmosphere", worldRoot);
        atmosphere.transform.localScale = Vector3.one * GlobeRadius * 2.055f;
        atmosphere.GetComponent<MeshRenderer>().sharedMaterial = CreateTransparentMaterial(
            new Color(0.13f, 0.66f, 0.86f, 0.09f));
    }

    private void BuildGrid()
    {
        Material gridMaterial = CreateUnlitMaterial(new Color(0.18f, 0.66f, 0.77f, 0.16f));
        Transform gridRoot = new GameObject("Globe Grid").transform;
        gridRoot.SetParent(worldRoot, false);

        for (int latitude = -60; latitude <= 60; latitude += 30)
        {
            var points = new Vector3[73];
            for (int index = 0; index < points.Length; index++)
            {
                float longitude = index / 72f * 360f;
                points[index] = StandardGeoToPosition(latitude, longitude, GlobeRadius * 1.006f);
            }
            CreateLine($"Latitude {latitude}", gridRoot, gridMaterial, points, 0.009f, true);
        }

        for (int longitude = 0; longitude < 360; longitude += 30)
        {
            var points = new Vector3[73];
            for (int index = 0; index < points.Length; index++)
            {
                float latitude = -90f + index / 72f * 180f;
                points[index] = StandardGeoToPosition(latitude, longitude, GlobeRadius * 1.006f);
            }
            CreateLine($"Longitude {longitude}", gridRoot, gridMaterial, points, 0.009f, false);
        }
    }

    private void BuildPhilippineIslands()
    {
        Material landMaterial = CreateLitMaterial(LandColor, 0.03f, 0.48f);
        Material coastMaterial = CreateUnlitMaterial(new Color(0.28f, 0.92f, 0.73f, 0.72f));
        Transform islandsRoot = new GameObject("Stylized Philippines").transform;
        islandsRoot.SetParent(worldRoot, false);

        CreateIsland("Luzon", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(18.6, 120.7), Geo(18.2, 122.1), Geo(17.2, 122.5), Geo(16.1, 121.5),
            Geo(15.8, 121.0), Geo(14.6, 121.3), Geo(13.7, 123.0), Geo(13.0, 123.7),
            Geo(13.4, 122.2), Geo(14.3, 120.6), Geo(16.0, 120.2), Geo(17.4, 120.4)
        });
        CreateIsland("Mindoro", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(13.5, 120.3), Geo(13.4, 121.2), Geo(12.6, 121.5), Geo(12.1, 120.8), Geo(12.5, 120.2)
        });
        CreateIsland("Palawan", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(12.2, 119.7), Geo(11.5, 119.4), Geo(10.2, 118.8), Geo(8.7, 117.9),
            Geo(8.4, 117.5), Geo(9.5, 118.0), Geo(11.0, 118.9)
        });
        CreateIsland("Panay", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(11.8, 121.9), Geo(11.9, 122.7), Geo(11.1, 122.8), Geo(10.4, 122.1), Geo(10.8, 121.8)
        });
        CreateIsland("Negros", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(11.1, 122.8), Geo(10.8, 123.2), Geo(9.1, 123.1), Geo(9.0, 122.7), Geo(10.1, 122.5)
        });
        CreateIsland("Cebu", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(11.3, 123.7), Geo(11.1, 124.0), Geo(9.4, 123.9), Geo(9.5, 123.5)
        });
        CreateIsland("Bohol", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(10.2, 123.7), Geo(10.1, 124.5), Geo(9.6, 124.6), Geo(9.5, 123.8)
        });
        CreateIsland("Leyte", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(11.5, 124.7), Geo(11.2, 125.1), Geo(9.9, 125.3), Geo(9.7, 124.8), Geo(10.6, 124.5)
        });
        CreateIsland("Samar", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(12.6, 124.2), Geo(12.5, 125.3), Geo(11.4, 125.5), Geo(11.0, 124.8), Geo(11.8, 124.4)
        });
        CreateIsland("Mindanao", islandsRoot, landMaterial, coastMaterial, new[]
        {
            Geo(9.8, 125.3), Geo(9.0, 126.2), Geo(7.3, 126.5), Geo(6.0, 125.7),
            Geo(5.7, 124.6), Geo(6.3, 123.8), Geo(7.2, 122.0), Geo(8.1, 123.0),
            Geo(8.6, 124.0), Geo(9.5, 124.3)
        });
    }

    private void BuildOrbitDecoration()
    {
        Material orbitMaterial = CreateUnlitMaterial(new Color(0.26f, 0.82f, 0.96f, 0.2f));
        Transform decorationRoot = new GameObject("Orbit Decoration").transform;
        decorationRoot.SetParent(worldRoot, false);
        decorationRoot.localRotation = Quaternion.Euler(63f, 12f, 8f);

        var points = new Vector3[97];
        for (int index = 0; index < points.Length; index++)
        {
            float angle = index / 96f * Mathf.PI * 2f;
            points[index] = new Vector3(Mathf.Cos(angle) * 3.72f, 0f, Mathf.Sin(angle) * 3.72f);
        }
        CreateLine("Orbital Ring", decorationRoot, orbitMaterial, points, 0.018f, true);
    }

    private void BuildMarkers()
    {
        markerMaterial = CreateLitMaterial(Color.white, 0.05f, 0.78f);
        markerMaterial.EnableKeyword("_EMISSION");
        Transform markersRoot = new GameObject("Mock City Markers").transform;
        markersRoot.SetParent(worldRoot, false);

        foreach (MockCityFuelPrice city in MockFuelPriceData.Cities)
        {
            GameObject markerRoot = new(city.city);
            markerRoot.transform.SetParent(markersRoot, false);
            Vector3 normal = GeoToDirection(city.latitude, city.longitude);
            markerRoot.transform.localPosition = normal * (GlobeRadius + 0.08f);
            markerRoot.transform.up = normal;

            GameObject stem = CreatePrimitive(PrimitiveType.Cylinder, "Stem", markerRoot.transform);
            stem.transform.localPosition = Vector3.up * 0.13f;
            stem.transform.localScale = new Vector3(0.018f, 0.13f, 0.018f);
            stem.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;

            GameObject dot = CreatePrimitive(PrimitiveType.Sphere, "Price Marker", markerRoot.transform);
            dot.transform.localPosition = Vector3.up * 0.31f;
            dot.transform.localScale = Vector3.one * 0.17f;
            dot.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;

            Canvas labelCanvas = CreateWorldLabel(markerRoot.transform, out TMP_Text label);
            labelCanvas.transform.localPosition = GetLabelOffset(city.city);

            MockCityMarker marker = markerRoot.AddComponent<MockCityMarker>();
            marker.Configure(city, worldCamera, markerMaterial, label, labelCanvas);
            markers.Add(marker);
        }
    }

    private void BuildInterface()
    {
        GameObject canvasObject = new("Mock World UI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;

        TMP_Text eyebrow = CreateScreenText("Eyebrow", canvasObject.transform, "PHILIPPINES  •  FRONTEND PREVIEW", 22, FontStyles.Bold);
        SetRect(eyebrow.rectTransform, new Vector2(0.06f, 0.91f), new Vector2(0.94f, 0.95f));
        eyebrow.color = AccentColor;
        eyebrow.characterSpacing = 5f;

        TMP_Text title = CreateScreenText("Title", canvasObject.transform, "Presyo Planet", 58, FontStyles.Bold);
        SetRect(title.rectTransform, new Vector2(0.06f, 0.84f), new Vector2(0.94f, 0.92f));
        title.color = TextColor;

        TMP_Text subtitle = CreateScreenText("Subtitle", canvasObject.transform, "Explore sample fuel prices across the islands", 25, FontStyles.Normal);
        SetRect(subtitle.rectTransform, new Vector2(0.06f, 0.80f), new Vector2(0.94f, 0.85f));
        subtitle.color = new Color(0.65f, 0.75f, 0.82f);

        TMP_Text mockBadge = CreateScreenText("Mock Badge", canvasObject.transform, "●  MOCK DATA", 20, FontStyles.Bold);
        SetRect(mockBadge.rectTransform, new Vector2(0.68f, 0.94f), new Vector2(0.94f, 0.98f));
        mockBadge.alignment = TextAlignmentOptions.Right;
        mockBadge.color = new Color(1f, 0.78f, 0.32f);

        GameObject selectorPanel = CreateUiObject("Fuel Selector", canvasObject.transform);
        Image selectorBackground = selectorPanel.AddComponent<Image>();
        selectorBackground.color = new Color(0.035f, 0.09f, 0.14f, 0.94f);
        SetRect(selectorPanel.GetComponent<RectTransform>(), new Vector2(0.055f, 0.075f), new Vector2(0.945f, 0.145f));

        HorizontalLayoutGroup layout = selectorPanel.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 12, 12);
        layout.spacing = 10f;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = true;
        layout.childForceExpandWidth = true;

        CreateFuelButton(selectorPanel.transform, MockFuelType.Diesel, "DIESEL");
        CreateFuelButton(selectorPanel.transform, MockFuelType.Regular91, "REGULAR 91");
        CreateFuelButton(selectorPanel.transform, MockFuelType.Premium95, "PREMIUM 95");

        TMP_Text hint = CreateScreenText("Interaction Hint", canvasObject.transform, "DRAG TO ROTATE   •   PINCH OR SCROLL TO ZOOM", 18, FontStyles.Bold);
        SetRect(hint.rectTransform, new Vector2(0.06f, 0.025f), new Vector2(0.94f, 0.065f));
        hint.alignment = TextAlignmentOptions.Center;
        hint.color = new Color(0.48f, 0.63f, 0.72f);

        BuildLegend(canvasObject.transform);
    }

    private void BuildLegend(Transform parent)
    {
        GameObject legend = CreateUiObject("Price Legend", parent);
        SetRect(legend.GetComponent<RectTransform>(), new Vector2(0.08f, 0.16f), new Vector2(0.92f, 0.195f));
        HorizontalLayoutGroup layout = legend.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 12f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;

        CreateLegendItem(legend.transform, "LOWER", EvaluatePriceColor(0f));
        CreateLegendItem(legend.transform, "AVERAGE", EvaluatePriceColor(0.5f));
        CreateLegendItem(legend.transform, "HIGHER", EvaluatePriceColor(1f));
    }

    private void CreateFuelButton(Transform parent, MockFuelType fuelType, string caption)
    {
        GameObject buttonObject = CreateUiObject(caption, parent);
        Image background = buttonObject.AddComponent<Image>();
        background.color = new Color(0.08f, 0.17f, 0.23f, 1f);
        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = background;
        button.onClick.AddListener(() => SelectFuel(fuelType));

        TMP_Text label = CreateScreenText("Label", buttonObject.transform, caption, 21, FontStyles.Bold);
        Stretch(label.rectTransform, 3f);
        label.alignment = TextAlignmentOptions.Center;

        selectorVisuals[fuelType] = new SelectorVisual(background, label);
    }

    private void SelectFuel(MockFuelType fuelType)
    {
        selectedFuelType = fuelType;
        GetPriceRange(fuelType, out float minimumPrice, out float maximumPrice);

        foreach (MockCityMarker marker in markers)
            marker.Refresh(selectedFuelType, minimumPrice, maximumPrice);

        foreach (KeyValuePair<MockFuelType, SelectorVisual> pair in selectorVisuals)
        {
            bool isSelected = pair.Key == selectedFuelType;
            pair.Value.Background.color = isSelected
                ? new Color(0.08f, 0.62f, 0.72f, 1f)
                : new Color(0.08f, 0.17f, 0.23f, 1f);
            pair.Value.Label.color = isSelected ? Color.white : new Color(0.58f, 0.7f, 0.77f);
        }
    }

    private static void GetPriceRange(MockFuelType fuelType, out float minimumPrice, out float maximumPrice)
    {
        minimumPrice = float.MaxValue;
        maximumPrice = float.MinValue;
        foreach (MockCityFuelPrice city in MockFuelPriceData.Cities)
        {
            float price = city.GetPrice(fuelType);
            minimumPrice = Mathf.Min(minimumPrice, price);
            maximumPrice = Mathf.Max(maximumPrice, price);
        }
    }

    public static Color EvaluatePriceColor(float normalizedPrice)
    {
        Color lower = new(0.22f, 0.88f, 0.57f);
        Color average = new(1f, 0.76f, 0.24f);
        Color higher = new(1f, 0.27f, 0.3f);
        return normalizedPrice < 0.5f
            ? Color.Lerp(lower, average, normalizedPrice * 2f)
            : Color.Lerp(average, higher, (normalizedPrice - 0.5f) * 2f);
    }

    private static Canvas CreateWorldLabel(Transform parent, out TMP_Text label)
    {
        GameObject canvasObject = new("City Label", typeof(RectTransform), typeof(Canvas));
        canvasObject.transform.SetParent(parent, false);
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 5;
        RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
        canvasRect.sizeDelta = new Vector2(180f, 64f);
        canvasRect.localScale = Vector3.one * 0.0032f;

        GameObject labelObject = CreateUiObject("Price", canvasObject.transform);
        label = labelObject.AddComponent<TextMeshProUGUI>();
        label.font = TMP_Settings.defaultFontAsset;
        label.fontSize = 24f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = TextColor;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.raycastTarget = false;
        Stretch(label.rectTransform, 0f);
        return canvas;
    }

    private static Vector3 GetLabelOffset(string cityName) => cityName switch
    {
        "Manila" => new Vector3(-0.3f, 0.53f, 0f),
        "Quezon City" => new Vector3(0.3f, 0.72f, 0f),
        "Angeles" => new Vector3(-0.22f, 0.69f, 0f),
        "Iloilo" => new Vector3(-0.3f, 0.53f, 0f),
        "Bacolod" => new Vector3(0.3f, 0.71f, 0f),
        "Cagayan de Oro" => new Vector3(-0.28f, 0.62f, 0f),
        "Davao" => new Vector3(0.28f, 0.54f, 0f),
        "General Santos" => new Vector3(-0.25f, 0.7f, 0f),
        _ => new Vector3(0f, 0.54f, 0f)
    };

    private static void CreateIsland(
        string name,
        Transform parent,
        Material landMaterial,
        Material coastMaterial,
        IReadOnlyList<Vector2> coordinates)
    {
        var vertices = new Vector3[coordinates.Count + 1];
        Vector2 center = Vector2.zero;
        for (int index = 0; index < coordinates.Count; index++) center += coordinates[index];
        center /= coordinates.Count;
        vertices[0] = GeoToDirection(center.x, center.y) * (GlobeRadius + 0.025f);
        for (int index = 0; index < coordinates.Count; index++)
            vertices[index + 1] = GeoToDirection(coordinates[index].x, coordinates[index].y) * (GlobeRadius + 0.025f);

        var triangles = new List<int>();
        for (int index = 0; index < coordinates.Count; index++)
        {
            int current = index + 1;
            int next = (index + 1) % coordinates.Count + 1;
            triangles.Add(0);
            triangles.Add(next);
            triangles.Add(current);
            triangles.Add(0);
            triangles.Add(current);
            triangles.Add(next);
        }

        var mesh = new Mesh { name = name + " Mesh", vertices = vertices, triangles = triangles.ToArray() };
        var normals = new Vector3[vertices.Length];
        for (int index = 0; index < vertices.Length; index++) normals[index] = vertices[index].normalized;
        mesh.normals = normals;
        mesh.RecalculateBounds();

        GameObject island = new(name, typeof(MeshFilter), typeof(MeshRenderer));
        island.transform.SetParent(parent, false);
        island.GetComponent<MeshFilter>().sharedMesh = mesh;
        island.GetComponent<MeshRenderer>().sharedMaterial = landMaterial;

        var coastPoints = new Vector3[coordinates.Count + 1];
        for (int index = 0; index < coordinates.Count; index++)
            coastPoints[index] = GeoToDirection(coordinates[index].x, coordinates[index].y) * (GlobeRadius + 0.034f);
        coastPoints[^1] = coastPoints[0];
        CreateLine(name + " Coast", island.transform, coastMaterial, coastPoints, 0.015f, false);
    }

    private static Vector2 Geo(double latitude, double longitude) => new((float)latitude, (float)longitude);

    private static Vector3 GeoToDirection(double latitude, double longitude)
    {
        float displayLatitude = PhilippinesCenterLatitude + ((float)latitude - PhilippinesCenterLatitude) * GeographicExaggeration;
        float displayLongitude = PhilippinesCenterLongitude + ((float)longitude - PhilippinesCenterLongitude) * GeographicExaggeration;
        return StandardGeoToPosition(displayLatitude, displayLongitude - PhilippinesCenterLongitude, 1f).normalized;
    }

    private static Vector3 StandardGeoToPosition(float latitude, float longitude, float radius)
    {
        float latitudeRadians = latitude * Mathf.Deg2Rad;
        float longitudeRadians = longitude * Mathf.Deg2Rad;
        float latitudeRadius = Mathf.Cos(latitudeRadians);
        return new Vector3(
            latitudeRadius * Mathf.Sin(longitudeRadians) * radius,
            Mathf.Sin(latitudeRadians) * radius,
            -latitudeRadius * Mathf.Cos(longitudeRadians) * radius);
    }

    private static GameObject CreatePrimitive(PrimitiveType primitiveType, string name, Transform parent)
    {
        GameObject result = GameObject.CreatePrimitive(primitiveType);
        result.name = name;
        result.transform.SetParent(parent, false);
        Collider primitiveCollider = result.GetComponent<Collider>();
        if (primitiveCollider != null) Destroy(primitiveCollider);
        return result;
    }

    private static void CreateLine(
        string name,
        Transform parent,
        Material material,
        IReadOnlyList<Vector3> points,
        float width,
        bool loop)
    {
        GameObject lineObject = new(name);
        lineObject.transform.SetParent(parent, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = false;
        line.loop = loop;
        line.positionCount = points.Count;
        line.startWidth = width;
        line.endWidth = width;
        line.numCornerVertices = 2;
        line.numCapVertices = 2;
        for (int index = 0; index < points.Count; index++) line.SetPosition(index, points[index]);
    }

    private static Material CreateLitMaterial(Color color, float metallic, float smoothness)
    {
        Shader shader = Shader.Find("Standard") ?? Shader.Find("Universal Render Pipeline/Lit");
        var material = new Material(shader) { color = color };
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        return material;
    }

    private static Material CreateUnlitMaterial(Color color)
    {
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        return new Material(shader) { color = color };
    }

    private static Material CreateTransparentMaterial(Color color)
    {
        Material material = CreateLitMaterial(color, 0f, 0.65f);
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        return material;
    }

    private static void CreateLegendItem(Transform parent, string caption, Color color)
    {
        GameObject item = CreateUiObject(caption, parent);
        HorizontalLayoutGroup layout = item.AddComponent<HorizontalLayoutGroup>();
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = false;

        GameObject swatch = CreateUiObject("Swatch", item.transform);
        Image image = swatch.AddComponent<Image>();
        image.color = color;
        LayoutElement swatchLayout = swatch.AddComponent<LayoutElement>();
        swatchLayout.preferredWidth = 14f;
        swatchLayout.preferredHeight = 14f;

        TMP_Text label = CreateScreenText("Label", item.transform, caption, 16, FontStyles.Bold);
        LayoutElement labelLayout = label.gameObject.AddComponent<LayoutElement>();
        labelLayout.preferredWidth = 82f;
        label.color = new Color(0.58f, 0.7f, 0.77f);
    }

    private static GameObject CreateUiObject(string name, Transform parent)
    {
        GameObject result = new(name, typeof(RectTransform));
        result.layer = LayerMask.NameToLayer("UI");
        result.transform.SetParent(parent, false);
        return result;
    }

    private static TMP_Text CreateScreenText(string name, Transform parent, string value, float size, FontStyles style)
    {
        GameObject textObject = CreateUiObject(name, parent);
        var text = textObject.AddComponent<TextMeshProUGUI>();
        text.font = TMP_Settings.defaultFontAsset;
        text.text = value;
        text.fontSize = size;
        text.fontStyle = style;
        text.color = TextColor;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        return text;
    }

    private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }
}

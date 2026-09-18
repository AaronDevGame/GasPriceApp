using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas))]
public class APIEndpointCanvasBuilder : MonoBehaviour
{
    [SerializeField] private bool buildOnAwake = true;
    [SerializeField] private Vector2 panelSize = new(1500, 900);
    [SerializeField] private float rowHeight = 150;

    private const string RootName = "API Endpoint Samples";

    private void Awake()
    {
        if (buildOnAwake && transform.Find(RootName) == null) BuildCanvasSamples();
    }

    [ContextMenu("Build API Canvas Samples")]
    public void BuildCanvasSamples()
    {
        GameObject root = CreateUIObject(RootName, transform);
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = rootRect.anchorMax = new Vector2(.5f, .5f);
        rootRect.sizeDelta = panelSize;
        root.AddComponent<Image>().color = new Color(.04f, .05f, .07f, .94f);

        ScrollRect scroll = root.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        GameObject viewport = CreateUIObject("Viewport", root.transform);
        Stretch(viewport.GetComponent<RectTransform>(), 20);
        viewport.AddComponent<Image>().color = new Color(0, 0, 0, .01f);
        viewport.AddComponent<Mask>().showMaskGraphic = false;

        GameObject content = CreateUIObject("Content", viewport.transform);
        RectTransform contentRect = content.GetComponent<RectTransform>();
        contentRect.anchorMin = new Vector2(0, 1);
        contentRect.anchorMax = new Vector2(1, 1);
        contentRect.pivot = new Vector2(.5f, 1);
        contentRect.anchoredPosition = Vector2.zero;
        var layout = content.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(10, 10, 10, 10);
        layout.spacing = 10;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        var fitter = content.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.content = contentRect;

        foreach (APIEndpoint endpoint in Enum.GetValues(typeof(APIEndpoint))) CreateEndpointRow(content.transform, endpoint);
    }

    private void CreateEndpointRow(Transform parent, APIEndpoint endpoint)
    {
        GameObject row = CreateUIObject(endpoint.ToString(), parent);
        row.AddComponent<Image>().color = new Color(.12f, .14f, .18f, 1);
        row.AddComponent<LayoutElement>().preferredHeight = rowHeight;

        GameObject buttonObject = CreateUIObject("Call " + endpoint, row.transform);
        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0, 0);
        buttonRect.anchorMax = new Vector2(0, 1);
        buttonRect.pivot = new Vector2(0, .5f);
        buttonRect.anchoredPosition = new Vector2(10, 0);
        buttonRect.sizeDelta = new Vector2(280, -20);
        Image buttonImage = buttonObject.AddComponent<Image>();
        buttonImage.color = new Color(.12f, .42f, .76f, 1);
        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        TMP_Text buttonLabel = CreateText("Label", buttonObject.transform, endpoint.ToString(), 24);
        Stretch(buttonLabel.rectTransform, 8);
        buttonLabel.alignment = TextAlignmentOptions.Center;

        TMP_Text output = CreateText("Response", row.transform, "Press the button to call this endpoint.", 18);
        RectTransform outputRect = output.rectTransform;
        outputRect.anchorMin = Vector2.zero;
        outputRect.anchorMax = Vector2.one;
        outputRect.offsetMin = new Vector2(310, 10);
        outputRect.offsetMax = new Vector2(-10, -10);
        output.enableWordWrapping = true;
        output.overflowMode = TextOverflowModes.Ellipsis;

        TMP_InputField adminKeyInput = null;
        if (endpoint >= APIEndpoint.AdminRoutes)
        {
            buttonRect.anchorMin = new Vector2(0, .42f);
            buttonRect.sizeDelta = new Vector2(280, -10);
            buttonRect.anchoredPosition = new Vector2(10, 5);
            adminKeyInput = CreateAdminKeyInput(row.transform);
        }

        APICallingSampleScript caller = row.AddComponent<APICallingSampleScript>();
        caller.Configure(endpoint, button, output, adminKeyInput);
    }

    private static TMP_InputField CreateAdminKeyInput(Transform parent)
    {
        GameObject inputObject = CreateUIObject("Runtime Admin Bearer Token", parent);
        RectTransform inputRect = inputObject.GetComponent<RectTransform>();
        inputRect.anchorMin = inputRect.anchorMax = new Vector2(0, 0);
        inputRect.pivot = new Vector2(0, 0);
        inputRect.anchoredPosition = new Vector2(10, 10);
        inputRect.sizeDelta = new Vector2(280, 45);
        inputObject.AddComponent<Image>().color = new Color(.04f, .05f, .07f, 1);
        TMP_Text text = CreateText("Text", inputObject.transform, string.Empty, 16);
        Stretch(text.rectTransform, 8);
        var input = inputObject.AddComponent<TMP_InputField>();
        input.textViewport = inputObject.GetComponent<RectTransform>();
        input.textComponent = text;
        input.contentType = TMP_InputField.ContentType.Password;
        return input;
    }

    private static GameObject CreateUIObject(string name, Transform parent)
    {
        var gameObject = new GameObject(name, typeof(RectTransform));
        gameObject.layer = LayerMask.NameToLayer("UI");
        gameObject.transform.SetParent(parent, false);
        return gameObject;
    }

    private static TMP_Text CreateText(string name, Transform parent, string value, float size)
    {
        GameObject gameObject = CreateUIObject(name, parent);
        var text = gameObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = size;
        text.color = Color.white;
        return text;
    }

    private static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }
}

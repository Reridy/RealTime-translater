using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RealTimeTranslater.UnityBepInEx;

[BepInPlugin(
    "com.realtimetranslater.unityadapter",
    "RealTimeTranslater Unity Adapter",
    "0.4.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private const int MaximumRegions = 128;
    private const int MaximumTextLength = 2048;
    private const float PollIntervalSeconds = 0.12f;

    private static readonly Regex RichTextTagPattern =
        new Regex("<[^>]+>", RegexOptions.Compiled);

    private PipePublisher _publisher;

    private void Awake()
    {
        _publisher = new PipePublisher(
            message => Logger.LogInfo(message),
            message => Logger.LogWarning(message));
        _publisher.Start();

        StartCoroutine(PublishLoop());
        Logger.LogInfo(
            "RealTimeTranslater Unity Adapter 0.4.0 loaded; glyph bounds, layout bounds, and text style metadata enabled.");
    }

    private void OnDestroy()
    {
        if (_publisher != null)
        {
            _publisher.Dispose();
            _publisher = null;
        }
    }

    private IEnumerator PublishLoop()
    {
        var wait = new WaitForSecondsRealtime(PollIntervalSeconds);

        while (true)
        {
            string snapshot;

            try
            {
                snapshot = BuildSnapshot();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Could not collect visible text: " + ex.Message);
                snapshot = null;
            }

            if (!string.IsNullOrEmpty(snapshot))
                _publisher.Publish(snapshot);

            yield return wait;
        }
    }

    private static string BuildSnapshot()
    {
        var regions = new List<RegionPayload>(MaximumRegions);

        var tmpTexts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
        for (var i = 0;
             i < tmpTexts.Length && regions.Count < MaximumRegions;
             i++)
        {
            var text = tmpTexts[i];
            TryAddGraphic(
                text,
                text == null ? null : text.text,
                "TMP",
                regions);
        }

        if (regions.Count < MaximumRegions)
        {
            var legacyTexts = Resources.FindObjectsOfTypeAll<Text>();
            for (var i = 0;
                 i < legacyTexts.Length && regions.Count < MaximumRegions;
                 i++)
            {
                var text = legacyTexts[i];
                TryAddGraphic(
                    text,
                    text == null ? null : text.text,
                    "UnityUI",
                    regions);
            }
        }

        regions.Sort(CompareRegions);
        return SerializeSnapshot(regions);
    }

    private static void TryAddGraphic(
        Graphic graphic,
        string rawText,
        string kind,
        List<RegionPayload> regions)
    {
        if (graphic == null ||
            !graphic.isActiveAndEnabled ||
            !graphic.gameObject.activeInHierarchy ||
            !graphic.gameObject.scene.IsValid())
        {
            return;
        }

        if (!IsActuallyVisible(graphic))
            return;

        var text = NormalizeText(rawText);
        if (string.IsNullOrEmpty(text))
            return;

        ScreenRect rect;
        ScreenRect layoutRect;

        if (graphic is TextMeshProUGUI tmp)
        {
            var hasTightRect =
                TryGetTightTextScreenRect(
                    tmp,
                    out rect);

            var hasLayoutRect =
                TryGetScreenRect(
                    graphic,
                    out layoutRect);

            if (!hasTightRect &&
                !hasLayoutRect)
            {
                return;
            }

            if (!hasTightRect)
                rect = layoutRect;

            if (!hasLayoutRect)
                layoutRect = rect;
        }
        else
        {
            if (!TryGetScreenRect(
                    graphic,
                    out rect))
            {
                return;
            }

            layoutRect = rect;
        }

        if (rect.Width < 2 || rect.Height < 2)
            return;

        var sourceLineCount = 1;
        var sourceAlignment = string.Empty;

        if (graphic is TextMeshProUGUI styleTmp)
        {
            try
            {
                styleTmp.ForceMeshUpdate(
                    ignoreActiveState: false,
                    forceTextReparsing: false);

                sourceLineCount = Mathf.Max(
                    1,
                    styleTmp.textInfo == null
                        ? 1
                        : styleTmp.textInfo.lineCount);

                sourceAlignment =
                    styleTmp.alignment.ToString();
            }
            catch
            {
                sourceLineCount = 1;
                sourceAlignment = string.Empty;
            }
        }
        else if (graphic is Text legacyText)
        {
            try
            {
                sourceLineCount = Mathf.Max(
                    1,
                    legacyText.cachedTextGenerator == null
                        ? 1
                        : legacyText.cachedTextGenerator.lineCount);
            }
            catch
            {
                sourceLineCount = 1;
            }

            sourceAlignment =
                legacyText.alignment.ToString();
        }

        var foregroundArgb =
            PackArgb(
                graphic.color);

        var hierarchy = BuildHierarchyPath(graphic.transform);
        var selectable = graphic.GetComponentInParent<Selectable>();
        var selectableName =
            selectable == null
                ? string.Empty
                : selectable.gameObject.name ?? string.Empty;

        regions.Add(new RegionPayload
        {
            Text = text,
            Kind = kind,
            ObjectName = graphic.gameObject.name ?? string.Empty,
            Hierarchy = hierarchy,
            SelectableName = selectableName,
            IsSelectable = selectable != null,
            IsButton = selectable is Button,
            IsChoiceLike = IsChoiceLike(
                graphic.gameObject.name,
                hierarchy,
                selectableName),
            IsSpeakerLike = IsSpeakerLike(
                graphic.gameObject.name,
                hierarchy),
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height,
            LayoutX = layoutRect.X,
            LayoutY = layoutRect.Y,
            LayoutWidth = layoutRect.Width,
            LayoutHeight = layoutRect.Height,
            ForegroundArgb = foregroundArgb,
            SourceLineCount = sourceLineCount,
            SourceAlignment = sourceAlignment
        });
    }

    private static bool IsActuallyVisible(Graphic graphic)
    {
        if (graphic.canvas == null || graphic.color.a <= 0.01f)
            return false;

        var effectiveAlpha = graphic.color.a;
        var current = graphic.transform;

        while (current != null)
        {
            var groups = current.GetComponents<CanvasGroup>();

            for (var i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (group == null)
                    continue;

                effectiveAlpha *= group.alpha;
                if (effectiveAlpha <= 0.01f)
                    return false;

                if (group.ignoreParentGroups)
                    return true;
            }

            current = current.parent;
        }

        return effectiveAlpha > 0.01f;
    }

    private static string BuildHierarchyPath(Transform transform)
    {
        if (transform == null)
            return string.Empty;

        var names = new List<string>(8);
        var current = transform;

        while (current != null && names.Count < 12)
        {
            names.Add(current.gameObject.name ?? string.Empty);
            current = current.parent;
        }

        names.Reverse();
        return string.Join("/", names.ToArray());
    }

    private static bool IsChoiceLike(
        string objectName,
        string hierarchy,
        string selectableName)
    {
        var metadata =
            ((objectName ?? string.Empty) + " " +
             (hierarchy ?? string.Empty) + " " +
             (selectableName ?? string.Empty))
            .ToLowerInvariant();

        return ContainsAny(
            metadata,
            "choice",
            "answer",
            "option",
            "decision",
            "response");
    }

    private static bool IsSpeakerLike(
        string objectName,
        string hierarchy)
    {
        var metadata =
            ((objectName ?? string.Empty) + " " +
             (hierarchy ?? string.Empty))
            .ToLowerInvariant();

        return ContainsAny(
            metadata,
            "speaker",
            "speakername",
            "nameplate",
            "charactername",
            "character_name",
            "chara_name",
            "talker",
            "talkername",
            "name_text",
            "nametext");
    }

    private static bool ContainsAny(
        string value,
        params string[] needles)
    {
        for (var i = 0; i < needles.Length; i++)
        {
            if (value.IndexOf(
                    needles[i],
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = RichTextTagPattern.Replace(value, string.Empty);
        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        if (text.Length > MaximumTextLength)
            text = text.Substring(0, MaximumTextLength);

        return text;
    }

    private static bool TryGetTightTextScreenRect(
        TextMeshProUGUI text,
        out ScreenRect result)
    {
        result = default(ScreenRect);

        if (text == null ||
            text.rectTransform == null ||
            string.IsNullOrWhiteSpace(text.text))
        {
            return false;
        }

        try
        {
            text.ForceMeshUpdate(
                ignoreActiveState: false,
                forceTextReparsing: false);
        }
        catch
        {
            return false;
        }

        var bounds = text.textBounds;
        var size = bounds.size;

        if (size.x <= 0.01f || size.y <= 0.01f)
            return false;

        var min = bounds.min;
        var max = bounds.max;

        var localCorners = new[]
        {
            new Vector3(min.x, min.y, 0f),
            new Vector3(max.x, min.y, 0f),
            new Vector3(max.x, max.y, 0f),
            new Vector3(min.x, max.y, 0f)
        };

        var worldCorners = new Vector3[4];

        for (var i = 0; i < localCorners.Length; i++)
        {
            worldCorners[i] =
                text.rectTransform.TransformPoint(
                    localCorners[i]);
        }

        return TryGetScreenRectFromWorldPoints(
            text.canvas,
            worldCorners,
            out result);
    }

    private static bool TryGetScreenRectFromWorldPoints(
        Canvas canvas,
        Vector3[] worldPoints,
        out ScreenRect result)
    {
        result = default(ScreenRect);

        if (worldPoints == null || worldPoints.Length == 0)
            return false;

        Camera camera = null;

        if (canvas != null &&
            canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            camera = canvas.worldCamera != null
                ? canvas.worldCamera
                : Camera.main;
        }

        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;

        for (var i = 0; i < worldPoints.Length; i++)
        {
            var point = RectTransformUtility.WorldToScreenPoint(
                camera,
                worldPoints[i]);

            minX = Mathf.Min(minX, point.x);
            minY = Mathf.Min(minY, point.y);
            maxX = Mathf.Max(maxX, point.x);
            maxY = Mathf.Max(maxY, point.y);
        }

        return TryCreateClampedScreenRect(
            minX,
            minY,
            maxX,
            maxY,
            out result);
    }

    private static bool TryCreateClampedScreenRect(
        float minX,
        float minY,
        float maxX,
        float maxY,
        out ScreenRect result)
    {
        result = default(ScreenRect);

        if (float.IsInfinity(minX) ||
            float.IsInfinity(minY) ||
            float.IsInfinity(maxX) ||
            float.IsInfinity(maxY))
        {
            return false;
        }

        if (maxX <= 0 ||
            maxY <= 0 ||
            minX >= Screen.width ||
            minY >= Screen.height)
        {
            return false;
        }

        minX = Mathf.Clamp(minX, 0, Screen.width);
        maxX = Mathf.Clamp(maxX, 0, Screen.width);
        minY = Mathf.Clamp(minY, 0, Screen.height);
        maxY = Mathf.Clamp(maxY, 0, Screen.height);

        var x = Mathf.RoundToInt(minX);
        var y = Mathf.RoundToInt(Screen.height - maxY);
        var width = Mathf.Max(
            1,
            Mathf.RoundToInt(maxX - minX));
        var height = Mathf.Max(
            1,
            Mathf.RoundToInt(maxY - minY));

        result = new ScreenRect(
            x,
            y,
            width,
            height);

        return true;
    }

    private static bool TryGetScreenRect(
        Graphic graphic,
        out ScreenRect result)
    {
        result = default(ScreenRect);

        var rectTransform = graphic.rectTransform;
        if (rectTransform == null)
            return false;

        var corners = new Vector3[4];
        rectTransform.GetWorldCorners(corners);

        return TryGetScreenRectFromWorldPoints(
            graphic.canvas,
            corners,
            out result);
    }

    private static int CompareRegions(
        RegionPayload left,
        RegionPayload right)
    {
        var byY = left.Y.CompareTo(right.Y);
        if (byY != 0)
            return byY;

        var byX = left.X.CompareTo(right.X);
        if (byX != 0)
            return byX;

        return string.CompareOrdinal(left.Text, right.Text);
    }

    private static string SerializeSnapshot(
        List<RegionPayload> regions)
    {
        var builder = new StringBuilder(4096);

        builder.Append("{\"protocol\":2,\"screenWidth\":");
        builder.Append(Screen.width);
        builder.Append(",\"screenHeight\":");
        builder.Append(Screen.height);
        builder.Append(",\"regions\":[");

        for (var i = 0; i < regions.Count; i++)
        {
            if (i > 0)
                builder.Append(',');

            var region = regions[i];

            builder.Append("{\"text\":\"");
            AppendJsonString(builder, region.Text);
            builder.Append("\",\"kind\":\"");
            AppendJsonString(builder, region.Kind);
            builder.Append("\",\"objectName\":\"");
            AppendJsonString(builder, region.ObjectName);
            builder.Append("\",\"hierarchy\":\"");
            AppendJsonString(builder, region.Hierarchy);
            builder.Append("\",\"selectableName\":\"");
            AppendJsonString(builder, region.SelectableName);
            builder.Append("\",\"isSelectable\":");
            builder.Append(region.IsSelectable ? "true" : "false");
            builder.Append(",\"isButton\":");
            builder.Append(region.IsButton ? "true" : "false");
            builder.Append(",\"isChoiceLike\":");
            builder.Append(region.IsChoiceLike ? "true" : "false");
            builder.Append(",\"isSpeakerLike\":");
            builder.Append(region.IsSpeakerLike ? "true" : "false");
            builder.Append(",\"x\":");
            builder.Append(region.X);
            builder.Append(",\"y\":");
            builder.Append(region.Y);
            builder.Append(",\"width\":");
            builder.Append(region.Width);
            builder.Append(",\"height\":");
            builder.Append(region.Height);
            builder.Append(",\"layoutX\":");
            builder.Append(region.LayoutX);
            builder.Append(",\"layoutY\":");
            builder.Append(region.LayoutY);
            builder.Append(",\"layoutWidth\":");
            builder.Append(region.LayoutWidth);
            builder.Append(",\"layoutHeight\":");
            builder.Append(region.LayoutHeight);
            builder.Append(",\"foregroundArgb\":");
            builder.Append(region.ForegroundArgb);
            builder.Append(",\"sourceLineCount\":");
            builder.Append(region.SourceLineCount);
            builder.Append(",\"sourceAlignment\":\"");
            AppendJsonString(builder, region.SourceAlignment);
            builder.Append("\"}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static int PackArgb(
        Color color)
    {
        var alpha = Mathf.RoundToInt(
            Mathf.Clamp01(color.a) * 255f);
        var red = Mathf.RoundToInt(
            Mathf.Clamp01(color.r) * 255f);
        var green = Mathf.RoundToInt(
            Mathf.Clamp01(color.g) * 255f);
        var blue = Mathf.RoundToInt(
            Mathf.Clamp01(color.b) * 255f);

        return unchecked(
            (int)(
                ((uint)alpha << 24) |
                ((uint)red << 16) |
                ((uint)green << 8) |
                (uint)blue));
    }

    private static void AppendJsonString(
        StringBuilder builder,
        string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];

            switch (ch)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (ch < 32)
                    {
                        builder.Append("\\u");
                        builder.Append(((int)ch).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(ch);
                    }

                    break;
            }
        }
    }

    private sealed class RegionPayload
    {
        public string Text;
        public string Kind;
        public string ObjectName;
        public string Hierarchy;
        public string SelectableName;
        public bool IsSelectable;
        public bool IsButton;
        public bool IsChoiceLike;
        public bool IsSpeakerLike;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int LayoutX;
        public int LayoutY;
        public int LayoutWidth;
        public int LayoutHeight;
        public int ForegroundArgb;
        public int SourceLineCount;
        public string SourceAlignment;
    }

    private struct ScreenRect
    {
        public ScreenRect(
            int x,
            int y,
            int width,
            int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public int X;
        public int Y;
        public int Width;
        public int Height;
    }
}

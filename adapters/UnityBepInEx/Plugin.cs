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
    "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    private const int MaximumRegions = 128;
    private const int MaximumTextLength = 2048;
    private const float PollIntervalSeconds = 0.20f;

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
            "RealTimeTranslater Unity Adapter 0.1.0 loaded; read-only text capture enabled.");
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

        if (!TryGetScreenRect(graphic, out var rect))
            return;

        if (rect.Width < 2 || rect.Height < 2)
            return;

        regions.Add(new RegionPayload
        {
            Text = text,
            Kind = kind,
            ObjectName = graphic.gameObject.name ?? string.Empty,
            Hierarchy = BuildHierarchyPath(graphic.transform),
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = rect.Height
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

        var canvas = graphic.canvas;
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

        for (var i = 0; i < corners.Length; i++)
        {
            var point = RectTransformUtility.WorldToScreenPoint(
                camera,
                corners[i]);

            minX = Mathf.Min(minX, point.x);
            minY = Mathf.Min(minY, point.y);
            maxX = Mathf.Max(maxX, point.x);
            maxY = Mathf.Max(maxY, point.y);
        }

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
        var width = Mathf.Max(1, Mathf.RoundToInt(maxX - minX));
        var height = Mathf.Max(1, Mathf.RoundToInt(maxY - minY));

        result = new ScreenRect(x, y, width, height);
        return true;
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

        builder.Append("{\"protocol\":1,\"screenWidth\":");
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
            builder.Append("\",\"x\":");
            builder.Append(region.X);
            builder.Append(",\"y\":");
            builder.Append(region.Y);
            builder.Append(",\"width\":");
            builder.Append(region.Width);
            builder.Append(",\"height\":");
            builder.Append(region.Height);
            builder.Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
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
        public int X;
        public int Y;
        public int Width;
        public int Height;
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
